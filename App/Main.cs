using Godot;
using SoundShelf.Core;
using System.Text.Json;

namespace SoundShelf.App;

public partial class Main : Control
{
    private Catalog _catalog = null!;
    private AudioLibrary? _audio;
    private string _data = "";
    private Library? _library;
    private string _folder = "";
    private int _offset;
    private long? _scanFilter;
    private readonly HashSet<long> _tagFilters = new();
    private List<Tag> _filterTags = new();
    private int _queryVersion;
    private int _previewVersion;
    private BrowserTile? _selectedTile;
    private Sound? _contextSound;
    private string? _contextLocation;
    private bool _contextIsFolder;
    private Func<string, bool, Godot.Error> _showInFileManager = OS.ShellShowInFileManager;
    private VBoxContainer _libraryList = null!, _gridArea = null!;
    private GridContainer _grid = null!;
    private Label _status = null!, _location = null!, _pageLabel = null!, _previewLabel = null!;
    private LineEdit _search = null!;
    private MenuButton _tagPicker = null!;
    private OptionButton _scope = null!;
    private Button _previous = null!, _next = null!;
    private Button _browseFolders = null!;
    private Button _backButton = null!, _forwardButton = null!;
    private PopupMenu _fileMenu = null!;
    private AudioStreamPlayer _previewPlayer = null!;
    private AudioStreamGeneratorPlayback? _previewPlayback;
    private PcmSource? _previewSource;
    private long _previewFrame;
    private int _previewCapacity;
    private CancellationTokenSource? _operation;
    private Godot.Timer _searchTimer = null!;
    private HSplitContainer _outerSplit = null!;
    private VSplitContainer _mainSplit = null!;
    private bool _shuttingDown;
    private bool _operationBusy;
    private bool _testMode;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    public override void _Ready()
    {
        Theme = Skin.Create();
        var arguments = OS.GetCmdlineUserArgs();
        _testMode = arguments.Contains("--smoke-test");
        if (_testMode) { GetViewport().GuiEmbedSubwindows = true; GetWindow().Size = new(1440, 900); }
        _data = arguments.FirstOrDefault(a => a.StartsWith("--data-dir="))?[11..] ?? ProjectSettings.GlobalizePath("user://");
        Directory.CreateDirectory(_data);
        _catalog = new Catalog(Path.Combine(_data, "soundshelf.sqlite"));
        string tools = OS.HasFeature("editor") ? ProjectSettings.GlobalizePath($"res://tools/ffmpeg/{AudioLibrary.RuntimeId}") : Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "tools", "ffmpeg", AudioLibrary.RuntimeId);
        try { _audio = new AudioLibrary(Path.Combine(_data, "cache"), tools); } catch (Exception e) { CallDeferred(MethodName.ShowStartupError, e.Message); }
        BuildInterface(); LoadLibraries(); LoadTags(); LoadLastMix(); RestoreLayout();
        GetTree().AutoAcceptQuit = false;
        _ = RefreshBrowser();
        if (arguments.Contains("--smoke-test")) _ = RunSmokeTest(arguments);
    }
    private void ShowStartupError(string message) => Error(message);
    private void BuildInterface()
    {
        var background = new ColorRect { Color = Skin.Background, MouseFilter = MouseFilterEnum.Ignore }; background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(background);
        var margin = new MarginContainer(); margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (string edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 10);
        AddChild(margin); var root = new VBoxContainer(); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        header.AddChild(Skin.Label("◈  SFXplore", 20, Skin.Accent));
        header.AddChild(Skin.Label("  SOUND LIBRARY & MIX WORKSPACE", 11, Skin.Muted));
        header.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        header.AddChild(Skin.Button("Tags", () => ShowTags(null)));
        header.AddChild(Skin.Button("Mix drawer", () => _mixPanel.Visible = !_mixPanel.Visible));
        header.AddChild(Skin.Button("About", () => Message("SFXplore", "SFXplore · Godot 4.7.2 .NET\n\nBrowse, tag, preview and mix your sound collection.\nYour original files are never modified.\n\nSpace: play/pause mix · Ctrl+Z: undo · Ctrl+Shift+Z: redo\nClick the timeline ruler to seek.\n\nBuilt with Godot, SQLite and FFmpeg. See THIRD_PARTY_NOTICES.md for dependencies.")));
        _outerSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SplitOffsets = new int[] { 255 } }; root.AddChild(_outerSplit);
        var sidebar = new PanelContainer { CustomMinimumSize = new(220, 0) }; _outerSplit.AddChild(sidebar);
        var side = new VBoxContainer(); sidebar.AddChild(side);
        side.AddChild(Skin.Label("SOUND LIBRARIES", 12, Skin.Muted));
        side.AddChild(Skin.Button("+  Add library", AddLibrary));
        var librariesScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; side.AddChild(librariesScroll);
        _libraryList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; librariesScroll.AddChild(_libraryList);
        side.AddChild(Skin.Button("Rescan selected", () => { if (_library != null) _ = Guard(() => Scan(_library)); }));
        side.AddChild(Skin.Button("Scan history", ShowScanHistory));
        side.AddChild(Skin.Button("Remove library", RemoveLibrary));
        side.AddChild(Skin.Label("WAV  ·  OGG  ·  MP3\nMetadata stays on this computer", 11, Skin.Muted));
        _mainSplit = new VSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SplitOffsets = new int[] { 480 } }; _outerSplit.AddChild(_mainSplit);
        var browser = new PanelContainer { CustomMinimumSize = new(600, 200) }; _mainSplit.AddChild(browser);
        var browserBody = new VBoxContainer(); browser.AddChild(browserBody);
        var searchRow = new HBoxContainer(); browserBody.AddChild(searchRow);
        _search = new LineEdit { PlaceholderText = "Search sounds by name or path…", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true }; searchRow.AddChild(_search);
        _scope = new OptionButton(); _scope.AddItem("Selected library"); _scope.AddItem("All libraries"); searchRow.AddChild(_scope);
        _tagPicker = new MenuButton { Text = "All tags", CustomMinimumSize = new(140, 0), Flat = false, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis }; searchRow.AddChild(_tagPicker);
        _tagPicker.GetPopup().HideOnCheckableItemSelection = false;
        _tagPicker.GetPopup().IndexPressed += TagFilterPressed;
        _searchTimer = new Godot.Timer { WaitTime = .2, OneShot = true }; AddChild(_searchTimer);
        _search.TextChanged += _ => _searchTimer.Start(); _searchTimer.Timeout += () => { _offset = 0; _ = RefreshBrowser(); };
        _scope.ItemSelected += index => { _offset = 0; _ = RefreshBrowser(); };
        var nav = new HBoxContainer(); browserBody.AddChild(nav);
        _backButton = Skin.IconButton("res://Assets/Icons/arrow-left.svg", GoBack, "Back · mouse back button"); nav.AddChild(_backButton);
        _forwardButton = Skin.IconButton("res://Assets/Icons/arrow-right.svg", GoForward, "Forward · mouse forward button"); nav.AddChild(_forwardButton);
        nav.AddChild(Skin.IconButton("res://Assets/Icons/arrow-up.svg", GoUp, "Parent folder"));
        _location = Skin.Label("All libraries", 13, Skin.Muted); _location.SizeFlagsHorizontal = SizeFlags.ExpandFill; _location.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; nav.AddChild(_location);
        _browseFolders = Skin.Button("Browse folders", () => { if (_library != null) SelectLibrary(_library); }, "Return to this library's root folder and clear result filters"); nav.AddChild(_browseFolders);
        nav.AddChild(Skin.Button("Clear filters", () => { _search.Text = ""; _tagFilters.Clear(); UpdateTagPicker(); _scanFilter = null; _offset = 0; _ = RefreshBrowser(); }));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; browserBody.AddChild(scroll);
        _gridArea = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(_gridArea);
        _grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill }; _grid.AddThemeConstantOverride("h_separation", 10); _grid.AddThemeConstantOverride("v_separation", 10); _gridArea.AddChild(_grid);
        scroll.Resized += () => { _grid.Columns = Math.Max(1, (int)(scroll.Size.X / 190)); };
        var paging = new HBoxContainer(); browserBody.AddChild(paging);
        _previous = Skin.Button("Previous", () => { _offset = Math.Max(0, _offset - 80); _ = RefreshBrowser(); }); paging.AddChild(_previous);
        _pageLabel = Skin.Label(""); _pageLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill; _pageLabel.HorizontalAlignment = HorizontalAlignment.Center; paging.AddChild(_pageLabel);
        _next = Skin.Button("Next", () => { _offset += 80; _ = RefreshBrowser(); }); paging.AddChild(_next);
        var preview = new HBoxContainer(); browserBody.AddChild(preview);
        preview.AddChild(Skin.Button("■", StopPreview, "Stop sound preview"));
        _previewLabel = Skin.Label("Select a sound to preview", 12, Skin.Muted); _previewLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill; _previewLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; preview.AddChild(_previewLabel);
        _previewPlayer = new AudioStreamPlayer(); AddChild(_previewPlayer);
        BuildMixer();
        var status = new HBoxContainer(); root.AddChild(status); _status = Skin.Label("Ready · Add a folder to begin", 12, Skin.Muted); _status.SizeFlagsHorizontal = SizeFlags.ExpandFill; _status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; status.AddChild(_status);
        status.AddChild(Skin.Button("Cancel operation", () => _operation?.Cancel()));
        _fileMenu = new PopupMenu(); AddChild(_fileMenu);
        _fileMenu.IdPressed += id =>
        {
            if (id == 2) { OpenContextLocation(); return; }
            if (_contextSound == null) return;
            if (id == 0) ShowTags(_contextSound);
            else if (id == 1) _ = Guard(() => AddToMix(_contextSound));
        };
    }
    private void LoadLibraries()
    {
        Skin.Clear(_libraryList);
        var all = Skin.Button("▦  All sounds", () => { _library = null; _folder = ""; _back.Clear(); _forward.Clear(); _scanFilter = null; _offset = 0; _scope.Select(1); _ = RefreshBrowser(); }); _libraryList.AddChild(all);
        foreach (var library in _catalog.Libraries())
        {
            var b = Skin.Button("▣  " + library.Name, () => SelectLibrary(library), library.Path); b.Alignment = HorizontalAlignment.Left;
            b.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; b.ToggleMode = true; b.ButtonPressed = _library?.Id == library.Id; _libraryList.AddChild(b);
        }
    }
    private void SelectLibrary(Library library)
    {
        _library = library; _folder = ""; _back.Clear(); _forward.Clear(); _scanFilter = null; _offset = 0; _scope.Select(0);
        _search.Text = ""; _searchTimer.Stop(); _tagFilters.Clear(); UpdateTagPicker();
        EnqueuePersistence(() => { _catalog.SetSetting("library", library.Id.ToString()); _catalog.SetSetting("folder", ""); }); LoadLibraries(); _ = RefreshBrowser();
    }
    private void BrowseNewSounds(ScanResult result)
    {
        var library = _catalog.Libraries().FirstOrDefault(l => l.Id == result.LibraryId);
        if (library == null) return;
        SelectLibrary(library); _scanFilter = result.Id; _ = RefreshBrowser();
    }
    private void LoadTags()
    {
        _filterTags = _catalog.Tags(); var valid = _filterTags.Select(t => t.Id).ToHashSet();
        if (_tagFilters.RemoveWhere(id => !valid.Contains(id)) > 0) _offset = 0;
        var popup = _tagPicker.GetPopup(); popup.Clear(); popup.AddItem("Clear tag filters"); popup.AddSeparator();
        foreach (var tag in _filterTags) { popup.AddCheckItem(tag.Name); popup.SetItemMetadata(popup.ItemCount - 1, tag.Id); }
        UpdateTagPicker();
    }
    private void TagFilterPressed(long index)
    {
        var popup = _tagPicker.GetPopup();
        if (index == 0) _tagFilters.Clear();
        else if (index >= 2 && index < popup.ItemCount) { long id = (long)popup.GetItemMetadata((int)index); if (!_tagFilters.Add(id)) _tagFilters.Remove(id); }
        else return;
        UpdateTagPicker(); _offset = 0; _ = RefreshBrowser();
    }
    private void UpdateTagPicker()
    {
        var popup = _tagPicker.GetPopup();
        if (popup.ItemCount > 0) popup.SetItemDisabled(0, _tagFilters.Count == 0);
        for (int index = 2; index < popup.ItemCount; index++) popup.SetItemChecked(index, _tagFilters.Contains((long)popup.GetItemMetadata(index)));
        var selected = _filterTags.Where(t => _tagFilters.Contains(t.Id)).ToList();
        _tagPicker.Text = selected.Count == 0 ? "All tags" : selected.Count == 1 ? selected[0].Name : $"Tags ({selected.Count})";
        _tagPicker.TooltipText = "Select multiple tags. Sounds must have all selected tags." + (selected.Count > 0 ? "\n\n" + string.Join("\n", selected.Select(t => t.Name)) : "");
    }
    private async Task RefreshBrowser()
    {
        _backButton.Disabled = _library == null || (!ShowingLibraryResults && _back.Count == 0 && _folder.Length == 0);
        _forwardButton.Disabled = _library == null || ShowingLibraryResults || _forward.Count == 0;
        int version = ++_queryVersion; long? libraryId = _scope.Selected == 1 ? null : _library?.Id; string folder = _folder, search = _search.Text.Trim(); var tags = _tagFilters.ToArray(); int offset = _offset; var scan = _scanFilter;
        try
        {
            var result = await Task.Run(() => _catalog.Browse(libraryId, folder, search, tags, offset, scanId: scan));
            if (_shuttingDown || version != _queryVersion) return;
            Skin.Clear(_grid); _selectedTile = null;
            foreach (var entry in result.Entries)
            {
                var tile = new BrowserTile { Entry = entry, SizeFlagsHorizontal = SizeFlags.ExpandFill }; tile.Clicked += TileClicked; _grid.AddChild(tile);
            }
            foreach (var child in _gridArea.GetChildren().Skip(1).ToArray()) { _gridArea.RemoveChild(child); child.QueueFree(); }
            if (result.Entries.Count == 0)
            {
                var empty = Skin.Label(_catalog.Libraries().Count == 0 ? "Your sound collection, organized.\n\nAdd a library folder from the left to scan your WAV, OGG and MP3 files." : "No indexed sounds here.\n\nScan this library, open another folder, or adjust your filters.", 17, Skin.Muted); empty.CustomMinimumSize = new(0, 180); empty.HorizontalAlignment = HorizontalAlignment.Center; _gridArea.AddChild(empty);
            }
            bool flatResults = scan.HasValue || search.Length > 0 || tags.Length > 0 || !libraryId.HasValue;
            _browseFolders.Visible = _library != null && flatResults;
            string mode = scan.HasValue ? "  ·  Newly added sounds (flat results)" : search.Length > 0 || tags.Length > 0 ? "  ·  Filtered sound results" : "";
            _location.Text = (libraryId.HasValue ? _library?.Name + (!flatResults && folder.Length > 0 ? "  /  " + folder.Replace("/", "  /  ") : "") : "All libraries") + mode;
            _previous.Disabled = offset == 0; _next.Disabled = offset + 80 >= result.Total;
            _pageLabel.Text = result.Total == 0 ? "0 items" : $"{offset + 1}–{Math.Min(offset + 80, result.Total)} of {result.Total:N0} items";
        }
        catch (Exception e) { if (version == _queryVersion) Error(e.Message); }
    }
    private void TileClicked(BrowserTile tile, bool right)
    {
        if (_selectedTile != null && IsInstanceValid(_selectedTile)) { _selectedTile.Selected = false; _selectedTile.QueueRedraw(); }
        _selectedTile = tile; tile.Selected = true; tile.QueueRedraw();
        if (right)
        {
            _contextIsFolder = tile.Entry.IsFolder; _contextSound = tile.Entry.Sound;
            _contextLocation = _contextIsFolder && _library != null ? Paths.Join(_library.Path, tile.Entry.RelativePath) : _contextSound?.FullPath;
            if (_contextLocation == null) return;
            _fileMenu.Clear();
            if (!_contextIsFolder) { _fileMenu.AddItem("Manage tags…", 0); _fileMenu.AddItem("Add to mix", 1); _fileMenu.AddSeparator(); }
            _fileMenu.AddItem(_contextIsFolder ? "Open folder location" : "Open file location", 2);
            _fileMenu.Position = (Vector2I)GetScreenPosition() + (Vector2I)GetGlobalMousePosition(); _fileMenu.Popup();
            return;
        }
        if (tile.Entry.IsFolder) Navigate(tile.Entry.RelativePath);
        else _ = Guard(() => Preview(tile.Entry.Sound!));
    }
    private void OpenContextLocation()
    {
        if (_contextLocation == null) return;
        if (_contextIsFolder ? !Directory.Exists(_contextLocation) : !File.Exists(_contextLocation))
        { Error("This " + (_contextIsFolder ? "folder" : "file") + " is no longer available.\n\n" + _contextLocation + "\n\nRescan the library to refresh its contents."); return; }
        var result = _showInFileManager(_contextLocation, _contextIsFolder);
        if (result != Godot.Error.Ok) Error($"Could not open the location in your file explorer ({result}).\n\n{_contextLocation}");
    }
    private void Navigate(string folder)
    {
        if (_library == null || folder == _folder) return;
        _back.Push(_folder); _forward.Clear(); OpenHistoryFolder(folder);
    }
    private void OpenHistoryFolder(string folder)
    { _folder = folder; _offset = 0; _scanFilter = null; EnqueuePersistence(() => _catalog.SetSetting("folder", folder)); _ = RefreshBrowser(); }
    private bool ShowingLibraryResults => _library != null && (_scanFilter.HasValue || _tagFilters.Count > 0 || _search.Text.Trim().Length > 0 || _scope.Selected == 1);
    private void GoBack()
    {
        if (_library == null) return;
        if (ShowingLibraryResults) { SelectLibrary(_library); return; }
        if (_back.Count > 0) { _forward.Push(_folder); OpenHistoryFolder(_back.Pop()); }
        else if (_folder.Length > 0) { _forward.Push(_folder); OpenHistoryFolder(_folder.Contains('/') ? _folder[.._folder.LastIndexOf('/')] : ""); }
    }
    private void GoForward()
    {
        if (_library == null || ShowingLibraryResults || _forward.Count == 0) return;
        _back.Push(_folder); OpenHistoryFolder(_forward.Pop());
    }
    private void GoUp() { if (ShowingLibraryResults) { SelectLibrary(_library!); return; } if (_folder.Length > 0) Navigate(_folder.Contains('/') ? _folder[.._folder.LastIndexOf('/')] : ""); }
    private void AddLibrary()
    {
        var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenDir, Access = FileDialog.AccessEnum.Filesystem, Title = "Choose a sound library folder", UseNativeDialog = true };
        AddChild(dialog); dialog.DirSelected += path =>
        {
            try { var library = _catalog.AddLibrary(path); SelectLibrary(library); Confirm("Scan library?", "Recursively scan this folder and all its subfolders for WAV, OGG and MP3 sounds?\n\n" + library.Path, () => _ = Guard(() => Scan(library)), "Scan now", "Later"); }
            catch (Exception e) { Error(e.Message); } finally { dialog.QueueFree(); }
        }; dialog.Canceled += dialog.QueueFree; dialog.PopupCenteredRatio(.7f);
    }
    private void RemoveLibrary()
    {
        if (_library == null) return; var library = _library;
        Confirm("Remove library", $"Remove ‘{library.Name}’ from the catalog?\n\nOriginal files and saved mix source references are retained.", () => { _catalog.RemoveLibrary(library.Id); _library = null; _folder = ""; _back.Clear(); _forward.Clear(); _scanFilter = null; _offset = 0; LoadLibraries(); _ = RefreshBrowser(); }, "Remove");
    }
    private async Task Scan(Library library)
    {
        if (_operationBusy) throw new InvalidOperationException("Wait for the current scan or export to finish.");
        _operationBusy = true; _operation = new();
        try
        {
            Status("Scanning " + library.Name + "…");
            var progress = new Progress<ScanProgress>(p => Status($"Scanning · {p.Files:N0} sounds · {p.Path}"));
            var result = await _catalog.ScanAsync(library, progress, _operation.Token);
            SelectLibrary(library); ShowScanResult(result);
            Status($"Scan complete · {result.Added:N0} added · {result.Removed:N0} removed · {result.Changed:N0} changed");
        }
        finally { _operationBusy = false; _operation.Dispose(); _operation = null; }
    }
    private async Task Preview(Sound sound)
    {
        if (_audio == null) throw new InvalidOperationException("Audio tools are unavailable. Run setup first.");
        StopPreview(); StopMix(false); int version = _previewVersion; _previewLabel.Text = "Loading " + Path.GetFileName(sound.FullPath) + "…";
        var source = await _audio.LoadAsync(sound.FullPath); if (version != _previewVersion || _shuttingDown) return;
        _previewSource = source; _previewFrame = 0;
        _previewPlayer.Stream = new AudioStreamGenerator { MixRate = 48000, BufferLength = .1f }; _previewPlayer.Play(); _previewPlayback = (AudioStreamGeneratorPlayback)_previewPlayer.GetStreamPlayback(); _previewCapacity = _previewPlayback.GetFramesAvailable();
        _previewLabel.Text = $"▶  {Path.GetFileName(sound.FullPath)}  ·  {source.Duration:0.00}s";
        try { string info = await _audio.ProbeAsync(sound.FullPath); if (version == _previewVersion) _previewLabel.Text += "  ·  " + info; } catch { /* Preview remains available when optional metadata fails. */ }
    }
    private void StopPreview() { _previewVersion++; _previewPlayer?.Stop(); _previewPlayback = null; _previewSource = null; if (_previewLabel != null) _previewLabel.Text = _previewLabel.Text.Replace("▶", "■"); }
    private void FillPreview()
    {
        if (_previewSource == null || _previewPlayback == null) return;
        int frames = (int)Math.Min(_previewPlayback.GetFramesAvailable(), _previewSource.Frames - _previewFrame);
        if (frames > 0)
        {
            var values = new Vector2[frames]; for (int i = 0; i < frames; i++) values[i] = new(_previewSource.Sample(_previewFrame + i, 0), _previewSource.Sample(_previewFrame + i, 1));
            _previewPlayback.PushBuffer(values); _previewFrame += frames;
        }
        if (_previewFrame >= _previewSource.Frames && _previewPlayback.GetFramesAvailable() >= _previewCapacity) { _previewPlayer.Stop(); _previewPlayback = null; _previewSource = null; }
    }
    private void Status(string message) { if (!_shuttingDown && _status != null) _status.Text = message; }
    private async Task Guard(Func<Task> work) { try { await work(); } catch (OperationCanceledException) { Status("Operation cancelled · previous data retained"); } catch (Exception e) { if (!_shuttingDown) Error(e.Message); GD.PrintErr(e); } }
    private void Error(string text) { Status(text.Replace('\n', ' ')); Message("SFXplore", text); }
    private void Message(string title, string text)
    {
        var dialog = new AcceptDialog { Title = title, DialogText = text, DialogAutowrap = true, MinSize = new(460, 160) }; AddChild(dialog); dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered(new(580, 250));
    }
    private void Confirm(string title, string text, Action action, string yes = "Confirm", string no = "Cancel")
    {
        var dialog = new ConfirmationDialog { Title = title, DialogText = text, DialogAutowrap = true, OkButtonText = yes, CancelButtonText = no }; AddChild(dialog);
        dialog.Confirmed += () => { dialog.QueueFree(); try { action(); } catch (Exception e) { Error(e.Message); } }; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered(new(540, 210));
    }
    private void RestoreLayout()
    {
        if (long.TryParse(_catalog.GetSetting("library"), out long id)) _library = _catalog.Libraries().FirstOrDefault(l => l.Id == id);
        _folder = _catalog.GetSetting("folder") ?? ""; LoadLibraries();
        if (int.TryParse(_catalog.GetSetting("sidebar_width"), out int sw)) _outerSplit.SplitOffsets = new int[] { sw };
        if (int.TryParse(_catalog.GetSetting("browser_height"), out int bh)) _mainSplit.SplitOffsets = new int[] { bh };
        if (!_testMode && int.TryParse(_catalog.GetSetting("window_width"), out int w) && int.TryParse(_catalog.GetSetting("window_height"), out int h)) GetWindow().Size = new(Math.Clamp(w, 1180, 3840), Math.Clamp(h, 720, 2160));
        if (_catalog.GetSetting("mix_visible") == "false") _mixPanel.Hide();
    }
    private void SaveLayout()
    {
        if (_testMode) return;
        _catalog.SetSetting("sidebar_width", _outerSplit.SplitOffsets[0].ToString()); _catalog.SetSetting("browser_height", _mainSplit.SplitOffsets[0].ToString());
        _catalog.SetSetting("window_width", GetWindow().Size.X.ToString()); _catalog.SetSetting("window_height", GetWindow().Size.Y.ToString()); _catalog.SetSetting("mix_visible", _mixPanel.Visible ? "true" : "false");
    }
    public override void _Process(double delta) { FillPreview(); FillMix(); if (_saveErrors.TryDequeue(out string? error)) Error("Could not save application data: " + error); }
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true } mouse) return;
        if (HandleMixerWheel(mouse)) { GetViewport().SetInputAsHandled(); return; }
        if (_library == null) return;
        if (mouse.ButtonIndex == MouseButton.Xbutton1) GoBack();
        else if (mouse.ButtonIndex == MouseButton.Xbutton2) GoForward();
        else return;
        GetViewport().SetInputAsHandled();
    }
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return;
        if (key.Keycode == Key.Space) { _ = Guard(ToggleMixPlayback); GetViewport().SetInputAsHandled(); }
        if (key.CtrlPressed && key.Keycode == Key.Z) { RestoreHistory(key.ShiftPressed); GetViewport().SetInputAsHandled(); }
    }
    public override void _Notification(int what)
    {
        if (what != NotificationWMCloseRequest) return;
        if (_operationBusy) { Confirm("Operation in progress", "Cancel the operation and close SFXplore?", () => { _operation?.Cancel(); _ = CloseWhenReady(); }, "Cancel and close"); return; }
        CloseApplication();
    }
    private async Task CloseWhenReady() { while (_operationBusy) await ToSignal(GetTree().CreateTimer(.05), SceneTreeTimer.SignalName.Timeout); CloseApplication(); }
    private void CloseApplication()
    {
        SaveLayout(); SaveMix(); _pendingSave.GetAwaiter().GetResult(); _shuttingDown = true; StopPreview(); StopMix(false); _audio?.Dispose(); GetTree().Quit();
    }
    private async Task SmokeTest(string[] arguments)
    {
        await RefreshBrowser(); await PrepareWaveforms();
        if (arguments.Contains("--exercise-tag-filter")) await ExerciseTagFilter();
        if (arguments.Contains("--exercise-zoom")) await ExerciseMixerZoom();
        if (arguments.Contains("--exercise-folders")) await ExerciseFolderNavigation();
        if (arguments.Contains("--exercise-ui")) await ExerciseInterface();
        if (arguments.Contains("--exercise-library-export")) await ExerciseLibraryExport();
        string? controlsCapture = arguments.FirstOrDefault(a => a.StartsWith("--capture-controls="))?[19..];
        if (controlsCapture != null) await CaptureMixControls(controlsCapture);
        if (arguments.Contains("--preview-library-export")) ChooseLibraryExport(false);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print("SOUNDSHELF_SMOKE_OK libraries=" + _catalog.Libraries().Count);
        string? capture = arguments.FirstOrDefault(a => a.StartsWith("--capture="))?[10..];
        if (capture != null) { await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw); GetViewport().GetTexture().GetImage().SavePng(capture); }
        CloseApplication();
    }
    private async Task RunSmokeTest(string[] arguments)
    {
        try { await SmokeTest(arguments); }
        catch (Exception e) { GD.PrintErr("SOUNDSHELF_SMOKE_FAILED " + e); _shuttingDown = true; StopPreview(); StopMix(false); GetTree().Quit(1); }
    }
    private async Task ExerciseTagFilter()
    {
        var sounds = _catalog.Browse(null, "", "", null, 0).Entries.Select(e => e.Sound!).Take(2).ToList();
        if (sounds.Count < 2) throw new InvalidOperationException("Tag filter smoke test requires two indexed sounds.");
        var library = _catalog.Libraries().First(l => l.Id == sounds[0].LibraryId); SelectLibrary(library);
        string suffix = Guid.NewGuid().ToString(); var a = _catalog.SaveTag("Filter A " + suffix, "#aabbcc"); var b = _catalog.SaveTag("Filter B " + suffix, "#ddeeff");
        try
        {
            _catalog.AssignTag(sounds[0].Id, a.Id, true); _catalog.AssignTag(sounds[0].Id, b.Id, true); _catalog.AssignTag(sounds[1].Id, a.Id, true); LoadTags();
            async Task Toggle(long id)
            {
                var popup = _tagPicker.GetPopup(); int index = Enumerable.Range(2, popup.ItemCount - 2).First(i => (long)popup.GetItemMetadata(i) == id);
                popup.EmitSignal(PopupMenu.SignalName.IndexPressed, (long)index); await RefreshBrowser();
            }
            await Toggle(a.Id); await Toggle(b.Id);
            var entries = _grid.GetChildren().OfType<BrowserTile>().ToList();
            if (_tagFilters.Count != 2 || _tagPicker.Text != "Tags (2)" || entries.Count != 1 || entries[0].Entry.Sound?.Id != sounds[0].Id || _tagPicker.GetPopup().HideOnCheckableItemSelection) throw new InvalidOperationException("Tag filter must support multiple checked tags and require every selected tag.");
            await Toggle(a.Id);
            if (_tagFilters.Count != 1 || !_tagFilters.Contains(b.Id)) throw new InvalidOperationException("Unchecking one tag must preserve the remaining selection.");
            await Toggle(a.Id); _tagPicker.GetPopup().EmitSignal(PopupMenu.SignalName.IndexPressed, 0L); await RefreshBrowser();
            if (_tagFilters.Count != 0 || _tagPicker.Text != "All tags") throw new InvalidOperationException("Clear tag filters must reset every selection.");
            await Toggle(a.Id); await Toggle(b.Id); _catalog.DeleteTag(b.Id); LoadTags(); await RefreshBrowser();
            if (_tagFilters.Count != 1 || !_tagFilters.Contains(a.Id)) throw new InvalidOperationException("Deleting a selected tag must retain other valid filters.");
            _catalog.SaveTag("Renamed filter " + suffix, a.Color, a.Id); LoadTags();
            if (!_tagFilters.Contains(a.Id) || !_tagPicker.Text.StartsWith("Renamed filter")) throw new InvalidOperationException("Renaming a selected tag must preserve and refresh its filter.");
            SelectLibrary(library); await RefreshBrowser();
            if (_tagFilters.Count != 0) throw new InvalidOperationException("Library selection must clear every selected tag filter.");
            GD.Print("SOUNDSHELF_TAG_FILTER_OK multiple-checkboxes match-all uncheck clear delete rename library-reset");
        }
        finally { _catalog.DeleteTag(a.Id); _catalog.DeleteTag(b.Id); LoadTags(); _ = RefreshBrowser(); }
    }
    private async Task ExerciseMixerZoom()
    {
        bool visible = _mixPanel.Visible; _mixPanel.Show();
        async Task Layout() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Wheel(Vector2 position, MouseButton button, bool shift = false)
        {
            using var mouse = new InputEventMouseButton { Position = position, ButtonIndex = button, Pressed = true, Factor = 1, ShiftPressed = shift };
            Input.ParseInputEvent(mouse); await Layout();
        }
        await Layout(); double initial = _pixelsPerSecond;
        _timelineScroll.ScrollHorizontal = 100; await Layout();
        var rect = _timelineScroll.GetGlobalRect(); double anchor = rect.Size.X * .4;
        var point = rect.Position + new Vector2((float)anchor, 50);
        double before = (_timelineScroll.ScrollHorizontal + anchor) / _pixelsPerSecond;
        await Wheel(point, MouseButton.WheelUp);
        double after = (_timelineScroll.ScrollHorizontal + anchor) / _pixelsPerSecond;
        if (_pixelsPerSecond <= initial || Math.Abs(after - before) > 1.5 / _pixelsPerSecond || Math.Abs(_zoomSlider.Value - _pixelsPerSecond) > .001) throw new InvalidOperationException("Wheel up must zoom in around the pointer and synchronize the slider.");
        await Wheel(point, MouseButton.WheelDown);
        if (Math.Abs(_pixelsPerSecond - initial) > .001) throw new InvalidOperationException("Wheel down must zoom out.");
        await Wheel(_trackControls.GetGlobalRect().Position + new Vector2(70, 70), MouseButton.WheelUp);
        if (_pixelsPerSecond <= initial) throw new InvalidOperationException("Wheel over track controls must zoom the mixer too.");
        double zoom = _pixelsPerSecond; await Wheel(point, MouseButton.WheelDown, true);
        if (_pixelsPerSecond != zoom) throw new InvalidOperationException("Shift + wheel must scroll tracks without zooming.");
        await Wheel(_gridArea.GetGlobalRect().Position + new Vector2(20, 20), MouseButton.WheelUp);
        if (_pixelsPerSecond != zoom) throw new InvalidOperationException("Wheel outside the mixer must not change its zoom.");
        SetTimelineZoom(300); await Layout(); await Wheel(point, MouseButton.WheelUp);
        if (_pixelsPerSecond <= 300) throw new InvalidOperationException("Mixer zoom must extend beyond the former 300 px/s limit.");
        _zoomSlider.Value = 3000; await Layout(); await Wheel(point, MouseButton.WheelUp);
        if (_pixelsPerSecond != 3000) throw new InvalidOperationException("Mixer zoom must respect its upper limit.");
        if (!_timelineScrollbar.IsVisibleInTree() || !_mixPanel.GetGlobalRect().Encloses(_timelineScrollbar.GetGlobalRect())) throw new InvalidOperationException("The horizontal scrollbar must be visible inside the mixer when zoomed in.");
        _timelineScrollbar.Value = 200; await Layout();
        if (Math.Abs(_timelineScroll.ScrollHorizontal - 200) > 1) throw new InvalidOperationException("The pinned scrollbar must scroll the timeline.");
        _timelineScroll.ScrollHorizontal = 350; await Layout();
        if (Math.Abs(_timelineScrollbar.Value - 350) > 1) throw new InvalidOperationException("The scrollbar must synchronize with timeline scrolling.");
        float scrollbarY = _timelineScrollbar.GlobalPosition.Y;
        _tracksScroll.ScrollVertical = 500; await Layout();
        if (Math.Abs(_timelineScrollbar.GlobalPosition.Y - scrollbarY) > 1 || !_timelineScrollbar.IsVisibleInTree()) throw new InvalidOperationException("Vertical track scrolling must not move the horizontal scrollbar.");
        _tracksScroll.ScrollVertical = 0;
        SetTimelineZoom(10); await Layout(); await Wheel(point, MouseButton.WheelDown);
        if (_pixelsPerSecond != 10) throw new InvalidOperationException("Mixer zoom must respect its lower limit.");
        SetTimelineZoom(initial); await Layout(); _timelineScroll.ScrollHorizontal = 0; _mixPanel.Visible = visible;
        GD.Print("SOUNDSHELF_ZOOM_OK wheel-in wheel-out pointer-anchor slider-sync track-controls shift-scroll browser-isolation limits pinned-scrollbar");
    }
    private async Task ExerciseFolderNavigation()
    {
        var library = _library ?? _catalog.Libraries().First();
        var scan = _catalog.GetScan(_catalog.ScanHistory(library.Id).First().Id);
        _search.Text = "previous search"; _tagFilters.Add(long.MaxValue); _scanFilter = scan.Id; _scope.Select(1);
        SelectLibrary(library); await RefreshBrowser();
        int folders = _grid.GetChildren().OfType<BrowserTile>().Count(t => t.Entry.IsFolder);
        if (folders == 0 || ShowingLibraryResults) throw new InvalidOperationException("Selecting a library must clear result filters and display its root folders.");
        await Scan(library); await RefreshBrowser();
        if (ShowingLibraryResults || !_grid.GetChildren().OfType<BrowserTile>().Any(t => t.Entry.IsFolder)) throw new InvalidOperationException("Finishing a scan must preserve the library folder view.");
        foreach (var window in GetChildren().OfType<Window>().Where(w => w is not PopupMenu).ToArray()) window.QueueFree();
        var folder = _grid.GetChildren().OfType<BrowserTile>().First(t => t.Entry.IsFolder);
        TileClicked(folder, true);
        if (_fileMenu.ItemCount != 1 || _fileMenu.GetItemText(0) != "Open folder location" || _contextSound != null || _folder.Length != 0) throw new InvalidOperationException("Right-clicking a folder must show its own location action without opening it or retaining a file target.");
        VerifyFileManagerAction(Paths.Join(library.Path, folder.Entry.RelativePath), true); _fileMenu.Hide();
        string path = folder.Entry.RelativePath; TileClicked(folder, false); await RefreshBrowser();
        if (_folder != path || _grid.GetChildCount() == 0) throw new InvalidOperationException("Opening an indexed folder must display its contents.");
        async Task MouseNavigation(MouseButton button, bool pressed = true)
        {
            using var mouse = new InputEventMouseButton { ButtonIndex = button, Pressed = pressed, Position = new(500, 200) };
            Input.ParseInputEvent(mouse); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await RefreshBrowser();
        }
        await MouseNavigation(MouseButton.Xbutton1);
        if (_folder.Length != 0 || _forwardButton.Disabled) throw new InvalidOperationException("Mouse back must return to the previous folder and enable forward navigation.");
        await MouseNavigation(MouseButton.Xbutton1, false);
        if (_folder.Length != 0) throw new InvalidOperationException("Releasing mouse back must not navigate.");
        await MouseNavigation(MouseButton.Xbutton2);
        if (_folder != path || !_forwardButton.Disabled) throw new InvalidOperationException("Mouse forward must restore the folder and disable itself when history is exhausted.");
        await MouseNavigation(MouseButton.Xbutton2);
        if (_folder != path) throw new InvalidOperationException("Mouse forward without history must do nothing.");
        await MouseNavigation(MouseButton.Xbutton1); Navigate(path); await RefreshBrowser();
        if (_forward.Count != 0) throw new InvalidOperationException("Opening a folder after going back must clear forward history.");
        _back.Clear(); _forward.Clear(); // A folder restored on launch has no in-session history.
        await MouseNavigation(MouseButton.Xbutton1);
        if (_folder.Length != 0 || _forward.Count == 0) throw new InvalidOperationException("Mouse back from a restored folder must go to its parent and preserve forward navigation.");
        await MouseNavigation(MouseButton.Xbutton2);
        if (_folder != path) throw new InvalidOperationException("Mouse forward must revisit a folder restored on launch.");
        GoUp(); await RefreshBrowser();
        if (_folder.Length != 0) throw new InvalidOperationException("Up must return to the library root.");
        BrowseNewSounds(scan); await RefreshBrowser();
        if (!_browseFolders.Visible || _grid.GetChildren().OfType<BrowserTile>().Any(t => t.Entry.IsFolder)) throw new InvalidOperationException("New sound results must provide a return-to-folders action.");
        _browseFolders.EmitSignal(Button.SignalName.Pressed); await RefreshBrowser();
        if (_grid.GetChildren().OfType<BrowserTile>().Count(t => t.Entry.IsFolder) != folders || ShowingLibraryResults) throw new InvalidOperationException("Browse folders must restore the root folder grid.");
        if (_back.Count != 0 || _forward.Count != 0) throw new InvalidOperationException("Selecting a library must reset navigation history.");
        GD.Print($"SOUNDSHELF_FOLDERS_OK root_folders={folders} folder-location rescan open mouse-back mouse-forward branch-history up newly-added return-to-folders");
    }
    private void VerifyFileManagerAction(string expectedPath, bool folder)
    {
        var original = _showInFileManager; string? requested = null; bool openFolder = !folder;
        try
        {
            _showInFileManager = (path, open) => { requested = path; openFolder = open; return Godot.Error.Ok; };
            _fileMenu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
            if (requested != expectedPath || openFolder != folder) throw new InvalidOperationException("The location action must request the correct path and file/folder mode from the OS.");
        }
        finally { _showInFileManager = original; }
    }
    private async Task ExerciseInterface()
    {
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("UI check: " + message); }
        Check(_grid.GetChildCount() > 0, "demo grid is empty");
        var file = _grid.GetChildren().OfType<BrowserTile>().First(t => !t.Entry.IsFolder);
        TileClicked(file, true); Check(_contextSound != null, "file context menu target");
        Check(_fileMenu.GetItemIndex(0) >= 0 && _fileMenu.GetItemIndex(1) >= 0 && _fileMenu.GetItemText(_fileMenu.GetItemIndex(2)) == "Open file location", "file context menu actions");
        VerifyFileManagerAction(file.Entry.Sound!.FullPath, false); _fileMenu.Hide();
        await Preview(file.Entry.Sound!); Check(_previewSource != null, "preview decoding"); StopPreview();
        ShowTags(file.Entry.Sound); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var tagsWindow = GetChildren().OfType<Window>().First(w => w is not PopupMenu);
        var tagName = tagsWindow.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().First(t => t.PlaceholderText == "New tag name");
        string smokeTagName = "Smoke tag " + Guid.NewGuid(); tagName.Text = smokeTagName;
        tagsWindow.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text == "Create tag").EmitSignal(Button.SignalName.Pressed);
        var createdTag = _catalog.Tags(file.Entry.Sound!.Id).FirstOrDefault(t => t.Name == smokeTagName);
        Check(createdTag != null, "create and assign tag from context interface"); _catalog.DeleteTag(createdTag!.Id); LoadTags();
        foreach (var window in GetChildren().OfType<Window>().Where(w => w is not PopupMenu).ToArray()) window.QueueFree();
        Check(_trackLanes.Count > 0, "demo tracks missing");
        var original = _mix.Clone(); var lane = _trackLanes[0]; double start = lane.Track.Start;
        float x = (float)((lane.Track.Start + lane.Track.Duration / 2) * _pixelsPerSecond);
        lane._GuiInput(new InputEventMouseButton { Position = new(x, 55), ButtonIndex = MouseButton.Left, Pressed = true });
        lane._GuiInput(new InputEventMouseMotion { Position = new(x + 24, 55) });
        lane._GuiInput(new InputEventMouseButton { Position = new(x + 24, 55), ButtonIndex = MouseButton.Left, Pressed = false });
        Check(_mix.Tracks[0].Start > start, "clip positioning"); RestoreHistory(false); Check(Math.Abs(_mix.Tracks[0].Start - start) < .00001, "undo positioning");
        lane = _trackLanes[0]; float edge = (float)(lane.Track.Start * _pixelsPerSecond);
        lane._GuiInput(new InputEventMouseButton { Position = new(edge + 2, lane.Size.Y - 22), ButtonIndex = MouseButton.Left, Pressed = true });
        lane._GuiInput(new InputEventMouseMotion { Position = new(edge + 10, lane.Size.Y - 22) });
        lane._GuiInput(new InputEventMouseButton { Position = new(edge + 10, lane.Size.Y - 22), ButtonIndex = MouseButton.Left, Pressed = false });
        Check(_mix.Tracks[0].TrimStart > original.Tracks[0].TrimStart, "left trim handle"); RestoreHistory(false);
        await ExerciseMixControls();
        await ToggleMixPlayback();
        await ToSignal(GetTree().CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
        Check(_transport > 0, "live playhead advance");
        _mix.Tracks[0].Muted = true; UpdateLiveMix();
        await SeekMix(.3); Check(_playing, "resume after seeking"); StopMix(true);
        _mix = original; RebuildTracks(); SaveMix();
        GD.Print("SOUNDSHELF_UI_OK context file-location preview tag-create drag trim pitch volume undo playback mute seek");
    }
}
