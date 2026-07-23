using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Logging;

namespace DeckView;

/// <summary>Shared game-native visual metrics for DeckView controls.</summary>
internal static class GameStyle
{
    internal static readonly Color TextColor = new("FFF6E2");
    internal static Font? Font { get; private set; }
    internal static Texture2D? Ticked { get; private set; }
    internal static Texture2D? Unticked { get; private set; }
    internal static int ToggleFontSize { get; private set; } = 18;
    internal static float ToggleBoxSize { get; private set; } = 24f;

    private static readonly List<ToggleSwitch> Toggles = new();
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

    internal static void Register(ToggleSwitch toggle) => Toggles.Add(toggle);

    /// <summary>
    /// Measure the live vanilla "View upgrades" control. All DeckView toggles then use the same
    /// effective font and checkbox dimensions, including controls that were created earlier.
    /// </summary>
    internal static void ConfigureToggleMetrics(Control? checkboxVisuals, Control? label)
    {
        EnsureLoaded();

        if (label != null && GodotObject.IsInstanceValid(label))
        {
            int themed = label.GetThemeFontSize("font_size");
            float scale = Mathf.Abs(label.GetGlobalTransformWithCanvas().Scale.Y);
            int effective = Mathf.RoundToInt(themed * Mathf.Clamp(scale, 0.2f, 2f));
            if (effective > 0)
                ToggleFontSize = effective;
        }

        if (checkboxVisuals != null && GodotObject.IsInstanceValid(checkboxVisuals))
        {
            Vector2 scale = checkboxVisuals.GetGlobalTransformWithCanvas().Scale;
            Vector2 effective = new(
                checkboxVisuals.Size.X * Mathf.Abs(scale.X),
                checkboxVisuals.Size.Y * Mathf.Abs(scale.Y));
            float measured = Mathf.Max(effective.X, effective.Y);
            if (measured > 0f)
                ToggleBoxSize = Mathf.Clamp(measured, 16f, 48f);
        }
        else
        {
            ToggleBoxSize = ToggleFontSize * 1.3f;
        }

        Toggles.RemoveAll(t => !GodotObject.IsInstanceValid(t));
        foreach (ToggleSwitch toggle in Toggles)
            toggle.RefreshMetrics();
    }
}

/// <summary>
/// A code-built checkbox using STS2's own art and font. It is focusable and responds to
/// ui_accept, so mouse, keyboard, and controller all use the same activation path.
/// </summary>
internal sealed partial class ToggleSwitch : Control
{
    private bool _on;
    private readonly string _label;
    private readonly Action<bool> _onToggled;
    private int _fontSize;
    private float _box;
    private const float TrackW = 46f;

    internal ToggleSwitch(string label, bool on, Action<bool> onToggled)
    {
        _label = label;
        _on = on;
        _onToggled = onToggled;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        TooltipText = $"{label} (press accept to toggle)";
        GameStyle.EnsureLoaded();
        GameStyle.Register(this);
        RefreshMetrics();

        Connect(CanvasItem.SignalName.Draw, Callable.From(OnDraw));
        Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnGuiInput));
        Connect(Control.SignalName.FocusEntered, Callable.From(QueueRedraw));
        Connect(Control.SignalName.FocusExited, Callable.From(QueueRedraw));
    }

    internal void RefreshMetrics()
    {
        _fontSize = GameStyle.ToggleFontSize;
        _box = GameStyle.ToggleBoxSize;
        Font font = GameStyle.Font ?? GetThemeDefaultFont();
        float textWidth = font.GetStringSize(
            _label, HorizontalAlignment.Left, -1, _fontSize).X;
        float height = Mathf.Max(_box, _fontSize + 8f);
        Size = new Vector2(Mathf.Ceil(_box + 8f + textWidth), Mathf.Ceil(height));
        CustomMinimumSize = Size;
        QueueRedraw();
    }

    internal void SetOn(bool on)
    {
        if (_on == on) return;
        _on = on;
        QueueRedraw();
    }

    private void Toggle()
    {
        _on = !_on;
        QueueRedraw();
        try
        {
            _onToggled(_on);
        }
        catch (Exception ex)
        {
            ModRuntime.Disable($"{nameof(ToggleSwitch)} '{_label}'", ex);
        }
    }

    private void OnGuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            GrabFocus();
            Toggle();
            AcceptEvent();
            return;
        }

        if (e.IsActionPressed(MegaInput.accept))
        {
            Toggle();
            AcceptEvent();
        }
    }

    private void OnDraw()
    {
        GameStyle.EnsureLoaded();
        float cy = Size.Y * 0.5f;
        float labelX;

        Texture2D? tex = _on ? GameStyle.Ticked : GameStyle.Unticked;
        if (tex != null)
        {
            DrawTextureRect(tex, new Rect2(0f, cy - _box * 0.5f, _box, _box), false);
            labelX = _box + 8f;
        }
        else
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

        if (HasFocus())
            DrawRect(new Rect2(Vector2.Zero, Size).Grow(3f),
                new Color(1f, 0.88f, 0.45f, 0.95f), false, 2f);
    }
}
