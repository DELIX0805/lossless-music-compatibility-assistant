$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$enginePath = Join-Path $repositoryRoot 'Engine\ffmpeg.exe'

if (-not (Test-Path -LiteralPath $enginePath -PathType Leaf)) {
    throw "Missing Engine\ffmpeg.exe. Copy a Windows x64 FFmpeg build with libsoxr and libmp3lame support there first."
}

$versionOutput = & $enginePath -version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 `
    -or $versionOutput -notmatch '--enable-libsoxr' `
    -or $versionOutput -notmatch '--enable-libmp3lame') {
    throw 'Engine\ffmpeg.exe is unavailable or was built without libsoxr/libmp3lame support.'
}

dotnet restore (Join-Path $repositoryRoot 'LightAudioConverter.csproj')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build (Join-Path $repositoryRoot 'LightAudioConverter.csproj') -c Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project (Join-Path $repositoryRoot 'tests\CompatibilityTests\CompatibilityTests.csproj') `
    -c Release -- $enginePath
exit $LASTEXITCODE
