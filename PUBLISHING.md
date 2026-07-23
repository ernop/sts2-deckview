# Publishing DeckView

DeckView `0.1.0` is built and tested for Slay the Spire 2 `v0.109.0`. Only publish a DLL
that was built against and tested with that game version.

## 1. Pre-release checks

On a machine with STS2 installed:

```powershell
.\scripts\build.ps1 -Install
```

From WSL, verify every private hook against the installed game:

```bash
scripts/verify-hooks.sh
```

Launch STS2 and check mini-cards, hover, all three checkboxes with mouse and controller,
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
└── manifest.json
```

Upload both files to a GitHub Release named `DeckView 0.1.0 for STS2 v0.109.0`.
Never bundle `sts2.dll`, `GodotSharp.dll`, or `0Harmony.dll`.

## 3. Steam Workshop

Download `ModUploader.exe` from
https://github.com/megacrit/sts2-mod-uploader/releases and run it once to create a workspace.

Copy into its `content/` directory:

- `bin/deckview.dll`
- `workshop/content/manifest.json`

Use:

- **Title:** `DeckView — Zoomed-out Deck & Clearer Map`
- **Tags:** `UI`, `Quality of Life`
- **Preview:** `docs/images/deck-view.png` (already below the 1 MB uploader limit)
- **Visibility:** private for the first subscription test, then public

Workshop description:

```text
DeckView zooms out deck-like views and adds a new, clearer whole-act map view for
Slay the Spire 2. Mini-cards make decks and piles visible at a glance while hover
restores full size. The optional flat map shows the same routes left-to-right on
one screen.

Visibility only: DeckView does not change cards, routes, travel rules, combat,
rewards, saves, or any other gameplay.

Mouse, keyboard, and controller supported. Built and tested for STS2 v0.109.0.
```

Upload with:

```text
ModUploader.exe upload -w deckview-workshop
```

Subscribe to the private item, remove the local `mods\deckview\` copy, and repeat the smoke
test before changing the Workshop item to public.

## 4. Updating later

1. Bump `version` in both manifests.
2. For a game update, change `TestedGameVersion` and both `min_game_version` values.
3. Run public CI, `verify-hooks.sh`, the in-game checklist, and `package.ps1`.
4. Publish the new GitHub Release and Workshop change note.
