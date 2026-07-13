# Publishing DeckView to the Steam Workshop

DeckView is pinned to **Slay the Spire 2 v0.108.0** — it self-disables on any other
game version (runtime check in `DeckViewMod.Init`) and the manifest name/description say
so. Keep that promise honest: only publish a build you actually tested on the version in
the title.

## 0. Verify the current build in-game first

Launch STS2 (v0.108.0), confirm the deck view still shrinks and choose-a-card/reward are
full size, and check the log line in
`C:\Users\ernes\AppData\Roaming\SlayTheSpire2\logs\godot.log`:

```
[INFO] [DeckView] loaded — game v0.108.0, card scale x0.6, padding 24px
```

(On any other game version you'd instead see a `[DeckView] built for … v0.108.0 ONLY …`
error and no patches — that's the guard working.)

## 1. Get MegaCrit's uploader

Download `ModUploader.exe` from https://github.com/megacrit/sts2-mod-uploader
(releases). This is the official tool; there is no in-game "publish" button.

## 2. Generate a workspace and drop our files in

- Double-click `ModUploader.exe` once — it creates a `NewModWorkspace/` folder containing
  `content/`, `workshop.json`, and a placeholder `image.png`.
- Rename it to `deckview-workshop` (anything you like).
- Copy the **contents** of this repo's `workshop/content/` into the workspace's
  `content/` folder:
  - `manifest.json`
  - `deckview.dll`
  (These are the exact files that live in `…\Slay the Spire 2\mods\deckview\`. Re-copy
  them from `bin/` whenever you rebuild.)

## 3. Fill in `workshop.json`

Edit the generated `workshop.json` (use the tool's field names as authoritative). Values
to use:

- **Title:** `DeckView (game v0.108.0 only)`
- **Visibility:** start `private` or `friends` for a first test, switch to `public` once
  you've subscribed and confirmed it loads from Workshop.
- **Tags:** whatever the game exposes (e.g. `UI`, `Quality of Life`).
- **changeNote** (on updates): short summary, e.g. "Initial release. Requires game v0.108.0."
- **Description:** paste the block below.

```
DeckView shrinks the cards in deck-like views — the deck view, draw/discard/exhaust
piles, the card library, and deck card-select screens — so more fit on screen and the
whole deck is easier to read at a glance. Hover still pops a card to full size.

Left at normal size: the combat hand, the inspect popup, and the choose-a-card,
card-reward, unlock, shop, and card-bundle screens.

Works with Slay the Spire 2 v0.108.0 ONLY. The mod checks the game version on load and
self-disables (no patches, vanilla UI) on any other version, so it can't silently break a
newer build. If the game updates past v0.108.0, wait for a DeckView update.

No gameplay effect (UI/layout only).
```

## 4. Preview image

Add `image.png` (< 1 MB) — a screenshot of the shrunk deck view is ideal. Take one in
game (the deck view with many small cards) and shrink it under 1 MB.

## 5. Upload

Make sure Steam is running and logged in, then from the workspace's parent folder:

```
ModUploader.exe upload -w deckview-workshop
```

- First run creates `mod_id.txt` with the assigned Workshop item ID and publishes the item.
- Check `mod-uploader.log` if anything fails.

## 6. Verify from Workshop, then go public

- Subscribe to your own item, unsubscribe from / remove the local `mods\deckview\` copy so
  you're testing the Workshop copy, launch, and confirm it loads
  (`--- RUNNING MODDED! ---` + the DeckView log line).
- Then flip visibility to `public` (edit `workshop.json` and re-run the upload, or toggle
  on the item's Workshop page).

## Updating later

- Rebuild (`scripts/build.ps1`), re-copy `deckview.dll` into the workspace `content/`.
- Bump `version` in `manifest.json` (and in `content/manifest.json`).
- If you rebuilt for a new game version: update `TargetGameVersion` in `DeckViewMod.cs`,
  `min_game_version` + the name/description in `manifest.json`, and the Workshop title.
- Add a `changeNote`, then re-run `ModUploader.exe upload -w deckview-workshop`
  (it reuses `mod_id.txt`).
