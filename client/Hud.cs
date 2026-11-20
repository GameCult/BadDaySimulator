using System.Text;
using BadDay.Core;
using BadDay.Ymir;
using Godot;

namespace BadDay.Client;

/// <summary>Readout and controls. Every number comes from the session; every control calls a Main method that calls it.</summary>
public partial class Hud : CanvasLayer
{
    private readonly Main _main;
    private readonly Label _readout = new();
    private readonly SpinBox _seed = new() { MinValue = 0, MaxValue = 999999999, Step = 1, Value = 1 };
    private readonly HSlider _intensity = new() { MinValue = 0, MaxValue = PainBox.MaxLevel, Step = 0.05, CustomMinimumSize = new Vector2(220, 24) };
    private readonly StringBuilder _text = new();

    public Hud(Main main) => _main = main;

    public override void _Ready()
    {
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 16);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(margin);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.AddChild(row);

        _readout.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.9f));
        _readout.AddThemeFontSizeOverride("font_size", 16);
        _readout.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _readout.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.AddChild(_readout);

        var controls = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };
        row.AddChild(controls);

        var seedRow = new HBoxContainer();
        seedRow.AddChild(new Label { Text = "Seed", Modulate = new Color(0.92f, 0.92f, 0.9f) });
        seedRow.AddChild(_seed);
        controls.AddChild(seedRow);
        controls.AddChild(Button("Reset", () => _main.Reset((ulong)_seed.Value)));

        controls.AddChild(new Label { Text = "Intensity", Modulate = new Color(0.92f, 0.92f, 0.9f) });
        _intensity.Value = _main.Intensity;
        _intensity.ValueChanged += v => _main.SetIntensity(v);
        controls.AddChild(_intensity);

        controls.AddChild(Button("Box off", () => _main.SwitchOff()));
        var slow = new CheckButton { Text = "Slow motion", Modulate = new Color(0.92f, 0.92f, 0.9f) };
        slow.Toggled += on => _main.SetSlowMotion(on);
        controls.AddChild(slow);
    }

    public override void _Process(double delta)
    {
        var s = _main.Session;
        var r = s.Result();
        _text.Clear();
        _text.Append($"seed {_main.Seed}    t {s.TickIndex * BodyPainBox.TickSeconds:F2} s  (tick {s.TickIndex})\n");
        _text.Append($"box intensity {s.BoxIntensity:F2}  (set {_main.Intensity:F2})\n");
        _text.Append($"pain {s.Pain:F2}   strain {s.Strain:F2}   resolve {s.Resolve:F2}\n");
        _text.Append($"goal {s.Goal}   flinches {r.Flinches}\n");
        _text.Append($"will broke at {Ms(r.TimeToBreakMs)}   withdrew {(s.Goal == Lodur.Goal.Withdraw ? "yes" : "no")}\n");
        _text.Append($"hand {(s.Hand.InBox ? "in box" : "out of box")}   hand out at {Ms(s.HandOutMs)}\n");
        _text.Append(s.Finished ? $"outcome {r.Outcome}\n" : "running\n");
        foreach (var line in _main.Log) _text.Append(line).Append('\n');
        _readout.Text = _text.ToString();
    }

    private static string Ms(long? ms) => ms is { } v ? $"{v / 1000.0:F2} s" : "-";

    private static Button Button(string text, System.Action pressed)
    {
        var b = new Button { Text = text };
        b.Pressed += pressed;
        return b;
    }
}
