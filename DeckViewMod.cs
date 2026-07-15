using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;

namespace DeckView;

// DeckView — Slay the Spire 2 port of the STS1 "make deck-view cards smaller" mod.
//
// STS2's card grid (NCardGrid) is already responsive: the column count and scroll
// bounds are computed from the per-card layout size and padding. So we only have to
// make the cards smaller and everything reflows to fit more per screen.
//
// Two coordinated levers, kept proportional so layout and rendering stay in sync:
//   1. NCardGrid._cardSize  — the layout cell size (drives Columns, positions, scroll).
//   2. NCardHolder.SmallScale — the *rendered* scale of each grid card.
//
// Both are the vanilla 0.8 baseline (NCard.defaultSize * smallScale / SmallScale).
// We multiply both by the same factor. Merchant and card-bundle screens read the
// static `smallScale` field directly (not the SmallScale property), so they are
// untouched — the shrink is naturally scoped to card-grid views.
//
// A grid card enlarges to HoverScale (1.0) only while the mouse is *actually* over its
// (small) on-screen rect. See GridHoverGate for why: without that, a card can open
// "stuck big" from a stale mouse-over the game never cleared.
[ModInitializer(nameof(Init))]
public static class DeckViewMod
{
    // 0.6 => cards render at 60% of the vanilla deck-view size, matching the STS1 mod.
    // Lower = smaller cards / more columns. Tune to taste. (Only applied in mini mode —
    // see DeckModeController; toggle with the hotkey below, state persists across runs.)
    public const float CardScaleFactor = 0.6f;

    // Vanilla NCardGrid.CardPadding is a constant 40f. Tighter spacing packs in more
    // columns/rows. Set equal to 40f to keep vanilla spacing.
    public const float CardPadding = 24f;

    // Hotkey to toggle mini <-> large card mode. Checked while a card grid is on screen
    // (deck view, library, card-select), so open the deck and press it to flip live.
    // NOTE: this is a plain key (no modifier) because the key is *read*, not consumed —
    // if it happens to also be bound to something on that screen, both would fire. T isn't
    // a known deck-screen binding; change here if it clashes.
    public const Key ToggleDeckModeKey = Key.T;

    // Hotkey to toggle the whole-act map overview (zoom out to see the entire map). Checked
    // while the map screen is open. O = "overview". Change to taste.
    public const Key ToggleMapOverviewKey = Key.O;

    // Overview fits the map into this fraction of the viewport (0.9 = 90%, small margin).
    public const float MapOverviewFitFraction = 0.9f;

    // DeckView was built and tested against this game version. It patches internal names
    // (methods + private fields) that MegaCrit can rename between builds, so this is the
    // build we KNOW matches. On other versions DeckView does NOT blindly self-disable: it
    // probes for the members it hooks and patches anyway if they're all still there (point
    // releases usually don't move them). If a hooked member is missing — or patching throws
    // — it backs out cleanly and leaves the UI vanilla rather than crashing the game.
    public const string TestedGameVersion = "v0.108.0";

    private const string HarmonyId = "ernes.deckview";

    public static void Init()
    {
        string? gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        try
        {
            if (!EssentialHooksPresent(out string missing))
            {
                Log.Error(
                    $"[DeckView] this game ('{gameVersion ?? "unknown"}') is missing members DeckView hooks " +
                    $"({missing}). Leaving the UI vanilla — DeckView was built for {TestedGameVersion} and likely " +
                    $"needs a rebuild against this version.");
                return;
            }

            new Harmony(HarmonyId).PatchAll(typeof(DeckViewMod).Assembly);

            string versionNote = gameVersion == TestedGameVersion
                ? ""
                : $" — NOTE: game '{gameVersion ?? "unknown"}' is not the tested {TestedGameVersion}; hooks matched so " +
                  "it's patched, but re-verify and report anything off";
            Log.Info(
                $"[DeckView] loaded — card scale x{CardScaleFactor}, padding {CardPadding}px" +
                (GridHoverGate.Viable ? "" : " [hover reconcile disabled: private fields not found — a card may open enlarged]") +
                versionNote);
        }
        catch (Exception e)
        {
            new Harmony(HarmonyId).UnpatchAll(HarmonyId);
            Log.Error(
                $"[DeckView] failed to patch game '{gameVersion ?? "unknown"}' — backed out, UI left vanilla. {e}");
        }
    }

    // Verify every member PatchAll will hook actually exists on the running game, so an
    // unknown/incompatible version fails cleanly (tidy log, vanilla UI) instead of throwing
    // part-way through patching.
    private static bool EssentialHooksPresent(out string missing)
    {
        (string name, bool ok)[] hooks =
        {
            ("NCardGrid.ConnectSignals", AccessTools.Method(typeof(NCardGrid), "ConnectSignals") != null),
            ("NCardGrid._cardSize", AccessTools.Field(typeof(NCardGrid), "_cardSize") != null),
            ("NCardHolder.SmallScale", AccessTools.PropertyGetter(typeof(NCardHolder), "SmallScale") != null),
            ("NCardGrid.CardPadding", AccessTools.PropertyGetter(typeof(NCardGrid), "CardPadding") != null),
            ("NCardGrid._Process", AccessTools.Method(typeof(NCardGrid), "_Process") != null),
            ("NCardGrid._ExitTree", AccessTools.Method(typeof(NCardGrid), "_ExitTree") != null),
            ("NMapScreen._Process", AccessTools.Method(typeof(NMapScreen), "_Process") != null),
            ("NGridCardHolder.Create", AccessTools.Method(typeof(NGridCardHolder), "Create") != null),
            ("NCardGrid.CurrentlyDisplayedCardHolders",
                AccessTools.PropertyGetter(typeof(NCardGrid), "CurrentlyDisplayedCardHolders") != null),
        };
        missing = string.Join(", ", hooks.Where(h => !h.ok).Select(h => h.name));
        return missing.Length == 0;
    }
}

// --- Mini/large toggle: persistent state + hotkey + clean live swap -------------------
//
// The shrink is no longer unconditional: every shrink patch is gated on
// DeckModeController.MiniEnabled, whose value is loaded from (and saved to) a small config
// file so it survives across runs. Default is mini (matches the mod's original behavior).
//
// Toggling has to be a *complete* swap, not a half-state: the layout cell size (_cardSize)
// determines Columns / positions / scroll and is only computed in ConnectSignals, which
// runs once. So on toggle we set each live grid's _cardSize from the vanilla base we
// recorded (times the mode factor) and flag it for reinit — the grid then rebuilds itself
// (InitGrid) next frame with the new size, padding, and rendered scale all in agreement.
internal static class DeckViewConfig
{
    private const string Path = "user://deckview.cfg";

    private static bool _loaded;
    private static bool _miniDeck = true; // default: shrink (original behavior)

    internal static bool MiniDeck
    {
        get { EnsureLoaded(); return _miniDeck; }
        set
        {
            EnsureLoaded();
            if (_miniDeck == value) return;
            _miniDeck = value;
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var cfg = new ConfigFile();
            if (cfg.Load(Path) == Error.Ok)
                _miniDeck = cfg.GetValue("deck", "mini", true).AsBool();
        }
        catch (Exception e)
        {
            Log.Error($"[DeckView] could not read {Path}, using default (mini). {e.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            var cfg = new ConfigFile();
            cfg.Load(Path); // preserve any other keys; ignore "file missing"
            cfg.SetValue("deck", "mini", _miniDeck);
            cfg.Save(Path);
        }
        catch (Exception e)
        {
            Log.Error($"[DeckView] could not save {Path}. {e.Message}");
        }
    }
}

internal static class DeckModeController
{
    private static readonly FieldInfo? CardSizeField = AccessTools.Field(typeof(NCardGrid), "_cardSize");
    private static readonly FieldInfo? NeedsReinitField = AccessTools.Field(typeof(NCardGrid), "_needsReinit");

    // Live grids -> the vanilla (un-shrunk) _cardSize captured at ConnectSignals time.
    private static readonly Dictionary<NCardGrid, Vector2> _grids = new();

    private static bool _keyWasDown;

    internal static bool MiniEnabled => DeckViewConfig.MiniDeck;

    internal static void Register(NCardGrid grid, Vector2 vanillaCardSize) => _grids[grid] = vanillaCardSize;

    internal static void Unregister(NCardGrid grid) => _grids.Remove(grid);

    // Edge-detected hotkey poll (called each frame a card grid processes).
    internal static void PollHotkey()
    {
        bool down = Input.IsKeyPressed(DeckViewMod.ToggleDeckModeKey);
        if (down && !_keyWasDown)
            Toggle();
        _keyWasDown = down;
    }

    internal static void Toggle()
    {
        DeckViewConfig.MiniDeck = !DeckViewConfig.MiniDeck;
        float factor = MiniEnabled ? DeckViewMod.CardScaleFactor : 1f;
        Log.Info($"[DeckView] {(MiniEnabled ? "mini" : "large")} deck mode");

        foreach (KeyValuePair<NCardGrid, Vector2> kv in _grids.ToArray())
        {
            NCardGrid grid = kv.Key;
            if (!GodotObject.IsInstanceValid(grid))
            {
                _grids.Remove(grid);
                continue;
            }
            // Resize the layout cell from the recorded vanilla base, then let the grid rebuild
            // itself so columns/positions/scroll and the rendered card scale all flip together.
            CardSizeField?.SetValue(grid, kv.Value * factor);
            NeedsReinitField?.SetValue(grid, true);
        }
    }
}

// Shrink the layout cell size right after the grid computes it in ConnectSignals
// (vanilla: _cardSize = NCard.defaultSize * NCardHolder.smallScale). Because Columns,
// row count, positions and scroll limits all derive from _cardSize + CardPadding, the
// grid reflows to more columns automatically.
[HarmonyPatch(typeof(NCardGrid), "ConnectSignals")]
internal static class NCardGrid_ConnectSignals_Patch
{
    // Harmony injects the private field `_cardSize` as the parameter `____cardSize`
    // (three-underscore prefix + the field name, which itself starts with '_'). At postfix
    // entry it holds the vanilla base size; we record that (so a later live toggle can
    // recompute from it without re-running ConnectSignals) and shrink only in mini mode.
    private static void Postfix(NCardGrid __instance, ref Vector2 ____cardSize)
    {
        DeckModeController.Register(__instance, ____cardSize);
        if (DeckModeController.MiniEnabled)
            ____cardSize *= DeckViewMod.CardScaleFactor;
    }
}

// Drop a grid from the toggle registry when it leaves the tree.
[HarmonyPatch(typeof(NCardGrid), "_ExitTree")]
internal static class NCardGrid_ExitTree_Patch
{
    private static void Postfix(NCardGrid __instance) => DeckModeController.Unregister(__instance);
}

// Shrink the *rendered* scale of grid cards to match the smaller layout cells.
// Grid holders set their Scale from the SmallScale property (NCardGrid line ~799 and
// NGridCardHolder line ~104), and the hover-out tween returns to SmallScale, so this
// keeps rendering consistent — hover still pops to HoverScale (1.0) for readability.
//
// Guarded to NGridCardHolder so the shrink is limited to deck-grid cards. This leaves
// the combat hand (NHandCardHolder — uses its own _targetScale), the inspect popup
// (NPreviewCardHolder — overrides SmallScale, so this patch never runs for it), and
// selected-from-hand cards (NSelectedHandCardHolder) at their normal size — matching
// the STS1 mod's "leave hand/popups/normal rendering alone" scope.
[HarmonyPatch(typeof(NCardHolder), "SmallScale", MethodType.Getter)]
internal static class NCardHolder_SmallScale_Patch
{
    private static void Postfix(NCardHolder __instance, ref Vector2 __result)
    {
        if (DeckModeController.MiniEnabled && __instance is NGridCardHolder && !GridHoverGate.IsInFixedCardRow(__instance))
            __result *= DeckViewMod.CardScaleFactor;
    }
}

// Tighten the spacing between cards (vanilla getter returns a constant 40f).
[HarmonyPatch(typeof(NCardGrid), "CardPadding", MethodType.Getter)]
internal static class NCardGrid_CardPadding_Patch
{
    private static void Postfix(ref float __result)
    {
        if (DeckModeController.MiniEnabled)
            __result = DeckViewMod.CardPadding;
    }
}

// --- Hover reconcile: a grid card is "big" only while the mouse is really over it ------
//
// Symptom this fixes: open the deck view (press "d") and one card sometimes shows at full
// size while every other card is correctly shrunk. It's NOT under the mouse — it's the
// card that would have been under the cursor at *vanilla* card size — and it stays big
// until you mouse over it and off again.
//
// Root cause: while the grid lays out / animates in, a card's hitbox briefly passes under
// the (stationary) mouse, so Godot fires MouseEntered and the card pops to HoverScale.
// The card then settles into its small position away from the cursor, but Godot does NOT
// fire MouseExited for a control that moves out from under a stationary mouse — so the
// hitbox's _isHovered stays true and nothing shrinks the card back. Trusting _isHovered
// can't fix this; it IS the stale value.
//
// Fix: every frame the grid processes, reconcile against ground truth. For each displayed
// holder that's currently enlarged (_isFocused), if the mouse isn't actually inside the
// holder's real (scaled) on-screen rect, force it back to un-hovered SmallScale. A genuine
// mouse-over keeps the card big (the game's own MouseEntered path still enlarges it); when
// you move off, the game's normal MouseExited shrink runs (a smooth tween, left untouched
// because _isFocused is already false by then). Only the stuck/stale case is corrected.
//
// This also covers the deck view grab-focusing a default card on open (enlarge with no
// mouse on it): that card is _isFocused with the mouse elsewhere, so it's reconciled small
// within a frame. Skipped while a controller is in use, so controller focus still enlarges.
internal static class GridHoverGate
{
    // NClickableControl's *mouse* hover flag (set by MouseEntered/Exited). We clear the
    // stale value when correcting a card; we do NOT read it to decide (that's the bug).
    private static readonly FieldInfo? HitboxMouseHovered =
        AccessTools.Field(typeof(NClickableControl), "_isHovered");

    // The holder's own hover/focus bookkeeping (base NCardHolder).
    private static readonly FieldInfo? HolderIsHovered =
        AccessTools.Field(typeof(NCardHolder), "_isHovered");
    internal static readonly FieldInfo? HolderIsFocused =
        AccessTools.Field(typeof(NCardHolder), "_isFocused");
    private static readonly FieldInfo? HolderHoverTween =
        AccessTools.Field(typeof(NCardHolder), "_hoverTween");

    // If an essential private field vanished (e.g. a game update renamed it), disable the
    // reconcile and fall back to vanilla behavior rather than crash or wedge cards.
    internal static bool Viable =>
        HolderIsFocused != null && HolderIsHovered != null && HitboxMouseHovered != null;

    internal static bool UsingController()
    {
        try { return NControllerManager.Instance?.IsUsingController ?? false; }
        catch { return false; }
    }

    // Is the mouse genuinely inside this hitbox's current on-screen rect? Uses the full
    // canvas transform (which includes the holder's scale), so it's correct whether the
    // card is drawn small or popped to full size — unlike the stale _isHovered flag.
    internal static bool MouseActuallyInside(NClickableControl? hitbox)
    {
        if (hitbox == null || !hitbox.IsInsideTree() || !hitbox.IsVisibleInTree())
            return false;
        Viewport? vp = hitbox.GetViewport();
        if (vp == null)
            return false;
        Vector2 local = hitbox.GetGlobalTransformWithCanvas().AffineInverse() * vp.GetMousePosition();
        return new Rect2(Vector2.Zero, hitbox.Size).HasPoint(local);
    }

    // Drive a stuck-enlarged holder back to the clean, un-hovered small state.
    internal static void ForceUnhover(NCardHolder holder)
    {
        NClickableControl? hitbox = holder.Hitbox;
        if (hitbox != null)
            HitboxMouseHovered?.SetValue(hitbox, false);
        HolderIsHovered?.SetValue(holder, false);
        HolderIsFocused?.SetValue(holder, false);
        if (HolderHoverTween?.GetValue(holder) is Tween tween && GodotObject.IsInstanceValid(tween))
            tween.Kill();
        holder.Scale = holder.SmallScale; // patched getter -> the shrunk grid size

        // The game's normal unfocus (DoCardHoverEffects(false)) also calls ClearHoverTips();
        // we bypass that path, so remove this holder's hover-tip popup (keyword tips AND the
        // related-card previews) too — otherwise a stuck card shrinks but its popped cards
        // stay floating. Keyed on the holder, matching NCardHolder.CreateHoverTips(this).
        NHoverTipSet.Remove(holder);
    }

    // A few screens reuse NGridCardHolder but lay a handful of cards out in a fixed-spacing
    // row instead of a scrollable NCardGrid: the choose-a-card screen, the post-combat card
    // reward, and the unlock screen. Those aren't NCardGrid (so the reconcile never touches
    // them) and we also leave them full size here.
    internal static bool IsInFixedCardRow(Node node)
    {
        for (Node? p = node; p != null; p = p.GetParent())
        {
            if (p is NChooseACardSelectionScreen or NCardRewardSelectionScreen or NUnlockCardsScreen)
                return true;
        }
        return false;
    }
}

// Every frame the grid processes, snap any "big" card the mouse isn't really over back to
// small. Cheap: only cards that are currently enlarged (usually 0–1) get the hit test.
[HarmonyPatch(typeof(NCardGrid), "_Process")]
internal static class NCardGrid_Process_Reconcile_Patch
{
    private static void Postfix(NCardGrid __instance)
    {
        DeckModeController.PollHotkey();            // always — so the hotkey works in either mode
        if (!DeckModeController.MiniEnabled)        // reconcile is only meaningful when shrinking
            return;
        if (!GridHoverGate.Viable || GridHoverGate.UsingController())
            return;
        foreach (NGridCardHolder holder in __instance.CurrentlyDisplayedCardHolders)
        {
            if (holder == null)
                continue;
            if (GridHoverGate.HolderIsFocused!.GetValue(holder) is not true) // not enlarged -> nothing to fix
                continue;
            if (GridHoverGate.MouseActuallyInside(holder.Hitbox))            // genuinely hovered -> leave it
                continue;
            GridHoverGate.ForceUnhover(holder);                             // stuck big -> shrink
        }
    }
}

// Grid holders come from a pool; Create()/OnReturnedFromPool reset Scale but not the
// _isHovered/_isFocused flags. Scrub them on reuse so a recycled "hovered" card starts
// clean (avoids even a one-frame stale enlarge before the reconcile runs).
[HarmonyPatch(typeof(NGridCardHolder), "Create")]
internal static class Create_Reset_Patch
{
    private static void Postfix(NGridCardHolder __result)
    {
        if (__result != null && GridHoverGate.Viable)
            GridHoverGate.ForceUnhover(__result);
    }
}

// --- Map overview: zoom the whole act map out so it fits on screen at once --------------
//
// FIRST CUT — the goal here is to *see* the whole map so we can iterate on the layout. The
// whole map (points + paths + marker + drawings) lives under one node, `_mapContainer`
// ("TheMap"); the screen normally just slides it vertically between fixed scroll limits.
// This scales that node down to fit the map's bounding box into the viewport and pins it
// centered, so top-to-bottom is visible at once. Toggle with ToggleMapOverviewKey while the
// map is open. Session-only (resets on game restart) — deliberately simple while we tune it.
//
// It computes the bounds from the live map points each frame, so it adapts to any act size.
// Everything is null-guarded (Available); if a field name moved it simply does nothing.
internal static class MapOverviewController
{
    private static readonly FieldInfo? MapContainerField = AccessTools.Field(typeof(NMapScreen), "_mapContainer");
    private static readonly FieldInfo? PointsField = AccessTools.Field(typeof(NMapScreen), "_points");
    private static readonly FieldInfo? PointDictField = AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary");
    private static readonly FieldInfo? TargetDragField = AccessTools.Field(typeof(NMapScreen), "_targetDragPos");

    private static bool Available => MapContainerField != null && PointDictField != null;

    private static bool _enabled;
    private static bool _keyWasDown;

    internal static void Tick(NMapScreen screen)
    {
        if (!Available || !screen.IsVisibleInTree())
            return;

        bool down = Input.IsKeyPressed(DeckViewMod.ToggleMapOverviewKey);
        if (down && !_keyWasDown)
        {
            _enabled = !_enabled;
            Log.Info($"[DeckView] map overview {(_enabled ? "on" : "off")}");
            if (!_enabled)
                Restore(screen);
        }
        _keyWasDown = down;

        if (_enabled)
            Apply(screen);
    }

    private static void Apply(NMapScreen screen)
    {
        if (MapContainerField!.GetValue(screen) is not Control map)
            return;

        // Bounding box of all map points, in _mapContainer-local space. Points live under
        // `_points` (a child of _mapContainer), so add that child's offset.
        Vector2 pointsOffset = (PointsField?.GetValue(screen) as Control)?.Position ?? Vector2.Zero;
        if (PointDictField!.GetValue(screen) is not System.Collections.IDictionary dict || dict.Count == 0)
            return;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (object? value in dict.Values)
        {
            if (value is not Control pt || !GodotObject.IsInstanceValid(pt))
                continue;
            Vector2 topLeft = pointsOffset + pt.Position;
            Vector2 size = pt.Size * pt.Scale;
            minX = Math.Min(minX, topLeft.X);
            minY = Math.Min(minY, topLeft.Y);
            maxX = Math.Max(maxX, topLeft.X + size.X);
            maxY = Math.Max(maxY, topLeft.Y + size.Y);
        }

        float contentW = maxX - minX, contentH = maxY - minY;
        if (contentW <= 0f || contentH <= 0f)
            return;

        Vector2 viewport = screen.GetViewportRect().Size;
        float fit = DeckViewMod.MapOverviewFitFraction;
        float scale = Math.Min(viewport.X * fit / contentW, viewport.Y * fit / contentH);
        scale = Math.Clamp(scale, 0.1f, 1f); // only ever zoom out, never past 1:1

        // Place the content's center at the viewport's center.
        Vector2 contentCenter = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
        map.Scale = new Vector2(scale, scale);
        map.Position = viewport * 0.5f - contentCenter * scale;
    }

    private static void Restore(NMapScreen screen)
    {
        if (MapContainerField!.GetValue(screen) is Control map)
        {
            map.Scale = Vector2.One;
            map.Position = Vector2.Zero;
        }
        // Reset the scroll target so the normal view doesn't lerp toward a stale overview pos.
        TargetDragField?.SetValue(screen, Vector2.Zero);
    }
}

// Drive the map overview each frame the map screen processes. _Process isn't focus-gated
// (unlike the arrow-key scroll path), so the overview toggle works regardless of what has
// keyboard focus on the map.
[HarmonyPatch(typeof(NMapScreen), "_Process")]
internal static class NMapScreen_Process_Patch
{
    private static void Postfix(NMapScreen __instance) => MapOverviewController.Tick(__instance);
}
