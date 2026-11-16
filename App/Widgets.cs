using Godot;
using SoundShelf.Core;

namespace SoundShelf.App;

public static class Skin
{
    public static readonly Color Background = new("20242d"), Panel = new("292f3b"), Raised = new("333c4b"), Border = new("465165"), Text = new("dfe6ef"), Muted = new("92a0b6"), Accent = new("79b9ef");
    public static StyleBoxFlat Box(Color color, int radius = 4, Color? border = null)
    {
        var box = new StyleBoxFlat { BgColor = color, CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 7, ContentMarginBottom = 7 };
        if (border.HasValue) { box.BorderColor = border.Value; box.SetBorderWidthAll(1); }
        return box;
    }
    public static Theme Create()
    {
        var theme = new Theme { DefaultFontSize = 14 };
        foreach (string type in new[] { "Label", "Button", "LineEdit", "CheckBox", "OptionButton", "PopupMenu", "Tree", "ItemList", "SpinBox", "RichTextLabel" })
        { theme.SetColor("font_color", type, Text); theme.SetColor("font_hover_color", type, Colors.White); }
        theme.SetStylebox("panel", "PanelContainer", Box(Panel));
        theme.SetStylebox("panel", "PopupMenu", Box(Panel, 4, Border));
        theme.SetStylebox("panel", "AcceptDialog", Box(Panel, 4, Border));
        theme.SetStylebox("panel", "Window", Box(Panel, 4, Border));
        foreach (string type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Box(Raised)); theme.SetStylebox("hover", type, Box(new("42516a")));
            theme.SetStylebox("pressed", type, Box(new("365d7c"))); theme.SetStylebox("disabled", type, Box(Background)); theme.SetStylebox("focus", type, Box(new Color(0, 0, 0, 0), 4, Accent));
        }
        theme.SetStylebox("normal", "LineEdit", Box(Background, 4, Border)); theme.SetStylebox("focus", "LineEdit", Box(Background, 4, Accent));
        theme.SetConstant("separation", "VBoxContainer", 8); theme.SetConstant("separation", "HBoxContainer", 8);
        return theme;
    }
    public static Label Label(string text, int size = 14, Color? color = null)
    { var label = new Label { Text = text }; label.AddThemeFontSizeOverride("font_size", size); if (color.HasValue) label.AddThemeColorOverride("font_color", color.Value); return label; }
    public static Button Button(string text, Action clicked, string tooltip = "")
    { var b = new Button { Text = text, TooltipText = tooltip, MouseDefaultCursorShape = Control.CursorShape.PointingHand }; b.Pressed += clicked; return b; }
    public static Button IconButton(string iconPath, Action clicked, string tooltip)
    {
        var button = Button("", clicked, tooltip);
        button.AccessibilityName = tooltip;
        button.Icon = GD.Load<Texture2D>(iconPath); button.IconAlignment = HorizontalAlignment.Center;
        button.ExpandIcon = true; button.CustomMinimumSize = new(40, 38);
        button.AddThemeConstantOverride("icon_max_width", 24);
        return button;
    }
    public static void Clear(Node parent) { foreach (var child in parent.GetChildren()) { parent.RemoveChild(child); child.QueueFree(); } }
}

public partial class BrowserTile : Control
{
    public BrowserEntry Entry { get; set; } = null!;
    public bool Selected { get; set; }
    public event Action<BrowserTile, bool>? Clicked;
    public override void _Ready()
    { CustomMinimumSize = new(180, 126); FocusMode = FocusModeEnum.All; MouseDefaultCursorShape = CursorShape.PointingHand; TooltipText = Entry.RelativePath + (Entry.Tags.Count > 0 ? "\n" + string.Join(", ", Entry.Tags.Select(t => t.Name)) : ""); MouseEntered += QueueRedraw; MouseExited += QueueRedraw; FocusEntered += QueueRedraw; FocusExited += QueueRedraw; }
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } mouse && (mouse.ButtonIndex == MouseButton.Left || mouse.ButtonIndex == MouseButton.Right))
        { GrabFocus(); Clicked?.Invoke(this, mouse.ButtonIndex == MouseButton.Right); AcceptEvent(); }
    }
    public override Variant _GetDragData(Vector2 position)
    {
        if (Entry.IsFolder || Entry.Sound == null) return default;
        SetDragPreview(Skin.Label(Entry.Name, 14, Skin.Accent));
        return MixDragData();
    }
    internal Variant MixDragData()
    {
        if (Entry.IsFolder || Entry.Sound == null) return default;
        return new Godot.Collections.Dictionary { ["kind"] = "mix-sound", ["sound"] = Entry.Sound.Id, ["path"] = Entry.Sound.FullPath };
    }
    public override void _Draw()
    {
        bool hover = GetLocalMousePosition().X >= 0 && GetLocalMousePosition().Y >= 0 && new Rect2(Vector2.Zero, Size).HasPoint(GetLocalMousePosition());
        DrawStyleBox(Skin.Box(Selected ? new("344f68") : hover ? Skin.Raised : Skin.Panel, 5, Selected || HasFocus() ? Skin.Accent : Skin.Border), new(Vector2.Zero, Size));
        float cx = Size.X / 2;
        if (Entry.IsFolder)
        {
            DrawStyleBox(Skin.Box(new("91aac5"), 3), new(cx - 24, 21, 24, 10));
            DrawStyleBox(Skin.Box(new("abc3dc"), 3), new(cx - 24, 27, 49, 32));
        }
        else
        {
            for (int i = 0; i < 17; i++) { float h = 4 + (float)(Math.Sin(i * 1.7) * Math.Sin(i * .7) + 1) * 13; DrawLine(new(cx - 32 + i * 4, 40 - h / 2), new(cx - 32 + i * 4, 40 + h / 2), Skin.Accent, 2); }
        }
        DrawString(ThemeDB.FallbackFont, new(10, 85), Entry.Name, HorizontalAlignment.Center, Size.X - 20, 14, Skin.Text);
        string detail = Entry.IsFolder ? "FOLDER" : $"{Path.GetExtension(Entry.Name).TrimStart('.').ToUpperInvariant()}  ·  {Entry.Sound!.Size / 1024.0:0.#} KB";
        DrawString(ThemeDB.FallbackFont, new(10, 107), detail, HorizontalAlignment.Center, Size.X - 20, 11, Skin.Muted);
        int dots = Math.Min(5, Entry.Tags.Count);
        for (int i = 0; i < dots; i++) DrawCircle(new(13 + i * 11, 13), 3.5f, new Color(Entry.Tags[i].Color));
        if (Entry.Tags.Count > dots) DrawString(ThemeDB.FallbackFont, new(73, 17), $"+{Entry.Tags.Count - dots}", fontSize: 10, modulate: Skin.Muted);
    }
}

public partial class TimelineRuler : Control
{
    internal static double TickStep(double pixelsPerSecond) => new[] { .001, .002, .005, .01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 30, 60 }.First(step => step * pixelsPerSecond >= 60);
    public double PixelsPerSecond { get; set; } = 80;
    public double Playhead { get; set; }
    public event Action<double>? Seek;
    public override void _GuiInput(InputEvent e)
    { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } m) { Seek?.Invoke(m.Position.X / PixelsPerSecond); AcceptEvent(); } }
    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), Skin.Background);
        float visibleLeft = Math.Max(0, -GlobalPosition.X), visibleRight = Math.Min(Size.X, GetViewportRect().Size.X - GlobalPosition.X);
        double step = TickStep(PixelsPerSecond);
        for (double s = Math.Floor(visibleLeft / PixelsPerSecond / step) * step; s * PixelsPerSecond < visibleRight; s += step)
        { float x = (float)(s * PixelsPerSecond); DrawLine(new(x, 20), new(x, Size.Y), Skin.Border); DrawString(ThemeDB.FallbackFont, new(x + 4, 16), $"{s:0.##}s", fontSize: 11, modulate: Skin.Muted); }
        float p = (float)(Playhead * PixelsPerSecond); DrawLine(new(p, 0), new(p, Size.Y), Skin.Accent, 2);
    }
}

public partial class ClipView : Control
{
    public MixClip ClipData { get; set; } = null!;
    public bool Audible { get; set; } = true;
    public bool Selected { get; set; }
    public Guid TrackId { get; set; }
    public event Action<MixClip>? CandidateEdited;
    public event Action? Clicked;
    public event Action? EditCanceled;
    public float[] Peaks { get; set; } = Array.Empty<float>();
    public double PixelsPerSecond { get; set; } = 80;
    public double Playhead { get; set; }
    internal bool Expanded { get; set; }
    public event Action? EditStarted;
    public event Action? Edited;
    public event Action<bool>? EditFinished;
    public event Action? AuditionRequested;
    private enum Drag { None, Move, TrimLeft, TrimRight, Pitch, Volume, Start, In, Out, End }
    private Drag _drag;
    private MixClip? _before;
    private Vector2 _pointer;
    private bool _changed;
    private string _readout = "";
    private static readonly Color Pink = new("f366d9"), Yellow = new("ffe078"), Orange = new("ff983e");
    private float ClipTop => Expanded ? 38 : 19;
    private float Top => ClipTop + (Expanded ? 20 : 8);
    private float Bottom => Size.Y - (Expanded ? 34 : 32);
    private bool CanEditVolume => ClipData.Volume != null || ClipData.Envelope.Count == 0;
    internal Rect2 ClipBounds => new((float)(ClipData.Start * PixelsPerSecond), ClipTop, Math.Max(2, (float)(ClipData.Duration * PixelsPerSecond)), Size.Y - ClipTop - 16);
    internal Vector2 PitchGrabPosition => new(ClipBounds.GetCenter().X, PitchY(ClipData.Semitones));
    internal Vector2 VolumeGrabPosition => new(ClipBounds.Position.X + (float)(((ClipData.Volume?.HasIn == true ? ClipData.Volume.InEnd : 0) + (ClipData.Volume?.HasOut == true ? ClipData.Volume.OutStart : 1)) / 2) * ClipBounds.Size.X, VolumeY(ClipData.Gain));
    private float PitchY(double pitch) => Top + (float)((24 - pitch) / 48) * (Bottom - Top);
    private float VolumeY(float gain) => Top + (float)((12 - ClipVolume.Decibels(gain)) / 72) * (Bottom - Top);
    private float GainAt(float y) => ClipVolume.FromDecibels(12 - Math.Clamp((y - Top) / Math.Max(1, Bottom - Top), 0, 1) * 72);
    private double SourceAt(float x) => ClipData.TrimStart + Math.Clamp((x - ClipBounds.Position.X) / ClipBounds.Size.X, 0, 1) * (ClipData.TrimEnd - ClipData.TrimStart);
    internal Vector2[] VolumeHandlePositions => ClipData.Volume == null ? Array.Empty<Vector2>() : new[]
    {
        new Vector2(ClipBounds.Position.X, VolumeY(ClipData.Volume.StartGain)),
        new Vector2(ClipBounds.Position.X + (float)ClipData.Volume.InEnd * ClipBounds.Size.X, VolumeY(ClipData.Gain)),
        new Vector2(ClipBounds.Position.X + (float)ClipData.Volume.OutStart * ClipBounds.Size.X, VolumeY(ClipData.Gain)),
        new Vector2(ClipBounds.End.X, VolumeY(ClipData.Volume.EndGain))
    };
    public override void _Ready()
    {
        MouseDefaultCursorShape = CursorShape.Drag;
        FocusMode = FocusModeEnum.All;
        GetWindow().FocusExited += WindowFocusLost;
        MouseExited += () => { if (_drag == Drag.None) { _readout = ""; QueueRedraw(); } };
    }
    private Drag Hit(Vector2 p)
    {
        var v = ClipData.Volume;
        var handles = VolumeHandlePositions;
        // Distance first; stable ordering still gives each coincident handle its own side.
        if (v != null)
        {
            var candidates = new List<(Drag Kind, float Distance, float X)>();
            if (v.HasIn) { candidates.Add((Drag.Start, p.DistanceTo(handles[0]), handles[0].X)); candidates.Add((Drag.In, p.DistanceTo(handles[1]), handles[1].X)); }
            if (v.HasOut) { candidates.Add((Drag.Out, p.DistanceTo(handles[2]), handles[2].X)); candidates.Add((Drag.End, p.DistanceTo(handles[3]), handles[3].X)); }
            var nearest = candidates.OrderBy(c => c.Distance).ThenBy(c => p.X >= c.X ? -(int)c.Kind : (int)c.Kind).FirstOrDefault();
            if (nearest.Kind != Drag.None && nearest.Distance <= 9) return nearest.Kind;
        }
        // Dedicated bottom grips leave the pitch and volume endpoints accessible.
        if (p.Y >= Bottom + 5 && p.Y <= Size.Y - 12)
        {
            if (Math.Abs(p.X - ClipBounds.Position.X) <= 9) return Drag.TrimLeft;
            if (Math.Abs(p.X - ClipBounds.End.X) <= 9) return Drag.TrimRight;
        }
        if (p.X >= ClipBounds.Position.X && p.X <= ClipBounds.End.X && p.Y >= 0 && p.Y < ClipTop) return Drag.Move;
        if (!ClipBounds.HasPoint(p)) return Drag.None;
        float pitchDistance = Math.Abs(p.Y - PitchY(ClipData.Semitones));
        float volumeDistance = CanEditVolume && (v?.HasIn != true || p.X >= handles[1].X) && (v?.HasOut != true || p.X <= handles[2].X) ? Math.Abs(p.Y - VolumeY(ClipData.Gain)) : float.PositiveInfinity;
        // When the bars coincide, their upper side controls pitch and lower side controls volume.
        if (pitchDistance <= 5 && volumeDistance <= 5)
            return Math.Abs(pitchDistance - volumeDistance) < .01f ? (p.Y > PitchY(ClipData.Semitones) ? Drag.Volume : Drag.Pitch) : pitchDistance < volumeDistance ? Drag.Pitch : Drag.Volume;
        if (pitchDistance <= 5) return Drag.Pitch;
        if (volumeDistance <= 5) return Drag.Volume;
        return Drag.Move;
    }
    private string Describe(Drag kind) => kind switch
    {
        Drag.Pitch => $"Pitch {ClipData.Semitones:+0.0;-0.0;0.0} st · {ClipData.Duration:0.000}s",
        Drag.Start => $"Start {ClipVolume.Describe(ClipData.Volume!.StartGain)}",
        Drag.End => $"End {ClipVolume.Describe(ClipData.Volume!.EndGain)}",
        Drag.In => $"Target {ClipVolume.Describe(ClipData.Gain)} · ease in {ClipData.Volume!.InEnd * ClipData.Duration:0.000}s",
        Drag.Out => $"Target {ClipVolume.Describe(ClipData.Gain)} · ease out {(1 - ClipData.Volume!.OutStart) * ClipData.Duration:0.000}s",
        Drag.Volume => $"Volume {ClipVolume.Describe(ClipData.Gain)}",
        Drag.TrimLeft or Drag.TrimRight => $"Trim · {ClipData.Duration:0.000}s",
        _ => "Drag clip to position · bottom corner grips trim · pink bar changes pitch"
    };
    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mouse)
        {
            if (!mouse.Pressed && _drag != Drag.None) { FinishDrag(); AcceptEvent(); return; }
            if (!mouse.Pressed) return;
            _drag = Hit(mouse.Position);
            if (_drag == Drag.None) return;
            GrabFocus();
            _pointer = mouse.Position; _before = ClipData.Clone(); _changed = false;
            _readout = Describe(_drag); QueueRedraw(); AcceptEvent();
        }
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { CancelGesture(); AcceptEvent(); return; }
        if (e is not InputEventMouseMotion motion) return;
        if (_drag == Drag.None) { _readout = Describe(Hit(motion.Position)); TooltipText = _readout; QueueRedraw(); return; }
        if (_drag == Drag.Move) return; // Native drag-and-drop moves clips between lanes.
        var before = _before!;
        var candidate = before.Clone();
        double dx = (motion.Position.X - _pointer.X) / PixelsPerSecond;
        float dy = motion.Position.Y - _pointer.Y;
        switch (_drag)
        {
            case Drag.Move: candidate.Start = Math.Max(0, before.Start + dx); break;
            case Drag.TrimLeft:
                candidate.TrimStart = Math.Clamp(before.TrimStart + dx * before.Rate, Math.Max(0, before.TrimStart - before.Start * before.Rate), before.TrimEnd - 1.0 / PcmSource.SampleRate);
                candidate.Start = before.Start + (candidate.TrimStart - before.TrimStart) / before.Rate; break;
            case Drag.TrimRight: candidate.TrimEnd = Math.Clamp(before.TrimEnd + dx * before.Rate, before.TrimStart + 1.0 / PcmSource.SampleRate, before.SourceDuration); break;
            case Drag.Pitch: candidate.Semitones = Math.Round(Math.Clamp(before.Semitones - dy / Math.Max(1, Bottom - Top) * 48, -24, 24), 1); break;
            case Drag.Volume:
                if (dy == 0) { candidate.Gain = before.Gain; candidate.Volume = before.Volume; break; }
                candidate.Volume ??= new VolumeEasing(); candidate.Gain = GainAt(VolumeY(before.Gain) + dy); break;
            case Drag.Start: candidate.Volume!.StartGain = dy == 0 ? before.Volume!.StartGain : GainAt(VolumeY(before.Volume!.StartGain) + dy); break;
            case Drag.End: candidate.Volume!.EndGain = dy == 0 ? before.Volume!.EndGain : GainAt(VolumeY(before.Volume!.EndGain) + dy); break;
            case Drag.In:
                candidate.Gain = dy == 0 ? before.Gain : GainAt(VolumeY(before.Gain) + dy);
                candidate.Volume!.InEnd = Math.Clamp(before.Volume!.InEnd + dx / Math.Max(1e-9, before.Duration), 0, candidate.Volume.HasOut ? candidate.Volume.OutStart : 1); break;
            case Drag.Out:
                candidate.Gain = dy == 0 ? before.Gain : GainAt(VolumeY(before.Gain) + dy);
                candidate.Volume!.OutStart = Math.Clamp(before.Volume!.OutStart + dx / Math.Max(1e-9, before.Duration), candidate.Volume.HasIn ? candidate.Volume.InEnd : 0, 1); break;
        }
        if (!Same(candidate, ClipData))
        {
            if (!_changed) { EditStarted?.Invoke(); _changed = true; }
            CandidateEdited?.Invoke(candidate);
            Edited?.Invoke();
        }
        _readout = Describe(_drag); QueueRedraw(); AcceptEvent();
    }
    private static bool Same(MixClip a, MixClip b) => System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);
    private void FinishDrag(bool audition = true)
    {
        bool changed = _changed && !Same(_before!, ClipData);
        var completedDrag = _drag;
        _drag = Drag.None; _before = null; _readout = ""; QueueRedraw();
        if (_changed) EditFinished?.Invoke(changed);
        else if (audition) Clicked?.Invoke();
        _changed = false;
        if (audition && changed && completedDrag is Drag.Pitch or Drag.Volume or Drag.Start or Drag.In or Drag.Out or Drag.End) AuditionRequested?.Invoke();
    }
    internal void CancelGesture()
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None; _before = null; _readout = "";
        if (_changed) EditCanceled?.Invoke();
        _changed = false; QueueRedraw();
    }
    private void WindowFocusLost() => CancelGesture();
    public override bool _HasPoint(Vector2 point) => new Rect2(ClipBounds.Position.X - 9, 0, ClipBounds.Size.X + 18, Size.Y).HasPoint(point);
    public override Variant _GetDragData(Vector2 position)
    {
        if (_drag != Drag.Move) return default;
        double offset = _pointer.X / PixelsPerSecond - ClipData.Start;
        _drag = Drag.None; _before = null; _readout = "";
        SetDragPreview(Skin.Label(ClipData.Name, 14, Skin.Accent));
        return new Godot.Collections.Dictionary { ["kind"] = "mix-clip", ["track"] = TrackId.ToString(), ["clip"] = ClipData.Id.ToString(), ["offset"] = offset };
    }
    public override bool _CanDropData(Vector2 position, Variant data) => ((TrackLane)GetParent())._CanDropData(position, data);
    public override void _DropData(Vector2 position, Variant data) => ((TrackLane)GetParent())._DropData(position, data);
    public override void _ExitTree() => GetWindow().FocusExited -= WindowFocusLost;
    public override void _Draw()
    {
        float visibleLeft = Math.Max(0, -GlobalPosition.X), visibleRight = Math.Min(Size.X, GetViewportRect().Size.X - GlobalPosition.X);
        var clip = ClipBounds;
        DrawStyleBox(Skin.Box(!Audible ? new("424b58") : new("325875"), 4, Selected ? Skin.Accent : Skin.Border), clip);
        float center = (Top + Bottom) / 2;
        for (float x = Math.Max(clip.Position.X + 1, visibleLeft); x < Math.Min(clip.End.X, visibleRight); x += 2)
        {
            double seconds = SourceAt(x);
            if (Peaks.Length > 0)
            {
                int i = Math.Clamp((int)(seconds / Math.Max(.0001, ClipData.SourceDuration) * Peaks.Length), 0, Peaks.Length - 1);
                float h = Math.Min(.95f, Peaks[i] * ClipVolume.Gain(ClipData, seconds)) * (Bottom - Top) / 2;
                DrawLine(new(x, center - h), new(x, center + h), !Audible ? Skin.Muted : new("8ab9d5"));
            }
        }
        DrawLine(new(clip.Position.X, center), new(clip.End.X, center), new Color(1, 1, 1, .15f));
        float left = Math.Max(clip.Position.X, visibleLeft), right = Math.Min(clip.End.X, visibleRight);
        if (right >= left)
        {
            Vector2 previous = new(left, VolumeY(ClipVolume.Gain(ClipData, SourceAt(left))));
            for (float x = left + 1; x <= right + 1; x += 2)
            {
                float end = Math.Min(x, right);
                var next = new Vector2(end, VolumeY(ClipVolume.Gain(ClipData, SourceAt(end))));
                DrawLine(previous, next, Yellow, 2, true); previous = next;
            }
        }
        DrawLine(new(clip.Position.X, PitchY(ClipData.Semitones)), new(clip.End.X, PitchY(ClipData.Semitones)), Pink, 3, true);
        var v = ClipData.Volume; var points = VolumeHandlePositions;
        if (v != null)
        {
            if (v.HasIn) { DrawCircle(points[0], 6, Orange); DrawCircle(points[1], 6, Orange); }
            if (v.HasOut) { DrawCircle(points[2], 6, Orange); DrawCircle(points[3], 6, Orange); }
        }
        foreach (float x in new[] { clip.Position.X, clip.End.X })
        {
            DrawRect(new(x - 5, Bottom + 6, 10, 10), Skin.Text);
            DrawLine(new(x, Bottom + 8), new(x, Bottom + 14), Skin.Background, 2);
        }
        DrawString(ThemeDB.FallbackFont, new(Math.Max(clip.Position.X + 4, visibleLeft + 4), Expanded ? 21 : 14), string.IsNullOrEmpty(_readout) ? ClipData.Name : _readout, width: Math.Max(0, clip.End.X - Math.Max(clip.Position.X + 4, visibleLeft + 4)), fontSize: 11, modulate: Skin.Text);
        float playhead = (float)(Playhead * PixelsPerSecond); DrawLine(new(playhead, 0), new(playhead, Size.Y), new Color("f9d780"), 1.5f);
    }
}
