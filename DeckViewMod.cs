using System;
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
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
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
    // Lower = smaller cards / more columns. Tune to taste.
    public const float CardScaleFactor = 0.6f;

    // Vanilla NCardGrid.CardPadding is a constant 40f. Tighter spacing packs in more
    // columns/rows. Set equal to 40f to keep vanilla spacing.
    public const float CardPadding = 24f;

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
            ("NGridCardHolder.Create", AccessTools.Method(typeof(NGridCardHolder), "Create") != null),
            ("NCardGrid.CurrentlyDisplayedCardHolders",
                AccessTools.PropertyGetter(typeof(NCardGrid), "CurrentlyDisplayedCardHolders") != null),
        };
        missing = string.Join(", ", hooks.Where(h => !h.ok).Select(h => h.name));
        return missing.Length == 0;
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
    // (three-underscore prefix + the field name, which itself starts with '_').
    private static void Postfix(ref Vector2 ____cardSize)
    {
        ____cardSize *= DeckViewMod.CardScaleFactor;
    }
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
        if (__instance is NGridCardHolder && !GridHoverGate.IsInFixedCardRow(__instance))
            __result *= DeckViewMod.CardScaleFactor;
    }
}

// Tighten the spacing between cards (vanilla getter returns a constant 40f).
[HarmonyPatch(typeof(NCardGrid), "CardPadding", MethodType.Getter)]
internal static class NCardGrid_CardPadding_Patch
{
    private static void Postfix(ref float __result)
    {
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
