use contain_workflow::{
    artifact::{Artifact, pe_x64, safe_name, sha256},
    environment,
    manifest::{self, Manifest, Tool},
    package::{self, Launch, PackageSpec},
    report::{Report, Status},
    runner::{self, Step},
};
use std::{
    fs,
    io::Write,
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};
static SERIAL: AtomicU64 = AtomicU64::new(0);
struct Temp(PathBuf);
impl Temp {
    fn new() -> Self {
        let p = std::env::temp_dir().join(format!(
            "contain-workflow-test-{}-{}",
            std::process::id(),
            SERIAL.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&p).unwrap();
        Self(p)
    }
}
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
fn pe() -> Vec<u8> {
    let mut b = vec![0; 256];
    b[..2].copy_from_slice(b"MZ");
    b[0x3c..0x40].copy_from_slice(&128u32.to_le_bytes());
    b[128..134].copy_from_slice(b"PE\0\0\x64\x86");
    b[152..154].copy_from_slice(b"\x0b\x02");
    b
}
fn zip(path: &Path, files: &[(&str, &[u8])]) {
    let mut w = zip::ZipWriter::new(fs::File::create(path).unwrap());
    for (name, data) in files {
        w.start_file(*name, zip::write::SimpleFileOptions::default())
            .unwrap();
        w.write_all(data).unwrap();
    }
    w.finish().unwrap();
}
fn spec(path: &Path) -> PackageSpec {
    PackageSpec {
        schema_version: 1,
        target: "windows-x64".into(),
        sha256: sha256(&fs::read(path).unwrap()),
        entry: "app.exe".into(),
        comparison_entry: None,
        required_files: vec!["critical.dll".into()],
        launch: Some(Launch {
            args: vec![],
            timeout_ms: 200,
            expected_exit: Some(0),
            window_title: None,
        }),
        tests: vec![Step {
            name: "self-test".into(),
            program: "app.exe".into(),
            args: vec!["--self-test".into()],
            expected_exit: 0,
            timeout_ms: 200,
        }],
    }
}
fn manifest() -> Manifest {
    Manifest {
        schema_version: 1,
        target: "windows-x64".into(),
        tools: vec![Tool {
            program: "rustc".into(),
            version: "1.100.0".into(),
            version_args: vec!["--version".into()],
            remedy: "install pinned Rust".into(),
        }],
        required_variables: vec!["REQUIRED_TOKEN".into()],
        build: vec![Step {
            name: "build".into(),
            program: "cargo".into(),
            args: vec!["build".into()],
            expected_exit: 0,
            timeout_ms: 200,
        }],
        verify: vec![],
        pending: vec![],
    }
}
fn status(r: &Report, name: &str) -> Status {
    r.checks
        .iter()
        .find(|c| c.name == name)
        .unwrap()
        .status
        .clone()
}
#[test]
fn environment_missing_version_mismatch_and_restored() {
    let m = manifest();
    let r = environment::check_with(
        &m,
        "windows",
        "x86_64",
        |_, _| anyhow::bail!("not found"),
        |_| false,
    );
    assert_eq!(status(&r, "tool:rustc"), Status::Failed);
    assert_eq!(status(&r, "variable:REQUIRED_TOKEN"), Status::Failed);
    let r = environment::check_with(
        &m,
        "windows",
        "x86_64",
        |_, _| Ok("rustc 1.99.0".into()),
        |_| true,
    );
    assert_eq!(status(&r, "tool:rustc"), Status::Failed);
    let r = environment::check_with(
        &m,
        "windows",
        "x86_64",
        |_, _| Ok("rustc 1.100.0 (commit)".into()),
        |_| true,
    );
    assert!(r.passed());
}
#[test]
fn report_never_serializes_probe_or_variable_values() {
    let secret = "SECRET_SENTINEL_not_for_report";
    let r = environment::check_with(
        &manifest(),
        "windows",
        "x86_64",
        |_, _| Ok(format!("rustc 1.100.0 {secret}")),
        |_| true,
    );
    let text = serde_json::to_string(&r).unwrap();
    assert!(!text.contains(secret));
    assert!(text.contains("REQUIRED_TOKEN"));
}
#[test]
fn exact_version_token_not_prefix() {
    let r = environment::check_with(
        &manifest(),
        "windows",
        "x86_64",
        |_, _| Ok("rustc 1.100.01".into()),
        |_| true,
    );
    assert_eq!(status(&r, "tool:rustc"), Status::Failed);
}
#[test]
fn wrong_platform_and_unresolved_requirements_block_readiness() {
    let mut m = manifest();
    m.pending.push("SDK unknown".into());
    let r = environment::check_with(&m, "linux", "x86_64", |_, _| Ok("1.100.0".into()), |_| true);
    assert!(!r.passed());
    assert_eq!(status(&r, "requirement"), Status::NeedsConfirmation);
}
#[test]
fn manifest_rejects_env_values_and_unknown_fields() {
    let mut m = manifest();
    m.required_variables = vec!["TOKEN=secret".into()];
    assert!(m.validate().is_err());
    let mut v = serde_json::to_value(manifest()).unwrap();
    v["secrets"] = serde_json::json!({"TOKEN":"secret"});
    assert!(serde_json::from_value::<Manifest>(v).is_err());
}
#[test]
fn inference_uses_declarations_and_keeps_unknowns() {
    let t = Temp::new();
    fs::write(
        t.0.join("Cargo.toml"),
        "[package]\nname='example'\nrust-version='1.88'\n",
    )
    .unwrap();
    fs::write(
        t.0.join("rust-toolchain.toml"),
        "[toolchain]\nchannel='stable'\ncomponents=['clippy']\n",
    )
    .unwrap();
    fs::write(
        t.0.join("global.json"),
        r#"{"sdk":{"version":"10.0.403","rollForward":"disable"}}"#,
    )
    .unwrap();
    let m = manifest::infer(&t.0).unwrap();
    assert_eq!(m.tools.len(), 1);
    assert_eq!(m.tools[0].version, "10.0.403");
    assert!(!m.pending.is_empty());
    assert!(m.pending.iter().any(|p| p.contains("minimum")));
    assert_eq!(m.build[0].program, "cargo");
    assert!(m.required_variables.is_empty());
}
#[test]
fn inference_exact_rust_and_unsupported_project() {
    let t = Temp::new();
    let empty = manifest::infer(&t.0).unwrap();
    assert!(empty.pending.iter().any(|p| p.contains("unsupported")));
    fs::write(
        t.0.join("rust-toolchain.toml"),
        "[toolchain]\nchannel='1.100.0'\n",
    )
    .unwrap();
    let m = manifest::infer(&t.0).unwrap();
    assert_eq!(m.tools[0].version, "1.100.0");
    assert!(!m.pending.is_empty());
}
#[test]
fn malformed_declarations_do_not_become_ready() {
    let t = Temp::new();
    fs::write(t.0.join("Cargo.toml"), "[broken").unwrap();
    assert!(manifest::infer(&t.0).is_err());
}
#[test]
fn unsafe_windows_paths_rejected() {
    for s in [
        "../x",
        "/absolute",
        "C:/x",
        "x\\y",
        "CON.txt",
        "LPT1",
        "dir/../x",
        "foo. ",
        "x:y",
        "a//b",
        "x\0z",
    ] {
        assert!(!safe_name(s), "{s}");
    }
    assert!(safe_name("中文 & [x]/app.exe"));
}
#[test]
fn pe_architecture_header_is_only_static_evidence() {
    assert!(pe_x64(&pe()));
    let mut b = pe();
    b[132] = 0x4c;
    assert!(!pe_x64(&b));
    assert!(!pe_x64(b"MZ"));
    assert!(!pe_x64(&vec![0; 256]));
    let mut b = pe();
    b[60..64].copy_from_slice(&u32::MAX.to_le_bytes());
    assert!(!pe_x64(&b));
}
#[test]
fn zip_identity_and_missing_key_file_report() {
    let t = Temp::new();
    let path = t.0.join("missing.zip");
    zip(&path, &[("app.exe", &pe())]);
    let r = package::verify(&path, &spec(&path), None, true, Some(&t.0.join("work")));
    assert_eq!(status(&r, "static_file:critical.dll"), Status::Failed);
    assert_eq!(status(&r, "launch"), Status::NotRun);
    assert_eq!(status(&r, "self-test"), Status::NotRun);
    assert!(!t.0.join("work").exists());
}
#[test]
fn hash_mismatch_blocks_execution() {
    let t = Temp::new();
    let path = t.0.join("app.zip");
    zip(&path, &[("app.exe", &pe()), ("critical.dll", b"synthetic")]);
    let mut s = spec(&path);
    s.sha256 = "0".repeat(64);
    let r = package::verify(&path, &s, None, true, Some(&t.0.join("work")));
    assert_eq!(status(&r, "package_identity"), Status::Failed);
    assert_eq!(status(&r, "launch"), Status::NotRun);
    assert!(!t.0.join("work").exists());
}
#[test]
fn pr22_same_exe_in_different_zips_rejected_before_any_execution() {
    let t = Temp::new();
    let a = t.0.join("baseline.zip");
    let b = t.0.join("candidate.zip");
    zip(
        &a,
        &[("app.exe", &pe()), ("critical.dll", b"baseline label")],
    );
    zip(
        &b,
        &[("app.exe", &pe()), ("critical.dll", b"candidate label")],
    );
    assert_ne!(fs::read(&a).unwrap(), fs::read(&b).unwrap());
    let r = package::verify(
        &b,
        &spec(&b),
        Some(&a),
        true,
        Some(&t.0.join("must-not-exist")),
    );
    assert_eq!(status(&r, "comparison_identity"), Status::Failed);
    assert_eq!(status(&r, "launch"), Status::NotRun);
    assert_eq!(status(&r, "self-test"), Status::NotRun);
    assert!(!t.0.join("must-not-exist").exists());
    assert!(
        r.checks
            .iter()
            .any(|c| c.detail.contains("identical bytes"))
    );
}
#[test]
fn different_exe_does_not_claim_behavior_validated() {
    let t = Temp::new();
    let a = t.0.join("a.zip");
    let b = t.0.join("b.zip");
    let mut different = pe();
    different.push(1);
    zip(&a, &[("app.exe", &pe())]);
    zip(&b, &[("app.exe", &different)]);
    let r = package::compare(&a, &b, "app.exe");
    assert!(r.passed());
    assert!(
        r.checks[1]
            .detail
            .contains("does not prove different behavior")
    );
}
#[test]
fn archive_traversal_aliases_and_symlinks_rejected() {
    let t = Temp::new();
    for (n, entries) in [
        vec![("../evil", b"x".as_slice())],
        vec![("APP.exe", b"x".as_slice()), ("app.exe", b"y".as_slice())],
        vec![("x", b"x".as_slice()), ("x/child", b"y".as_slice())],
    ]
    .iter()
    .enumerate()
    {
        let p = t.0.join(format!("bad{n}.zip"));
        zip(&p, entries);
        assert!(Artifact::read(&p).is_err());
    }
    let p = t.0.join("link.zip");
    let mut w = zip::ZipWriter::new(fs::File::create(&p).unwrap());
    w.add_symlink("link", "target", zip::write::SimpleFileOptions::default())
        .unwrap();
    w.finish().unwrap();
    assert!(Artifact::read(&p).is_err());
}
#[test]
fn corrupt_zip_yields_failed_report_and_unrun_steps() {
    let t = Temp::new();
    let p = t.0.join("bad.zip");
    fs::write(&p, b"not zip").unwrap();
    let r = package::verify(&p, &spec(&p), None, false, None);
    assert_eq!(status(&r, "static_archive"), Status::Failed);
    assert_eq!(status(&r, "launch"), Status::NotRun);
}
#[test]
fn contents_manifest_reuses_existing_contract() {
    let t = Temp::new();
    let p = t.0.join("app.zip");
    let exe = pe();
    let content = serde_json::json!({"schema_version":1,"source_sha":"a".repeat(40),"files":[{"path":"app.exe","bytes":exe.len(),"sha256":sha256(&exe)},{"path":"critical.dll","bytes":1,"sha256":sha256(b"x")}]});
    zip(
        &p,
        &[
            ("app.exe", &exe),
            ("critical.dll", b"x"),
            ("CONTENTS.json", &serde_json::to_vec(&content).unwrap()),
        ],
    );
    let r = package::verify(&p, &spec(&p), None, false, None);
    assert_eq!(status(&r, "static_contents"), Status::Passed);
    zip(
        &p,
        &[
            ("app.exe", &exe),
            ("critical.dll", b"changed"),
            ("CONTENTS.json", &serde_json::to_vec(&content).unwrap()),
        ],
    );
    let r = package::verify(&p, &spec(&p), None, true, None);
    assert_eq!(status(&r, "static_contents"), Status::Failed);
    assert_eq!(status(&r, "launch"), Status::NotRun);
}
#[test]
fn package_expectations_require_exactly_one_outcome() {
    let t = Temp::new();
    let p = t.0.join("a.zip");
    zip(&p, &[]);
    let mut s = spec(&p);
    assert!(s.validate().is_ok());
    s.launch.as_mut().unwrap().window_title = Some("test".into());
    assert!(s.validate().is_err());
    s.launch = None;
    s.tests[0].program = "../outside.exe".into();
    assert!(s.validate().is_err());
}
#[test]
fn report_statuses_are_separate_and_never_overwritten() {
    let t = Temp::new();
    let p = t.0.join("report");
    let mut r = Report::new("test");
    r.checks.push(contain_workflow::report::Check::new(
        "launch",
        Status::Passed,
        "started",
    ));
    r.checks.push(contain_workflow::report::Check::new(
        "test",
        Status::Failed,
        "assertion",
    ));
    r.checks.push(contain_workflow::report::Check::new(
        "windows",
        Status::NotRun,
        "not measured",
    ));
    r.write(&p).unwrap();
    assert!(!r.passed());
    assert!(r.write(&p).is_err());
    let text = fs::read_to_string(p.join("report.md")).unwrap();
    assert!(text.contains("通过") && text.contains("失败") && text.contains("未实测"));
}
#[test]
fn subprocess_exit_and_timeout_are_observed_not_assumed() {
    let t = Temp::new();
    #[cfg(unix)]
    let (program, good, bad, slow) = (
        "sh",
        vec!["-c", "exit 0"],
        vec!["-c", "exit 7"],
        vec!["-c", "exec sleep 3"],
    );
    #[cfg(windows)]
    let (program, good, bad, slow) = (
        "cmd.exe",
        vec!["/D", "/C", "exit 0"],
        vec!["/D", "/C", "exit 7"],
        vec!["/D", "/C", "ping -n 5 127.0.0.1 >NUL"],
    );
    let make = |args: Vec<&str>| Step {
        name: "fixture".into(),
        program: program.into(),
        args: args.into_iter().map(str::to_string).collect(),
        expected_exit: 0,
        timeout_ms: 100,
    };
    assert_eq!(runner::execute(&make(good), &t.0).status, Status::Passed);
    assert_eq!(runner::execute(&make(bad), &t.0).status, Status::Failed);
    assert_eq!(runner::execute(&make(slow), &t.0).status, Status::Failed);
}
#[cfg(not(windows))]
#[test]
fn linux_static_pass_is_not_native_launch_acceptance() {
    let t = Temp::new();
    let p = t.0.join("a.zip");
    zip(&p, &[("app.exe", &pe()), ("critical.dll", b"x")]);
    let r = package::verify(&p, &spec(&p), None, true, None);
    assert_eq!(status(&r, "static_entry"), Status::Passed);
    assert_eq!(status(&r, "launch"), Status::NotRun);
    assert!(!r.passed());
}
#[cfg(windows)]
#[test]
fn windows_real_test_exe_launch_and_self_test() {
    let t = Temp::new();
    let p = t.0.join("native.zip");
    let bytes = fs::read(std::env::current_exe().unwrap()).unwrap();
    zip(
        &p,
        &[("app.exe", &bytes), ("critical.dll", b"fixture only")],
    );
    let mut s = spec(&p);
    s.launch.as_mut().unwrap().args = vec!["--list".into()];
    s.launch.as_mut().unwrap().timeout_ms = 10000;
    s.tests[0].args = vec!["--list".into()];
    s.tests[0].timeout_ms = 10000;
    let r = package::verify(&p, &s, None, true, Some(&t.0.join("work")));
    assert_eq!(status(&r, "launch"), Status::Passed);
    assert_eq!(status(&r, "self-test"), Status::Passed);
}

#[test]
fn source_gate_runs_real_build_only_after_requirements_pass() {
    let t = Temp::new();
    let mut m = manifest();
    #[cfg(unix)]
    let (program, args) = ("sh", vec!["-c", "printf built > marker"]);
    #[cfg(windows)]
    let (program, args) = ("cmd.exe", vec!["/D", "/C", "echo built>marker"]);
    m.build = vec![Step {
        name: "actual fixture build".into(),
        program: program.into(),
        args: args.into_iter().map(str::to_string).collect(),
        expected_exit: 0,
        timeout_ms: 3000,
    }];
    let missing = environment::check_with(
        &m,
        "windows",
        "x86_64",
        |_, _| anyhow::bail!("missing"),
        |_| true,
    );
    let r = environment::execute_build(&m, &t.0, true, missing);
    assert_eq!(status(&r, "actual fixture build"), Status::NotRun);
    assert!(!t.0.join("marker").exists());
    let ready = environment::check_with(
        &m,
        "windows",
        "x86_64",
        |_, _| Ok("rustc 1.100.0".into()),
        |_| true,
    );
    let r = environment::execute_build(&m, &t.0, true, ready);
    assert_eq!(status(&r, "actual fixture build"), Status::Passed);
    assert!(
        fs::read_to_string(t.0.join("marker"))
            .unwrap()
            .contains("built")
    );
}
#[test]
fn source_build_failure_retains_unrun_following_builds_and_tests() {
    let t = Temp::new();
    let mut m = manifest();
    m.build[0].program = "contain-deliberately-absent-build-command".into();
    let mut later = m.build[0].clone();
    later.name = "later build".into();
    m.build.push(later.clone());
    later.name = "verification".into();
    m.verify.push(later);
    let ready = environment::check_with(
        &m,
        "windows",
        "x86_64",
        |_, _| Ok("1.100.0".into()),
        |_| true,
    );
    let r = environment::execute_build(&m, &t.0, true, ready);
    assert_eq!(status(&r, "build"), Status::Failed);
    assert_eq!(status(&r, "later build"), Status::NotRun);
    assert_eq!(status(&r, "verification"), Status::NotRun);
}
#[test]
fn cli_same_exe_rejection_writes_reviewable_reports() {
    use std::process::Command;
    let t = Temp::new();
    let a = t.0.join("baseline.zip");
    let b = t.0.join("candidate.zip");
    zip(&a, &[("app.exe", &pe()), ("critical.dll", b"baseline")]);
    zip(&b, &[("app.exe", &pe()), ("critical.dll", b"candidate")]);
    let s = t.0.join("spec.json");
    fs::write(&s, serde_json::to_vec(&spec(&b)).unwrap()).unwrap();
    let report = t.0.join("report");
    let work = t.0.join("must-not-run");
    let result = Command::new(env!("CARGO_BIN_EXE_contain-workflow"))
        .arg("verify")
        .arg("--zip")
        .arg(&b)
        .arg("--spec")
        .arg(&s)
        .arg("--compare-to")
        .arg(&a)
        .arg("--run")
        .arg("--work-dir")
        .arg(&work)
        .arg("--report")
        .arg(&report)
        .output()
        .unwrap();
    assert_eq!(result.status.code(), Some(1));
    assert!(!work.exists());
    let r: Report = serde_json::from_slice(&fs::read(report.join("report.json")).unwrap()).unwrap();
    assert_eq!(status(&r, "comparison_identity"), Status::Failed);
    assert_eq!(status(&r, "launch"), Status::NotRun);
    assert!(report.join("report.md").exists());
}
#[test]
fn cli_check_redacts_injected_environment_and_does_not_overwrite_reports() {
    use std::process::Command;
    let t = Temp::new();
    let mut m = manifest();
    m.tools[0].program = "contain-absent-tool-fixture".into();
    let s = t.0.join("environment.json");
    fs::write(&s, serde_json::to_vec(&m).unwrap()).unwrap();
    let report = t.0.join("report");
    let result = Command::new(env!("CARGO_BIN_EXE_contain-workflow"))
        .arg("check")
        .arg("--project")
        .arg(&t.0)
        .arg("--manifest")
        .arg(&s)
        .arg("--report")
        .arg(&report)
        .env("REQUIRED_TOKEN", "SECRET_SENTINEL")
        .output()
        .unwrap();
    assert_eq!(result.status.code(), Some(1));
    let bytes = fs::read(report.join("report.json")).unwrap();
    assert!(!String::from_utf8_lossy(&bytes).contains("SECRET_SENTINEL"));
    let again = Command::new(env!("CARGO_BIN_EXE_contain-workflow"))
        .arg("check")
        .arg("--project")
        .arg(&t.0)
        .arg("--manifest")
        .arg(&s)
        .arg("--report")
        .arg(&report)
        .output()
        .unwrap();
    assert_eq!(again.status.code(), Some(1));
    assert_eq!(fs::read(report.join("report.json")).unwrap(), bytes);
}

#[test]
fn comparison_can_bind_core_exe_instead_of_gui_entry() {
    let t = Temp::new();
    let a = t.0.join("baseline.zip");
    let b = t.0.join("candidate.zip");
    let mut changed_gui = pe();
    changed_gui.push(42);
    zip(
        &a,
        &[
            ("app.exe", &pe()),
            ("core.exe", b"same actual core"),
            ("critical.dll", b"x"),
        ],
    );
    zip(
        &b,
        &[
            ("app.exe", &changed_gui),
            ("core.exe", b"same actual core"),
            ("critical.dll", b"x"),
        ],
    );
    let mut s = spec(&b);
    s.comparison_entry = Some("core.exe".into());
    let r = package::verify(&b, &s, Some(&a), true, Some(&t.0.join("must-not-run")));
    assert_eq!(status(&r, "comparison_identity"), Status::Failed);
    assert_eq!(status(&r, "launch"), Status::NotRun);
    assert!(r.checks.iter().any(|c| c.detail.contains("entry core.exe")));
    assert!(!t.0.join("must-not-run").exists());
}

#[test]
fn checked_in_environment_and_package_specs_are_valid() {
    let m: Manifest =
        serde_json::from_str(include_str!("../../../contain.environment.json")).unwrap();
    m.validate().unwrap();
    assert!(m.pending.is_empty());
    assert!(
        m.tools
            .iter()
            .any(|t| t.version == "x86_64-pc-windows-msvc")
    );
    for bytes in [
        include_str!("../../../examples/workflow/contain.package.json"),
        include_str!("../../../examples/workflow/contain-beta1.package.json"),
    ] {
        let s: PackageSpec = serde_json::from_str(bytes).unwrap();
        s.validate().unwrap();
    }
}
