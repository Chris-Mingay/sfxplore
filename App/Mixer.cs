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
    private OptionButton _mixPicker = null!;
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
    private Guid? _soloTrackId;
    private Dictionary<string, PcmSource> _prepared = new();
    private readonly Dictionary<string, float[]> _peaks = new();
    private readonly List<TrackLane> _trackLanes = new();
    private bool _loadingMixes;
    private const int TrackHeight = 244;
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
        var projectRow = new HBoxContainer(); body.AddChild(projectRow); projectRow.AddChild(Skin.Label("MIX WORKSPACE", 12, Skin.Muted));
        _mixPicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill }; projectRow.AddChild(_mixPicker);
        _mixPicker.ItemSelected += index => { if (_loadingMixes) return; SaveMix(); long id = (long)_mixPicker.GetItemMetadata((int)index); var mix = _mixCache.GetValueOrDefault(id)?.Clone() ?? _catalog.LoadMix(id); if (mix != null) SetMix(mix); };
        projectRow.AddChild(Skin.Button("New", () => NameDialog("New mix", "Untitled mix", name => { SaveMix(); SetMix(new MixProject { Name = name }); SaveMix(); RefreshMixPicker(); })));
        projectRow.AddChild(Skin.Button("Rename", () => NameDialog("Rename mix", _mix.Name, name => { _mix.Name = name; SaveMix(); RefreshMixPicker(); })));
        projectRow.AddChild(Skin.Button("Duplicate", () => NameDialog("Duplicate mix", _mix.Name + " copy", name => { var copy = _mix.Clone(); copy.Id = 0; copy.Name = name; SetMix(copy); SaveMix(); RefreshMixPicker(); })));
        projectRow.AddChild(Skin.Button("Delete", () => Confirm("Delete mix", "Delete this saved mix? Original sound files are retained.", () =>
        { StopMix(true); _catalog.DeleteMix(_mix.Id); var first = _catalog.Mixes().FirstOrDefault(); SetMix(first.Id == 0 ? new MixProject() : _catalog.LoadMix(first.Id)!); SaveMix(); RefreshMixPicker(); }, "Delete")));
        var transport = new HBoxContainer(); body.AddChild(transport);
        _play = Skin.Button("▶  Play", () => _ = Guard(ToggleMixPlayback)); transport.AddChild(_play);
        transport.AddChild(Skin.Button("■", () => StopMix(true), "Stop and return to start"));
        transport.AddChild(Skin.Button("Undo", () => RestoreHistory(false))); transport.AddChild(Skin.Button("Redo", () => RestoreHistory(true)));
        transport.AddChild(Skin.Label("Zoom", 12, Skin.Muted));
        _zoomSlider = new HSlider { MinValue = 10, MaxValue = 3000, Value = 80, Step = 0, CustomMinimumSize = new(90, 0), TooltipText = "Mouse wheel over the mixer: zoom · Shift + wheel: scroll tracks" }; transport.AddChild(_zoomSlider); _zoomSlider.ValueChanged += value => SetTimelineZoom(value);
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
        if (mouse.ShiftPressed)
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
        SetMix(mix ?? new MixProject()); _mixPanel.Visible = _mix.Tracks.Count > 0; SaveMix(); RefreshMixPicker();
    }

    private void SetMix(MixProject mix)
    { StopMix(true); _mix = mix; _history.Clear(); if (mix.Id > 0) { long id = mix.Id; EnqueuePersistence(() => _catalog.SetSetting("last_mix", id.ToString())); } RebuildTracks(); RefreshMixPicker(); _ = Guard(PrepareWaveforms); }

    private void RefreshMixPicker()
    {
        _loadingMixes = true;
        _mixPicker.Clear();
        int idx = 0;
        foreach (var item in _catalog.Mixes())
        {
            _mixPicker.AddItem(_mixCache.GetValueOrDefault(item.Id)?.Name ?? item.Name);
            int num = _mixPicker.ItemCount - 1;
            _mixPicker.SetItemMetadata(num, item.Id);
            if (item.Id == _mix.Id)
            {
                idx = num;
            }
        }
        _mixPicker.Select(idx);
        _loadingMixes = false;
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
        if (_catalog == null || _shuttingDown)
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
        MixProject snapshot = _mix.Clone();
        _mixCache[snapshot.Id] = snapshot;
        EnqueuePersistence(delegate
        {
            _catalog.SaveMix(snapshot, makeActive: false);
        });
    }

    private void MixEdited()
    {
        foreach (var sync in _syncTrackControls) sync();
        _saveTimer.Start();
        if (_liveUpdateTimer.IsStopped()) _liveUpdateTimer.Start();
        UpdateTimelineDimensions();
        RefreshAudibleLanes();
    }

    private void UpdateLiveMix()
    {
        if (_soloTrackId is Guid id && !_mix.Tracks.Any(t => t.Id == id)) { StopMix(false); return; }
        if (_live != null)
        {
            _live.Update(PlaybackMix(), _prepared);
        }
    }

    private MixProject PlaybackMix() => _soloTrackId is Guid id ? _mix.ForTrackPreview(id) : _mix.Clone();
    private void RefreshAudibleLanes()
    {
        foreach (var lane in _trackLanes)
        {
            lane.Audible = _soloTrackId is Guid id ? lane.Track.Id == id : !lane.Track.Muted;
            lane.QueueRedraw();
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
        MixProject? mixProject = (redo ? _history.Redo(_mix) : _history.Undo(_mix));
        if (mixProject != null)
        {
            _mix = mixProject;
            RebuildTracks();
            MixEdited();
            SaveMix();
        }
    }

    private async Task AddToMix(Sound sound)
    {
        if (_audio == null)
        {
            throw new InvalidOperationException("Audio tools are unavailable.");
        }
        Status("Preparing " + Path.GetFileName(sound.FullPath) + "…");
        PcmSource source = await _audio.LoadAsync(sound.FullPath);
        _history.Capture(_mix);
        _mix.Tracks.Add(new MixTrack
        {
            SoundId = sound.Id,
            SourcePath = sound.FullPath,
            Name = Path.GetFileName(sound.FullPath),
            SourceDuration = source.Duration,
            TrimEnd = source.Duration,
            Volume = new VolumeEasing()
        });
        _prepared[sound.FullPath] = source;
        if (!_peaks.ContainsKey(sound.FullPath))
        {
            Dictionary<string, float[]> peaks = _peaks;
            string fullPath = sound.FullPath;
            peaks[fullPath] = await Task.Run(() => source.Peaks());
        }
        _mixPanel.Show();
        RebuildTracks();
        MixEdited();
        SaveMix();
        Status("Added to mix · " + Path.GetFileName(sound.FullPath));
    }

    private async Task PrepareWaveforms()
    {
        if (_audio == null)
        {
            return;
        }
        MixProject mix = _mix;
        foreach (string path in mix.Tracks.Select((MixTrack t) => t.SourcePath).Distinct())
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
                foreach (TrackLane lane in _trackLanes.Where((TrackLane l) => l.Track.SourcePath == path))
                {
                    lane.Peaks = peaks;
                    lane.QueueRedraw();
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

    private void RebuildTracks()
    {
        int verticalScroll = _tracksScroll.ScrollVertical, horizontalScroll = _timelineScroll.ScrollHorizontal;
        _buildingTracks = true; Skin.Clear(_trackControls); Skin.Clear(_lanes); _trackLanes.Clear(); _syncTrackControls.Clear();
        var header = Skin.Label("TRACK / GAIN / PITCH / START", 11, Skin.Muted); header.CustomMinimumSize = new(290, 30); _trackControls.AddChild(header);
        _ruler = new TimelineRuler { CustomMinimumSize = new(1200, 30), PixelsPerSecond = _pixelsPerSecond }; _lanes.AddChild(_ruler); _ruler.Seek += seconds => _ = Guard(() => SeekMix(seconds));
        for (int i = 0; i < _mix.Tracks.Count; i++)
        {
            var track = _mix.Tracks[i]; bool missing = !File.Exists(track.SourcePath);
            var panel = new PanelContainer { CustomMinimumSize = new(290, TrackHeight) }; _trackControls.AddChild(panel); var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 3); panel.AddChild(body);
            var nameRow = new HBoxContainer(); body.AddChild(nameRow);
            var name = Skin.Label($"{i + 1:00}  {track.Name}" + (missing ? "  [missing]" : ""), 12, missing ? new Color("edaa88") : Skin.Text); name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; name.SizeFlagsHorizontal = SizeFlags.ExpandFill; name.TooltipText = track.SourcePath; nameRow.AddChild(name);
            var solo = Skin.Button("SOLO", () => _ = Guard(() => PlayTrack(track)), "Play this track from its beginning in isolation"); solo.Name = "TrackSolo"; nameRow.AddChild(solo);
            nameRow.AddChild(Skin.Button("×", () => { _history.Capture(_mix); _mix.Tracks.Remove(track); RebuildTracks(); MixEdited(); SaveMix(); }, "Remove track"));
            var gainRow = new HBoxContainer(); body.AddChild(gainRow);
            var mute = new CheckBox { Text = "Mute", ButtonPressed = track.Muted }; gainRow.AddChild(mute); mute.Toggled += value => { if (_buildingTracks) return; _history.Capture(_mix); track.Muted = value; MixEdited(); };
            var gain = Number(TrackVolume.Decibels(track.Gain), -60, 12, .5, "dB", value => track.Gain = TrackVolume.FromDecibels(value), () => _ = Guard(() => PlayTrack(track)));
            gain.Name = "TrackGain"; gain.TooltipText = "Flat / middle volume · −60 selects exact silence"; gainRow.AddChild(gain);
            gainRow.AddChild(Skin.Button("−∞", () => { if (track.Gain == 0) return; _history.Capture(_mix); track.Gain = 0; MixEdited(); _ = Guard(() => PlayTrack(track)); }, "Set volume to exact silence"));
            var pitchRow = new HBoxContainer(); body.AddChild(pitchRow);
            var pitch = Number(track.Semitones, -24, 24, .1, "st", value => track.Semitones = value, () => _ = Guard(() => PlayTrack(track))); pitch.Name = "TrackPitch";
            var start = Number(track.Start, 0, 86400, .01, "s", value => track.Start = value);
            pitchRow.AddChild(pitch); pitchRow.AddChild(start);
            var modeRow = new HBoxContainer(); body.AddChild(modeRow); modeRow.AddChild(Skin.Label("Volume easing", 11, Skin.Muted));
            var mode = new OptionButton { Name = "VolumeMode", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            foreach (string text in new[] { "None", "Ease In", "Ease Out", "Both" }) mode.AddItem(text);
            bool legacy = track.Volume == null && track.Envelope.Count > 0;
            mode.Select(legacy ? -1 : (int)(track.Volume?.Mode ?? VolumeMode.None));
            if (legacy) { mode.Text = "Choose easing…"; mode.TooltipText = "Saved envelope is preserved. Choosing a mode replaces it; Undo restores it."; }
            modeRow.AddChild(mode);
            mode.ItemSelected += index =>
            {
                if (_buildingTracks || track.Volume?.Mode == (VolumeMode)index) return;
                _history.Capture(_mix); track.Volume ??= new VolumeEasing(); track.Volume.Mode = (VolumeMode)index;
                RebuildTracks(); MixEdited(); SaveMix();
                _ = Guard(() => PlayTrack(track));
            };
            void AddMethod(string title, string nodeName, EasingMethod selected, Action<EasingMethod> change)
            {
                var row = new HBoxContainer(); body.AddChild(row); row.AddChild(Skin.Label(title, 11, Skin.Muted));
                var method = new OptionButton { Name = nodeName, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                foreach (var item in Enum.GetValues<EasingMethod>()) method.AddItem(item.ToString());
                method.Select((int)selected); row.AddChild(method);
                method.ItemSelected += index => { if (_buildingTracks || (int)selected == index) return; _history.Capture(_mix); selected = (EasingMethod)index; change(selected); MixEdited(); _ = Guard(() => PlayTrack(track)); };
            }
            if (track.Volume?.HasIn == true) AddMethod("Ease In Method", "EaseInMethod", track.Volume.InMethod, value => track.Volume!.InMethod = value);
            if (track.Volume?.HasOut == true) AddMethod("Ease Out Method", "EaseOutMethod", track.Volume.OutMethod, value => track.Volume!.OutMethod = value);
            if (legacy) body.AddChild(Skin.Label("Saved envelope · choose easing to replace", 10, Skin.Muted));
            var lane = new TrackLane { Track = track, Audible = _soloTrackId is Guid soloId ? track.Id == soloId : !track.Muted, Peaks = _peaks.GetValueOrDefault(track.SourcePath) ?? Array.Empty<float>(), CustomMinimumSize = new(1200, TrackHeight), PixelsPerSecond = _pixelsPerSecond };
            _lanes.AddChild(lane); _trackLanes.Add(lane);
            MixProject? beforeGesture = null;
            lane.EditStarted += () => beforeGesture = _mix.Clone();
            lane.Edited += MixEdited;
            lane.AuditionRequested += () => _ = Guard(() => PlayTrack(track));
            lane.EditFinished += changed => { if (changed && beforeGesture != null) _history.Capture(beforeGesture); beforeGesture = null; SaveMix(); UpdateLiveMix(); };
            void SyncNumbers()
            {
                gain.SetValueNoSignal(TrackVolume.Decibels(track.Gain));
                if (track.Gain <= 0 && !gain.GetLineEdit().HasFocus()) gain.GetLineEdit().Text = "−∞ dB";
                pitch.SetValueNoSignal(track.Semitones); start.SetValueNoSignal(track.Start);
            }
            _syncTrackControls.Add(SyncNumbers); SyncNumbers();
        }
        if (_mix.Tracks.Count == 0)
        {
            _trackControls.AddChild(Skin.Label("No tracks yet", 14, Skin.Muted));
            var empty = Skin.Label("Right-click a sound → Add to mix\n\nEach sound gets its own track. Drag clips to arrange them.", 16, Skin.Muted); empty.CustomMinimumSize = new(900, TrackHeight); _lanes.AddChild(empty);
        }
        foreach (var node in _mixPanel.FindChildren("Master", "HSlider", true, false)) if (node is HSlider master) master.Value = _mix.MasterGain;
        _buildingTracks = false; UpdateTimelineDimensions();
        Callable.From(() => { _tracksScroll.ScrollVertical = verticalScroll; _timelineScroll.ScrollHorizontal = horizontalScroll; }).CallDeferred();
    }
    private void UpdateTimelineDimensions()
    {
        double num = Math.Max(15.0, _mix.Tracks.Select((MixTrack t) => t.Start + t.Duration).DefaultIfEmpty(0.0).Max() + 3.0);
        float x = (float)Math.Min(8000000.0, num * _pixelsPerSecond);
        if (_ruler != null)
        {
            _ruler.CustomMinimumSize = new Vector2(x, 30f);
            _ruler.PixelsPerSecond = _pixelsPerSecond;
            _ruler.QueueRedraw();
        }
        foreach (TrackLane trackLane in _trackLanes)
        {
            trackLane.CustomMinimumSize = new Vector2(x, TrackHeight);
            trackLane.PixelsPerSecond = _pixelsPerSecond;
            trackLane.QueueRedraw();
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
        if (!snapshot.AudibleTracks.Any(t => File.Exists(t.SourcePath)))
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
            _prepared = prepared;
            if (_soloTrackId is Guid soloId && !_mix.Tracks.Any(t => t.Id == soloId)) { StopMix(false); return; }
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
            Status(_soloTrackId is Guid trackId ? "Playing track · " + _mix.Tracks.First(t => t.Id == trackId).Name : playback.AudibleTracks.Any(t => !File.Exists(t.SourcePath)) ? "Playing mix · unavailable sources skipped" : "Playing mix · controls update live");
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
        _soloTrackId = null; RefreshAudibleLanes();
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
        foreach (TrackLane trackLane in _trackLanes)
        {
            trackLane.Playhead = _transport;
            trackLane.QueueRedraw();
        }
        if (_mixTime != null)
        {
            _mixTime.Text = $"{FormatTime(_transport)} / {FormatTime(_mix.TimelineDuration)}  ·  {_mix.Tracks.Count} tracks  ·  48 kHz stereo";
        }
    }

    private void ChooseExport()
    {
        if (_operationBusy)
        {
            Error("Wait for the current operation to finish.");
            return;
        }
        if (!_mix.AudibleTracks.Any(t => t.Duration > 0))
        {
            Error("Add an unmuted sound before exporting.");
            return;
        }
        var (window, vBoxContainer) = Dialog("Export mix", new Vector2I(520, 330));
        vBoxContainer.AddChild(Skin.Label("48 kHz · stereo · complete mix", 16), forceReadableName: false, InternalMode.Disabled);
        OptionButton format = new OptionButton
        {
            Name = "ExportFormat"
        };
        format.AddItem("WAV · 24-bit PCM (lossless)");
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

    private void ChooseExportDestination(bool ogg)
    {
        string extension = (ogg ? ".ogg" : ".wav");
        FileDialog dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Export mix · 48 kHz stereo",
            UseNativeDialog = true,
            CurrentFile = _mix.Name + extension
        };
        dialog.AddFilter("*" + extension, ogg ? "OGG · Vorbis quality 6" : "WAV · 24-bit PCM");
        AddChild(dialog, forceReadableName: false, InternalMode.Disabled);
        dialog.FileSelected += delegate(string path)
        {
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                path += extension;
            }
            string target = path;
            dialog.QueueFree();
            _ = Guard(() => ExportMix(target, ogg));
        };
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCenteredRatio(0.7f);
    }

    private async Task ExportMix(string path, bool ogg, Library? library = null)
    {
        if (_audio == null)
        {
            throw new InvalidOperationException("Audio tools are unavailable.");
        }
        if (_operationBusy)
        {
            throw new InvalidOperationException("An operation is already running.");
        }
        _operationBusy = true;
        _operation = new CancellationTokenSource();
        StopMix(reset: false);
        SaveMix();
        try
        {
            await _audio.ExportAsync(_mix.Clone(), path, ogg, new Progress<double>(delegate(double p)
            {
                Status($"Exporting · {p:P0}");
            }), _operation.Token);
            if (library != null)
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
            _operationBusy = false;
            _operation.Dispose();
            _operation = null;
        }
    }

}
