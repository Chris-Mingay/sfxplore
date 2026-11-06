using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace SoundShelf.Core;

public sealed class Catalog
{
    private readonly string _connectionString;
    public Catalog(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 30 }.ToString();
        using var db = Open();
        using (var version = db.CreateCommand()) { version.CommandText = "PRAGMA user_version"; if (Convert.ToInt32(version.ExecuteScalar()) > 1) throw new InvalidOperationException("This database was created by a newer SFXplore version."); }
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS libraries(id INTEGER PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS folders(library_id INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE, path TEXT NOT NULL, parent TEXT NOT NULL, name TEXT NOT NULL, PRIMARY KEY(library_id,path));
            CREATE TABLE IF NOT EXISTS sounds(id INTEGER PRIMARY KEY, library_id INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE, path TEXT NOT NULL, path_key TEXT NOT NULL, parent TEXT NOT NULL, name TEXT NOT NULL, size INTEGER NOT NULL, modified INTEGER NOT NULL, present INTEGER NOT NULL DEFAULT 1, UNIQUE(library_id,path_key));
            CREATE INDEX IF NOT EXISTS sounds_parent ON sounds(library_id,parent,present,name);
            CREATE VIRTUAL TABLE IF NOT EXISTS sound_search USING fts5(name,path,content='sounds',content_rowid='id',tokenize='trigram');
            CREATE TRIGGER IF NOT EXISTS sounds_ai AFTER INSERT ON sounds BEGIN INSERT INTO sound_search(rowid,name,path) VALUES(new.id,new.name,new.path); END;
            CREATE TRIGGER IF NOT EXISTS sounds_ad AFTER DELETE ON sounds BEGIN INSERT INTO sound_search(sound_search,rowid,name,path) VALUES('delete',old.id,old.name,old.path); END;
            CREATE TRIGGER IF NOT EXISTS sounds_au AFTER UPDATE OF name,path ON sounds BEGIN INSERT INTO sound_search(sound_search,rowid,name,path) VALUES('delete',old.id,old.name,old.path); INSERT INTO sound_search(rowid,name,path) VALUES(new.id,new.name,new.path); END;
            CREATE TABLE IF NOT EXISTS tags(id INTEGER PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE, color TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sound_tags(sound_id INTEGER NOT NULL REFERENCES sounds(id) ON DELETE CASCADE,tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,PRIMARY KEY(sound_id,tag_id));
            CREATE INDEX IF NOT EXISTS sound_tags_tag ON sound_tags(tag_id,sound_id);
            CREATE TABLE IF NOT EXISTS scans(id INTEGER PRIMARY KEY, library_id INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE, created TEXT NOT NULL, added INTEGER NOT NULL,removed INTEGER NOT NULL,changed INTEGER NOT NULL,entries TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS mixes(id INTEGER PRIMARY KEY,name TEXT NOT NULL,body TEXT NOT NULL,updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;"; cmd.ExecuteNonQuery();
        return db;
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] parameters)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    public List<Library> Libraries()
    {
        using var db = Open(); using var cmd = Command(db, "SELECT id,name,path FROM libraries ORDER BY name COLLATE NOCASE"); using var reader = cmd.ExecuteReader();
        var result = new List<Library>(); while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))); return result;
    }
    public Library AddLibrary(string path)
    {
        path = Paths.CanonicalDirectory(path);
        foreach (var library in Libraries())
            if (Paths.Overlaps(path, library.Path)) throw new InvalidOperationException($"This folder overlaps the existing library ‘{library.Name}’.\n{library.Path}");
        string name = new DirectoryInfo(path).Name.Length > 0 ? new DirectoryInfo(path).Name : path;
        using var db = Open(); using var cmd = Command(db, "INSERT INTO libraries(name,path) VALUES($name,$path); SELECT last_insert_rowid();", ("$name", name), ("$path", path));
        return new(Convert.ToInt64(cmd.ExecuteScalar()), name, path);
    }
    public void RemoveLibrary(long id) { using var db = Open(); using var cmd = Command(db, "DELETE FROM libraries WHERE id=$id", ("$id", id)); cmd.ExecuteNonQuery(); }
    public string? GetSetting(string key) { using var db = Open(); using var cmd = Command(db, "SELECT value FROM settings WHERE key=$key", ("$key", key)); return cmd.ExecuteScalar() as string; }
    public void SetSetting(string key, string value) { using var db = Open(); using var cmd = Command(db, "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value)); cmd.ExecuteNonQuery(); }

    public List<Tag> Tags(long? soundId = null)
    {
        using var db = Open(); using var cmd = Command(db, soundId.HasValue ? "SELECT t.id,t.name,t.color FROM tags t JOIN sound_tags st ON st.tag_id=t.id WHERE st.sound_id=$id ORDER BY t.name" : "SELECT id,name,color FROM tags ORDER BY name COLLATE NOCASE", ("$id", soundId));
        using var r = cmd.ExecuteReader(); var list = new List<Tag>(); while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2))); return list;
    }
    public Tag SaveTag(string name, string color, long? id = null)
    {
        name = name.Trim(); if (name.Length == 0) throw new ArgumentException("Enter a tag name.");
        if (color.Length != 7 || color[0] != '#' || !int.TryParse(color[1..], System.Globalization.NumberStyles.HexNumber, null, out _)) throw new ArgumentException("Choose a valid color.");
        using var db = Open();
        try
        {
            using var cmd = Command(db, id.HasValue ? "UPDATE tags SET name=$name,color=$color WHERE id=$id; SELECT $id;" : "INSERT INTO tags(name,color) VALUES($name,$color); SELECT last_insert_rowid();", ("$name", name), ("$color", color), ("$id", id));
            return new(Convert.ToInt64(cmd.ExecuteScalar()), name, color);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new ArgumentException("A tag with that name already exists."); }
    }
    public void AssignTag(long soundId, long tagId, bool assigned)
    {
        using var db = Open(); using var cmd = Command(db, assigned ? "INSERT OR IGNORE INTO sound_tags VALUES($sound,$tag)" : "DELETE FROM sound_tags WHERE sound_id=$sound AND tag_id=$tag", ("$sound", soundId), ("$tag", tagId)); cmd.ExecuteNonQuery();
    }
    public void DeleteTag(long id) { using var db = Open(); using var cmd = Command(db, "DELETE FROM tags WHERE id=$id", ("$id", id)); cmd.ExecuteNonQuery(); }

    public CatalogPage Browse(long? libraryId, string folder, string search, IReadOnlyCollection<long>? tagIds, int offset, int limit = 80, long? scanId = null)
    {
        using var db = Open();
        var entries = new List<BrowserEntry>();
        var selectedTags = tagIds?.Distinct().ToArray() ?? Array.Empty<long>();
        bool filter = search.Length > 0 || selectedTags.Length > 0 || scanId.HasValue || !libraryId.HasValue;
        string where = "s.present=1";
        var args = new List<(string, object?)>();
        if (libraryId.HasValue) { where += " AND s.library_id=$lib"; args.Add(("$lib", libraryId.Value)); }
        if (!filter) { where += " AND s.parent=$folder"; args.Add(("$folder", folder)); }
        if (search.Length > 0) { where += " AND s.id IN (SELECT rowid FROM sound_search WHERE path LIKE $search ESCAPE '\\')"; args.Add(("$search", "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%")); }
        if (selectedTags.Length > 0)
        {
            var placeholders = selectedTags.Select((id, index) => "$tag" + index).ToArray();
            where += $" AND s.id IN (SELECT sound_id FROM sound_tags WHERE tag_id IN ({string.Join(',', placeholders)}) GROUP BY sound_id HAVING count(*)=$tagCount)";
            for (int index = 0; index < selectedTags.Length; index++) args.Add((placeholders[index], selectedTags[index]));
            args.Add(("$tagCount", selectedTags.Length));
        }
        if (scanId.HasValue)
        {
            where += " AND s.path IN (SELECT json_extract(value,'$.Path') FROM scans,json_each(scans.entries) WHERE scans.id=$scan AND json_extract(value,'$.Kind')='Added')";
            args.Add(("$scan", scanId));
        }
        int folderCount = 0;
        if (!filter)
        {
            using var fc = Command(db, "SELECT count(*) FROM folders WHERE library_id=$lib AND parent=$folder AND path<>''", ("$lib", libraryId), ("$folder", folder)); folderCount = Convert.ToInt32(fc.ExecuteScalar());
            if (offset < folderCount)
            {
                using var f = Command(db, "SELECT name,path FROM folders WHERE library_id=$lib AND parent=$folder AND path<>'' ORDER BY name COLLATE NOCASE LIMIT $limit OFFSET $offset", ("$lib", libraryId), ("$folder", folder), ("$limit", limit), ("$offset", offset));
                using var fr = f.ExecuteReader(); while (fr.Read()) entries.Add(new(fr.GetString(0), fr.GetString(1), true, null, new()));
            }
        }
        using var count = Command(db, "SELECT count(*) FROM sounds s WHERE " + where, args.ToArray()); int total = Convert.ToInt32(count.ExecuteScalar()) + folderCount;
        if (entries.Count < limit)
        {
            var queryArgs = args.Concat(new (string, object?)[] { ("$limit", limit - entries.Count), ("$offset", Math.Max(0, offset - folderCount)) }).ToArray();
            using var q = Command(db, "SELECT s.id,s.library_id,s.path,l.path,s.size,s.modified,s.name FROM sounds s JOIN libraries l ON l.id=s.library_id WHERE " + where + " ORDER BY s.name COLLATE NOCASE,s.id LIMIT $limit OFFSET $offset", queryArgs);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var sound = new Sound(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Paths.Join(r.GetString(3), r.GetString(2)), r.GetInt64(4), r.GetInt64(5), true);
                entries.Add(new(r.GetString(6), sound.RelativePath, false, sound, new()));
            }
        }
        // One query for the visible page's tags, rather than one connection per tile.
        var byId = entries.Where(e => e.Sound != null).ToDictionary(e => e.Sound!.Id);
        if (byId.Count > 0)
        {
            using var tq = Command(db, $"SELECT st.sound_id,t.id,t.name,t.color FROM sound_tags st JOIN tags t ON t.id=st.tag_id WHERE st.sound_id IN ({string.Join(',', byId.Keys)}) ORDER BY t.name");
            using var tr = tq.ExecuteReader(); while (tr.Read()) byId[tr.GetInt64(0)].Tags.Add(new(tr.GetInt64(1), tr.GetString(2), tr.GetString(3)));
        }
        return new(entries, total);
    }

    public Task<ScanResult> ScanAsync(Library library, IProgress<ScanProgress>? progress, CancellationToken cancel) => Task.Run(() => Scan(library, progress, cancel), cancel);
    private ScanResult Scan(Library library, IProgress<ScanProgress>? progress, CancellationToken cancel)
    {
        if (!Directory.Exists(library.Path)) throw new DirectoryNotFoundException("Library unavailable; its catalog has been preserved.");
        var found = new Dictionary<string, (string Path, long Size, long Modified)>();
        var folders = new List<string>(); var entries = new List<ScanEntry>(); var failed = new List<string>();
        void Walk(string directory)
        {
            cancel.ThrowIfCancellationRequested();
            string rel = Paths.Relative(library.Path, directory); if (rel == ".") rel = "";
            try
            {
                var children = new DirectoryInfo(directory).GetFileSystemInfos();
                folders.Add(rel);
                foreach (var child in children)
                {
                    cancel.ThrowIfCancellationRequested();
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) { entries.Add(new("Skipped", Paths.Relative(library.Path, child.FullName), "Symbolic link or junction")); continue; }
                    if ((child.Attributes & FileAttributes.Directory) != 0) { Walk(child.FullName); continue; }
                    if (!new[] { ".wav", ".ogg", ".mp3" }.Contains(child.Extension, StringComparer.OrdinalIgnoreCase)) continue;
                    var file = (FileInfo)child; var path = Paths.Relative(library.Path, child.FullName);
                    found[Key(path)] = (path, file.Length, file.LastWriteTimeUtc.Ticks);
                    if (found.Count % 100 == 0) progress?.Report(new(found.Count, path));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { failed.Add(rel); entries.Add(new("Error", rel, e.Message)); }
        }
        Walk(library.Path); cancel.ThrowIfCancellationRequested();
        if (failed.Contains("")) throw new IOException("Cannot read the library folder; its catalog has been preserved.");
        using var db = Open(); using var tx = db.BeginTransaction();
        SqliteCommand Tx(string sql, params (string, object?)[] values) { var c = Command(db, sql, values); c.Transaction = tx; return c; }
        var old = new Dictionary<string, (long Id, long Size, long Modified, bool Present, string Path)>();
        using (var q = Tx("SELECT id,path_key,size,modified,present,path FROM sounds WHERE library_id=$lib", ("$lib", library.Id)))
        using (var r = q.ExecuteReader()) while (r.Read()) old[r.GetString(1)] = (r.GetInt64(0), r.GetInt64(2), r.GetInt64(3), r.GetBoolean(4), r.GetString(5));
        int added = 0, removed = 0, changed = 0;
        using var upsert = Tx("INSERT INTO sounds(library_id,path,path_key,parent,name,size,modified,present) VALUES($lib,$path,$key,$parent,$name,$size,$modified,1) ON CONFLICT(library_id,path_key) DO UPDATE SET path=excluded.path,parent=excluded.parent,name=excluded.name,size=excluded.size,modified=excluded.modified,present=1");
        foreach (string key in new[] { "$lib", "$path", "$key", "$parent", "$name", "$size", "$modified" }) upsert.Parameters.Add(new SqliteParameter(key, null));
        foreach (var (key, file) in found)
        {
            cancel.ThrowIfCancellationRequested();
            if (!old.TryGetValue(key, out var previous) || !previous.Present) { added++; entries.Add(new("Added", file.Path)); }
            else if (file.Size != previous.Size || file.Modified != previous.Modified) { changed++; entries.Add(new("Changed", file.Path)); }
            else continue;
            upsert.Parameters["$lib"].Value = library.Id; upsert.Parameters["$path"].Value = file.Path; upsert.Parameters["$key"].Value = key;
            upsert.Parameters["$parent"].Value = Parent(file.Path); upsert.Parameters["$name"].Value = Path.GetFileName(file.Path);
            upsert.Parameters["$size"].Value = file.Size; upsert.Parameters["$modified"].Value = file.Modified; upsert.ExecuteNonQuery();
        }
        foreach (var (key, previous) in old)
        {
            if (!previous.Present || found.ContainsKey(key) || failed.Any(f => f.Length == 0 || previous.Path == f || previous.Path.StartsWith(f + '/', StringComparison.Ordinal))) continue;
            cancel.ThrowIfCancellationRequested();
            using var missing = Tx("UPDATE sounds SET present=0 WHERE id=$id", ("$id", previous.Id)); missing.ExecuteNonQuery(); removed++; entries.Add(new("Removed", previous.Path));
        }
        var previousFolders = new List<string>();
        using (var q = Tx("SELECT path FROM folders WHERE library_id=$lib", ("$lib", library.Id)))
        using (var r = q.ExecuteReader()) while (r.Read()) previousFolders.Add(r.GetString(0));
        var folderSet = folders.ToHashSet();
        foreach (var folder in previousFolders)
        {
            if (folderSet.Contains(folder) || failed.Any(f => folder == f || folder.StartsWith(f + '/', StringComparison.Ordinal))) continue;
            using var delete = Tx("DELETE FROM folders WHERE library_id=$lib AND path=$path", ("$lib", library.Id), ("$path", folder)); delete.ExecuteNonQuery();
        }
        foreach (var folder in folders)
        {
            using var f = Tx("INSERT OR IGNORE INTO folders VALUES($lib,$path,$parent,$name)", ("$lib", library.Id), ("$path", folder), ("$parent", Parent(folder)), ("$name", Path.GetFileName(folder))); f.ExecuteNonQuery();
        }
        using var scan = Tx("INSERT INTO scans(library_id,created,added,removed,changed,entries) VALUES($lib,$now,$added,$removed,$changed,$entries); SELECT last_insert_rowid();", ("$lib", library.Id), ("$now", DateTime.UtcNow.ToString("O")), ("$added", added), ("$removed", removed), ("$changed", changed), ("$entries", JsonSerializer.Serialize(entries)));
        long id = Convert.ToInt64(scan.ExecuteScalar()); cancel.ThrowIfCancellationRequested(); tx.Commit(); progress?.Report(new(found.Count, "Complete"));
        return new(id, library.Id, added, removed, changed, entries);
    }
    private static string Key(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    private static string Parent(string path) { int i = path.LastIndexOf('/'); return i < 0 ? "" : path[..i]; }
    public Sound RegisterExport(long libraryId, string path)
    {
        var library = Libraries().FirstOrDefault(l => l.Id == libraryId) ?? throw new InvalidOperationException("The destination library has been removed.");
        string root = Paths.CanonicalDirectory(library.Path);
        string directory = Paths.CanonicalDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!Paths.Within(directory, root)) throw new ArgumentException("The exported file is outside the selected library.");
        var file = new FileInfo(Path.Combine(directory, Path.GetFileName(path)));
        if (!file.Exists) throw new FileNotFoundException("The exported file was not found.", path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("A library export cannot be registered through a symbolic link.");
        if (!new[] { ".wav", ".ogg" }.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Library exports must be WAV or OGG files.");
        string relative = Paths.Relative(root, file.FullName), parent = Parent(relative);
        using var db = Open(); using var tx = db.BeginTransaction();
        string folder = parent;
        while (true)
        {
            using var f = Command(db, "INSERT OR IGNORE INTO folders VALUES($lib,$path,$parent,$name)", ("$lib", libraryId), ("$path", folder), ("$parent", Parent(folder)), ("$name", Path.GetFileName(folder))); f.Transaction = tx; f.ExecuteNonQuery();
            if (folder.Length == 0) break; folder = Parent(folder);
        }
        using var insert = Command(db, "INSERT INTO sounds(library_id,path,path_key,parent,name,size,modified,present) VALUES($lib,$path,$key,$parent,$name,$size,$modified,1) ON CONFLICT(library_id,path_key) DO UPDATE SET path=excluded.path,parent=excluded.parent,name=excluded.name,size=excluded.size,modified=excluded.modified,present=1; SELECT id FROM sounds WHERE library_id=$lib AND path_key=$key", ("$lib", libraryId), ("$path", relative), ("$key", Key(relative)), ("$parent", parent), ("$name", file.Name), ("$size", file.Length), ("$modified", file.LastWriteTimeUtc.Ticks));
        insert.Transaction = tx; long id = Convert.ToInt64(insert.ExecuteScalar()); tx.Commit();
        return new(id, libraryId, relative, file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, true);
    }
    public List<(long Id, string Description)> ScanHistory(long libraryId)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT id,created,added,removed FROM scans WHERE library_id=$id ORDER BY id DESC LIMIT 30", ("$id", libraryId)); using var r = cmd.ExecuteReader();
        var result = new List<(long, string)>(); while (r.Read()) result.Add((r.GetInt64(0), $"{DateTime.Parse(r.GetString(1)).ToLocalTime():g}   +{r.GetInt32(2)} / −{r.GetInt32(3)}")); return result;
    }
    public ScanResult GetScan(long id)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT library_id,added,removed,changed,entries FROM scans WHERE id=$id", ("$id", id)); using var r = cmd.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("Scan not found.");
        return new(id, r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), JsonSerializer.Deserialize<List<ScanEntry>>(r.GetString(4))!);
    }
    public List<(long Id, string Name)> Mixes()
    {
        using var db = Open(); using var cmd = Command(db, "SELECT id,name FROM mixes ORDER BY updated DESC"); using var r = cmd.ExecuteReader(); var list = new List<(long, string)>(); while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1))); return list;
    }
    public void SaveMix(MixProject mix, bool makeActive = true)
    {
        using var db = Open(); using var cmd = Command(db, mix.Id == 0 ? "INSERT INTO mixes(name,body,updated) VALUES($name,$body,$now); SELECT last_insert_rowid();" : "UPDATE mixes SET name=$name,body=$body,updated=$now WHERE id=$id; SELECT $id;", ("$id", mix.Id), ("$name", mix.Name), ("$body", JsonSerializer.Serialize(mix)), ("$now", DateTime.UtcNow.ToString("O")));
        mix.Id = Convert.ToInt64(cmd.ExecuteScalar()); if (makeActive) SetSetting("last_mix", mix.Id.ToString());
    }
    public MixProject? LoadMix(long id)
    {
        using var db = Open(); using var cmd = Command(db, "SELECT body FROM mixes WHERE id=$id", ("$id", id)); var body = cmd.ExecuteScalar() as string; if (body == null) return null;
        var mix = JsonSerializer.Deserialize<MixProject>(body)!; mix.Id = id; return mix;
    }
    public void DeleteMix(long id) { using var db = Open(); using var cmd = Command(db, "DELETE FROM mixes WHERE id=$id", ("$id", id)); cmd.ExecuteNonQuery(); }
}
