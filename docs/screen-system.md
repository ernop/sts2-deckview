# STS2 screen system — reference

How Slay the Spire 2 (`sts2.dll`) manages full-screen views, captured so we don't have to
re-decompile it. All types are under `MegaCrit.Sts2.Core`. Used by DeckView's minimap, which is
implemented as a **capstone screen** (see `MiniMapScreen` in `DeckViewMod.cs`).

There is **no single `NScreen` base class**. Instead there's a shared interface `IScreenContext`,
a capstone interface `ICapstoneScreen`, several parallel "screen systems" (rooms, overlays,
capstones, the special persistent map screen), and one non-Node manager `ActiveScreenContext` that
resolves which one is "current."

## NGlobalUi — the UI root
`Nodes.CommonUi.NGlobalUi : Control`, obtained via `NRun.Instance.GlobalUi`. In `_Ready()` it grabs
children by scene-unique name and exposes them as `{ get; private set; }` properties:

| Property | Type | Node |
|---|---|---|
| `TopBar` | `NTopBar` | `%TopBar` |
| `Overlays` | `NOverlayStack` | `%OverlayScreensContainer` |
| `CapstoneContainer` | `NCapstoneContainer` | `%CapstoneScreenContainer` |
| `MapScreen` | `NMapScreen` | `%MapScreen` |
| `SubmenuStack` | `NCapstoneSubmenuStack` | `%CapstoneSubmenuStack` |
| `RelicInventory` | `NRelicInventory` | `%RelicInventory` |
| `TargetManager` | `NTargetManager` | `TargetManager` |

There is **no common parent container** exposed and no list of screens. The `%TopBar` is a sibling
of the capstone container and renders **above** capstones (that's why the top bar stays visible on
the deck-view screen and on our minimap page).

## Interfaces
- `Nodes.Screens.ScreenContext.IScreenContext` — `Control? DefaultFocusedControl { get; }` (+ a
  default `FocusedControlFromTopBar`).
- `Nodes.Screens.Capstones.ICapstoneScreen : IScreenContext` — the "stackable full-screen" contract.
  The members a mod must implement: a `ScreenType` getter (a `NetScreenType`, synced to multiplayer
  peers), a `UseSharedBackstop` bool getter, and two lifecycle callbacks `AfterCapstoneOpened()` /
  `AfterCapstoneClosed()` invoked by the container.
  `NetScreenType` (enum, `Entities.Multiplayer`): None, Room, Map, Settings, Compendium, DeckView,
  CardPile, SimpleCardsView, CardSelection, GameOver, PauseMenu, Rewards, Feedback,
  SharedRelicPicking, RemotePlayerExpandedState.
- `NDeckViewScreen : NCardsViewScreen`, and `NCardsViewScreen : Control, ICapstoneScreen` is the
  reusable capstone template (has `_backButton`, `ConnectSignals`, `OnReturnButtonPressed` →
  `NCapstoneContainer.Instance.Close()`).
- `NMapScreen : Control, IScreenContext` — NOT a capstone; it's the special persistent map with its
  own `Open()`/`Close()`/`IsOpen` and `Opened`/`Closed` signals.

## NCapstoneContainer — the single-slot fullscreen stack
`Nodes.Screens.Capstones.NCapstoneContainer : Control`. Singleton
`Instance => NRun.Instance?.GlobalUi.CapstoneContainer`.
- `ICapstoneScreen? CurrentCapstoneScreen { get; }`, `bool InUse`.
- `void Open(ICapstoneScreen screen)` — closes any current capstone, then: hides overlays, fades in
  the shared backstop (`CapstoneBackstop`), `AddChildSafely(screen)`, sets `ProcessMode = Inherit`,
  calls `screen.AfterCapstoneOpened()`, pauses combat (single-player), `ActiveScreenContext.Update()`.
- `void Close()` — unpause, show overlays, fade backstop out, sets screen `ProcessMode = Disabled`,
  `CurrentCapstoneScreen = null`, `screen.AfterCapstoneClosed()`, `ActiveScreenContext.Update()`.
- Signals `Changed`, `CapstoneClosed`.
- **Holds exactly one capstone** (opening a new one closes the old).

## ActiveScreenContext — "current screen" resolver
`Nodes.Screens.ScreenContext.ActiveScreenContext` — a plain C# singleton (`Instance`). `Update()`
fires the `Updated` event. `GetCurrentScreen()` is a fixed priority ladder: feedback → modal →
inspect card/relic → menu → **`NCapstoneContainer.CurrentCapstoneScreen`** → **`NMapScreen` (if
open)** → overlay → room types. `IsCurrent(screen)`, `FocusOnDefaultControl()`.

**Consequence for mods:** a capstone is automatically recognized as current (focus/controller/ESC
routing all work). An arbitrary sibling Control added under NGlobalUi is NOT — that path would need
a Harmony patch on `GetCurrentScreen()`. So **capstone is the clean route.**

## Back / ESC — via the hotkey stack, not a central handler
`Nodes.CommonUi.NHotkeyManager : Node`, singleton `Instance`:
`PushHotkeyPressedBinding/RemoveHotkeyPressedBinding/PushHotkeyReleasedBinding/RemoveHotkeyReleasedBinding(string hotkey, Action)`.
Dispatch in `_UnhandledInput` invokes the **last** registered action per hotkey (LIFO), and marks the
event handled. So enabling a back button pushes a `ui_cancel` binding; ESC fires the top-most one.
Input constants: `ControllerInput.MegaInput` — `cancel = "ui_cancel"`, `accept = "ui_accept"`,
`back = "mega_back"`, `pauseAndBack = "mega_pause_and_back"` (all `StringName`).

`NBackButton : NButton` auto-wires `Hotkeys => {cancel, pauseAndBack, back}` — but its `_Ready`
requires child nodes `"Outline"` and `"Image"`, so it can't be `new`'d without a scene. For a
code-built screen, push a `cancel` binding directly on `NHotkeyManager` instead.

## How the map occludes the game
`NMapScreen.Open()` doesn't use a separate CanvasLayer: it toggles its own `Visible`/`ProcessMode`,
fades a `%Backstop` control's alpha to 0.85 (dimming everything behind), and pauses combat. Capstones
do the equivalent via the shared `CapstoneBackstop`. Open call-path: `NMapRoom._Ready()` →
`NMapScreen.Instance.Open()`; toggled by `NTopBarMapButton` (`NRun.Instance.GlobalUi.TopBar.Map`).

## Recipe for a code-built (no-.tscn) screen — what DeckView's minimap does
1. `class MiniMapScreen : Control, ICapstoneScreen` — implement `ScreenType` (reuse
   `NetScreenType.DeckView`), `UseSharedBackstop => true`, `DefaultFocusedControl`.
2. Build UI in code; draw via the `Draw` **signal** and take input via the `gui_input` **signal**
   (source generators don't run for us, so `_Draw`/`_GuiInput` overrides never fire — but interface
   methods and signal connections do).
3. Show: `NCapstoneContainer.Instance.Open(screen)` (parents it, dims, pauses, keeps the top bar,
   routes focus). Dismiss: `NCapstoneContainer.Instance.Close()`.
4. Native ESC/back: in `AfterCapstoneOpened` push `NHotkeyManager.Instance.PushHotkeyReleasedBinding(
   MegaInput.cancel, OnBack)`; remove it in `AfterCapstoneClosed`. LIFO dispatch makes ours win while
   open, and removing it lets ESC then back out to the real map beneath.
