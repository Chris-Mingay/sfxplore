param(
    [string]$GodotDirectory = 'C:\apps\Godot_v4.7.2-stable_mono_win64',
    [string]$FfmpegDirectory = 'C:\apps\ffmpeg-2024-02-22-git-76b2bb96b4-full_build\bin'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskFeed = Join-Path $taskRoot '.tools\nuget'
New-Item -ItemType Directory -Force -Path $taskFeed | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $taskRoot 'artifacts') | Out-Null
Set-Content -LiteralPath (Join-Path $taskRoot 'artifacts\.gdignore') -Value ''
Get-ChildItem -LiteralPath (Join-Path $GodotDirectory 'GodotSharp\Tools\nupkgs') -Filter '*.nupkg' | Copy-Item -Destination $taskFeed -Force
$taskAudio = Join-Path $taskRoot 'tools\ffmpeg\win-x64'
New-Item -ItemType Directory -Force -Path $taskAudio | Out-Null
foreach ($taskName in @('ffmpeg.exe','ffprobe.exe')) { Copy-Item -LiteralPath (Join-Path $FfmpegDirectory $taskName) -Destination $taskAudio -Force }
Get-ChildItem -LiteralPath $taskAudio -Filter '*.exe' | Get-FileHash -Algorithm SHA256 | ForEach-Object { '{0}  {1}' -f $_.Hash,(Split-Path -Leaf $_.Path) } | Set-Content -LiteralPath (Join-Path $taskAudio 'SHA256SUMS')
$taskLicense = Join-Path (Split-Path -Parent $FfmpegDirectory) 'LICENSE'
if (Test-Path -LiteralPath $taskLicense) { Copy-Item -LiteralPath $taskLicense -Destination (Join-Path $taskAudio 'LICENSE') -Force }
& (Join-Path $taskAudio 'ffmpeg.exe') -version | Set-Content -LiteralPath (Join-Path $taskAudio 'BUILD-INFO.txt')
Write-Output 'Local Godot SDK feed and Windows audio tools are ready.'
