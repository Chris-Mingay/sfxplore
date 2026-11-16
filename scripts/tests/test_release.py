import json
import os
import pathlib
import sys
import tarfile
import tempfile
import unittest
import zipfile
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import release
import version


class VersionTests(unittest.TestCase):
    def test_metadata_sync_and_check(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / "VERSION").write_text("0.2.0\n")
            (root / "CHANGELOG.md").write_text("# Changes\n\n## 0.2.0\n\nFeatures\n")
            (root / "project.godot").write_text('[application]\nconfig/name="Test"\n')
            (root / "export_presets.cfg").write_text('application/version="0.1.0"\napplication/file_version="0.1.0.0"\n')
            with self.assertRaises(ValueError): version.synchronize(root, check=True)
            version.synchronize(root)
            self.assertEqual("0.2.0", version.synchronize(root, check=True))
            self.assertIn('application/file_version="0.2.0.0"', (root / "export_presets.cfg").read_text())
            (root / "VERSION").write_text("v0.2.0")
            with self.assertRaises(ValueError): version.read_version(root)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = pathlib.Path(self.temporary.name)
        self.commit = "a" * 40
        self.version = version.read_version()
        self.tag = f"v{self.version}"
        self.run = {"id": 123, "status": "completed", "conclusion": "success", "event": "push",
                    "head_sha": self.commit, "path": ".github/workflows/build.yml", "repository": {"full_name": "owner/repo"}}
        with patch.dict(os.environ, {"GITHUB_SHA": self.commit}):
            for rid in release.CHANNELS:
                output = self.directory / rid; output.mkdir()
                executable = output / ("SFXplore.exe" if rid == "win-x64" else "SFXplore"); executable.write_text("verified binary"); executable.chmod(0o755)
                if rid == "win-x64":
                    tools = output / "tools" / "ffmpeg" / rid; tools.mkdir(parents=True)
                    (tools / "ffmpeg.exe").write_text("audio tool")
                    libraries = output / "data_SFXplore_windows_x86_64"; libraries.mkdir()
                    (libraries / "SFXplore.dll").write_text("app assembly")
                release.package(output, rid)
        release.manifest(self.directory, self.tag, self.commit, 123)

    def verify(self, run=None):
        return release.verify(self.directory, self.tag, self.commit, run or self.run, "owner/repo")

    def test_packages_and_manifest_round_trip(self):
        data = self.verify()
        self.assertEqual(set(release.CHANNELS), set(data["assets"]))
        for rid, asset in data["assets"].items():
            if rid == "win-x64":
                self.assertTrue(asset["file"].endswith(".zip"))
                with zipfile.ZipFile(self.directory / asset["file"]) as archive:
                    self.assertIn("SFXplore/build-info.json", archive.namelist())
                    self.assertIn("SFXplore/VERSION", archive.namelist())
                    self.assertIn("SFXplore/SFXplore.exe", archive.namelist())
                    self.assertIn("SFXplore/data_SFXplore_windows_x86_64/SFXplore.dll", archive.namelist())
                    self.assertIn("SFXplore/tools/ffmpeg/win-x64/ffmpeg.exe", archive.namelist())
            else:
                self.assertTrue(asset["file"].endswith(".tar.gz"))
                with tarfile.open(self.directory / asset["file"]) as archive:
                    self.assertIn("SFXplore/build-info.json", archive.getnames())
                    self.assertIn("SFXplore/VERSION", archive.getnames())
                    if os.name != "nt":
                        self.assertTrue(archive.getmember("SFXplore/SFXplore").mode & 0o111)

    def test_tampering_and_wrong_provenance_are_rejected(self):
        for field, value in (("conclusion", "failure"), ("head_sha", "b" * 40), ("event", "pull_request"),
                             ("id", 456), ("path", "other.yml")):
            with self.subTest(field=field), self.assertRaises(ValueError): self.verify({**self.run, field: value})
        asset = self.directory / release.archive_name(self.version, "win-x64")
        asset.write_bytes(b"altered")
        with self.assertRaises(ValueError): self.verify()

    def test_publish_uses_verified_packages_and_fixed_channels(self):
        data = self.verify()
        with patch.dict(os.environ, {"BUTLER_API_KEY": "test-only"}), patch("release.subprocess.run") as run:
            release.publish(self.directory, data, "owner/sfxplore")
            self.assertEqual(4, run.call_count)
            targets = [call.args[0][3] for call in run.call_args_list]
            self.assertEqual([f"owner/sfxplore:{channel}" for channel in release.CHANNELS.values()], targets)
            self.assertTrue(all(call.args[0][-2:] == ["--userversion", self.version] for call in run.call_args_list))

    def test_invalid_target_and_missing_assets_do_not_upload(self):
        data = self.verify()
        with patch("release.subprocess.run") as run, self.assertRaises(ValueError):
            release.publish(self.directory, data, "")
        run.assert_not_called()
        (self.directory / data["assets"]["linux-x64"]["file"]).unlink()
        with self.assertRaises(FileNotFoundError): self.verify()

    def test_existing_windows_tar_release_remains_publishable(self):
        data = self.verify()
        legacy = self.directory / f"SFXplore-{self.version}-win-x64.tar.gz"
        with tarfile.open(legacy, "w:gz") as archive:
            archive.add(self.directory / "win-x64", arcname="SFXplore")
        data["assets"]["win-x64"] = {"file": legacy.name, "sha256": release.checksum(legacy)}
        (self.directory / "release-manifest.json").write_text(json.dumps(data))
        with patch.dict(os.environ, {"BUTLER_API_KEY": "test-only"}), patch("release.subprocess.run") as run:
            release.publish(self.directory, self.verify(), "owner/sfxplore")
            self.assertEqual(4, run.call_count)

    def test_unsafe_zip_paths_are_rejected_before_any_upload(self):
        data = self.verify()
        archive = self.directory / data["assets"]["win-x64"]["file"]
        with zipfile.ZipFile(archive, "w") as zipped:
            zipped.writestr("../escape.txt", "bad entry")
        with patch.dict(os.environ, {"BUTLER_API_KEY": "test-only"}), patch("release.subprocess.run") as run:
            with self.assertRaisesRegex(ValueError, "Unsafe ZIP entry"):
                release.publish(self.directory, data, "owner/sfxplore")
            run.assert_not_called()

    def test_zip_cannot_replace_a_linux_or_mac_tar_in_manifest(self):
        data = self.verify()
        data["assets"]["linux-x64"]["file"] = f"SFXplore-{self.version}-linux-x64.zip"
        (self.directory / "release-manifest.json").write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, "checksum mismatch: linux-x64"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
