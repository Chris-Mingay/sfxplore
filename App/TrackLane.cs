using Godot;
using SoundShelf.Core;

namespace SoundShelf.App;

public partial class TrackLane : Control
{
    public MixTrack Track { get; set; } = new();
    public bool NewTrack { get; set; }
    public bool Expanded { get; set; }
    public bool Audible { get; set; } = true;
    public double PixelsPerSecond { get; set; } = 80;
    public double Playhead { get; set; }
    public Func<Variant, bool>? ValidateDrop { get; set; }
    public Func<Variant, double>? DropDuration { get; set; }
    public Action<Variant, double>? Dropped { get; set; }
    public Action<ClipView>? ConfigureClip { get; set; }
    public Func<string, float[]>? GetPeaks { get; set; }
    public Guid? SelectedClip { get; set; }
    private readonly Dictionary<Guid, ClipView> _views = new();
    private double? _dropStart;
    private double _dropDuration;
    private LaneDropPreview _preview = null!;
    internal IEnumerable<ClipView> ClipViews => _views.Values;

    public void SyncClips()
    {
        foreach (var id in _views.Keys.Where(id => !Track.Clips.Any(c => c.Id == id)).ToArray())
        {
            var view = _views[id]; RemoveChild(view); view.QueueFree(); _views.Remove(id);
        }
        foreach (var clip in Track.Clips)
        {
            if (!_views.TryGetValue(clip.Id, out var view))
            {
                view = new ClipView { TrackId = Track.Id, MouseFilter = MouseFilterEnum.Stop };
                _views.Add(clip.Id, view); AddChild(view); ConfigureClip?.Invoke(view);
            }
            view.ClipData = clip; view.Peaks = GetPeaks?.Invoke(clip.SourcePath) ?? Array.Empty<float>();
            view.Size = Size; view.PixelsPerSecond = PixelsPerSecond;
            view.Expanded = Expanded; view.Audible = Audible; view.Selected = SelectedClip == clip.Id;
            view.Playhead = Playhead; view.QueueRedraw();
        }
        QueueRedraw();
    }

    internal static double DropTime(Vector2 position, double pixelsPerSecond, Variant data)
    {
        var payload = data.AsGodotDictionary();
        double offset = payload.TryGetValue("offset", out var value) ? value.AsDouble() : 0;
        return Math.Max(0, position.X / pixelsPerSecond - offset);
    }

    public override bool _CanDropData(Vector2 position, Variant data)
    {
        if (ValidateDrop?.Invoke(data) != true) { _dropStart = null; QueueRedraw(); return false; }
        _dropStart = DropTime(position, PixelsPerSecond, data);
        _dropDuration = DropDuration?.Invoke(data) ?? 1;
        QueueRedraw(); return true;
    }

    public override void _DropData(Vector2 position, Variant data)
    {
        if (ValidateDrop?.Invoke(data) == true) Dropped?.Invoke(data, DropTime(position, PixelsPerSecond, data));
        _dropStart = null; QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_dropStart.HasValue && (!GetViewport().GuiIsDragging() || !GetGlobalRect().HasPoint(GetGlobalMousePosition())))
        { _dropStart = null; QueueRedraw(); }
    }
    public override void _Ready()
    {
        _preview = new LaneDropPreview { ZIndex = 2, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_preview); Resized += SyncClips;
        Draw += () =>
        {
            _preview.Size = Size; _preview.Start = _dropStart; _preview.Duration = _dropDuration;
            _preview.PixelsPerSecond = PixelsPerSecond; _preview.QueueRedraw();
        };
    }
    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), Skin.Background);
        float left = Math.Max(0, -GlobalPosition.X), right = Math.Min(Size.X, GetViewportRect().Size.X - GlobalPosition.X);
        if (NewTrack)
        {
            var color = _dropStart.HasValue ? Skin.Accent : Skin.Muted;
            for (float x = left + 2; x < right - 2; x += 14)
            {
                DrawLine(new(x, 2), new(Math.Min(x + 8, right - 2), 2), color);
                DrawLine(new(x, Size.Y - 2), new(Math.Min(x + 8, right - 2), Size.Y - 2), color);
            }
            for (float y = 2; y < Size.Y - 2; y += 14)
            {
                DrawLine(new(left + 2, y), new(left + 2, Math.Min(y + 8, Size.Y - 2)), color);
                DrawLine(new(right - 2, y), new(right - 2, Math.Min(y + 8, Size.Y - 2)), color);
            }
            DrawString(ThemeDB.FallbackFont, new(left + 16, 30), "Drop to create track", fontSize: 14, modulate: color);
        }
        else
        {
            double step = TimelineRuler.TickStep(PixelsPerSecond);
            for (double s = Math.Floor(left / PixelsPerSecond / step) * step; s * PixelsPerSecond < right; s += step)
                DrawLine(new((float)(s * PixelsPerSecond), 0), new((float)(s * PixelsPerSecond), Size.Y), new Color("303947"));
            if (Track.Clips.Count == 0) DrawString(ThemeDB.FallbackFont, new(left + 16, 48), "Drop a sound here", fontSize: 13, modulate: Skin.Muted);
        }
        float playhead = (float)(Playhead * PixelsPerSecond);
        DrawLine(new(playhead, 0), new(playhead, Size.Y), new Color("f9d780"), 1.5f);
    }
}

public partial class LaneDropPreview : Control
{
    public double? Start { get; set; }
    public double Duration { get; set; }
    public double PixelsPerSecond { get; set; }
    public override void _Draw()
    {
        if (Start is not double start) return;
        var rect = new Rect2((float)(start * PixelsPerSecond), 4, Math.Max(4, (float)(Duration * PixelsPerSecond)), Size.Y - 8);
        DrawRect(rect, new Color(Skin.Accent, .3f)); DrawRect(rect, Skin.Accent, false, 2);
        DrawString(ThemeDB.FallbackFont, rect.Position + new Vector2(6, 18), $"{start:0.000}s", fontSize: 12, modulate: Skin.Text);
    }
}
