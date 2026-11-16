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


def require_encoders(executable):
    listing = run(str(executable), "-hide_banner", "-encoders")
    encoders = {parts[1] for line in listing.splitlines() if len(parts := line.split()) >= 2 and parts[0].startswith("A")}
    missing = {"pcm_f32le", "libvorbis"} - encoders
    if missing:
        raise RuntimeError(f"FFmpeg is missing required encoders: {', '.join(sorted(missing))}. On macOS install ffmpeg-full and put its bin directory first on PATH.")


def macho_load_commands(source):
    """Read search paths and the library's own ID (which is not a dependency)."""
    command = None
    rpaths, identity = [], None
    for line in run("otool", "-l", str(source)).splitlines():
        line = line.strip()
        if line.startswith("cmd "):
            command = line[4:]
        elif command == "LC_RPATH" and line.startswith("path "):
            rpaths.append(line[5:].rsplit(" (offset ", 1)[0])
        elif command == "LC_ID_DYLIB" and line.startswith("name "):
            identity = line[5:].rsplit(" (offset ", 1)[0]
    return rpaths, identity


def expand_macho_path(reference, source, executable):
    for token, directory in (("@loader_path", source.parent), ("@executable_path", executable.parent)):
        if reference == token or reference.startswith(token + "/"):
            return directory / reference[len(token):].lstrip("/")
    if reference.startswith("@"):
        raise RuntimeError(f"Unsupported library path {reference} in {source}")
    path = pathlib.Path(reference)
    if not path.is_absolute():
        raise RuntimeError(f"Non-absolute library path {reference} in {source}")
    return path


def macho_search_paths(rpaths, source, executable, inherited):
    paths = []
    for reference in rpaths:
        if reference.startswith("@rpath/"):
            paths.extend(parent / reference[len("@rpath/"):] for parent in inherited)
        else:
            paths.append(expand_macho_path(reference, source, executable))
    # dyld searches the current loader and then the chain that loaded it.
    return tuple(dict.fromkeys(path.resolve() for path in (*paths, *inherited)))


def resolve_macho_dependency(reference, source, executable, search_paths):
    if reference.startswith("@rpath/"):
        candidates = [directory / reference[len("@rpath/"):] for directory in search_paths]
    else:
        candidates = [expand_macho_path(reference, source, executable)]
    for candidate in candidates:
        if candidate.is_file():
            return candidate.resolve()
    raise RuntimeError(f"Unresolved library reference {reference} in {source}. Searched: {[str(p) for p in candidates]}")


def bundle_macos(binaries, output):
    libdir = output / "lib"
    libdir.mkdir(exist_ok=True)
    # Keep the original executable and loader search stack for transitive @rpath references.
    pending = [(source, target, source, ()) for source, target in binaries]
    copied, modified = {}, []
    while pending:
        source, target, executable, inherited = pending.pop()
        modified.append(target)
        rpaths, identity = macho_load_commands(source)
        search_paths = macho_search_paths(rpaths, source, executable, inherited)
        for line in run("otool", "-L", str(source)).splitlines()[1:]:
            if not line[:1].isspace():
                continue  # Ignore additional architecture headings in universal binaries.
            dependency = line.strip().split(" (", 1)[0]
            if dependency == identity or dependency.startswith(("/System/", "/usr/lib/")):
                continue
            path = resolve_macho_dependency(dependency, source, executable, search_paths)
            if path == source or str(path).startswith(("/System/", "/usr/lib/")):
                continue
            if path not in copied:
                dest = libdir / path.name
                if dest.exists():
                    raise RuntimeError(f"Library name collision: {dest}")
                shutil.copy2(path, dest)
                dest.chmod(0o755)
                copied[path] = dest
                pending.append((path, dest, executable, search_paths))
            dest = copied[path]
            reference = "@loader_path/" + ("lib/" if target.parent == output else "") + dest.name
            subprocess.run(["install_name_tool", "-change", dependency, reference, str(target)], check=True)
        if target.parent == libdir:
            subprocess.run(["install_name_tool", "-id", "@loader_path/" + target.name, str(target)], check=True)
    for target in modified:
        subprocess.run(["codesign", "--force", "--sign", "-", str(target)], check=True)


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
        if name == "ffmpeg":
            require_encoders(source)
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
        bundle_macos(binaries, output)
    records["sha256"] = {str(path.relative_to(output)): hashlib.file_digest(path.open("rb"), "sha256").hexdigest() for path in output.rglob("*") if path.is_file()}
    (output / "BUILD-INFO.json").write_text(json.dumps(records, indent=2))


if __name__ == "__main__":
    bundle(sys.argv[1])
