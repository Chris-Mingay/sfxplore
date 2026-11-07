using Godot;
using SoundShelf.Core;

namespace SoundShelf.App;

public partial class Main
{
    private readonly record struct WorkspaceItem(long Id, bool IsFolder);
    private enum WorkspaceAction { NewMix, NewFolder, Rename, Duplicate, Delete, Export, Play }
    private const string WorkspaceFolderMetadata = "workspace_folder_id";
    private Tree _mixTree = null!;
    private PopupMenu _workspaceMenu = null!;
    private Texture2D _workspaceFolderIcon = null!, _workspaceMixIcon = null!;
    private readonly Dictionary<WorkspaceItem, TreeItem> _workspaceRows = new();
    private WorkspaceItem? _workspaceMenuTarget;
    private bool _refreshingMixTree;

    private void BuildMixWorkspace()
    {
        const float minHeight = 105;
        var workspace = new VBoxContainer { CustomMinimumSize = new(0, minHeight) };
        _sidebar.AddChild(workspace);
        var heading = Skin.Label("MIX WORKSPACE", 12, Skin.Muted);
        heading.MouseFilter = MouseFilterEnum.Stop;
        heading.TooltipText = "Click or drop here to use the top level";
        workspace.AddChild(heading);

        var actions = new HBoxContainer(); workspace.AddChild(actions);
        var newFolder = Skin.Button("New folder", () => NewWorkspaceFolder(SelectedWorkspaceFolder()), "Create a folder in the selected location");
        var newMix = Skin.Button("New mix", () => NewWorkspaceMix(SelectedWorkspaceFolder()), "Create a mix in the selected location");
        newFolder.SizeFlagsHorizontal = newMix.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(newFolder); actions.AddChild(newMix);
        _workspaceFolderIcon = GD.Load<Texture2D>("res://Assets/Icons/workspace-folder.svg");
        _workspaceMixIcon = GD.Load<Texture2D>("res://Assets/Icons/workspace-mix.svg");
        _mixTree = new Tree { HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, AccessibilityName = "Mix workspace" };
        _mixTree.AddThemeStyleboxOverride("panel", Skin.Box(Skin.Background));
        workspace.AddChild(_mixTree);
        _mixTree.ItemSelected += OpenSelectedMix;
        _mixTree.ItemActivated += OpenSelectedMix;

        _workspaceMenu = new PopupMenu();
        AddChild(_workspaceMenu);
        _workspaceMenu.IdPressed += id => RunWorkspaceAction((WorkspaceAction)id);
        _mixTree.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true } mouse) return;
            var row = _mixTree.GetItemAtPosition(mouse.Position);
            if (mouse.ButtonIndex == MouseButton.Right) ShowWorkspaceMenu(ReadWorkspaceItem(row));
            else if (mouse.ButtonIndex == MouseButton.Left && row == null) _mixTree.DeselectAll();
        };
        heading.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _mixTree.DeselectAll();
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) ShowWorkspaceMenu(null);
        };

        _mixTree.SetDragForwarding(
            Callable.From<Vector2, Variant>(BeginWorkspaceDrag),
            Callable.From<Vector2, Variant, bool>((position, data) =>
            {
                var target = WorkspaceRowAt(position);
                bool canDrop = CanDropWorkspaceItem(data, target);
                _mixTree.DropModeFlags = (int)(canDrop && target != null ? Tree.DropModeFlagsEnum.OnItem : Tree.DropModeFlagsEnum.Disabled);
                return canDrop;
            }),
            Callable.From<Vector2, Variant>((position, data) => DropWorkspaceItem(data, WorkspaceRowAt(position))));
        heading.SetDragForwarding(default,
            Callable.From<Vector2, Variant, bool>((_, data) => CanDropWorkspaceItem(data, null)),
            Callable.From<Vector2, Variant>((_, data) => DropWorkspaceItem(data, null)));

        void AlignWorkspace()
        {
            float available = _sidebar.Size.Y - _libraryRegion.GetCombinedMinimumSize().Y - _sidebar.GetThemeConstant("separation");
            float height = _mixPanel.Visible ? _sidebar.GetGlobalRect().End.Y - _mixPanel.GlobalPosition.Y : _sidebar.Size.Y / 3;
            workspace.CustomMinimumSize = new(0, Math.Clamp(height, minHeight, Math.Max(minHeight, available)));
        }
        _sidebar.Resized += AlignWorkspace;
        _mixPanel.ItemRectChanged += AlignWorkspace;
        _mixPanel.VisibilityChanged += AlignWorkspace;
        AlignWorkspace();
    }

    private static WorkspaceItem? ReadWorkspaceItem(TreeItem? row)
    {
        if (row == null) return null;
        if (row.HasMeta(WorkspaceFolderMetadata)) return new((long)row.GetMeta(WorkspaceFolderMetadata), true);
        var mixId = row.GetMetadata(0);
        return mixId.VariantType == Variant.Type.Int ? new((long)mixId, false) : null;
    }

    private long? SelectedWorkspaceFolder()
    {
        var row = _mixTree.GetSelected();
        if (ReadWorkspaceItem(row) is { IsFolder: false }) row = row!.GetParent();
        return ReadWorkspaceItem(row)?.Id;
    }

    private TreeItem? WorkspaceRowAt(Vector2 position) =>
        position.IsFinite() ? _mixTree.GetItemAtPosition(position) : _mixTree.GetSelected();

    private Variant BeginWorkspaceDrag(Vector2 position)
    {
        var row = WorkspaceRowAt(position);
        if (row == null || ReadWorkspaceItem(row) == null) return default;
        var preview = new PanelContainer();
        preview.AddThemeStyleboxOverride("panel", Skin.Box(Skin.Raised, border: Skin.Accent));
        preview.AddChild(Skin.Label(row.GetText(0)));
        _mixTree.SetDragPreview(preview);
        return row;
    }

    private bool CanDropWorkspaceItem(Variant data, TreeItem? target)
    {
        if (data.VariantType != Variant.Type.Object || data.AsGodotObject() is not TreeItem source ||
            source.GetTree() != _mixTree || ReadWorkspaceItem(source) == null) return false;
        if (target != null && ReadWorkspaceItem(target) is not { IsFolder: true }) return false;
        if (source.GetParent() == (target ?? _mixTree.GetRoot())) return false;
        for (var row = target; row != null; row = row.GetParent())
            if (row == source) return false;
        return true;
    }

    private void DropWorkspaceItem(Variant data, TreeItem? target)
    {
        if (!CanDropWorkspaceItem(data, target)) return;
        var item = ReadWorkspaceItem((TreeItem)data.AsGodotObject())!.Value;
        long? folder = ReadWorkspaceItem(target)?.Id;
        // Defer rebuilding the tree until Godot finishes dispatching the drop.
        Callable.From(() =>
        {
            try
            {
                if (item.IsFolder) _catalog.MoveMixFolder(item.Id, folder);
                else _catalog.MoveMix(item.Id, folder);
                RefreshMixTree(item, revealSelection: true);
            }
            catch (Exception e) { Error(e.Message); }
        }).CallDeferred();
    }

    private void ShowWorkspaceMenu(WorkspaceItem? item)
    {
        _workspaceMenuTarget = item;
        _workspaceMenu.Clear();
        if (item is not { IsFolder: false })
        {
            _workspaceMenu.AddItem("New mix…", (int)WorkspaceAction.NewMix);
            _workspaceMenu.AddItem("New folder…", (int)WorkspaceAction.NewFolder);
            if (item.HasValue) _workspaceMenu.AddSeparator();
        }
        if (item.HasValue)
        {
            if (item is { IsFolder: false })
            {
                _workspaceMenu.AddItem("Play", (int)WorkspaceAction.Play);
                _workspaceMenu.AddItem("Export to…", (int)WorkspaceAction.Export);
            }
            _workspaceMenu.AddItem("Rename…", (int)WorkspaceAction.Rename);
            _workspaceMenu.AddItem("Duplicate…", (int)WorkspaceAction.Duplicate);
            _workspaceMenu.AddItem("Delete…", (int)WorkspaceAction.Delete);
        }
        _workspaceMenu.Position = (Vector2I)GetScreenPosition() + (Vector2I)GetGlobalMousePosition();
        _workspaceMenu.Popup();
    }

    private void RunWorkspaceAction(WorkspaceAction action)
    {
        var item = _workspaceMenuTarget;
        if (action == WorkspaceAction.NewMix) NewWorkspaceMix(item?.Id);
        else if (action == WorkspaceAction.NewFolder) NewWorkspaceFolder(item?.Id);
        else if (item is { } target && _workspaceRows.TryGetValue(target, out var row))
        {
            string name = row.GetText(0);
            switch (action)
            {
                case WorkspaceAction.Play when !target.IsFolder:
                    _ = Guard(async () =>
                    {
                        if (FindWorkspaceMix(target.Id) is not { } preview) return;
                        OpenWorkspaceMix(preview);
                        row.Select(0);
                        StopMix(reset: true);
                        await StartMixPlayback();
                    });
                    break;
                case WorkspaceAction.Rename: RenameWorkspaceItem(target, name); break;
                case WorkspaceAction.Duplicate: DuplicateWorkspaceItem(target, name); break;
                case WorkspaceAction.Delete: DeleteWorkspaceItem(target, name); break;
                case WorkspaceAction.Export:
                    if (!target.IsFolder && FindWorkspaceMix(target.Id) is { } mix) ChooseExportDestination(null, mix);
                    break;
            }
        }
    }

    private void NewWorkspaceFolder(long? parent)
    {
        NameDialog("New folder", "", name =>
        {
            long id = _catalog.CreateMixFolder(name, parent);
            RefreshMixTree(new(id, true), revealSelection: true);
        }, "Folder name");
    }

    private void NewWorkspaceMix(long? folder) =>
        NameDialog("New mix", "Untitled mix", name => CreateWorkspaceMix(new MixProject { Name = name }, folder));

    private void CreateWorkspaceMix(MixProject mix, long? folder)
    {
        SaveMix();
        _catalog.SaveMix(mix, makeActive: false, newMixFolderId: folder);
        SetMix(mix);
        _mixPanel.Show();
        RefreshMixTree(new(mix.Id, false), revealSelection: true);
    }

    private MixProject? FindWorkspaceMix(long id) =>
        id == _mix.Id ? _mix : _mixCache.GetValueOrDefault(id)?.Clone() ?? _catalog.LoadMix(id);

    private void OpenSelectedMix()
    {
        if (_refreshingMixTree || ReadWorkspaceItem(_mixTree.GetSelected()) is not { IsFolder: false } item) return;
        if (FindWorkspaceMix(item.Id) is { } mix) OpenWorkspaceMix(mix);
    }

    private void OpenWorkspaceMix(MixProject mix)
    {
        if (mix.Id != _mix.Id)
        {
            SaveMix();
            SetMix(mix);
        }
        _mixPanel.Show();
    }

    private void RenameWorkspaceItem(WorkspaceItem item, string currentName)
    {
        string kind = item.IsFolder ? "folder" : "mix";
        NameDialog("Rename " + kind, currentName, name =>
        {
            if (item.IsFolder) _catalog.RenameMixFolder(item.Id, name);
            else
            {
                var mix = FindWorkspaceMix(item.Id) ?? throw new InvalidOperationException("Mix not found.");
                mix.Name = name;
                SaveMixSnapshot(mix);
            }
            RefreshMixTree(item, revealSelection: true);
        }, item.IsFolder ? "Folder name" : "Mix name");
    }

    private void DuplicateWorkspaceItem(WorkspaceItem item, string currentName)
    {
        string kind = item.IsFolder ? "folder" : "mix";
        NameDialog("Duplicate " + kind, currentName + " copy", name =>
        {
            if (item.IsFolder)
            {
                SaveMix();
                _pendingSave.GetAwaiter().GetResult();
                long id = _catalog.DuplicateMixFolder(item.Id, name);
                RefreshMixTree(new(id, true), revealSelection: true);
            }
            else
            {
                var copy = FindWorkspaceMix(item.Id)?.Clone() ?? throw new InvalidOperationException("Mix not found.");
                copy.Id = 0;
                copy.Name = name;
                long? folder = _catalog.Mixes().Find(mix => mix.Id == item.Id).FolderId;
                CreateWorkspaceMix(copy, folder);
            }
        }, item.IsFolder ? "Folder name" : "Mix name");
    }

    private void DeleteWorkspaceItem(WorkspaceItem item, string name)
    {
        string kind = item.IsFolder ? "folder" : "mix";
        string description = item.IsFolder ? $"Delete ‘{name}’ and all of its nested folders and mixes?" : $"Delete the mix ‘{name}’?";
        Confirm("Delete " + kind, description + "\n\nOriginal sound files are retained.", () =>
        {
            // Complete queued autosaves before IDs can be reused by new entries.
            _pendingSave.GetAwaiter().GetResult();
            List<long> deleted;
            if (item.IsFolder) deleted = _catalog.DeleteMixFolder(item.Id);
            else { _catalog.DeleteMix(item.Id); deleted = new() { item.Id }; }
            foreach (long id in deleted) _mixCache.Remove(id);

            if (deleted.Contains(_mix.Id))
            {
                var first = _catalog.Mixes().FirstOrDefault();
                SetMix(_catalog.LoadMix(first.Id) ?? new MixProject());
                SaveMix();
            }
            RefreshMixTree();
        }, "Delete");
    }

    private void RefreshMixTree(WorkspaceItem? selectedItem = null, bool revealSelection = false)
    {
        _mixTitle.Text = _mix.Name;
        selectedItem ??= ReadWorkspaceItem(_mixTree.GetSelected());
        var collapsed = _workspaceRows.Where(row => row.Key.IsFolder && row.Value.Collapsed).Select(row => row.Key).ToHashSet();
        _refreshingMixTree = true;
        try
        {
            _mixTree.Clear();
            _workspaceRows.Clear();
            var root = _mixTree.CreateItem();
            TreeItem ParentRow(long? id) => id.HasValue ? _workspaceRows[new(id.Value, true)] : root;

            var foldersByParent = _catalog.MixFolders().ToLookup(folder => folder.ParentId);
            var parents = new Queue<long?>();
            parents.Enqueue(null);
            while (parents.TryDequeue(out long? parent))
            {
                foreach (var folder in foldersByParent[parent])
                {
                    var row = AddWorkspaceItem(ParentRow(parent), folder.Name, _workspaceFolderIcon);
                    row.SetMeta(WorkspaceFolderMetadata, folder.Id);
                    _workspaceRows.Add(new(folder.Id, true), row);
                    parents.Enqueue(folder.Id);
                }
            }

            foreach (var mix in _catalog.Mixes())
            {
                string name = _mixCache.GetValueOrDefault(mix.Id)?.Name ?? mix.Name;
                var row = AddWorkspaceItem(ParentRow(mix.FolderId), name, _workspaceMixIcon);
                row.SetMetadata(0, mix.Id);
                _workspaceRows.Add(new(mix.Id, false), row);
            }

            foreach (var item in collapsed)
                if (_workspaceRows.TryGetValue(item, out var row)) row.Collapsed = true;
            var selected = _workspaceRows.GetValueOrDefault(selectedItem ?? new(_mix.Id, false))
                ?? _workspaceRows.GetValueOrDefault(new(_mix.Id, false));
            if (selected == null) return;
            if (revealSelection)
                for (TreeItem? row = selected; row != null && row != root; row = row.GetParent()) row.Collapsed = false;
            selected.Select(0);
            if (revealSelection) _mixTree.ScrollToItem(selected);
        }
        finally { _refreshingMixTree = false; }
    }

    private TreeItem AddWorkspaceItem(TreeItem parent, string name, Texture2D icon)
    {
        var row = _mixTree.CreateItem(parent);
        row.SetText(0, name);
        row.SetTextOverrunBehavior(0, TextServer.OverrunBehavior.TrimEllipsis);
        row.SetTooltipText(0, name);
        row.SetIcon(0, icon);
        return row;
    }
}
