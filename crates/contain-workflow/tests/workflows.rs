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
            stdout_sha256: None,
            timeout_ms: 200,
        }],
    }
}
fn manifest() -> Manifest {
    Manifest {
        schema_version: 1,
        target: "windows-x64".into(),
        windows_toolchain: None,
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
            stdout_sha256: None,
            timeout_ms: 200,
        }],
        verify: vec![],
        artifact_dir: None,
        artifacts: vec![],
        installer: None,
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
        stdout_sha256: None,
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
        stdout_sha256: None,
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

#[test]
fn child_inherits_selected_environment_and_checks_actual_stdout_digest() {
    use std::collections::BTreeMap;
    let t = Temp::new();
    #[cfg(unix)]
    let (program, args) = ("sh", vec!["-c", "printf %s \"$CONTAIN_TEST_SELECTED\""]);
    #[cfg(windows)]
    let (program, args) = (
        "powershell.exe",
        vec![
            "-NoProfile",
            "-Command",
            "[Console]::Write($env:CONTAIN_TEST_SELECTED)",
        ],
    );
    let step = Step {
        name: "selected child".into(),
        program: program.into(),
        args: args.into_iter().map(str::to_string).collect(),
        expected_exit: 0,
        timeout_ms: 10000,
        stdout_sha256: Some(sha256(b"selected_fixture")),
    };
    let env = BTreeMap::from([("CONTAIN_TEST_SELECTED".into(), "selected_fixture".into())]);
    let r = runner::execute_in(&step, &t.0, &env);
    assert_eq!(r.status, Status::Passed);
    let wrong = BTreeMap::from([(
        "CONTAIN_TEST_SELECTED".into(),
        "SECRET_SENTINEL_wrong_selection".into(),
    )]);
    let r = runner::execute_in(&step, &t.0, &wrong);
    assert_eq!(r.status, Status::Failed);
    assert!(!r.detail.contains("SECRET_SENTINEL"));
}
#[test]
fn stdout_digest_is_validated_as_input() {
    let mut m = manifest();
    m.build[0].stdout_sha256 = Some("not a hash".into());
    assert!(m.validate().is_err());
}

#[test]
fn observed_version_extracted_for_mismatch_without_serializing_secrets() {
    let mut m = manifest();
    m.tools = vec![
        Tool {
            program: "dotnet".into(),
            version: "10.0.999".into(),
            version_args: vec!["--version".into()],
            remedy: "Install .NET SDK 10.0.999".into(),
        },
        Tool {
            program: "rustup".into(),
            version: "x86_64-pc-windows-msvc".into(),
            version_args: vec!["target".into(), "list".into(), "--installed".into()],
            remedy: "rustup target add x86_64-pc-windows-msvc".into(),
        },
    ];
    let secret = "SECRET_PATH_C:\\Users\\Private\\1.0";
    let r = environment::check_with_policy(
        &m,
        "windows",
        "x86_64",
        true,
        |program, _| match program {
            "dotnet" => Ok(format!("10.0.403 {secret} EXTRA_TOKEN")),
            "rustup" => Ok("aarch64-pc-windows-msvc".into()),
            _ => unreachable!(),
        },
        |_| true,
    );
    let dotnet = r.checks.iter().find(|c| c.name == "tool:dotnet").unwrap();
    assert_eq!(dotnet.status, Status::Failed);
    assert_eq!(dotnet.outcome.as_deref(), Some("version_mismatch"));
    assert_eq!(dotnet.required.as_deref(), Some("10.0.999"));
    assert_eq!(dotnet.observed.as_deref(), Some("10.0.403"));
    assert_eq!(dotnet.remedy.as_deref(), Some("Install .NET SDK 10.0.999"));
    let rustup = r.checks.iter().find(|c| c.name == "tool:rustup").unwrap();
    assert_eq!(rustup.status, Status::Failed);
    assert_eq!(rustup.observed, None);
    let serialized = serde_json::to_string(&r).unwrap();
    assert!(!serialized.contains("SECRET_PATH"));
    assert!(!serialized.contains("EXTRA_TOKEN"));
}

#[test]
fn safe_probes_skip_unsupported_commands_and_mark_needs_confirmation() {
    let mut m = manifest();
    m.tools.push(Tool {
        program: "cmd.exe".into(),
        version: "1.0".into(),
        version_args: vec!["/C".into(), "echo".into()],
        remedy: "confirm manually".into(),
    });
    let mut executed = Vec::new();
    let r = environment::check_with_policy(
        &m,
        "windows",
        "x86_64",
        true,
        |program, _| {
            executed.push(program.to_string());
            Ok("rustc 1.100.0".into())
        },
        |_| true,
    );
    assert_eq!(executed, vec!["rustc".to_string()]);
    let skipped = r.checks.iter().find(|c| c.name == "tool:cmd.exe").unwrap();
    assert_eq!(skipped.status, Status::NeedsConfirmation);
    assert_eq!(skipped.outcome.as_deref(), Some("unsupported_probe"));
    assert!(!r.passed());
}

#[test]
fn cli_infer_if_missing_checks_project_without_manifest_and_keeps_pending() {
    use std::process::Command;
    let t = Temp::new();
    fs::write(
        t.0.join("Cargo.toml"),
        "[package]\nname='sample'\nversion='0.1.0'\nrust-version='1.88'\n",
    )
    .unwrap();
    let report = t.0.join("report");
    let result = Command::new(env!("CARGO_BIN_EXE_contain-workflow"))
        .args([
            "check",
            "--project",
            t.0.to_str().unwrap(),
            "--safe-probes",
            "--infer-if-missing",
            "--report",
            report.to_str().unwrap(),
        ])
        .output()
        .unwrap();
    assert_eq!(result.status.code(), Some(2));
    let parsed: Report =
        serde_json::from_slice(&fs::read(report.join("report.json")).unwrap()).unwrap();
    assert_eq!(
        parsed.manifest_source.as_deref(),
        Some("inferred_from_project_files")
    );
    assert!(
        parsed
            .checks
            .iter()
            .any(|c| c.status == Status::NeedsConfirmation)
    );
}

#[test]
fn bundled_selector_lookup_prefers_workflow_subdirectory() {
    let t = Temp::new();
    assert!(contain_workflow::toolchain::bundled_selector_in(&t.0).is_none());
    let workflow_dir = t.0.join("workflow");
    fs::create_dir_all(&workflow_dir).unwrap();
    let script = workflow_dir.join("select-workflow-toolchain.ps1");
    fs::write(&script, "# reviewed selector").unwrap();
    assert_eq!(
        contain_workflow::toolchain::bundled_selector_in(&t.0),
        Some(script)
    );
}

#[test]
fn dotnet_global_json_rejection_falls_back_to_sdk_list_without_leaking_paths() {
    let mut m = manifest();
    m.tools = vec![Tool {
        program: "dotnet".into(),
        version: "10.0.404".into(),
        version_args: vec!["--version".into()],
        remedy: "Install .NET SDK 10.0.404".into(),
    }];
    let r = environment::check_with_policy(
        &m,
        "windows",
        "x86_64",
        true,
        |program, args| match (program, args.first().map(String::as_str)) {
            ("dotnet", Some("--version")) => anyhow::bail!("global.json SDK 10.0.404 not found"),
            ("dotnet", Some("--list-sdks")) => Ok(
                "9.0.308 [C:\\Users\\PrivateUser\\dotnet\\sdk]\n10.0.403 [D:\\SecretPath\\sdk]\n"
                    .into(),
            ),
            _ => unreachable!(),
        },
        |_| true,
    );
    let dotnet = r.checks.iter().find(|c| c.name == "tool:dotnet").unwrap();
    assert_eq!(dotnet.status, Status::Failed);
    assert_eq!(dotnet.outcome.as_deref(), Some("version_mismatch"));
    assert_eq!(dotnet.required.as_deref(), Some("10.0.404"));
    assert_eq!(dotnet.observed.as_deref(), Some("9.0.308, 10.0.403"));
    let text = serde_json::to_string(&r).unwrap();
    assert!(!text.contains("PrivateUser"));
    assert!(!text.contains("SecretPath"));
}

#[test]
fn cli_build_safe_probes_blocks_unsupported_probe_before_executing_build_steps() {
    use std::process::Command;
    let t = Temp::new();
    let marker = t.0.join("must-not-build.txt");
    #[cfg(windows)]
    let (probe_prog, probe_args, build_prog, build_args) = (
        "cmd.exe",
        vec!["/D", "/C", "echo 1.0"],
        "cmd.exe",
        vec!["/D", "/C", "echo built>must-not-build.txt"],
    );
    #[cfg(unix)]
    let (probe_prog, probe_args, build_prog, build_args) = (
        "sh",
        vec!["-c", "printf 1.0"],
        "sh",
        vec!["-c", "printf built > must-not-build.txt"],
    );
    let m = Manifest {
        schema_version: 1,
        target: "windows-x64".into(),
        windows_toolchain: None,
        tools: vec![Tool {
            program: probe_prog.into(),
            version: "1.0".into(),
            version_args: probe_args.into_iter().map(str::to_string).collect(),
            remedy: "manual review".into(),
        }],
        required_variables: vec![],
        build: vec![Step {
            name: "guarded build".into(),
            program: build_prog.into(),
            args: build_args.into_iter().map(str::to_string).collect(),
            expected_exit: 0,
            stdout_sha256: None,
            timeout_ms: 5000,
        }],
        verify: vec![],
        artifact_dir: None,
        artifacts: vec![],
        installer: None,
        pending: vec![],
    };
    let manifest_path = t.0.join("contain.environment.json");
    fs::write(&manifest_path, serde_json::to_vec_pretty(&m).unwrap()).unwrap();
    let report = t.0.join("report");
    let out = Command::new(env!("CARGO_BIN_EXE_contain-workflow"))
        .args([
            "build",
            "--project",
            t.0.to_str().unwrap(),
            "--safe-probes",
            "--execute",
            "--report",
            report.to_str().unwrap(),
        ])
        .output()
        .unwrap();
    assert_ne!(out.status.code(), Some(0));
    assert!(!marker.exists());
    let parsed: Report =
        serde_json::from_slice(&fs::read(report.join("report.json")).unwrap()).unwrap();
    assert_eq!(
        status(&parsed, &format!("tool:{probe_prog}")),
        Status::NeedsConfirmation
    );
    assert_eq!(status(&parsed, "guarded build"), Status::NotRun);
}

#[test]
fn cli_build_logs_progress_and_errors_locally_without_polluting_report_or_accepting_stale_artifacts()
 {
    use std::process::Command;
    let t = Temp::new();
    let ext_cwd = Temp::new();
    let out_dir = t.0.join("artifacts").join("app");
    fs::create_dir_all(&out_dir).unwrap();
    // Plant a stale artifact before running a failing build!
    fs::write(out_dir.join("stale.bin"), b"old-artifact-bytes").unwrap();

    #[cfg(windows)]
    let (fail_prog, fail_args, verify_prog, verify_args) = (
        "powershell.exe",
        vec![
            "-NoProfile",
            "-Command",
            "[Console]::Error.WriteLine('LOCAL_COMPILER_ERR_CS1002_SENTINEL'); exit 1",
        ],
        "cmd.exe",
        vec!["/D", "/C", "exit 0"],
    );
    #[cfg(unix)]
    let (fail_prog, fail_args, verify_prog, verify_args) = (
        "sh",
        vec!["-c", "echo LOCAL_COMPILER_ERR_CS1002_SENTINEL >&2; exit 1"],
        "sh",
        vec!["-c", "exit 0"],
    );

    let mut m = Manifest {
        schema_version: 1,
        target: "windows-x64".into(),
        windows_toolchain: None,
        tools: vec![],
        required_variables: vec![],
        build: vec![Step {
            name: "compile app".into(),
            program: fail_prog.into(),
            args: fail_args.into_iter().map(str::to_string).collect(),
            expected_exit: 0,
            stdout_sha256: None,
            timeout_ms: 10000,
        }],
        verify: vec![Step {
            name: "verify app".into(),
            program: verify_prog.into(),
            args: verify_args.into_iter().map(str::to_string).collect(),
            expected_exit: 0,
            stdout_sha256: None,
            timeout_ms: 10000,
        }],
        artifact_dir: Some("artifacts/app".into()),
        artifacts: vec!["stale.bin".into()],
        installer: None,
        pending: vec![],
    };
    let manifest_path = t.0.join("contain.environment.json");
    fs::write(&manifest_path, serde_json::to_vec_pretty(&m).unwrap()).unwrap();

    let report_fail = t.0.join("report-fail");
    let logs_fail = t.0.join("logs-fail");
    let res = Command::new(env!("CARGO_BIN_EXE_contain-workflow"))
        .current_dir(&ext_cwd.0)
        .args([
            "build",
            "--project",
            t.0.to_str().unwrap(),
            "--safe-probes",
            "--execute",
            "--log-dir",
            logs_fail.to_str().unwrap(),
            "--report",
            report_fail.to_str().unwrap(),
        ])
        .output()
        .unwrap();
    assert_eq!(res.status.code(), Some(1));

    let report_json = fs::read_to_string(report_fail.join("report.json")).unwrap();
    assert!(!report_json.contains("LOCAL_COMPILER_ERR_CS1002_SENTINEL"));
    let parsed_fail: Report = serde_json::from_str(&report_json).unwrap();
    assert_eq!(status(&parsed_fail, "compile app"), Status::Failed);
    assert_eq!(status(&parsed_fail, "verify app"), Status::NotRun);
    assert_eq!(status(&parsed_fail, "build_artifacts"), Status::NotRun);

    let stderr_log = fs::read_to_string(logs_fail.join("build-01.stderr.local.log")).unwrap();
    assert!(stderr_log.contains("LOCAL_COMPILER_ERR_CS1002_SENTINEL"));
    let progress_lines = fs::read_to_string(logs_fail.join("progress.jsonl")).unwrap();
    assert!(progress_lines.contains("\"stage\":\"build\""));
    assert!(logs_fail.join("steps.json").is_file());

    // Now test succeeding build that produces a relative executable/script verified from external CWD
    let fresh_proj = Temp::new();
    #[cfg(windows)]
    {
        m.build = vec![Step {
            name: "emit bat".into(),
            program: "powershell.exe".into(),
            args: vec![
                "-NoProfile".into(),
                "-Command".into(),
                "New-Item -ItemType Directory -Force 'artifacts/app' | Out-Null; [IO.File]::WriteAllText((Join-Path $PWD 'artifacts/app/hello.cmd'), \"@echo off`r`necho HELLO_OK`r`n\", (New-Object Text.UTF8Encoding $false))".into(),
            ],
            expected_exit: 0,
            stdout_sha256: None,
            timeout_ms: 10000,
        }];
        m.verify = vec![Step {
            name: "run relative bat".into(),
            program: "artifacts/app/hello.cmd".into(),
            args: vec![],
            expected_exit: 0,
            stdout_sha256: Some(sha256(b"HELLO_OK\r\n")),
            timeout_ms: 10000,
        }];
        m.artifacts = vec!["hello.cmd".into()];
    }
    #[cfg(unix)]
    {
        m.build = vec![Step {
            name: "emit script".into(),
            program: "sh".into(),
            args: vec![
                "-c".into(),
                "mkdir -p artifacts/app && printf '#!/bin/sh\nprintf HELLO_OK' > artifacts/app/hello.sh && chmod +x artifacts/app/hello.sh".into(),
            ],
            expected_exit: 0,
            stdout_sha256: None,
            timeout_ms: 10000,
        }];
        m.verify = vec![Step {
            name: "run relative script".into(),
            program: "artifacts/app/hello.sh".into(),
            args: vec![],
            expected_exit: 0,
            stdout_sha256: Some(sha256(b"HELLO_OK")),
            timeout_ms: 10000,
        }];
        m.artifacts = vec!["hello.sh".into()];
    }
    fs::write(
        fresh_proj.0.join("contain.environment.json"),
        serde_json::to_vec_pretty(&m).unwrap(),
    )
    .unwrap();
    let report_ok = fresh_proj.0.join("report-ok");
    let logs_ok = fresh_proj.0.join("logs-ok");
    let res_ok = Command::new(env!("CARGO_BIN_EXE_contain-workflow"))
        .current_dir(&ext_cwd.0)
        .args([
            "build",
            "--project",
            fresh_proj.0.to_str().unwrap(),
            "--safe-probes",
            "--execute",
            "--log-dir",
            logs_ok.to_str().unwrap(),
            "--report",
            report_ok.to_str().unwrap(),
        ])
        .output()
        .unwrap();
    assert_eq!(res_ok.status.code(), Some(0));
    let parsed_ok: Report =
        serde_json::from_slice(&fs::read(report_ok.join("report.json")).unwrap()).unwrap();
    assert!(parsed_ok.passed());
    assert_eq!(status(&parsed_ok, "build_artifacts"), Status::Passed);
    assert!(!report_fail.join("build-attestation.json").exists());
    assert!(report_ok.join("build-attestation.json").is_file());
}

#[test]
fn project_installer_identity_persists_across_move_and_isolates_same_name_projects() {
    use contain_workflow::project_installer::{
        CONTAIN_OWN_APP_ID, derive_and_validate_install_paths, derive_and_validate_setup_file_stem,
        inspect_existing_identity, normalize_valid_app_id, render_project_iss,
        resolve_or_persist_identity,
    };

    assert!(normalize_valid_app_id(CONTAIN_OWN_APP_ID).is_err());
    assert!(normalize_valid_app_id(&format!("{{{}}}", CONTAIN_OWN_APP_ID.to_uppercase())).is_err());
    assert!(normalize_valid_app_id("00000000-0000-0000-0000-000000000000").is_err());

    let root = Temp::new();
    let proj_a = root.0.join("WorkspaceA").join("HelloContain");
    let proj_b = root.0.join("WorkspaceB").join("HelloContain");
    fs::create_dir_all(&proj_a).unwrap();
    fs::create_dir_all(&proj_b).unwrap();

    let (id_a1, src_a1) = resolve_or_persist_identity(&proj_a, None).unwrap();
    assert_eq!(src_a1, "generated_and_persisted");
    assert_ne!(id_a1, CONTAIN_OWN_APP_ID);
    let (id_a2, src_a2) = resolve_or_persist_identity(&proj_a, None).unwrap();
    assert_eq!(id_a1, id_a2);
    assert_eq!(src_a2, "contain.installer.json");

    // Simulate moving Project A to a directory with spaces and Chinese characters
    let moved_a = root.0.join("移动 项目 目录").join("HelloContain");
    fs::create_dir_all(&moved_a).unwrap();
    fs::copy(
        proj_a.join("contain.installer.json"),
        moved_a.join("contain.installer.json"),
    )
    .unwrap();
    let (id_moved, _) = resolve_or_persist_identity(&moved_a, None).unwrap();
    assert_eq!(id_moved, id_a1);

    // Independent Project B with the exact same name gets an independent AppId, install dir, and shortcut
    let (id_b, _) = resolve_or_persist_identity(&proj_b, None).unwrap();
    assert_ne!(id_a1, id_b);

    let stem_a = derive_and_validate_setup_file_stem("HelloContain", "HelloContain").unwrap();
    let stem_b = derive_and_validate_setup_file_stem("HelloContain", "HelloContain").unwrap();
    assert_eq!(stem_a, "HelloContain");
    assert_eq!(stem_b, "HelloContain");

    let (dir_a, lnk_a) =
        derive_and_validate_install_paths("HelloContain", &stem_a, &id_a1[..8]).unwrap();
    let (dir_b, lnk_b) =
        derive_and_validate_install_paths("HelloContain", &stem_b, &id_b[..8]).unwrap();
    assert_ne!(dir_a, dir_b);
    assert_ne!(lnk_a, lnk_b);
    assert_ne!(dir_a.to_ascii_lowercase(), "contain");

    // Conflicting manifest app_id vs persisted contain.installer.json must fail cleanly
    let conflict = inspect_existing_identity(&proj_a, Some(&id_b));
    assert!(matches!(conflict, Err(("conflicting_app_id", _))));

    // Default publisher must NEVER inject LuckyLivio or Contain repository URLs
    let iss = render_project_iss(
        &id_a1,
        &id_a1[..8],
        "HelloContain",
        "0.1.0",
        "0.1.0.0",
        None,
        &dir_a,
        &lnk_a,
        "HelloContain.exe",
        Path::new("C:\\stage\\payload"),
        Path::new("C:\\stage\\out"),
        "HelloContain-Setup-x64",
    )
    .unwrap();
    assert!(iss.contains(&format!("AppId={{{{{id_a1}}}")));
    assert!(!iss.contains("LuckyLivio"));
    assert!(!iss.contains(CONTAIN_OWN_APP_ID));
    assert!(
        !iss.to_ascii_lowercase()
            .contains("8f4e2d91-5c3a-4b7e-9a16-2d8c4e7f1035")
    );
}

#[test]
fn project_installer_separates_display_name_filename_and_template_escaping() {
    use contain_workflow::project_installer::{
        convert_to_four_part_version, derive_and_validate_install_paths,
        derive_and_validate_setup_file_stem, render_project_iss, validate_display_name,
    };

    assert!(validate_display_name("Bad\nName").is_err());
    let display = validate_display_name("我的工具: {Beta} \"Pro\" & 助手").unwrap();
    let stem = derive_and_validate_setup_file_stem(&display, "MyHelper").unwrap();
    assert!(!stem.contains([':', '"', '<', '>', '|', '?', '*', '/', '\\']));
    let (dir_leaf, shortcut) =
        derive_and_validate_install_paths(&display, &stem, "a1b2c3d4").unwrap();
    assert!(!dir_leaf.contains([':', '"']));
    assert!(!shortcut.contains([':', '"']));
    assert_eq!(
        convert_to_four_part_version("1.2.3-beta.4").unwrap(),
        "1.2.3.4"
    );

    let iss = render_project_iss(
        "a1b2c3d4-1111-4222-8333-444455556666",
        "a1b2c3d4",
        &display,
        "1.2.3-beta.4",
        "1.2.3.4",
        Some("开发团队 {Team}"),
        &dir_leaf,
        &shortcut,
        "sub/MyHelper.exe",
        Path::new("C:\\测试 目录\\payload"),
        Path::new("C:\\测试 目录\\out"),
        &format!("{stem}-Setup-x64"),
    )
    .unwrap();
    assert!(iss.contains("{{Beta}"));
    assert!(iss.contains("AppPublisher=开发团队 {{Team}"));
    assert!(iss.contains("&&"));
}

#[cfg(windows)]
#[test]
fn project_installer_validates_publish_mode_entry_version_freshness_and_compiles_setup() {
    use contain_workflow::manifest::InstallerSpec;
    use contain_workflow::project_installer;
    use std::process::Command;

    let t = Temp::new();
    let proj = t.0.join("SampleApp");
    let pub_dir = proj.join("artifacts").join("publish");
    fs::create_dir_all(&pub_dir).unwrap();
    fs::write(
        proj.join("SampleApp.csproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
    )
    .unwrap();

    // Start with framework-dependent runtimeconfig (no includedFrameworks, no coreclr.dll) and no version
    fs::write(pub_dir.join("SampleApp.exe"), pe()).unwrap();
    fs::write(pub_dir.join("SampleApp.dll"), b"managed-il").unwrap();
    fs::write(
        pub_dir.join("SampleApp.deps.json"),
        r#"{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0"}}"#,
    )
    .unwrap();
    fs::write(
        pub_dir.join("SampleApp.runtimeconfig.json"),
        r#"{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}"#,
    )
    .unwrap();

    let mut m = Manifest {
        schema_version: 1,
        target: "windows-x64".into(),
        windows_toolchain: None,
        tools: vec![],
        required_variables: vec![],
        build: vec![Step {
            name: "noop".into(),
            program: "cmd.exe".into(),
            args: vec!["/D".into(), "/C".into(), "exit 0".into()],
            expected_exit: 0,
            stdout_sha256: None,
            timeout_ms: 5000,
        }],
        verify: vec![],
        artifact_dir: Some("artifacts/publish".into()),
        artifacts: vec!["SampleApp.exe".into()],
        installer: None,
        pending: vec![],
    };
    let manifest_path = proj.join("contain.environment.json");
    fs::write(&manifest_path, serde_json::to_vec_pretty(&m).unwrap()).unwrap();

    let build_rep_1 = t.0.join("build-rep-1");
    project_installer::write_build_attestation(&m, &manifest_path, &proj, None, &{
        fs::create_dir_all(&build_rep_1).unwrap();
        build_rep_1.clone()
    })
    .unwrap();
    let att_1 = build_rep_1.join("build-attestation.json");

    // 1) Framework-dependent + missing version both reported cleanly; output_dir not created
    let out_fd = t.0.join("out-fd");
    let rep_fd = t.0.join("rep-fd");
    let (r_fd, rec_fd) =
        project_installer::package_installer(&proj, None, &att_1, &out_fd, None, &rep_fd).unwrap();
    assert!(rec_fd.is_none());
    assert!(!out_fd.exists());
    let pub_chk = r_fd
        .checks
        .iter()
        .find(|c| c.name == "dotnet_self_contained_publish")
        .unwrap();
    assert_eq!(
        pub_chk.outcome.as_deref(),
        Some("framework_dependent_output")
    );
    assert!(
        pub_chk.remedy.as_deref().unwrap().contains(
            "dotnet publish SampleApp.csproj -c Release -r win-x64 --self-contained true"
        )
    );
    let ver_chk = r_fd
        .checks
        .iter()
        .find(|c| c.name == "installer_version")
        .unwrap();
    assert_eq!(ver_chk.outcome.as_deref(), Some("missing_version"));

    // 2) Upgrade output to full self-contained x64 multi-file publish (+ createdump.exe ignored, extra exe triggers ambiguous_entry)
    fs::write(pub_dir.join("createdump.exe"), pe()).unwrap();
    fs::write(pub_dir.join("SecondTool.exe"), pe()).unwrap();
    fs::write(pub_dir.join("coreclr.dll"), pe()).unwrap();
    fs::write(pub_dir.join("hostfxr.dll"), b"hostfxr").unwrap();
    fs::write(pub_dir.join("hostpolicy.dll"), b"hostpolicy").unwrap();
    fs::write(
        pub_dir.join("SampleApp.deps.json"),
        r#"{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"}}"#,
    )
    .unwrap();
    fs::write(
        pub_dir.join("SampleApp.runtimeconfig.json"),
        r#"{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}"#,
    )
    .unwrap();

    m.installer = Some(InstallerSpec {
        version: Some("0.2.0".into()),
        ..Default::default()
    });
    fs::write(&manifest_path, serde_json::to_vec_pretty(&m).unwrap()).unwrap();
    let build_rep_2 = t.0.join("build-rep-2");
    fs::create_dir_all(&build_rep_2).unwrap();
    project_installer::write_build_attestation(&m, &manifest_path, &proj, None, &build_rep_2)
        .unwrap();
    let (r_amb, _) = project_installer::package_installer(
        &proj,
        None,
        &build_rep_2.join("build-attestation.json"),
        &t.0.join("out-amb"),
        None,
        &t.0.join("rep-amb"),
    )
    .unwrap();
    assert_eq!(
        r_amb
            .checks
            .iter()
            .find(|c| c.name == "installer_entry")
            .unwrap()
            .outcome
            .as_deref(),
        Some("ambiguous_entry")
    );

    // 3) Explicitly set entry = SampleApp.exe (createdump.exe & SecondTool.exe disambiguated), test missing ISCC first
    fs::remove_file(pub_dir.join("SecondTool.exe")).unwrap();
    m.installer = Some(InstallerSpec {
        app_name: Some("SampleApp".into()),
        version: Some("0.2.0".into()),
        entry: Some("SampleApp.exe".into()),
        publish_dir: Some("artifacts/publish".into()),
        publish_kind: Some("dotnet-self-contained-win-x64".into()),
        ..Default::default()
    });
    fs::write(&manifest_path, serde_json::to_vec_pretty(&m).unwrap()).unwrap();
    let build_rep_3 = t.0.join("build-rep-3");
    fs::create_dir_all(&build_rep_3).unwrap();
    project_installer::write_build_attestation(&m, &manifest_path, &proj, None, &build_rep_3)
        .unwrap();
    let att_3 = build_rep_3.join("build-attestation.json");

    let absent_iscc = t.0.join("no-such-iscc.exe");
    let (r_noiscc, _) = project_installer::package_installer(
        &proj,
        None,
        &att_3,
        &t.0.join("out-noiscc"),
        Some(&absent_iscc),
        &t.0.join("rep-noiscc"),
    )
    .unwrap();
    let iscc_chk = r_noiscc
        .checks
        .iter()
        .find(|c| c.name == "inno_setup_compiler")
        .unwrap();
    assert_eq!(iscc_chk.outcome.as_deref(), Some("inno_setup_missing"));
    assert!(iscc_chk.remedy.as_deref().unwrap().contains("无需安装"));

    // 4) If real ISCC.exe is installed on this Windows dev machine, compile real SampleApp-Setup-x64.exe via CLI!
    if project_installer::resolve_iscc_executable(None).is_some() {
        let out_real = t.0.join("out-real");
        let rep_real = t.0.join("rep-real");
        let cli_res = Command::new(env!("CARGO_BIN_EXE_contain-workflow"))
            .args([
                "package-installer",
                "--project",
                proj.to_str().unwrap(),
                "--attestation",
                att_3.to_str().unwrap(),
                "--output-dir",
                out_real.to_str().unwrap(),
                "--report",
                rep_real.to_str().unwrap(),
            ])
            .output()
            .unwrap();
        assert_eq!(cli_res.status.code(), Some(0));
        assert!(out_real.join("SampleApp-Setup-x64.exe").is_file());
        assert!(out_real.join("SampleApp-Setup-x64.sha256").is_file());
        assert!(out_real.join("installer-receipt.json").is_file());
        assert!(proj.join("contain.installer.json").is_file());

        // Tamper with SampleApp.dll after build -> must reject stale/modified artifacts while preserving out_real!
        fs::write(pub_dir.join("SampleApp.dll"), b"tampered-bytes").unwrap();
        let out_tampered = t.0.join("out-tampered");
        let rep_tampered = t.0.join("rep-tampered");
        let (r_tamp, _) = project_installer::package_installer(
            &proj,
            None,
            &att_3,
            &out_tampered,
            None,
            &rep_tampered,
        )
        .unwrap();
        assert_eq!(
            r_tamp
                .checks
                .iter()
                .find(|c| c.name == "verified_output_binding")
                .unwrap()
                .outcome
                .as_deref(),
            Some("artifacts_modified_or_stale")
        );
        assert!(out_real.join("SampleApp-Setup-x64.exe").is_file());
        assert!(!out_tampered.exists());
    }
}
