using Microsoft.Data.Sqlite;
using SoundShelf.Core;
using Xunit;

namespace SoundShelf.Tests;

public sealed class Workspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "soundshelf-tests-" + Guid.NewGuid());
    public Catalog Catalog { get; }
    public Workspace() { Directory.CreateDirectory(Root); Catalog = new Catalog(Path.Combine(Root, "catalog.sqlite")); }
    public string DirectoryAt(string relative) { string path = Path.Combine(Root, relative); Directory.CreateDirectory(path); return path; }
    public string FileAt(string relative, string body = "fixture") { string path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, body); return path; }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(Root, true); } catch (IOException) { } }
}

public class CatalogTests
{
    [Fact]
    public void RejectsIdenticalParentChildButAllowsPrefixSiblings()
    {
        using var w = new Workspace(); string parent = w.DirectoryAt("packs"), child = w.DirectoryAt("packs/child"), sibling = w.DirectoryAt("packs-extra");
        w.Catalog.AddLibrary(child);
        Assert.Throws<InvalidOperationException>(() => w.Catalog.AddLibrary(child));
        Assert.Throws<InvalidOperationException>(() => w.Catalog.AddLibrary(parent));
        Assert.Throws<InvalidOperationException>(() => w.Catalog.AddLibrary(w.DirectoryAt("packs/child/deeper")));
        Assert.NotNull(w.Catalog.AddLibrary(sibling));
        Assert.True(Paths.Overlaps(Path.GetPathRoot(w.Root)!, child));
        Assert.Throws<InvalidOperationException>(() => w.Catalog.AddLibrary(Path.GetPathRoot(w.Root)!));
        if (OperatingSystem.IsWindows()) Assert.Throws<InvalidOperationException>(() => w.Catalog.AddLibrary(child.ToUpperInvariant()));
    }
    [Fact]
    public async Task LibraryRootShowsFoldersWhileNewAdditionsAreExplicitFlatResults()
    {
        using var w = new Workspace(); var library = w.Catalog.AddLibrary(w.DirectoryAt("collection (OGG)"));
        for (int folder = 0; folder < 30; folder++)
            for (int sound = 0; sound < 3; sound++) w.FileAt($"collection (OGG)/Category {folder:00}/effect {sound}.ogg");
        var scan = await w.Catalog.ScanAsync(library, null, default);
        var root = w.Catalog.Browse(library.Id, "", "", null, 0);
        Assert.Equal(30, root.Total); Assert.Equal(30, root.Entries.Count); Assert.All(root.Entries, e => Assert.True(e.IsFolder));
        var contents = w.Catalog.Browse(library.Id, root.Entries[0].RelativePath, "", null, 0);
        Assert.Equal(3, contents.Total); Assert.All(contents.Entries, e => Assert.False(e.IsFolder));
        var additions = w.Catalog.Browse(library.Id, "", "", null, 0, scanId: scan.Id);
        Assert.Equal(90, additions.Total); Assert.Equal(80, additions.Entries.Count); Assert.All(additions.Entries, e => Assert.False(e.IsFolder));
        Assert.Equal(30, w.Catalog.Browse(library.Id, "", "", null, 0).Total);
    }
    [Fact]
    public async Task RecursiveRescanPreservesTagsAndReportsChanges()
    {
        using var w = new Workspace(); var library = w.Catalog.AddLibrary(w.DirectoryAt("library"));
        w.FileAt("library/α sounds/door.WAV"); string removed = w.FileAt("library/drop.mp3"); w.FileAt("library/ignored.txt");
        var initial = await w.Catalog.ScanAsync(library, null, default); Assert.Equal(2, initial.Added); Assert.Equal(0, initial.Removed);
        var sound = Assert.Single(w.Catalog.Browse(library.Id, "α sounds", "", null, 0).Entries).Sound!;
        var tag = w.Catalog.SaveTag(" Door ", "#aabbcc"); w.Catalog.AssignTag(sound.Id, tag.Id, true);
        File.Delete(removed); w.FileAt("library/new.ogg"); w.FileAt("library/α sounds/door.WAV", "changed data");
        var rescan = await w.Catalog.ScanAsync(library, null, default);
        Assert.Equal(1, rescan.Added); Assert.Equal(1, rescan.Removed); Assert.Equal(1, rescan.Changed);
        Assert.Single(w.Catalog.Tags(sound.Id)); Assert.Contains(rescan.Entries, e => e.Kind == "Removed" && e.Path == "drop.mp3");
        Assert.Single(w.Catalog.Browse(library.Id, "", "", null, 0, scanId: rescan.Id).Entries);
        Assert.Equal(rescan.Entries.Count, w.Catalog.GetScan(rescan.Id).Entries.Count);
        w.FileAt("library/drop.mp3"); var returned = await w.Catalog.ScanAsync(library, null, default); Assert.Equal(1, returned.Added);
    }
    [Fact]
    public async Task CancelAndOfflineScanDoNotRemoveCatalogEntries()
    {
        using var w = new Workspace(); var library = w.Catalog.AddLibrary(w.DirectoryAt("library")); w.FileAt("library/one.wav");
        await w.Catalog.ScanAsync(library, null, default);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Catalog.ScanAsync(library, null, cancel.Token));
        Directory.Move(library.Path, library.Path + "-offline");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => w.Catalog.ScanAsync(library, null, default));
        Assert.Equal(1, w.Catalog.Browse(null, "", "", null, 0).Total);
    }
    [Fact]
    public async Task SearchIsLiteralPagedAndTagFiltered()
    {
        using var w = new Workspace(); var library = w.Catalog.AddLibrary(w.DirectoryAt("library"));
        for (int i = 0; i < 90; i++) w.FileAt($"library/fx_{i:000}.wav"); w.FileAt("library/100%real.wav");
        await w.Catalog.ScanAsync(library, null, default);
        Assert.Equal(80, w.Catalog.Browse(null, "", "fx_", null, 0).Entries.Count);
        Assert.Equal(10, w.Catalog.Browse(null, "", "fx_", null, 80).Entries.Count);
        Assert.Single(w.Catalog.Browse(null, "", "%", null, 0).Entries);
        var sound = w.Catalog.Browse(null, "", "fx_001", null, 0).Entries[0].Sound!;
        var tag = w.Catalog.SaveTag("Impact", "#aabbcc"); w.Catalog.AssignTag(sound.Id, tag.Id, true);
        Assert.Single(w.Catalog.Browse(null, "", "", new[] { tag.Id }, 0).Entries);
        Assert.Throws<ArgumentException>(() => w.Catalog.SaveTag("impact", "#aabbcc"));
        Assert.Throws<ArgumentException>(() => w.Catalog.SaveTag("  ", "#aabbcc"));
        w.Catalog.SaveTag("Hit", "#aabbdd", tag.Id); Assert.Equal("Hit", w.Catalog.Tags(sound.Id)[0].Name);
        w.Catalog.DeleteTag(tag.Id); Assert.Empty(w.Catalog.Tags(sound.Id));
    }
    [Fact]
    public async Task MultipleTagFiltersMatchAllSelectionsWithSearchPagingAndLibraryScope()
    {
        using var w = new Workspace(); var library = w.Catalog.AddLibrary(w.DirectoryAt("library"));
        for (int i = 0; i < 90; i++) w.FileAt($"library/fx_{i:000}.wav");
        await w.Catalog.ScanAsync(library, null, default);
        var sounds = w.Catalog.Browse(library.Id, "", "", null, 0, limit: 100).Entries.Select(e => e.Sound!).ToList();
        var a = w.Catalog.SaveTag("Impact", "#aabbcc"); var b = w.Catalog.SaveTag("Metal", "#ddeeff");
        for (int i = 0; i < sounds.Count; i++) { if (i < 85) w.Catalog.AssignTag(sounds[i].Id, a.Id, true); if (i >= 5 && i < 88) w.Catalog.AssignTag(sounds[i].Id, b.Id, true); }
        long[] selected = { a.Id, b.Id };
        var page = w.Catalog.Browse(library.Id, "", "", selected, 25, limit: 25);
        Assert.Equal(80, page.Total); Assert.Equal(25, page.Entries.Count); Assert.All(page.Entries, e => Assert.Equal(2, e.Tags.Count));
        Assert.Equal(5, w.Catalog.Browse(library.Id, "", "fx_00", selected, 0).Total);
        Assert.Equal(80, w.Catalog.Browse(library.Id, "", "", new[] { a.Id, a.Id, b.Id }, 0).Total);
        Assert.Equal(0, w.Catalog.Browse(library.Id, "", "", new[] { a.Id, long.MaxValue }, 0).Total);
        var other = w.Catalog.AddLibrary(w.DirectoryAt("other")); w.FileAt("other/extra.wav"); await w.Catalog.ScanAsync(other, null, default);
        var extra = w.Catalog.Browse(other.Id, "", "", null, 0).Entries[0].Sound!; w.Catalog.AssignTag(extra.Id, a.Id, true); w.Catalog.AssignTag(extra.Id, b.Id, true);
        Assert.Equal(81, w.Catalog.Browse(null, "", "", selected, 0).Total); Assert.Equal(80, w.Catalog.Browse(library.Id, "", "", selected, 0).Total);
        File.Delete(sounds[5].FullPath); await w.Catalog.ScanAsync(library, null, default);
        Assert.Equal(79, w.Catalog.Browse(library.Id, "", "", selected, 0).Total);
        Assert.Equal(89, w.Catalog.Browse(library.Id, "", "", Array.Empty<long>(), 0).Total);
    }
    [Fact]
    public void LibraryExportDestinationsHandleNestedFoldersAndRejectEscapes()
    {
        using var w = new Workspace(); var library = w.Catalog.AddLibrary(w.DirectoryAt("library")); w.DirectoryAt("library/Level 1/Level 2"); w.DirectoryAt("library-extra");
        Assert.Equal(Path.Combine(library.Path, "Level 1", "Level 2", "Mix.wav"), ExportDestination.InLibrary(library, "Level 1/Level 2", "Mix", false));
        Assert.Equal(Path.Combine(library.Path, "Mix.ogg"), ExportDestination.InLibrary(library, "", "Mix.wav", true));
        Assert.Throws<ArgumentException>(() => ExportDestination.InLibrary(library, "../library-extra", "Mix", false));
        Assert.Throws<ArgumentException>(() => ExportDestination.InLibrary(library, "", "../escape", false));
        Assert.Throws<ArgumentException>(() => ExportDestination.InLibrary(library, "", "", false));
        Assert.Throws<DirectoryNotFoundException>(() => ExportDestination.InLibrary(library, "missing", "Mix", false));
        try { Directory.CreateSymbolicLink(Path.Combine(library.Path, "Outside link"), Path.Combine(w.Root, "library-extra")); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }
        Assert.Throws<ArgumentException>(() => ExportDestination.InLibrary(library, "Outside link", "Mix", false));
    }
    [Fact]
    public void ExportRegistrationAddsUnscannedFoldersAndPreservesExistingTags()
    {
        using var w = new Workspace(); var library = w.Catalog.AddLibrary(w.DirectoryAt("library")); string path = w.FileAt("library/Exports/Nested/Mix.wav");
        var sound = w.Catalog.RegisterExport(library.Id, path); Assert.Equal("Exports/Nested/Mix.wav", sound.RelativePath);
        Assert.Equal("Exports", Assert.Single(w.Catalog.Browse(library.Id, "", "", null, 0).Entries).Name);
        Assert.Single(w.Catalog.Browse(library.Id, "Exports/Nested", "", null, 0).Entries);
        var tag = w.Catalog.SaveTag("Created", "#aabbcc"); w.Catalog.AssignTag(sound.Id, tag.Id, true);
        File.WriteAllText(path, "replacement audio"); var replacement = w.Catalog.RegisterExport(library.Id, path);
        Assert.Equal(sound.Id, replacement.Id); Assert.Single(w.Catalog.Tags(replacement.Id)); Assert.Equal(17, replacement.Size);
        Assert.Throws<ArgumentException>(() => w.Catalog.RegisterExport(library.Id, w.FileAt("outside.wav")));
        Assert.Throws<FileNotFoundException>(() => w.Catalog.RegisterExport(library.Id, Path.Combine(library.Path, "missing.wav")));
        w.Catalog.RemoveLibrary(library.Id); Assert.Throws<InvalidOperationException>(() => w.Catalog.RegisterExport(library.Id, path));
    }
    [Fact]
    public void MixAndSettingsSurviveReopeningAndLibraryRemoval()
    {
        using var w = new Workspace(); var library = w.Catalog.AddLibrary(w.DirectoryAt("library"));
        var mix = new MixProject { Name = "Rain", Tracks = new() { new() { SourcePath = Path.Combine(library.Path, "rain.wav"), TrimStart = .2, TrimEnd = 2, SourceDuration = 3, Start = 1.5, Semitones = -3, Envelope = new() { new(.2, .5f), new(2, 0) } } } };
        w.Catalog.SaveMix(mix); w.Catalog.SetSetting("layout", "wide"); w.Catalog.RemoveLibrary(library.Id);
        var reopened = new Catalog(Path.Combine(w.Root, "catalog.sqlite")); var loaded = reopened.LoadMix(mix.Id)!;
        Assert.Equal("Rain", loaded.Name); Assert.Equal(1.5, loaded.Tracks[0].Start); Assert.Equal(2, loaded.Tracks[0].Envelope.Count); Assert.Equal("wide", reopened.GetSetting("layout")); Assert.Equal(mix.Id.ToString(), reopened.GetSetting("last_mix"));
    }
    [Fact]
    public async Task DirectoryLinksCannotCreateDuplicateTraversal()
    {
        using var w = new Workspace(); var root = w.DirectoryAt("library"); w.FileAt("library/source.wav");
        try { Directory.CreateSymbolicLink(Path.Combine(root, "loop"), root); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; } // Windows may require Developer Mode.
        var library = w.Catalog.AddLibrary(root);
        Assert.Throws<InvalidOperationException>(() => w.Catalog.AddLibrary(Path.Combine(root, "loop")));
        var result = await w.Catalog.ScanAsync(library, null, default); Assert.Equal(1, result.Added); Assert.Contains(result.Entries, e => e.Kind == "Skipped");
    }
    [Fact]
    public void HundredThousandEntryCatalogUsesBoundedResultPages()
    {
        using var w = new Workspace(); var library = w.Catalog.AddLibrary(w.DirectoryAt("library"));
        using (var db = new SqliteConnection($"Data Source={Path.Combine(w.Root, "catalog.sqlite")}"))
        {
            db.Open(); using var tx = db.BeginTransaction(); using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO sounds(library_id,path,path_key,parent,name,size,modified) VALUES($lib,$name,$name,'',$name,1,1)";
            insert.Parameters.AddWithValue("$lib", library.Id); var name = insert.Parameters.Add("$name", SqliteType.Text);
            for (int i = 0; i < 100000; i++) { name.Value = $"impact_{i:000000}.wav"; insert.ExecuteNonQuery(); } tx.Commit();
        }
        var watch = System.Diagnostics.Stopwatch.StartNew(); var result = w.Catalog.Browse(library.Id, "", "impact_099", null, 0);
        Assert.Equal(1000, result.Total); Assert.Equal(80, result.Entries.Count); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Search took {watch.Elapsed}");
    }
}
