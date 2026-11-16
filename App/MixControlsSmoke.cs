using Godot;
using SoundShelf.Core;
using System.Text.Json;

namespace SoundShelf.App;

public partial class Main
{
    private async Task CaptureMixControls(string directory)
    {
        Directory.CreateDirectory(directory);
        var original = _mix.Clone(); var offsets = _mainSplit.SplitOffsets; double zoom = _pixelsPerSecond;
        try
        {
            _mainSplit.SplitOffsets = new[] { 200 }; _mixPanel.Show();
            var source = _mix.Clips.OrderByDescending(c => c.SourceDuration).First().Clone();
            source.Start = .1; source.TrimStart = 0; source.TrimEnd = Math.Min(1.4, source.SourceDuration); source.Semitones = 0;
            source.Volume = new() { Mode = VolumeMode.Both, InMethod = EasingMethod.easeInOutCirc };
            var second = source.Clone(); second.Id = Guid.NewGuid(); second.Start = source.End + .3; second.Gain = .5f; second.Semitones = 7;
            var third = source.Clone(); third.Id = Guid.NewGuid(); third.TrimEnd = .08;
            _mix.Tracks = new() { new() { Name = "Effects", Clips = new() { source, second } }, new() { Name = "Details", Clips = new() { third } }, new() { Name = "Empty track" } };
            RebuildTracks();
            async Task Capture(string name)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, name + ".png"));
            }
            await Capture("mix-controls-compact");
            _expandedTracks.Add(_mix.Tracks[0].Id); RebuildTracks();
            foreach (double scale in new[] { 80.0, 10.0, 300.0, 3000.0 })
            {
                SetTimelineZoom(scale); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                _timelineScroll.ScrollHorizontal = 0; await Capture($"mix-controls-{scale:0}");
                var panels = _trackControls.GetChildren().OfType<PanelContainer>().ToArray();
                for (int i = 0; i < panels.Length; i++)
                    if (Math.Abs(panels[i].GlobalPosition.Y - _trackLanes[i].GlobalPosition.Y) > 1 || Math.Abs(panels[i].Size.Y - _trackLanes[i].Size.Y) > 1)
                        throw new InvalidOperationException("Track headers and lanes must remain aligned.");
            }
            SetTimelineZoom(160); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            ShowClipPopup(_mix.Tracks[0].Id, source.Id); await Capture("mix-clip-popup");
            HideClipPopup(); _newTrackLane.Show(); _newTrackSpacer.Show(); await Capture("mix-new-track-drop");
        }
        finally { HideClipPopup(); _mix = original; _expandedTracks.Clear(); _mainSplit.SplitOffsets = offsets; SetTimelineZoom(zoom); RebuildTracks(); SaveMix(); }
    }

    private async Task ExerciseMixControls()
    {
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("Mix controls: " + message); }
        async Task Layout() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        var original = _mix.Clone(); var offsets = _mainSplit.SplitOffsets; double zoom = _pixelsPerSecond;
        try
        {
            var source = original.Clips.OrderByDescending(c => c.SourceDuration).First().Clone();
            source.Start = .1; source.TrimStart = 0; source.TrimEnd = Math.Min(1.5, source.SourceDuration); source.Semitones = 0; source.Gain = 1; source.Volume = new();
            _mix.Tracks = new() { new() { Name = "Effects", Clips = new() { source } }, new() { Name = "Second track" } };
            _mixPanel.Show(); _mainSplit.SplitOffsets = new[] { 200 }; SetTimelineZoom(160); RebuildTracks(); await Layout();
            Guid trackId = _mix.Tracks[0].Id, clipId = source.Id;
            ClipView View() => _trackLanes.First(l => l.Track.Id == trackId).ClipViews.First(v => v.ClipData.Id == clipId);
            void DragHandle(Vector2 from, Vector2 to)
            {
                var view = View(); view._GuiInput(new InputEventMouseButton { Position = from, ButtonIndex = MouseButton.Left, Pressed = true });
                view._GuiInput(new InputEventMouseMotion { Position = to }); view._GuiInput(new InputEventMouseButton { Position = to, ButtonIndex = MouseButton.Left });
            }
            var point = View().PitchGrabPosition;
            DragHandle(point, point + new Vector2(0, -8)); await Layout();
            Check(View().ClipData.Semitones > 0 && _soloClipId == clipId, "inline pitch and clip audition");
            RestoreHistory(false); await Layout(); Check(View().ClipData.Semitones == 0, "one undo per gesture"); StopMix(true);
            point = View().VolumeGrabPosition; DragHandle(point, point + new Vector2(0, 8)); await Layout();
            Check(View().ClipData.Gain < 1, "inline clip volume"); RestoreHistory(false); await Layout(); StopMix(true);
            string before = JsonSerializer.Serialize(_mix); point = View().PitchGrabPosition;
            View()._GuiInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = true });
            View()._GuiInput(new InputEventMouseMotion { Position = point + new Vector2(0, -10) });
            Check(_gestureBefore != null, "transactional gesture"); CancelClipGesture();
            Check(JsonSerializer.Serialize(_mix) == before, "cancel restores gesture");
            point = View().ClipBounds.Position + new Vector2(15, 5);
            View()._GuiInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = true });
            View()._GuiInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left }); await Layout();
            Check(_clipPopup?.Visible == true && _selectedClipId == clipId, "click opens popup");
            Check(_trackControls.FindChildren("ClipPitch", "SpinBox", true, false).Count == 0, "clip controls absent from headers");
            T Popup<T>(string name) where T : Node => (T)_clipPopup!.FindChild(name, true, false);
            Popup<SpinBox>("ClipPitch").Value = 3; await Layout(); Check(View().ClipData.Semitones == 3 && _soloClipId == clipId, "popup pitch");
            Popup<SpinBox>("ClipGain").Value = -12; await Layout(); Check(View().ClipData.Gain < .3f, "popup volume");
            foreach (var mode in Enum.GetValues<VolumeMode>())
            {
                Popup<OptionButton>("VolumeMode").EmitSignal(OptionButton.SignalName.ItemSelected, (long)mode); await Layout();
                Check(View().ClipData.Volume?.Mode == mode, "clip easing mode");
                Check(_clipPopup!.FindChildren("EaseInMethod", "OptionButton", true, false).Count == (mode is VolumeMode.EaseIn or VolumeMode.Both ? 1 : 0), "easing selectors");
            }
            RestoreHistory(false); await Layout();
            Check(Popup<OptionButton>("VolumeMode").Selected == (int)View().ClipData.Volume!.Mode, "undo refreshes open popup selectors");
            RestoreHistory(true); await Layout();
            Popup<OptionButton>("VolumeMode").EmitSignal(OptionButton.SignalName.ItemSelected, (long)VolumeMode.Both); await Layout();
            var handles = View().VolumeHandlePositions; DragHandle(handles[0], handles[0] + new Vector2(0, -8)); await Layout();
            Check(View().ClipData.Volume!.StartGain > 0, "fade endpoint"); StopMix(true);
            var browserTile = _grid.GetChildren().OfType<BrowserTile>().First(t => !t.Entry.IsFolder);
            var sound = browserTile.MixDragData(); Check(CanDropMixSound(sound), "browser sound payload");
            _mixPanel.Hide(); browserTile.ForceDrag(sound, Skin.Label(browserTile.Entry.Name)); await Layout();
            Check(_mixPanel.Visible && _newTrackLane.Visible, "drag reveals a hidden drawer");
            HandleClipInput(new InputEventKey { Pressed = true, Keycode = Key.Escape }); await Layout();
            Check(!_mixPanel.Visible && _mix.Tracks[0].Clips.Count == 1, "canceled drag restores hidden drawer without adding a clip"); _mixPanel.Show();
            browserTile.ForceDrag(sound, Skin.Label(browserTile.Entry.Name)); await Layout();
            Check(_newTrackLane.Visible && _newTrackSpacer.Visible, "native drag reveals dashed new-track row");
            var dropPoint = _trackLanes[0].GlobalPosition + new Vector2(3 * (float)_pixelsPerSecond, 50);
            bool headless = DisplayServer.GetName() == "headless";
            if (headless)
            {
                // Headless viewports have no OS pointer for Godot's native drop-position lookup.
                GetViewport().GuiCancelDrag(); _trackLanes[0]._DropData(new(3 * (float)_pixelsPerSecond, 50), sound);
            }
            else
            {
                Input.WarpMouse(dropPoint); await Layout();
                GetViewport().PushInput(new InputEventMouseMotion { Position = dropPoint, GlobalPosition = dropPoint, ButtonMask = MouseButtonMask.Left }, true);
                GetViewport().PushInput(new InputEventMouseButton { Position = dropPoint, GlobalPosition = dropPoint, ButtonIndex = MouseButton.Left }, true);
            }
            for (int i = 0; i < 120 && _mix.Tracks.First(t => t.Id == trackId).Clips.Count != 2; i++) await Layout();
            await Layout();
            Check(_mix.Tracks.First(t => t.Id == trackId).Clips.Count == 2, $"add to existing track; dragging={GetViewport().GuiIsDragging()} status={_status.Text} counts={string.Join(",", _mix.Tracks.Select(t => t.Clips.Count))}");
            Check(!_newTrackLane.Visible, "new-track row hides after drag");
            var moving = _mix.Tracks.First(t => t.Id == trackId).Clips.Last();
            Check(Math.Abs(moving.Start - 3) < .02, "native sound drop follows horizontal pointer position");
            before = JsonSerializer.Serialize(_mix); point = View().PitchGrabPosition;
            View()._GuiInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = true });
            View()._GuiInput(new InputEventMouseMotion { Position = point + new Vector2(0, 200) });
            Check(_mix.Tracks[0].Clips.Count == 1, "lengthening the edited clip displaces an overlapping clip");
            CancelClipGesture(); Check(JsonSerializer.Serialize(_mix) == before, "cancel also restores displaced clips");
            _expandedTracks.Add(trackId); RebuildTracks(); await Layout();
            float clipGain = View().ClipData.Gain;
            var trackGain = _trackControls.FindChildren("TrackGain", "SpinBox", true, false).OfType<SpinBox>().First();
            trackGain.Value = -6;
            Check(Math.Abs(_mix.Tracks[0].Gain - ClipVolume.FromDecibels(-6)) < 1e-6 && View().ClipData.Gain == clipGain, "track volume leaves clip volume independent");
            var mute = _trackControls.FindChildren("TrackMute", "Button", true, false).OfType<Button>().First();
            var speaker = mute.Icon; mute.ButtonPressed = true;
            Check(_mix.Tracks[0].Muted && mute.Icon != speaker && !View().Audible, "track mute updates playback state and clip appearance"); mute.ButtonPressed = false;
            StopMix(true); await ToggleMixPlayback(); int updates = 0, playbackVersion = _playVersion;
            void Updated() => updates++;
            _liveUpdateTimer.Timeout += Updated;
            try
            {
                point = View().PitchGrabPosition;
                View()._GuiInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = true });
                for (int i = 1; i <= 18; i++)
                {
                    View()._GuiInput(new InputEventMouseMotion { Position = point + new Vector2(0, i) });
                    await ToSignal(GetTree().CreateTimer(.016), SceneTreeTimer.SignalName.Timeout);
                }
                Check(_playing && _soloClipId == null && _playVersion == playbackVersion && updates >= 2, "continuous gestures update live playback without restarting");
                View()._GuiInput(new InputEventMouseButton { Position = point + new Vector2(0, 18), ButtonIndex = MouseButton.Left }); await Layout();
                Check(_soloClipId == clipId && _playVersion > playbackVersion, "release auditions the edited clip");
            }
            finally { _liveUpdateTimer.Timeout -= Updated; StopMix(true); }
            Variant payload = new Godot.Collections.Dictionary { ["kind"] = "mix-clip", ["clip"] = moving.Id.ToString(), ["track"] = trackId.ToString(), ["offset"] = .1 };
            Check(Math.Abs(TrackLane.DropTime(new(176, 30), 160, payload) - 1) < 1e-8, "grab offset");
            var movingView = _trackLanes[0].ClipViews.First(v => v.ClipData.Id == moving.Id);
            var press = movingView.GlobalPosition + new Vector2((float)((moving.Start + .1) * _pixelsPerSecond), 12);
            GetViewport().PushInput(new InputEventMouseButton { Position = press, GlobalPosition = press, ButtonIndex = MouseButton.Left, Pressed = true }, true);
            GetViewport().PushInput(new InputEventMouseMotion { Position = press + new Vector2(20, 0), GlobalPosition = press + new Vector2(20, 0), Relative = new Vector2(20, 0), ButtonMask = MouseButtonMask.Left }, true);
            await Layout(); Check(GetViewport().GuiIsDragging(), "clip background starts native dragging");
            dropPoint = _trackLanes[1].GlobalPosition + new Vector2(176, 30);
            if (headless)
            {
                var dragData = GetViewport().GuiGetDragData(); GetViewport().GuiCancelDrag();
                _trackLanes[1]._DropData(new(176, 30), dragData);
            }
            else
            {
                Input.WarpMouse(dropPoint); await Layout();
                GetViewport().PushInput(new InputEventMouseMotion { Position = dropPoint, GlobalPosition = dropPoint, ButtonMask = MouseButtonMask.Left }, true);
                GetViewport().PushInput(new InputEventMouseButton { Position = dropPoint, GlobalPosition = dropPoint, ButtonIndex = MouseButton.Left }, true);
            }
            await Layout();
            Check(_mix.Tracks[1].Clips.Single().Id == moving.Id && Math.Abs(_mix.Tracks[1].Clips[0].Start - 1) < .001, $"cross-track move: start={_mix.Tracks[1].Clips[0].Start:0.000000}, expected ID={moving.Id}, actual ID={_mix.Tracks[1].Clips[0].Id}");
            RestoreHistory(false); await Layout(); Check(_mix.Tracks[0].Clips.Count == 2 && _mix.Tracks[1].Clips.Count == 0, "undo cross-track move");
            int count = _mix.Tracks.Count; _newTrackLane._DropData(new(336, 30), payload); await Layout();
            Check(_mix.Tracks.Count == count + 1 && _mix.Tracks[0].Clips.Single().Id == moving.Id, "new track at top");
            RestoreHistory(false); await Layout(); Check(_mix.Tracks.Count == count, "undo track creation");
            ShowClipPopup(trackId, clipId); await Layout(); Check(GetViewportRect().Encloses(_clipPopup!.GetGlobalRect()), "popup within viewport");
            HandleClipInput(new InputEventKey { Pressed = true, Keycode = Key.Escape }); Check(!_clipPopup.Visible, "Escape dismissal");
            _mix.Tracks[0].Gain = .5f; _mix.Tracks[0].Muted = true; await PlayClip(trackId, clipId);
            Check(_soloClipId == clipId && PlaybackMix().Clips.Count() == 1 && PlaybackMix().Tracks[0].Gain == .5f && !PlaybackMix().Tracks[0].Muted, "clip audition gain and mute bypass");
            StopMix(true); await PlayTrack(_mix.Tracks[0]); Check(_soloClipId == null && PlaybackMix().Clips.Count() == 2, "track SOLO includes all clips");
            StopMix(true); HideClipPopup();
            GD.Print("SOUNDSHELF_MIX_CONTROLS_OK multi-clip popup pitch volume easing cancel undo browser-payload cross-track new-track clip-audition track-solo");
        }
        finally { CancelClipGesture(); HideClipPopup(); StopMix(true); _mix = original; _history.Clear(); _expandedTracks.Clear(); _mainSplit.SplitOffsets = offsets; SetTimelineZoom(zoom); RebuildTracks(); SaveMix(); await Layout(); }
    }
}
