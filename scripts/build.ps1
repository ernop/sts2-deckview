# Build DeckView and (optionally) install it into the game's mods folder.
#
#   .\scripts\build.ps1            # build only -> bin\deckview.dll
#   .\scripts\build.ps1 -Install   # build, then copy deckview.json + deckview.dll
#                                   # into <game>\mods\deckview\
#
# Override the game path if it isn't the default Steam location:
#   $env:STS2 = "D:\Games\Slay the Spire 2"
#   .\scripts\build.ps1 -Public    # build the PUBLIC release variant (reverts to vanilla with a
#                                  # warning on failure, instead of the dev-default strict crash)
[CmdletBinding()]
param([switch]$Install, [switch]$Public)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$game = if ($env:STS2) { $env:STS2 } else { "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2" }
$data = Join-Path $game "data_sts2_windows_x86_64"

if (-not (Test-Path (Join-Path $data "sts2.dll"))) {
    throw "Couldn't find sts2.dll under '$data'. Set `$env:STS2 to your Slay the Spire 2 install folder."
}

$mode = if ($Public) { "PUBLIC (revert-to-vanilla on failure)" } else { "dev/STRICT (crash on failure)" }
Write-Host "Building against $data  [$mode] ..."
dotnet build "$root\deckview.csproj" -c Release -p:Sts2Data="$data" -p:DeckViewPublic=$($Public.IsPresent.ToString().ToLower()) -o "$root\bin"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dll = Join-Path $root "bin\deckview.dll"
if (-not (Test-Path $dll)) { throw "Build reported success but $dll is missing." }
Write-Host "Built $dll"

if ($Install) {
    $dest = Join-Path $game "mods\deckview"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item $dll                          -Destination $dest -Force
    Copy-Item (Join-Path $root "deckview.json") -Destination $dest -Force
    Write-Host "Installed to $dest"
    Write-Host "Launch STS2 -> Mods menu -> enable DeckView -> restart."
}
