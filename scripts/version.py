"""Synchronize Godot metadata with VERSION, or check it without writing."""
import argparse
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parent.parent


def read_version(root=ROOT):
    version = (root / "VERSION").read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", version):
        raise ValueError("VERSION must contain a stable major.minor.patch version (e.g. 0.2.0)")
    return version


def synchronize(root=ROOT, check=False):
    version = read_version(root)
    for filename, patterns in {
        "project.godot": [r'^config/version="[^"]*"$'],
        "export_presets.cfg": [r'^application/short_version="[^"]*"$', r'^application/version="[^"]*"$',
                               r'^application/file_version="[^"]*"$', r'^application/product_version="[^"]*"$'],
    }.items():
        path = root / filename
        old = path.read_text(encoding="utf-8")
        new = old
        for pattern in patterns:
            def replace(match):
                key = match[0].split("=")[0]
                stamped = version + ".0" if key in ("application/file_version", "application/product_version") else version
                return key + f'="{stamped}"'
            new = re.sub(pattern, replace, new, flags=re.MULTILINE)
        if filename == "project.godot" and 'config/version=' not in new:
            new = new.replace('[application]\n', f'[application]\n\nconfig/version="{version}"\n', 1)
        if new != old:
            if check:
                raise ValueError(f"{filename} is out of sync; run python scripts/version.py")
            path.write_text(new, encoding="utf-8")
    changelog = (root / "CHANGELOG.md").read_text(encoding="utf-8")
    if f"\n## {version}\n" not in changelog:
        raise ValueError(f"CHANGELOG.md needs a section for {version}")
    return version


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--tag", help="Require this Git tag to match VERSION")
    args = parser.parse_args()
    version = synchronize(check=args.check)
    if args.tag is not None and args.tag != f"v{version}":
        parser.error(f"Tag must be v{version}, got {args.tag}")
    print(version)
