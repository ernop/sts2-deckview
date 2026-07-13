# DeckView (Slay the Spire 2)

Shrinks the cards in deck-like views — the deck view, draw/discard/exhaust piles, the
card library, and deck card-select screens — so more fit on screen and the whole deck
is easier to read at a glance. Cards stay small until you actually mouse over one, which
pops it to full size; move off and it shrinks back. The combat hand, the inspect popup,
and shop/card-bundle screens are left at their normal size.

This is a Slay the Spire **2** reimplementation of the STS1 DeckView mod (`../stsmod`).
STS1 was Java + ModTheSpire; STS2 is Godot + C#, so this is a rewrite using STS2's
built-in mod loader and Harmony. See `../stsmod/STS2_PORT.md` for the full design notes.

## How it works

STS2's card grid (`NCardGrid`) is already responsive — the column count, row count and
scroll bounds are all computed from the per-card layout size (`_cardSize`) and
`CardPadding`. So the mod just makes the cards smaller and everything reflows to fit
more per screen. Two coordinated Harmony patches keep layout and rendering in sync:

| Patch | Target | Effect |
|-------|--------|--------|
| Postfix | `NCardGrid.ConnectSignals` (`_cardSize`) | shrinks the layout cell → more columns/rows, tighter scroll |
| Postfix | `NCardHolder.SmallScale` getter, guarded to `NGridCardHolder` | shrinks the *rendered* grid card to match |
| Postfix | `NCardGrid.CardPadding` getter | tighter spacing between cards |
| Postfix | `NCardGrid._Process` | each frame, snap any enlarged card the mouse isn't really over back to small |
| Postfix | `NGridCardHolder.Create` | clear stale `_isHovered`/`_isFocused` on pooled reuse |

### Why the hover reconcile (the "one card opens too big" fix)

Open the deck view and one card sometimes shows at full size while the rest are shrunk. It
isn't under the mouse — it's the card that *would* have been under the cursor at vanilla
card size. Cause: while the grid lays out / animates in, a card's hitbox briefly passes
under the (stationary) mouse, so Godot fires `MouseEntered` and the card pops to
`HoverScale`. The card then settles into its small position away from the cursor, but Godot
does **not** fire `MouseExited` for a control that moves out from under a stationary mouse,
so the hitbox's `_isHovered` stays `true` and nothing shrinks it back. (Trusting
`_isHovered` can't fix this — it *is* the stale value.)

So each frame the grid processes, the mod reconciles against ground truth: for any card
that's currently enlarged, if the mouse isn't actually inside the card's real (scaled)
on-screen rect, it's forced back to un-hovered `SmallScale`. A genuine mouse-over still pops
a card to full size (the game's own `MouseEntered` path), and moving off still plays the
smooth shrink tween; only the stuck/stale case is snapped. This also covers the deck view
grab-focusing a default card on open. The reconcile is skipped while a controller is in use,
so controller focus still enlarges the focused card; the `Create` reset clears stale hover
flags on pooled holders.

Tunables are constants at the top of `DeckViewMod.cs`:

- `CardScaleFactor` (default `0.6`) — 0.6 ≈ the STS1 mod's "60% of vanilla" look. Lower = smaller.
- `CardPadding` (default `24`) — vanilla is `40`. Set to `40` to keep vanilla spacing.

## Requirements

- Slay the Spire 2 installed (default: `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2`).
- .NET 9 SDK (`dotnet`) to build.
- The mod references the game's own `sts2.dll`, `GodotSharp.dll`, and `0Harmony.dll`
  from `…\Slay the Spire 2\data_sts2_windows_x86_64\` — nothing is bundled.

## Build & install

```powershell
# from the repo root, on Windows:
.\scripts\build.ps1 -Install
# then: launch STS2 -> Mods menu -> enable DeckView -> restart (Godot compiles mods on startup)
```

Or manually: `dotnet build deckview.csproj -c Release -o bin`, then copy `bin\deckview.dll`
and `manifest.json` into `…\Slay the Spire 2\mods\deckview\`.

A/B check: launch with `--nomods` to see vanilla for comparison.

## Status / caveats

- **Not yet built or tested in-game.** Written against the decompiled build **v0.103.3**
  (`../sts2-run-comparison/tools/decompiled/sts2`). If your installed build differs,
  re-verify the member names below before trusting it.
- Member names this depends on (re-check after game updates):
  `NCardGrid.ConnectSignals`, private field `NCardGrid._cardSize`,
  `NCardGrid.CardPadding` getter, `NCardHolder.SmallScale` getter, and that
  `NGridCardHolder` does not override `SmallScale`.
  Hover reconcile adds: `NCardGrid._Process`, `NCardGrid.CurrentlyDisplayedCardHolders`,
  `NGridCardHolder.Create`, `NControllerManager.IsUsingController`, and the private fields
  `NCardHolder._isHovered` / `_isFocused` / `_hoverTween` and `NClickableControl._isHovered`.
  If any of those private fields are renamed, the reconcile self-disables (logged on load)
  and falls back to vanilla behavior rather than crashing — a card may then open enlarged.
- `min_game_version` in `manifest.json` is set to `0.103.3`; bump it to match the build
  you compile against.
