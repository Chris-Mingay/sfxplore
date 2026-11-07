using System.Text.Json;

namespace SoundShelf.Core;

public record Library(long Id, string Name, string Path);
public record Tag(long Id, string Name, string Color);
public record Sound(long Id, long LibraryId, string RelativePath, string FullPath, long Size, long Modified, bool Present);
public record BrowserEntry(string Name, string RelativePath, bool IsFolder, Sound? Sound, List<Tag> Tags);
public record CatalogPage(List<BrowserEntry> Entries, int Total);
public record ScanEntry(string Kind, string Path, string Detail = "");
public record ScanResult(long Id, long LibraryId, int Added, int Removed, int Changed, List<ScanEntry> Entries);
public record MixFolder(long Id, long? ParentId, string Name);
public record ScanProgress(int Files, string Path);
public record EnvelopePoint(double SourceSeconds, float Gain);

public sealed class MixTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long SoundId { get; set; }
    public string SourcePath { get; set; } = "";
    public string Name { get; set; } = "";
    public double Start { get; set; }
    public double TrimStart { get; set; }
    public double TrimEnd { get; set; }
    public double SourceDuration { get; set; }
    public bool Muted { get; set; }
    public float Gain { get; set; } = 1;
    public double Semitones { get; set; }
    public List<EnvelopePoint> Envelope { get; set; } = new();
    // Null preserves the source-anchored envelope in older saved projects.
    public VolumeEasing? Volume { get; set; }
    public double Rate => Math.Pow(2, Semitones / 12);
    public double Duration => Math.Max(0, TrimEnd - TrimStart) / Rate;
    public MixTrack Clone() => JsonSerializer.Deserialize<MixTrack>(JsonSerializer.Serialize(this))!;
}

public sealed class MixProject
{
    public long Id { get; set; }
    public string Name { get; set; } = "Untitled mix";
    public float MasterGain { get; set; } = 0.75f;
    public List<MixTrack> Tracks { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<MixTrack> AudibleTracks
    {
        get => Tracks.Where(t => !t.Muted);
    }
    public double Duration => AudibleTracks.Select(t => t.Start + t.Duration).DefaultIfEmpty(0).Max();
    public double TimelineDuration => Tracks.Select(t => t.Start + t.Duration).DefaultIfEmpty(0).Max();
    public MixProject ForTrackPreview(Guid trackId)
    {
        var track = Tracks.First(t => t.Id == trackId).Clone();
        track.Muted = false;
        return new MixProject { Name = Name, MasterGain = MasterGain, Tracks = new() { track } };
    }
    public MixProject Clone() => JsonSerializer.Deserialize<MixProject>(JsonSerializer.Serialize(this))!;
}

public sealed class MixHistory
{
    private readonly Stack<MixProject> _undo = new(), _redo = new();
    public void Capture(MixProject mix) { _undo.Push(mix.Clone()); _redo.Clear(); }
    public MixProject? Undo(MixProject current) { if (_undo.Count == 0) return null; _redo.Push(current.Clone()); return _undo.Pop(); }
    public MixProject? Redo(MixProject current) { if (_redo.Count == 0) return null; _undo.Push(current.Clone()); return _redo.Pop(); }
    public void Clear() { _undo.Clear(); _redo.Clear(); }
}
