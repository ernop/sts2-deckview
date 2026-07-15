# DeckView — smaller deck-view cards for Slay the Spire 2

> **Built and tested for Slay the Spire 2 `v0.108.0`.** On other versions it doesn't blindly
> switch off: at load it checks that the game internals it hooks are still present and patches
> anyway if they are (minor updates usually don't move them). If something it needs is missing
> — or patching fails — it backs out and leaves the UI vanilla rather than crashing. Either
> way a game update can't break your run; worst case you get stock card sizes until a rebuild.

## What it's for

STS2 draws deck-like views at a size where only a handful of cards fit on screen, so you
scroll a lot and can't take your deck in at a glance. DeckView shrinks those cards so more
of the deck fits at once and the whole thing is easier to read. Cards stay small until you
mouse over one, which pops it to full size; move off and it shrinks back.

- **Shrinks:** the deck view, the draw / discard / exhaust piles, the card library, and the
  deck card-select screens.
- **Left at normal size:** the combat hand, the inspect popup, and the choose-a-card,
  card-reward, unlock, shop, and card-bundle screens.
- **Toggle any time:** open a card view and press **F9** to flip between shrunk (mini) and
  normal card size. The choice is saved and persists across runs (`user://deckview.cfg`).

It's a Godot + C# + Harmony rewrite of the original STS1 DeckView mod (Java + ModTheSpire),
built on STS2's own mod loader — nothing from the game is bundled.

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
| Postfix | `NCardGrid._Process` | each frame, snap any enlarged card the mouse isn't really over back to small (and dismiss its hover-tip / related-card popup) |
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
on-screen rect, it's forced back to un-hovered `SmallScale` — and its hover-tip popup (the
keyword tips and related-card previews) is dismissed too, since the normal shrink path that
would clear them is bypassed. A genuine mouse-over still pops a card to full size (the game's
own `MouseEntered` path), and moving off still plays the smooth shrink tween; only the
stuck/stale case is snapped. This also covers the deck view
grab-focusing a default card on open. The reconcile is skipped while a controller is in use,
so controller focus still enlarges the focused card; the `Create` reset clears stale hover
flags on pooled holders.

### Mini / large toggle (persistent)

Every shrink patch is gated on a single mode flag (`DeckModeController.MiniEnabled`), loaded
from and saved to `user://deckview.cfg`, so your choice survives across runs (default: mini).
Press **F9** while a card grid is on screen to flip it. Because the layout cell size
(`_cardSize`) is only computed once in `ConnectSignals`, a live toggle can't just change the
render scale — it would leave columns/scroll out of sync. So the mod records each grid's
vanilla base size at connect time and, on toggle, rewrites `_cardSize` from that (× the mode
factor) and flags the grid for reinit; the grid then rebuilds (`InitGrid`) with size, padding,
and rendered scale all in agreement — a clean, complete swap with no half-state.

(F9 rather than Shift+D: `D` also drives the deck-open action and a polled hotkey can't stop
that key from reaching the game, so a `D`-based combo would fight it. Change `ToggleDeckModeKey`
in `DeckViewMod.cs` to taste.)

Tunables are constants at the top of `DeckViewMod.cs`:

- `CardScaleFactor` (default `0.6`) — 0.6 ≈ the STS1 mod's "60% of vanilla" look. Lower = smaller.
- `CardPadding` (default `24`) — vanilla is `40`. Set to `40` to keep vanilla spacing.
- `ToggleDeckModeKey` (default `F9`) — the mini/large toggle key.

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

- **Built against the game's own `v0.108.0` assemblies and confirmed working in-game** — the
  deck view shrinks, hover still zooms, and no card opens stuck-large. `TestedGameVersion` in
  `DeckViewMod.cs` records that build; `min_game_version` in `manifest.json` is the floor. On
  a newer game version DeckView probes for the members it hooks and patches if they're still
  there, otherwise backs out to vanilla (see the load behavior above). (The patch logic was
  originally written against a `v0.103.3` decompile, then built and verified on `v0.108.0`.)
- Member names it depends on — re-check and rebuild after any game update:
  `NCardGrid.ConnectSignals`, private field `NCardGrid._cardSize`,
  `NCardGrid.CardPadding` getter, `NCardHolder.SmallScale` getter, and that
  `NGridCardHolder` does not override `SmallScale`.
  The hover reconcile also uses: `NCardGrid._Process`, `NCardGrid.CurrentlyDisplayedCardHolders`,
  `NGridCardHolder.Create`, `NControllerManager.IsUsingController`, and the private fields
  `NCardHolder._isHovered` / `_isFocused` / `_hoverTween` and `NClickableControl._isHovered`.
  The mini/large toggle also uses: `NCardGrid._ExitTree` and the private fields
  `NCardGrid._cardSize` / `_needsReinit`.
  If one of those private fields is renamed, the reconcile self-disables (logged on load) and
  falls back to vanilla behavior rather than crashing — a card may then open enlarged again.
- To support a newer game build: rebuild against its `sts2.dll`, then bump `TargetGameVersion`
  and `manifest.json`'s `min_game_version` / name to that version.
