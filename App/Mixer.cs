using Godot;
using SoundShelf.Core;
using System.Collections.Concurrent;

namespace SoundShelf.App;

public partial class Main
{
    private MixProject _mix = new();
    private readonly MixHistory _history = new();
    private readonly Dictionary<long, MixProject> _mixCache = new();
    private Task _pendingSave = Task.CompletedTask;
    private readonly ConcurrentQueue<string> _saveErrors = new();
    private PanelContainer _mixPanel = null!;
    private Label _mixTitle = null!;
    private VBoxContainer _trackControls = null!, _lanes = null!;
    private ScrollContainer _timelineScroll = null!, _tracksScroll = null!;
    private HSlider _zoomSlider = null!;
    private HScrollBar _timelineScrollbar = null!;
    private double? _pendingZoomScroll;
    private int _zoomVersion;
    private TimelineRuler _ruler = null!;
    private Button _play = null!;
    private Label _mixTime = null!, _meter = null!;
    private Godot.Timer _saveTimer = null!, _liveUpdateTimer = null!;
    private AudioStreamPlayer _mixPlayer = null!;
    private AudioStreamGeneratorPlayback? _mixPlayback;
    private LiveMixer? _live;
    private Guid? _soloTrackId, _soloClipId;
    private Dictionary<string, PcmSource> _prepared = new();
    private readonly Dictionary<string, float[]> _peaks = new();
    private readonly List<TrackLane> _trackLanes = new();
    private readonly HashSet<Guid> _expandedTracks = new();
    private const int CompactTrackHeight = 96, ExpandedTrackHeight = 200;
    private readonly List<Action> _syncTrackControls = new();
    private double _pixelsPerSecond = 80, _transport, _playStart, _playWallTime;
    private long _lastPushedFrame;
    private int _mixCapacity;
    private bool _playing;
    private int _playVersion;
    private double _outputLatency;
    private double _peakHold;
    private bool _buildingTracks;

    private void BuildMixer()
    {
        _mixPanel = new PanelContainer { CustomMinimumSize = new(600, 245) }; _mainSplit.AddChild(_mixPanel);
        var body = new VBoxContainer(); _mixPanel.AddChild(body);
        var projectRow = new HBoxContainer(); body.AddChild(projectRow);
        _mixTitle = Skin.Label("", 14); _mixTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _mixTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; projectRow.AddChild(_mixTitle);
        projectRow.AddChild(Skin.Button("New", () => NewWorkspaceMix(SelectedWorkspaceFolder())));
        projectRow.AddChild(Skin.Button("Rename", () => RenameWorkspaceItem(new(_mix.Id, false), _mix.Name)));
        projectRow.AddChild(Skin.Button("Duplicate", () => DuplicateWorkspaceItem(new(_mix.Id, false), _mix.Name)));
        projectRow.AddChild(Skin.Button("Delete", () => DeleteWorkspaceItem(new(_mix.Id, false), _mix.Name)));
        var transport = new HBoxContainer(); body.AddChild(transport);
        _play = Skin.Button("▶  Play", () => _ = Guard(ToggleMixPlayback)); transport.AddChild(_play);
        transport.AddChild(Skin.Button("■", () => StopMix(true), "Stop and return to start"));
        transport.AddChild(Skin.Button("Undo", () => RestoreHistory(false))); transport.AddChild(Skin.Button("Redo", () => RestoreHistory(true)));
        transport.AddChild(Skin.Label("Zoom", 12, Skin.Muted));
        _zoomSlider = new HSlider { MinValue = 10, MaxValue = 3000, Value = 80, Step = 0, CustomMinimumSize = new(90, 0), TooltipText = "Scroll tracks with the mouse wheel · Hold Ctrl to zoom" }; transport.AddChild(_zoomSlider); _zoomSlider.ValueChanged += value => SetTimelineZoom(value);
        transport.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        transport.AddChild(Skin.Label("Master", 12, Skin.Muted)); var master = new HSlider { MinValue = 0, MaxValue = 1, Step = .01, Value = .75, CustomMinimumSize = new(90, 0), Name = "Master" }; transport.AddChild(master);
        master.DragStarted += () => _history.Capture(_mix); master.ValueChanged += value => { if (_buildingTracks) return; _mix.MasterGain = (float)value; MixEdited(); }; master.DragEnded += _ => SaveMix();
        _meter = Skin.Label("PEAK —", 11, Skin.Muted); _meter.CustomMinimumSize = new(82, 0); transport.AddChild(_meter);
        transport.AddChild(Skin.Button("Export…", ChooseExport));
        _tracksScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; body.AddChild(_tracksScroll);
        var tracks = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _tracksScroll.AddChild(tracks);
        _trackControls = new VBoxContainer { CustomMinimumSize = new(290, 0) }; _trackControls.AddThemeConstantOverride("separation", 6); tracks.AddChild(_trackControls);
        _timelineScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever, Name = "TimelineScroll" }; tracks.AddChild(_timelineScroll);
        _lanes = new VBoxContainer(); _lanes.AddThemeConstantOverride("separation", 6); _timelineScroll.AddChild(_lanes);
        // Keep horizontal scrolling outside the vertically scrolling track content.
        var scrollRow = new HBoxContainer(); body.AddChild(scrollRow);
        var scrollSpacer = new Control(); scrollRow.AddChild(scrollSpacer);
        _timelineScrollbar = new HScrollBar { Name = "TimelineScrollbar", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 18), Step = 1, TooltipText = "Scroll timeline horizontally" }; scrollRow.AddChild(_timelineScrollbar);
        var nativeScroll = _timelineScroll.GetHScrollBar();
        void SyncHorizontalScroll()
        {
            _timelineScrollbar.MinValue = nativeScroll.MinValue; _timelineScrollbar.MaxValue = nativeScroll.MaxValue; _timelineScrollbar.Page = nativeScroll.Page;
            _timelineScrollbar.SetValueNoSignal(nativeScroll.Value);
            scrollSpacer.CustomMinimumSize = new(_trackControls.Size.X + tracks.GetThemeConstant("separation"), 0);
            scrollRow.Visible = nativeScroll.MaxValue - nativeScroll.MinValue > nativeScroll.Page + 1;
        }
        nativeScroll.Changed += SyncHorizontalScroll;
        nativeScroll.ValueChanged += value => _timelineScrollbar.SetValueNoSignal(value);
        _timelineScrollbar.ValueChanged += value => _timelineScroll.ScrollHorizontal = (int)Math.Round(value);
        _trackControls.Resized += SyncHorizontalScroll; SyncHorizontalScroll();
        _mixTime = Skin.Label("00:00.000 / 00:00.000 · Add sounds with the file context menu", 12, Skin.Muted); body.AddChild(_mixTime);
        _mixPlayer = new AudioStreamPlayer(); AddChild(_mixPlayer); _outputLatency = AudioServer.GetOutputLatency();
        _saveTimer = new Godot.Timer { WaitTime = .35, OneShot = true }; AddChild(_saveTimer); _saveTimer.Timeout += SaveMix;
        _liveUpdateTimer = new Godot.Timer { WaitTime = .05, OneShot = true }; AddChild(_liveUpdateTimer); _liveUpdateTimer.Timeout += UpdateLiveMix;
    }

    private bool HandleMixerWheel(InputEventMouseButton mouse)
    {
        if (_clipPopup?.Visible == true && _clipPopup.GetGlobalRect().HasPoint(mouse.Position)) return false;
        if (!_mixPanel.IsVisibleInTree() || !_mixPanel.GetGlobalRect().HasPoint(mouse.Position))
        {
            return false;
        }
        if (mouse.ButtonIndex != MouseButton.WheelUp && mouse.ButtonIndex != MouseButton.WheelDown)
        {
            return false;
        }
        double num = ((mouse.ButtonIndex == MouseButton.WheelUp) ? 1 : (-1));
        double num2 = ((mouse.Factor > 0f) ? mouse.Factor : 1f);
        if (!mouse.CtrlPressed)
        {
            _tracksScroll.ScrollVertical -= (int)Math.Round(num * num2 * 48.0);
        }
        else
        {
            Rect2 globalRect = _timelineScroll.GetGlobalRect();
            double value = (globalRect.HasPoint(mouse.Position) ? (mouse.Position.X - globalRect.Position.X) : (globalRect.Size.X / 2f));
            SetTimelineZoom(_pixelsPerSecond * Math.Pow(1.15, num * num2), value);
        }
        return true;
    }

    private void SetTimelineZoom(double value, double? anchorX = null)
    {
        value = Math.Clamp(value, _zoomSlider.MinValue, _zoomSlider.MaxValue);
        if (Math.Abs(value - _pixelsPerSecond) < 1E-06)
        {
            return;
        }
        double num = Math.Clamp(anchorX ?? ((double)(_timelineScroll.Size.X / 2f)), 0.0, _timelineScroll.Size.X);
        double num2 = ((_pendingZoomScroll ?? ((double)_timelineScroll.ScrollHorizontal)) + num) / _pixelsPerSecond;
        _pixelsPerSecond = value;
        _zoomSlider.SetValueNoSignal(value);
        UpdateTimelineDimensions();
        double scroll = Math.Max(0.0, num2 * value - num);
        _pendingZoomScroll = scroll;
        int version = ++_zoomVersion;
        Callable.From(delegate
        {
            if (version == _zoomVersion)
            {
                _timelineScroll.ScrollHorizontal = (int)Math.Round(scroll);
                _pendingZoomScroll = null;
            }
        }).CallDeferred();
    }

    private void LoadLastMix()
    {
        MixProject? mix = null;
        if (long.TryParse(_catalog.GetSetting("last_mix"), out long id)) mix = _catalog.LoadMix(id);
        mix ??= _catalog.Mixes().FirstOrDefault() is var first && first.Id != 0 ? _catalog.LoadMix(first.Id) : null;
        SetMix(mix ?? new MixProject()); _mixPanel.Visible = _mix.Tracks.Count > 0; SaveMix(); RefreshMixTree();
    }

    private void SetMix(MixProject mix)
    {
        CancelClipGesture(); HideClipPopup();
        StopMix(true);
        _mix = mix; _mixGeneration++;
        _mixTitle.Text = mix.Name;
        _history.Clear();
        _expandedTracks.Clear();
        if (mix.Id > 0) { long id = mix.Id; EnqueuePersistence(() => _catalog.SetSetting("last_mix", id.ToString())); }
        RebuildTracks();
        _ = Guard(PrepareWaveforms);
    }

    private void EnqueuePersistence(Action write)
    {
        _pendingSave = _pendingSave.ContinueWith(delegate
        {
            try
            {
                write();
            }
            catch (Exception ex)
            {
                _saveErrors.Enqueue(ex.Message);
            }
        }, TaskScheduler.Default);
    }

    private void SaveMix()
    {
        if (_catalog == null || _shuttingDown || _gestureBefore != null)
        {
            return;
        }
        if (_mix.Id == 0)
        {
            _catalog.SaveMix(_mix, makeActive: false);
            long id = _mix.Id;
            EnqueuePersistence(delegate
            {
                _catalog.SetSetting("last_mix", id.ToString());
            });
        }
        SaveMixSnapshot(_mix);
    }

    private void SaveMixSnapshot(MixProject mix)
    {
        MixProject snapshot = mix.Clone();
        _mixCache[snapshot.Id] = snapshot;
        EnqueuePersistence(delegate
        {
            _catalog.SaveMix(snapshot, makeActive: false);
        });
    }

    private void MixEdited()
    {
        foreach (var sync in _syncTrackControls) sync();
        SyncClipPopup();
        if (_gestureBefore == null) _saveTimer.Start();
        if (_liveUpdateTimer.IsStopped()) _liveUpdateTimer.Start();
        UpdateTimelineDimensions();
        RefreshAudibleLanes();
    }

    private void UpdateLiveMix()
    {
        if (_soloTrackId is Guid id && !_mix.Tracks.Any(t => t.Id == id && (_soloClipId == null || t.Clips.Any(c => c.Id == _soloClipId)))) { StopMix(false); return; }
        if (_live != null)
        {
            _live.Update(PlaybackMix(), _prepared);
        }
    }

    private MixProject PlaybackMix() => _soloTrackId is Guid id ? _soloClipId is Guid clipId ? _mix.ForClipPreview(id, clipId) : _mix.ForTrackPreview(id) : _mix.Clone();
    private void RefreshAudibleLanes()
    {
        foreach (var lane in _trackLanes)
        {
            lane.Audible = _soloTrackId is Guid id ? lane.Track.Id == id : !lane.Track.Muted;
            lane.SyncClips();
        }
    }
    private async Task PlayTrack(MixTrack track)
    {
        StopMix(false); _soloTrackId = track.Id; _transport = track.Start;
        int requestVersion = _playVersion + 1;
        try { await StartMixPlayback(); }
        catch { if (_playVersion == requestVersion) StopMix(false); throw; }
    }

    private void RestoreHistory(bool redo)
    {
        CancelClipGesture();
        MixProject? mixProject = (redo ? _history.Redo(_mix) : _history.Undo(_mix));
        if (mixProject != null)
        {
            _mixGeneration++;
            mixProject.Name = _mix.Name;
            _mix = mixProject;
            RebuildTracks();
            MixEdited();
            SaveMix();
        }
    }

    private Task AddToMix(Sound sound) => AddSoundClip(sound.Id, sound.FullPath, null, 0);

    private async Task AddSoundClip(long soundId, string path, Guid? trackId, double start)
    {
        if (_audio == null) throw new InvalidOperationException("Audio tools are unavailable.");
        var destinationMix = _mix; int generation = _mixGeneration;
        Status("Preparing " + Path.GetFileName(path) + "…");
        var source = await _audio.LoadAsync(path);
        var peaks = _peaks.GetValueOrDefault(path) ?? await Task.Run(() => source.Peaks());
        if (_mix != destinationMix || generation != _mixGeneration || _shuttingDown) return;
        if (trackId.HasValue && !_mix.Tracks.Any(t => t.Id == trackId)) return;
        _history.Capture(_mix);
        var track = trackId.HasValue ? _mix.Tracks.First(t => t.Id == trackId) : new MixTrack { Name = "Track" };
        if (!trackId.HasValue) _mix.Tracks.Insert(0, track);
        var clip = new MixClip { SoundId = soundId, SourcePath = path, Name = Path.GetFileName(path), Start = start,
            SourceDuration = source.Duration, TrimEnd = source.Duration, Volume = new VolumeEasing() };
        MixEditing.Place(_mix, track.Id, clip);
        _prepared[path] = source; _peaks[path] = peaks;
        _mixPanel.Show(); RebuildTracks(); MixEdited(); SaveMix();
        Status("Added to mix · " + clip.Name);
    }

    private async Task PrepareWaveforms()
    {
        if (_audio == null)
        {
            return;
        }
        MixProject mix = _mix;
        foreach (string path in mix.Clips.Select(t => t.SourcePath).Distinct())
        {
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                var source = await _audio.LoadAsync(path);
                float[]? valueOrDefault = _peaks.GetValueOrDefault(path);
                float[]? array = valueOrDefault;
                if (array == null)
                {
                    array = await Task.Run(() => source.Peaks());
                }
                float[] peaks = array;
                if (_mix != mix || _shuttingDown)
                {
                    return;
                }
                _prepared[path] = source;
                _peaks[path] = peaks;
                foreach (TrackLane lane in _trackLanes.Where((TrackLane l) => l.Track.Clips.Any(c => c.SourcePath == path)))
                {
                    lane.SyncClips();
                }
            }
            catch (Exception ex)
            {
                Exception e = ex;
                Status("Unable to load " + Path.GetFileName(path) + " · " + e.Message);
            }
        }
        UpdateLiveMix();
    }

    private SpinBox Number(double value, double min, double max, double step, string suffix, Action<double> changed, Action? audition = null)
    {
        SpinBox spinBox = new SpinBox
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            Suffix = suffix,
            CustomMinimumSize = new Vector2(110f, 0f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        spinBox.ValueChanged += delegate(double v)
        {
            if (!_buildingTracks)
            {
                _history.Capture(_mix);
                changed(v);
                MixEdited();
                audition?.Invoke();
            }
        };
        return spinBox;
    }

    private static Button TrackHeaderToggle(string name, bool pressed) => new()
    {
        Name = name, ToggleMode = true, ButtonPressed = pressed, IconAlignment = HorizontalAlignment.Center,
        ExpandIcon = true, CustomMinimumSize = new(36, 32), MouseDefaultCursorShape = CursorShape.PointingHand
    };

    private void RebuildTracks()
    {
        bool popupVisible = _clipPopup?.Visible == true;
        int verticalScroll = _tracksScroll.ScrollVertical, horizontalScroll = _timelineScroll.ScrollHorizontal;
        _buildingTracks = true; Skin.Clear(_trackControls); Skin.Clear(_lanes); _trackLanes.Clear(); _syncTrackControls.Clear();
        _trackControls.AddChild(new Control { CustomMinimumSize = new(0, 30) });
        _ruler = new TimelineRuler { CustomMinimumSize = new(1200, 30), PixelsPerSecond = _pixelsPerSecond };
        _lanes.AddChild(_ruler); _ruler.Seek += seconds => _ = Guard(() => SeekMix(seconds));
        _newTrackSpacer = new Control { CustomMinimumSize = new(0, 52), Visible = _mixDragging };
        _trackControls.AddChild(_newTrackSpacer);
        _newTrackLane = CreateTrackLane(new MixTrack(), true); _newTrackLane.CustomMinimumSize = new(1200, 52);
        _newTrackLane.Visible = _mixDragging; _lanes.AddChild(_newTrackLane);
        var speaker = GD.Load<Texture2D>("res://Assets/Icons/track-speaker.svg");
        var mutedSpeaker = GD.Load<Texture2D>("res://Assets/Icons/track-muted.svg");
        for (int i = 0; i < _mix.Tracks.Count; i++)
        {
            var track = _mix.Tracks[i]; bool missing = track.Clips.Any(c => !File.Exists(c.SourcePath));
            var panel = new PanelContainer(); _trackControls.AddChild(panel);
            var channel = new VBoxContainer(); panel.AddChild(channel);
            var row = new HBoxContainer(); channel.AddChild(row);
            var disclosure = TrackHeaderToggle("TrackDisclosure", _expandedTracks.Contains(track.Id)); row.AddChild(disclosure);
            var label = Skin.Label($"{i + 1:00}  {track.Name}" + (missing ? "  [missing]" : ""), 12, missing ? new Color("edaa88") : Skin.Text);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill; label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; row.AddChild(label);
            var mute = TrackHeaderToggle("TrackMute", track.Muted); row.AddChild(mute);
            void SyncMute() { mute.Icon = track.Muted ? mutedSpeaker : speaker; mute.TooltipText = mute.AccessibilityName = track.Muted ? "Unmute track" : "Mute track"; }
            mute.Toggled += value => { if (_buildingTracks) return; _history.Capture(_mix); track.Muted = value; SyncMute(); MixEdited(); };
            SyncMute();
            var body = new VBoxContainer(); channel.AddChild(body);
            body.AddChild(Skin.Label("Track volume", 11, Skin.Muted));
            var gain = Number(ClipVolume.Decibels(track.Gain), -60, 12, .5, "dB", value => track.Gain = ClipVolume.FromDecibels(value));
            gain.Name = "TrackGain"; gain.TooltipText = "Overall volume for all clips on this track"; body.AddChild(gain);
            _syncTrackControls.Add(() => gain.SetValueNoSignal(ClipVolume.Decibels(track.Gain)));
            var actions = new HBoxContainer(); body.AddChild(actions);
            var solo = Skin.Button("SOLO", () => _ = Guard(() => PlayTrack(track)), "Play every clip on this track in isolation"); solo.Name = "TrackSolo"; actions.AddChild(solo);
            actions.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            actions.AddChild(Skin.Button("×", () => { CancelClipGesture(); _history.Capture(_mix); _mix.Tracks.Remove(track); RebuildTracks(); MixEdited(); SaveMix(); }, "Remove track"));
            var lane = CreateTrackLane(track); _lanes.AddChild(lane); _trackLanes.Add(lane);
            void SetExpanded(bool expanded)
            {
                body.Visible = expanded; panel.CustomMinimumSize = new(290, expanded ? ExpandedTrackHeight : CompactTrackHeight);
                disclosure.Icon = GetThemeIcon(expanded ? "arrow" : "arrow_collapsed", "Tree");
                disclosure.TooltipText = disclosure.AccessibilityName = expanded ? "Collapse track controls" : "Expand track controls";
                lane.Expanded = expanded; lane.SyncClips();
            }
            panel.Resized += () => lane.CustomMinimumSize = new(lane.CustomMinimumSize.X, panel.Size.Y);
            disclosure.Toggled += expanded => { if (expanded) _expandedTracks.Add(track.Id); else _expandedTracks.Remove(track.Id); SetExpanded(expanded); };
            SetExpanded(disclosure.ButtonPressed);
        }
        if (_mix.Tracks.Count == 0)
        {
            _trackControls.AddChild(Skin.Label("No tracks yet", 14, Skin.Muted));
            var empty = Skin.Label("Drag a sound into the mix to create a track.\nDrop more sounds onto a track to arrange them together.", 15, Skin.Muted);
            empty.CustomMinimumSize = new(900, CompactTrackHeight); empty.MouseFilter = MouseFilterEnum.Ignore; _lanes.AddChild(empty);
        }
        foreach (var node in _mixPanel.FindChildren("Master", "HSlider", true, false)) if (node is HSlider master) master.SetValueNoSignal(_mix.MasterGain);
        _buildingTracks = false; UpdateTimelineDimensions();
        if (popupVisible && SelectedClip() != null) ShowClipPopup(_selectedTrackId!.Value, _selectedClipId!.Value);
        else RefreshClipSelection();
        Callable.From(() => { _tracksScroll.ScrollVertical = verticalScroll; _timelineScroll.ScrollHorizontal = horizontalScroll; }).CallDeferred();
    }
    private void UpdateTimelineDimensions()
    {
        double num = Math.Max(15.0, _mix.TimelineDuration + 3.0);
        float x = (float)Math.Min(8000000.0, num * _pixelsPerSecond);
        if (_ruler != null)
        {
            _ruler.CustomMinimumSize = new Vector2(x, 30f);
            _ruler.PixelsPerSecond = _pixelsPerSecond;
            _ruler.QueueRedraw();
        }
        foreach (TrackLane trackLane in _trackLanes.Concat(_newTrackLane == null ? Array.Empty<TrackLane>() : new[] { _newTrackLane }))
        {
            trackLane.CustomMinimumSize = new Vector2(x, trackLane.CustomMinimumSize.Y);
            trackLane.PixelsPerSecond = _pixelsPerSecond;
            trackLane.SyncClips();
        }
        if (_mixTime != null)
        {
            _mixTime.Text = $"{FormatTime(_transport)} / {FormatTime(_mix.TimelineDuration)}  ·  {_mix.Tracks.Count} tracks  ·  48 kHz stereo";
        }
    }

    private static string FormatTime(double seconds)
    {
        TimeSpan timeSpan = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
        return $"{(int)timeSpan.TotalMinutes:00}:{timeSpan.Seconds:00}.{timeSpan.Milliseconds:000}";
    }

    private async Task ToggleMixPlayback()
    {
        if (_playing)
        {
            StopMix(reset: false);
            return;
        }
        StopMix(false);
        await StartMixPlayback();
    }

    private async Task StartMixPlayback()
    {
        if (_audio == null)
        {
            throw new InvalidOperationException("Audio tools are unavailable.");
        }
        MixProject snapshot = PlaybackMix();
        if (!snapshot.AudibleClips.Any(t => File.Exists(t.SourcePath)))
        {
            StopMix(false); Status("Add an available, unmuted track to play the mix.");
            return;
        }
        StopPreview();
        int version = ++_playVersion;
        _play.Text = "Preparing…";
        Dictionary<string, PcmSource> prepared = await _audio.PrepareAsync(snapshot, default(CancellationToken), includeMuted: true);
        if (version == _playVersion && !_shuttingDown)
        {
            foreach (var (path, source) in prepared) _prepared[path] = source;
            if (_soloTrackId is Guid soloId && !_mix.Tracks.Any(t => t.Id == soloId && (_soloClipId == null || t.Clips.Any(c => c.Id == _soloClipId)))) { StopMix(false); return; }
            var playback = PlaybackMix();
            if (_transport >= playback.TimelineDuration)
            {
                _transport = 0.0;
            }
            _live = new LiveMixer(playback, _prepared, _transport);
            _playStart = _transport;
            _lastPushedFrame = (long)(_transport * 48000.0);
            _mixPlayer.Stream = new AudioStreamGenerator
            {
                MixRate = 48000f,
                BufferLength = 0.08f
            };
            _mixPlayer.Play();
            _mixPlayback = (AudioStreamGeneratorPlayback)_mixPlayer.GetStreamPlayback();
            _mixCapacity = _mixPlayback.GetFramesAvailable();
            _playWallTime = (double)Time.GetTicksUsec() / 1000000.0;
            _playing = true;
            RefreshAudibleLanes();
            _play.Text = "Ⅱ  Pause";
            FillMix();
            Status(_soloClipId is Guid clipId ? "Playing clip · " + _mix.Clips.First(c => c.Id == clipId).Name : _soloTrackId is Guid trackId ? "Playing track · " + _mix.Tracks.First(t => t.Id == trackId).Name : playback.AudibleClips.Any(t => !File.Exists(t.SourcePath)) ? "Playing mix · unavailable sources skipped" : "Playing mix · controls update live");
        }
    }

    private void StopMix(bool reset)
    {
        bool playing = _playing;
        _playVersion++;
        _live?.Dispose();
        _live = null;
        _mixPlayer?.Stop();
        _mixPlayback = null;
        _playing = false;
        _soloTrackId = null; _soloClipId = null; RefreshAudibleLanes();
        if (reset)
        {
            _transport = 0.0;
        }
        if (_play != null)
        {
            _play.Text = "▶  Play";
        }
        UpdatePlayhead();
        if (playing)
        {
            Status(reset ? "Mix stopped" : "Mix paused");
        }
    }

    private async Task SeekMix(double seconds)
    {
        bool resume = _playing;
        StopMix(reset: false);
        _transport = Math.Clamp(seconds, 0.0, _mix.TimelineDuration);
        UpdatePlayhead();
        if (resume)
        {
            await ToggleMixPlayback();
        }
    }

    private void FillMix()
    {
        if (!_playing || _mixPlayback == null || _live == null)
        {
            return;
        }
        LiveMixer.Block? block;
        while (_mixPlayback.GetFramesAvailable() >= 512 && _live.TryRead(out block) && block != null)
        {
            int num = block.Samples.Length / 2;
            Vector2[] array = new Vector2[num];
            float num2 = 0f;
            for (int i = 0; i < num; i++)
            {
                array[i] = new Vector2(block.Samples[i * 2], block.Samples[i * 2 + 1]);
                num2 = Math.Max(num2, Math.Max(Math.Abs(array[i].X), Math.Abs(array[i].Y)));
            }
            _mixPlayback.PushBuffer(array);
            _lastPushedFrame = block.Frame + num;
            _peakHold = Math.Max(_peakHold * 0.97, num2);
            _meter.Text = (block.Clipped ? "CLIPPING" : $"{20.0 * Math.Log10(Math.Max(1E-05, _peakHold)):0.0} dB");
            _meter.Modulate = (block.Clipped ? new Color("ff987f") : Skin.Text);
        }
        double value = (double)(_lastPushedFrame - (_mixCapacity - _mixPlayback.GetFramesAvailable())) / 48000.0 - _outputLatency;
        _transport = Math.Clamp(value, _playStart, Math.Max(_playStart, (double)_lastPushedFrame / 48000.0));
        UpdatePlayhead();
        if (_live.Finished && _mixPlayback.GetFramesAvailable() >= _mixCapacity)
        {
            double end = PlaybackMix().TimelineDuration;
            bool trackPreview = _soloTrackId.HasValue;
            StopMix(reset: false);
            _transport = end;
            UpdatePlayhead();
            Status(trackPreview ? "Track playback complete" : "Mix playback complete");
        }
    }

    private void UpdatePlayhead()
    {
        if (_ruler != null)
        {
            _ruler.Playhead = _transport;
            _ruler.QueueRedraw();
        }
        foreach (TrackLane trackLane in _trackLanes.Concat(_newTrackLane == null ? Array.Empty<TrackLane>() : new[] { _newTrackLane }))
        {
            trackLane.Playhead = _transport;
            trackLane.SyncClips();
        }
        if (_mixTime != null)
        {
            _mixTime.Text = $"{FormatTime(_transport)} / {FormatTime(_mix.TimelineDuration)}  ·  {_mix.Tracks.Count} tracks  ·  48 kHz stereo";
        }
    }

    private bool CanChooseExport(MixProject mix)
    {
        if (_operationBusy)
        {
            Error("Wait for the current operation to finish.");
            return false;
        }
        if (!mix.AudibleClips.Any(t => t.Duration > 0))
        {
            Error("Add an unmuted sound before exporting.");
            return false;
        }
        return true;
    }

    private void ChooseExport()
    {
        if (!CanChooseExport(_mix)) return;
        var (window, vBoxContainer) = Dialog("Export mix", new Vector2I(520, 330));
        vBoxContainer.AddChild(Skin.Label("48 kHz · stereo · complete mix", 16), forceReadableName: false, InternalMode.Disabled);
        OptionButton format = new OptionButton
        {
            Name = "ExportFormat"
        };
        format.AddItem("WAV · 32-bit float (lossless)");
        format.AddItem("OGG · Vorbis quality 6");
        vBoxContainer.AddChild(format, forceReadableName: false, InternalMode.Disabled);
        vBoxContainer.AddChild(Skin.Label("Mute, trim, pitch and volume envelopes are included.\nOriginal sound files are preserved.", 12, Skin.Muted), forceReadableName: false, InternalMode.Disabled);
        Button button = Skin.Button("Export to disk…", delegate
        {
            bool ogg = format.Selected == 1;
            window.Hide();
            window.QueueFree();
            ChooseExportDestination(ogg);
        });
        button.Name = "ExportToDiskChoice";
        vBoxContainer.AddChild(button, forceReadableName: false, InternalMode.Disabled);
        Button button2 = Skin.Button("Export to library…", delegate
        {
            bool ogg = format.Selected == 1;
            window.Hide();
            window.QueueFree();
            ChooseLibraryExport(ogg);
        });
        button2.Name = "ExportToLibraryChoice";
        button2.Disabled = _catalog.Libraries().Count == 0;
        button2.TooltipText = (button2.Disabled ? "Add a sound library first" : "Choose a library and a folder inside it");
        vBoxContainer.AddChild(button2, forceReadableName: false, InternalMode.Disabled);
        vBoxContainer.AddChild(Skin.Button("Cancel", window.QueueFree), forceReadableName: false, InternalMode.Disabled);
    }

    private static string SuggestedExportName(string mixName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new string(mixName.Select(c => c is '/' or '\\' || char.IsControl(c) || invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return name is "" or "." or ".." ? "Untitled mix" : name;
    }

    private void ChooseExportDestination(bool? ogg, MixProject? requestedMix = null)
    {
        var mix = requestedMix ?? _mix;
        if (!CanChooseExport(mix)) return;
        var snapshot = mix.Clone();
        string suggested = SuggestedExportName(snapshot.Name);
        var dialog = CreateSaveDialog("Export mix · 48 kHz stereo",
            ogg.HasValue ? ExportDestination.FileName(suggested, ogg.Value) : suggested, LastExportDirectory);
        const string formatOption = "Audio format";
        if (ogg is bool format)
            dialog.AddFilter(format ? "*.ogg" : "*.wav", format ? "OGG · Vorbis quality 6" : "WAV · 32-bit float");
        else
            dialog.AddOption(formatOption, new[] { "WAV · 32-bit float", "OGG · Vorbis quality 6" }, 0);

        ShowSaveDialog(dialog, path =>
        {
            bool exportOgg = ogg ?? (dialog.GetSelectedOptions().TryGetValue(formatOption, out var selected) && (int)selected == 1);
            string fullPath = Path.GetFullPath(path);
            string target = Path.Combine(Path.GetDirectoryName(fullPath)!, ExportDestination.FileName(Path.GetFileName(fullPath), exportOgg));
            SaveToChosenPath(path, target, overwrite => _ = Guard(() => ExportMix(target, exportOgg, snapshot: snapshot, overwrite: overwrite)));
        });
    }

    private async Task ExportMix(string path, bool ogg, Library? library = null, MixProject? snapshot = null, bool overwrite = true)
    {
        if (_audio == null)
        {
            throw new InvalidOperationException("Audio tools are unavailable.");
        }
        if (_operationBusy)
        {
            throw new InvalidOperationException("An operation is already running.");
        }
        var cancellation = BeginOperation();
        try
        {
            if (snapshot == null || snapshot.Id == _mix.Id)
            {
                StopMix(reset: false);
                SaveMix();
            }
            await _audio.ExportAsync(snapshot ?? _mix.Clone(), path, ogg, new Progress<double>(delegate(double p)
            {
                Status($"Exporting · {p:P0}");
            }), cancellation, overwrite);
            _cancelOperation.Hide();
            if (library == null) RememberSaveDirectory(LastExportDirectory, path);
            else
            {
                Sound exported;
                try
                {
                    exported = await Task.Run(() => _catalog.RegisterExport(library.Id, path));
                }
                catch (Exception ex)
                {
                    Exception e = ex;
                    Status("Exported to disk; catalog update failed");
                    Message("Export saved", path + "\n\nThe audio was saved, but could not be added to the library catalog. Rescan the library to add it.\n\n" + e.Message);
                    return;
                }
                _peaks.Remove(exported.FullPath);
                SelectLibrary(library);
                string folder = (exported.RelativePath.Contains('/') ? exported.RelativePath.Substring(0, exported.RelativePath.LastIndexOf('/')) : "");
                if (folder.Length > 0)
                {
                    Navigate(folder);
                }
                await RefreshBrowser();
            }
            Status("Exported " + path);
            Message("Export complete", ((library != null) ? ("Saved and added to ‘" + library.Name + "’.\n\n") : "") + path);
        }
        finally
        {
            EndOperation();
        }
    }

}
