"""Freeze and publish the existing Windows CLI/fixture, never run an installer.

prepare copies one build before the two planned native runs. report executes only
read-only CLI commands on a private SQLite backup. finalize never rebuilds or
replaces an executable: a failed validation can still produce a clearly labelled
diagnostic package. Full captures, databases, clock sidecars and logs stay private.
"""
import argparse
from collections import Counter
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path, PureWindowsPath
import platform
import re
import shutil
import sqlite3
import stat
import struct
import subprocess
import zipfile


REPO = Path(__file__).resolve().parent.parent
PROTOCOL = "contain-read-only-trial-1"
TARGET = "x86_64-pc-windows-msvc"
BINARIES = ("contain.exe", "contain-test-installer.exe")
SCRIPTS = (
    "demo.ps1", "demo-provenance.ps1", "demo-checks.ps1", "demo-measure.py",
    "score-fixture.ps1", "score-fixture.py", "v04-verify-views.py",
    "v04-clock-protocol.py", "v04-matrix.py", "v04-metrics.py",
)
RESOURCES = ("LICENSE", "docs/schema/cli-v3.schema.json",
             "docs/examples/read-only-finished.txt", "docs/examples/read-only-historical.txt",
             "docs/examples/read-only-interrupted.txt")
SYSTEM_DLLS = frozenset("advapi32.dll bcrypt.dll bcryptprimitives.dll cabinet.dll cfgmgr32.dll "
    "comctl32.dll comdlg32.dll crypt32.dll dbghelp.dll dnsapi.dll dwmapi.dll gdi32.dll "
    "imm32.dll iphlpapi.dll kernel32.dll kernelbase.dll mpr.dll msimg32.dll msvcrt.dll "
    "ncrypt.dll netapi32.dll ntdll.dll ole32.dll oleaut32.dll powrprof.dll propsys.dll "
    "psapi.dll rpcrt4.dll secur32.dll setupapi.dll shell32.dll shlwapi.dll user32.dll "
    "userenv.dll ucrtbase.dll version.dll wevtapi.dll wer.dll winhttp.dll wininet.dll "
    "winmm.dll winspool.drv wintrust.dll wldap32.dll ws2_32.dll wtsapi32.dll".split())
RUNTIME_DLL = re.compile(r"(?:vcruntime140(?:_1)?|msvcp140(?:_[12]|_atomic_wait|_codecvt_ids)?|concrt140)\.dll")
SAFE_LIMITATIONS = frozenset({
    "Limitation: operation completion was not observed.",
    "Limitation: Scoped capture cannot prove a complete machine footprint; unsupported paths and missed provider events remain possible.",
})
# The names are the versioned demo-result-1 contract, not copies of its checks.
# Individual applicability still comes from the real demo; missing plan entries
# cannot silently turn a partial result into this fixed trial's acceptance.
DEMO_PRODUCT_PLAN = frozenset("capture_execution independent_fixture detached_fixture registry_noise_fixture "
    "inspect_json capture_finished source_loss_accounting independent_score view_consistency "
    "process_family_count installer_certain descendant_ancestry independent_process_excluded "
    "file_modified file_deleted registry_modified unrelated_state_unknown providers_active source_events "
    "file_high_source independent_source_unknown snapshot_state_unknown failed_delete_preserved rename_identity "
    "registry_noise_unknown truth_count independent_source_not_promoted scorer_attribution short_actor "
    "detached_actor detached_after_exit history_stability explain_json diff_json history_json doctor_json dry_run_unchanged".split())
DEMO_PLAN = DEMO_PRODUCT_PLAN | {"clock_diagnostic"}
FIXED_ETW_REQUIRED = DEMO_PRODUCT_PLAN - {"registry_noise_fixture", "snapshot_state_unknown"}
PUBLIC_CHECK_NAMES = DEMO_PLAN | {"demo_runtime", "package_identity"}


def require(condition, code):
    if not condition:
        raise ValueError(code)


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def write(path, value):
    with Path(path).open("x", encoding="utf-8", newline="\n") as output:
        json.dump(value, output, indent=2, allow_nan=False)
        output.write("\n")


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def redirected(path):
    return path.is_symlink() or bool(getattr(path.lstat(), "st_file_attributes", 0)
                                    & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def safe_file(path):
    path = Path(path)
    require(path.is_file() and not redirected(path), "input_not_regular_file")
    return path


def new_directory(path):
    path = Path(path).absolute()
    require(not path.exists() and path.parent.is_dir(), "output_must_be_new_with_existing_parent")
    require(not any(redirected(p) for p in (path.parent, *path.parent.parents)), "output_parent_redirected")
    path.mkdir()
    return path


def relative_path(name):
    require(isinstance(name, str) and name and "\\" not in name and ":" not in name,
            "invalid_package_relative_path")
    path = Path(name)
    # Windows /name is rooted on the current drive but is_absolute() is false.
    # A manifest entry must have neither a drive nor any root/anchor.
    require(not path.anchor and all(part not in {".", ".."} for part in path.parts)
            and path.as_posix() == name, "invalid_package_relative_path")
    return path


def identity(path, name=None):
    path = safe_file(path)
    return dict(name=name or path.name, bytes=path.stat().st_size, sha256=sha(path))


def pe_imports(path):
    """Inspect actual PE32+ imports and delay imports; fail closed on bad RVAs."""
    data = safe_file(path).read_bytes()
    def unpack(fmt, offset):
        require(0 <= offset <= len(data) - struct.calcsize(fmt), "invalid_pe_bounds")
        return struct.unpack_from(fmt, data, offset)
    require(data[:2] == b"MZ", "not_pe_image")
    pe, = unpack("<I", 0x3c)
    require(data[pe:pe + 4] == b"PE\0\0", "not_pe_image")
    machine, count = unpack("<HH", pe + 4)
    require(machine == 0x8664 and 0 < count <= 96, "not_windows_x64_image")
    optional_size, = unpack("<H", pe + 20)
    optional = pe + 24
    magic, = unpack("<H", optional)
    require(magic == 0x20b and optional_size >= 112, "not_pe32_plus_image")
    header_size, = unpack("<I", optional + 60)
    directory_count, = unpack("<I", optional + 108)
    require(directory_count <= 16 and optional_size >= 112 + directory_count * 8,
            "invalid_pe_directories")
    sections = []
    for index in range(count):
        off = optional + optional_size + index * 40
        virtual_size, rva, raw_size, raw_offset = unpack("<IIII", off + 8)
        sections.append((rva, virtual_size, raw_offset, raw_size))
    def offset(rva, size=1):
        candidates = []
        if rva < header_size and rva + size <= min(header_size, len(data)):
            candidates.append(rva)
        for base, virtual_size, raw, raw_size in sections:
            if base <= rva < base + max(virtual_size, raw_size):
                delta = rva - base
                require(delta + size <= raw_size and raw + delta + size <= len(data), "invalid_pe_rva")
                candidates.append(raw + delta)
        require(len(candidates) == 1, "invalid_or_ambiguous_pe_rva")
        return candidates[0]
    def dll_name(rva):
        start = offset(rva)
        end = data.find(b"\0", start, min(start + 260, len(data)))
        require(end > start, "invalid_pe_dll_name")
        offset(rva, end - start + 1)
        name = data[start:end].decode("ascii").lower()
        require(re.fullmatch(r"[a-z0-9_.-]+\.(?:dll|drv)", name), "invalid_pe_dll_name")
        return name
    imports, delayed = set(), set()
    for directory, row_size, target in ((1, 20, imports), (13, 32, delayed)):
        if directory >= directory_count:
            continue
        rva, size = unpack("<II", optional + 112 + directory * 8)
        if not rva and not size:
            continue
        require(rva > 0 and row_size <= size <= 1024 * 1024, "invalid_pe_import_directory")
        terminated = False
        for cursor in range(0, size - row_size + 1, row_size):
            row = unpack("<" + "I" * (row_size // 4), offset(rva + cursor, row_size))
            if not any(row):
                terminated = True
                break
            if directory == 13:
                require(row[0] == 1, "unsupported_delay_import_address_mode")
            target.add(dll_name(row[3] if directory == 1 else row[1]))
        require(terminated, "unterminated_pe_import_directory")
    return dict(machine="AMD64", optional_header="PE32+", imports=sorted(imports), delay_imports=sorted(delayed))


def system_dll(name):
    return name in SYSTEM_DLLS or bool(re.fullmatch(r"(?:api|ext)-ms-win-[a-z0-9-]+\.dll", name))


def source_identity(repo):
    def git(*arguments):
        return subprocess.check_output(["git", "-C", str(repo), *arguments], text=True, encoding="utf-8").strip()
    revision = git("rev-parse", "HEAD")
    require(re.fullmatch(r"[0-9a-f]{40}", revision), "source_revision_invalid")
    require(not git("status", "--porcelain=v1", "--untracked-files=all"), "source_checkout_not_clean")
    paths = sorted(set(git("ls-files", "--cached", "--others", "--exclude-standard", "--", "Cargo.toml",
        "Cargo.lock", "rust-toolchain.toml", "crates", "examples/test-installer", "vendor").splitlines()))
    require(bool(paths), "runtime_source_inputs_missing")
    rows = [name + ":" + sha(safe_file(repo / relative_path(name))) for name in paths]
    return revision, hashlib.sha256("\n".join(rows).encode()).hexdigest()


def platform_summary():
    def safe(value):
        return value if isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_. -]{1,120}", value) else None
    run = os.environ.get("GITHUB_RUN_ID", "")
    return dict(system=safe(platform.system()), release=safe(platform.release()), version=safe(platform.version()),
        architecture=safe(platform.machine()), runner_image=safe(os.environ.get("ImageOS")),
        runner_image_version=safe(os.environ.get("ImageVersion")),
        ci_run_url=f"https://github.com/LuckyLivio/Contain/actions/runs/{run}"
            if os.environ.get("GITHUB_REPOSITORY") == "LuckyLivio/Contain" and re.fullmatch(r"[0-9]+", run) else None)


def usage_text(manifest):
    return f"""# Contain 0.3.1 Windows x64 read-only trial

Source: {manifest['source_sha']}
Build: {manifest['target']}, {manifest['profile']} (diagnostic/functional build, not a release-performance claim).
Read VALIDATION.json for the actual bounded trial result. BUILD.json fixes the exact executables and scripts.

The ordinary CLI requires Windows x64 and the system DLL/API-set imports listed
in BUILD.json. Any extra runtime DLLs listed there are included beside the EXEs.
Rust, Cargo, Git and Visual Studio are not required to run this package.
Only the Windows CI platform/scenarios named in VALIDATION.json were tested.

From the extracted directory, ordinary PowerShell can run:

```powershell
.\\contain.exe --help
.\\contain.exe --db C:\\path\\to\\saved.db inspect TestFixture
.\\contain.exe --db C:\\path\\to\\saved.db history TestFixture --limit 20 --offset 0
.\\contain.exe --db C:\\path\\to\\saved.db explain <event-id>
```

The controlled demo additionally requires PowerShell 7 and Python 3 (3.12 was
tested). Start in the extracted directory and use a new output directory:

```powershell
pwsh -NoProfile -File .\\scripts\\demo.ps1 -RequireEtw -KeepArtifacts -OutputDirectory "$PWD\\demo-result-new"
```

The demo creates only marked temporary fixture files and its own HKCU fixture
key. It keeps the fixture/database and prints their locations; it does not run a
third-party installer, clean application data, uninstall or roll back anything.
The two fixture binaries are already built. Do not use RegistryNoise with this
package. ETW needs existing suitable trace/provider permissions. The demo never
opens UAC or changes permissions; RequireEtw reports failure instead of silently
accepting snapshot fallback. Preserve output on any failure.

Reuse the printed database path in inspect, diff, paged history and explain.
Each command reopens the database. JSON output is selected with --json; logs stay
on stderr. A finished recording may still be Degraded. Source actor evidence,
recorded/historical names, operation results, current paths and final ownership
are separate claims. Unknown, unavailable counters and unobserved scope remain
unknown. remove --dry-run only previews; no deletion executor exists.

examples/ contains reviewed outputs from the fixed controlled real-ETW runs when
available. docs/examples/read-only-* are separately marked synthetic scenarios;
they are not live installations. Full databases, traces and host details are not
included. Formal 32-run, scale, third-party-installer, final-ownership and safe
cleanup validation remain separate unfinished milestones. Historical six-event
timing failures remain D; non-reproduction does not clear them.
"""


def prepare(args):
    repo = args.repository.resolve()
    revision, tree = source_identity(repo)
    require(args.profile in {"debug", "release"}, "invalid_profile")
    output = new_directory(args.output)
    manifest = dict(schema_version=1, protocol_revision=PROTOCOL, version="0.3.1", source_sha=revision,
        runtime_tree_sha256=tree, target=TARGET, profile=args.profile,
        build_command=["cargo", "build", "--workspace", "--locked"] + (["--release"] if args.profile == "release" else []),
        binaries=[], scripts=[], resources=[], runtime_files=[], pe_dependencies={},
        runtime_requirement="Windows x64 system DLLs/API sets plus only the listed packaged runtime files",
        validation_at_freeze="not_run", loaded_image_attestation=False)
    manifest["toolchain"] = {program: subprocess.check_output([program, "--version"], text=True, encoding="utf-8").strip()
                             for program in ("rustc", "cargo")}
    manifest["build_input_sha256"] = {name: sha(repo / name) for name in
        ("Cargo.toml", "Cargo.lock", "rust-toolchain.toml", ".cargo/config.toml") if (repo / name).is_file()}
    manifest["rustflags_sha256"] = hashlib.sha256(os.environ.get("RUSTFLAGS", "").encode()).hexdigest()
    manifest["build_configuration_note"] = "Command/configuration of this CI build; no loaded-image attestation or release benchmark"
    manifest["build_platform"] = platform_summary()
    for name, path in zip(BINARIES, (args.contain, args.fixture)):
        safe_file(path)
        info = pe_imports(path)
        shutil.copyfile(path, output / name)
        manifest["binaries"].append(identity(output / name, name))
        manifest["pe_dependencies"][name] = info
    pending = sorted({name for info in manifest["pe_dependencies"].values()
                      for name in info["imports"] + info["delay_imports"] if not system_dll(name)})
    visited = set()
    while pending:
        name = pending.pop(0)
        if name in visited:
            continue
        require(RUNTIME_DLL.fullmatch(name) is not None and args.runtime_directory is not None,
                "unprovided_non_system_runtime_dependency")
        matches = [p for p in args.runtime_directory.iterdir() if p.name.lower() == name]
        require(len(matches) == 1, "runtime_dependency_missing_or_ambiguous")
        source = safe_file(matches[0]); info = pe_imports(source)
        shutil.copyfile(source, output / name)
        manifest["runtime_files"].append(identity(output / name, name))
        manifest["pe_dependencies"][name] = info
        visited.add(name)
        pending.extend(dll for dll in info["imports"] + info["delay_imports"] if not system_dll(dll))
    for name in SCRIPTS:
        relative = "scripts/" + name
        destination = output / relative; destination.parent.mkdir(exist_ok=True)
        shutil.copyfile(safe_file(repo / relative), destination)
        row = identity(destination); row["path"] = relative; row.pop("name")
        manifest["scripts"].append(row)
    for relative in RESOURCES:
        destination = output / relative; destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(safe_file(repo / relative), destination)
        row = identity(destination); row["path"] = relative; row.pop("name")
        manifest["resources"].append(row)
    (output / "TRY.md").write_text(usage_text(manifest), encoding="utf-8", newline="\n")
    row = identity(output / "TRY.md"); row["path"] = "TRY.md"; row.pop("name")
    manifest["resources"].append(row)
    require(source_identity(repo) == (revision, tree), "source_changed_during_freeze")
    write(output / "BUILD.json", manifest)
    print(json.dumps(dict(protocol=PROTOCOL, phase="frozen", source_sha=revision, candidate_verified=False)))
    return 0


def verify_package(package):
    manifest = read(safe_file(package / "BUILD.json"))
    require(manifest.get("schema_version") == 1 and manifest.get("protocol_revision") == PROTOCOL
            and manifest.get("target") == TARGET and manifest.get("version") == "0.3.1"
            and manifest.get("profile") in {"debug", "release"}
            and re.fullmatch(r"[0-9a-f]{40}", manifest.get("source_sha", "")), "invalid_build_manifest")
    require({row.get("name") for row in manifest["binaries"]} == set(BINARIES), "required_binaries_missing")
    files = ["BUILD.json"]
    for group, key in (("binaries", "name"), ("runtime_files", "name"), ("scripts", "path"), ("resources", "path")):
        for row in manifest[group]:
            name = row[key]; path = package / relative_path(name)
            require(name not in files, "duplicate_package_path")
            actual = identity(path)
            require(row.get("bytes") == actual["bytes"] and row.get("sha256") == actual["sha256"], "package_file_changed")
            files.append(name)
    return manifest, files


def run_identity_matches(directory, manifest, manifest_sha256):
    try:
        observed = read(directory / "run-identity.json")
        require(observed.get("build_mode") == "prebuilt" and observed.get("prebuilt_manifest_verified") is True
                and observed.get("prebuilt_manifest_unchanged") is True
                and observed.get("prebuilt_manifest_sha256") == manifest_sha256
                and observed.get("prebuilt_manifest_after_sha256") == manifest_sha256, "run_manifest_not_package")
        before = {PureWindowsPath(row["path"]).name: row for row in observed["binary_before"]}
        after = observed["binary_after"]
        require(after["unchanged"] is True, "run_binary_changed")
        rows = {PureWindowsPath(row["before"]["path"]).name: row for row in after["binaries"]}
        require(len(before) == len(observed["binary_before"]) == len(rows) == len(after["binaries"]) == 2,
                "run_binary_identities_not_unique")
        for expected in manifest["binaries"]:
            name = expected["name"]
            require(all(before[name][key] == expected[key] for key in ("sha256", "bytes")), "run_binary_not_package")
            require(rows[name]["unchanged"] is True and all(rows[name][phase][key] == expected[key]
                for phase in ("before", "after") for key in ("sha256", "bytes")), "run_binary_changed")
        for phase in ("source_before_build", "source_after_build"):
            require(observed[phase]["head"] == manifest["source_sha"]
                    and observed[phase]["runtime_tree_sha256"] == manifest["runtime_tree_sha256"], "run_source_not_package")
        return True, observed
    except (OSError, ValueError, KeyError, TypeError):
        return False, {}


def snapshot(database, destination):
    require(not destination.exists(), "report_snapshot_already_exists")
    with closing(sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)) as source:
        with closing(sqlite3.connect(destination)) as target:
            source.backup(target)


def json_document(text, kind):
    value = json.loads(text)
    require(value.get("schema_version") == 3 and value.get("kind") == kind and "data" in value,
            "report_json_contract_failed")
    return value["data"]


def public_text(text, replacements):
    """Replace only known fixture values, then reject residual machine paths."""
    for original, alias in sorted(replacements.items(), key=lambda row: len(row[0]), reverse=True):
        if original:
            text = re.sub(re.escape(original), lambda match: alias, text, flags=re.IGNORECASE)
            text = re.sub(re.escape(original.replace("\\", "\\\\")), lambda match: alias, text, flags=re.IGNORECASE)
    # Rust's text explanation quotes paths as JSON strings. After replacing a
    # known fixture root, normalize only that alias's escaped path separators;
    # an unrelated UNC path elsewhere must still be rejected below.
    text = re.sub(r"<fixture-(?:root|bin|registry-key)>(?:\\{1,2}[A-Za-z0-9_.-]+)*",
                  lambda match: match[0].replace("\\\\", "\\"), text)
    require(not re.search(r"[A-Za-z]:[\\/]|\\\\|S-1-5-\d|\\Device\\|\\Registry\\", text, re.IGNORECASE), "public_text_contains_private_path")
    return text


def windows_spelling(value):
    value = value.replace("/", "\\")
    for prefix in ("\\\\?\\", "\\??\\"):
        if value.startswith(prefix):
            value = value[len(prefix):]
    return value.casefold().rstrip("\\")


def report(args):
    package = args.package.resolve()
    manifest, _ = verify_package(package)
    local = new_directory(args.private_output)
    public = new_directory(args.public_output)
    summary = dict(schema_version=1, protocol_revision=PROTOCOL, source_sha=manifest["source_sha"],
        scenario=args.scenario, scope="real_etw_controlled_fixture_cli_excerpt", complete=False,
        candidate_verified=False, commands=[], checks={}, failure=None)
    try:
        same, observed = run_identity_matches(args.capture_directory, manifest, sha(package / "BUILD.json"))
        require(same, "report_run_identity_not_package")
        capture = read(args.capture_directory / "capture.json")["data"]
        truth = read(args.capture_directory / "ground-truth.json")
        require(capture.get("name") == "TestFixture" and len(truth) == 21, "not_fixed_controlled_fixture")
        require(all(capture["backend"].get(key) == "active" for key in ("etw_process", "etw_file", "etw_registry")), "report_not_real_etw")
        root = observed["fixture_root"]
        require(len(capture["watch_roots"]) == 1 and windows_spelling(capture["watch_roots"][0]) == windows_spelling(root)
                and re.fullmatch(r"contain-demo-[0-9a-f]{32}", PureWindowsPath(root).name), "not_fixed_fixture_scope")
        key = capture.get("registry_key")
        require(key == "Software\\Contain\\Demo\\" + PureWindowsPath(root).name, "not_fixed_fixture_registry")
        database = Path(observed["database"])
        copy = local / "read-only.db"; snapshot(database, copy)
        executable = package / "contain.exe"
        def command(label, kind, *arguments, json_output=False):
            argv = [str(executable), "--db", str(copy), kind, *arguments] + (["--json"] if json_output else [])
            completed = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", timeout=120)
            (local / (label + ".stdout.txt")).write_text(completed.stdout, encoding="utf-8")
            (local / (label + ".stderr.txt")).write_text(completed.stderr, encoding="utf-8")
            summary["commands"].append(dict(name=label, exit_code=completed.returncode,
                stdout_sha256=hashlib.sha256(completed.stdout.encode()).hexdigest()))
            require(completed.returncode == 0, "report_cli_failed")
            return json_document(completed.stdout, kind) if json_output else completed.stdout
        reopened = command("inspect-json", "inspect", "TestFixture", json_output=True)
        require(reopened == capture, "report_reopened_capture_changed")
        summary["checks"]["reopen_json_equal"] = True
        fixture_actors = {(row["pid"], str(row["creation_time"])) for row in truth}
        selected = next((row for row in reopened["events"] if row["event_type"] == "file"
            and row["confidence"] in {"High", "Certain"} and row["operation"] == "write_requested"
            and row["evidence"].get("source") == "EtwFile"
            and windows_spelling(row["resource"]).startswith(windows_spelling(root) + "\\")
            and (row["evidence"].get("pid"), str(row["evidence"].get("process_creation_time"))) in fixture_actors), None)
        require(selected is not None, "report_no_known_fixture_source")
        event_id = selected["id"]; index = reopened["events"].index(selected)
        history = command("history-json", "history", "TestFixture", "--limit", "1", "--offset", str(index), json_output=True)
        require(history["events"] == [selected], "report_history_page_mismatch")
        explanation = command("explain-json", "explain", event_id, json_output=True)
        require(explanation["event"] == selected and explanation["app_id"] == reopened["id"], "report_explain_mismatch")
        summary["checks"].update(history_page_equal=True, explain_event_equal=True, json_stdout_only=True)
        inspect_text = command("inspect-text", "inspect", "TestFixture")
        diff_text = command("diff-text", "diff", "TestFixture")
        history_text = command("history-text", "history", "TestFixture", "--limit", "1", "--offset", str(index))
        explain_text = command("explain-text", "explain", event_id)
        require(event_id in history_text and event_id in explain_text, "report_text_missing_event_id")
        replacements = {root: "<fixture-root>", capture["watch_roots"][0]: "<fixture-root>", key: "<fixture-registry-key>",
                        capture["installer"]: "<fixture-bin>\\contain-test-installer.exe"}
        for process in capture["processes"]:
            require((process["pid"], str(process["creation_time"])) in fixture_actors,
                    "report_process_outside_fixture")
            if process.get("image"):
                replacements[process["image"]] = "<fixture-bin>\\contain-test-installer.exe"
        # Summary's free-form warning text may contain host errors. Preserve the
        # numeric scope/quality/unknown lines and explicitly count omitted warnings.
        lines = inspect_text.splitlines()
        warning_lines = [line for line in lines if line.startswith("Limitation:") and line not in SAFE_LIMITATIONS]
        inspect_safe = "\n".join(line for line in lines if line not in warning_lines) + "\n"
        diff_lines = [line for line in diff_text.splitlines() if line.startswith(("STATE DIFFERENCES", "State differences"))
                      or (line.startswith(("  FILE ", "  REGISTRY ")) and (root.lower() in line.lower() or key.lower() in line.lower()))]
        # Explain is restricted to a verified fixture source and its ancestry.
        # Omit free-form general warnings rather than guess their sensitivity.
        explanation_warnings = [line for line in explain_text.splitlines()
                                if line.startswith("Limitation:") and line not in SAFE_LIMITATIONS]
        explanation_lines = [line for line in explain_text.splitlines() if line not in explanation_warnings]
        sections = dict(INSPECT=public_text(inspect_safe, replacements),
            DIFF=public_text("\n".join(diff_lines) + "\n", replacements),
            HISTORY=public_text(history_text, replacements),
            EXPLAIN=public_text("\n".join(explanation_lines) + "\n", replacements))
        text = ("REAL ETW CONTROLLED FIXTURE — actual packaged contain.exe output; not a third-party installer.\n"
                "Fixture paths are replaced consistently; no database or system trace is published.\n"
                f"Source: {manifest['source_sha']} | configuration: {args.scenario} | event: {event_id}\n"
                f"Public excerpt omits {len(warning_lines)} inspect and {len(explanation_warnings)} explain free-form limitation lines; recording quality and measured gaps below are unchanged.\n"
                "Diff excerpt shows fixture file/registry changes only; inventory details are omitted.\n"
                "See local inspect --verbose for all warnings. Source confidence does not establish explicit API intent or ownership.\n\n")
        for name, content in sections.items():
            display = dict(INSPECT="inspect TestFixture", DIFF="diff TestFixture",
                HISTORY=f"history TestFixture --limit 1 --offset {index}", EXPLAIN=f"explain {event_id}")
            text += name + "\n> .\\contain.exe --db <saved-db> " + display[name] + "\n" + content + "\n"
        verify_package(package)
        (public / "cli-report.txt").write_text(text, encoding="utf-8", newline="\n")
        summary.update(complete=True, event_id=event_id, capture_state=reopened["capture_state"],
            quality=reopened["quality"]["level"], omitted_limitation_lines=len(warning_lines),
            omitted_explain_limitation_lines=len(explanation_warnings),
            report_sha256=sha(public / "cli-report.txt"),
            interpretation="Actual read-only commands on a backup; excerpt/JSON agreement only, not product or formal acceptance")
    except (OSError, ValueError, KeyError, TypeError, StopIteration, sqlite3.Error, subprocess.SubprocessError) as error:
        summary["complete"] = False
        summary["failure"] = str(error) if isinstance(error, ValueError) and re.fullmatch(r"[a-z_]+", str(error)) else "report_failed_private_details_retained"
        (local / "failure.txt").write_text(repr(error), encoding="utf-8")
    write(public / "summary.json", summary)
    print(json.dumps(dict(protocol=PROTOCOL, phase="report", complete=summary["complete"], failure=summary["failure"])))
    return 0 if summary["complete"] else 2


def product_result(directory):
    try:
        value = read(directory / "product-checks.json")
        require(value.get("schema_version") == 1 and value.get("protocol_revision") == "contain-demo-result-1",
                "product_result_contract_invalid")
        all_rows = value["checks"]; planned = value["planned_check_names"]
        require(isinstance(all_rows, list) and bool(all_rows) and all(isinstance(row, dict)
            and isinstance(row.get("name"), str) and re.fullmatch(r"[a-z0-9_]+", row["name"])
            and row.get("domain") in {"product", "diagnostic"} and type(row.get("required")) is bool
            and row.get("status") in {"passed", "failed", "not_run"} for row in all_rows), "product_check_rows_invalid")
        names = [row["name"] for row in all_rows]
        require(len(names) == len(set(names)) and isinstance(planned, list) and bool(planned)
            and all(isinstance(name, str) for name in planned) and len(planned) == len(set(planned)), "product_check_plan_invalid")
        require(DEMO_PLAN <= set(planned), "fixed_trial_check_plan_incomplete")
        missing = sorted(set(planned) - set(names))
        require(value.get("missing_planned_checks") == missing, "product_check_plan_disagrees")
        by_name = {row["name"]: row for row in all_rows}
        require(all(name in by_name and by_name[name]["domain"] == "product" and by_name[name]["required"] is True
                    for name in FIXED_ETW_REQUIRED)
                and by_name.get("clock_diagnostic", {}).get("domain") == "diagnostic",
                "fixed_trial_required_checks_changed")
        rows = [row for row in value["checks"] if row.get("domain", "product") == "product" and row.get("required") is True]
        counts = Counter(row.get("status") for row in rows)
        require(all(type(value["product"].get("required_" + key)) is int
            and value["product"]["required_" + key] == counts[key] for key in ("passed", "failed", "not_run")),
            "product_required_counts_disagree")
        passed = bool(rows) and not missing and counts["passed"] == len(rows) and value["product"].get("passed") is True
        diagnostics = [row for row in all_rows if row["domain"] == "diagnostic" and row["required"]]
        diagnostic_counts = Counter(row["status"] for row in diagnostics)
        execution = value.get("diagnostic_execution", {}).get("status")
        evidence = value.get("evidence", {}).get("status")
        diagnostic_passed = bool(diagnostics) and diagnostic_counts["passed"] == len(diagnostics)
        clock_requested = value.get("clock_requested")
        bounded = passed and type(clock_requested) is bool and (not clock_requested or
            execution == "completed" and evidence == "complete" and diagnostic_passed) and value.get("bounded_pass") is True
        return dict(status="passed" if passed else "failed" if counts["failed"] else "incomplete",
                    passed=passed, required_passed=counts["passed"], required_failed=counts["failed"], required_not_run=counts["not_run"],
                    diagnostic_execution_status=execution if execution in {"completed", "failed", "not_requested"} else "unavailable",
                    supplied_view_clock_evidence=evidence if evidence in {"complete", "incomplete", "not_evaluated"} else "unavailable",
                    required_diagnostic_passed=diagnostic_counts["passed"], required_diagnostic_failed=diagnostic_counts["failed"],
                    required_diagnostic_not_run=diagnostic_counts["not_run"], bounded_pass=bounded,
                    clock_requested=clock_requested if type(clock_requested) is bool else None,
                    checks=[dict(name=row["name"] if row["name"] in PUBLIC_CHECK_NAMES else "unrecognized_check",
                        domain=row["domain"], required=row["required"], status=row["status"]) for row in all_rows],
                    missing_planned_checks=[name if name in PUBLIC_CHECK_NAMES else "unrecognized_check" for name in missing],
                    source_summary_sha256=sha(directory / "product-checks.json"))
    except (OSError, ValueError, KeyError, TypeError) as error:
        code = str(error) if isinstance(error, ValueError) and re.fullmatch(r"[a-z_]+", str(error)) else "product_result_unavailable_or_invalid"
        return dict(status="unavailable", passed=False, required_passed=None, required_failed=None, required_not_run=None,
                    checks=[], failure=code)


def finalize(args):
    package = args.package.resolve()
    manifest, files = verify_package(package)
    require(not args.output.exists() and args.output.parent.is_dir(), "archive_output_must_be_new")
    default_same, default_identity = run_identity_matches(args.default_directory, manifest, sha(package / "BUILD.json"))
    clock_same, clock_identity = run_identity_matches(args.clock_directory, manifest, sha(package / "BUILD.json"))
    default = product_result(args.default_directory); clock = product_result(args.clock_directory)
    def optional(path):
        try: return read(path)
        except (OSError, ValueError): return {}
    analysis = optional(args.clock_analysis)
    quality = optional(args.quality_summary)
    default_mode = default_identity.get("fixture_clock") in (None, "") and default_identity.get("raw_clock_path") in (None, "") and default.get("clock_requested") is False
    dual_mode = clock_identity.get("fixture_clock") == "1" and bool(clock_identity.get("raw_clock_path")) and clock.get("clock_requested") is True
    # This is a bounded candidate gate, separate from the retained formal status.
    checks = dict(default_product=default["passed"], dual_clock_product=clock["passed"],
        default_package_identity=default_same, dual_clock_package_identity=clock_same,
        default_clocks_disabled=default_mode, dual_clocks_enabled=dual_mode,
        dual_clock_diagnostic_executed=clock.get("diagnostic_execution_status") == "completed",
        default_demo_bounded_pass=default.get("bounded_pass") is True,
        dual_clock_demo_bounded_pass=clock.get("bounded_pass") is True,
        distinct_captures=bool(default_identity.get("database") and clock_identity.get("database")
                              and default_identity["database"] != clock_identity["database"]),
        clock_evidence_complete=analysis.get("diagnostic_evidence_complete") is True
            and analysis.get("source_sha") == manifest["source_sha"],
        quality_complete=quality.get("current_required_gates_passed") is True
            and quality.get("source_revision") == manifest["source_sha"])
    reports = {}
    for scenario in ("default", "dual-clock"):
        root = package / "examples" / scenario
        item = optional(root / "summary.json")
        text_path = root / "cli-report.txt"
        checks[scenario + "_actual_cli_report"] = bool(item.get("schema_version") == 1
            and item.get("protocol_revision") == PROTOCOL and item.get("scenario") == scenario
            and item.get("scope") == "real_etw_controlled_fixture_cli_excerpt"
            and item.get("complete") is True and item.get("source_sha") == manifest["source_sha"]
            and text_path.is_file() and item.get("report_sha256") == sha(safe_file(text_path)))
        if (root / "summary.json").is_file():
            files.append((root / "summary.json").relative_to(package).as_posix())
            reports[scenario] = item
        if (root / "cli-report.txt").is_file():
            require(item.get("report_sha256") == sha(root / "cli-report.txt"), "public_report_changed")
            files.append((root / "cli-report.txt").relative_to(package).as_posix())
    complete = all(value is True for value in checks.values())
    result = dict(schema_version=1, protocol_revision=PROTOCOL, version="0.3.1", source_sha=manifest["source_sha"],
        build_manifest_sha256=sha(package / "BUILD.json"), build_profile=manifest["profile"], target=TARGET,
        candidate_status="bounded_read_only_trial_verified" if complete else "diagnostic_build_unverified",
        candidate_verified=complete, checks=checks, default_product=default, dual_clock_product=clock,
        clock_evidence=dict(complete=checks["clock_evidence_complete"],
            missing_evidence=[item if isinstance(item, str) and re.fullmatch(r"[a-z0-9_]+", item) else "private_detail_omitted"
                for item in analysis["missing_evidence"]] if isinstance(analysis.get("missing_evidence"), list) else None),
        formal=dict(status="requirements_unmet", accepted=False),
        validation_platform=platform_summary(),
        actual_cli_reports={key: dict(complete=row.get("complete"), event_id=row.get("event_id"), report_sha256=row.get("report_sha256")) for key, row in reports.items()},
        scope="Two different configurations of one controlled real-ETW fixture using identical packaged binaries; not repeated stability, a third-party installer, scale, ownership or cleanup acceptance",
        historical_six_event_timing="D_unresolved", loaded_image_attestation=False,
        commands=dict(default="demo.ps1 -RequireEtw -KeepArtifacts with package executables and BUILD.json; clocks disabled",
                      dual_clock="same demo/build/fixture with CONTAIN_FIXTURE_CLOCK=1 and a new absolute CONTAIN_ETW_RAW_CLOCK_PATH"))
    write(package / "VALIDATION.json", result); files.append("VALIDATION.json")
    write(package / "CONTENTS.json", [dict(path=name, sha256=sha(package / name), bytes=(package / name).stat().st_size) for name in sorted(files)])
    files.append("CONTENTS.json")
    verify_package(package)
    with zipfile.ZipFile(args.output, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for name in sorted(files):
            archive.write(safe_file(package / relative_path(name)), name)
    with zipfile.ZipFile(args.output) as archive:
        require(archive.testzip() is None and sorted(archive.namelist()) == sorted(files), "archive_readback_failed")
        for name in files:
            require(hashlib.sha256(archive.read(name)).hexdigest() == sha(package / name), "archive_content_changed")
    print(json.dumps(dict(protocol=PROTOCOL, phase="packaged", source_sha=manifest["source_sha"],
                         candidate_verified=complete, zip_sha256=sha(args.output))))
    return 0 if complete else 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    freeze = sub.add_parser("prepare")
    freeze.add_argument("--repository", type=Path, default=REPO)
    freeze.add_argument("--contain", type=Path, required=True)
    freeze.add_argument("--fixture", type=Path, required=True)
    freeze.add_argument("--profile", choices=("debug", "release"), required=True)
    freeze.add_argument("--runtime-directory", type=Path)
    freeze.add_argument("--output", type=Path, required=True)
    export = sub.add_parser("report")
    export.add_argument("--package", type=Path, required=True)
    export.add_argument("--capture-directory", type=Path, required=True)
    export.add_argument("--private-output", type=Path, required=True)
    export.add_argument("--public-output", type=Path, required=True)
    export.add_argument("--scenario", choices=("default", "dual-clock"), required=True)
    finish = sub.add_parser("finalize")
    finish.add_argument("--package", type=Path, required=True)
    finish.add_argument("--default-directory", type=Path, required=True)
    finish.add_argument("--clock-directory", type=Path, required=True)
    finish.add_argument("--clock-analysis", type=Path, required=True)
    finish.add_argument("--quality-summary", type=Path, required=True)
    finish.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        return {"prepare": prepare, "report": report, "finalize": finalize}[args.action](args)
    except (OSError, ValueError, KeyError, TypeError, struct.error, UnicodeError, subprocess.SubprocessError) as error:
        # Full errors can contain private paths. CI retains the command's local
        # input/output directory; public stdout only names the failed phase.
        code = str(error) if isinstance(error, ValueError) and re.fullmatch(r"[a-z_]+", str(error)) else "trial_artifact_failed"
        print(json.dumps(dict(protocol=PROTOCOL, phase=args.action, error=code)))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
