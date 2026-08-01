param(
    [string]$Source
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $repositoryRoot 'Engine\ffmpeg.exe'
$temporaryDestination = Join-Path $repositoryRoot 'Engine\ffmpeg.prepare.tmp.exe'

function Test-CompatibleFfmpeg {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    try {
        $versionOutput = & $Path -version 2>&1 | Out-String
        return $LASTEXITCODE -eq 0 `
            -and $versionOutput -match '--enable-libsoxr' `
            -and $versionOutput -match '--enable-libmp3lame'
    }
    catch {
        return $false
    }
}

$candidates = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($Source)) {
    $candidates.Add([System.IO.Path]::GetFullPath($Source))
}
else {
    Get-Command ffmpeg.exe -All -ErrorAction SilentlyContinue |
        ForEach-Object { if ($_.Source) { $candidates.Add($_.Source) } }

    $chocolateyRoot = $env:ChocolateyInstall
    if ([string]::IsNullOrWhiteSpace($chocolateyRoot)) {
        $chocolateyRoot = 'C:\ProgramData\chocolatey'
    }
    $chocolateyLibrary = Join-Path $chocolateyRoot 'lib'
    if (Test-Path -LiteralPath $chocolateyLibrary -PathType Container) {
        Get-ChildItem -LiteralPath $chocolateyLibrary -Filter ffmpeg.exe -File -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $candidates.Add($_.FullName) }
    }
}

New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
try {
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-CompatibleFfmpeg -Path $candidate)) {
            continue
        }

        Copy-Item -LiteralPath $candidate -Destination $temporaryDestination -Force
        if (-not (Test-CompatibleFfmpeg -Path $temporaryDestination)) {
            Remove-Item -LiteralPath $temporaryDestination -Force -ErrorAction SilentlyContinue
            continue
        }

        Move-Item -LiteralPath $temporaryDestination -Destination $destination -Force
        Write-Host "Prepared $destination from $candidate"
        exit 0
    }
}
finally {
    Remove-Item -LiteralPath $temporaryDestination -Force -ErrorAction SilentlyContinue
}

throw 'Could not find a relocatable Windows FFmpeg executable with libsoxr and libmp3lame support.'
