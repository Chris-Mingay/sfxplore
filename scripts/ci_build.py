"""Build and verify the host platform. Requires .NET 8, Python, FFmpeg/FFprobe."""
import os
import pathlib
import platform
import shutil
import subprocess
import sys
import urllib.request
import zipfile

from bundle_audio_tools import bundle
from version import synchronize
from release import package

ROOT = pathlib.Path(__file__).resolve().parent.parent
os.chdir(ROOT)
VERSION = "4.7.2-stable"
WINDOWS = sys.platform == "win32"
MAC = sys.platform == "darwin"
ARM = platform.machine().lower() in ("arm64", "aarch64")
RID = "win-x64" if WINDOWS else "osx-arm64" if MAC and ARM else "osx-x64" if MAC else "linux-x64"
SLUG = "win64" if WINDOWS else "macos.universal" if MAC else "linux_x86_64"
FILENAME = f"Godot_v{VERSION}_mono_{SLUG}.zip"


def call(*args):
    subprocess.run([str(a) for a in args], check=True)


def main():
    synchronize(check=True)
    work = ROOT / ".tools"
    work.mkdir(exist_ok=True)
    archive = work / FILENAME
    if not archive.exists():
        urllib.request.urlretrieve(f"https://godot-releases.nbg1.your-objectstorage.com/{VERSION}/{FILENAME}", archive)
    editor = work / "editor"
    editor.mkdir(exist_ok=True)
    with zipfile.ZipFile(archive) as package:
        package.extractall(editor)
    pattern = "*_console.exe" if WINDOWS else "Godot" if MAC else "Godot*_x86_64"
    engine = next(p for p in editor.rglob(pattern) if p.is_file())
    if not WINDOWS:
        engine.chmod(0o755)
    feed = work / "nuget"
    feed.mkdir(exist_ok=True)
    for package in editor.rglob("*.nupkg"):
        shutil.copy2(package, feed / package.name)
    tools = ROOT / "tools" / "ffmpeg" / RID
    bundle(tools)
    os.environ["SOUNDSHELF_AUDIO_TOOLS"] = str(tools)
    os.environ["SOUNDSHELF_DEMO_DIR"] = str(ROOT / "artifacts" / "demo-profile")
    os.environ["MSBuildEnableWorkloadResolver"] = "false"
    (ROOT / "artifacts").mkdir(exist_ok=True)
    (ROOT / "artifacts" / ".gdignore").touch()
    call("dotnet", "build", "SFXplore.csproj")
    call("dotnet", "test", "Tests/SoundShelf.Tests.csproj", "--logger", "trx;LogFileName=results.trx")
    call(engine, "--headless", "--editor", "--import", "--quit", "--path", ROOT)
    call(engine, "--headless", "--path", ROOT, "--", "--smoke-test", "--exercise-tag-filter", "--exercise-zoom", "--exercise-folders", "--exercise-ui", "--exercise-library-export", f"--data-dir={ROOT / 'artifacts' / 'demo-profile'}")
    call(sys.executable, "scripts/fetch_templates.py", "windows" if WINDOWS else "macos" if MAC else "linux")
    output = ROOT / "dist" / RID
    output.mkdir(parents=True, exist_ok=True)
    preset = "Windows" if WINDOWS else "macOS Apple Silicon" if MAC and ARM else "macOS Intel" if MAC else "Linux"
    destination = output / ("SFXplore.exe" if WINDOWS else "SFXplore.zip" if MAC else "SFXplore.x86_64")
    call(engine, "--headless", "--path", ROOT, "--export-release", preset, destination)
    package_root = output
    if MAC:
        with zipfile.ZipFile(destination) as app:
            app.extractall(output)
            for entry in app.infolist():
                mode = entry.external_attr >> 16
                if mode:
                    (output / entry.filename).chmod(mode)
        destination.unlink()
        package_root = next(output.glob("*.app")) / "Contents" / "MacOS"
    shutil.copytree(tools, package_root / "tools" / "ffmpeg" / RID, dirs_exist_ok=True)
    shutil.copy2(ROOT / "THIRD_PARTY_NOTICES.md", output)
    shutil.copy2(ROOT / "README.md", output)
    executable = next(package_root.glob("SFXplore*")) if MAC else destination
    executable.chmod(0o755)
    call(executable, "--headless", "--", "--smoke-test", "--exercise-tag-filter", "--exercise-zoom", "--exercise-folders", "--exercise-ui", "--exercise-library-export", f"--data-dir={ROOT / 'artifacts' / 'demo-profile'}")
    print(f"Verified package: {output}")
    print(f"Release archive: {package(output, RID)}")


if __name__ == "__main__":
    main()
