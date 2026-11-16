import pathlib
import io
import json
import struct
import sys
import tempfile
import unittest
import zipfile
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from macos_templates import CPU_TYPES, thin_macho, prepare_template
import fetch_templates


def thin(cpu):
    return struct.pack("<8I", 0xFEEDFACF, cpu, 0, 2, 0, 0, 0, 0)


def universal(endian=">", wide=False):
    slices = [thin(cpu) for cpu in CPU_TYPES.values()]
    output = bytearray(288)
    struct.pack_into(endian + "II", output, 0, 0xCAFEBABF if wide else 0xCAFEBABE, 2)
    for index, (cpu, data) in enumerate(zip(CPU_TYPES.values(), slices)):
        offset = 128 + index * 128
        if wide:
            struct.pack_into(endian + "IIQQII", output, 8 + index * 32, cpu, 0, offset, len(data), 0, 0)
        else:
            struct.pack_into(endian + "IIIII", output, 8 + index * 20, cpu, 0, offset, len(data), 0)
        output[offset:offset + len(data)] = data
    return bytes(output)


class MacTemplateTests(unittest.TestCase):
    def test_thins_both_architectures_and_fat_header_formats(self):
        for endian in (">", "<"):
            for wide in (False, True):
                for architecture, cpu in CPU_TYPES.items():
                    with self.subTest(endian=endian, wide=wide, architecture=architecture):
                        self.assertEqual(thin(cpu), thin_macho(universal(endian, wide), architecture))

    def test_rejects_missing_architecture_and_malformed_slices(self):
        with self.assertRaisesRegex(ValueError, "does not contain"):
            thin_macho(thin(CPU_TYPES["arm64"]), "x86_64")
        with self.assertRaisesRegex(ValueError, "bounds"):
            thin_macho(universal()[:150], "x86_64")
        with self.assertRaisesRegex(ValueError, "header"):
            thin_macho(b"bad", "x86_64")

    def test_rewrites_template_names_and_preserves_bundle_metadata(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); original = root / "macos.zip"
            prefix = "macos_template.app/Contents/"
            with zipfile.ZipFile(original, "w") as archive:
                for mode in ("release", "debug"):
                    info = zipfile.ZipInfo(prefix + f"MacOS/godot_macos_{mode}.universal")
                    info.create_system = 3; info.external_attr = 0o100755 << 16
                    archive.writestr(info, universal())
                archive.writestr(prefix + "Info.plist", b"original metadata")
            for architecture, cpu in CPU_TYPES.items():
                destination = root / f"macos-{architecture}.zip"
                prepare_template(original, destination, architecture)
                with zipfile.ZipFile(destination) as archive:
                    for mode in ("release", "debug"):
                        name = prefix + f"MacOS/godot_macos_{mode}.{architecture}"
                        self.assertEqual(thin(cpu), archive.read(name))
                        self.assertEqual(0o100755, archive.getinfo(name).external_attr >> 16)
                    self.assertEqual(b"original metadata", archive.read(prefix + "Info.plist"))
                    self.assertFalse(any(name.endswith(".universal") for name in archive.namelist()))

    def test_invalid_template_does_not_replace_previous_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); original = root / "source.zip"; destination = root / "macos-arm64.zip"
            destination.write_bytes(b"previous template")
            with zipfile.ZipFile(original, "w") as archive: archive.writestr("Info.plist", "metadata")
            with self.assertRaisesRegex(ValueError, "Missing Mac template"):
                prepare_template(original, destination, "arm64")
            self.assertEqual(b"previous template", destination.read_bytes())
            self.assertFalse(destination.with_suffix(".zip.tmp").exists())

    def test_fetcher_generates_and_records_both_architecture_templates(self):
        inner = io.BytesIO()
        with zipfile.ZipFile(inner, "w") as archive:
            for mode in ("release", "debug"):
                archive.writestr(f"macos_template.app/Contents/MacOS/godot_macos_{mode}.universal", universal())
        outer = io.BytesIO()
        with zipfile.ZipFile(outer, "w") as archive:
            archive.writestr("templates/macos.zip", inner.getvalue())
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / "dependencies.json").write_text('{"godot":{}}')
            with patch.object(fetch_templates, "ROOT", root), patch.object(fetch_templates, "RemoteZip", return_value=io.BytesIO(outer.getvalue())), patch.object(sys, "argv", ["fetch_templates.py", "macos"]):
                fetch_templates.main()
            output = root / ".tools" / "templates"
            manifest = json.loads((output / "macos-checksums.json").read_text())
            self.assertEqual({"macos.zip", "macos-x86_64.zip", "macos-arm64.zip"}, set(manifest["sha256"]))
            self.assertEqual(inner.getvalue(), (output / "macos.zip").read_bytes())


if __name__ == "__main__":
    unittest.main()
