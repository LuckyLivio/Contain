"""Deliver one already accepted immutable Beta; never build, replace or publish."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

REPO = "LuckyLivio/Contain"
TAG = "v0.3.2-beta.5"
SOURCE = "2a788cf4705234d1cca472ee78709cfa9062366b"
RUN = 42144562866
RELEASE = 435988076
ZIP = "contain-0.3.2-beta.5-windows-x64.zip"
ZIP_SHA = "2403e582104e10b5ceedd8053fe3e9690fbc969ec827daaffbbbfe1a0abe2c5e"
IDENTITY_SHA = "65f61db92d4bd010ea108a63372af2b761391d3a6cb644e2f1976601af77d891"
SUMS_SHA = "2f026ddbf46bb8a6ddfd2d7b73c48d98ad8a1c20475639de7c80d8f766a9339a"
ARTIFACTS = {13469922353: "contain-windows-x64-viewer", 13469812538: "windows-read-only-quality", 13469757684: "windows-viewer-evidence"}


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", f"repos/{REPO}/{path}"], text=True))


def digest(path):
    require(path.is_file() and not path.is_symlink(), "Expected an ordinary immutable file")
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def draft():
    value = api(f"releases/{RELEASE}")
    require(value["id"] == RELEASE and value["tag_name"] == TAG and value["target_commitish"] == SOURCE
            and value["draft"] is True and value["prerelease"] is True,
            "Only the exact existing Beta 5 draft may receive assets")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--upload", action="store_true")
    args = parser.parse_args()
    delivery = Path(__file__).resolve().parents[1] / "docs/delivery" / TAG
    files = {
        ZIP: args.input / ARTIFACTS[13469922353] / ZIP,
        "BUILD-IDENTITY.json": delivery / "BUILD-IDENTITY.json",
        "SHA256SUMS.txt": delivery / "SHA256SUMS.txt",
        "QUALITY-SUMMARY.json": args.input / ARTIFACTS[13469812538] / "summary.json",
        "search-packaged-second-page.png": args.input / ARTIFACTS[13469757684] / "screenshots/search-packaged-second-page.png",
    }
    require(digest(files["BUILD-IDENTITY.json"]) == IDENTITY_SHA
            and digest(files["SHA256SUMS.txt"]) == SUMS_SHA, "Reviewed delivery documents differ")
    expected = dict(line.split("  ", 1)[::-1] for line in files["SHA256SUMS.txt"].read_text().splitlines())
    require(set(expected) == set(files) - {"SHA256SUMS.txt"} and expected[ZIP] == ZIP_SHA, "Reviewed asset manifest differs")
    require(all(digest(files[name]) == value for name, value in expected.items()), "Accepted asset hash differs")
    identity = json.loads(files["BUILD-IDENTITY.json"].read_text())
    require(identity["source_sha"] == SOURCE and identity["artifact"]["id"] == 13469922353
            and identity["artifact"]["run_id"] == RUN and identity["release_tag"] == TAG, "Build identity differs")
    with zipfile.ZipFile(files[ZIP]) as archive:
        require(archive.testzip() is None, "Unreadable accepted ZIP")
        build_bytes, validation_bytes = archive.read("BUILD.json"), archive.read("VALIDATION.json")
        build, validation = json.loads(build_bytes), json.loads(validation_bytes)
        require(build["source_sha"] == validation["source_sha"] == SOURCE and build["version"] == "0.3.2-beta.5"
                and validation["viewer_verified"] is True and all(validation["checks"].values())
                and hashlib.sha256(build_bytes).hexdigest() == identity["build_manifest_sha256"]
                and hashlib.sha256(validation_bytes).hexdigest() == identity["validation_manifest_sha256"], "Frozen manifests differ")
    ref = api(f"git/ref/tags/{TAG}")["object"]
    for _ in range(8):
        if ref["type"] != "tag":
            break
        ref = api(f"git/tags/{ref['sha']}")["object"]
    require(ref["type"] == "commit" and ref["sha"] == SOURCE, "Immutable tag differs")
    run = api(f"actions/runs/{RUN}")
    require(run["head_sha"] == SOURCE and run["event"] == "push" and run["status"] == "completed"
            and run["conclusion"] == "success" and run["run_attempt"] == 1
            and run["path"] == ".github/workflows/ci.yml", "Accepted CI identity differs")
    for artifact_id, name in ARTIFACTS.items():
        artifact = api(f"actions/artifacts/{artifact_id}")
        require(artifact["name"] == name and artifact["expired"] is False
                and artifact["workflow_run"]["id"] == RUN, "Accepted artifact identity differs")
    require(not {a["name"] for a in draft()["assets"]} - set(files), "Unexpected draft assets")
    if not args.upload:
        print("Verified exact Beta 5 draft, tag, CI artifacts and all local asset hashes; no upload requested.")
        return
    with tempfile.TemporaryDirectory(prefix="contain-beta5-upload-") as temporary:
        staged = Path(temporary) / "upload"
        staged.mkdir()
        readback = Path(temporary) / "readback"
        readback.mkdir()
        for name, source in files.items():
            current = draft()
            if name not in {a["name"] for a in current["assets"]}:
                # No --clobber: an existing asset is downloaded and compared below.
                shutil.copyfile(source, staged / name)
                subprocess.check_call(["gh", "release", "upload", TAG, str(staged / name), "--repo", REPO])
            destination = readback / name
            subprocess.check_call(["gh", "release", "download", TAG, "--pattern", name, "--dir", str(readback), "--repo", REPO])
            require(digest(destination) == digest(source), "Uploaded asset readback differs")
        require({a["name"] for a in draft()["assets"]} == set(files), "Final asset set differs")
    print("Five exact assets uploaded/read back; release remains a draft for final publication.")


if __name__ == "__main__":
    main()
