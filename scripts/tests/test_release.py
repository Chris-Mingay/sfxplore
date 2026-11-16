import json
import os
import pathlib
import sys
import tarfile
import tempfile
import unittest
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
                executable = output / "SFXplore"; executable.write_text("verified binary"); executable.chmod(0o755)
                release.package(output, rid)
        release.manifest(self.directory, self.tag, self.commit, 123)

    def verify(self, run=None):
        return release.verify(self.directory, self.tag, self.commit, run or self.run, "owner/repo")

    def test_packages_and_manifest_round_trip(self):
        data = self.verify()
        self.assertEqual(set(release.CHANNELS), set(data["assets"]))
        for asset in data["assets"].values():
            with tarfile.open(self.directory / asset["file"]) as archive:
                self.assertIn("SFXplore/build-info.json", archive.getnames())
                self.assertIn("SFXplore/VERSION", archive.getnames())

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


if __name__ == "__main__":
    unittest.main()
