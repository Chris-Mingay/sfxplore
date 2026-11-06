"""Extract selected official .NET templates using HTTP ranges (no 1.2 GB download).

Usage: python scripts/fetch_templates.py windows|linux|macos
The ZIP reader verifies each extracted entry's CRC. Only HTTPS official Godot
storage is used; extracted files are hashed for the build's dependency manifest.
"""
import hashlib
import io
import json
import pathlib
import shutil
import sys
import urllib.request
import zipfile

URL = "https://godot-releases.nbg1.your-objectstorage.com/4.7.2-stable/Godot_v4.7.2-stable_mono_export_templates.tpz"
ROOT = pathlib.Path(__file__).resolve().parent.parent


class RemoteZip(io.RawIOBase):
    def __init__(self):
        with urllib.request.urlopen(urllib.request.Request(URL, method="HEAD"), timeout=60) as response:
            self.length = int(response.headers["Content-Length"])
        self.position = 0
        self.cached_start = -1
        self.cached = b""

    def seekable(self):
        return True

    def readable(self):
        return True

    def tell(self):
        return self.position

    def seek(self, offset, whence=0):
        self.position = offset if whence == 0 else self.position + offset if whence == 1 else self.length + offset
        return self.position

    def read(self, size=-1):
        size = self.length - self.position if size < 0 else min(size, self.length - self.position)
        if size <= 0:
            return b""
        result = bytearray()
        while len(result) < size:
            if not self.cached_start <= self.position < self.cached_start + len(self.cached):
                start = self.position
                end = min(self.length - 1, start + 4 * 1024 * 1024 - 1)
                request = urllib.request.Request(URL, headers={"Range": f"bytes={start}-{end}"})
                with urllib.request.urlopen(request, timeout=90) as response:
                    if response.status != 206:
                        raise RuntimeError("Server did not honor the range request; refusing a full archive download")
                    self.cached = response.read(end - start + 2)
                    if len(self.cached) != end - start + 1:
                        raise RuntimeError("Incomplete template download")
                self.cached_start = start
            begin = self.position - self.cached_start
            count = min(size - len(result), len(self.cached) - begin)
            result += self.cached[begin:begin + count]
            self.position += count
        return bytes(result)


def main():
    platform = sys.argv[1] if len(sys.argv) > 1 else "windows"
    targets = {
        "windows": ["windows_release_x86_64.exe", "windows_debug_x86_64.exe"],
        "linux": ["linux_release.x86_64", "linux_debug.x86_64"],
        "macos": ["macos.zip"],
    }[platform]
    output = ROOT / ".tools" / "templates"
    output.mkdir(parents=True, exist_ok=True)
    manifest = {}
    with zipfile.ZipFile(RemoteZip()) as archive:
        for target in targets:
            entry = next((e for e in archive.infolist() if pathlib.PurePosixPath(e.filename).name == target), None)
            if entry is None:
                raise RuntimeError(f"Missing {target}; available entries: {archive.namelist()}")
            path = output / target
            print(f"Extracting {target} ({entry.compress_size / 1048576:.1f} MiB transfer)", flush=True)
            with archive.open(entry) as source, path.open("wb") as destination:
                shutil.copyfileobj(source, destination, 1024 * 1024)
            manifest[target] = hashlib.file_digest(path.open("rb"), "sha256").hexdigest()
            expected = json.loads((ROOT / "dependencies.json").read_text())["godot"].get(target)
            if expected and expected != manifest[target]:
                path.unlink()
                raise RuntimeError(f"Checksum mismatch for {target}")
            if platform == "linux":
                path.chmod(0o755)
    (output / f"{platform}-checksums.json").write_text(json.dumps({"source": URL, "sha256": manifest}, indent=2))
    print("Templates ready.", flush=True)


if __name__ == "__main__":
    main()
