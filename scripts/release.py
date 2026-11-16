"""Package verified builds and validate release assets before publishing."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import tarfile
import tempfile

from version import ROOT, read_version

CHANNELS = {"win-x64": "windows", "linux-x64": "linux", "osx-x64": "mac-intel", "osx-arm64": "mac-arm64"}


def archive_name(version, rid):
    return f"SFXplore-{version}-{rid}.tar.gz"


def checksum(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def package(output, rid):
    version = read_version()
    commit = os.environ.get("GITHUB_SHA") or subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    metadata = {"version": version, "commit": commit, "rid": rid}
    (output / "build-info.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    for name in ("VERSION", "CHANGELOG.md"):
        (output / name).write_bytes((ROOT / name).read_bytes())
    archive = output.parent / archive_name(version, rid)
    with tarfile.open(archive, "w:gz") as tar:
        tar.add(output, arcname="SFXplore")
    return archive


def manifest(directory, tag, commit, run_id):
    version = read_version()
    if tag != f"v{version}":
        raise ValueError("Release tag does not match VERSION")
    assets = {rid: {"file": archive_name(version, rid), "sha256": checksum(directory / archive_name(version, rid))} for rid in CHANNELS}
    data = {"version": version, "tag": tag, "commit": commit, "run_id": int(run_id), "assets": assets}
    (directory / "release-manifest.json").write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def verify(directory, tag, commit, run, repository):
    data = json.loads((directory / "release-manifest.json").read_text(encoding="utf-8"))
    if not re.fullmatch(r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", tag):
        raise ValueError("Expected a stable release tag, such as v0.2.0")
    if data["tag"] != tag or data["version"] != tag[1:] or data["commit"] != commit:
        raise ValueError("Release metadata does not match the requested tag and commit")
    if (run["id"] != data["run_id"] or run["conclusion"] != "success" or run["status"] != "completed"
            or run["event"] != "push" or run["head_sha"] != commit
            or run["path"] != ".github/workflows/build.yml" or run["repository"]["full_name"] != repository):
        raise ValueError("Release must originate from a successful tagged build in this repository")
    if set(data["assets"]) != set(CHANNELS):
        raise ValueError("Release must contain all four desktop builds")
    for rid, asset in data["assets"].items():
        expected = archive_name(data["version"], rid)
        if asset["file"] != expected or checksum(directory / expected) != asset["sha256"]:
            raise ValueError(f"Release archive checksum mismatch: {rid}")
    return data


def publish(directory, data, target):
    if not re.fullmatch(r"[a-z0-9][a-z0-9_-]*/[a-z0-9][a-z0-9_-]*", target):
        raise ValueError("ITCH_TARGET must be a lower-case account/project slug")
    if not os.environ.get("BUTLER_API_KEY"):
        raise ValueError("BUTLER_API_KEY is required")
    with tempfile.TemporaryDirectory() as temporary:
        # Validate every archive before making the first upload.
        packages = []
        for rid, channel in CHANNELS.items():
            destination = pathlib.Path(temporary) / rid
            with tarfile.open(directory / data["assets"][rid]["file"]) as tar:
                tar.extractall(destination, filter="data")
            package_root = destination / "SFXplore"
            info = json.loads((package_root / "build-info.json").read_text(encoding="utf-8"))
            if info != {"version": data["version"], "commit": data["commit"], "rid": rid}:
                raise ValueError(f"Package metadata mismatch: {rid}")
            packages.append((package_root, channel))
        for package_root, channel in packages:
            subprocess.run(["butler", "push", str(package_root), f"{target}:{channel}", "--userversion", data["version"]], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    pack = sub.add_parser("pack"); pack.add_argument("directory", type=pathlib.Path); pack.add_argument("rid", choices=CHANNELS)
    index = sub.add_parser("manifest"); index.add_argument("directory", type=pathlib.Path)
    index.add_argument("--tag", required=True); index.add_argument("--commit", required=True); index.add_argument("--run-id", required=True)
    for command in ("verify", "publish"):
        action = sub.add_parser(command); action.add_argument("directory", type=pathlib.Path)
        action.add_argument("--tag", required=True); action.add_argument("--commit", required=True)
        action.add_argument("--run-info", type=pathlib.Path, required=True); action.add_argument("--repository", required=True)
        if command == "publish": action.add_argument("--target", required=True)
    args = parser.parse_args()
    if args.command == "pack": print(package(args.directory, args.rid))
    elif args.command == "manifest": manifest(args.directory, args.tag, args.commit, args.run_id)
    else:
        data = verify(args.directory, args.tag, args.commit, json.loads(args.run_info.read_text(encoding="utf-8")), args.repository)
        if args.command == "publish": publish(args.directory, data, args.target)
        print(f"Verified release {args.tag} ({args.commit})")


if __name__ == "__main__":
    main()
