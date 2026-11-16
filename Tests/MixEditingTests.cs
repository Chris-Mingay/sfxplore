using System.Text.Json;
using SoundShelf.Core;
using Xunit;

namespace SoundShelf.Tests;

public class MixEditingTests
{
    private static MixClip Clip(double start, double end, double pitch = 0) => new()
    {
        Start = start, TrimStart = 2, TrimEnd = 2 + (end - start) * Math.Pow(2, pitch / 12),
        SourceDuration = 100, Semitones = pitch, SourcePath = "source.wav", Volume = new() { Mode = VolumeMode.Both }
    };

    [Theory]
    [InlineData(0, 10, 3, 5, 0, 3)] // Middle: discard the later portion.
    [InlineData(5, 10, 3, 7, 7, 10)] // Leading overlap: keep the tail.
    [InlineData(0, 5, 3, 7, 0, 3)]
    [InlineData(0, 3, 3, 7, 0, 3)] // Touching edges do not overlap.
    [InlineData(7, 10, 3, 7, 7, 10)]
    public void OverlapPreservesTheChosenSideAndPitchedSourceMapping(double a, double b, double start, double end, double remainingStart, double remainingEnd)
    {
        var victim = Clip(a, b, 12); var edited = Clip(start, end);
        var track = new MixTrack { Clips = new() { victim } }; var mix = new MixProject { Tracks = new() { track } };
        MixEditing.Place(mix, track.Id, edited);
        Assert.Equal(remainingStart, victim.Start, 8); Assert.Equal(remainingEnd, victim.End, 8);
        Assert.Equal(2 + (remainingStart - a) * 2, victim.TrimStart, 8);
        Assert.Equal(2 + (remainingEnd - a) * 2, victim.TrimEnd, 8);
        Assert.Equal(start, edited.Start); Assert.Equal(end, edited.End);
        AssertNonOverlapping(track);
    }

    [Fact]
    public void PlacementRemovesCoveredClipsAndResolvesMultipleVictims()
    {
        var a = Clip(0, 4); var b = Clip(4, 6); var c = Clip(6, 10); var untouched = Clip(12, 13);
        var track = new MixTrack { Clips = new() { a, b, c, untouched } }; var mix = new MixProject { Tracks = new() { track } };
        var edited = Clip(3, 8); MixEditing.Place(mix, track.Id, edited);
        Assert.Equal(4, track.Clips.Count); Assert.DoesNotContain(b, track.Clips);
        Assert.Equal(3, a.End); Assert.Equal(8, c.Start); Assert.Equal(10, c.End);
        Assert.Equal(12, untouched.Start); AssertNonOverlapping(track);
    }

    [Fact]
    public void CrossTrackMoveAndNewTrackAreOneHistoryOperation()
    {
        var moving = Clip(0, 2); var victim = Clip(0, 4);
        var from = new MixTrack { Gain = .5f, Clips = new() { moving } };
        var to = new MixTrack { Gain = .25f, Clips = new() { victim } };
        var mix = new MixProject { Tracks = new() { from, to } }; var history = new MixHistory();
        string original = JsonSerializer.Serialize(mix); history.Capture(mix);
        var candidate = moving.Clone(); candidate.Start = 1; MixEditing.Place(mix, to.Id, candidate);
        Assert.Empty(from.Clips); Assert.Equal(1, victim.End); Assert.Equal(moving.Id, candidate.Id);
        string placed = JsonSerializer.Serialize(mix);
        var undone = history.Undo(mix)!; Assert.Equal(original, JsonSerializer.Serialize(undone));
        var redone = history.Redo(undone)!; Assert.Equal(placed, JsonSerializer.Serialize(redone));
        history.Capture(redone); var newTrack = new MixTrack(); redone.Tracks.Insert(0, newTrack);
        MixEditing.Place(redone, newTrack.Id, redone.Clips.First(c => c.Id == moving.Id).Clone());
        Assert.Equal(2, history.Undo(redone)!.Tracks.Count);
    }

    [Fact]
    public void ExtendingOrPitchingAClipUsesTheSameOverlapRules()
    {
        var edited = Clip(0, 2); var next = Clip(3, 7);
        var track = new MixTrack { Clips = new() { edited, next } }; var mix = new MixProject { Tracks = new() { track } };
        var candidate = edited.Clone(); candidate.Semitones = -12; MixEditing.Place(mix, track.Id, candidate);
        Assert.Equal(4, candidate.End); Assert.Equal(4, next.Start); Assert.Equal(7, next.End);
        AssertNonOverlapping(track);
        candidate = candidate.Clone(); candidate.TrimEnd += 4; MixEditing.Place(mix, track.Id, candidate);
        Assert.Single(track.Clips); Assert.Equal(12, candidate.End);
    }

    [Fact]
    public void MigratesActualLegacyJsonWithoutChangingSettingsOrDoubleApplyingGain()
    {
        const string json = """
            {"Id":12,"Name":"Old mix","MasterGain":0.6,"Tracks":[
              {"Id":"ca8af6d3-9ba2-40f7-86c8-56875bd6f094","Name":"Rain","SoundId":9,"SourcePath":"rain.wav",
               "Start":1.5,"TrimStart":2,"TrimEnd":8,"SourceDuration":12,"Muted":true,"Gain":0.4,"Semitones":12,
               "Envelope":[{"SourceSeconds":2,"Gain":0},{"SourceSeconds":8,"Gain":1}]},
              {"Name":"Wind","SourcePath":"wind.wav","TrimEnd":3,"Gain":0.8,"Volume":{"Mode":3,"InEnd":0.25,"OutStart":0.75}}]}
            """;
        var mix = JsonSerializer.Deserialize<MixProject>(json)!;
        Assert.Equal(12, mix.Id); Assert.Equal(.6f, mix.MasterGain);
        var track = mix.Tracks[0]; var clip = Assert.Single(track.Clips);
        Assert.True(track.Muted); Assert.Equal(1, track.Gain); Assert.Equal(.4f, clip.Gain);
        Assert.Equal(9, clip.SoundId); Assert.Equal("rain.wav", clip.SourcePath);
        Assert.Equal(1.5, clip.Start); Assert.Equal(3, clip.Duration); Assert.Equal(.2f, ClipVolume.Gain(clip, 5));
        Assert.Null(clip.Volume); Assert.Equal(2, clip.Envelope.Count);
        Assert.Equal(VolumeMode.Both, mix.Tracks[1].Clips[0].Volume!.Mode);
        string updated = JsonSerializer.Serialize(mix);
        Assert.Equal(updated, JsonSerializer.Serialize(mix.Clone()));
        using var w = new Workspace(); mix.Id = 0; w.Catalog.SaveMix(mix);
        Assert.Equal(JsonSerializer.Serialize(mix), JsonSerializer.Serialize(w.Catalog.LoadMix(mix.Id)));
    }

    [Fact]
    public void EmptyTracksStayEmptyAcrossSerializationAndAuditionCopiesAreIndependent()
    {
        var clip = Clip(1, 3); var track = new MixTrack { Gain = .5f, Muted = true, Clips = new() { clip, Clip(4, 6) } };
        var mix = new MixProject { Tracks = new() { track, new() } };
        Assert.Empty(mix.Clone().Tracks[1].Clips);
        var preview = mix.ForClipPreview(track.Id, clip.Id);
        Assert.Single(preview.Clips); Assert.False(preview.Tracks[0].Muted); Assert.True(track.Muted);
        Assert.Equal(.5f, preview.Tracks[0].Gain); preview.Tracks[0].Clips[0].Volume!.StartGain = .7f;
        Assert.Equal(0, clip.Volume!.StartGain);
    }

    private static void AssertNonOverlapping(MixTrack track)
    {
        var clips = track.Clips.OrderBy(c => c.Start).ToArray();
        for (int i = 1; i < clips.Length; i++) Assert.True(clips[i - 1].End <= clips[i].Start + 1e-9);
    }
}
