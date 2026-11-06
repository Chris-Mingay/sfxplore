param([string]$GodotDirectory = 'C:\apps\Godot_v4.7.2-stable_mono_win64', [switch]$Offline, [string]$OutputDirectory = 'dist\windows')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Offline:$Offline -Configuration ExportRelease
$taskOutput = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory)) }
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
$taskGodot = Join-Path $GodotDirectory 'Godot_v4.7.2-stable_mono_win64_console.exe'
& $taskGodot --headless --path $taskRoot --export-release Windows (Join-Path $taskOutput 'SFXplore.exe')
if ($LASTEXITCODE -ne 0) { throw 'Godot export failed. Run python scripts/fetch_templates.py windows first.' }
if (!(Get-ChildItem -LiteralPath $taskOutput -Filter 'SFXplore.dll' -File -Recurse)) { throw 'The export did not contain the compiled .NET application.' }
$taskTools = Join-Path $taskOutput 'tools\ffmpeg'
New-Item -ItemType Directory -Force -Path $taskTools | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'tools\ffmpeg\win-x64') -Destination $taskTools -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskOutput -Force
Write-Output "Standalone application: $taskOutput\SFXplore.exe"
