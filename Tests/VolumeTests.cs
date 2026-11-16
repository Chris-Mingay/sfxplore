using System.Text.Json;
using SoundShelf.Core;
using Xunit;

namespace SoundShelf.Tests;

public class VolumeTests
{
    [Fact]
    public void SoloPreviewIsTemporaryAndOldToggleFlagsAreIgnored()
    {
        using var w = new Workspace(); var mix = new MixProject { Tracks = new() { new() { Clips = new() { Clip(VolumeMode.Both) } }, new() { Clips = new() { Clip(VolumeMode.None) } } } };
        mix.Tracks[0].Muted = true; string original = JsonSerializer.Serialize(mix);
        var preview = mix.ForTrackPreview(mix.Tracks[0].Id);
        Assert.Single(preview.Tracks); Assert.False(preview.Tracks[0].Muted); Assert.Equal(original, JsonSerializer.Serialize(mix));
        preview.Tracks[0].Clips[0].Volume!.StartGain = .123f; Assert.NotEqual(preview.Tracks[0].Clips[0].Volume!.StartGain, mix.Tracks[0].Clips[0].Volume!.StartGain);
        w.Catalog.SaveMix(mix); Assert.Equal(original.Replace("\"Id\":0", $"\"Id\":{mix.Id}"), JsonSerializer.Serialize(w.Catalog.LoadMix(mix.Id)));
        var legacy = JsonSerializer.Deserialize<MixProject>("{\"Tracks\":[{\"Solo\":true,\"TrimEnd\":1},{\"TrimEnd\":2}]}")!;
        Assert.Equal(2, legacy.AudibleTracks.Count()); Assert.DoesNotContain("\"Solo\"", JsonSerializer.Serialize(legacy));
    }

    private static MixClip Clip(VolumeMode mode) => new() { TrimStart = 2, TrimEnd = 12, SourceDuration = 15, Gain = .8f, Volume = new() { Mode = mode, StartGain = .2f, EndGain = .4f } };

    [Theory]
    [InlineData(VolumeMode.None, .8, .8)]
    [InlineData(VolumeMode.EaseIn, .5, .8)]
    [InlineData(VolumeMode.EaseOut, .8, .6)]
    [InlineData(VolumeMode.Both, .5, .6)]
    public void ModesHaveExpectedRampsAndSharedMiddle(VolumeMode mode, double first, double last)
    {
        var t = Clip(mode);
        Assert.Equal(first, ClipVolume.Gain(t, 3), 5);
        Assert.Equal(.8, ClipVolume.Gain(t, 7), 5);
        Assert.Equal(last, ClipVolume.Gain(t, 11), 5);
        t.Envelope = new() { new(2, .01f) }; // Structured volume is a complete gain.
        Assert.Equal(.8, ClipVolume.Gain(t, 7), 5);
        t.Gain = .3f;
        Assert.Equal(.3, ClipVolume.Gain(t, 4), 5);
        Assert.Equal(.3, ClipVolume.Gain(t, 10), 5);
    }

    [Theory]
    [InlineData(EasingMethod.Linear, .25)]
    [InlineData(EasingMethod.easeInQuad, .0625)]
    [InlineData(EasingMethod.easeOutQuad, .4375)]
    [InlineData(EasingMethod.easeInOutCubic, .0625)]
    [InlineData(EasingMethod.easeInOutCirc, .0669872981)]
    public void CurvesMatchKnownQuarterPoints(EasingMethod method, double expected) => Assert.Equal(expected, ClipVolume.Ease(method, .25), 8);

    [Fact]
    public void EveryCurveIsBoundedMonotonicAndHasExactEndpoints()
    {
        foreach (var method in Enum.GetValues<EasingMethod>())
        {
            Assert.Equal(0, ClipVolume.Ease(method, -1)); Assert.Equal(1, ClipVolume.Ease(method, 2));
            double previous = 0;
            for (int i = 0; i <= 100; i++) { double value = ClipVolume.Ease(method, i / 100.0); Assert.InRange(value, previous, 1); previous = value; }
        }
    }

    [Fact]
    public void FadeProportionsSurvivePitchTrimAndZeroDuration()
    {
        var t = Clip(VolumeMode.Both); float gain = ClipVolume.Gain(t, 3);
        t.Semitones = 24; Assert.Equal(2.5, t.Duration); Assert.Equal(gain, ClipVolume.Gain(t, 3));
        t.Semitones = -24; Assert.Equal(40, t.Duration);
        t.TrimStart = 4; t.TrimEnd = 9; Assert.Equal(gain, ClipVolume.Gain(t, 4.5));
        t.Volume!.InEnd = 0; t.Volume.OutStart = 1;
        Assert.Equal(t.Gain, ClipVolume.Gain(t, 4)); Assert.Equal(t.Gain, ClipVolume.Gain(t, 9));
        t.Volume.InEnd = t.Volume.OutStart = .5;
        Assert.Equal(t.Gain, ClipVolume.Gain(t, 6.5));
        t.TrimEnd = t.TrimStart; Assert.True(float.IsFinite(ClipVolume.Gain(t, t.TrimStart)));
        Assert.Equal(0, ClipVolume.FromDecibels(-60)); Assert.Equal(1, ClipVolume.FromDecibels(0));
        Assert.Equal(ClipVolume.MaximumGain, ClipVolume.FromDecibels(12));
    }

    [Fact]
    public void LegacyAudioAndStructuredSettingsSurvivePersistenceAndHistory()
    {
        using var w = new Workspace();
        var legacyTrack = JsonSerializer.Deserialize<MixTrack>("{\"Gain\":0.5,\"TrimEnd\":2,\"Envelope\":[{\"SourceSeconds\":0,\"Gain\":0},{\"SourceSeconds\":2,\"Gain\":1}]}")!;
        var legacy = legacyTrack.Clips[0];
        Assert.Null(legacy.Volume); Assert.Equal(.25f, ClipVolume.Gain(legacy, 1));
        var mix = new MixProject { Tracks = new() { legacyTrack, new() { Clips = new() { Clip(VolumeMode.Both) } } } }; var history = new MixHistory();
        history.Capture(mix); legacy.Volume = new() { Mode = VolumeMode.None };
        Assert.Equal(.5f, ClipVolume.Gain(legacy, 1)); Assert.Null(history.Undo(mix)!.Tracks[0].Clips[0].Volume);
        Assert.NotNull(history.Redo(mix)!.Tracks[0].Clips[0].Volume);
        var v = mix.Tracks[1].Clips[0].Volume!; v.InEnd = .9; v.OutStart = .95; v.InMethod = EasingMethod.easeInOutCirc; v.OutMethod = EasingMethod.easeOutSine;
        w.Catalog.SaveMix(mix); var saved = w.Catalog.LoadMix(mix.Id)!; var clone = saved.Clone();
        Assert.Equal(JsonSerializer.Serialize(mix), JsonSerializer.Serialize(clone));
        clone.Tracks[1].Clips[0].Volume!.StartGain = .123f; Assert.NotEqual(v.StartGain, clone.Tracks[1].Clips[0].Volume!.StartGain);
        v.Mode = VolumeMode.None; v.Mode = VolumeMode.Both; Assert.Equal(.9, v.InEnd); Assert.Equal(.95, v.OutStart);
    }
}
