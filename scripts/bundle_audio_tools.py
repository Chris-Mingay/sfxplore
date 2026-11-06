"""Bundle FFmpeg/FFprobe and non-system shared-library dependencies on the host."""
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys


def run(*args):
    return subprocess.check_output(args, text=True)


def bundle(output):
    output = pathlib.Path(output)
    output.mkdir(parents=True, exist_ok=True)
    records = {}
    binaries = []
    for name in ("ffmpeg", "ffprobe"):
        source = shutil.which(name)
        if not source:
            raise RuntimeError(f"Install {name} first")
        source = pathlib.Path(source).resolve()
        if sys.platform == "win32" and source.stat().st_size < 1024 * 1024:
            # Chocolatey's PATH entries can be shims pointing outside the package.
            installed = pathlib.Path(os.environ.get("ChocolateyInstall", r"C:\ProgramData\chocolatey")) / "lib" / "ffmpeg"
            source = next(p for p in installed.rglob(name + ".exe") if p.stat().st_size > 1024 * 1024)
        destination = output / (name + (".exe" if sys.platform == "win32" else ".bin" if sys.platform.startswith("linux") else ""))
        shutil.copy2(source, destination)
        destination.chmod(0o755)
        binaries.append((source, destination))
        records[name] = {"source": str(source), "version": run(str(source), "-version")}
        for license_name in ("LICENSE", "LICENSE.txt", "COPYING.GPLv3", "COPYING.LGPLv2.1"):
            for parent in (source.parent, source.parent.parent):
                license_file = parent / license_name
                if license_file.is_file():
                    shutil.copy2(license_file, output / license_name)
        if sys.platform.startswith("linux"):
            wrapper = output / name
            wrapper.write_text('#!/bin/sh\nHERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\nexport LD_LIBRARY_PATH="$HERE/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"\nexec "$HERE/' + name + '.bin" "$@"\n')
            wrapper.chmod(0o755)
    if sys.platform.startswith("linux"):
        libdir = output / "lib"
        libdir.mkdir(exist_ok=True)
        for source, _ in binaries:
            for dependency in re.findall(r"=>\s+(/\S+)", run("ldd", str(source))):
                path = pathlib.Path(dependency)
                if re.match(r"lib(c|m|pthread|dl|rt|resolv|util)\.so", path.name):
                    continue
                target = libdir / path.name
                if target.exists() and target.read_bytes() != path.read_bytes():
                    raise RuntimeError(f"Library name collision: {target}")
                shutil.copy2(path.resolve(), target)
        # ldd returns the transitive closure for these executables.
        copyright_dir = pathlib.Path("/usr/share/doc")
        notices = output / "licenses"
        notices.mkdir(exist_ok=True)
        for path in copyright_dir.glob("*/copyright"):
            if path.parent.name.startswith(("ffmpeg", "libav", "libsw", "libvorbis", "libogg")):
                shutil.copy2(path, notices / (path.parent.name + "-copyright"))
    elif sys.platform == "darwin":
        libdir = output / "lib"
        libdir.mkdir(exist_ok=True)
        pending = list(binaries)
        copied = {}
        modified = []
        while pending:
            source, target = pending.pop()
            modified.append(target)
            for line in run("otool", "-L", str(source)).splitlines()[1:]:
                dependency = line.strip().split(" (", 1)[0]
                if dependency.startswith(("/System/", "/usr/lib/")):
                    continue
                path = pathlib.Path(dependency.replace("@loader_path", str(source.parent)))
                if not path.is_absolute():
                    raise RuntimeError(f"Unresolved library reference {dependency} in {source}")
                path = path.resolve()
                if path == source:
                    continue
                dest = libdir / path.name
                if path not in copied:
                    if dest.exists():
                        raise RuntimeError(f"Library name collision: {dest}")
                    shutil.copy2(path, dest)
                    dest.chmod(0o755)
                    copied[path] = dest
                    pending.append((path, dest))
                reference = "@loader_path/" + ("lib/" if target.parent == output else "") + dest.name
                subprocess.run(["install_name_tool", "-change", dependency, reference, str(target)], check=True)
            if target.parent == libdir:
                subprocess.run(["install_name_tool", "-id", "@loader_path/" + target.name, str(target)], check=True)
        for target in modified:
            subprocess.run(["codesign", "--force", "--sign", "-", str(target)], check=True)
    records["sha256"] = {str(path.relative_to(output)): hashlib.file_digest(path.open("rb"), "sha256").hexdigest() for path in output.rglob("*") if path.is_file()}
    (output / "BUILD-INFO.json").write_text(json.dumps(records, indent=2))


if __name__ == "__main__":
    bundle(sys.argv[1])
