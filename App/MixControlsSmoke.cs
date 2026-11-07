using Godot;
using SoundShelf.Core;

namespace SoundShelf.App;

public partial class Main
{
    private async Task CaptureMixControls(string directory)
    {
        Directory.CreateDirectory(directory);
        var original = _mix.Clone(); var offsets = _mainSplit.SplitOffsets; double zoom = _pixelsPerSecond;
        var expandedTracks = _expandedTracks.ToArray();
        try
        {
            _mainSplit.SplitOffsets = new[] { 200 }; _mixPanel.Show();
            var source = _mix.Tracks.OrderByDescending(t => t.SourceDuration).First();
            var first = source.Clone(); first.Start = .1; first.TrimStart = 0; first.TrimEnd = source.SourceDuration; first.Gain = 1; first.Semitones = 0;
            first.Volume = new() { Mode = VolumeMode.Both, InMethod = EasingMethod.easeInOutCirc, OutMethod = EasingMethod.easeInOutCirc };
            var second = first.Clone(); second.Id = Guid.NewGuid(); second.TrimEnd = .08; second.Volume = new() { Mode = VolumeMode.Both }; second.Semitones = -24;
            var third = first.Clone(); third.Id = Guid.NewGuid(); third.Volume = new() { Mode = VolumeMode.None }; third.Gain = TrackVolume.FromDecibels(-24); // Pink and yellow coincide.
            _mix.Tracks = new() { first, second, third }; RebuildTracks();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, "mix-controls-compact.png"));
            var disclosures = _trackControls.FindChildren("TrackDisclosure", "Button", true, false).OfType<Button>().ToArray();
            disclosures[0].ButtonPressed = disclosures[1].ButtonPressed = true;
            foreach (double scale in new[] { 80.0, 10.0, 300.0, 3000.0 })
            {
                SetTimelineZoom(scale);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                _tracksScroll.ScrollVertical = 0; _timelineScroll.ScrollHorizontal = 0;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var panels = _trackControls.GetChildren().OfType<PanelContainer>().ToArray();
                for (int i = 0; i < panels.Length; i++)
                    if (Math.Abs(panels[i].GlobalPosition.Y - _trackLanes[i].GlobalPosition.Y) > 1 || Math.Abs(panels[i].Size.Y - _trackLanes[i].Size.Y) > 1)
                        throw new InvalidOperationException($"Misaligned track {i}: controls {panels[i].GlobalPosition.Y}/{panels[i].Size.Y}, lane {_trackLanes[i].GlobalPosition.Y}/{_trackLanes[i].Size.Y}");
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, $"mix-controls-{scale:0}.png"));
            }
            _tracksScroll.ScrollVertical = (int)(_trackLanes[0].Size.Y + _trackLanes[1].Size.Y);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(directory, "mix-controls-overlap.png"));
        }
        finally
        {
            _expandedTracks.Clear();
            _expandedTracks.UnionWith(expandedTracks);
            _mix = original; _mainSplit.SplitOffsets = offsets; SetTimelineZoom(zoom); RebuildTracks();
        }
    }

    private async Task ExerciseMixControls()
    {
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("Mix controls: " + message); }
        async Task Layout() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        void DragAt(Vector2 from, Vector2 to)
        {
            var lane = _trackLanes[0];
            lane._GuiInput(new InputEventMouseButton { Position = from, ButtonIndex = MouseButton.Left, Pressed = true });
            lane._GuiInput(new InputEventMouseMotion { Position = to });
            lane._GuiInput(new InputEventMouseButton { Position = to, ButtonIndex = MouseButton.Left });
        }
        OptionButton Mode() => _trackControls.FindChildren("VolumeMode", "OptionButton", true, false).OfType<OptionButton>().First();
        async Task SelectMode(VolumeMode mode)
        {
            Mode().EmitSignal(OptionButton.SignalName.ItemSelected, (long)mode); await Layout();
        }
        var original = _mix.Clone();
        try
        {
            await Layout();
            var disclosures = _trackControls.FindChildren("TrackDisclosure", "Button", true, false).OfType<Button>().ToArray();
            var mutes = _trackControls.FindChildren("TrackMute", "Button", true, false).OfType<Button>().ToArray();
            var soloButtons = _trackControls.FindChildren("TrackSolo", "Button", true, false).OfType<Button>().ToArray();
            Check(disclosures.All(button => !button.ButtonPressed && button.Icon != null) && soloButtons.All(button => !button.IsVisibleInTree()), "tracks start collapsed");
            var mute = mutes[0];
            var originalIcon = mute.Icon;
            Check(mute.IsVisibleInTree() && mute.Text.Length == 0 && originalIcon != null, "mute is an icon in the collapsed header");
            mute.ButtonPressed = !mute.ButtonPressed;
            Check(_mix.Tracks[0].Muted != original.Tracks[0].Muted && mute.Icon != originalIcon, "mute toggles playback state and icon");
            mute.ButtonPressed = !mute.ButtonPressed;
            disclosures[0].ButtonPressed = true;
            await Layout();
            var panels = _trackControls.GetChildren().OfType<PanelContainer>().ToArray();
            for (int i = 0; i < panels.Length; i++)
                Check(Math.Abs(panels[i].GlobalPosition.Y - _trackLanes[i].GlobalPosition.Y) <= 1 && Math.Abs(panels[i].Size.Y - _trackLanes[i].Size.Y) <= 1, "expanded and compact tracks align with timeline lanes");
            Check(soloButtons[0].IsVisibleInTree() && _trackLanes[0].Size.Y > CompactTrackHeight, "chevron reveals controls and grows the lane");
            disclosures[0].ButtonPressed = false;
            await Layout();
            Check(!soloButtons[0].IsVisibleInTree() && _trackLanes[0].Size.Y == CompactTrackHeight, "chevron restores the compact lane");
            foreach (var disclosure in disclosures) disclosure.ButtonPressed = true;
            await Layout();
            Check(soloButtons.Length == _mix.Tracks.Count, "SOLO on every track");
            Check(soloButtons.All(b => !b.ToggleMode), "SOLO is a playback action, not a toggle");
            string savedMix = System.Text.Json.JsonSerializer.Serialize(_mix);
            async Task ClickSolo(int index)
            {
                soloButtons[index].EmitSignal(Button.SignalName.Pressed);
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while ((!_playing || _soloTrackId != _mix.Tracks[index].Id) && DateTime.UtcNow < deadline) await Layout();
                Check(_playing && _soloTrackId == _mix.Tracks[index].Id, "SOLO starts playback immediately");
                Check(PlaybackMix().Tracks.Count == 1 && _playStart == _mix.Tracks[index].Start, "SOLO plays only the chosen track from its beginning");
            }
            await ClickSolo(0); int version = _playVersion;
            await ClickSolo(0); Check(_playVersion > version, "clicking SOLO again restarts playback");
            if (soloButtons.Length > 1) await ClickSolo(1);
            StopMix(false); Check(_soloTrackId == null, "stopping clears temporary isolation");
            Check(System.Text.Json.JsonSerializer.Serialize(_mix) == savedMix, "SOLO does not change saved mix settings");
            _mix.Tracks[0].Muted = true;
            await ClickSolo(0); Check(!PlaybackMix().Tracks[0].Muted && _mix.Tracks[0].Muted, "SOLO auditions muted tracks without changing mute");
            var finishDeadline = DateTime.UtcNow.AddSeconds(4);
            while (_playing && DateTime.UtcNow < finishDeadline) await Layout();
            Check(!_playing && _soloTrackId == null && Math.Abs(_transport - (_mix.Tracks[0].Start + _mix.Tracks[0].Duration)) < .001, "SOLO stops at the selected clip end");
            _mix.Tracks[0].Muted = original.Tracks[0].Muted;
            await ToggleMixPlayback(); Check(_playing && _soloTrackId == null && PlaybackMix().Tracks.Count == _mix.Tracks.Count, "normal Play returns to the full mix");
            StopMix(true);
            _mix = original.Clone(); _history.Clear(); RebuildTracks(); await Layout();
            var lane = _trackLanes[0]; double originalPitch = lane.Track.Semitones, originalDuration = lane.Track.Duration;
            var grab = lane.PitchGrabPosition;
            DragAt(grab, grab + new Vector2(0, -20));
            await Layout(); Check(_playing && _soloTrackId == lane.Track.Id, "pitch mouse-up automatically auditions the edited track");
            Check(lane.Track.Semitones > originalPitch && lane.Track.Duration < originalDuration, "pitch changes duration while dragging");
            var pitch = _trackControls.FindChildren("TrackPitch", "SpinBox", true, false).OfType<SpinBox>().First();
            Check(Math.Abs(pitch.Value - lane.Track.Semitones) < .001, "numeric pitch synchronization");
            DragAt(grab, grab); // A click must not consume an undo step.
            var volumeGrab = lane.VolumeGrabPosition; float initialGain = lane.Track.Gain;
            lane._GuiInput(new InputEventMouseButton { Position = volumeGrab, ButtonIndex = MouseButton.Left, Pressed = true });
            lane._GuiInput(new InputEventMouseMotion { Position = volumeGrab + new Vector2(0, 8) });
            lane._GuiInput(new InputEventMouseMotion { Position = volumeGrab });
            lane._GuiInput(new InputEventMouseButton { Position = volumeGrab, ButtonIndex = MouseButton.Left });
            Check(lane.Track.Gain == initialGain, "returning a volume drag to its origin");
            RestoreHistory(false); await Layout();
            Check(_mix.Tracks[0].Semitones == originalPitch, "one undo per gesture; click has no history");
            foreach (float delta in new[] { -1000f, 1000f })
            {
                lane = _trackLanes[0]; grab = lane.PitchGrabPosition;
                DragAt(grab, grab + new Vector2(0, delta));
                Check(lane.Track.Semitones == (delta < 0 ? 24 : -24), "pitch limits");
            }
            _mix = original.Clone(); RebuildTracks(); await Layout();
            Check(Mode().ItemCount == 4, "exactly four volume modes");
            var legacy = _mix.Tracks[0]; legacy.Volume = null; legacy.Envelope = new() { new(legacy.TrimStart, .25f), new(legacy.TrimEnd, .75f) };
            RebuildTracks(); await Layout();
            Check(Mode().Selected == -1 && legacy.Volume == null, "legacy envelope remains unconverted");
            await SelectMode(VolumeMode.None); Check(TrackVolume.Gain(_mix.Tracks[0], legacy.TrimStart) == legacy.Gain, "explicit legacy replacement");
            RestoreHistory(false); await Layout(); Check(_mix.Tracks[0].Volume == null, "undo restores legacy envelope");
            await SelectMode(VolumeMode.None);
            lane = _trackLanes[0]; grab = lane.VolumeGrabPosition; float gain = lane.Track.Gain;
            DragAt(grab, grab + new Vector2(0, 15)); Check(lane.Track.Gain < gain, "flat volume drag");
            await Layout(); Check(_playing && _soloTrackId == lane.Track.Id, "volume mouse-up automatically auditions the edited track");
            grab = lane.VolumeGrabPosition; DragAt(grab, grab + new Vector2(0, 1000)); Check(lane.Track.Gain == 0, "exact silence");
            grab = lane.VolumeGrabPosition; DragAt(grab, grab + new Vector2(0, -1000)); Check(Math.Abs(lane.Track.Gain - TrackVolume.MaximumGain) < .00001, "maximum volume");
            lane.Track.Gain = TrackVolume.FromDecibels(-24); lane.Track.Semitones = 0; MixEdited();
            grab = lane.PitchGrabPosition + new Vector2(0, -2);
            DragAt(grab, grab + new Vector2(0, -10)); Check(lane.Track.Semitones > 0, "overlapping bars: upper side controls pitch");
            lane.Track.Semitones = 0; MixEdited(); float overlappingGain = lane.Track.Gain;
            grab = lane.VolumeGrabPosition + new Vector2(0, 2);
            DragAt(grab, grab + new Vector2(0, 10)); Check(lane.Track.Gain < overlappingGain && lane.Track.Semitones == 0, "overlapping bars: lower side controls volume");
            lane.Track.Gain = 1; MixEdited();
            foreach (var mode in new[] { VolumeMode.EaseIn, VolumeMode.EaseOut, VolumeMode.Both, VolumeMode.None })
            {
                int replayVersion = _playVersion;
                await SelectMode(mode);
                Check(_playVersion > replayVersion && _soloTrackId == _mix.Tracks[0].Id, "easing mode automatically auditions the edited track");
                Check(_trackControls.FindChildren("EaseInMethod", "OptionButton", true, false).Count == (mode is VolumeMode.EaseIn or VolumeMode.Both ? 1 : 0), "ease in selector visibility");
                Check(_trackControls.FindChildren("EaseOutMethod", "OptionButton", true, false).Count == (mode is VolumeMode.EaseOut or VolumeMode.Both ? 1 : 0), "ease out selector visibility");
            }
            await SelectMode(VolumeMode.Both); lane = _trackLanes[0];
            var points = lane.VolumeHandlePositions;
            DragAt(points[0], points[0] + new Vector2(0, -20)); Check(lane.Track.Volume!.StartGain > 0, "start endpoint");
            points = lane.VolumeHandlePositions; double inEnd = lane.Track.Volume.InEnd;
            DragAt(points[1], points[1] + new Vector2(5, 10));
            Check(lane.Track.Volume.InEnd > inEnd && lane.Track.Gain < 1, "ease in duration and target");
            points = lane.VolumeHandlePositions; double outStart = lane.Track.Volume.OutStart;
            DragAt(points[2], points[2] + new Vector2(-5, -5));
            Check(lane.Track.Volume.OutStart < outStart, "ease out duration");
            Check(Math.Abs(lane.VolumeHandlePositions[1].Y - lane.VolumeHandlePositions[2].Y) < .001, "shared middle volume");
            points = lane.VolumeHandlePositions;
            DragAt(points[3], points[3] + new Vector2(0, -20)); Check(lane.Track.Volume.EndGain > 0, "end endpoint");
            var method = _trackControls.FindChildren("EaseInMethod", "OptionButton", true, false).OfType<OptionButton>().First();
            int methodVersion = _playVersion;
            method.EmitSignal(OptionButton.SignalName.ItemSelected, (long)EasingMethod.easeInOutCirc);
            await Layout(); Check(_playVersion > methodVersion && _soloTrackId == lane.Track.Id, "easing method automatically auditions the edited track");
            Check(lane.Track.Volume.InMethod == EasingMethod.easeInOutCirc, "method selection");
            points = lane.VolumeHandlePositions;
            DragAt(points[1], points[1] + new Vector2(10000, 0));
            Check(lane.Track.Volume.InEnd <= lane.Track.Volume.OutStart, "handles cannot cross");

            // Exercise timer updates throughout continuous input, not only after release.
            var numericPitch = _trackControls.FindChildren("TrackPitch", "SpinBox", true, false).OfType<SpinBox>().First();
            int numericVersion = _playVersion; numericPitch.Value += 1; await Layout();
            Check(_playVersion > numericVersion && _soloTrackId == lane.Track.Id, "numeric pitch automatically auditions the edited track");
            var numericGain = _trackControls.FindChildren("TrackGain", "SpinBox", true, false).OfType<SpinBox>().First();
            numericVersion = _playVersion; numericGain.Value -= 1; await Layout();
            Check(_playVersion > numericVersion && _soloTrackId == lane.Track.Id, "numeric gain automatically auditions the edited track");
            StopMix(true); await ToggleMixPlayback(); int updates = 0;
            void Updated() => updates++;
            _liveUpdateTimer.Timeout += Updated;
            try
            {
                grab = lane.PitchGrabPosition;
                int dragVersion = _playVersion;
                lane._GuiInput(new InputEventMouseButton { Position = grab, ButtonIndex = MouseButton.Left, Pressed = true });
                for (int i = 1; i <= 18; i++)
                {
                    lane._GuiInput(new InputEventMouseMotion { Position = grab + new Vector2(0, i) });
                    await ToSignal(GetTree().CreateTimer(.016), Godot.Timer.SignalName.Timeout);
                }
                Check(_soloTrackId == null && _playVersion == dragVersion, "dragging updates current playback without restarting it");
                lane._GuiInput(new InputEventMouseButton { Position = grab + new Vector2(0, 18), ButtonIndex = MouseButton.Left });
                await Layout(); Check(_soloTrackId == lane.Track.Id && _playVersion > dragVersion, "mouse-up replays in solo after continuous edits");
                Check(updates >= 2, "continuous live updates");
            }
            finally { _liveUpdateTimer.Timeout -= Updated; StopMix(true); }
            GD.Print("SOUNDSHELF_MIX_CONTROLS_OK compact disclosure lane-alignment mute-icon pitch volume four-handles modes curves undo live-updates solo-play replay-on-edit");
        }
        finally { StopMix(true); _mix = original; _history.Clear(); _expandedTracks.Clear(); RebuildTracks(); await Layout(); }
    }
}
