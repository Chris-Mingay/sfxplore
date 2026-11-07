using Godot;
using SoundShelf.Core;

namespace SoundShelf.App;

public partial class Main
{
    private const string LastCopyDirectory = "last_copy_directory";
    private const string LastExportDirectory = "last_export_directory";
    private readonly Dictionary<string, string> _saveDirectories = new();

    private FileDialog CreateSaveDialog(string title, string fileName, string setting, string? fallback = null)
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem,
            Title = title, UseNativeDialog = true
        };
        AddChild(dialog);
        string? directory = _saveDirectories.GetValueOrDefault(setting) ?? _catalog.GetSetting(setting);
        if (directory != null && Directory.Exists(directory)) dialog.CurrentDir = directory;
        else if (fallback != null && Directory.Exists(fallback)) dialog.CurrentDir = fallback;
        dialog.CurrentFile = fileName;
        return dialog;
    }

    private void ShowSaveDialog(FileDialog dialog, Action<string> selected)
    {
        dialog.FileSelected += path =>
        {
            dialog.QueueFree();
            try { selected(path); }
            catch (Exception e) { Error(e.Message); }
        };
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCenteredRatio(.7f);
    }

    private void RememberSaveDirectory(string setting, string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        _saveDirectories[setting] = directory;
        EnqueuePersistence(() => _catalog.SetSetting(setting, directory));
    }

    private void SaveToChosenPath(string chosen, string target, Action<bool> save)
    {
        bool exists = File.Exists(target);
        // If adding or changing an extension changes the path, the picker only checked the original name.
        if (exists && !string.Equals(Path.GetFullPath(chosen), Path.GetFullPath(target), Paths.Comparison))
            Confirm("Replace existing file?", "Replace this file?\n\n" + target, () => save(true), "Replace");
        else save(exists);
    }

    private void ChooseSoundCopy(Sound sound)
    {
        if (_operationBusy) { Error("Wait for the current operation to finish."); return; }
        if (!File.Exists(sound.FullPath)) { Error("The source sound is unavailable. Rescan the library to refresh its contents."); return; }
        string extension = Path.GetExtension(sound.FullPath);
        var dialog = CreateSaveDialog("Copy sound to", Path.GetFileName(sound.FullPath), LastCopyDirectory, Path.GetDirectoryName(sound.FullPath));
        ShowSaveDialog(dialog, path =>
        {
            string target = path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
            SaveToChosenPath(path, target, overwrite => _ = Guard(() => CopySound(sound, target, overwrite)));
        });
    }

    private async Task CopySound(Sound sound, string destination, bool overwrite)
    {
        if (_operationBusy) throw new InvalidOperationException("Wait for the current operation to finish.");
        var cancellation = BeginOperation();
        try
        {
            Status("Copying " + Path.GetFileName(sound.FullPath) + "…");
            await SoundFileCopy.CopyAsync(sound.FullPath, destination, overwrite, cancellation);
            RememberSaveDirectory(LastCopyDirectory, destination);
            Status("Copied to " + destination);
        }
        finally { EndOperation(); }
    }

    private (Window Window, VBoxContainer Body) Dialog(string title, Vector2I size)
    {
        var window = new Window { Title = title, Size = size, MinSize = new(420, 260), Transient = true, Exclusive = true, WrapControls = false };
        AddChild(window); window.CloseRequested += window.QueueFree;
        var panel = new PanelContainer(); panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); window.AddChild(panel);
        var body = new VBoxContainer(); panel.AddChild(body); window.PopupCentered(); return (window, body);
    }
    private void ShowTags(Sound? sound)
    {
        var (window, body) = Dialog(sound == null ? "Manage tags" : "Tags · " + Path.GetFileName(sound.FullPath), new(650, 510));
        body.AddChild(Skin.Label(sound == null ? "Create and edit tags for your collection" : "Check tags to assign them to this sound", 16));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; body.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(list);
        var name = new LineEdit { PlaceholderText = "New tag name", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var color = new ColorPickerButton { Color = new("8cc7ed"), CustomMinimumSize = new(64, 32), EditAlpha = false };
        var row = new HBoxContainer(); body.AddChild(row); row.AddChild(name); row.AddChild(color);
        var hint = Skin.Label("", 12, new Color("efb08d")); body.AddChild(hint);
        void Refresh()
        {
            Skin.Clear(list); var assigned = sound == null ? new HashSet<long>() : _catalog.Tags(sound.Id).Select(t => t.Id).ToHashSet();
            foreach (var tag in _catalog.Tags())
            {
                var line = new HBoxContainer(); list.AddChild(line);
                if (sound != null)
                {
                    var check = new CheckBox { ButtonPressed = assigned.Contains(tag.Id) }; line.AddChild(check);
                    check.Toggled += value => { _catalog.AssignTag(sound.Id, tag.Id, value); _ = RefreshBrowser(); };
                }
                var picker = new ColorPickerButton { Color = new(tag.Color), CustomMinimumSize = new(40, 30), EditAlpha = false }; line.AddChild(picker);
                var text = new LineEdit { Text = tag.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill }; line.AddChild(text);
                line.AddChild(Skin.Button("Save", () =>
                {
                    try { _catalog.SaveTag(text.Text, "#" + picker.Color.ToHtml(false), tag.Id); hint.Text = "Tag updated"; LoadTags(); _ = RefreshBrowser(); Refresh(); } catch (Exception e) { hint.Text = e.Message; }
                }));
                line.AddChild(Skin.Button("Delete", () =>
                {
                    _catalog.DeleteTag(tag.Id); LoadTags(); _ = RefreshBrowser(); Refresh();
                }));
            }
            if (list.GetChildCount() == 0) list.AddChild(Skin.Label("No tags yet. Create your first tag below.", 14, Skin.Muted));
        }
        row.AddChild(Skin.Button("Create tag", () =>
        {
            try { var tag = _catalog.SaveTag(name.Text, "#" + color.Color.ToHtml(false)); if (sound != null) _catalog.AssignTag(sound.Id, tag.Id, true); name.Text = ""; hint.Text = sound == null ? "Tag created" : "Tag created and assigned"; LoadTags(); Refresh(); _ = RefreshBrowser(); }
            catch (Exception e) { hint.Text = e.Message; }
        }));
        body.AddChild(Skin.Button("Done", window.QueueFree)); Refresh();
    }
    private void ShowScanHistory()
    {
        if (_library == null) { Error("Select a library to see its scan history."); return; }
        var (window, body) = Dialog("Scan history · " + _library.Name, new(620, 420));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(list);
        var history = _catalog.ScanHistory(_library.Id);
        foreach (var scan in history) list.AddChild(Skin.Button(scan.Description, () => { window.QueueFree(); ShowScanResult(_catalog.GetScan(scan.Id)); }));
        if (history.Count == 0) list.AddChild(Skin.Label("This library has not been scanned yet."));
        body.AddChild(Skin.Button("Close", window.QueueFree));
    }
    private void ShowScanResult(ScanResult result)
    {
        var (window, body) = Dialog("Scan complete", new(790, 540));
        body.AddChild(Skin.Label($"{result.Added:N0} added   ·   {result.Removed:N0} removed   ·   {result.Changed:N0} changed", 18));
        body.AddChild(Skin.Label("Removed files were removed from the browser. No source files were deleted.", 12, Skin.Muted));
        var bar = new HBoxContainer(); body.AddChild(bar);
        var kind = new OptionButton(); foreach (string value in new[] { "Added", "Removed", "Changed", "Error", "Skipped" }) kind.AddItem(value); bar.AddChild(kind);
        var search = new LineEdit { PlaceholderText = "Filter report paths…", SizeFlagsHorizontal = SizeFlags.ExpandFill }; bar.AddChild(search);
        var list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(list);
        var pagination = new HBoxContainer(); body.AddChild(pagination); int offset = 0; var page = Skin.Label("");
        void Refresh()
        {
            list.Clear(); var entries = result.Entries.Where(e => e.Kind == kind.GetItemText(kind.Selected) && e.Path.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var e in entries.Skip(offset).Take(200)) { int i = list.AddItem(e.Path + (e.Detail.Length > 0 ? " — " + e.Detail : "")); list.SetItemTooltip(i, e.Path + "\n" + e.Detail); }
            page.Text = $"{Math.Min(offset + 1, entries.Count)}–{Math.Min(offset + 200, entries.Count)} / {entries.Count:N0}";
        }
        pagination.AddChild(Skin.Button("Previous", () => { offset = Math.Max(0, offset - 200); Refresh(); })); pagination.AddChild(page);
        pagination.AddChild(Skin.Button("Next", () => { if (offset + 200 < result.Entries.Count(e => e.Kind == kind.GetItemText(kind.Selected) && e.Path.Contains(search.Text, StringComparison.OrdinalIgnoreCase))) offset += 200; Refresh(); }));
        kind.ItemSelected += _ => { offset = 0; Refresh(); }; search.TextChanged += _ => { offset = 0; Refresh(); };
        var actions = new HBoxContainer(); body.AddChild(actions);
        actions.AddChild(Skin.Button("Browse newly added sounds", () =>
        { BrowseNewSounds(result); window.QueueFree(); }));
        actions.AddChild(Skin.Button("Browse library folders", () =>
        { var library = _catalog.Libraries().FirstOrDefault(l => l.Id == result.LibraryId); if (library != null) SelectLibrary(library); window.QueueFree(); }));
        actions.AddChild(Skin.Button("Close", window.QueueFree)); Refresh();
    }
    private void NameDialog(string title, string initial, Action<string> accepted, string fieldLabel = "Mix name")
    {
        var (window, body) = Dialog(title, new(440, 270));
        body.AddChild(Skin.Label(fieldLabel, 14, Skin.Muted)); var input = new LineEdit { Text = initial }; body.AddChild(input);
        var error = Skin.Label("", 12, new Color("efb08d")); body.AddChild(error);
        void Accept()
        {
            string name = input.Text.Trim();
            if (name.Length == 0) { error.Text = "Enter a name."; return; }
            try { accepted(name); window.QueueFree(); }
            catch (Exception e) { error.Text = e.Message; }
        }
        var actions = new HBoxContainer(); body.AddChild(actions); actions.AddChild(Skin.Button("Save", Accept)); actions.AddChild(Skin.Button("Cancel", window.QueueFree)); input.TextSubmitted += _ => Accept(); input.GrabFocus(); input.SelectAll();
    }
}
