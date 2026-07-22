# DeckView — requirements & product spec

Living record of everything this mod is meant to do, gathered across development. Status tags:
**[done]** shipped & in the build, **[done*]** shipped but wants in-game verification/tuning,
**[todo]** requested, not yet built.

DeckView is a Slay the Spire 2 (Godot + C# + Harmony) mod with two features: **mini-cards** (deck
views) and an **alternate map view**. Companion docs: `screen-system.md` (capstone/screen internals),
`DEVELOPMENT.md` (build + decompile). Cross-cutting policies in "Invariants" below are mandatory.

---

## 1. Product goals

- **Mini-cards:** STS2 draws deck-like screens at a size where few cards fit. Shrink them so more of
  the deck is visible at a glance; hovering a card still pops it to full size.
- **Map:** the game's map is a tall vertical scroll. Offer a compact, glance-able, whole-act-on-one-
  screen alternate view that is provably the *same* map — just laid out better.

---

## 2. Mini-cards (deck views)

- **[done]** Shrink cards in: deck view, draw/discard/exhaust piles, card library, deck card-select.
- **[done]** Leave at normal size: combat hand, inspect popup, choose-a-card, card-reward, unlock,
  shop, card-bundle screens.
- **[done]** Hover a shrunk card → pops to full size; move off → shrinks back smoothly, and its
  hover-tip / related-card popup is dismissed via the game's *own* un-hover path (no lingering popup).
- **[done]** A stuck "opens too big" card (stale hover from layout) is reconciled back to small each
  frame; skipped under controller input.
- **[done]** Visible **"Mini-cards" toggle** in the deck control cluster (by the sort buttons /
  "View upgrades"), **on by default**, hotkey **T**. Persisted across runs (`user://deckview.cfg`).
- **[done*]** The toggle matches the game's "View upgrades" control: game checkbox art, native size,
  and the same label font size (Kreon). Placement still wants tuning.

---

## 3. Map — the flat page

### Structure & integration
- **[done]** Rendered as a **real capstone screen** (`ICapstoneScreen` via `NCapstoneContainer`),
  exactly like the deck-view screen — so it's a top-level page that naturally keeps the game's **top
  bar**, dim backstop, combat pause, and **native input/back/controller** routing.
- **[done]** Layout is **left→right**: floors run along X (start at left, boss at right); the vertical
  axis is the (compacted) lane. Whole act fits one screen.
- **[done]** Reads live state only (`_mapPointDictionary`, `_runState`) — never mutates game data.

### Node appearance
- **[done]** Each node = a colored circle with the game's **real room icon** on top.
- **[done]** Colors: monster **red**, elite **purple**, shop **yellow**, camp/rest **green**,
  treasure **orange**, unknown **grey**, start **teal**, boss **red** (real boss placeholder art;
  Spine-art bosses fall back to "B").
- **[done]** Two-boss levels draw both bosses (both are in the point dictionary).

### Node state clarity
- **[done]** **Current position:** a bold **blue double-ring** AND the game's own **"you are here"
  arrow** (`NMapScreen._marker`'s per-character `MapMarker` texture) floated above the node. Both, on
  purpose (item B / later revision). Arrow falls back to a blue chevron if the marker art is absent.
- **[done]** **Current node "done vs not-done" (gated on `NMapScreen.IsTravelEnabled`):**
  - *Room finished, travel enabled* → the current node reads as a **done** node (dimmed like your
    past rooms); attention shifts to the highlighted next options.
  - *Room NOT finished yet* → the current node stays **full/active**, so it's obvious your next
    action is *within* the current node. It is not dimmed-as-done.
- **[done]** **Start:** cyan ring.
- **[done]** **Live next options** (the rooms you can move to *right now*): a bright white
  "selectable" halo — deliberately NOT a red arrow, since the character marker above the current node
  is itself an arrow. Shown **only when travel is enabled**: if you must finish the current room
  first, these highlights do NOT appear on any downstream node (you can't go there yet). The set is
  the game's own **relic-aware** `MapPointState.Travelable`, so **Wing Boots** (free travel = the
  whole next row via `Hook.ShouldAllowFreeTravel`) is respected with no extra logic.
- **[done]** **Unreachable-and-unvisited** rooms: greyed out; their edges fade. Reachability =
  forward BFS seeded from {current ∪ every Travelable node}, so with Wing Boots more stays lit, and
  without them parallel/future tracks you can't currently reach are greyed.
- **[done]** **Visited (past)** rooms keep their colour but are **partially dimmed** (~68% fill,
  softened icon/outline), so the first full-colour node reads as the first one still ahead (item A).
- **[done]** A visited **`?` node adopts its revealed room** (item F): resolved via
  `_runState.MapPointHistory[act][row].Rooms.First().RoomType`, so it re-colours + tallies as the real
  room (monster/shop/elite/treasure/rest), matching the game's on-entry reveal. The live node already
  carries the resolved icon art. The old "been here" dot is gone — dimming + the resolved icon now
  signal "visited".

### Interaction
- **[done]** Hover highlights a node (pointer cursor over travelable ones).
- **[done]** Click a **travelable** node → travels via the game's own selection path
  (`OnMapPointSelectedLocally`), then closes the page so the animation plays on the real map.

### Info panel (bottom-right)
- **[done]** Act name, current floor, and a per-type tally of rooms visited so far.
- **[done]** The tally uses the actual room **icons** (the game's own art) + counts, right-aligned,
  instead of letter labels (item E). Counts use the effective type, so revealed `?`s tally as their
  real room.

### Bottom-left labels
- **[done]** Removed the "MAP" subtitle (item D) and the "O or Esc: back to the map" hint (item C);
  the bottom-left is now clear. Page identity comes from the info panel's act name.

### Style toggle & keys
- **[done]** **"Flat map" toggle** on BOTH the classic map and the flat page, at the same bottom-left
  spot (reads as one control), never bleeding onto other screens (hidden when any capstone is up).
- **[done]** **"Compress" toggle** on the flat page, **on by default**: off = raw layout 1:1 with the
  game's columns (proves compression changed nothing but spacing); on = compacted.
- **[done]** The two checkboxes are the **single source of truth** for how the map looks. The map
  style (flat vs classic) + compression are a stable, saved preference.
- **[done]** **M is the only global shortcut** — from ANY screen (combat, reward, room) it toggles
  the map's visibility. Opening shows the map in exactly the configured checkbox state. Closing
  (`NMapScreen.Close()` + capstone close) returns to the **prior view**, never a half-state.
- **[done]** **O is NOT global** — it only acts while a map is displayed, where it flips the "Flat
  map" checkbox (persisted, both on-screen toggles synced) and swaps flat↔classic **in place**.
- **[done]** **Flat map is never a submodule of the classic map.** ESC/back from the flat page exits
  the *whole* map to the prior view — identical to ESC on the classic map. It never peels back to
  classic. Switching flat↔classic happens ONLY via O or the checkbox, never via ESC.
- **[done]** Global M is wired via a one-time `SceneTree.ProcessFrame` connection bootstrapped from
  an `NGlobalUi._Ready` patch (the map's own `_Process` only runs while the map is up, so it can't
  carry a global key). Confirmed: no game logic reopens the map after a capstone closes.
- **[done]** All toggles are one self-drawn `ToggleSwitch` (game checkbox art, shared font size,
  consistent positions) — item C "all UIs match".

### Compaction algorithm
- **[done]** Goal: minimize the number of lanes (clear whole rows) AND straighten paths, **without
  ever adding a crossing**.
- **[done]** Key property: every step preserves each floor's column order ⇒ the crossing count is
  invariant (== the game's), so compaction can never scramble which rooms connect.
- **[done]** Method: two candidates — alignment-first (columns → straighten → merge clean lanes) and
  compactness-first (min-pack to fewest lanes → straighten within budget) — pick fewer lanes,
  tie-break straightness. Verified offline: ~27% fewer lanes, ~28% shorter edges, 0 crossings added,
  legal, across 500 random maps + real captured levels.

---

## 4. Invariants & policies (mandatory)

- **Work or crash — never degrade.** No probe-and-disable, no catch-and-fall-back-to-vanilla. A
  missing/renamed hooked member throws loudly. (Reflection resolves fail-loud via `Reflect`.)
- **View-only / connectivity sanctity.** Never modify the real map's data or nodes; the moves we draw
  ARE the vanilla player's moves. We only reassign *display lanes*. Never overlap two nodes (would
  hide a choice). A runtime assert crashes rather than draw an illegal layout.
- **Never persist to the game's save.** Our only file is `user://deckview.cfg` (our preferences).

---

## 5. Tooling & process

- **Build/decompile:** Windows dotnet from WSL; `ilspycmd` for decompiling `sts2.dll`. See
  `DEVELOPMENT.md`.
- **Offline layout harness** (`layout/`, no game): pure `MapLayout` algorithm + invariant checker +
  metrics + a 500-map property test + curated cases + a **viz tool** (`layouttest -- viz` renders any
  captured level and compression options as ASCII). This is where the algorithm is proven before it
  ships.
- **`MAPDUMP`** logging: each map open logs the live graph so a real level can be replayed offline.
- **Diagnostics:** toggle positions/sizes/font-size and per-open state are logged so one play-test
  pinpoints tuning. Screenshots load from `c:\screenshots`.
- **Reliability principle:** pure logic → offline tests (gold standard); game-integration → verify
  hooks + runtime asserts + loud logs; visual/positional → accept one tuning pass, made cheap by logs.

---

## 6. Iteration batch (from the Overgrowth screenshot) — all shipped

- **[done*] A** Visited nodes keep colour, partially dimmed.
- **[done*] B** "You are here" = the game's native map-marker arrow above the node (was a gold ring).
- **[done*] C** Removed the "O or Esc: back to the map" hint text.
- **[done*] D** Removed the "MAP" subtitle.
- **[done*] E** Info-panel tally uses real room icons + counts.
- **[done*] F** Visited `?` nodes re-colour/re-icon to their revealed room (via `MapPointHistory`).

Research notes (from decompiling `sts2.dll`, for future reference): the game has **no dedicated reveal
morph** — `NNormalMapPoint.UpdateIcon()` swaps the icon texture instantly; the surrounding flourish is
`OnSelected()`'s scale/colour tween + the `NMapCircleVfx` ink-circle (`map_circle_0..4.tres`) +
`NMapNodeSelectVfx` brush-burst. Resolved-`?` art is a distinct set
(`map/icons/map_unknown_{monster,shop,elite,chest}.tres`). The current-location marker is `NMapMarker`
(a `TextureRect` = `Character.MapMarker`), single-player only, popped in with an elastic tween. The
selectable frontier isn't a separate vfx — reachable `NNormalMapPoint`s self-pulse in `_Process`.
