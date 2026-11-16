# Third-party components

SFXplore's own code is licensed under the MIT License in `LICENSE`, copyright (c) 2026 Chris Mingay. The third-party components below retain their respective licenses.

- **Godot Engine 4.7.2**, MIT license. Engine and C# SDK: https://godotengine.org/license/ and https://github.com/godotengine/godot/tree/4.7.2-stable . Godot contains additional third-party components listed at https://github.com/godotengine/godot/blob/4.7.2-stable/COPYRIGHT.txt . Export templates come from Godot's official HTTPS release storage; `scripts/fetch_templates.py` records extracted checksums.
- **Microsoft.Data.Sqlite.Core 8.0.17**, MIT license, https://github.com/dotnet/efcore .
- **SQLitePCLRaw.bundle_e_sqlite3 2.1.11** and associated provider/core packages, Apache-2.0 license, https://github.com/ericsink/SQLitePCL.raw .
- **SQLite**, public domain, https://sqlite.org/copyright.html .
- **FFmpeg and FFprobe** are separate bundled executables. The local Windows build is `2024-02-22-git-76b2bb96b4-full_build-www.gyan.dev`, built with GPL/version3 options, including libvorbis. Its GPLv3 license, exact build configuration and SHA-256 checksums accompany the binaries under `tools/ffmpeg/win-x64`. Upstream source: https://git.ffmpeg.org/ffmpeg.git at revision `76b2bb96b4`; build provider and corresponding build-source information: https://www.gyan.dev/ffmpeg/builds/ . Different Linux/macOS builds carry their own configurations and dependencies in the bundled tools directory.
- **xUnit.net 2.9.3**, Apache-2.0 license, https://github.com/xunit/xunit . Test tooling is not included in the application package.

Before redistributing a packaged build, retain applicable component licenses and provide the corresponding source/build materials required by that particular FFmpeg build and its bundled libraries. The setup/packaging tools retain the supplied license files and record binary provenance; they do not collect complete corresponding sources for every optional codec in a third-party full build.
