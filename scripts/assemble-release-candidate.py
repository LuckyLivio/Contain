"""Assemble release assets on Windows from the two frozen packages built on A."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import tomllib
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[1]
CLI = "contain-workflow.exe"
SETUP = "Contain-Setup-x64.exe"
SELECTORS = ("select-workflow-toolchain.ps1", "workflow-toolchain.ps1")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def validate_handoff(handoff, source):
    fixed = {"schema_version": 1, "source_sha": source, "native_acceptance": "not_run",
             "clean_snapshot": "not_asserted_by_build"}
    require(handoff == {**fixed, "desktop": handoff["desktop"], "cli": handoff["cli"]}, "unexpected A handoff metadata")
    for kind in ("desktop", "cli"):
        identity = handoff[kind]
        tools = identity["toolchain"]
        versions = {key: tools[key] for key in ("msvc", "sdk")}
        require(all(re.fullmatch(r"\d+(?:\.\d+)+", v) for v in versions.values()), "invalid toolchain version")
        fields = {"schema_version": 1, "source_sha": source, "toolchain": versions}
        hashes = ["entry_sha256", "zip_sha256", "spec_sha256"]
        if kind == "desktop":
            fields.update(native_acceptance="not_run", toolchain={**versions, "status": "passed"})
            hashes += ["core_sha256", "workflow_cli_sha256"]
        else:
            fields.update(acceptance="not_run", crt="static requested; clean-machine execution still requires validation")
        for key in hashes:
            require(re.fullmatch("[0-9a-f]{64}", identity[key]), f"{kind}: invalid {key}")
            fields[key] = identity[key]
        require(fields == identity, f"{kind}: unexpected package identity metadata")


def check_packaged_probes(cli, version, work):
    require(subprocess.check_output([cli, "--version"], text=True).strip() == f"contain-workflow {version}",
            "packaged workflow CLI version mismatch")
    git_version = subprocess.check_output(["git", "--version"], text=True).split()[2]
    manifest = {
        "schema_version": 1, "target": "windows-x64", "required_variables": [], "build": [], "verify": [],
        "tools": [
            {"program": program, "version": expected, "version_args": ["--version"], "remedy": "check version"}
            for program, expected in [("git", git_version), (str(cli), version)]
        ],
    }
    (work / "contain.environment.json").write_text(json.dumps(manifest), encoding="utf-8")
    for mode, flags in [("default", []), ("safe-probes", ["--safe-probes"])]:
        report = work / mode
        result = subprocess.run([cli, "check", *flags, "--project", work, "--report", report])
        require(result.returncode == 2, f"{mode}: expected confirmation exit 2, got {result.returncode}")
        checks = {row["name"]: row for row in read_json(report / "report.json")["checks"]}
        require(checks["platform"]["status"] == "passed", f"{mode}: expected native Windows x64")
        git, custom = checks["tool:git"], checks[f"tool:{cli}"]
        require(git["status"] == "passed" and git["observed"] == git_version, f"{mode}: git version mismatch")
        require(custom["status"] == "needs_confirmation" and custom["outcome"] == "unsupported_probe",
                f"{mode}: custom probe was not skipped")
    return {"default": "passed", "safe_probes": "passed", "git_version_observed": git_version}


def stage_package(kind, source, version, delivery, handoff, assets, work):
    desktop = kind == "desktop"
    directory = delivery / f"{kind}-delivery"
    input_name = "contain-source-windows-x64.zip" if desktop else "contain-workflow-windows-x64.zip"
    target_prefix = "contain" if desktop else "contain-workflow"
    archive = assets / f"{target_prefix}-{version}-windows-x64.zip"
    spec = assets / f"{kind}-package-spec.json"
    identity_file = assets / f"{kind}-identity.json"
    for original, staged in [(input_name, archive), ("package-spec.json", spec), ("identity.json", identity_file)]:
        shutil.copyfile(directory / original, staged)
    identity = read_json(identity_file)
    require(identity == handoff[kind] and identity["source_sha"] == source, f"{kind}: handoff identity mismatch")
    contract = read_json(spec)
    require(digest(archive) == identity["zip_sha256"] == contract["sha256"] and digest(spec) == identity["spec_sha256"],
            f"{kind}: ZIP or spec hash mismatch")
    work.mkdir()
    cli = work / CLI
    with ZipFile(archive) as z:
        if desktop:
            build = json.loads(z.read("BUILD.json"))
            require(build["workflow_cli_sha256"] == identity["workflow_cli_sha256"] and
                    build["source_sha"] == source and build["version"] == version and
                    build["workflow_cli_version"] == version, "desktop: BUILD identity mismatch")
        cli.write_bytes(z.read(CLI))
        for selector in SELECTORS:
            entry = f"workflow/{selector}"
            require(entry in contract["required_files"] and z.read(entry) == (ROOT / "scripts" / selector).read_bytes(),
                    f"{kind}: missing or changed {entry}")
    cli_hash = identity["workflow_cli_sha256" if desktop else "entry_sha256"]
    require(digest(cli) == cli_hash, f"{kind}: workflow CLI hash mismatch")
    return archive, spec, identity_file, check_packaged_probes(cli, version, work)


def validate_installer(receipt, source, version, setup, desktop, spec):
    expected = {
        "schema_version": 1, "protocol": "contain-windows-installer-receipt-1", "source_sha": source,
        "installer_file": SETUP, "installer_bytes": setup.stat().st_size, "installer_sha256": digest(setup),
        "source_zip_sha256": digest(desktop), "source_spec_sha256": digest(spec),
        "app_name": "Contain", "app_version": version, "app_id": "{8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}",
        "upgrade_rehearsal_only": False, "install_scope": "current_user_lowest_privilege",
        "default_install_dir": r"%LOCALAPPDATA%\Programs\Contain",
    }
    require(re.fullmatch(r"\d+(?:\.\d+){3}", receipt["version_info_version"]) and
            re.fullmatch(r"(?:\d+(?:\.\d+)+|6\.x)", receipt["inno_setup_version"]), "invalid installer version metadata")
    require(receipt["authenticode_status"] in ("Valid", "NotSigned", "UnknownError", "HashMismatch", "NotTrusted",
                                               "NotSupportedFileFormat", "Incompatible") and
            receipt["signed"] is (receipt["authenticode_status"] == "Valid"), "invalid Authenticode status")
    require(type(receipt["payload_file_count"]) is int and receipt["payload_file_count"] > 0, "invalid payload count")
    for key in ("version_info_version", "inno_setup_version", "authenticode_status", "signed", "payload_file_count"):
        expected[key] = receipt[key]
    require(receipt == expected, "installer receipt mismatch or unexpected fields")


def assemble(source, delivery, output):
    require(os.name == "nt", "assemble on native Windows x64")
    require(re.fullmatch("[0-9a-f]{40}", source), "expected the full lowercase source commit SHA")
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    require(head == source and not subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT),
            "expected a clean checkout at the requested source SHA")
    require(not os.path.lexists(output), "use a new candidate output directory")
    delivery, output = delivery.resolve(), output.resolve()
    require(not output.is_relative_to(delivery), "preserve A delivery and assemble in a separate directory")
    handoff_bytes = (delivery / "handoff.json").read_bytes()
    handoff = json.loads(handoff_bytes)
    validate_handoff(handoff, source)
    with (ROOT / "crates/contain-workflow/Cargo.toml").open("rb") as cargo:
        version = tomllib.load(cargo)["package"]["version"]
    output.parent.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="contain-candidate-", dir=output.parent))
    print(f"Assembly working directory (retained on failure): {work}", flush=True)
    assets = work / "assets"
    assets.mkdir()
    (assets / "handoff.json").write_bytes(handoff_bytes)
    desktop, spec, identity, desktop_probe = stage_package("desktop", source, version, delivery, handoff, assets, work / "desktop")
    _, _, _, cli_probe = stage_package("cli", source, version, delivery, handoff, assets, work / "cli")
    installer = work / "installer"
    subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-File", ROOT / "scripts/build-windows-installer.ps1",
                    "-Zip", desktop, "-Spec", spec, "-Identity", identity, "-ExpectedZipSha256", handoff["desktop"]["zip_sha256"],
                    "-OutputDirectory", installer], check=True)
    shutil.copyfile(installer / SETUP, assets / SETUP)
    receipt = read_json(installer / "installer-receipt.json")
    validate_installer(receipt, source, version, assets / SETUP, desktop, spec)
    shutil.copyfile(installer / "installer-receipt.json", assets / "installer-receipt.json")
    (assets / "candidate-identity.json").write_text(json.dumps({
        "schema_version": 1, "source_sha": source, "version": version,
        "a_handoff_sha256": hashlib.sha256(handoff_bytes).hexdigest(),
        "packaged_cli_probes": {"desktop": desktop_probe, "cli": cli_probe},
    }, indent=2) + "\n", encoding="utf-8")
    sums = "".join(f"{digest(path)}  {path.name}\n" for path in sorted(assets.iterdir()))
    (assets / "SHA256SUMS.txt").write_text(sums, encoding="utf-8", newline="\n")
    assets.rename(output)
    shutil.rmtree(work)
    print(f"Assembled candidate {source} in {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--delivery", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    assemble(args.source_sha, args.delivery, args.output)
