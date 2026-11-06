param([string]$GodotDirectory = 'C:\apps\Godot_v4.7.2-stable_mono_win64', [switch]$Build, [switch]$Offline)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if ($Build) { & (Join-Path $PSScriptRoot 'build.ps1') -Offline:$Offline }
& (Join-Path $GodotDirectory 'Godot_v4.7.2-stable_mono_win64_console.exe') --path $taskRoot
