# DeckView — development environment

Everything here is about *inspecting the game* and *building the mod*. It is
deliberately explicit so nobody has to rediscover it. This repo runs from WSL
(Linux) but the toolchain is the Windows one under `/mnt/c`.

## Error policy: work or crash — never degrade

**DeckView does not do graceful degradation.** If a hook target or reflected
member is missing, or a patch fails, we let it throw and crash rather than
catch-and-continue with a "fallback" to vanilla behavior. Silent fallbacks hide
exactly the errors we need to see (a renamed field, a moved method after a game
update). Reflection lookups resolve at load and throw immediately if a member is
absent. There is no "leave the UI vanilla" path. If a game update breaks a hook,
we want a loud crash pointing at the broken member, not a quietly half-working mod.

## The game

- Install root: `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\`
  (from WSL: `/mnt/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/`).
- Managed DLLs live in `…\Slay the Spire 2\data_sts2_windows_x86_64\`.
  The mod references `sts2.dll`, `GodotSharp.dll`, `0Harmony.dll` from there
  (reference-only — nothing is bundled or copied).
- Built/tested against game version **v0.108.0** (assemblies dated 2025-05-04).

## Decompiler (how to read game internals)

We have **no bundled decompiled source** — decompile on demand from `sts2.dll`.

- Tool: **ilspycmd** (ICSharpCode.Decompiler CLI), installed as a *Windows*
  dotnet global tool at:
  `/mnt/c/Users/ernes/.dotnet/tools/ilspycmd.exe`  (version 8.2.0.7535)
- It is a Windows exe; invoke it directly from WSL with the full path.

```bash
ILSPY="/mnt/c/Users/ernes/.dotnet/tools/ilspycmd.exe"
DLL="C:\\Program Files (x86)\\Steam\\steamapps\\common\\Slay the Spire 2\\data_sts2_windows_x86_64\\sts2.dll"

# List every type (Class/Struct/Enum … with fully-qualified names):
"$ILSPY" -l c "$DLL"

# Decompile one (or several) fully-qualified types to C# on stdout:
"$ILSPY" -t "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen" "$DLL"
"$ILSPY" -t "MegaCrit.Sts2.Core.Map.ActMap" -t "MegaCrit.Sts2.Core.Map.MapPoint" "$DLL"
```

Notes:
- `-t` on a big type can take a couple of minutes — allow up to ~5 min per call.
- Reinstall if ever missing:
  `"/mnt/c/Program Files/dotnet/dotnet.exe" tool install --global ilspycmd --version 8.2.0.7535`
  (the bare `latest` package has a broken `DotnetToolSettings.xml`; pin the version).

## Building

- **There is no Linux `dotnet`.** Use the Windows SDK from WSL:
  `/mnt/c/Program Files/dotnet/dotnet.exe`
- Normal build is the PowerShell script (runs the Windows dotnet + installs into
  the game's `mods\deckview\`):

```powershell
# from a Windows shell, repo root:
.\scripts\build.ps1 -Install
```

  From WSL you can drive the same thing via:
  `"/mnt/c/Program Files/dotnet/dotnet.exe" build deckview.csproj -c Release -o bin`
  then copy `bin\deckview.dll` + `manifest.json` into
  `…\Slay the Spire 2\mods\deckview\`.
- The csproj resolves the game DLL folder from `-p:Sts2Data=…`, the `STS2_DATA`
  env var, or the default install path (in that order).
- After building: launch STS2 → Mods menu → enable DeckView → restart (Godot
  compiles mods on startup). Launch with `--nomods` for a vanilla A/B comparison.

## Map data model (reference for the minimap work)

Decompile these when touching the map feature (see the classes for exact private
field names — they are what we reflect into):
- `NMapScreen` — on-screen map. `_mapPointDictionary` (`Dictionary<MapCoord,
  NMapPoint>`) is every on-screen point; `_mapContainer`/`_points` are the node
  parents; `_targetDragPos` is the scroll target (the container's `Position.Y`
  lerps toward it every frame and is clamped to `[-600, 1800]` — setting
  `_mapContainer.Position` directly does nothing). There is **no zoom field**.
- `ActMap` / `MapPoint` / `StandardActMap` — the data model. `MapPoint.coord`
  (`MapCoord{col,row}`), `MapPoint.PointType` (`MapPointType`), `MapPoint.Children`
  / `parents` (edges). Enumerate `GetAllMapPoints()` ∪ `StartingMapPoint` ∪
  `BossMapPoint` (∪ `SecondBossMapPoint`).
- Live run access: `RunManager.Instance` → private prop `State` (`RunState`,
  implements public `IRunState`) → `.Map`, `.CurrentMapCoord`, `.VisitedMapCoords`.
- `MapPointType` enum: `Unassigned, Unknown, Shop, Treasure, RestSite, Monster,
  Elite, Boss, Ancient`.
</content>
</invoke>
