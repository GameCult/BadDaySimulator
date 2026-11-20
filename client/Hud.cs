using System.Text;
using BadDay.Core;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client;

/// <summary>Readout and controls. Every number comes from the session; every control calls a Main method that calls it.</summary>
public partial class Hud : CanvasLayer
{
    private const int LogLines = 6;
    private static readonly Color Ink = new(0.92f, 0.92f, 0.9f);

    private readonly Main _main;
    private readonly Label _readout = new();
    private readonly StringBuilder _text = new();

    public Hud(Main main) => _main = main;

    public SpinBox SeedBox { get; } = new() { MinValue = 0, MaxValue = 999999999, Step = 1, Value = 1 };
    public Button ResetButton { get; } = new() { Text = "Reset" };
    public HSlider IntensitySlider { get; } = new() { MinValue = 0, MaxValue = PainBox.MaxLevel, Step = 0.05, CustomMinimumSize = new Vector2(220, 24) };
    public Button BoxOffButton { get; } = new() { Text = "Box off" };
    public CheckButton SlowToggle { get; } = new() { Text = "Slow motion", Modulate = Ink };

    /// <summary>The readout as drawn.</summary>
    public string Readout => _readout.Text;

    public override void _Ready()
    {
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 16);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(margin);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.AddChild(row);

        _readout.AddThemeColorOverride("font_color", Ink);
        _readout.AddThemeFontSizeOverride("font_size", 16);
        _readout.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _readout.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.AddChild(_readout);

        var controls = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };
        row.AddChild(controls);

        var seedRow = new HBoxContainer();
        seedRow.AddChild(new Label { Text = "Seed", Modulate = Ink });
        seedRow.AddChild(SeedBox);
        controls.AddChild(seedRow);
        ResetButton.Pressed += () => _main.Reset((ulong)SeedBox.Value);
        controls.AddChild(ResetButton);

        controls.AddChild(new Label { Text = "Intensity", Modulate = Ink });
        IntensitySlider.Value = _main.Intensity;
        IntensitySlider.ValueChanged += v => _main.SetIntensity(v);
        controls.AddChild(IntensitySlider);

        BoxOffButton.Pressed += () => _main.SwitchOff();
        controls.AddChild(BoxOffButton);
        SlowToggle.Toggled += on => _main.SetSlowMotion(on);
        controls.AddChild(SlowToggle);
    }

    public override void _Process(double delta)
    {
        if (_main.Failure is { } failure)
        {
            _readout.Text = $"Cannot run: {failure}\nReset tries again.";
            return;
        }

        var s = _main.Session;
        var r = s.Result();
        _text.Clear();
        _text.Append($"seed {_main.Seed}    t {s.TickIndex * BodyPainBox.TickSeconds:F2} s  (tick {s.TickIndex})\n");
        _text.Append($"box intensity {_main.ShownBoxIntensity:F2}  (set {_main.Intensity:F2})\n");
        _text.Append($"pain {s.Pain:F2}   strain {s.Strain:F2}   resolve {s.Resolve:F2}\n");
        _text.Append($"goal {s.Goal}   flinches {r.Flinches}\n");
        _text.Append($"will broke at {Ms(r.TimeToBreakMs)}   withdrew {(s.Goal == Lodur.Goal.Withdraw ? "yes" : "no")}\n");
        _text.Append($"hand {(s.Hand.InBox ? "in box" : "out of box")}   hand out at {Ms(s.HandOutMs)}\n");
        _text.Append(s.Finished ? $"outcome {r.Outcome}\n" : "running\n");
        var events = _main.Events;
        for (int i = System.Math.Max(0, events.Count - LogLines); i < events.Count; i++)
        {
            _text.Append($"{events[i].Tick * BodyPainBox.TickSeconds,6:F2} s  {events[i].Events}\n");
        }
        _readout.Text = _text.ToString();
    }

    private static string Ms(long? ms) => ms is { } v ? $"{v / 1000.0:F2} s" : "-";
}
