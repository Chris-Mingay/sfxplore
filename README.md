# SFXplore

A Godot 4.7.2 .NET desktop app for browsing, tagging, previewing and mixing local sound effects. Catalog data and saved mixes live in SQLite; original audio files are never edited.

## Run on this computer

The project is configured for your Godot installation at `C:\apps\Godot_v4.7.2-stable_mono_win64`. Windows audio tools have already been staged under `tools/ffmpeg/win-x64`.

Open `project.godot` in **Godot .NET**, build the C# project, and press **F6/F5**, or run:

```powershell
.\scripts\run.ps1
```

To build first, use `scripts/run.ps1 -Build`. For a standalone Windows build, run `scripts/package.ps1`, then launch `dist/windows/SFXplore.exe`. Keep the executable alongside its `data_SFXplore_*` directory and `tools` folder.

## Fresh checkout

Install Godot **4.7.2 .NET**, the **.NET 8 SDK** (8.0.413 or a later 8.0 patch), and Python 3 for fetching export templates. The SDK version is recorded in `global.json`.

On Windows, stage the Godot SDK packages and FFmpeg/FFprobe from local installations:

```powershell
.\scripts\setup.ps1 -GodotDirectory C:\apps\Godot_v4.7.2-stable_mono_win64 -FfmpegDirectory C:\path\to\ffmpeg\bin
.\scripts\build.ps1 -Test
python scripts/fetch_templates.py windows
.\scripts\package.ps1
```

`build.ps1 -Offline -Test` uses the staged Godot SDK packages and existing NuGet cache. A fresh machine needs an online restore first. The scripts bypass mobile/WASM workload resolution because the application targets desktop platforms.

On Linux/macOS, `scripts/ci_build.py` automates editor/SDK/template preparation, build, tests, export, and audio-tool bundling. It needs `dotnet`, Python, and FFmpeg/FFprobe on PATH, plus `ldd` on Linux or Xcode command-line tools on macOS. Linux packages target the build machine's glibc baseline; CI builds on Ubuntu 22.04. Mac packages are produced separately for Intel and Apple Silicon. CI configuration is in `.github/workflows/build.yml`.

## Using the app

- **Libraries:** add a folder, then choose **Scan now** or **Later**. Scans include nested and hidden folders and WAV/OGG/MP3 files. Directory links are skipped. Identical or nested libraries, including linked aliases, are rejected.
- **Rescans:** add new records, refresh changed paths, and hide missing sounds. The browser stays in the library's folder grid after scanning. A report lists added/removed/changed files and errors; choose **Browse newly added sounds** for the explicit flat results view, or **Browse library folders** to return to folders. Earlier reports remain in **Scan history**. Offline folders and unreadable subtrees retain their catalog records.
- **Browser:** selecting a library clears previous search/result filters and opens its root folder grid. Click a folder to enter it, use Back/Forward/Up to navigate, and click a sound to preview it. Mouse back/forward buttons follow folder history within the current library; opening a different folder after going back clears forward history. Selecting a library resets both histories. Search matches literal filename/path text. The tag filter supports multiple checkboxes and stays open while selecting; sounds must have **all** selected tags. Use **Clear tag filters** to reset the selection. Switch to all libraries to widen the search scope. Filtered/newly added views include **Browse folders** to restore folder navigation. Results use pages of 80 items.
- **Tags:** right-click a sound and choose **Manage tags**. Check existing tags or create and assign a new tag with a name and color. The top-level tag manager edits tag names/colors and deletes tags. Colored dots show assignments; hover for names.
- **File explorer:** right-click a sound and choose **Open file location** to open its containing directory (and select the file where supported). Right-click a folder and choose **Open folder location** to open that directory directly in the default OS file explorer. Unavailable paths produce an error with a rescan suggestion.
- **Mixes:** right-click a sound and choose **Add to mix**. Every addition creates an independent track. Create, rename, duplicate and reopen named mixes; edits autosave in SQLite.
- **Tracks:** drag a clip background horizontally or enter its start time in seconds. Drag the white bottom corner grips to trim it. Drag the pink pitch bar vertically between −24 and +24 semitones; its centre is the original pitch. Speed, waveform and clip length update together. Numeric pitch and gain fields stay synchronized. Mouse wheel over the mixer zooms in/out around the pointer, up to 3,000 pixels per second (10× closer than the original limit); the Zoom slider stays synchronized. Hold Shift while wheeling to scroll vertically through tracks. The horizontal timeline scrollbar stays below the track area whenever the timeline extends beyond the viewport. Click **SOLO** to immediately play that track from its trimmed beginning in isolation, with its pitch, volume easing and master gain applied. Clicking again restarts it; clicking another track plays that one instead. SOLO can audition muted tracks, stops at the clip end, and leaves mix settings and exports unchanged.
- **Volume easing:** each track offers **None**, **Ease In**, **Ease Out**, or **Both**. None has a draggable yellow horizontal volume bar. When pitch and volume overlap, drag the upper side for pitch or the lower side for volume. Orange endpoint handles move vertically. Inner handles move vertically to set the shared middle volume and horizontally to set fade duration; they cannot cross. Both keeps the section between the inner handles horizontal. Volume ranges from silence to +12 dB; the **−∞** button or −60 dB numeric floor selects exact silence. Each active fade has its own method selector: Linear, or In/Out/InOut variants of Sine, Quad, Cubic and Circ (including easeInOutCirc). Fades default to 20% at each enabled end with silent endpoints and scale with the audible clip when pitching or trimming. Mode changes retain settings. Existing saved envelopes continue playing and appear read-only until a mode is explicitly chosen; Undo restores the original envelope. One completed mouse gesture is one undo step, and edits update playback during dragging. Releasing a changed pitch or volume handle automatically replays that track in isolation from its trimmed beginning. Changing an easing mode/method, numeric pitch/gain or the silence button also replays it immediately.
- **Transport:** Space plays/pauses the mix. Stop returns to zero. Click the ruler to seek. Ctrl+Z and Ctrl+Shift+Z undo/redo track edits. Browser preview and mix playback stop each other.
- **Export:** choose WAV (24-bit PCM) or OGG (Vorbis quality 6), both at 48 kHz stereo, then choose **Export to disk** or **Export to library**. Disk export opens the native system save dialog. Library export lets you select a library, expand its nested folders, choose the root or a subdirectory, and enter a filename. A successful library export is indexed immediately and its folder opens in the browser. Export includes leading silence and ends after the last unmuted track. Muted tracks are excluded. Missing unmuted sources prevent export. Existing outputs require overwrite confirmation; source paths cannot be overwritten.

The master control applies to previewed mixes and exports. The peak indicator identifies clipping; final samples are clamped to the output range. Reduce master/track gain when it lights up. No automatic normalization changes your balance.

## Data and implementation

SFXplore retains the existing `SoundShelf` user-data directory for compatibility with saved libraries and mixes (on Windows, `%APPDATA%\SoundShelf`). It contains `soundshelf.sqlite` and a rebuildable decoded-audio/waveform cache. A missing sound keeps its internal identity and tags, so reappearing at the same path restores them. Renames are treated as removal plus addition. Removing a library never removes source references from saved mixes.

`Core/` contains UI-independent catalog, scanner, models, undo history, decoding/cache services and the shared block-rendering kernel. `App/` contains the Godot interface. The live mixer renders on a worker thread into a bounded queue; the UI feeds Godot's audio generator. Live playback and offline export share source-frame scheduling, windowed-sinc pitch interpolation and volume easing (with linear gain envelopes retained for older mixes). Export uses an immutable snapshot, so edits during export affect subsequent playback rather than the export in progress.

The decoded PCM cache is disk-backed and memory-mapped. Old PCM entries are pruned to a 4 GiB retained budget at startup; newly decoded active sounds can temporarily exceed that budget. Metadata and waveforms are generated on demand. The application does not watch folders automatically, split clips, or edit files on disk.

## Verification

```powershell
.\scripts\build.ps1 -Offline -Test
```

Tests cover catalog overlap, linked folders, recursive reconciliation, canceled/offline scans, persistence, literal/tag search, a 100,000-sound catalog, frame scheduling, trims, envelopes, mute, pitch, undo/redo, 32-track rendering, live/offline block equivalence, WAV/OGG exports and MP3 decoding.

To create the isolated demo profile and run the Godot interaction smoke check:

```powershell
$env:SOUNDSHELF_DEMO_DIR = "$PWD\artifacts\demo-profile"
.\scripts\build.ps1 -Offline -Test
& C:\apps\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe --path . --audio-driver Dummy -- --smoke-test --exercise-ui "--data-dir=$PWD\artifacts\demo-profile" "--capture=$PWD\artifacts\sfxplore-preview.png"
```

`--data-dir` is a development/testing override. Smoke mode never changes saved window geometry. Cross-platform CI is supplied but must run on the corresponding hosts before those packages are considered verified. Release signing and Apple notarization are not configured.
