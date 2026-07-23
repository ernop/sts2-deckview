# Publishing DeckView

DeckView `0.1.0` targets Slay the Spire 2 `v0.109.0`. Only publish a DLL after it has been
built against and tested with that game version.

## 1. Pre-release checks

On a machine with STS2 installed:

```powershell
.\scripts\build.ps1 -Install
```

From WSL, verify every private hook against the installed game:

```bash
scripts/verify-hooks.sh
```

Launch STS2 with Steam's **Play with Mods** option and check mini-cards, hover, all three checkboxes with mouse and controller,
flat/classic map switching, controller map travel, and ESC/back. New users must not produce
`MAPDUMP` log lines. The expected successful load line starts:

```text
[DeckView] loaded — card scale x0.6, padding 24px
```

If an update moved a hook, DeckView logs `DISABLED`, applies no behavior, and the vanilla UI
continues. Do not publish that build for the new game version.

## 2. Build the installable zip

After the checks above:

```powershell
.\scripts\package.ps1
```

This creates `dist\deckview-0.1.0-sts2-0.109.0.zip` and its SHA-256 file. The archive contains:

```text
deckview/
├── deckview.dll
└── deckview.json
```

Upload both files to a GitHub Release named `DeckView 0.1.0 for STS2 v0.109.0`.
Never bundle `sts2.dll`, `GodotSharp.dll`, or `0Harmony.dll`.

## 3. GitHub discovery metadata

Repository description:

```text
Slay the Spire 2 visibility-only mod: zoomed-out deck and map views, plus a clearer whole-act map. Does not change gameplay. Godot/C#/Harmony.
```

Repository topics:

```text
slay-the-spire-2  sts2  sts2-mod  godot  csharp  harmony  quality-of-life  deck  map
```

After publishing, set the repository website to the Steam Workshop page and link the GitHub
Release, Workshop page, and any Nexus listing from the README.

## 4. Steam Workshop

Download `ModUploader.exe` from
https://github.com/megacrit/sts2-mod-uploader/releases and run it once to create a workspace.

Copy into its `content/` directory:

- `bin/deckview.dll`
- `workshop/content/deckview.json`

Copy `docs/images/deck-view.png` to the workspace root as `image.png`. Edit the generated
`workshop.json` to set the title, description, visibility, tags, and change note; use the
uploader's generated field names as authoritative.

Use:

- **Title:** `DeckView — Zoomed-out Deck & Clearer Map`
- **Tags:** `UI`, `Quality of Life`
- **Preview:** workspace-root `image.png` (already below the 1 MB uploader limit)
- **Visibility:** private for the first subscription test, then public

Workshop description:

```text
DeckView zooms out deck-like views and adds a new, clearer whole-act map view for
Slay the Spire 2. Mini-cards make decks and piles visible at a glance while hover
restores full size. The optional flat map shows the same routes left-to-right on
one screen.

Visibility only: DeckView does not change cards, routes, travel rules, combat,
rewards, saves, or any other gameplay.

Mouse, keyboard, and controller supported. Targets STS2 v0.109.0.
```

Upload with:

```text
ModUploader.exe upload -w deckview-workshop
```

Subscribe to the private item, remove the local `mods\deckview\` copy, and repeat the smoke
test before changing the Workshop item to public.

## 5. Updating later

1. Bump `version` in both manifests.
2. For a game update, change `TestedGameVersion` and both `min_game_version` values.
3. Run public CI, `verify-hooks.sh`, the in-game checklist, and `package.ps1`.
4. Publish the new GitHub Release and Workshop change note.
