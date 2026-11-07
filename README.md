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
- **Browser:** selecting a library clears previous search/result filters and opens its root folder grid. More results load as you scroll. Click a folder to enter it or a sound to preview it. With focus in the grid, the arrow keys move the selection and automatically preview sounds. Enter plays the selected sound or opens the selected folder. Backspace goes back. The Back/Forward/Up buttons and mouse back/forward buttons also navigate the current library. Opening a different folder after going back clears forward history. Selecting a library resets both histories. Search matches literal filename/path text. The tag filter supports multiple checkboxes and stays open while selecting. Sounds must have **all** selected tags. Use **Clear tag filters** to reset the selection. Switch to all libraries to widen the search scope. Filtered/newly added views include **Browse folders** to restore folder navigation.
- **Tags:** right-click a sound and choose **Manage tags**. Check existing tags or create and assign a new tag with a name and color. The top-level tag manager edits tag names/colors and deletes tags. Colored dots show assignments; hover for names.
- **File explorer:** right-click a sound and choose **Open file location** to open its containing directory (and select the file where supported). Choose **Copy to** to save a copy under a new name or in another directory. The dialog remembers the last successful copy destination. Right-click a folder and choose **Open folder location** to open that directory directly in the default OS file explorer. Unavailable paths produce an error with a rescan suggestion.
- **Mixes** — Choose a saved mix in the sidebar's **Mix Workspace** tree. Right-click a mix and choose **Play** to preview it from the beginning. Select a folder, then click **New folder** or **New mix** to create an item there. Click the workspace heading to create at the top level. Drag a mix or folder onto a folder to move it inside, or drop it on the heading or empty tree space to move it to the top level. Right-click a mix or folder to rename, duplicate, or delete it. The folder menu can also create a mix in that folder. Duplicating or deleting a folder includes all its nested folders and mixes. Right-click a sound and choose **Add to mix** to add an independent track. Edits autosave in SQLite.
- **Tracks** — Click the speaker icon to mute or unmute a channel. Click its chevron to reveal or hide the other channel controls. Drag a clip background horizontally or enter its start time in seconds. Drag the white bottom corner grips to trim it. Drag the pink pitch bar vertically between −24 and +24 semitones. Its centre is the original pitch. Speed, waveform and clip length update together. Numeric pitch and gain fields stay synchronized. Use the mouse wheel over the mixer to scroll vertically through tracks. Hold **Ctrl** while wheeling to zoom around the pointer, up to 3,000 pixels per second. The Zoom slider stays synchronized. The horizontal timeline scrollbar stays below the track area whenever the timeline extends beyond the viewport. Click **SOLO** to immediately play that track from its trimmed beginning in isolation, with its pitch, volume easing and master gain applied. Click it again to restart the track, or click another track's **SOLO** button to audition that track. SOLO can audition muted tracks and stops at the clip end.
- **Volume easing:** each track offers **None**, **Ease In**, **Ease Out**, or **Both**. None has a draggable yellow horizontal volume bar. When pitch and volume overlap, drag the upper side for pitch or the lower side for volume. Orange endpoint handles move vertically. Inner handles move vertically to set the shared middle volume and horizontally to set fade duration; they cannot cross. Both keeps the section between the inner handles horizontal. Volume ranges from silence to +12 dB; the **−∞** button or −60 dB numeric floor selects exact silence. Each active fade has its own method selector: Linear, or In/Out/InOut variants of Sine, Quad, Cubic and Circ (including easeInOutCirc). Fades default to 20% at each enabled end with silent endpoints and scale with the audible clip when pitching or trimming. Mode changes retain settings. Existing saved envelopes continue playing and appear read-only until a mode is explicitly chosen; Undo restores the original envelope. One completed mouse gesture is one undo step, and edits update playback during dragging. Releasing a changed pitch or volume handle automatically replays that track in isolation from its trimmed beginning. Changing an easing mode/method, numeric pitch/gain or the silence button also replays it immediately.
- **Transport:** Space plays/pauses the mix. Stop returns to zero. Click the ruler to seek. Ctrl+Z and Ctrl+Shift+Z undo/redo track edits. Browser preview and mix playback stop each other.
- **Export:** right-click a saved mix and choose **Export to** to select its filename, folder, and format in one save dialog. WAV (24-bit PCM) and OGG (Vorbis quality 6) are both 48 kHz stereo. Disk export remembers the last successful export directory. The drawer also offers **Export to library**, which lets you choose a library folder and indexes the output immediately. Export includes leading silence and ends after the last unmuted track. Muted tracks are excluded. Missing unmuted sources prevent export. Existing outputs require overwrite confirmation. Source paths cannot be overwritten.

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
