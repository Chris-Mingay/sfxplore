using Godot;
using SoundShelf.Core;

namespace SoundShelf.App;

public partial class Main
{
    private TrackLane _newTrackLane = null!;
    private Control _newTrackSpacer = null!;
    private bool _mixDragging, _drawerBeforeDrag, _dropAccepted;
    private int _mixGeneration, _dragGeneration;
    private double? _dragSourceDuration;
    private MixProject? _gestureBefore;
    private Guid? _gestureTrackId;
    private Guid? _selectedTrackId, _selectedClipId;
    private PanelContainer? _clipPopup;
    private readonly List<Action> _syncClipControls = new();
    private bool _syncingClipPopup;

    private TrackLane CreateTrackLane(MixTrack track, bool newTrack = false)
    {
        var lane = new TrackLane { Track = track, NewTrack = newTrack, PixelsPerSecond = _pixelsPerSecond,
            CustomMinimumSize = new(1200, CompactTrackHeight), GetPeaks = path => _peaks.GetValueOrDefault(path) ?? Array.Empty<float>(),
            ValidateDrop = CanDropMixSound, DropDuration = MixDragDuration };
        lane.Dropped = (data, start) => DropMixSound(data, newTrack ? null : track.Id, start);
        lane.ConfigureClip = view =>
        {
            view.Clicked += () => ShowClipPopup(track.Id, view.ClipData.Id);
            view.EditStarted += () =>
            {
                HideClipPopup(); _saveTimer.Stop(); _gestureBefore = _mix.Clone(); _gestureTrackId = track.Id;
            };
            view.CandidateEdited += candidate =>
            {
                if (_gestureBefore == null) return;
                track.Clips = _gestureBefore.Tracks.First(t => t.Id == track.Id).Clips.Select(c => c.Clone()).ToList();
                MixEditing.Place(_mix, track.Id, candidate); lane.SyncClips();
            };
            view.Edited += MixEdited;
            view.EditCanceled += CancelClipGesture;
            view.EditFinished += _ =>
            {
                if (_gestureBefore != null && System.Text.Json.JsonSerializer.Serialize(_gestureBefore) != System.Text.Json.JsonSerializer.Serialize(_mix))
                    _history.Capture(_gestureBefore);
                _gestureBefore = null; _gestureTrackId = null; MixEdited(); SaveMix(); UpdateLiveMix();
            };
            view.AuditionRequested += () => _ = Guard(() => PlayClip(track.Id, view.ClipData.Id));
        };
        return lane;
    }

    private void CancelClipGesture()
    {
        var before = _gestureBefore; var trackId = _gestureTrackId;
        _gestureBefore = null; _gestureTrackId = null;
        foreach (var view in _trackLanes.SelectMany(l => l.ClipViews).ToArray()) view.CancelGesture();
        if (before == null) return;
        var track = _mix.Tracks.FirstOrDefault(t => t.Id == trackId);
        if (track != null) track.Clips = before.Tracks.First(t => t.Id == trackId).Clips.Select(c => c.Clone()).ToList();
        foreach (var lane in _trackLanes) lane.SyncClips();
        MixEdited(); UpdateLiveMix();
    }

    private bool CanDropMixSound(Variant data)
    {
        if (data.VariantType != Variant.Type.Dictionary) return false;
        var payload = data.AsGodotDictionary();
        if (!payload.TryGetValue("kind", out var kind)) return false;
        if (kind.AsString() == "mix-sound")
            return _audio != null && payload.ContainsKey("sound") && payload.TryGetValue("path", out var path) && File.Exists(path.AsString());
        return kind.AsString() == "mix-clip" && payload.TryGetValue("clip", out var id) && Guid.TryParse(id.AsString(), out var clipId)
            && _mix.Clips.Any(c => c.Id == clipId);
    }

    private double MixDragDuration(Variant data)
    {
        var payload = data.AsGodotDictionary();
        if (payload["kind"].AsString() == "mix-clip")
            return _mix.Clips.First(c => c.Id == Guid.Parse(payload["clip"].AsString())).Duration;
        return _prepared.GetValueOrDefault(payload["path"].AsString())?.Duration ?? _dragSourceDuration ?? 1;
    }

    private void DropMixSound(Variant data, Guid? trackId, double start)
    {
        if (!CanDropMixSound(data)) return;
        _dropAccepted = true; HideClipPopup();
        var payload = data.AsGodotDictionary();
        if (payload["kind"].AsString() == "mix-sound")
        {
            _ = Guard(() => AddSoundClip(payload["sound"].AsInt64(), payload["path"].AsString(), trackId, start));
            return;
        }
        var clip = _mix.Clips.First(c => c.Id == Guid.Parse(payload["clip"].AsString())).Clone();
        if (trackId.HasValue && _mix.Tracks.First(t => t.Id == trackId).Clips.Any(c => c.Id == clip.Id) && Math.Abs(clip.Start - start) < 1e-9) return;
        _history.Capture(_mix);
        var track = trackId.HasValue ? _mix.Tracks.First(t => t.Id == trackId) : new MixTrack();
        if (!trackId.HasValue) _mix.Tracks.Insert(0, track);
        clip.Start = start; MixEditing.Place(_mix, track.Id, clip);
        RebuildTracks(); MixEdited(); SaveMix(); UpdateLiveMix();
    }

    private async Task PrepareDragDuration(string path, int generation)
    {
        try
        {
            if (_audio == null) return;
            var source = await _audio.LoadAsync(path);
            if (_mixDragging && generation == _dragGeneration) _dragSourceDuration = source.Duration;
        }
        catch { /* A rejected decode is reported if the user drops the file. */ }
    }

    private void ProcessMixerInteraction(double delta)
    {
        var viewport = GetViewport();
        bool dragging = viewport.GuiIsDragging() && CanDropMixSound(viewport.GuiGetDragData());
        if (dragging && !_mixDragging)
        {
            _mixDragging = true; _dropAccepted = false; _dragSourceDuration = null; _dragGeneration++;
            HideClipPopup(); _drawerBeforeDrag = _mixPanel.Visible; _mixPanel.Show();
            _newTrackLane.Show(); _newTrackSpacer.Show(); _tracksScroll.ScrollVertical = 0;
            var payload = viewport.GuiGetDragData().AsGodotDictionary();
            if (payload["kind"].AsString() == "mix-sound") _ = PrepareDragDuration(payload["path"].AsString(), _dragGeneration);
        }
        else if (!dragging && _mixDragging)
        {
            _mixDragging = false; _dragGeneration++; _newTrackLane.Hide(); _newTrackSpacer.Hide();
            if (!_dropAccepted) _mixPanel.Visible = _drawerBeforeDrag;
        }
        if (dragging)
        {
            var pointer = GetGlobalMousePosition(); var rect = _tracksScroll.GetGlobalRect();
            if (rect.HasPoint(pointer))
            {
                if (pointer.Y > rect.End.Y - 24) _tracksScroll.ScrollVertical += (int)(500 * delta);
                else if (pointer.Y < rect.Position.Y + 24) _tracksScroll.ScrollVertical -= (int)(500 * delta);
                var timeline = _timelineScroll.GetGlobalRect();
                if (pointer.X > timeline.End.X - 24) _timelineScroll.ScrollHorizontal += (int)(500 * delta);
                else if (pointer.X >= timeline.Position.X && pointer.X < timeline.Position.X + 24) _timelineScroll.ScrollHorizontal -= (int)(500 * delta);
            }
        }
        PositionClipPopup();
    }

    private async Task PlayClip(Guid trackId, Guid clipId)
    {
        var clip = _mix.Tracks.FirstOrDefault(t => t.Id == trackId)?.Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip == null) return;
        StopMix(false); _soloTrackId = trackId; _soloClipId = clipId; _transport = clip.Start;
        int requestVersion = _playVersion + 1;
        try { await StartMixPlayback(); }
        catch { if (_playVersion == requestVersion) StopMix(false); throw; }
    }

    private MixClip? SelectedClip() => _mix.Tracks.FirstOrDefault(t => t.Id == _selectedTrackId)?.Clips.FirstOrDefault(c => c.Id == _selectedClipId);

    private void HideClipPopup()
    {
        _clipPopup?.Hide(); _selectedClipId = null; _selectedTrackId = null; _syncClipControls.Clear();
        foreach (var lane in _trackLanes) { lane.SelectedClip = null; lane.SyncClips(); }
    }

    private void RefreshClipSelection()
    {
        if (SelectedClip() == null) { HideClipPopup(); return; }
        foreach (var lane in _trackLanes) { lane.SelectedClip = lane.Track.Id == _selectedTrackId ? _selectedClipId : null; lane.SyncClips(); }
        SyncClipPopup();
    }

    private void ChangeClip(Action<MixClip> change, bool audition = true)
    {
        var clip = SelectedClip(); if (clip == null || _syncingClipPopup) return;
        Guid trackId = _selectedTrackId!.Value, clipId = clip.Id;
        var candidate = clip.Clone(); change(candidate);
        if (System.Text.Json.JsonSerializer.Serialize(candidate) == System.Text.Json.JsonSerializer.Serialize(clip)) return;
        _history.Capture(_mix); MixEditing.Place(_mix, trackId, candidate);
        foreach (var lane in _trackLanes) lane.SyncClips();
        MixEdited(); SaveMix();
        if (audition) _ = Guard(() => PlayClip(trackId, clipId));
    }

    private void SyncClipPopup()
    {
        if (_clipPopup?.Visible != true || SelectedClip() == null) return;
        _syncingClipPopup = true;
        try { foreach (var sync in _syncClipControls) sync(); }
        finally { _syncingClipPopup = false; }
    }

    private void ShowClipPopup(Guid trackId, Guid clipId)
    {
        _selectedTrackId = trackId; _selectedClipId = clipId;
        var clip = SelectedClip(); if (clip == null) { HideClipPopup(); return; }
        if (_clipPopup != null) { RemoveChild(_clipPopup); _clipPopup.QueueFree(); }
        _syncClipControls.Clear();
        _clipPopup = new PanelContainer { Name = "ClipPopup", ZIndex = 100, CustomMinimumSize = new(340, 0), MouseFilter = MouseFilterEnum.Stop };
        _clipPopup.AddThemeStyleboxOverride("panel", Skin.Box(Skin.Panel, 6, Skin.Accent)); AddChild(_clipPopup);
        var body = new VBoxContainer(); _clipPopup.AddChild(body);
        var heading = new HBoxContainer(); body.AddChild(heading);
        var name = Skin.Label(clip.Name, 13); name.SizeFlagsHorizontal = SizeFlags.ExpandFill; name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; heading.AddChild(name);
        heading.AddChild(Skin.Button("×", HideClipPopup, "Close clip controls · Escape"));
        void Field(string label, string nodeName, double value, double min, double max, double step, string suffix, Action<MixClip, double> update, Func<MixClip, double> read, bool audition = true)
        {
            var row = new HBoxContainer(); body.AddChild(row);
            var title = Skin.Label(label, 12, Skin.Muted); title.CustomMinimumSize = new(80, 0); row.AddChild(title);
            var number = new SpinBox { Name = nodeName, Value = value, MinValue = min, MaxValue = max, Step = step, Suffix = suffix, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddChild(number); number.ValueChanged += v => ChangeClip(c => update(c, v), audition);
            _syncClipControls.Add(() => { if (SelectedClip() is { } selected) number.SetValueNoSignal(read(selected)); });
        }
        Field("Pitch", "ClipPitch", clip.Semitones, -24, 24, .1, "st", (c, v) => c.Semitones = v, c => c.Semitones);
        Field("Volume", "ClipGain", ClipVolume.Decibels(clip.Gain), -60, 12, .5, "dB", (c, v) => c.Gain = ClipVolume.FromDecibels(v), c => ClipVolume.Decibels(c.Gain));
        Field("Start", "ClipStart", clip.Start, 0, 86400, .01, "s", (c, v) => c.Start = v, c => c.Start, false);
        var modeRow = new HBoxContainer(); body.AddChild(modeRow); modeRow.AddChild(Skin.Label("Volume easing", 12, Skin.Muted));
        var mode = new OptionButton { Name = "VolumeMode", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (string text in new[] { "None", "Ease In", "Ease Out", "Both" }) mode.AddItem(text);
        bool legacy = clip.Volume == null && clip.Envelope.Count > 0;
        mode.Select(legacy ? -1 : (int)(clip.Volume?.Mode ?? VolumeMode.None));
        if (legacy) { mode.Text = "Choose easing…"; mode.TooltipText = "Saved envelope is preserved until you choose a mode. Undo restores it."; }
        modeRow.AddChild(mode);
        mode.ItemSelected += index =>
        {
            ChangeClip(c => { c.Volume ??= new VolumeEasing(); c.Volume.Mode = (VolumeMode)index; });
            Callable.From(() => { if (_selectedTrackId == trackId && _selectedClipId == clipId) ShowClipPopup(trackId, clipId); }).CallDeferred();
        };
        void Method(string label, string nodeName, EasingMethod selected, bool incoming)
        {
            var row = new HBoxContainer(); body.AddChild(row); row.AddChild(Skin.Label(label, 12, Skin.Muted));
            var method = new OptionButton { Name = nodeName, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            foreach (var item in Enum.GetValues<EasingMethod>()) method.AddItem(item.ToString());
            method.Select((int)selected); row.AddChild(method);
            method.ItemSelected += index => ChangeClip(c => { if (incoming) c.Volume!.InMethod = (EasingMethod)index; else c.Volume!.OutMethod = (EasingMethod)index; });
        }
        if (clip.Volume?.HasIn == true) Method("Ease In Method", "EaseInMethod", clip.Volume.InMethod, true);
        if (clip.Volume?.HasOut == true) Method("Ease Out Method", "EaseOutMethod", clip.Volume.OutMethod, false);
        var actions = new HBoxContainer(); body.AddChild(actions);
        actions.AddChild(Skin.Button("−∞", () => ChangeClip(c => c.Gain = 0), "Set this clip to exact silence"));
        actions.AddChild(Skin.Button("Audition", () => _ = Guard(() => PlayClip(trackId, clipId))));
        actions.AddChild(Skin.Button("Remove clip", () =>
        {
            if (SelectedClip() is not { } selected) return;
            _history.Capture(_mix); _mix.Tracks.First(t => t.Id == _selectedTrackId).Clips.Remove(selected);
            HideClipPopup(); RebuildTracks(); MixEdited(); SaveMix();
        }));
        _clipPopup.ResetSize(); RefreshClipSelection();
        Callable.From(PositionClipPopup).CallDeferred();
    }

    private void PositionClipPopup()
    {
        if (_clipPopup?.Visible != true) return;
        var lane = _trackLanes.FirstOrDefault(l => l.Track.Id == _selectedTrackId);
        var view = lane?.ClipViews.FirstOrDefault(v => v.ClipData.Id == _selectedClipId);
        if (view == null || !_mixPanel.IsVisibleInTree()) { HideClipPopup(); return; }
        var anchor = new Rect2(view.GlobalPosition + view.ClipBounds.Position, view.ClipBounds.Size);
        var visible = anchor.Intersection(_timelineScroll.GetGlobalRect()).Intersection(_tracksScroll.GetGlobalRect());
        if (!visible.HasArea()) { _clipPopup.Hide(); return; }
        var viewport = GetViewportRect(); var size = _clipPopup.Size;
        float y = visible.Position.Y - size.Y - 8;
        if (y < 4) y = visible.End.Y + 8;
        _clipPopup.Position = new(Math.Clamp(visible.GetCenter().X - size.X / 2, 4, Math.Max(4, viewport.Size.X - size.X - 4)), Math.Clamp(y, 4, Math.Max(4, viewport.Size.Y - size.Y - 4)));
    }

    private bool HandleClipInput(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            if (GetViewport().GuiIsDragging() && CanDropMixSound(GetViewport().GuiGetDragData()))
            { GetViewport().GuiCancelDrag(); return true; }
            if (_gestureBefore != null) { CancelClipGesture(); return true; }
            if (_clipPopup?.Visible == true) { HideClipPopup(); return true; }
        }
        if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right } mouse && _clipPopup?.Visible == true && !_clipPopup.GetGlobalRect().HasPoint(mouse.Position))
            HideClipPopup();
        return false;
    }
}
