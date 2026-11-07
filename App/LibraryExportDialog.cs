using Godot;
using SoundShelf.Core;

namespace SoundShelf.App;

public partial class Main
{
    private Window ChooseLibraryExport(bool ogg)
    {
        var libraries = _catalog.Libraries();
        var (window, body) = Dialog("Export to library", new(660, 610)); window.Name = "LibraryExportDialog";
        body.AddChild(Skin.Label("Choose a library and destination folder", 17));
        var libraryPicker = new OptionButton { Name = "ExportLibrary", TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis }; body.AddChild(libraryPicker);
        foreach (var library in libraries) libraryPicker.AddItem(library.Name);
        int initial = libraries.FindIndex(l => l.Id == _library?.Id); if (initial >= 0) libraryPicker.Select(initial);
        var folders = new Tree { Name = "ExportFolders", HideRoot = false, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 240) }; body.AddChild(folders);
        var folderPath = Skin.Label("", 12, Skin.Muted); folderPath.Name = "ExportFolderPath"; folderPath.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; body.AddChild(folderPath);
        body.AddChild(Skin.Label(ogg ? "File name · OGG Vorbis" : "File name · 24-bit WAV", 12, Skin.Muted));
        var fileName = new LineEdit { Name = "ExportFilename", Text = ExportDestination.FileName(SuggestedExportName(_mix.Name), ogg), PlaceholderText = "Export file name" }; body.AddChild(fileName);
        var hint = Skin.Label("", 12, new Color("efb08d")); hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; body.AddChild(hint);
        var actions = new HBoxContainer(); body.AddChild(actions);
        var export = new Button { Name = "ExportToLibrary", Text = "Export to this folder", SizeFlagsHorizontal = SizeFlags.ExpandFill }; actions.AddChild(export);
        actions.AddChild(Skin.Button("Cancel", window.QueueFree));
        Library? SelectedLibrary() => libraryPicker.Selected >= 0 && libraryPicker.Selected < libraries.Count ? libraries[libraryPicker.Selected] : null;
        void Expand(TreeItem item)
        {
            if (item.HasMeta("loaded")) return;
            while (item.GetFirstChild() is { } child) child.Free();
            try
            {
                var library = SelectedLibrary() ?? throw new InvalidOperationException("Choose a library.");
                string relative = (string)item.GetMetadata(0);
                string directory = Path.GetDirectoryName(ExportDestination.InLibrary(library, relative, "destination-check", ogg))!;
                foreach (var child in new DirectoryInfo(directory).GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var next = folders.CreateItem(item); next.SetText(0, child.Name); next.SetMetadata(0, Paths.Relative(library.Path, child.FullName)); next.SetTooltipText(0, child.FullName);
                    next.Collapsed = true; var placeholder = folders.CreateItem(next); placeholder.SetText(0, "Expand to load folders…"); placeholder.SetSelectable(0, false);
                }
                item.SetMeta("loaded", true); hint.Text = "";
            }
            catch (Exception e) { hint.Text = e.Message; }
        }
        void Reload()
        {
            folders.Clear(); hint.Text = ""; export.Disabled = false;
            try
            {
                var library = SelectedLibrary() ?? throw new InvalidOperationException("Add a library before exporting to it.");
                Paths.CanonicalDirectory(library.Path);
                var root = folders.CreateItem(); root.SetText(0, library.Name + " (library root)"); root.SetMetadata(0, ""); root.SetTooltipText(0, library.Path);
                Expand(root); root.Select(0); folderPath.Text = library.Path;
            }
            catch (Exception e) { export.Disabled = true; hint.Text = e.Message; folderPath.Text = "Library unavailable"; }
        }
        libraryPicker.ItemSelected += index => Reload();
        folders.ItemCollapsed += item => { if (!item.Collapsed) Expand(item); };
        folders.ItemSelected += () => { if (folders.GetSelected() is { } selected && SelectedLibrary() is { } library) folderPath.Text = Paths.Join(library.Path, (string)selected.GetMetadata(0)); };
        actions.AddChild(Skin.Button("Refresh folders", Reload));
        export.Pressed += () =>
        {
            try
            {
                var library = SelectedLibrary() ?? throw new InvalidOperationException("Choose a library.");
                var selected = folders.GetSelected() ?? throw new InvalidOperationException("Select a destination folder.");
                string relative = (string)selected.GetMetadata(0);
                string target = ExportDestination.InLibrary(library, relative, fileName.Text, ogg);
                void StartExport()
                {
                    try { target = ExportDestination.InLibrary(library, relative, fileName.Text, ogg); }
                    catch (Exception e) { hint.Text = e.Message; return; }
                    window.Hide(); window.QueueFree(); _ = Guard(() => ExportMix(target, ogg, library));
                }
                if (!File.Exists(target)) { StartExport(); return; }
                var confirm = new ConfirmationDialog { Title = "Replace existing file?", DialogText = "An audio file already exists at this location. Replace it?\n\n" + target, DialogAutowrap = true, OkButtonText = "Replace" };
                window.AddChild(confirm); confirm.Confirmed += () => { confirm.QueueFree(); StartExport(); }; confirm.Canceled += confirm.QueueFree; confirm.PopupCentered(new(520, 230));
            }
            catch (Exception e) { hint.Text = e.Message; }
        };
        Reload(); return window;
    }
}
