using System.Collections.Generic;
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

    private readonly VBoxContainer _chamberControls = new();
    private readonly VBoxContainer _officeControls = new();
    private readonly Dictionary<string, Button> _silence = new();

    public OptionButton ScenarioPicker { get; } = new();
    /// <summary>His will, 0 to 1 (lodur:ruling:bds-will-visible).</summary>
    public ProgressBar WillBar { get; } = Bar(Color.Color8(0x4c, 0x9a, 0xd8));
    /// <summary>His arousal, 0 to 1 (lodur:ruling:stress-bar-shows-arousal-calm-silences).</summary>
    public ProgressBar StressBar { get; } = Bar(Color.Color8(0xd8, 0x8a, 0x2c));

    /// <summary>A 0 to 1 bar with a visible track (dark, outlined), so an empty bar reads as empty, and a fill in <paramref name="fill"/>.</summary>
    private static ProgressBar Bar(Color fill)
    {
        var bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0, ShowPercentage = false, CustomMinimumSize = new Vector2(220, 18) };
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Color.Color8(0x1c, 0x1c, 0x20), BorderColor = Ink, BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1 });
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = fill });
        return bar;
    }
    /// <summary>One button per stressor of the office's cause, by id.</summary>
    public IReadOnlyDictionary<string, Button> SilenceButtons => _silence;
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

        foreach (var scenario in System.Enum.GetValues<Scenario>()) ScenarioPicker.AddItem(scenario.ToString(), (int)scenario);
        ScenarioPicker.Selected = (int)_main.Scenario;
        ScenarioPicker.ItemSelected += i => _main.SetScenario((Scenario)ScenarioPicker.GetItemId((int)i));
        controls.AddChild(ScenarioPicker);

        controls.AddChild(_officeControls);
        _officeControls.AddChild(new Label { Text = "Will", Modulate = Ink });
        _officeControls.AddChild(WillBar);
        _officeControls.AddChild(new Label { Text = "Stress", Modulate = Ink });
        _officeControls.AddChild(StressBar);
        _officeControls.AddChild(new Label { Text = "Silence", Modulate = Ink });
        foreach (var stressor in Main.OfficeParameters.Cause.Stressors)
        {
            var id = stressor.Id;
            var button = new Button { Text = id };
            button.Pressed += () => _main.Silence(id);
            _silence[id] = button;
            _officeControls.AddChild(button);
        }

        controls.AddChild(_chamberControls);
        _chamberControls.AddChild(new Label { Text = "Intensity", Modulate = Ink });
        IntensitySlider.Value = _main.Intensity;
        IntensitySlider.ValueChanged += v => _main.SetIntensity(v);
        _chamberControls.AddChild(IntensitySlider);

        BoxOffButton.Pressed += () => _main.SwitchOff();
        _chamberControls.AddChild(BoxOffButton);
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

        _chamberControls.Visible = _main.Scenario == Scenario.Chamber;
        _officeControls.Visible = _main.Scenario == Scenario.Office;
        _text.Clear();
        if (_main.Scenario == Scenario.Office)
        {
            OfficeReadout(_main.Office);
            return;
        }

        var s = _main.PainBox;
        var r = s.Result();
        _text.Append($"seed {_main.Seed}    t {s.TickIndex * BodyPainBox.TickSeconds:F2} s  (tick {s.TickIndex})\n");
        _text.Append($"box intensity {s.BoxIntensity:F2}{(s.BoxOn ? "" : " off")}  (set {_main.Intensity:F2})\n");
        _text.Append($"pain {s.Pain:F2}   strain {s.Strain:F2}   resolve {s.Resolve:F2}\n");
        _text.Append($"goal {s.Goal}   flinches {r.Flinches}\n");
        _text.Append($"will broke at {Ms(r.TimeToBreakMs)}   withdrew {(s.Goal == Goal.Withdraw ? "yes" : "no")}\n");
        _text.Append($"hand {(s.Hand.InBox ? "in box" : "out of box")}   hand out at {Ms(s.HandOutMs)}\n");
        _text.Append(s.Finished ? $"outcome {r.Outcome}\n" : "running\n");
        var events = _main.Events;
        for (int i = System.Math.Max(0, events.Count - LogLines); i < events.Count; i++)
        {
            _text.Append($"{events[i].Tick * BodyPainBox.TickSeconds,6:F2} s  {events[i].Events}\n");
        }
        _readout.Text = _text.ToString();
    }

    private void OfficeReadout(OfficeBodySession o)
    {
        WillBar.Value = o.Will;
        StressBar.Value = o.Arousal;
        _text.Append($"office   seed {_main.Seed}    t {o.TickIndex * OfficeBody.TickSeconds:F2} s  (tick {o.TickIndex})\n");
        _text.Append($"will {o.Will:F2}   stress {o.Arousal:F2}   goal {o.Goal}\n");
        _text.Append($"blood loss {o.BloodLoss * 100:F0} % of lethal   blood spilled {o.BloodEmitted * 1000:F2} L\n");
        _text.Append($"consciousness {o.Consciousness:F2}   air hunger {o.AirHunger:F2}\n");
        _text.Append($"airway {o.AirwayLiquid * 1e6:F0} mL ({o.Occlusion * 100:F0} % full)\n");
        if (o.Dead) _text.Append($"DEAD: {o.DeathCause}\n");
        else _text.Append(o.Finished ? "finished\n" : "running\n");
        _text.Append(_main.Cuts.Count > 0 ? $"last cut: {_main.Cuts[^1].Part} at tick {_main.Cuts[^1].Tick}\n" : "no cuts yet\n");
        var events = _main.Events;
        for (int i = System.Math.Max(0, events.Count - LogLines); i < events.Count; i++)
        {
            _text.Append($"{events[i].Tick * OfficeBody.TickSeconds,6:F2} s  {events[i].Events}\n");
        }
        _readout.Text = _text.ToString();
    }

    private static string Ms(long? ms) => ms is { } v ? $"{v / 1000.0:F2} s" : "-";
}
