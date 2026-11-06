using Godot;
using SoundShelf.Core;

namespace SoundShelf.App;

public partial class Main
{
    private async Task ExerciseLibraryExport()
    {
        var previousLibrary = _library;
        string directory = Path.Combine(_data, "Export picker " + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(directory, "Level 1", "Level 2"));
        var library = _catalog.AddLibrary(directory);
        async Task Layout() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        T Find<T>(Window window, string name) where T : Node => window.FindChild(name, true, false) as T ?? throw new InvalidOperationException("Missing export control: " + name);
        void SelectNested(Window window)
        {
            var picker = Find<OptionButton>(window, "ExportLibrary");
            int index = Enumerable.Range(0, picker.ItemCount).First(i => picker.GetItemText(i) == library.Name); picker.Select(index); picker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
            var folders = Find<Tree>(window, "ExportFolders");
            var first = folders.GetRoot().GetFirstChild() ?? throw new InvalidOperationException("The first directory level is missing."); first.Collapsed = false;
            var second = first.GetFirstChild() ?? throw new InvalidOperationException("The nested directory level is missing."); second.Select(0);
            if ((string)second.GetMetadata(0) != "Level 1/Level 2") throw new InvalidOperationException("Selecting nested export folders must preserve their relative path.");
        }
        try
        {
            ChooseExport(); await Layout();
            var choices = GetChildren().OfType<Window>().First(w => w.FindChild("ExportToLibraryChoice", true, false) != null);
            Find<Button>(choices, "ExportToDiskChoice");
            Find<Button>(choices, "ExportToLibraryChoice").EmitSignal(Button.SignalName.Pressed); await Layout();
            var window = GetNode<Window>("LibraryExportDialog"); SelectNested(window);
            Find<LineEdit>(window, "ExportFilename").Text = "UI export";
            Find<Button>(window, "ExportToLibrary").EmitSignal(Button.SignalName.Pressed);
            var timeout = DateTime.UtcNow.AddSeconds(20);
            while (_operationBusy && DateTime.UtcNow < timeout) await ToSignal(GetTree().CreateTimer(.05), SceneTreeTimer.SignalName.Timeout);
            string target = Path.Combine(directory, "Level 1", "Level 2", "UI export.wav");
            if (_operationBusy || !File.Exists(target)) throw new InvalidOperationException("The library export did not produce the chosen file.");
            var sound = _catalog.Browse(library.Id, "Level 1/Level 2", "", null, 0).Entries.Single().Sound!;
            if (sound.FullPath != target || _library?.Id != library.Id || _folder != "Level 1/Level 2") throw new InvalidOperationException("Library export must register the file and show its destination folder.");
            foreach (var popup in GetChildren().OfType<Window>().Where(w => w is not PopupMenu).ToArray()) { popup.Hide(); popup.QueueFree(); } await Layout();
            byte[] original = File.ReadAllBytes(target);
            window = ChooseLibraryExport(false); SelectNested(window); Find<LineEdit>(window, "ExportFilename").Text = "UI export.wav";
            Find<Button>(window, "ExportToLibrary").EmitSignal(Button.SignalName.Pressed); await Layout();
            var confirm = window.GetChildren().OfType<ConfirmationDialog>().Single(); confirm.EmitSignal(ConfirmationDialog.SignalName.Canceled); await Layout();
            if (_operationBusy || !original.SequenceEqual(File.ReadAllBytes(target))) throw new InvalidOperationException("Canceling replacement must preserve the existing export.");
            window.Hide(); window.QueueFree();
            GD.Print("SOUNDSHELF_LIBRARY_EXPORT_OK disk-or-library choice nested-folder filename render index browser-folder overwrite-cancel");
        }
        finally
        {
            _catalog.RemoveLibrary(library.Id);
            if (previousLibrary != null) { SelectLibrary(previousLibrary); await RefreshBrowser(); }
        }
    }
}
