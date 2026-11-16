# Changelog

## Unreleased

- Fix itch publishing access to tested draft-release assets by granting the production job the required GitHub contents permission.

## 0.2.5

- Package Windows releases as ZIP while retaining tar archives for Linux/Mac and supporting existing Windows tar releases in the publisher.

## 0.2.4

- Generate Intel and Apple Silicon export templates from the official universal macOS binaries.
- Preserve template bundle metadata and permissions while validating architecture slice bounds.
- Enable ETC2/ASTC texture imports required by the Apple Silicon export preset.
- Explicitly exit the headless editor after CI exports finish.

## 0.2.3

- Resolve macOS FFmpeg dependencies using Mach-O run paths, including transitive Homebrew WebP libraries.
- Handle loader-relative and executable-relative paths while keeping bundled libraries relocatable.
- Skip dylib identifiers during dependency traversal and retain collision checks and code signing.

## 0.2.2

- Normalize older FFmpeg's float WAV headers for Godot compatibility without changing audio samples.
- Allow bounded Vorbis decoder padding while verifying lossless WAV conversion of decoded audio.
- Resolve linked ancestors inside directory-link targets, including macOS temporary-directory aliases.
- Bundle Homebrew ffmpeg-full on macOS and check required audio encoders before packaging.
- Preserve FFmpeg diagnostics when export fails with a broken pipe.
- Upgrade GitHub Actions to Node.js 24 runtimes.

## 0.2.1

- Fix CI discovery of the official Linux Godot executable.
- Report missing or ambiguous editor binaries with useful diagnostics.
- Fix a variable shadowing the release packager after ZIP extraction and SDK staging.
- Select the Mac app executable explicitly for packaged smoke tests.

## 0.2.0

- Arrange multiple sound-effect clips on each mix track.
- Drag library sounds into existing tracks or the dashed new-track area.
- Edit clip pitch, volume, timing, and easing in a popup or using inline handles.
- Adjust overall track volume independently from clip volume and the mix master.
- Automatically trim overlapping clips, with atomic undo and drag cancellation.
- Preserve older saved mixes when loading the new clip-based format.
- Export compatible 32-bit float WAV audio and convert WAV/OGG sound copies.
- Add versioned packages, draft GitHub releases, and manual itch.io publishing.

## 0.1.0

- Initial sound-library browsing, tagging, preview, mixing, and desktop export.
