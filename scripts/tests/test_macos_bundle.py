import pathlib
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import bundle_audio_tools as audio


def commands(rpaths=(), identity=None):
    text = ""
    if identity:
        text += f"Load command 0\n cmd LC_ID_DYLIB\n cmdsize 64\n name {identity} (offset 24)\n"
    for index, path in enumerate(rpaths, 1):
        text += f"Load command {index}\n cmd LC_RPATH\n cmdsize 64\n path {path} (offset 12)\n"
    return text


class MacBundleTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name).resolve()
        self.executable = self.file("ffmpeg/bin/ffmpeg")

    def file(self, name):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(name)
        return path

    def test_loader_rpath_resolves_reported_webp_dependency(self):
        source = self.file("webp/1.6.0/lib/libwebp.7.2.0.dylib")
        dependency = self.file("webp/1.6.0/lib/libsharpyuv.0.dylib")
        with patch.object(audio, "run", return_value=commands(["@loader_path"])):
            paths, identity = audio.macho_load_commands(source)
        search = audio.macho_search_paths(paths, source, self.executable, ())
        self.assertEqual(dependency, audio.resolve_macho_dependency("@rpath/libsharpyuv.0.dylib", source, self.executable, search))
        self.assertIsNone(identity)

    def test_inherited_executable_rpaths_and_paths_with_spaces(self):
        source = self.file("cellar/webp/libwebp.dylib")
        dependency = self.file("ffmpeg/lib with spaces/libsharpyuv.0.dylib")
        inherited = audio.macho_search_paths(["@executable_path/../lib with spaces"], self.executable, self.executable, ())
        search = audio.macho_search_paths([], source, self.executable, inherited)
        self.assertEqual(dependency, audio.resolve_macho_dependency("@rpath/libsharpyuv.0.dylib", source, self.executable, search))
        self.assertEqual(dependency, audio.resolve_macho_dependency("@executable_path/../lib with spaces/libsharpyuv.0.dylib", source, self.executable, ()))

    def test_missing_dependency_reports_candidates(self):
        with self.assertRaisesRegex(RuntimeError, "missing.dylib.*Searched:"):
            audio.resolve_macho_dependency("@rpath/missing.dylib", self.executable, self.executable, (self.root,))

    def test_transitive_bundle_rewrites_signs_and_deduplicates_cycles(self):
        output = self.root / "bundle"; output.mkdir()
        target = output / "ffmpeg"; shutil.copy2(self.executable, target)
        webp = self.file("webp/lib/libwebp.7.dylib")
        sharp = self.file("webp/lib/libsharpyuv.0.dylib")
        # Only the executable supplies the @rpath directory; both dylibs inherit it.
        headers = {self.executable: commands([str(webp.parent)]),
                   webp: commands(identity="@rpath/libwebp.7.dylib"),
                   sharp: commands(identity="@rpath/libsharpyuv.0.dylib")}
        dependencies = {self.executable: [str(webp), "/usr/lib/libSystem.B.dylib"],
                        webp: ["@rpath/libwebp.7.dylib", "@rpath/libsharpyuv.0.dylib"],
                        sharp: ["@rpath/libsharpyuv.0.dylib", "@rpath/libwebp.7.dylib"]}
        def otool(tool, option, path):
            self.assertEqual("otool", tool)
            source = pathlib.Path(path)
            if option == "-l": return headers[source]
            return str(source) + ":\n" + "".join(f"\t{name} (compatibility version 1.0.0)\n" for name in dependencies[source])
        with patch.object(audio, "run", side_effect=otool), patch.object(audio.subprocess, "run") as invoke:
            audio.bundle_macos([(self.executable, target)], output)
        self.assertEqual(webp.read_bytes(), (output / "lib" / webp.name).read_bytes())
        self.assertEqual(sharp.read_bytes(), (output / "lib" / sharp.name).read_bytes())
        calls = [call.args[0] for call in invoke.call_args_list]
        self.assertIn(["install_name_tool", "-change", "@rpath/libsharpyuv.0.dylib", "@loader_path/libsharpyuv.0.dylib", str(output / "lib" / webp.name)], calls)
        self.assertIn(["install_name_tool", "-change", str(webp), "@loader_path/lib/" + webp.name, str(target)], calls)
        self.assertEqual(3, sum(call[0] == "codesign" for call in calls))
        # No mutation tools may operate on the Homebrew source files.
        self.assertTrue(all(pathlib.Path(call[-1]).is_relative_to(output) for call in calls))

    def test_name_collisions_remain_errors(self):
        first = self.file("first/same.dylib"); second = self.file("second/same.dylib")
        output = self.root / "bundle"; output.mkdir(); target = output / "ffmpeg"; target.touch()
        def otool(tool, option, path):
            if option == "-l": return commands()
            return str(path) + f":\n\t{first} (compatibility version 1.0.0)\n\t{second} (compatibility version 1.0.0)\n"
        with patch.object(audio, "run", side_effect=otool), patch.object(audio.subprocess, "run"), self.assertRaisesRegex(RuntimeError, "Library name collision"):
            audio.bundle_macos([(self.executable, target)], output)


if __name__ == "__main__":
    unittest.main()
