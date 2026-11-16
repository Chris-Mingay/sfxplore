using System.Text.Json;
using System.Text.Json.Serialization;

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

public sealed class MixClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long SoundId { get; set; }
    public string SourcePath { get; set; } = "";
    public string Name { get; set; } = "";
    public double Start { get; set; }
    public double TrimStart { get; set; }
    public double TrimEnd { get; set; }
    public double SourceDuration { get; set; }
    public float Gain { get; set; } = 1;
    public double Semitones { get; set; }
    public List<EnvelopePoint> Envelope { get; set; } = new();
    // Null preserves the source-anchored envelope in older saved projects.
    public VolumeEasing? Volume { get; set; }
    [JsonIgnore] public double Rate => Math.Pow(2, Semitones / 12);
    [JsonIgnore] public double Duration => Math.Max(0, TrimEnd - TrimStart) / Rate;
    [JsonIgnore] public double End => Start + Duration;
    public MixClip Clone() => JsonSerializer.Deserialize<MixClip>(JsonSerializer.Serialize(this))!;
}

[JsonConverter(typeof(MixTrackConverter))]
public sealed class MixTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Track";
    public bool Muted { get; set; }
    public float Gain { get; set; } = 1;
    public List<MixClip> Clips { get; set; } = new();
    [JsonIgnore] public double Start => Clips.Select(c => c.Start).DefaultIfEmpty(0).Min();
    [JsonIgnore] public double End => Clips.Select(c => c.End).DefaultIfEmpty(0).Max();
    [JsonIgnore] public double Duration => End - Start;
    public MixTrack Clone() => JsonSerializer.Deserialize<MixTrack>(JsonSerializer.Serialize(this))!;
}

// Read the original one-sound-per-track format, but only write the new format.
public sealed class MixTrackConverter : JsonConverter<MixTrack>
{
    public override MixTrack Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var track = new MixTrack();
        if (root.TryGetProperty("Id", out var id)) track.Id = id.GetGuid();
        if (root.TryGetProperty("Name", out var name)) track.Name = name.GetString() ?? "Track";
        if (root.TryGetProperty("Muted", out var muted)) track.Muted = muted.GetBoolean();
        if (root.TryGetProperty("Clips", out var clips))
        {
            track.Clips = clips.Deserialize<List<MixClip>>(options) ?? new();
            if (root.TryGetProperty("Gain", out var gain)) track.Gain = gain.GetSingle();
        }
        else
        {
            track.Clips.Add(root.Deserialize<MixClip>(options)!);
        }
        return track;
    }

    public override void Write(Utf8JsonWriter writer, MixTrack value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Id", value.Id);
        writer.WriteString("Name", value.Name);
        writer.WriteBoolean("Muted", value.Muted);
        writer.WriteNumber("Gain", value.Gain);
        writer.WritePropertyName("Clips");
        JsonSerializer.Serialize(writer, value.Clips, options);
        writer.WriteEndObject();
    }
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
    [JsonIgnore] public IEnumerable<MixClip> Clips => Tracks.SelectMany(t => t.Clips);
    [JsonIgnore] public IEnumerable<MixClip> AudibleClips => AudibleTracks.SelectMany(t => t.Clips);
    [JsonIgnore] public double Duration => AudibleTracks.Select(t => t.End).DefaultIfEmpty(0).Max();
    [JsonIgnore] public double TimelineDuration => Tracks.Select(t => t.End).DefaultIfEmpty(0).Max();
    public MixProject ForTrackPreview(Guid trackId)
    {
        var track = Tracks.First(t => t.Id == trackId).Clone();
        track.Muted = false;
        return new MixProject { Name = Name, MasterGain = MasterGain, Tracks = new() { track } };
    }
    public MixProject Clone() => JsonSerializer.Deserialize<MixProject>(JsonSerializer.Serialize(this))!;
    public MixProject ForClipPreview(Guid trackId, Guid clipId)
    {
        var preview = ForTrackPreview(trackId);
        preview.Tracks[0].Clips = new() { preview.Tracks[0].Clips.First(c => c.Id == clipId) };
        return preview;
    }
}

public sealed class MixHistory
{
    private readonly Stack<MixProject> _undo = new(), _redo = new();
    public void Capture(MixProject mix) { _undo.Push(mix.Clone()); _redo.Clear(); }
    public MixProject? Undo(MixProject current) { if (_undo.Count == 0) return null; _redo.Push(current.Clone()); return _undo.Pop(); }
    public MixProject? Redo(MixProject current) { if (_redo.Count == 0) return null; _undo.Push(current.Clone()); return _redo.Pop(); }
    public void Clear() { _undo.Clear(); _redo.Clear(); }
}
