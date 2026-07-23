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
- **[done]** The toggle measures the live "View upgrades" control and matches its game checkbox art,
  effective size, and Kreon label font. Mouse, keyboard, and controller activation are supported.

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
- **[done]** **Flat and classic are two co-equal MODES of the one map, never layered** — the
  load-bearing design rule (night-mode/day-mode analogy). The map is a single slot with two
  renderings; exactly one is ever open. Opening renders it *according to the current mode*; the other
  mode never appears — no flash, no fall-back, no "toggle-after-loading."
- **[done]** Implemented as a **prefix on `NMapScreen.Open`**: whenever anything opens the map (map
  room, top-bar button, our M key), if flat mode is on we render the flat page and **skip the classic
  `Open()` entirely** — the classic `NMapScreen` is *never opened* in flat mode (`IsOpen` stays
  false). Nothing underneath to bleed through or fall back to.
- **[done]** The flat page refreshes its **own** travelability (`RecalculateTravelability` via
  reflection) and reads the point dictionary directly — both exist from act start
  (`SetActInternal → GenerateMap → SetMap`) without the classic map ever opening; travel works closed
  (`OnMapPointSelectedLocally` ignores `IsOpen`). A `SetTravelEnabled` postfix rebuilds the flat page
  in place so its "can move here" highlights stay correct.
- **[done]** The two checkboxes (**Flat map** / **Compress**) are the saved single source of truth
  for the mode.
- **[done]** **M** is the only global shortcut (one-time `SceneTree.ProcessFrame` connection from an
  `NGlobalUi._Ready` patch): from any screen it opens the map in the current mode, or closes it to the
  **prior view**. **ESC/back** likewise exits the whole map — in flat mode the classic map was never
  opened, so falling back to it is *structurally impossible*. **O** — only while a map is showing —
  flips the mode and re-renders in place.
- **[done]** All toggles are one self-drawn `ToggleSwitch` (game checkbox art, shared font size,
  consistent positions) — item C "all UIs match".

### Compaction algorithm
- **[done]** **Hard invariant:** every step preserves each floor's column order ⇒ the crossing count
  is invariant (== the game's), so compaction can never scramble which rooms connect or overlap two
  rooms. A runtime assert crashes rather than draw an illegal layout.
- **[done]** **Preference ladder** (lexicographic — each tier breaks ties within the tier above):
  1. **No steep body spikes** — a body edge never jumps 2+ lanes between floors (a shape vanilla
     never makes); the start fan-out (row 0) and boss converge-in (last row) are exempt. A spike
     reads worse than an extra lane.
  2. **Fewest lanes** — clear whole rows so the act fits one screen.
  3. **Fewest slope-state changes (bends)** — minimize how many times a path changes slope on
     straight-through rooms: "up up" beats "up flat up"; `____/\` (there-and-back bump) is bad vs
     `/‾‾‾‾` (commit to the level and stay); when a change is inevitable, do it once.
  4. **Shortest total vertical travel**, then **centered** on the midline.
- **[done]** **Start and boss share one lane** (the centre line) so the map enters mid-left and exits
  mid-right at the same height. Single-node rows only (skipped on a two-boss floor); pinned after
  candidate selection, so it's metric-neutral (their fan-out / converge-in edges are already exempt
  from the slope rules). Implemented in `MapLayout.PinEnds`.
- **[done]** Method: three candidates — alignment-first, center-anchored min-pack, and a gentle
  slope-1-body layout — scored by the ladder above (`MapLayout.Better`). Straightener (`HillClimb`)
  shortens edges, then takes edge-neutral moves that cut bends, then centering. Verified offline:
  0 crossings added, 0 illegal, 0 regressed across 500 random maps + real captured levels (viz tool
  in `layout/`, `dotnet run -- viz`). Metrics: `SteepBodyEdges`, `BendCount`, `MaxEdgeSlope`.

---

## 4. Invariants & policies (mandatory)

- **Preflight and disable safely.** Validate every private/string-named hook before patching.
  Missing members or setup failures log a clear incompatibility and preserve the vanilla UI;
  never leave a partially enabled feature.
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
- **`MAPDUMP`** logging: developer opt-in only via `[debug] dump_map_graph=true` in
  `user://deckview.cfg`; it is off for new and upgraded users.
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
