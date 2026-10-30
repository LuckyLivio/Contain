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
REPORT_PROTOCOL = "contain-trial-report-3"
TARGET = "x86_64-pc-windows-msvc"
BINARIES = ("contain.exe", "contain-test-installer.exe")
SCRIPTS = (
    "demo.ps1", "demo-save-capture.ps1", "demo-provenance.ps1", "demo-checks.ps1", "demo-measure.py",
    "score-fixture.ps1", "score-fixture.py", "v04-verify-views.py",
    "v04-clock-protocol.py", "v04-matrix.py", "v04-metrics.py",
)
RESOURCES = ("LICENSE", "docs/schema/cli-v3.schema.json",
             "docs/examples/read-only-finished.txt", "docs/examples/read-only-historical.txt",
             "docs/examples/read-only-interrupted.txt")
SYSTEM_DLLS = frozenset("advapi32.dll bcrypt.dll bcryptprimitives.dll cabinet.dll cfgmgr32.dll "
    "comctl32.dll comdlg32.dll crypt32.dll dbghelp.dll dnsapi.dll dwmapi.dll gdi32.dll "
    "imm32.dll iphlpapi.dll kernel32.dll kernelbase.dll mpr.dll msimg32.dll msvcrt.dll "
    "ncrypt.dll netapi32.dll ntdll.dll ole32.dll oleaut32.dll pdh.dll powrprof.dll propsys.dll "
    "psapi.dll rpcrt4.dll secur32.dll setupapi.dll shell32.dll shlwapi.dll user32.dll "
    "tdh.dll userenv.dll ucrtbase.dll version.dll wevtapi.dll wer.dll winhttp.dll wininet.dll "
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
    manifest["report_exporter"] = exporter_identity()
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
            # A verified directory does not authorize a neighbouring prefix such
            # as root-other. Keep that private spelling intact so rejection below
            # can catch it instead of disguising it as an approved fixture path.
            boundary = r"(?=$|[\\/\"\r\n])" if alias in {"<fixture-root>", "<fixture-registry-key>"} else ""
            text = re.sub(re.escape(original) + boundary, lambda match: alias, text, flags=re.IGNORECASE)
            text = re.sub(re.escape(original.replace("\\", "\\\\")) + boundary, lambda match: alias, text, flags=re.IGNORECASE)
    # Rust's text explanation quotes paths as JSON strings. After replacing a
    # known fixture root, normalize only that alias's escaped path separators;
    # an unrelated UNC path elsewhere must still be rejected below.
    text = re.sub(r"<fixture-(?:root|bin|registry-key)>(?:\\{1,2}[A-Za-z0-9_.-]+)*",
                  lambda match: match[0].replace("\\\\", "\\"), text)
    require(not re.search(r"<fixture-root>(?:[\\/]+[^\\/\r\n\"]+)*[\\/]+\.{1,2}(?=[\\/\r\n\"]|$)", text),
            "public_text_ambiguous_relative_path")
    require(not re.search(r"[A-Za-z]:[\\/]|\\\\|S-1-5-\d|\\Device\\|\\Registry\\", text, re.IGNORECASE), "public_text_contains_private_path")
    return text


def windows_spelling(value):
    value = value.replace("/", "\\")
    for prefix in ("\\\\?\\", "\\??\\"):
        if value.startswith(prefix):
            value = value[len(prefix):]
    return value.casefold().rstrip("\\")


def exporter_identity():
    return dict(protocol_revision=REPORT_PROTOCOL, sha256=sha(Path(__file__)))


def directory_identity(path):
    """Use the live OS directory identity (Windows volume/file ID via stat).

    Zero/absent file IDs cannot prove same-directory identity. Resolve obtains
    the canonical long Windows spelling; neither short names nor file children
    are guessed. The IDs and paths are private diagnostic evidence only.
    """
    try:
        value = path.stat()
        require(stat.S_ISDIR(value.st_mode) and type(value.st_ino) is int and value.st_ino > 0
                and type(value.st_dev) is int and value.st_dev >= 0, "fixture_directory_identity_unavailable")
        return value.st_dev, value.st_ino
    except (OSError, ValueError):
        return None


class FixtureScope:
    """Only verified root aliases, shared by selection, diff and redaction.

    Matching names restricts this public fixture excerpt, not resource identity
    or ownership. Historical/deleted children never have to exist today.
    """
    def __init__(self, canonical, aliases, registry_key):
        self.canonical = canonical
        self.aliases = set(aliases) | {canonical}
        self.registry_key = registry_key

    def contains_file(self, resource):
        if not isinstance(resource, str):
            return False
        value = windows_spelling(resource)
        for root in self.aliases:
            prefix = windows_spelling(root) + "\\"
            if value.startswith(prefix) and not {".", ".."}.intersection(value[len(prefix):].split("\\")):
                return True
        return False

    def observe(self, resource):
        # Only inspect directory ancestors, never a recorded/deleted child.
        # Additional aliases need the same live OS identity as the proved root.
        if not isinstance(resource, str) or self.contains_file(resource):
            return
        canonical = Path(self.canonical)
        expected = directory_identity(canonical)
        require(expected is not None, "fixture_directory_identity_unavailable")
        for parent in Path(resource).parents:
            if directory_identity(parent) != expected:
                continue
            try:
                if parent.samefile(canonical):
                    self.aliases.add(str(parent))
                    return
            except OSError:
                pass

    def includes_diff(self, line):
        match = re.fullmatch(r"  (FILE|REGISTRY) \S+ (.+) \[[^\]]+\]", line)
        if not match:
            return False
        kind, resource = match.groups()
        if kind == "FILE":
            return self.contains_file(resource)
        key = self.registry_key.casefold()
        return resource.casefold() == key or resource.casefold().startswith(key + "\\")

    def replacements(self):
        result = {self.registry_key: "<fixture-registry-key>"}
        for original in self.aliases:
            # These are representations of an already verified root, not guessed
            # 8.3 names. Include full prefixes before replacing embedded roots.
            result[original] = "<fixture-root>"
            normalized = original.replace("/", "\\")
            for prefix in ("\\\\?\\", "\\??\\"):
                if normalized.startswith(prefix):
                    normalized = normalized[len(prefix):]
                    break
            for value in (normalized, normalized.replace("\\", "/")):
                result[value] = "<fixture-root>"
            if re.match(r"^[A-Za-z]:\\", normalized):
                result["\\\\?\\" + normalized] = "<fixture-root>"
                result["\\??\\" + normalized] = "<fixture-root>"
        return result


def fixture_scope(observed_root, captured_roots, registry_key):
    require(isinstance(observed_root, str) and observed_root and isinstance(captured_roots, list)
            and len(captured_roots) == 1 and isinstance(captured_roots[0], str), "not_fixed_fixture_scope")
    roots = [Path(observed_root), Path(captured_roots[0])]
    identities = [directory_identity(root) for root in roots]
    require(all(value is not None for value in identities), "fixture_directory_identity_unavailable")
    try:
        require(identities[0] == identities[1] and roots[0].samefile(roots[1]), "fixture_directory_identity_mismatch")
        canonical = roots[0].resolve(strict=True)
        require(not redirected(roots[0]) and not redirected(roots[1]), "fixture_directory_redirected")
        require(re.fullmatch(r"contain-demo-[0-9a-f]{32}", canonical.name), "not_fixed_fixture_scope")
        require(registry_key == "Software\\Contain\\Demo\\" + canonical.name, "not_fixed_fixture_registry")
        require(safe_file(canonical / ".contain-demo-marker").read_bytes() == b"Contain test fixture",
                "fixture_marker_invalid")
        aliases = [observed_root, captured_roots[0]] + [str(root) for root in roots] + [str(root.resolve(strict=True)) for root in roots]
        require(all(directory_identity(root) == identities[0] for root in roots + [canonical]),
                "fixture_directory_identity_changed")
    except OSError as error:
        raise ValueError("fixture_directory_identity_unavailable") from error
    return FixtureScope(str(canonical), aliases, registry_key)


def legacy_score_result(directory):
    """Publish the unchanged scorer's fixed fields, even without a CLI report.

    Numeric/group/UUID fields follow analyze-etw-clock.py's legacy projection.
    Rows add only fixed enums and event IDs, never resources, actors or reasons
    supplied as free text. This is preservation, not a replacement score.
    """
    path = directory / "reliability.json"
    try:
        value = read(safe_file(path))
        require(isinstance(value, dict), "legacy_score_document_invalid")
        digest = sha(path)
    except (OSError, ValueError, TypeError):
        return dict(status="unavailable", source_sha256=None, score=None,
                    failure_reasons=["legacy_score_missing_or_unreadable"])
    failures = set()
    def number(source, key, nullable=False, rate=False):
        item = source.get(key)
        numeric = (type(item) in (int, float) and 0 <= item <= 1) if rate else type(item) is int and item >= 0
        valid = key in source and ((item is None and nullable) or numeric)
        if not valid:
            failures.add("legacy_score_numeric_field_invalid")
        return item if valid else None
    def event_id(item, nullable=False):
        if item is None and nullable:
            return None
        if isinstance(item, str) and re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", item):
            return item
        failures.add("legacy_score_event_id_invalid")
        return None
    def choice(source, key, allowed, nullable=False):
        item = source.get(key)
        if key in source and ((item is None and nullable) or isinstance(item, str) and item in allowed):
            return item
        failures.add("legacy_score_row_enum_invalid")
        return None
    counts = ("schema_version", "expected", "observed", "correctly_attributed", "unknown", "incorrect_attribution",
              "positive_raw_predictions", "false_positive_target_events", "supporting_duplicate_raw_events",
              "excluded_control_positive_events", "dropped")
    score = {key: number(value, key) for key in counts}
    score.update({key: number(value, key, nullable=True) for key in ("etw_events_lost", "etw_buffers_lost")})
    score["precision"] = number(value, "precision", nullable=True, rate=True)
    if score["schema_version"] != 2:
        failures.add("legacy_score_schema_invalid")
    extra = value.get("unexpected_positive_events")
    score["unexpected_positive_events"] = [event_id(item) for item in extra] if isinstance(extra, list) else None
    if not isinstance(extra, list) or len(extra) != score["false_positive_target_events"]:
        failures.add("legacy_score_extra_positive_count_invalid")
    group_names = {"target_success", "noise_success", "failed", "unsupported"}
    group_keys = ("expected", "observed", "unobserved", "unsupported_out_of_scope", "correctly_attributed",
                  "incorrectly_attributed", "observed_but_unresolved", "correctly_not_assigned_to_target_app")
    groups = value.get("groups"); score["groups"] = {}
    for name in sorted(group_names):
        group = groups.get(name) if isinstance(groups, dict) else None
        group = group if isinstance(group, dict) else {}
        score["groups"][name] = {key: number(group, key) for key in group_keys}
        score["groups"][name].update({key: number(group, key, nullable=True, rate=True)
            for key in ("observation_rate", "correct_attribution_rate")})
    rows = value.get("rows"); score["rows"] = [] if isinstance(rows, list) else None
    if not isinstance(rows, list) or len(rows) != score["expected"]:
        failures.add("legacy_score_row_count_invalid")
    for index, original in enumerate(rows if isinstance(rows, list) else []):
        row = original if isinstance(original, dict) else {}
        public = dict(score_row=index,
            role=choice(row, "role", {"parent", "child", "grandchild", "unrelated", "detached", "short", "stress", "worker", "noise", "burst"}),
            group=choice(row, "group", group_names),
            operation=choice(row, "operation", {"write_requested", "rename_requested", "delete_requested", "set_value"}),
            observation=choice(row, "observation", {"unsupported_out_of_scope", "observed", "unobserved"}),
            attribution=choice(row, "attribution", {"correctly_attributed", "incorrectly_attributed",
                "correctly_not_assigned_to_target_app", "observed_but_unresolved"}, nullable=True),
            outcome=choice(row, "outcome", {"Correct", "Incorrect", "Unknown"}),
            matched_event_id=event_id(row.get("matched_event_id"), nullable=row.get("observed") is False),
            raw_matches=number(row, "raw_matches"))
        for key in ("success", "observed"):
            public[key] = row.get(key) if type(row.get(key)) is bool else None
            if public[key] is None:
                failures.add("legacy_score_row_boolean_invalid")
        # Original enum outcomes are the reason codes; no new matching inference.
        public["reason_codes"] = [item for item in (public["observation"], public["attribution"]) if item]
        score["rows"].append(public)
    return dict(status="invalid" if failures else "available", source_sha256=digest, score=score,
                row_order="unchanged_legacy_scorer_order", failure_reasons=sorted(failures))


# Reviewed structural locations only. Unknown object keys are never published,
# even if a private map happens to use a familiar schema property name.
PUBLIC_FIELDS = {
    (): frozenset({'schema_version', 'kind', 'data'}),
    ('data',): frozenset({'id', 'schema_version', 'name', 'installer', 'started_at', 'finished_at', 'watch_roots',
        'registry_key', 'capture_state', 'backend', 'stats', 'quality', 'events', 'processes', 'operations',
        'edges', 'warnings', 'files', 'registry', 'inventory', 'exit_code'}),
    ('data', 'events', '*'): frozenset({'id', 'timestamp', 'timestamp_ticks', 'sequence', 'event_type',
        'operation', 'resource', 'confidence', 'evidence', 'raw', 'dimensions', 'reason', 'state_validated', 'success'}),
    ('data', 'events', '*', 'evidence'): frozenset({'source', 'pid', 'process_creation_time', 'reason', 'timestamp'}),
    ('data', 'stats'): frozenset({'events_received', 'events_dropped', 'events_retained', 'source_events',
        'events_normalized', 'high_confidence_events', 'capture_elapsed_ms', 'drain_timed_out',
        'known_actor_events', 'known_resource_events', 'unknown_actor_events', 'unknown_resource_events'}),
    ('data', 'quality'): frozenset({'level', 'reasons'}),
    ('data', 'backend'): frozenset({'etw_process', 'etw_file', 'etw_registry', 'etw_events_lost',
        'etw_buffers_lost', 'pipeline', 'stream'}),
    ('data', 'processes', '*'): frozenset({'pid', 'creation_time', 'image', 'parent_pid', 'parent_creation_time'}),
}


def json_type(value):
    return {type(None): 'null', bool: 'boolean', int: 'integer', float: 'number', str: 'string',
            list: 'array', dict: 'object'}[type(value)]


def json_pointer(parts, public=False):
    result, context, reviewed = [], [], True
    for part in parts:
        if type(part) is int:
            result.append(str(part)); context.append('*')
        else:
            allowed = reviewed and part in PUBLIC_FIELDS.get(tuple(context), ())
            result.append(part if not public or allowed else '<unreviewed-key>')
            context.append(part); reviewed = allowed
    return '/' + '/'.join(part.replace('~', '~0').replace('/', '~1') for part in result) if result else ''


def compare_json_documents(before, after, limit=512):
    """Strict typed JSON comparison; no clock, number or field normalization.

    Counts cover the whole document even if retained differences are truncated.
    Public paths contain only reviewed structural names and array positions.
    Full JSON pointers and changed values remain in the private projection.
    """
    counts, public_rows, private_rows = Counter(), [], []
    def add(category, path, left=None, right=None, left_present=True, right_present=True):
        counts[category] += 1
        if len(public_rows) >= limit:
            return
        row = dict(index=sum(counts.values()) - 1, category=category, path=json_pointer(path, public=True),
            before_type=json_type(left) if left_present else 'missing',
            after_type=json_type(right) if right_present else 'missing')
        if category == 'array_length_changed':
            row.update(before_length=len(left), after_length=len(right))
        public_rows.append(row)
        private_rows.append(dict(row, path=json_pointer(path), before=left, after=right,
                                 before_present=left_present, after_present=right_present))
    def visit(left, right, path):
        if type(left) is not type(right):
            add('type_changed', path, left, right)
        elif isinstance(left, dict):
            for key in sorted(left.keys() | right.keys()):
                if key not in left: add('field_added', path + [key], right=right[key], left_present=False)
                elif key not in right: add('field_missing', path + [key], left=left[key], right_present=False)
                else: visit(left[key], right[key], path + [key])
        elif isinstance(left, list):
            if len(left) != len(right): add('array_length_changed', path, left, right)
            for index in range(max(len(left), len(right))):
                if index >= len(left): add('array_element_added', path + [index], right=right[index], left_present=False)
                elif index >= len(right): add('array_element_missing', path + [index], left=left[index], right_present=False)
                else: visit(left[index], right[index], path + [index])
        elif left != right:
            add('value_changed', path, left, right)
    visit(before, after, [])
    common = dict(protocol_revision='contain-capture-comparison-1', json_equal=not counts,
        comparison_scope='complete_inspect_envelope_strict_types_and_values',
        difference_count=sum(counts.values()), counts=dict(sorted(counts.items())),
        retained_count=len(public_rows), truncated=sum(counts.values()) > len(public_rows), limit=limit)
    return dict(common, differences=public_rows), dict(common, differences=private_rows)


def compare_capture_files(saved, reopened, manifest, package, database=None):
    public, private = compare_json_documents(read(saved), read(reopened))
    public['bytes_equal'] = safe_file(saved).read_bytes() == safe_file(reopened).read_bytes()
    public['byte_representation'] = 'saved_UTF8_file_vs_native_CLI_stdout_bytes'
    public['source_sha'] = manifest['source_sha']
    public['exporter'] = exporter_identity()
    public['build_manifest_sha256'] = sha(package / 'BUILD.json') if (package / 'BUILD.json').is_file() else None
    public['contain_sha256'] = sha(package / 'contain.exe') if (package / 'contain.exe').is_file() else None
    public['save_implementation_sha256'] = sha(package / 'scripts/demo-save-capture.ps1') if (package / 'scripts/demo-save-capture.ps1').is_file() else None
    public['inputs'] = [dict(identity(path, role), source=source) for path, role, source in (
        (saved, 'saved_capture_json', 'demo_validated_CLI_stdout_save'),
        (reopened, 'reopened_inspect_json', 'packaged_CLI_private_database_snapshot'))]
    if database is not None:
        public['database_snapshot'] = identity(database, 'private_read_only_snapshot')
    private.update({key: value for key, value in public.items() if key != 'differences'})
    private['inputs'] = [dict(row, path=str(path.absolute()), filesystem_identity=list((path.stat().st_dev, path.stat().st_ino)))
                        for row, path in zip(public['inputs'], (saved, reopened))]
    if database is not None: private['database_snapshot'] = dict(public['database_snapshot'], path=str(database.absolute()))
    return public, private


def safe_report_event_id(value, synthetic=False):
    return isinstance(value, str) and bool(re.fullmatch(
        r'synthetic-report-event-[0-9]+' if synthetic else r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}', value))


def report(args):
    package = args.package.resolve()
    manifest, _ = verify_package(package)
    local = new_directory(args.private_output)
    public = new_directory(args.public_output)
    summary = dict(schema_version=1, protocol_revision=PROTOCOL, source_sha=manifest["source_sha"],
        scenario=args.scenario, scope="synthetic_windows_cli_report" if args.scenario == "synthetic"
            else "real_etw_controlled_fixture_cli_excerpt", complete=False, exporter=exporter_identity(),
        candidate_verified=False, report_generated=False, acceptance_passed=False, commands=[], checks={}, failure=None, failures=[],
        legacy_score=legacy_score_result(args.capture_directory))
    stage = "run_identity"
    try:
        require(manifest.get("report_exporter") == summary["exporter"], "report_exporter_not_frozen")
        same, observed = run_identity_matches(args.capture_directory, manifest, sha(package / "BUILD.json"))
        require(same, "report_run_identity_not_package")
        capture_path = safe_file(args.capture_directory / "capture.json")
        capture = json_document(capture_path.read_text(encoding="utf-8-sig"), "inspect")
        truth = read(args.capture_directory / "ground-truth.json")
        if args.scenario == "synthetic":
            require(capture.get("name") == "SyntheticReport" and isinstance(truth, list) and bool(truth)
                    and observed.get("synthetic") is True
                    and read(args.capture_directory / "metadata.json").get("kind") == "synthetic_report_fixture"
                    and any(isinstance(warning, str) and warning.startswith("SYNTHETIC")
                            for warning in capture.get("warnings", [])), "report_synthetic_marker_missing")
        else:
            require(observed.get("synthetic") is not True and capture.get("name") == "TestFixture"
                    and len(truth) == 21, "not_fixed_controlled_fixture")
            require(all(capture["backend"].get(key) == "active" for key in ("etw_process", "etw_file", "etw_registry")), "report_not_real_etw")
        app_name = capture["name"]
        key = capture.get("registry_key")
        stage = "fixture_scope"
        scope = fixture_scope(observed["fixture_root"], capture["watch_roots"], key)
        write(local / "fixture-scope.json", dict(canonical=scope.canonical, aliases=sorted(scope.aliases),
              identity=directory_identity(Path(scope.canonical))))
        summary["checks"]["fixture_directory_identity"] = True
        database = Path(observed["database"])
        stage = "database_snapshot"
        copy = local / "read-only.db"; snapshot(database, copy)
        executable = package / "contain.exe"
        def failure(code):
            if code not in summary["failures"]: summary["failures"].append(code)
            if summary["failure"] is None: summary["failure"] = code
        def command(label, kind, *arguments, json_output=False):
            nonlocal stage
            stage = label
            argv = [str(executable), "--db", str(copy), kind, *arguments] + (["--json"] if json_output else [])
            row = dict(name=label, status="failed")
            try:
                completed = subprocess.run(argv, capture_output=True, timeout=120)
                stdout = completed.stdout.encode("utf-8") if isinstance(completed.stdout, str) else completed.stdout
                stderr = completed.stderr.encode("utf-8") if isinstance(completed.stderr, str) else completed.stderr
                (local / (label + ".stdout.txt")).write_bytes(stdout)
                (local / (label + ".stderr.txt")).write_bytes(stderr)
                row.update(exit_code=completed.returncode, stdout_sha256=hashlib.sha256(stdout).hexdigest())
                require(completed.returncode == 0, "report_cli_failed")
                text = stdout.decode("utf-8")
                value = json_document(text, kind) if json_output else text
                row["status"] = "passed"
                return value
            except (OSError, UnicodeError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
                code = str(error) if isinstance(error, ValueError) and re.fullmatch(r"[a-z_]+", str(error)) else "report_cli_failed"
                failure(code); row["failure"] = code
                (local / (label + ".failure.txt")).write_text(repr(error), encoding="utf-8")
                return None
            finally:
                summary["commands"].append(row)
        def skipped(label, reason):
            summary["commands"].append(dict(name=label, status="not_run", reason=reason))
        reopened = command("inspect-json", "inspect", app_name, json_output=True)
        require(reopened is not None, "report_reopened_inspect_unavailable")
        comparison, private_comparison = compare_capture_files(capture_path, local / "inspect-json.stdout.txt",
            manifest, package, copy if copy.is_file() else None)
        summary["comparison"] = comparison
        write(local / "comparison-details.json", private_comparison)
        summary["checks"]["reopen_json_equal"] = comparison["json_equal"]
        if not comparison["json_equal"]: failure("report_reopened_capture_changed")
        # Re-establish scope from this readback. A mismatching saved document
        # never supplies the event, process whitelist or redaction identities.
        stage = "reopened_scope"
        require(reopened.get("name") == app_name and reopened.get("id") == capture.get("id"), "report_reopened_identity_changed")
        require(reopened.get("registry_key") == key, "report_reopened_registry_changed")
        reopened_scope = fixture_scope(observed["fixture_root"], reopened["watch_roots"], reopened["registry_key"])
        require(directory_identity(Path(reopened_scope.canonical)) == directory_identity(Path(scope.canonical)),
                "report_reopened_scope_changed")
        scope.aliases.update(reopened_scope.aliases)
        if args.scenario != "synthetic":
            require(all(reopened["backend"].get(key) == "active" for key in ("etw_process", "etw_file", "etw_registry")), "report_not_real_etw")
        fixture_actors = {(row["pid"], str(row["creation_time"])) for row in truth}
        for process in reopened["processes"]:
            require((process["pid"], str(process["creation_time"])) in fixture_actors, "report_process_outside_fixture")
        require(reopened.get("capture_state") in {None, "capturing", "finalizing", "finished", "failed"}
                and reopened.get("quality", {}).get("level") in {"Complete", "Degraded", "Incomplete"},
                "report_state_or_quality_invalid")
        summary["checks"]["reopened_scope_identity"] = True
        for row in reopened["events"]:
            if row["event_type"] == "file": scope.observe(row["resource"])
        selected = next((row for row in reopened["events"] if row["event_type"] == "file"
            and row["confidence"] in {"High", "Certain"} and row["operation"] == "write_requested"
            and row["evidence"].get("source") == "EtwFile" and scope.contains_file(row["resource"])
            and safe_report_event_id(row.get("id"), args.scenario == "synthetic")
            and (row["evidence"].get("pid"), str(row["evidence"].get("process_creation_time"))) in fixture_actors), None)
        event_id = selected["id"] if selected else None
        index = reopened["events"].index(selected) if selected else None
        if selected:
            require(sum(row.get("id") == event_id for row in reopened["events"]) == 1, "report_event_id_ambiguous")
            history = command("history-json", "history", app_name, "--limit", "1", "--offset", str(index), json_output=True)
            explanation = command("explain-json", "explain", event_id, json_output=True)
            summary["checks"]["history_page_equal"] = history is not None and history.get("events") == [selected]
            summary["checks"]["explain_event_equal"] = explanation is not None and explanation.get("event") == selected and explanation.get("app_id") == reopened["id"]
            if not summary["checks"]["history_page_equal"]: failure("report_history_page_mismatch")
            if not summary["checks"]["explain_event_equal"]: failure("report_explain_mismatch")
        else:
            failure("report_no_known_fixture_source")
            skipped("history-json", "verified_event_unavailable"); skipped("explain-json", "verified_event_unavailable")
        inspect_text = command("inspect-text", "inspect", app_name)
        diff_text = command("diff-text", "diff", app_name)
        if selected:
            history_text = command("history-text", "history", app_name, "--limit", "1", "--offset", str(index))
            explain_text = command("explain-text", "explain", event_id)
        else:
            history_text = explain_text = None
            skipped("history-text", "verified_event_unavailable"); skipped("explain-text", "verified_event_unavailable")
        summary["checks"]["json_stdout_only"] = all(row.get("status") == "passed" for row in summary["commands"] if row["name"].endswith("-json"))
        inspect_available, diff_available = inspect_text is not None, diff_text is not None
        if not inspect_available: inspect_text = "NOT AVAILABLE: inspect text command failed; see command result.\n"
        if not diff_available: diff_text = "NOT AVAILABLE: diff text command failed; see command result.\n"
        history_verified = selected is not None and summary["checks"].get("history_page_equal") is True
        explain_verified = selected is not None and summary["checks"].get("explain_event_equal") is True
        for label, text_value, verified in (("history", history_text, history_verified), ("explain", explain_text, explain_verified)):
            if verified and (text_value is None or event_id not in text_value or selected["operation"] not in text_value):
                failure("report_text_missing_event_id")
                if label == "history": history_verified = False
                else: explain_verified = False
        if not history_verified: history_text = "NOT PUBLISHED: this event view was unavailable or did not match the verified readback.\n"
        if not explain_verified: explain_text = "NOT PUBLISHED: this event view was unavailable or did not match the verified readback.\n"
        stage = "public_excerpt"
        for line in diff_text.splitlines():
            match = re.fullmatch(r"  FILE \S+ (.+) \[[^\]]+\]", line)
            if match:
                scope.observe(match[1])
        replacements = scope.replacements()
        replacements[reopened["installer"]] = "<fixture-bin>\\contain-test-installer.exe"
        for process in reopened["processes"]:
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
                      or scope.includes_diff(line)]
        fixture_diff_present = diff_available and any(scope.includes_diff(line) for line in diff_lines)
        if not fixture_diff_present:
            failure("report_fixture_diff_missing")
            if not diff_lines: diff_lines = ["NOT AVAILABLE: no verified fixture diff excerpt; see command result."]
        # Explain is restricted to a verified fixture source and its ancestry.
        # Omit free-form general warnings rather than guess their sensitivity.
        explanation_warnings = [line for line in explain_text.splitlines()
                                if line.startswith("Limitation:") and line not in SAFE_LIMITATIONS]
        explanation_lines = [line for line in explain_text.splitlines() if line not in explanation_warnings]
        sections = dict(INSPECT=public_text(inspect_safe, replacements),
            DIFF=public_text("\n".join(diff_lines) + "\n", replacements),
            HISTORY=public_text(history_text, replacements),
            EXPLAIN=public_text("\n".join(explanation_lines) + "\n", replacements))
        require(all(content.strip() for content in sections.values()), "report_section_empty")
        inspect_agrees = inspect_available and app_name in inspect_text and f"Recording: {reopened['capture_state']} | Quality: {reopened['quality']['level']}" in inspect_text
        if not inspect_agrees: failure("report_text_json_disagree")
        summary["checks"].update(all_seven_commands_executed=len(summary["commands"]) == 7 and all("exit_code" in row for row in summary["commands"]),
            text_json_agree=inspect_agrees and history_verified and explain_verified,
            fixture_diff_present=fixture_diff_present, public_paths_redacted=True)
        title = ("SYNTHETIC WINDOWS CLI REPORT — production storage and packaged contain.exe; not ETW or an installation.\n"
                 if args.scenario == "synthetic" else
                 "REAL ETW CONTROLLED FIXTURE — actual packaged contain.exe output; not a third-party installer.\n")
        consistency = "PASSED" if comparison["json_equal"] else "FAILED — CONSISTENCY CHECK FAILED; candidate acceptance failed"
        text = (title + f"CAPTURE/REOPEN CONSISTENCY: {consistency}.\n" +
                f"Report acceptance: {'FAILED' if summary['failures'] else 'PASSED'}; report generation and candidate acceptance are separate.\n" +
                f"Strict JSON differences: {comparison['difference_count']}; retained: {comparison['retained_count']}; truncated: {comparison['truncated']}. Byte-identical: {comparison['bytes_equal']}.\n" +
                "Fixture paths are replaced consistently; no database or system trace is published.\n"
                f"Source: {manifest['source_sha']} | configuration: {args.scenario} | event: {event_id}\n"
                f"Public excerpt omits {len(warning_lines)} inspect and {len(explanation_warnings)} explain free-form limitation lines; recording quality and measured gaps below are unchanged.\n"
                "Diff excerpt shows fixture file/registry changes only; inventory details are omitted.\n"
                "See local inspect --verbose for all warnings. Source confidence does not establish explicit API intent or ownership.\n\n")
        for name, content in sections.items():
            display = dict(INSPECT=f"inspect {app_name}", DIFF=f"diff {app_name}",
                HISTORY=f"history {app_name} --limit 1 --offset {index}" if index is not None else "history: no verified event selected",
                EXPLAIN=f"explain {event_id}" if event_id else "explain: no verified event selected")
            text += name + "\n> .\\contain.exe --db <saved-db> " + display[name] + "\n" + content + "\n"
        verify_package(package)
        (public / "cli-report.txt").write_text(text, encoding="utf-8", newline="\n")
        summary.update(complete=not summary["failures"], acceptance_passed=not summary["failures"], report_generated=True, event_id=event_id, capture_state=reopened["capture_state"],
            quality=reopened["quality"]["level"], omitted_limitation_lines=len(warning_lines),
            omitted_explain_limitation_lines=len(explanation_warnings),
            report_sha256=sha(public / "cli-report.txt"),
            interpretation="Actual read-only commands on a backup; excerpt/JSON agreement only, not product or formal acceptance")
    except (OSError, ValueError, KeyError, TypeError, StopIteration, sqlite3.Error, subprocess.SubprocessError) as error:
        summary["complete"] = False
        code = str(error) if isinstance(error, ValueError) and re.fullmatch(r"[a-z_]+", str(error)) else "report_failed_private_details_retained"
        if code not in summary["failures"]: summary["failures"].append(code)
        if summary["failure"] is None: summary["failure"] = code
        (local / "failure.txt").write_text(repr(error), encoding="utf-8")
        write(local / "failure-context.json", dict(stage=stage, capture_directory=str(args.capture_directory),
            commands=summary["commands"], exporter=summary["exporter"], exception=repr(error)))
    if summary["failure"] and not (local / "failure-context.json").exists():
        write(local / "failure-context.json", dict(stage=stage, capture_directory=str(args.capture_directory),
            commands=summary["commands"], exporter=summary["exporter"], failures=summary["failures"]))
    planned = ["inspect-json", "history-json", "explain-json", "inspect-text", "diff-text", "history-text", "explain-text"]
    present = {row["name"] for row in summary["commands"]}
    for label in planned:
        if label not in present: summary["commands"].append(dict(name=label, status="not_run", reason="required_identity_scope_or_input_unavailable"))
    summary["commands_executed"] = sum("exit_code" in row for row in summary["commands"])
    summary["commands_not_run"] = sum(row.get("status") == "not_run" for row in summary["commands"])
    summary["commands_failed"] = sum(row.get("status") == "failed" for row in summary["commands"])
    summary["last_stage"] = stage
    write(public / "summary.json", summary)
    print(json.dumps(dict(protocol=PROTOCOL, phase="report", complete=summary["complete"], failure=summary["failure"])))
    return 0 if summary["complete"] else 2


def report_matches(item, manifest, scenario, text_path):
    expected_scope = "synthetic_windows_cli_report" if scenario == "synthetic" else "real_etw_controlled_fixture_cli_excerpt"
    commands = item.get("commands", [])
    return bool(item.get("schema_version") == 1 and item.get("protocol_revision") == PROTOCOL
        and item.get("scenario") == scenario and item.get("scope") == expected_scope
        and item.get("complete") is True and item.get("acceptance_passed") is True
        and item.get("report_generated") is True and not item.get("failures")
        and item.get("comparison", {}).get("json_equal") is True
        and item.get("source_sha") == manifest["source_sha"]
        and item.get("exporter") == manifest.get("report_exporter") == exporter_identity()
        and isinstance(commands, list) and [row.get("name") for row in commands] == ["inspect-json", "history-json",
            "explain-json", "inspect-text", "diff-text", "history-text", "explain-text"]
        and all(row.get("exit_code") == 0 for row in commands)
        and all(item.get("checks", {}).get(key) is True for key in ("fixture_directory_identity", "reopened_scope_identity", "reopen_json_equal",
            "history_page_equal", "explain_event_equal", "json_stdout_only", "all_seven_commands_executed",
            "text_json_agree", "fixture_diff_present", "public_paths_redacted"))
        and text_path.is_file() and item.get("report_sha256") == sha(safe_file(text_path)))


def collect_report(package, scenario, source):
    """The shared package collection step accepts only the reviewed report pair.

    Synthetic preflight uses the identical collector in a package copy. Its
    separate scope/scenario cannot satisfy either real-report candidate gate.
    """
    require(scenario in {"default", "dual-clock", "synthetic"}, "report_scenario_invalid")
    package = Path(package).resolve(); source = Path(source)
    manifest, _ = verify_package(package)
    item = read(safe_file(source / "summary.json"))
    text_path = source / "cli-report.txt"
    if item.get("complete") is True:
        require(report_matches(item, manifest, scenario, text_path), "report_collection_contract_failed")
    elif item.get("report_generated") is True:
        require(item.get("complete") is False and item.get("acceptance_passed") is False
                and item.get("failure") is not None and item.get("failures")
                and item.get("checks", {}).get("fixture_directory_identity") is True
                and item.get("checks", {}).get("reopened_scope_identity") is True
                and item.get("checks", {}).get("public_paths_redacted") is True
                and item.get("scenario") == scenario and item.get("source_sha") == manifest["source_sha"]
                and item.get("exporter") == manifest.get("report_exporter") == exporter_identity()
                and text_path.is_file() and item.get("report_sha256") == sha(safe_file(text_path)),
                "failed_report_collection_contract_failed")
    else:
        require(not text_path.exists() and item.get("scenario") == scenario
                and item.get("source_sha") == manifest["source_sha"]
                and item.get("exporter") == manifest.get("report_exporter") == exporter_identity(),
                "failed_report_collection_contract_failed")
    examples = package / "examples"
    examples.mkdir(exist_ok=True)
    destination = new_directory(examples / scenario)
    for name in ("summary.json", "cli-report.txt"):
        if (source / name).is_file():
            shutil.copyfile(safe_file(source / name), destination / name)
    return destination


def diagnose_report(args):
    """One read-only investigation of the same retained database, never a retry.

    Independent CLI readback still runs when fixture scope verification fails.
    Nothing here can promote the original report or candidate verdict.
    """
    local = new_directory(args.private_output); public = new_directory(args.public_output)
    result = dict(schema_version=1, protocol_revision=REPORT_PROTOCOL, exporter=exporter_identity(),
        scenario=args.scenario, scope="same_capture_offline_diagnosis", candidate_verified=False,
        original_report_unchanged=True, commands=[], failures=[], directory_identity_verified=False,
        original_failure_context_available=(local.parent / "report" / "failure-context.json").is_file(),
        completed=False)
    details = {}
    def failed(error):
        code = str(error) if isinstance(error, ValueError) and re.fullmatch(r"[a-z_]+", str(error)) else "diagnosis_failed_private_details_retained"
        result["failures"].append(code)
        details.setdefault("exceptions", []).append(repr(error))
    try:
        package = args.package.resolve(); manifest, _ = verify_package(package)
        result["source_sha"] = manifest["source_sha"]
        result["frozen_exporter_matches"] = manifest.get("report_exporter") == result["exporter"]
        same, observed = run_identity_matches(args.capture_directory, manifest, sha(package / "BUILD.json"))
        require(same, "diagnosis_run_identity_not_package")
        capture = read(args.capture_directory / "capture.json")["data"]
        details["observed"] = observed
        details["watch_roots"] = capture.get("watch_roots")
        try:
            scope = fixture_scope(observed["fixture_root"], capture["watch_roots"], capture.get("registry_key"))
            details["scope"] = dict(canonical=scope.canonical, aliases=sorted(scope.aliases),
                                    identity=directory_identity(Path(scope.canonical)))
            result["directory_identity_verified"] = True
        except (OSError, ValueError, KeyError, TypeError) as error:
            failed(error)
        copy = local / "read-only.db"; snapshot(Path(observed["database"]), copy)
        app_name = "SyntheticReport" if args.scenario == "synthetic" else "TestFixture"
        event_id = None
        for label, kind, arguments in (("inspect-json", "inspect", [app_name, "--json"]),
                ("diff-text", "diff", [app_name]), ("history-json", "history", [app_name, "--limit", "1", "--offset", "0", "--json"]),
                ("explain-json", "explain", None)):
            if kind == "explain":
                if not event_id:
                    result["commands"].append(dict(name=label, status="not_run", reason="no_readable_event_id"))
                    continue
                arguments = [event_id, "--json"]
            try:
                completed = subprocess.run([str(package / "contain.exe"), "--db", str(copy), kind, *arguments],
                    capture_output=True, text=True, encoding="utf-8", timeout=120)
                (local / (label + ".stdout.txt")).write_text(completed.stdout, encoding="utf-8")
                (local / (label + ".stderr.txt")).write_text(completed.stderr, encoding="utf-8")
                row = dict(name=label, status="passed" if completed.returncode == 0 else "failed", exit_code=completed.returncode,
                    stdout_sha256=hashlib.sha256(completed.stdout.encode()).hexdigest())
                result["commands"].append(row)
                if completed.returncode == 0 and "--json" in arguments:
                    value = json_document(completed.stdout, kind)
                    if kind == "inspect" and value.get("events"):
                        event_id = value["events"][0]["id"]
            except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
                failed(error)
        result["completed"] = True
    except (OSError, ValueError, KeyError, TypeError, sqlite3.Error, subprocess.SubprocessError) as error:
        failed(error)
    write(local / "details.json", details)
    result["commands_executed"] = sum("exit_code" in row for row in result["commands"])
    write(public / "summary.json", result)
    print(json.dumps(dict(phase="diagnose-report", completed=result["completed"],
                         commands_executed=result["commands_executed"], candidate_verified=False)))
    return 0 if result["completed"] else 2


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
    legacy = {"default": legacy_score_result(args.default_directory), "dual-clock": legacy_score_result(args.clock_directory)}
    def optional(path):
        try: return read(path)
        except (OSError, ValueError): return {}
    analysis = optional(args.clock_analysis)
    quality = optional(args.quality_summary)
    preflight_directory = getattr(args, "report_preflight", None)
    preflight = optional(preflight_directory / "summary.json") if preflight_directory else {}
    native_path = getattr(args, "report_paths", None)
    native_paths = optional(native_path) if native_path else {}
    def preflight_passed():
        try:
            cases = preflight["cases"]
            negative = preflight["negative_case"]
            labels = [case["case"] for case in cases]
            eight = preflight["windows_8dot3"]["status"]
            expected = ["normal", "extended"] + (["actual-8dot3"] if eight == "covered" else [])
            command_names = ["inspect-json", "history-json", "explain-json", "inspect-text", "diff-text", "history-text", "explain-text"]
            return bool(preflight.get("schema_version") == 1
                and preflight.get("protocol_revision") == "contain-synthetic-report-preflight-2"
                and preflight.get("scope") == "synthetic_windows_cli_report_preflight"
                and preflight.get("synthetic") is True and preflight.get("native_capture_executed") is False
                and preflight.get("native_windows") is True and preflight.get("passed") is True
                and preflight.get("complete") is True and preflight.get("collection_verified") is True
                and preflight.get("source_sha") == manifest["source_sha"]
                and preflight.get("build_manifest_sha256") == sha(package / "BUILD.json")
                and preflight.get("contain_sha256") == sha(package / "contain.exe")
                and preflight.get("preflight_sha256") == sha(Path(__file__).with_name("verify-trial-report.py"))
                and preflight.get("exporter") == manifest.get("report_exporter") == exporter_identity()
                and eight in {"covered", "not_covered"} and labels == expected
                and preflight.get("negative_verified") is True
                and preflight.get("save_implementation_sha256") == sha(package / "scripts/demo-save-capture.ps1")
                and preflight.get("save_commands_executed") == len(cases) + 1
                and preflight.get("negative_report_commands_executed") == 7
                and negative.get("case") == "changed-event-id" and negative.get("status") == "passed"
                and negative.get("report_exit_code") == 2
                and [row.get("name") for row in negative["commands"]] == command_names
                and all(row.get("exit_code") == 0 for row in negative["commands"])
                and all(negative.get("checks", {}).get(name) is True for name in ("real_contain_exe", "shared_demo_save",
                    "strict_mismatch_rejected", "exact_field_difference", "independent_commands_completed",
                    "report_marked_inconsistent", "production_collection", "production_candidate_report_gate_rejected"))
                and negative.get("comparison", {}).get("json_equal") is False
                and negative.get("comparison", {}).get("difference_count") == 1
                and negative.get("comparison", {}).get("differences", [{}])[0].get("path") == "/data/events/0/id"
                and preflight.get("commands_executed") == preflight.get("report_commands_executed") == 7 * len(cases)
                and preflight.get("commands_failed") == preflight.get("commands_not_run") == 0
                and all(case.get("status") == "passed"
                    and all(case.get("checks", {}).get(name) is True for name in ("real_contain_exe", "all_seven_commands",
                        "reopened_json_equal", "same_event_paged_and_explained", "historical_child_absent", "unknown_preserved",
                        "all_four_sections", "private_paths_redacted", "production_collection", "shared_demo_save",
                        "serialization_sensitive_values", "save_raw_text_preserved"))
                    and [row.get("name") for row in case["commands"]] == command_names
                    and all(row.get("exit_code") == 0 for row in case["commands"]) for case in cases))
        except (OSError, KeyError, IndexError, TypeError, ValueError):
            return False
    short = native_paths.get("short_alias", {})
    native_paths_passed = bool(native_paths.get("schema_version") == 1
        and native_paths.get("scope") == "synthetic_native_windows_directory_identity"
        and native_paths.get("native_windows") is True and native_paths.get("passed") is True
        and native_paths.get("exporter") == manifest.get("report_exporter") == exporter_identity()
        and native_paths.get("test_script_sha256") == sha(Path(__file__).with_name("test-report-paths.py"))
        and native_paths.get("tests_run") == 7 and native_paths.get("failed_count") == 0
        and ((short.get("status") == "covered" and short.get("old_string_comparison_equal") is False
                and short.get("native_samefile") is True and native_paths.get("passed_count") == 7
                and native_paths.get("skipped_count") == 0)
            or (short.get("status") == "unavailable" and native_paths.get("passed_count") == 6
                and native_paths.get("skipped_count") == 1)))
    default_mode = default_identity.get("fixture_clock") in (None, "") and default_identity.get("raw_clock_path") in (None, "") and default.get("clock_requested") is False
    dual_mode = clock_identity.get("fixture_clock") == "1" and bool(clock_identity.get("raw_clock_path")) and clock.get("clock_requested") is True
    # This is a bounded candidate gate, separate from the retained formal status.
    checks = dict(windows_report_paths=native_paths_passed, complete_synthetic_report_preflight=preflight_passed(),
        default_product=default["passed"], dual_clock_product=clock["passed"],
        default_legacy_score_available=legacy["default"]["status"] == "available",
        dual_clock_legacy_score_available=legacy["dual-clock"]["status"] == "available",
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
        checks[scenario + "_actual_cli_report"] = report_matches(item, manifest, scenario, text_path)
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
        legacy_scores=legacy,
        clock_evidence=dict(complete=checks["clock_evidence_complete"],
            missing_evidence=[item if isinstance(item, str) and re.fullmatch(r"[a-z0-9_]+", item) else "private_detail_omitted"
                for item in analysis["missing_evidence"]] if isinstance(analysis.get("missing_evidence"), list) else None),
        formal=dict(status="requirements_unmet", accepted=False),
        validation_platform=platform_summary(),
        actual_cli_reports={key: dict(complete=row.get("complete"), report_generated=row.get("report_generated"),
            acceptance_passed=row.get("acceptance_passed"), consistency=row.get("comparison", {}).get("json_equal"),
            event_id=row.get("event_id"), report_sha256=row.get("report_sha256")) for key, row in reports.items()},
        report_preflights=dict(native_paths_passed=checks["windows_report_paths"],
            synthetic_cli_passed=checks["complete_synthetic_report_preflight"],
            native_paths_summary_sha256=sha(native_path) if native_path and native_path.is_file() else None,
            synthetic_cli_summary_sha256=sha(preflight_directory / "summary.json")
                if preflight_directory and (preflight_directory / "summary.json").is_file() else None),
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
    for action in ("report", "diagnose-report"):
        export = sub.add_parser(action)
        export.add_argument("--package", type=Path, required=True)
        export.add_argument("--capture-directory", type=Path, required=True)
        export.add_argument("--private-output", type=Path, required=True)
        export.add_argument("--public-output", type=Path, required=True)
        export.add_argument("--scenario", choices=("default", "dual-clock", "synthetic"), required=True)
    collect = sub.add_parser("collect-report")
    collect.add_argument("--package", type=Path, required=True)
    collect.add_argument("--scenario", choices=("default", "dual-clock", "synthetic"), required=True)
    collect.add_argument("--public-output", type=Path, required=True)
    finish = sub.add_parser("finalize")
    finish.add_argument("--package", type=Path, required=True)
    finish.add_argument("--default-directory", type=Path, required=True)
    finish.add_argument("--clock-directory", type=Path, required=True)
    finish.add_argument("--clock-analysis", type=Path, required=True)
    finish.add_argument("--quality-summary", type=Path, required=True)
    finish.add_argument("--report-preflight", type=Path, required=True)
    finish.add_argument("--report-paths", type=Path, required=True)
    finish.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.action == "collect-report":
            collect_report(args.package, args.scenario, args.public_output)
            return 0
        return {"prepare": prepare, "report": report, "diagnose-report": diagnose_report, "finalize": finalize}[args.action](args)
    except (OSError, ValueError, KeyError, TypeError, struct.error, UnicodeError, subprocess.SubprocessError) as error:
        # Full errors can contain private paths. CI retains the command's local
        # input/output directory; public stdout only names the failed phase.
        code = str(error) if isinstance(error, ValueError) and re.fullmatch(r"[a-z_]+", str(error)) else "trial_artifact_failed"
        print(json.dumps(dict(protocol=PROTOCOL, phase=args.action, error=code)))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
