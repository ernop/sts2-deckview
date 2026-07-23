using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeckView.Layout;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
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

    // O flips the map STYLE (flat <-> classic) in place — it is NOT global; it only does anything
    // while a map is already displayed. O = "overview". Change to taste.
    public const Key ToggleMiniMapKey = Key.O;

    // M is the ONE global map shortcut: from anywhere, it toggles the map's visibility. When it
    // opens the map, the map shows in whatever state the two checkboxes ("Flat map" / "Compress")
    // are configured to. M = "map".
    public const Key MapKey = Key.M;

    // Debug: log the live map graph (nodes+edges+assigned lanes) on open, so a real random level
    // can be reconstructed offline and fed to the layout test harness. Turn off once tuning ends.
    public const bool DumpMinimapGraph = true;

    // DeckView was built and tested against this game version — purely informational (logged
    // at load). We patch internal names (methods + private fields) that MegaCrit can rename
    // between builds, so this records the build we KNOW matches.
    //
    // POLICY: work or crash — never degrade. DeckView does NOT probe-and-disable or catch-and-
    // fall-back-to-vanilla. If a hook target or reflected member is gone (e.g. a game update
    // renamed it), patching throws and the mod crashes loudly, pointing at the broken member.
    // A silent fallback would hide exactly that. See DEVELOPMENT.md ("work or crash").
    public const string TestedGameVersion = "v0.109.0";

    private const string HarmonyId = "ernes.deckview";

    public static void Init()
    {
        // No try/catch, no hook probe: if a target is missing PatchAll throws and we crash —
        // that is the intended behavior. Reflected members (see Reflect below) throw at load
        // for the same reason. Do NOT wrap this to "leave the UI vanilla".
        new Harmony(HarmonyId).PatchAll(typeof(DeckViewMod).Assembly);

        string? gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        string versionNote = gameVersion == TestedGameVersion
            ? ""
            : $" — NOTE: game '{gameVersion ?? "unknown"}' is not the tested {TestedGameVersion}; re-verify";
        Log.Info($"[DeckView] loaded — card scale x{CardScaleFactor}, padding {CardPadding}px{versionNote}");
    }
}

// Reflection that fails loud. Every reflected member DeckView needs is resolved through here,
// so a missing/renamed member throws immediately (at type-init / load) instead of silently
// resolving to null and no-oping later. This is the "work or crash — never degrade" policy in
// code form: we want a game update that moves a member to crash pointing at that member, not a
// mod that quietly half-works.
internal static class Reflect
{
    internal static FieldInfo Field(Type type, string name) =>
        AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);

    internal static MethodInfo Method(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);

    internal static MethodInfo Method(Type type, string name, Type[] parameters) =>
        AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);

    internal static MethodInfo PropertyGetter(Type type, string name) =>
        AccessTools.PropertyGetter(type, name)
        ?? throw new MissingMethodException(type.FullName, $"get_{name}");
}

// Log-once diagnostics. The card/hover patches run every frame per card, so we can't log them
// unconditionally without flooding the log. Once(key, ...) logs a given key exactly once, which
// is enough to prove a hot path executed and sample its values. Rearm() re-arms all keys (called
// when a grid connects) so each fresh deck/library open logs one more sample.
internal static class Dbg
{
    private static readonly HashSet<string> _seen = new();
    internal static void Once(string key, string message)
    {
        if (_seen.Add(key)) Log.Info($"[DeckView] {message}");
    }
    internal static void Rearm() => _seen.Clear();
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
    private static bool _miniDeck = true;       // default: shrink (original behavior)
    private static bool _preferFlatMap = false; // default: the game's classic map

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

    // Which map style to show: true = DeckView's flat page, false = the game's classic map.
    internal static bool PreferFlatMap
    {
        get { EnsureLoaded(); return _preferFlatMap; }
        set
        {
            EnsureLoaded();
            if (_preferFlatMap == value) return;
            _preferFlatMap = value;
            Save();
        }
    }

    // On the flat page: true = vertically compressed layout (default), false = raw 1:1 with the
    // game's column layout (so a user can confirm compression changed nothing but spacing).
    private static bool _compressMap = true;
    internal static bool CompressMap
    {
        get { EnsureLoaded(); return _compressMap; }
        set
        {
            EnsureLoaded();
            if (_compressMap == value) return;
            _compressMap = value;
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        // Error.Ok check is normal flow (the file legitimately may not exist on first run) —
        // NOT error hiding. Any real read exception propagates (work-or-crash policy).
        var cfg = new ConfigFile();
        if (cfg.Load(Path) == Error.Ok)
        {
            _miniDeck = cfg.GetValue("deck", "mini", true).AsBool();
            _preferFlatMap = cfg.GetValue("map", "flat", false).AsBool();
            _compressMap = cfg.GetValue("map", "compress", true).AsBool();
        }
    }

    private static void Save()
    {
        var cfg = new ConfigFile();
        cfg.Load(Path); // preserve any other keys; a missing file is a benign non-Ok return
        cfg.SetValue("deck", "mini", _miniDeck);
        cfg.SetValue("map", "flat", _preferFlatMap);
        cfg.SetValue("map", "compress", _compressMap);
        cfg.Save(Path);
    }
}

internal static class DeckModeController
{
    private static readonly FieldInfo CardSizeField = Reflect.Field(typeof(NCardGrid), "_cardSize");
    private static readonly FieldInfo NeedsReinitField = Reflect.Field(typeof(NCardGrid), "_needsReinit");

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

    internal static void Toggle() => SetMini(!DeckViewConfig.MiniDeck);

    // Set mini mode to an explicit value and rebuild every live grid to match. Used by both the
    // hotkey (Toggle) and the on-screen "View mini-cards" tickbox, so they stay in agreement.
    internal static void SetMini(bool on)
    {
        if (DeckViewConfig.MiniDeck == on)
            return; // already there — nothing to rebuild
        DeckViewConfig.MiniDeck = on;
        MiniCardsToggle_Patch.SyncAll(on); // keep the on-screen checkbox(es) in agreement with the hotkey
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
            CardSizeField.SetValue(grid, kv.Value * factor);
            NeedsReinitField.SetValue(grid, true);
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
        Vector2 vanilla = ____cardSize;
        DeckModeController.Register(__instance, vanilla);
        Dbg.Rearm(); // new grid connected -> re-arm the once-logs so this open logs fresh samples
        if (DeckModeController.MiniEnabled)
            ____cardSize *= DeckViewMod.CardScaleFactor;
        Log.Info($"[DeckView] grid connected: vanilla cardSize={vanilla}, mini={DeckModeController.MiniEnabled}, " +
                 $"final cardSize={____cardSize}");
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
        {
            __result *= DeckViewMod.CardScaleFactor;
            Dbg.Once("smallscale", $"SmallScale shrink active (x{DeckViewMod.CardScaleFactor}) -> {__result}");
        }
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

// --- Visible "Mini-cards" toggle in the deck-view control cluster -------------------------
//
// A discoverable on-screen counterpart to the T hotkey, wired to DeckModeController.
//
// We build our OWN control (a self-drawn ToggleSwitch) rather than cloning the game's
// "View upgrades" NTickbox. Cloning that tickbox does NOT work: it's inlined in the deck-view
// scene and its visuals are addressed by scene-unique names ("%TickboxVisuals") OWNED BY THE
// SCREEN, so a duplicated-and-reparented copy can't resolve them — its ConnectSignals throws
// NullReferenceException, which (as a postfix of NCardsViewScreen.ConnectSignals) aborts the
// whole deck-screen build and makes every card disappear. ToggleSwitch has no such scene
// coupling. Placement is anchored to the "View upgrades" control and still needs tuning (its real
// layout lives in the .tscn); the resolved position is logged so we can dial it in.
[HarmonyPatch(typeof(NCardsViewScreen), "ConnectSignals")]
internal static class MiniCardsToggle_Patch
{
    private static readonly FieldInfo ShowUpgradesField = Reflect.Field(typeof(NCardsViewScreen), "_showUpgrades");
    private const string AddedMeta = "deckview_minicards_toggle";

    // Live switches, so the T hotkey and the on-screen toggle always agree (SyncAll below).
    private static readonly List<ToggleSwitch> _toggles = new();

    // First-pass placement relative to the "View upgrades" control. Tune from the logged pos.
    public static readonly Vector2 ToggleOffset = new(0f, -52f);

    private static void Postfix(NCardsViewScreen __instance)
    {
        if (__instance.HasMeta(AddedMeta))
            return;
        __instance.SetMeta(AddedMeta, true);

        var upgrades = ShowUpgradesField.GetValue(__instance) as Control; // may be null on some screens

        // Match the game's "View upgrades" label — and share the size with EVERY toggle so they all
        // read identically (item C). The label's theme font_size is a LOGICAL size; the control is
        // scaled by its ancestors, so the on-screen size is font_size * canvas-scale. We use that
        // effective pixel size so our (unscaled) toggle matches what the eye sees.
        if (__instance.GetNodeOrNull("%ViewUpgradesLabel") is Control lbl)
        {
            int f = lbl.GetThemeFontSize("font_size");
            float scale = lbl.GetGlobalTransformWithCanvas().Scale.Y;
            int eff = Mathf.RoundToInt(f * Mathf.Clamp(scale, 0.2f, 1f));
            Log.Info($"[DeckView] upgrades label font={f} canvasScale={scale:0.###} -> effFont={eff}");
            if (eff > 0) GameStyle.ToggleFontSize = eff;
        }

        var toggle = new ToggleSwitch("Mini-cards", DeckModeController.MiniEnabled, OnMiniToggled)
        {
            Name = "DeckViewMiniCardsToggle",
            ZIndex = 50,
        };
        __instance.AddChild(toggle);
        if (upgrades != null && GodotObject.IsInstanceValid(upgrades))
            toggle.GlobalPosition = upgrades.GlobalPosition + ToggleOffset;
        _toggles.Add(toggle);
        Log.Info($"[DeckView] mini-cards toggle at {toggle.GlobalPosition} size={toggle.Size} " +
                 $"fontSize={GameStyle.ToggleFontSize} toggleCanvasScale={toggle.GetGlobalTransformWithCanvas().Scale.Y:0.###} " +
                 $"(upgrades at {(upgrades != null ? upgrades.GlobalPosition + " size=" + upgrades.Size : "n/a")})");
    }

    // Reflect the current mode onto every live switch WITHOUT re-firing the callback (so a T-key
    // flip updates the on-screen toggle, and vice-versa, with no feedback loop). Called by SetMini.
    internal static void SyncAll(bool on)
    {
        _toggles.RemoveAll(t => !GodotObject.IsInstanceValid(t));
        foreach (ToggleSwitch t in _toggles)
            t.SetOn(on);
    }

    private static void OnMiniToggled(bool pressed) => DeckModeController.SetMini(pressed);
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
    private static readonly FieldInfo HitboxMouseHovered =
        Reflect.Field(typeof(NClickableControl), "_isHovered");

    // The holder's own hover/focus bookkeeping (base NCardHolder).
    private static readonly FieldInfo HolderIsHovered = Reflect.Field(typeof(NCardHolder), "_isHovered");
    internal static readonly FieldInfo HolderIsFocused = Reflect.Field(typeof(NCardHolder), "_isFocused");
    private static readonly FieldInfo HolderHoverTween = Reflect.Field(typeof(NCardHolder), "_hoverTween");

    // The game's own un-hover entry point. RefreshFocusState() re-reads CanBeFocused (== the
    // holder's _isHovered) and, when it changed, flips _isFocused and calls DoCardHoverEffects,
    // which on false: kills the hover tween, starts the normal shrink tween, AND calls
    // ClearHoverTips() -> NHoverTipSet.Remove(this). Driving this is how we dismiss a card's
    // popup EXACTLY the way vanilla does (both keyword tips and related-card previews live in
    // the one NHoverTipSet keyed by the holder).
    private static readonly MethodInfo RefreshFocusStateMethod =
        Reflect.Method(typeof(NCardHolder), "RefreshFocusState");

    // Controller in use? The reconcile is skipped then, so controller focus still enlarges.
    // The Instance null-check is legitimate state (not error hiding): no controller manager
    // yet => treat as not using a controller. Any other failure propagates.
    internal static bool UsingController() => NControllerManager.Instance?.IsUsingController ?? false;

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

    // Drive a stuck-enlarged holder back to the clean, un-hovered small state — using the
    // game's OWN un-hover path so the shrink and, crucially, the popup dismissal are identical
    // to what happens when you normally move the mouse off a card. (The previous version
    // hand-rolled the shrink and called NHoverTipSet.Remove directly; that bypassed the game's
    // ClearHoverTips path and could leave the keyword tip / related-card preview floating.)
    internal static void ForceUnhover(NCardHolder holder)
    {
        // Clear the stale ground-truth mouse bit on the hitbox (the ROOT of the stuck-hover
        // bug: Godot never fired MouseExited, so this stayed true). Must clear it or (a) the
        // holder can't be focused again on a real future hover, and (b) RefreshFocusState below
        // wouldn't see "not hovered".
        if (holder.Hitbox is NClickableControl hitbox)
            HitboxMouseHovered.SetValue(hitbox, false);
        HolderIsHovered.SetValue(holder, false);

        // Now run the game's real un-hover: RefreshFocusState() sees _isHovered==false, flips
        // _isFocused to false, and calls DoCardHoverEffects(false) -> normal shrink tween +
        // ClearHoverTips() -> NHoverTipSet.Remove(this). This frees the whole tip set (keyword
        // tips AND related-card previews) exactly as vanilla does. The reconcile only calls us
        // for holders that are currently _isFocused, so this always drives the full dismissal.
        RefreshFocusStateMethod.Invoke(holder, null);

        // Safety net for any path where the holder was already un-focused but a tip set is
        // still registered under it (RefreshFocusState would short-circuit). Idempotent.
        NHoverTipSet.Remove(holder);
    }

    // Lightweight flag scrub for pooled reuse (NGridCardHolder.Create). The holder may not be
    // in the tree yet, so we must NOT start a tween or drive DoCardHoverEffects — just clear
    // any stale hover/focus bits and drop a stray tip so a recycled card starts clean.
    internal static void ScrubHoverFlags(NCardHolder holder)
    {
        if (holder.Hitbox is NClickableControl hitbox)
            HitboxMouseHovered.SetValue(hitbox, false);
        HolderIsHovered.SetValue(holder, false);
        HolderIsFocused.SetValue(holder, false);
        if (HolderHoverTween.GetValue(holder) is Tween tween && GodotObject.IsInstanceValid(tween))
            tween.Kill();
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
        if (GridHoverGate.UsingController())
            return;
        foreach (NGridCardHolder holder in __instance.CurrentlyDisplayedCardHolders)
        {
            if (holder == null)
                continue;
            if (GridHoverGate.HolderIsFocused.GetValue(holder) is not true) // not enlarged -> nothing to fix
                continue;
            if (GridHoverGate.MouseActuallyInside(holder.Hitbox))            // genuinely hovered -> leave it
                continue;
            Dbg.Once("reconcile", "hover reconcile fired: forcing a stuck-big card back to small " +
                                   "+ dismissing its popup via the game's own un-hover path");
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
        if (__result != null)
            GridHoverGate.ScrubHoverFlags(__result); // in-tree not guaranteed here -> no tween
    }
}

// --- Minimap: a compact, glance-able node-graph rendering of the whole act ---------------
//
// This REPLACES the old "zoom the real map out" overview, which couldn't work: NMapScreen's
// _Process rewrites _mapContainer.Position every frame (lerping toward _targetDragPos, which
// is itself clamped to [-600, 1800]) and there is no zoom field — so any transform we set on
// the real map is fought or clamped away within a frame.
//
// Instead the minimap is a real top-level PAGE: MiniMapScreen implements ICapstoneScreen and is
// opened through NCapstoneContainer, exactly like the deck-view screen. The game then supplies the
// standard top bar, the dimmed backstop, combat pause, "current screen" focus/controller routing,
// and native ESC/back — we don't hand-roll layering or input. We render the whole act as a node
// graph (one dot per point, colored by room type with the game's own icon, edges drawn, laid out
// LEFT->RIGHT and vertically flattened by MapLayout, current position highlighted) plus an info
// panel. It only READS the live map graph; it never touches the real map's data or nodes.
internal static class MiniMapController
{
    // _mapPointDictionary is Dictionary<MapCoord, NMapPoint> and already contains EVERY on-screen
    // point (normal + boss + second boss + start), so it's the one handle we need for the graph.
    private static readonly FieldInfo PointDictField = Reflect.Field(typeof(NMapScreen), "_mapPointDictionary");
    private static readonly FieldInfo RunStateField = Reflect.Field(typeof(NMapScreen), "_runState");
    // Whether you can travel RIGHT NOW (the current room is finished). False while you still have to
    // complete the room you're in — in that case there are no legal moves yet, so we neither dim the
    // current node "done" nor highlight the downstream options.
    private static readonly MethodInfo TravelEnabledGetter = Reflect.PropertyGetter(typeof(NMapScreen), "IsTravelEnabled");
    // Recomputes each point's travel State from the run state. The game runs this inside Open(); we
    // call it ourselves so the flat page has fresh travelability WITHOUT ever opening the classic map.
    private static readonly MethodInfo RecalcTravelMethod = Reflect.Method(typeof(NMapScreen), "RecalculateTravelability", Type.EmptyTypes);
    // The game's "you are here" arrow — NMapMarker (a TextureRect) whose .Texture is the current
    // character's MapMarker art. We read that live texture so our page marks the current node the
    // exact way the real map does. (Null in multiplayer, where the game suppresses the marker.)
    private static readonly FieldInfo MarkerField = Reflect.Field(typeof(NMapScreen), "_marker");
    // CurrentMapCoord (MapCoord?) tells us where the player is now (null before the first move).
    private static readonly MethodInfo CurrentCoordGetter =
        Reflect.PropertyGetter(RunStateField.FieldType, "CurrentMapCoord");

    // For the info panel: act index/floor + the act's display name (Act.Title is a LocString;
    // GetFormattedText() localizes it, e.g. "The Underdocks"). Resolved via ReturnType chaining
    // so we don't hardcode the ActModel/LocString namespaces.
    private static readonly MethodInfo ActIndexGetter = Reflect.PropertyGetter(RunStateField.FieldType, "CurrentActIndex");
    private static readonly MethodInfo ActFloorGetter = Reflect.PropertyGetter(RunStateField.FieldType, "ActFloor");
    private static readonly MethodInfo ActGetter = Reflect.PropertyGetter(RunStateField.FieldType, "Act");
    private static readonly MethodInfo ActTitleGetter = Reflect.PropertyGetter(ActGetter.ReturnType, "Title");
    private static readonly MethodInfo LocStringFormat = Reflect.Method(ActTitleGetter.ReturnType, "GetFormattedText", Type.EmptyTypes);

    // MapPointHistory[actIndex][row].Rooms.First().RoomType records what a visited "?" node turned
    // out to be. We resolve that so a revealed Unknown reads (colour + tally) as its real room type,
    // mirroring the game revealing the "?" icon on entry. Nested Rooms/RoomType are resolved on the
    // runtime types (fail-loud) the first time we touch one.
    private static readonly MethodInfo MapHistoryGetter = Reflect.PropertyGetter(RunStateField.FieldType, "MapPointHistory");
    private static MethodInfo? _roomsGet, _roomTypeGet;

    private static MapPointType ResolveUnknown(object runState, int actIndex, int row)
    {
        if (MapHistoryGetter.Invoke(runState, null) is not System.Collections.IList hist
            || actIndex < 0 || actIndex >= hist.Count
            || hist[actIndex] is not System.Collections.IList rows
            || row < 0 || row >= rows.Count || rows[row] is not object entry)
            return MapPointType.Unknown; // not yet recorded — leave it as "?"
        _roomsGet ??= Reflect.PropertyGetter(entry.GetType(), "Rooms");
        if (_roomsGet.Invoke(entry, null) is not System.Collections.IList rooms || rooms.Count == 0 || rooms[0] is not object room)
            return MapPointType.Unknown;
        _roomTypeGet ??= Reflect.PropertyGetter(room.GetType(), "RoomType");
        return _roomTypeGet.Invoke(room, null)?.ToString() switch
        {
            "Monster" => MapPointType.Monster,
            "Elite" => MapPointType.Elite,
            "Shop" => MapPointType.Shop,
            "Treasure" => MapPointType.Treasure,
            "RestSite" => MapPointType.RestSite,
            _ => MapPointType.Unknown,
        };
    }

    // Each on-screen point renders its room icon into a TextureRect field named "_icon". We grab
    // that live texture so the minimap shows the EXACT same icon art the real map does (including
    // "?" nodes that have resolved to a real room). Boss points are a different class with no
    // _icon (Spine/placeholder art) — those fall back to the "B" letter glyph.
    private static readonly FieldInfo NormalIconField = Reflect.Field(typeof(NNormalMapPoint), "_icon");
    private static readonly FieldInfo AncientIconField = Reflect.Field(typeof(NAncientMapPoint), "_icon");
    // Boss nodes have no _icon; the (non-Spine) act art lives in a "%PlaceholderImage" TextureRect.
    private static readonly FieldInfo BossImageField = Reflect.Field(typeof(NBossMapPoint), "_placeholderImage");

    private static Texture2D? IconOf(NMapPoint np)
    {
        TextureRect? rect = np switch
        {
            NNormalMapPoint => NormalIconField.GetValue(np) as TextureRect,
            NAncientMapPoint => AncientIconField.GetValue(np) as TextureRect,
            NBossMapPoint => BossImageField.GetValue(np) as TextureRect, // real boss art (if not Spine-only)
            _ => null, // -> letter fallback
        };
        return rect != null && GodotObject.IsInstanceValid(rect) ? rect.Texture : null;
    }

    private const string MapToggleMeta = "deckview_mapstyle_toggle";
    private static MiniMapScreen? _screen;      // reused capstone instance, reconfigured per open
    private static ToggleSwitch? _mapToggle;    // the "Flat map" toggle bolted onto the classic map

    // Is our flat page the currently-shown capstone?
    private static bool FlatOpen()
    {
        NCapstoneContainer? cc = NCapstoneContainer.Instance;
        return cc != null && _screen != null && ReferenceEquals(cc.CurrentCapstoneScreen, _screen);
    }

    // Runs while the CLASSIC map is processing (classic mode only — in flat mode NMapScreen is never
    // opened, so this doesn't run). Its only job is to mount the "Flat map" checkbox on the classic
    // map and keep it hidden under any capstone. The M/O keys are polled globally in GlobalTick.
    internal static void Tick(NMapScreen screen)
    {
        if (!screen.IsVisibleInTree()) return;
        EnsureMapToggle(screen);
        if (_mapToggle != null && GodotObject.IsInstanceValid(_mapToggle))
            _mapToggle.Visible = NCapstoneContainer.Instance?.CurrentCapstoneScreen == null;
    }

    // The map is bound to a game key (M by default). We intercept that key in NInputManager and
    // route it here, suppressing the vanilla action so ONLY this fires — no more double-toggle with
    // the game's own map button. Fired once per press (from the shortcut prefix).
    internal static void OnMapKey() => ToggleMap();

    // O flips the MODE (flat<->classic), only meaningful while a map is showing.
    internal static void OnFlipKey()
    {
        if (MapShown()) SetFlat(!DeckViewConfig.PreferFlatMap);
    }

    // Is the map showing, in EITHER mode? Flat mode -> our capstone; classic mode -> NMapScreen.IsOpen.
    // (In flat mode the classic screen is never opened, so IsOpen stays false — the two are exclusive.)
    internal static bool MapShown() => FlatOpen() || (NMapScreen.Instance?.IsOpen ?? false);

    // We deliberately closed the map on this process frame — used to swallow the map room's
    // synchronous ReopenMap (fired on capstone-close) so a deliberate close actually stays closed.
    private static ulong _closeFrame = ulong.MaxValue;
    internal static bool SuppressReopenThisFrame() => Engine.GetProcessFrames() == _closeFrame;

    // HOTKEY-COLLISION SELF-CHECK (runs once). For each key WE use, log which game action (if any)
    // is bound to the same physical key. A surprise collision (like map==M) is then obvious in the
    // log at a glance instead of a 50-minute debugging spiral. Diagnostic-only: tolerant of a missing
    // field (never crashes the mod over logging).
    private static bool _keyAuditDone;
    internal static void AuditKeysOnce(NInputManager mgr)
    {
        if (_keyAuditDone) return;
        _keyAuditDone = true;
        var field = typeof(NInputManager).GetField("_keyboardInputMap",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var gameMap = field?.GetValue(mgr) as System.Collections.IDictionary;
        (Key key, string label)[] ours =
            { (DeckViewMod.MapKey, "map"), (DeckViewMod.ToggleMiniMapKey, "flip/O"), (DeckViewMod.ToggleDeckModeKey, "deck/T") };
        var sb = new System.Text.StringBuilder("[DeckView] KEY AUDIT (our key -> colliding game action):");
        foreach (var (key, label) in ours)
        {
            var hits = new List<string>();
            if (gameMap != null)
                foreach (System.Collections.DictionaryEntry e in gameMap)
                    if (e.Value is Key gk && gk == key) hits.Add(e.Key?.ToString() ?? "?");
            sb.Append($"  {label}={key}->{(hits.Count > 0 ? string.Join("+", hits) : "none")}");
        }
        sb.Append(gameMap == null ? "  (game keymap unavailable)" : "");
        Log.Info(sb.ToString());
    }

    // Compact one-line snapshot, for tracing.
    private static string MapState(string where)
    {
        NMapScreen? s = NMapScreen.Instance;
        string cap = NCapstoneContainer.Instance?.CurrentCapstoneScreen?.GetType().Name ?? "none";
        return $"[DeckView] {where}: IsOpen={s?.IsOpen} flatOpen={FlatOpen()} " +
               $"prefFlat={DeckViewConfig.PreferFlatMap} capstone={cap}";
    }

    // Map key: if the map is showing (either mode) -> close it; else -> open it. Opening just calls
    // the game's Open(); our Open PREFIX renders it in the configured mode (flat page, or classic).
    private static void ToggleMap()
    {
        NMapScreen? screen = NMapScreen.Instance;
        if (screen == null) return;
        Log.Info(MapState("map key"));
        if (MapShown()) CloseMapCompletely();
        else screen.Open();
    }

    // Leave the map entirely -> prior view. Only ONE mode is ever open, so we close exactly that one.
    // Stamp the frame so the map room's ReopenMap (fired synchronously on capstone-close) is swallowed
    // by the Open prefix — otherwise a deliberate close would instantly bounce back open.
    internal static void CloseMapCompletely()
    {
        Log.Info(MapState("close"));
        _closeFrame = Engine.GetProcessFrames();
        if (FlatOpen()) NCapstoneContainer.Instance?.Close(); // flat mode: classic was never opened
        else NMapScreen.Instance?.Close();                    // classic mode
    }

    // Switch MODE (flat<->classic), persist it, sync the checkboxes, and re-render the *currently
    // showing* map in the new mode. Never leaves the other mode visible for a frame.
    internal static void SetFlat(bool flat)
    {
        Log.Info($"[DeckView] SetFlat({flat}) <- {MapState("setflat")}");
        DeckViewConfig.PreferFlatMap = flat;
        MapStyleToggle.SyncAll(flat);
        NMapScreen? screen = NMapScreen.Instance;
        NCapstoneContainer? cc = NCapstoneContainer.Instance;
        if (screen == null || cc == null) return;
        if (flat)
        {
            if (screen.IsOpen) screen.Close(false); // drop classic instantly (no fade) — no flash
            if (!FlatOpen()) OpenFlat(screen);
        }
        else
        {
            if (FlatOpen()) cc.Close();             // drop the flat page
            if (!screen.IsOpen) screen.Open();      // classic opens (the Open prefix now allows it)
        }
    }

    // Rebuild + redraw the flat page in place (used when the game changes travelability while the
    // flat page is already showing — e.g. a map room enables travel just after we opened).
    internal static void RefreshFlat()
    {
        if (!FlatOpen() || _screen == null || !GodotObject.IsInstanceValid(_screen)) return;
        NMapScreen? screen = NMapScreen.Instance;
        if (screen == null) return;
        RecalcTravelMethod.Invoke(screen, null);
        _screen.Configure(BuildModel(screen), screen.GetViewportRect().Size, coord => Travel(screen, coord));
        _screen.QueueRedraw();
    }

    // Add the "Flat map" toggle switch onto the classic map screen (once).
    private static void EnsureMapToggle(NMapScreen screen)
    {
        if (screen.HasMeta(MapToggleMeta)) return;
        screen.SetMeta(MapToggleMeta, true);
        ToggleSwitch box = MapStyleToggle.Create();
        _mapToggle = box;
        screen.AddChild(box);
        // Bottom-left — the SAME spot the flat page puts its "Flat map" toggle, so it reads as one
        // control across the two views.
        Vector2 vp = screen.GetViewportRect().Size;
        box.Position = new Vector2(vp.X * 0.04f, vp.Y * 0.80f);
    }

    // Open the minimap as a capstone screen — the game parents/shows it, dims the map behind, keeps
    // the top bar, pauses combat, and routes focus/ESC to it, exactly like the deck-view screen.
    // Entry point from the NMapScreen.Open prefix: render the flat page instead of the classic map.
    // Guarded so repeated Open() calls (e.g. a map room's reopen-on-capstone-close) don't stack.
    internal static void OpenFlatFromHook(NMapScreen screen)
    {
        if (FlatOpen()) return;
        OpenFlat(screen);
    }

    private static void OpenFlat(NMapScreen screen)
    {
        NCapstoneContainer? cc = NCapstoneContainer.Instance;
        if (cc == null) return; // not in a run / no capstone container -> nothing to open into

        // The classic map is NEVER opened in flat mode, so refresh travel state ourselves (Open()
        // normally does this) before reading it — the point dictionary is already built at act start.
        RecalcTravelMethod.Invoke(screen, null);

        MiniMapModel model = BuildModel(screen);
        Vector2 viewport = screen.GetViewportRect().Size;
        Log.Info($"[DeckView] flat open: nodes={model.Nodes.Count} edges={model.Edges.Count} " +
                 $"viewport={viewport} current={(model.Current?.ToString() ?? "none")} " +
                 $"act={model.ActIndex + 1}:'{model.ActName}' floor={model.ActFloor} travelEnabled={model.TravelEnabled}");

        if (_screen == null || !GodotObject.IsInstanceValid(_screen))
            _screen = new MiniMapScreen();
        _screen.Configure(model, viewport, coord => Travel(screen, coord));
        cc.Open(_screen);
    }

    // Clicking a travelable node runs the game's own selection path (identical to a real click),
    // then closes the page so the travel animates on the real map.
    private static void Travel(NMapScreen screen, MapCoord coord)
    {
        if (PointDictField.GetValue(screen) is System.Collections.IDictionary dict
            && dict[coord] is NMapPoint np && GodotObject.IsInstanceValid(np))
        {
            Log.Info($"[DeckView] minimap travel -> {coord}");
            screen.OnMapPointSelectedLocally(np);
        }
        NCapstoneContainer.Instance?.Close();
    }

    // Compacted display lane per map coord, cached per act-map signature (see ComputeLanes).
    private static readonly Dictionary<string, Dictionary<MapCoord, int>> _laneCache = new();

    private static MiniMapModel BuildModel(NMapScreen screen)
    {
        object runState = RunStateField.GetValue(screen)!;
        object act = ActGetter.Invoke(runState, null)!;
        var model = new MiniMapModel
        {
            Current = CurrentCoordGetter.Invoke(runState, null) is MapCoord c ? c : null,
            ActIndex = (int)ActIndexGetter.Invoke(runState, null)!,
            ActFloor = (int)ActFloorGetter.Invoke(runState, null)!,
            ActName = (string)LocStringFormat.Invoke(ActTitleGetter.Invoke(act, null), null)! ?? "",
            CurrentMarker = (MarkerField.GetValue(screen) as TextureRect)?.Texture,
            TravelEnabled = (bool)TravelEnabledGetter.Invoke(screen, null)!,
        };

        if (PointDictField.GetValue(screen) is not System.Collections.IDictionary dict)
            return model;

        // Pass 1: read the live graph (coords, type, state, icon) and its edges.
        var raw = new List<(MapCoord coord, MapPointType type, MapPointState state, Texture2D? icon)>();
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            if (entry.Value is not NMapPoint np || !GodotObject.IsInstanceValid(np))
                continue;
            MapPoint mp = np.Point;
            if (mp == null)
                continue;
            raw.Add((mp.coord, mp.PointType, np.State, IconOf(np)));
            foreach (MapPoint child in mp.Children)
                model.Edges.Add((mp.coord, child.coord));
        }

        // Pass 2: flatten the layout (pure MapLayout, cached per act-map), then place nodes at the
        // computed lane instead of the raw game column.
        Dictionary<MapCoord, int> lane = ComputeLanes(raw.Select(r => r.coord), model.Edges);
        // Reachability seed = where the game says you can move RIGHT NOW (every point it flagged
        // Travelable) plus the current node. This is already relic-aware (e.g. Wing Boots widen the
        // Travelable set), so BFS-forward from it greys out only what you genuinely can't still reach.
        var seeds = new List<MapCoord>();
        if (model.Current is MapCoord cur) seeds.Add(cur);
        foreach (var r in raw)
            if (r.state == MapPointState.Travelable) seeds.Add(r.coord);
        HashSet<MapCoord> reachable = ReachableFrom(seeds, model.Edges);
        var unknownReveals = new List<string>();
        foreach (var r in raw)
        {
            // A visited "?" has revealed its real room — colour/tally it as that type.
            MapPointType eff = r.type;
            if (r.type == MapPointType.Unknown && r.state == MapPointState.Traveled)
            {
                eff = ResolveUnknown(runState, model.ActIndex, r.coord.row);
                unknownReveals.Add($"{r.coord.row},{r.coord.col}->{eff}");
            }
            if (r.icon != null && GodotObject.IsInstanceValid(r.icon))
                model.TypeIcons.TryAdd(eff, r.icon); // a representative icon per type, for the tally
            model.Nodes[r.coord] = new MiniNode
            {
                Coord = r.coord, Type = r.type, EffType = eff, State = r.state, Icon = r.icon,
                Lane = lane.TryGetValue(r.coord, out int ly) ? ly : r.coord.col,
                RawLane = r.coord.col,
                // Reachable = can still be travelled to from where we are (or no current pos yet).
                Reachable = model.Current is null || reachable.Contains(r.coord),
            };
        }
        if (DeckViewMod.DumpMinimapGraph)
        {
            DumpGraph(raw, model.Edges, lane);
            Log.Info($"[DeckView] visited-? reveals: {(unknownReveals.Count > 0 ? string.Join(" ", unknownReveals) : "(none)")}");
        }
        return model;
    }

    // Forward-reachable set: BFS down the edges (parent -> child) from every seed. Seeds are the
    // nodes you can legally step to now (Travelable, relic-aware) plus the current node — so the set
    // is exactly "rooms you can still get to". Everything else you didn't visit is a dead option.
    private static HashSet<MapCoord> ReachableFrom(IEnumerable<MapCoord> seeds, List<(MapCoord From, MapCoord To)> edges)
    {
        var set = new HashSet<MapCoord>();
        var adj = new Dictionary<MapCoord, List<MapCoord>>();
        foreach (var (f, t) in edges)
        {
            if (!adj.TryGetValue(f, out List<MapCoord>? outs)) adj[f] = outs = new List<MapCoord>();
            outs.Add(t);
        }
        var queue = new Queue<MapCoord>();
        foreach (MapCoord s in seeds)
            if (set.Add(s)) queue.Enqueue(s);
        while (queue.Count > 0)
        {
            MapCoord n = queue.Dequeue();
            if (!adj.TryGetValue(n, out List<MapCoord>? kids)) continue;
            foreach (MapCoord k in kids)
                if (set.Add(k)) queue.Enqueue(k);
        }
        return set;
    }

    // Log the live graph so a real random level can be reconstructed offline and dropped into the
    // layout test harness (layout/Program.cs) as a real-world case.
    private static void DumpGraph(
        List<(MapCoord coord, MapPointType type, MapPointState state, Texture2D? icon)> raw,
        List<(MapCoord From, MapCoord To)> edges, Dictionary<MapCoord, int> lane)
    {
        var nb = new System.Text.StringBuilder("[DeckView] MAPDUMP nodes(row,col,type):");
        foreach (var r in raw.OrderBy(r => r.coord.row).ThenBy(r => r.coord.col))
            nb.Append($" {r.coord.row},{r.coord.col},{r.type}");
        Log.Info(nb.ToString());
        var eb = new System.Text.StringBuilder("[DeckView] MAPDUMP edges(row,col->row,col):");
        foreach (var (f, t) in edges.OrderBy(e => e.From.row).ThenBy(e => e.From.col))
            eb.Append($" {f.row},{f.col}->{t.row},{t.col}");
        Log.Info(eb.ToString());
        var lb = new System.Text.StringBuilder("[DeckView] MAPDUMP lanes(row,col=lane):");
        foreach (var kv in lane.OrderBy(k => k.Key.row).ThenBy(k => k.Key.col))
            lb.Append($" {kv.Key.row},{kv.Key.col}={kv.Value}");
        Log.Info(lb.ToString());
    }

    // Run the pure flatten algorithm on this level's graph and return coord -> display lane. Cached
    // by a signature of the coords+edges, so each random act-map is solved once and reused.
    private static Dictionary<MapCoord, int> ComputeLanes(
        IEnumerable<MapCoord> coords, List<(MapCoord From, MapCoord To)> edges)
    {
        MapCoord[] cs = coords.ToArray();
        var sig = new System.Text.StringBuilder();
        foreach (MapCoord c in cs.OrderBy(c => c.row).ThenBy(c => c.col))
            sig.Append(c.col).Append(',').Append(c.row).Append(';');
        sig.Append('|');
        foreach (var (f, t) in edges.OrderBy(e => e.From.row).ThenBy(e => e.From.col)
                                    .ThenBy(e => e.To.row).ThenBy(e => e.To.col))
            sig.Append(f.col).Append(',').Append(f.row).Append('-').Append(t.col).Append(',').Append(t.row).Append(';');
        string key = sig.ToString();
        if (_laneCache.TryGetValue(key, out Dictionary<MapCoord, int>? cached))
            return cached;

        var idOf = new Dictionary<MapCoord, int>();
        var nodes = new List<LNode>();
        foreach (MapCoord c in cs)
            if (!idOf.ContainsKey(c)) { idOf[c] = nodes.Count; nodes.Add(new LNode(nodes.Count, c.row, c.col)); }

        var ledges = new List<(int, int)>();
        foreach (var (f, t) in edges)
        {
            if (!idOf.TryGetValue(f, out int fi) || !idOf.TryGetValue(t, out int ti))
                continue; // endpoint not in the point set (shouldn't happen) -> nothing to draw
            // Orient parent(lower row) -> child(higher row); LGraph requires a strict layering.
            if (nodes[fi].Row <= nodes[ti].Row) ledges.Add((fi, ti));
            else ledges.Add((ti, fi));
        }

        var graph = new LGraph(nodes, ledges);
        int[] lanes = MapLayout.AssignLanes(graph);

        // Runtime safety net: the layout is legal by construction, but never DRAW a misleading map.
        // If a lane assignment ever overlaps or reorders a row, crash loud instead (work-or-crash).
        List<string> violations = LayoutInvariants.Check(graph, lanes);
        if (violations.Count > 0)
            throw new InvalidOperationException(
                "[DeckView] minimap layout produced an ILLEGAL placement: " + string.Join("; ", violations));

        var result = new Dictionary<MapCoord, int>();
        foreach (var kv in idOf) result[kv.Key] = lanes[kv.Value];
        _laneCache[key] = result;
        Log.Info($"[DeckView] minimap layout: {nodes.Count} nodes, {ledges.Count} edges -> " +
                 $"{lanes.Distinct().Count()} lanes (cached)");
        return result;
    }
}

// Reuse the game's actual UI assets so our own controls match its look. There's no global Godot
// theme to inherit, so we load the same resources by res:// path from the pck: the Kreon UI font
// and the real checkbox art. This is the "use their CSS" equivalent — same assets, our elements.
internal static class GameStyle
{
    internal static readonly Color TextColor = new("FFF6E2"); // StsColors.cream — primary UI text
    internal static Font? Font { get; private set; }
    internal static Texture2D? Ticked { get; private set; }
    internal static Texture2D? Unticked { get; private set; }
    // Shared label size for ALL our toggles so they match each other; seeded to the game's "View
    // upgrades" size once the deck view is opened (see MiniCardsToggle_Patch).
    internal static int ToggleFontSize = 18;
    private static bool _loaded;

    internal static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Font = GD.Load<Font>("res://themes/kreon_regular_shared.tres");
        Ticked = GD.Load<Texture2D>("res://images/atlases/ui_atlas.sprites/checkbox_ticked.tres");
        Unticked = GD.Load<Texture2D>("res://images/atlases/ui_atlas.sprites/checkbox_unticked.tres");
        Log.Info($"[DeckView] game style: font={Font != null}, ticked={Ticked != null}, unticked={Unticked != null}");
    }
}

// Our own toggle element that wears the game's look: the game's real checkbox art (ticked/unticked)
// + the Kreon font, loaded via GameStyle. If the art fails to load it falls back to a simple drawn
// pill so it still works. Draws via the Draw signal and flips on click via gui_input (source-gen
// safe, same approach as the minimap page).
internal sealed partial class ToggleSwitch : Control
{
    private bool _on;
    private readonly string _label;
    private readonly Action<bool> _onToggled;
    private readonly int _fontSize;
    private readonly float _box;      // checkbox draw size = game art's native size
    private const float TrackW = 46f;

    // fontSize <= 0 means "use the shared GameStyle.ToggleFontSize" so every toggle matches.
    internal ToggleSwitch(string label, bool on, Action<bool> onToggled, int fontSize = 0)
    {
        _label = label;
        _on = on;
        _onToggled = onToggled;
        _fontSize = fontSize > 0 ? fontSize : GameStyle.ToggleFontSize;
        MouseFilter = MouseFilterEnum.Stop;
        GameStyle.EnsureLoaded();
        // Checkbox drawn PROPORTIONAL to the label (the atlas art is high-res ~64px; drawing it at
        // native size dwarfs the game's own checkbox). ~1.3x the font reads like "View upgrades".
        _box = _fontSize * 1.3f;
        float h = Mathf.Max(_box, _fontSize + 8f);
        Size = new Vector2(_box + 12f + label.Length * (_fontSize * 0.62f), h);
        CustomMinimumSize = Size;
        Connect(CanvasItem.SignalName.Draw, Callable.From(OnDraw));
        Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnGuiInput));
    }

    // Set state WITHOUT firing the callback — used to keep several switches in agreement.
    internal void SetOn(bool on)
    {
        if (_on == on) return;
        _on = on;
        QueueRedraw();
    }

    private void OnGuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            _on = !_on;
            QueueRedraw();
            _onToggled(_on);
        }
    }

    private void OnDraw()
    {
        GameStyle.EnsureLoaded();
        float cy = Size.Y * 0.5f;
        float labelX;

        Texture2D? tex = _on ? GameStyle.Ticked : GameStyle.Unticked;
        if (tex != null) // the game's real checkbox art, at its native size
        {
            DrawTextureRect(tex, new Rect2(0f, cy - _box * 0.5f, _box, _box), false);
            labelX = _box + 8f;
        }
        else // fallback: a simple drawn pill switch, so it still works if the art didn't load
        {
            const float tr = 9f;
            float left = tr + 1f, right = TrackW - tr - 1f;
            Color track = _on ? new Color(0.95f, 0.80f, 0.35f) : new Color(0.24f, 0.26f, 0.30f);
            DrawCircle(new Vector2(left, cy), tr, track);
            DrawCircle(new Vector2(right, cy), tr, track);
            DrawRect(new Rect2(left, cy - tr, right - left, tr * 2f), track);
            DrawCircle(new Vector2(_on ? right : left, cy), tr - 2f, new Color(0.97f, 0.98f, 1f));
            labelX = TrackW + 8f;
        }

        Font font = GameStyle.Font ?? GetThemeDefaultFont();
        DrawString(font, new Vector2(labelX, cy + _fontSize * 0.35f), _label,
            HorizontalAlignment.Left, -1, _fontSize, GameStyle.TextColor);
    }
}

// The "Flat map" flip-flop toggle, shown on BOTH the classic map and the flat page. All instances
// stay in agreement via SyncAll; toggling any routes through MiniMapController.SetFlat.
internal static class MapStyleToggle
{
    private static readonly List<ToggleSwitch> _boxes = new();

    internal static ToggleSwitch Create()
    {
        var box = new ToggleSwitch("Flat map", DeckViewConfig.PreferFlatMap, OnToggled)
        {
            ZIndex = 60,
            Name = "DeckViewMapStyleToggle",
        };
        _boxes.Add(box);
        return box;
    }

    internal static void SyncAll(bool flat)
    {
        _boxes.RemoveAll(b => !GodotObject.IsInstanceValid(b));
        foreach (ToggleSwitch b in _boxes)
            b.SetOn(flat);
    }

    private static void OnToggled(bool pressed) => MiniMapController.SetFlat(pressed);
}

internal struct MiniNode
{
    public MapCoord Coord;
    public MapPointType Type;      // the raw point type (Unknown stays Unknown here)
    public MapPointType EffType;   // effective type for colour/tally: a visited "?" resolves to its real room
    public MapPointState State;
    public Texture2D? Icon; // the game's own room icon; null -> fall back to a letter glyph
    public int Lane;        // compacted display lane (Y) from MapLayout — NOT the raw game col
    public int RawLane;     // uncompressed lane == the game's column (for the "raw 1:1" view)
    public bool Reachable;  // can still be travelled to from the current position
}

internal sealed class MiniMapModel
{
    public readonly Dictionary<MapCoord, MiniNode> Nodes = new();
    public readonly List<(MapCoord From, MapCoord To)> Edges = new();
    public readonly Dictionary<MapPointType, Texture2D> TypeIcons = new(); // representative icon per type (info panel)
    public MapCoord? Current;
    public bool TravelEnabled;       // can you move right now (current room finished)?
    public Texture2D? CurrentMarker; // the game's "you are here" arrow art (null in multiplayer)
    public string ActName = "";
    public int ActIndex;
    public int ActFloor;
}

// The minimap PAGE: a capstone screen opened via NCapstoneContainer (like the deck-view screen),
// so the game supplies the top bar, dim backstop, combat pause, and native focus/ESC routing.
//
// IMPORTANT: we draw via the `Draw` SIGNAL and take input via the `gui_input` SIGNAL, NOT by
// overriding _Draw()/_GuiInput(). This mod builds with the plain Microsoft.NET.Sdk (referencing
// GodotSharp.dll directly), so Godot's source generators don't run and custom node virtual
// overrides never fire. Signal connections and interface methods (ICapstoneScreen, invoked
// directly by the container) still work, so everything is wired through those. MouseFilter.Stop
// lets us take hover/click on the page.
internal sealed partial class MiniMapScreen : Control, ICapstoneScreen
{
    private MiniMapModel? _model;
    private Action<MapCoord>? _onTravel;
    private readonly Dictionary<MapCoord, Vector2> _positions = new(); // filled each draw, for hit-testing
    private float _nodeRadius = 12f;
    private MapCoord? _hovered;
    private bool _logDrawOnce;
    private bool _compress = true;               // compressed layout vs raw 1:1 with game columns
    private readonly ToggleSwitch _styleToggle;  // "Flat map" (on for this page)
    private readonly ToggleSwitch _compressToggle; // "Compress" (on = flattened; off = raw 1:1)

    // --- ICapstoneScreen ---
    public NetScreenType ScreenType => NetScreenType.DeckView; // a benign existing full-screen type
    public bool UseSharedBackstop => true;                      // use the standard dimmed backstop
    public Control? DefaultFocusedControl => null;

    internal MiniMapScreen()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Connect(CanvasItem.SignalName.Draw, Callable.From(OnDraw));
        Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnGuiInput));
        _styleToggle = MapStyleToggle.Create();
        _compressToggle = new ToggleSwitch("Compress", DeckViewConfig.CompressMap, OnCompressToggled) { ZIndex = 60 };
        AddChild(_styleToggle);
        AddChild(_compressToggle);
    }

    // Which lane to draw a node at: compressed (flattened) or the raw game column (1:1 view).
    private int LaneOf(MiniNode n) => _compress ? n.Lane : n.RawLane;

    private void OnCompressToggled(bool on)
    {
        _compress = on;
        DeckViewConfig.CompressMap = on;
        QueueRedraw();
    }

    // Set the level to draw + viewport size + travel callback (called just before the page opens).
    internal void Configure(MiniMapModel model, Vector2 viewport, Action<MapCoord> onTravel)
    {
        _model = model;
        _onTravel = onTravel;
        _hovered = null;
        _logDrawOnce = true;
        _compress = DeckViewConfig.CompressMap;
        Position = Vector2.Zero;
        Size = viewport; // capstone fills the screen; the game's top bar renders above us
        // Toggles stacked bottom-left, above the title/hint. "Flat map" sits at the same spot as
        // the classic-map toggle so it reads as one control across the two views.
        _styleToggle.SetOn(DeckViewConfig.PreferFlatMap);
        _compressToggle.SetOn(_compress);
        _styleToggle.Position = new Vector2(viewport.X * 0.04f, viewport.Y * 0.80f);
        _compressToggle.Position = new Vector2(viewport.X * 0.04f, viewport.Y * 0.855f);
        QueueRedraw();
    }

    // Capstone lifecycle — invoked by NCapstoneContainer through the interface (not engine hooks).
    // All the "back/cancel" hotkeys — the same set NBackButton uses. We claim ALL of them while
    // open so a single ESC backs out exactly one level (our page). If we only claimed `cancel`,
    // the map's still-active back button would also catch `pauseAndBack` on the same ESC and close
    // the whole map underneath us.
    private static readonly StringName[] BackHotkeys = { MegaInput.cancel, MegaInput.pauseAndBack, MegaInput.back };

    public void AfterCapstoneOpened()
    {
        Visible = true; // draw when shown (in case the container left the reused node hidden)
        foreach (StringName hk in BackHotkeys)
            NHotkeyManager.Instance?.PushHotkeyReleasedBinding(hk, OnBack);
        QueueRedraw();
    }

    public void AfterCapstoneClosed()
    {
        // The container disables our ProcessMode on close but leaves the node parented; hide it
        // ourselves so our opaque page can't linger over the game once closed.
        Visible = false;
        foreach (StringName hk in BackHotkeys)
            NHotkeyManager.Instance?.RemoveHotkeyReleasedBinding(hk, OnBack);
    }

    // ESC/back from the flat page LEAVES THE MAP ENTIRELY -> prior view (fight/reward/room). The flat
    // map is never a sub-layer you peel back to the classic map from: ESC exits the whole map, exactly
    // as it does from the classic map. (Switching flat<->classic only happens via O or the checkbox.)
    private void OnBack()
    {
        NCapstoneContainer? cc = NCapstoneContainer.Instance;
        if (cc != null && ReferenceEquals(cc.CurrentCapstoneScreen, this))
            MiniMapController.CloseMapCompletely();
    }

    // Nearest node whose circle contains pt (a touch generous), else null.
    private MapCoord? NodeAt(Vector2 pt)
    {
        foreach (KeyValuePair<MapCoord, Vector2> kv in _positions)
            if (pt.DistanceTo(kv.Value) <= _nodeRadius * 1.4f)
                return kv.Key;
        return null;
    }

    private bool IsTravelable(MapCoord c) =>
        _model != null && _model.Nodes.TryGetValue(c, out MiniNode n) && n.State == MapPointState.Travelable;

    // Input via the gui_input SIGNAL (override _GuiInput wouldn't fire — same source-gen reason
    // as _Draw). Hover highlights the node under the cursor; a left click on a *travelable* node
    // travels there.
    private void OnGuiInput(InputEvent e)
    {
        if (_model == null)
            return;
        if (e is InputEventMouseMotion mm)
        {
            MapCoord? was = _hovered;
            _hovered = NodeAt(mm.Position);
            MouseDefaultCursorShape = _hovered is MapCoord h && IsTravelable(h)
                ? CursorShape.PointingHand : CursorShape.Arrow;
            bool changed = (was is null) != (_hovered is null)
                || (was is MapCoord w && _hovered is MapCoord hh && (w.col != hh.col || w.row != hh.row));
            if (changed)
                QueueRedraw();
        }
        else if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            if (NodeAt(mb.Position) is MapCoord c && IsTravelable(c))
                _onTravel?.Invoke(c);
        }
    }

    private void OnDraw()
    {
        MiniMapModel? model = _model;
        Vector2 size = Size;
        GameStyle.EnsureLoaded();
        Font font = GameStyle.Font ?? GetThemeDefaultFont(); // the game's Kreon UI font
        if (_logDrawOnce)
        {
            Log.Info($"[DeckView] minimap _Draw size={size} nodes={model?.Nodes.Count ?? -1}");
            _logDrawOnce = false;
        }
        if (model == null)
            return;

        // Opaque backdrop (drawn ALWAYS, before any early-out) hides the real map completely.
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.05f, 0.06f, 0.09f, 0.98f));

        if (model.Nodes.Count == 0)
        {
            DrawString(font, new Vector2(size.X * 0.06f, size.Y * 0.5f),
                "(no map points found)", HorizontalAlignment.Left, -1, 20, new Color(1, 0.6f, 0.6f));
            return;
        }

        // Grid bounds. Layout is LEFT->RIGHT: the long axis (rows: start..boss) runs along X to use
        // the widescreen; the vertical axis is the COMPACTED lane from MapLayout (not the raw game
        // column), so the whole thing is flattened.
        int minLane = int.MaxValue, maxLane = int.MinValue, minRow = int.MaxValue, maxRow = int.MinValue;
        foreach (MiniNode n in model.Nodes.Values)
        {
            minLane = Math.Min(minLane, LaneOf(n));
            maxLane = Math.Max(maxLane, LaneOf(n));
            minRow = Math.Min(minRow, n.Coord.row);
            maxRow = Math.Max(maxRow, n.Coord.row);
        }
        float rowSpan = Math.Max(1, maxRow - minRow);
        float laneSpan = Math.Max(1, maxLane - minLane);

        float marginX = size.X * 0.05f, marginTop = size.Y * 0.16f, marginBottom = size.Y * 0.12f;
        float drawW = size.X - 2f * marginX;
        float drawH = size.Y - marginTop - marginBottom;
        _nodeRadius = Mathf.Clamp(Math.Min(drawW / rowSpan, drawH / laneSpan) * 0.34f, 7f, 24f);

        Vector2 Pos(MapCoord c) => new(
            marginX + (c.row - minRow) / rowSpan * drawW,                  // row -> X (start left, boss right)
            marginTop + (LaneOf(model.Nodes[c]) - minLane) / laneSpan * drawH); // chosen lane -> Y

        _positions.Clear();
        foreach (MiniNode n in model.Nodes.Values)
            _positions[n.Coord] = Pos(n.Coord);

        // Edges first, so nodes draw on top. Traveled->traveled = the path taken (gold); an edge
        // into an unreachable room is faded so it recedes.
        foreach ((MapCoord from, MapCoord to) in model.Edges)
        {
            if (!model.Nodes.TryGetValue(from, out MiniNode a) || !model.Nodes.TryGetValue(to, out MiniNode b))
                continue;
            bool traveled = a.State == MapPointState.Traveled && b.State == MapPointState.Traveled;
            bool dead = IsDead(a) || IsDead(b);
            DrawLine(_positions[from], _positions[to],
                traveled ? new Color(0.96f, 0.80f, 0.35f, 0.80f)
                         : dead ? new Color(1, 1, 1, 0.10f) : new Color(1, 1, 1, 0.34f),
                traveled ? 3f : 2f, true);
        }

        foreach (MiniNode n in model.Nodes.Values)
        {
            bool isCurrent = model.Current is MapCoord cc && cc.col == n.Coord.col && cc.row == n.Coord.row;
            bool isHovered = _hovered is MapCoord hc && hc.col == n.Coord.col && hc.row == n.Coord.row;
            DrawNode(font, _positions[n.Coord], _nodeRadius, n, isCurrent, isHovered);
        }

        DrawInfoPanel(font, size, model);
    }

    // A room is "dead" when you can no longer reach it AND you never visited it — those get greyed.
    private static bool IsDead(MiniNode n) => n.State != MapPointState.Traveled && !n.Reachable;

    private void DrawNode(Font font, Vector2 p, float r, MiniNode n, bool isCurrent, bool isHovered)
    {
        float rr = n.Type == MapPointType.Boss ? r * 1.5f : r;
        bool isStart = n.Type == MapPointType.Ancient;
        bool visited = n.State == MapPointState.Traveled;
        bool dead = IsDead(n); // unreachable & never visited
        bool travelEnabled = _model?.TravelEnabled ?? false;
        // The current node reads as "done" (dimmed like your past rooms) ONLY once you've finished it
        // and can move on (travel enabled). Before that, your task is still HERE, so it stays
        // full/active. Start is a landmark and stays full.
        bool doneCurrent = isCurrent && travelEnabled;
        bool visitedPast = visited && !isCurrent && !isStart;
        bool dimAsDone = visitedPast || doneCurrent;

        // Dead rooms grey out and recede. "Done" rooms keep their room colour but are lightly dimmed —
        // enough that the first FULL-colour node reads as "still ahead", without washing the room type
        // out into the same murk as the unreachable-grey nodes.
        Color fill = dead ? new Color(0.26f, 0.28f, 0.32f) : ColorFor(n.EffType);
        float fillA = dead ? 0.55f : dimAsDone ? 0.68f : 1f;
        Color iconMod = dead ? new Color(0.55f, 0.57f, 0.62f, 0.5f)
                             : dimAsDone ? new Color(1, 1, 1, 0.82f) : new Color(1, 1, 1, 1);

        // A CURRENTLY-TRAVELABLE next room — the live options you can pick right now. Shown ONLY when
        // travel is actually enabled: if you must finish the current room first, these highlights do
        // NOT appear, because you can't go there yet. A bright white "selectable" halo (the game's
        // Travelable set is relic-aware, so Wing Boots etc. are respected automatically).
        if (n.State == MapPointState.Travelable && travelEnabled)
        {
            DrawArc(p, rr + 4f, 0f, Mathf.Tau, 44, new Color(1f, 1f, 1f, 0.95f), 3f, true);
            DrawArc(p, rr + 8.5f, 0f, Mathf.Tau, 44, new Color(1f, 1f, 1f, 0.38f), 2f, true);
        }

        DrawCircle(p, rr, new Color(fill.R, fill.G, fill.B, fill.A * fillA));
        DrawArc(p, rr, 0f, Mathf.Tau, 32, new Color(0, 0, 0, 0.5f * (dimAsDone ? 0.6f : 1f)), 1.5f, true); // outline

        // WHERE YOU STARTED — steady cyan double-ring, easy to pick out as the origin.
        if (isStart)
        {
            DrawArc(p, rr + 5f, 0f, Mathf.Tau, 44, new Color(0.35f, 0.95f, 1f, 0.95f), 3f, true);
            DrawArc(p, rr + 9f, 0f, Mathf.Tau, 44, new Color(0.35f, 0.95f, 1f, 0.40f), 2f, true);
        }
        // WHERE YOU ARE NOW — a bold BLUE double-ring around the node (plus the game's own marker
        // arrow above it, drawn later on top).
        if (isCurrent)
        {
            DrawArc(p, rr + 5f, 0f, Mathf.Tau, 48, new Color(0.20f, 0.50f, 1f, 1f), 4f, true);
            DrawArc(p, rr + 10f, 0f, Mathf.Tau, 48, new Color(0.20f, 0.50f, 1f, 0.45f), 2.5f, true);
        }
        if (isHovered) // under the cursor: bright white ring
            DrawArc(p, rr + 2f, 0f, Mathf.Tau, 40, new Color(1, 1, 1, 1f), 2.5f, true);

        // The game's own room icon on top of the colour circle. Fall back to a letter glyph when
        // there's no icon (e.g. a Spine-art boss).
        if (n.Icon != null && GodotObject.IsInstanceValid(n.Icon))
        {
            float d = rr * 1.7f;
            DrawTextureRect(n.Icon, new Rect2(p.X - d * 0.5f, p.Y - d * 0.5f, d, d), false, iconMod);
        }
        else DrawGlyph(font, p, rr, n, iconMod);

        // WHERE YOU ARE NOW — the game's own per-character "you are here" arrow, floated above the
        // node (drawn last, on top), alongside the blue ring. Fallback: a downward blue chevron.
        if (isCurrent)
            DrawCurrentMarker(p, rr);
    }

    private void DrawCurrentMarker(Vector2 p, float r)
    {
        Texture2D? marker = _model?.CurrentMarker;
        if (marker != null && GodotObject.IsInstanceValid(marker) && marker.GetWidth() > 0)
        {
            float w = r * 2.1f;
            float h = w * (marker.GetHeight() / (float)marker.GetWidth());
            DrawTextureRect(marker, new Rect2(p.X - w * 0.5f, p.Y - r - h - 1f, w, h), false, new Color(1, 1, 1, 1));
            return;
        }
        float s = r * 0.85f, ty = p.Y - r - 3f;
        var blue = new Color(0.20f, 0.50f, 1f, 1f);
        DrawColoredPolygon(new[] { new Vector2(p.X - s, ty - s * 1.3f), new Vector2(p.X + s, ty - s * 1.3f), new Vector2(p.X, ty) }, blue);
    }

    private void DrawGlyph(Font font, Vector2 p, float rr, MiniNode n, Color iconMod)
    {
        string glyph = GlyphFor(n.EffType);
        if (glyph.Length > 0)
        {
            int gfs = (int)(rr * 1.15f);
            Vector2 ts = font.GetStringSize(glyph, HorizontalAlignment.Left, -1, gfs);
            DrawString(font, new Vector2(p.X - ts.X * 0.5f, p.Y + gfs * 0.35f), glyph,
                HorizontalAlignment.Left, -1, gfs, iconMod);
        }
    }

    // Bottom-right: act name, current floor, and a tally of room types visited so far — a small,
    // quiet reference so you can move forward with a clear picture. Lines are right-aligned and
    // stacked upward from the bottom.
    private void DrawInfoPanel(Font font, Vector2 size, MiniMapModel model)
    {
        string actLine = string.IsNullOrEmpty(model.ActName)
            ? $"Act {model.ActIndex + 1}"
            : $"Act {model.ActIndex + 1} — {model.ActName}";
        string floorLine = $"Floor {model.ActFloor}";

        float right = size.X * 0.96f;
        var soft = new Color(0.80f, 0.83f, 0.90f, 0.75f);
        var softer = new Color(0.72f, 0.75f, 0.82f, 0.62f);
        RightLine(font, right, size.Y * 0.90f, actLine, 22, soft);
        RightLine(font, right, size.Y * 0.90f + 28, floorLine, 16, softer);
        DrawTally(font, right, size.Y * 0.90f + 54, model, softer);
    }

    // The visited-room tally as a row of the game's own room icons + counts (e.g. [monster]×5),
    // right-aligned. Counts use the effective type, so a revealed "?" tallies as its real room.
    private void DrawTally(Font font, float right, float y, MiniMapModel model, Color color)
    {
        var visited = new Dictionary<MapPointType, int>();
        foreach (MiniNode n in model.Nodes.Values)
            if (n.State == MapPointState.Traveled)
                visited[n.EffType] = visited.GetValueOrDefault(n.EffType) + 1;

        MapPointType[] order =
        {
            MapPointType.Monster, MapPointType.Elite, MapPointType.RestSite,
            MapPointType.Shop, MapPointType.Treasure, MapPointType.Unknown,
        };
        var items = new List<(Texture2D? icon, string label, MapPointType type)>();
        foreach (MapPointType t in order)
            if (visited.TryGetValue(t, out int cnt) && cnt > 0)
                items.Add((model.TypeIcons.GetValueOrDefault(t), $"×{cnt}", t));
        if (items.Count == 0)
        {
            RightLine(font, right, y, "none visited yet", 16, color);
            return;
        }

        const int fs = 16;
        const float iconSz = 20f, iconGap = 2f, itemGap = 14f;
        float ItemWidth((Texture2D? icon, string label, MapPointType type) it) =>
            (it.icon != null ? iconSz + iconGap : font.GetStringSize(GlyphFor(it.type) + " ", HorizontalAlignment.Left, -1, fs).X)
            + font.GetStringSize(it.label, HorizontalAlignment.Left, -1, fs).X;

        float total = -itemGap;
        foreach (var it in items) total += ItemWidth(it) + itemGap;

        float x = right - total;
        foreach (var it in items)
        {
            if (it.icon != null && GodotObject.IsInstanceValid(it.icon))
            {
                DrawTextureRect(it.icon, new Rect2(x, y - iconSz * 0.82f, iconSz, iconSz), false, new Color(1, 1, 1, 0.9f));
                x += iconSz + iconGap;
            }
            else
            {
                string g = GlyphFor(it.type) + " ";
                DrawString(font, new Vector2(x, y), g, HorizontalAlignment.Left, -1, fs, color);
                x += font.GetStringSize(g, HorizontalAlignment.Left, -1, fs).X;
            }
            DrawString(font, new Vector2(x, y), it.label, HorizontalAlignment.Left, -1, fs, color);
            x += font.GetStringSize(it.label, HorizontalAlignment.Left, -1, fs).X + itemGap;
        }
    }

    private void RightLine(Font font, float right, float y, string text, int fontSize, Color color)
    {
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        DrawString(font, new Vector2(right - w, y), text, HorizontalAlignment.Left, -1, fontSize, color);
    }

    // DeckView's own palette (intentionally not the game's art) — picked for at-a-glance
    // contrast between room types.
    private static Color ColorFor(MapPointType t) => t switch
    {
        MapPointType.Monster => new Color(0.85f, 0.30f, 0.28f),  // red
        MapPointType.Elite => new Color(0.72f, 0.28f, 0.80f),    // purple
        MapPointType.Boss => new Color(0.85f, 0.20f, 0.22f),     // red (real boss art drawn on top when available)
        MapPointType.Shop => new Color(0.95f, 0.82f, 0.30f),     // yellow (store)
        MapPointType.RestSite => new Color(0.35f, 0.75f, 0.42f), // green (camp)
        MapPointType.Treasure => new Color(0.95f, 0.55f, 0.18f), // orange (treasure box)
        MapPointType.Unknown => new Color(0.60f, 0.62f, 0.68f),  // grey (?)
        MapPointType.Ancient => new Color(0.25f, 0.78f, 0.74f),  // teal (start)
        _ => new Color(0.40f, 0.42f, 0.48f),
    };

    private static string GlyphFor(MapPointType t) => t switch
    {
        MapPointType.Monster => "M",
        MapPointType.Elite => "E",
        MapPointType.Boss => "B",
        MapPointType.Shop => "$",
        MapPointType.RestSite => "R",
        MapPointType.Treasure => "T",
        MapPointType.Unknown => "?",
        MapPointType.Ancient => "A",
        _ => "",
    };
}

// Drive the minimap each frame the map screen processes. _Process isn't focus-gated (unlike
// the arrow-key scroll path), so the toggle works regardless of what has keyboard focus.
[HarmonyPatch(typeof(NMapScreen), "_Process")]
internal static class NMapScreen_Process_Patch
{
    private static void Postfix(NMapScreen __instance) => MiniMapController.Tick(__instance);
}

// Whenever the map opens (map room, top-bar button, or our global M key), show the configured
// style: OnMapOpened requests the flat page on the next tick if "Flat map" is checked.
// The map has TWO co-equal modes: classic and flat. This is the single place the mode is honored —
// whenever ANYTHING opens the map (a map room, the top-bar button, our M key), if flat mode is on we
// render the flat page INSTEAD and skip the classic map entirely (it never opens, so it can never
// show or be fallen back to). In classic mode this patch does nothing and the game opens normally.
[HarmonyPatch(typeof(NMapScreen), "Open")]
internal static class NMapScreen_Open_Patch
{
    private static bool Prefix(NMapScreen __instance, ref NMapScreen __result)
    {
        // Swallow the map room's synchronous ReopenMap when WE just deliberately closed this frame,
        // so a close actually stays closed instead of instantly bouncing back open.
        if (MiniMapController.SuppressReopenThisFrame())
        {
            Log.Info("[DeckView] map open suppressed (deliberate close this frame)");
            __result = __instance;
            return false;
        }
        if (!DeckViewConfig.PreferFlatMap)
        {
            Log.Info("[DeckView] classic map open"); // invocation log — every view transition is traced
            return true; // classic mode -> let the game open the real map
        }
        MiniMapController.OpenFlatFromHook(__instance);
        __result = __instance;
        return false;    // flat mode -> classic map never opens
    }
}

// While the flat page is showing, keep it in sync if the game flips travelability (e.g. a map room
// enables travel just after we opened, or travel disables it) — rebuild + redraw so the "you can move
// here" highlights are always correct.
[HarmonyPatch(typeof(NMapScreen), "SetTravelEnabled")]
internal static class NMapScreen_SetTravelEnabled_Patch
{
    private static void Postfix() => MiniMapController.RefreshFlat();
}

// THE map key. The game hard-binds the map to a key (M by default) and re-broadcasts that key as the
// `mega_view_map` action here, in NInputManager.ProcessShortcutKeyInput — the one choke point that
// fires in EVERY context (combat, map room, deck view), regardless of the top-bar button's state.
// We intercept the map key (honoring rebinds) and our O key here, route to our own toggle, and skip
// the original so the vanilla map action is NEVER broadcast — one handler, no double-toggle.
[HarmonyPatch(typeof(NInputManager), "ProcessShortcutKeyInput")]
internal static class NInputManager_ShortcutKey_Patch
{
    private static bool Prefix(object[] __args)
    {
        if (__args.Length == 0 || __args[0] is not InputEventKey k || k.IsEcho() || !k.IsPressed())
            return true;
        NInputManager? mgr = NInputManager.Instance;
        if (mgr == null) return true;
        MiniMapController.AuditKeysOnce(mgr); // one-time: log which game actions share our keys
        if (k.Keycode == mgr.GetShortcutKey(MegaInput.viewMap))
        {
            MiniMapController.OnMapKey();
            return false; // suppress the vanilla map action -> no collision with the game's map button
        }
        if (k.Keycode == DeckViewMod.ToggleMiniMapKey && MiniMapController.MapShown())
        {
            MiniMapController.OnFlipKey();
            return false;
        }
        return true;
    }
}
