param([switch]$Offline, [switch]$Test, [string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $taskRoot
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\packages'
$taskArgs = @('--nologo','-v','minimal',"-p:RestoreFallbackFolders=$env:USERPROFILE\.nuget\packages")
if ($Offline) { $taskArgs += @("-p:RestoreSources=$taskRoot\.tools\nuget",'-p:NuGetAudit=false') }
& dotnet build SFXplore.csproj -c $Configuration @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
if ($Test) {
    $env:SOUNDSHELF_AUDIO_TOOLS = Join-Path $taskRoot 'tools\ffmpeg\win-x64'
    & dotnet test Tests\SoundShelf.Tests.csproj -c $Configuration @taskArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
