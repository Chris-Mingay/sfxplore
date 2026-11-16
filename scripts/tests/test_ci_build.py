import pathlib
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import ci_build
from bundle_audio_tools import require_encoders


class EngineDiscoveryTests(unittest.TestCase):
    def test_required_audio_encoders_are_checked(self):
        with patch("bundle_audio_tools.run", return_value=" A..... pcm_f32le PCM float\n A..... libvorbis Vorbis\n"):
            require_encoders("ffmpeg")
        with patch("bundle_audio_tools.run", return_value=" A..... vorbis Native Vorbis\n"), self.assertRaisesRegex(RuntimeError, "libvorbis.*ffmpeg-full"):
            require_encoders("ffmpeg")

    def test_official_archive_layouts(self):
        layouts = {
            "linux-x64": "Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64",
            "win-x64": "Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe",
            "osx-x64": "Godot_mono.app/Contents/MacOS/Godot",
            "osx-arm64": "Godot_mono.app/Contents/MacOS/Godot",
        }
        for rid, relative in layouts.items():
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as temporary:
                root = pathlib.Path(temporary); executable = root / relative
                executable.parent.mkdir(parents=True); executable.touch()
                self.assertEqual(executable, ci_build.find_engine(root, rid))

    def test_missing_editor_has_actionable_error(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); (root / "unexpected-file").touch()
            with self.assertRaisesRegex(RuntimeError, "linux-x64.*unexpected-file"):
                ci_build.find_engine(root, "linux-x64")

    def test_main_calls_release_packager_after_archive_extraction(self):
        # Exercise main through editor extraction and SDK enumeration. A ZIP
        # handle or nupkg loop variable must never shadow the release function.
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary); work = root / ".tools"; work.mkdir()
            archive = work / ci_build.FILENAME
            with ci_build.zipfile.ZipFile(archive, "w") as zipped:
                zipped.writestr("GodotSharp/Tools/nupkgs/SDK.nupkg", "sdk")
            engine = root / "engine"; engine.touch()
            (root / "tools" / "ffmpeg" / ci_build.RID).mkdir(parents=True)
            for name in ("README.md", "THIRD_PARTY_NOTICES.md"):
                (root / name).touch()
            license_text = "MIT License\n\nCopyright (c) 2026 Chris Mingay\n"
            (root / "LICENSE").write_text(license_text, encoding="utf-8")
            with patch.object(ci_build, "ROOT", root), patch.object(ci_build, "MAC", False), \
                    patch.object(ci_build, "synchronize"), patch.object(ci_build, "find_engine", return_value=engine), \
                    patch.object(ci_build, "bundle"), patch.object(ci_build, "call"), \
                    patch.object(ci_build, "package_release") as package, patch.dict(ci_build.os.environ):
                # The mocked export must leave an executable for chmod.
                output = root / "dist" / ci_build.RID; output.mkdir(parents=True)
                (output / ("SFXplore.exe" if ci_build.WINDOWS else "SFXplore.x86_64")).touch()
                ci_build.main()
                package.assert_called_once_with(output, ci_build.RID)
                self.assertEqual(license_text, (output / "LICENSE").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
