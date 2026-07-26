param(
    [string]$Source
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $repositoryRoot 'Engine\ffmpeg.exe'

if ([string]::IsNullOrWhiteSpace($Source)) {
    $command = Get-Command ffmpeg.exe -ErrorAction Stop
    $Source = $command.Source
}

$Source = [System.IO.Path]::GetFullPath($Source)
if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    throw "FFmpeg executable not found: $Source"
}

$versionOutput = & $Source -version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch '--enable-libsoxr') {
    throw 'The selected FFmpeg build does not include libsoxr support.'
}

New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
Copy-Item -LiteralPath $Source -Destination $destination -Force
Write-Host "Prepared $destination"
