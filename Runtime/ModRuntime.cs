using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace DeckView;

/// <summary>
/// Owns DeckView's all-or-nothing Harmony lifecycle. A game update that moves a hook now leaves
/// the unmodified game running and records every missing member in the log.
/// </summary>
internal static class ModRuntime
{
    internal const string HarmonyId = "ernes.deckview";

    private static Harmony? _harmony;

    internal static bool Enabled { get; private set; }

    internal static bool TryEnable(Assembly assembly)
    {
        IReadOnlyList<string> missing = HookCatalog.FindMissing();
        if (missing.Count > 0)
        {
            LogDisabled("incompatible game build; missing hooks: " + string.Join(", ", missing));
            return false;
        }

        _harmony = new Harmony(HarmonyId);
        try
        {
            _harmony.PatchAll(assembly);
            Enabled = true;
            return true;
        }
        catch (Exception ex)
        {
            Enabled = false;
            try
            {
                // PatchAll is not transactional. Remove any classes it applied before the failure.
                _harmony.UnpatchSelf();
            }
            catch (Exception rollback)
            {
                Log.Info($"[DeckView] ERROR: patch rollback failed; all patch callbacks remain " +
                         $"disabled by their runtime guards: {rollback}");
            }
            LogDisabled($"Harmony setup failed: {ex}");
            return false;
        }
    }

    /// <summary>Disable callbacks after an unexpected runtime integration failure.</summary>
    internal static void Disable(string location, Exception ex)
    {
        if (!Enabled)
            return;
        Enabled = false;
        LogDisabled($"{location} failed: {ex}");
    }

    private static void LogDisabled(string reason) =>
        Log.Info($"[DeckView] DISABLED — {reason}. The game will continue with its vanilla UI.");
}

/// <summary>
/// Preflight catalog for every non-public or string-named game member DeckView relies on.
/// Keep this list synchronized with scripts/verify-hooks.sh.
/// </summary>
internal static class HookCatalog
{
    internal static IReadOnlyList<string> FindMissing()
    {
        var missing = new List<string>();

        Method(typeof(NCardGrid), "ConnectSignals", missing);
        Method(typeof(NCardGrid), "_ExitTree", missing);
        Method(typeof(NCardGrid), "_Process", missing);
        Property(typeof(NCardGrid), "CardPadding", missing);
        Property(typeof(NCardGrid), "CurrentlyDisplayedCardHolders", missing);
        Field(typeof(NCardGrid), "_cardSize", missing);
        Field(typeof(NCardGrid), "_needsReinit", missing);

        Property(typeof(NCardHolder), "SmallScale", missing);
        Property(typeof(NCardHolder), "Hitbox", missing);
        Method(typeof(NCardHolder), "RefreshFocusState", missing);
        Field(typeof(NCardHolder), "_isHovered", missing);
        Field(typeof(NCardHolder), "_isFocused", missing);
        Field(typeof(NCardHolder), "_hoverTween", missing);
        Method(typeof(NGridCardHolder), "Create", missing);
        Field(typeof(NClickableControl), "_isHovered", missing);

        Method(typeof(NCardsViewScreen), "ConnectSignals", missing);
        Field(typeof(NCardsViewScreen), "_showUpgrades", missing);
        Method(typeof(NMapScreen), "_Process", missing);
        Method(typeof(NMapScreen), "Open", missing);
        Method(typeof(NMapScreen), "SetTravelEnabled", missing);
        Method(typeof(NMapScreen), "RecalculateTravelability", missing, Type.EmptyTypes);
        Property(typeof(NMapScreen), "IsTravelEnabled", missing);
        Field(typeof(NMapScreen), "_mapPointDictionary", missing);
        FieldInfo? runStateField = Field(typeof(NMapScreen), "_runState", missing);
        Field(typeof(NMapScreen), "_marker", missing);

        Field(typeof(NNormalMapPoint), "_icon", missing);
        Field(typeof(NAncientMapPoint), "_icon", missing);
        Field(typeof(NBossMapPoint), "_placeholderImage", missing);
        Property(typeof(NMapPoint), "Point", missing);
        Property(typeof(NMapPoint), "State", missing);
        Field(typeof(MapPoint), "coord", missing);
        Property(typeof(MapPoint), "PointType", missing);
        Property(typeof(MapPoint), "Children", missing);

        if (runStateField != null)
        {
            Type runState = runStateField.FieldType;
            Property(runState, "CurrentMapCoord", missing);
            Property(runState, "CurrentActIndex", missing);
            Property(runState, "ActFloor", missing);
            PropertyInfo? act = Property(runState, "Act", missing);
            Property(runState, "MapPointHistory", missing);
            if (act != null)
            {
                PropertyInfo? title = Property(act.PropertyType, "Title", missing);
                if (title != null)
                    Method(title.PropertyType, "GetFormattedText", missing, Type.EmptyTypes);
            }
        }

        MethodInfo? shortcut = Method(typeof(NInputManager), "ProcessShortcutKeyInput", missing);
        if (shortcut != null &&
            (shortcut.GetParameters().Length == 0 ||
             shortcut.GetParameters()[0].ParameterType != typeof(InputEventKey)))
            missing.Add("NInputManager.ProcessShortcutKeyInput(InputEventKey first argument)");
        Property(typeof(NControllerManager), "IsUsingController", missing);

        return missing;
    }

    private static FieldInfo? Field(Type type, string name, List<string> missing)
    {
        FieldInfo? value = AccessTools.Field(type, name);
        if (value == null) missing.Add($"{type.Name}.{name}");
        return value;
    }

    private static MethodInfo? Method(Type type, string name, List<string> missing, Type[]? args = null)
    {
        MethodInfo? value = args == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, args);
        if (value == null) missing.Add($"{type.Name}.{name}()");
        return value;
    }

    private static PropertyInfo? Property(Type type, string name, List<string> missing)
    {
        PropertyInfo? value = AccessTools.Property(type, name);
        if (value == null) missing.Add($"{type.Name}.{name}");
        return value;
    }

}

// Reflection calls remain fail-fast internally, but ModRuntime preflights all static lookups before
// applying any patch and every patch boundary catches unexpected runtime failures.
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

internal static class Dbg
{
    private static readonly HashSet<string> Seen = new();

    internal static void Once(string key, string message)
    {
        if (Seen.Add(key)) Log.Info($"[DeckView] {message}");
    }

    internal static void Rearm() => Seen.Clear();
}
