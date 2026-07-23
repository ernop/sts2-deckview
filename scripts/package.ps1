# Build a deterministic, user-installable DeckView archive from bin\deckview.dll.
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root "deckview.json"
$workshopManifestPath = Join-Path $root "workshop\content\deckview.json"
$dllPath = Join-Path $root "bin\deckview.dll"
$dist = Join-Path $root "dist"

# Ship the PUBLIC variant: on an incompatible/failed hook it reverts to vanilla with a warning
# instead of the dev-default strict crash. Functionally identical to the dev build you tested; only
# the failure mode differs. (Do your in-game checks on a normal build.ps1 build first.)
Write-Host "Building PUBLIC release DLL ..."
& (Join-Path $PSScriptRoot "build.ps1") -Public
if ($LASTEXITCODE -ne 0) { throw "Public build failed." }

if (-not (Test-Path $dllPath)) {
    throw "Missing '$dllPath' after the public build."
}

$manifestText = [IO.File]::ReadAllText($manifestPath)
$workshopText = [IO.File]::ReadAllText($workshopManifestPath)
if ($manifestText -ne $workshopText) {
    throw "Root and Workshop manifests differ. Synchronize them before packaging."
}

$manifest = $manifestText | ConvertFrom-Json
$baseName = "deckview-$($manifest.version)-sts2-$($manifest.min_game_version)"
$zipPath = Join-Path $dist "$baseName.zip"
$hashPath = "$zipPath.sha256"

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Remove-Item $zipPath, $hashPath -Force -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
try {
    $archive = [IO.Compression.ZipArchive]::new(
        $stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $timestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        foreach ($source in @(
            @{ Path = $dllPath; Name = "deckview/deckview.dll" },
            @{ Path = $manifestPath; Name = "deckview/deckview.json" }
        )) {
            $entry = $archive.CreateEntry(
                $source.Name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $timestamp
            $entryStream = $entry.Open()
            try {
                $bytes = [IO.File]::ReadAllBytes($source.Path)
                $entryStream.Write($bytes, 0, $bytes.Length)
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $stream.Dispose()
}

$hash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -Encoding ascii -NoNewline $hashPath
Write-Host "Created $zipPath"
Write-Host "Created $hashPath"
