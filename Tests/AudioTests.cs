using SoundShelf.Core;
using Xunit;

namespace SoundShelf.Tests;

public class AudioTests
{
    private static string Tools => Environment.GetEnvironmentVariable("SOUNDSHELF_AUDIO_TOOLS") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/ffmpeg", AudioLibrary.RuntimeId));
    internal static void WriteWave(string path, double seconds, double frequency = 440, int sampleRate = 48000)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); int frames = (int)(sampleRate * seconds);
        using var w = new BinaryWriter(File.Create(path)); w.Write("RIFF"u8); w.Write(36 + frames * 4); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(sampleRate); w.Write(sampleRate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(frames * 4);
        for (int i = 0; i < frames; i++) { short v = (short)(Math.Sin(i * 2 * Math.PI * frequency / sampleRate) * 10000); w.Write(v); w.Write(v); }
    }
    private static MixProject Mix(string path, double duration = 1) => new() { MasterGain = 1, Tracks = new() { new() { Clips = new() { new() { SourcePath = path, TrimEnd = duration, SourceDuration = duration } } } } };
    [Fact]
    public void EnvelopeAndHistoryPreserveSourceAnchors()
    {
        var points = new List<EnvelopePoint> { new(0, 0), new(1, 1), new(2, 0) };
        Assert.Equal(.5f, MixRenderer.EnvelopeGain(points, .5)); Assert.Equal(.5f, MixRenderer.EnvelopeGain(points, 1.5));
        var mix = Mix("source"); var history = new MixHistory(); history.Capture(mix); mix.Tracks[0].Clips[0].Semitones = 12; mix.Tracks[0].Clips[0].Envelope = points;
        Assert.Equal(.5, mix.Tracks[0].Duration); var original = history.Undo(mix)!; Assert.Equal(0, original.Tracks[0].Clips[0].Semitones); Assert.Empty(original.Tracks[0].Clips[0].Envelope);
        Assert.Equal(12, history.Redo(original)!.Tracks[0].Clips[0].Semitones);
    }
    [Fact]
    public async Task RendererSchedulesTrimsAndMatchesAcrossBlockSizes()
    {
        using var w = new Workspace(); string path = Path.Combine(w.Root, "test.wav"); WriteWave(path, 1);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools); var source = await audio.LoadAsync(path);
        var mix = Mix(path); var track = mix.Tracks[0].Clips[0]; track.Start = .1; track.TrimStart = .2; track.TrimEnd = .4; track.Envelope = new() { new(.2, .25f), new(.4, .8f) };
        var sources = new Dictionary<string, PcmSource> { [path] = source }; var render = new MixRenderer(mix, sources);
        var whole = new float[48000]; render.Render(0, 24000, whole); Assert.All(whole.Take(9600), sample => Assert.Equal(0, sample));
        Assert.Contains(whole.Skip(9600).Take(19000), sample => Math.Abs(sample) > .01);
        Assert.All(whole.Skip(28800), sample => Assert.Equal(0, sample));
        var chunked = new List<float>(); for (int start = 0; start < 24000; start += 127) { int frames = Math.Min(127, 24000 - start); var block = new float[frames * 2]; render.Render(start, frames, block); chunked.AddRange(block); }
        Assert.Equal(whole, chunked.ToArray());
        mix.Tracks[0].Muted = true; var muted = new float[1000]; new MixRenderer(mix, sources).Render(4800, 500, muted); Assert.All(muted, sample => Assert.Equal(0, sample));
    }
    [Fact]
    public async Task PitchChangesFrequencyAndDurationAndLiveBlocksMatchOffline()
    {
        using var w = new Workspace(); string path = Path.Combine(w.Root, "test.wav"); WriteWave(path, 1);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools); var source = await audio.LoadAsync(path); var mix = Mix(path); mix.Tracks[0].Clips[0].Semitones = 12;
        var sources = new Dictionary<string, PcmSource> { [path] = source }; var expected = new float[48000]; new MixRenderer(mix, sources).Render(0, 24000, expected);
        int rising = 0; for (int i = 2; i < expected.Length; i += 2) if (expected[i - 2] <= 0 && expected[i] > 0) rising++; Assert.InRange(rising, 438, 442);
        using var live = new LiveMixer(mix, sources, 0); var actual = new List<float>(); var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!live.Finished && DateTime.UtcNow < timeout) { if (live.TryRead(out var block)) actual.AddRange(block!.Samples); else await Task.Delay(1); }
        Assert.Equal(expected, actual.ToArray());
    }
    [Fact]
    public async Task CurvedPitchedVolumeMatchesLiveBlocksAndWavExport()
    {
        using var w = new Workspace(); string path = Path.Combine(w.Root, "curves.wav"); WriteWave(path, .2);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools); var source = await audio.LoadAsync(path);
        var mix = Mix(path, .2); var track = mix.Tracks[0].Clips[0]; track.Start = .02; track.Semitones = 12; track.Gain = .6f;
        track.Volume = new() { Mode = VolumeMode.Both, StartGain = .2f, EndGain = 0, InMethod = EasingMethod.easeInOutCirc, OutMethod = EasingMethod.easeOutSine };
        var sources = new Dictionary<string, PcmSource> { [path] = source };
        int frames = (int)Math.Ceiling(mix.Duration * 48000); var expected = new float[frames * 2];
        new MixRenderer(mix, sources).Render(0, frames, expected);
        var baseline = mix.Clone(); baseline.Tracks[0].Clips[0].Gain = 1; baseline.Tracks[0].Clips[0].Volume = new();
        var fullVolume = new float[expected.Length]; new MixRenderer(baseline, sources).Render(0, frames, fullVolume);
        for (int f = 960; f < frames; f++)
        {
            float gain = ClipVolume.Gain(track, (f - 960) * track.Rate / 48000);
            Assert.InRange(Math.Abs(expected[f * 2] - fullVolume[f * 2] * gain), 0, .000001f);
        }
        using var live = new LiveMixer(mix, sources, 0); var actual = new List<float>(); var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!live.Finished && DateTime.UtcNow < timeout) { if (live.TryRead(out var block)) actual.AddRange(block!.Samples); else await Task.Delay(1); }
        Assert.Equal(expected, actual.ToArray());
        string wav = Path.Combine(w.Root, "curved-export.wav"); await audio.ExportAsync(mix, wav, false, null, default);
        var exported = await audio.LoadAsync(wav); Assert.Equal(frames, exported.Frames);
        for (int f = 0; f < frames; f++) Assert.InRange(Math.Abs(expected[f * 2] - exported.Sample(f, 0)), 0, .000001f);
    }
    [Fact]
    public async Task LiveEditsTakeEffectAndMasterProtectsPeaks()
    {
        using var w = new Workspace(); string path = Path.Combine(w.Root, "test.wav"); WriteWave(path, 2);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools); var source = await audio.LoadAsync(path); var mix = Mix(path, 2); mix.Tracks[0].Gain = 8;
        var sources = new Dictionary<string, PcmSource> { [path] = source }; var renderer = new MixRenderer(mix, sources); var data = new float[1024]; renderer.Render(0, 512, data);
        Assert.True(renderer.Clipped); Assert.All(data, value => Assert.InRange(value, -1, 1));
        using var live = new LiveMixer(mix, sources, 0); mix.Tracks[0].Muted = true; live.Update(mix, sources); bool silent = false;
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < timeout && !silent) { if (live.TryRead(out var block)) silent = block!.Samples.All(v => v == 0); else await Task.Delay(1); } Assert.True(silent);
    }
    [Fact]
    public async Task InitiallyMutedSourcesCanBeUnmutedDuringPlayback()
    {
        using var w = new Workspace(); string path = Path.Combine(w.Root, "test.wav"); WriteWave(path, 1);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools); var mix = Mix(path); mix.Tracks[0].Muted = true;
        var sources = await audio.PrepareAsync(mix, includeMuted: true); Assert.Single(sources);
        using var live = new LiveMixer(mix, sources, 0); mix.Tracks[0].Muted = false; live.Update(mix, sources);
        bool audible = false; var timeout = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < timeout && !audible) { if (live.TryRead(out var block)) audible = block!.Samples.Any(v => Math.Abs(v) > .01); else await Task.Delay(1); }
        Assert.True(audible);
    }
    [Fact]
    public async Task SoloPreviewUsesTrackEditsAndLeavesNormalPlaybackAndExportUnchanged()
    {
        using var w = new Workspace(); string path = Path.Combine(w.Root, "solo.wav"); WriteWave(path, .4);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools); var source = await audio.LoadAsync(path);
        var mix = Mix(path, .4); var first = mix.Tracks[0].Clips[0]; first.Gain = .2f; mix.Tracks[0].Muted = true; first.Start = .02; first.TrimStart = .1; first.TrimEnd = .3; first.Semitones = 12;
        first.Volume = new() { Mode = VolumeMode.Both, InMethod = EasingMethod.easeInOutCirc };
        var second = new MixTrack { Clips = new() { new() { SourcePath = path, Gain = .3f, TrimEnd = .4, SourceDuration = .4 } } }; mix.Tracks.Add(second);
        var preview = mix.ForTrackPreview(mix.Tracks[0].Id); Assert.Single(preview.Tracks); Assert.True(mix.Tracks[0].Muted); Assert.False(preview.Tracks[0].Muted);
        var sources = await audio.PrepareAsync(preview); Assert.Single(sources);
        long start = (long)Math.Round(first.Start * 48000); int frames = (int)Math.Ceiling(preview.TimelineDuration * 48000) - (int)start;
        var expected = new float[frames * 2]; new MixRenderer(preview, sources).Render(start, frames, expected);
        Assert.Contains(expected, value => Math.Abs(value) > .01);
        using var live = new LiveMixer(preview, sources, first.Start); var actual = new List<float>(); var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!live.Finished && DateTime.UtcNow < timeout) { if (live.TryRead(out var block)) actual.AddRange(block!.Samples); else await Task.Delay(1); }
        Assert.Equal(expected, actual.ToArray());
        var fullMix = new float[960]; new MixRenderer(mix, sources).Render(0, 480, fullMix);
        for (int f = 0; f < 480; f++) Assert.Equal(source.Sample(f, 0) * .3f, fullMix[f * 2]);
        string output = Path.Combine(w.Root, "full-export.wav"); await audio.ExportAsync(mix, output, false, null, default);
        var exported = await audio.LoadAsync(output); Assert.Equal(19200, exported.Frames);
        for (int f = 0; f < 480; f++) Assert.InRange(Math.Abs(exported.Sample(f, 0) - fullMix[f * 2]), 0, .000001f);
    }
    [Fact]
    public async Task WavOggAndMp3DecodeAndExportsRoundTrip()
    {
        using var w = new Workspace(); string path = Path.Combine(w.Root, "test.wav"); WriteWave(path, .4);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools); var mix = Mix(path, .4); mix.Tracks[0].Clips[0].Start = .1;
        string wav = Path.Combine(w.Root, "mix.wav"), ogg = Path.Combine(w.Root, "mix.ogg"), mp3 = Path.Combine(w.Root, "source.mp3");
        await audio.ExportAsync(mix, wav, false, null, default); await audio.ExportAsync(mix, ogg, true, null, default);
        using (var header = new BinaryReader(File.OpenRead(wav)))
        {
            Assert.Equal("RIFF"u8.ToArray(), header.ReadBytes(4));
            header.ReadUInt32(); // RIFF size
            Assert.Equal("WAVE"u8.ToArray(), header.ReadBytes(4));
            while (true)
            {
                byte[] chunk = header.ReadBytes(4);
                uint size = header.ReadUInt32();
                if (chunk.AsSpan().SequenceEqual("fmt "u8)) break;
                header.BaseStream.Seek((long)size + (size & 1), SeekOrigin.Current);
            }
            // Godot checks the format tag directly, even when FFmpeg can decode the file.
            Assert.Equal((ushort)3, header.ReadUInt16());
        }
        Assert.InRange((await audio.LoadAsync(wav)).Duration, .499, .501); Assert.InRange((await audio.LoadAsync(ogg)).Duration, .49, .51);
        using (var p = AudioLibrary.Start(audio.Ffmpeg, new[] { "-v", "error", "-i", path, mp3 })) { var error = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); Assert.True(p.ExitCode == 0, await error); }
        Assert.InRange((await audio.LoadAsync(mp3)).Duration, .39, .41); Assert.Contains("48000", await audio.ProbeAsync(wav));
        var wavSource = await audio.LoadAsync(wav); var oggSource = await audio.LoadAsync(ogg); double errorPower = 0, power = 0;
        for (long f = 4800; f < Math.Min(wavSource.Frames, oggSource.Frames); f++) { double sample = wavSource.Sample(f, 0), error = sample - oggSource.Sample(f, 0); errorPower += error * error; power += sample * sample; }
        Assert.True(errorPower / power < .02, "OGG round-trip distortion exceeded tolerance.");
        await Assert.ThrowsAsync<IOException>(() => audio.ExportAsync(mix, path, false, null, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); byte[] before = File.ReadAllBytes(wav);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audio.ExportAsync(mix, wav, false, null, cancel.Token)); Assert.Equal(before, File.ReadAllBytes(wav));
    }
    [Fact]
    public async Task SoundCopyPreservesSourceAndRequiresOverwritePermission()
    {
        using var w = new Workspace();
        string source = w.FileAt("library/source.wav", "source audio");
        string destination = w.FileAt("copies/renamed.wav", "previous audio");
        await Assert.ThrowsAsync<IOException>(() => SoundFileCopy.CopyAsync(source, source, true, default));
        await Assert.ThrowsAsync<IOException>(() => SoundFileCopy.CopyAsync(source, destination, false, default));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SoundFileCopy.CopyAsync(source, destination, true, canceled.Token));
        Assert.Equal("previous audio", File.ReadAllText(destination));
        await SoundFileCopy.CopyAsync(source, destination, true, default);
        Assert.Equal("source audio", File.ReadAllText(destination));
        Assert.Equal("source audio", File.ReadAllText(source));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
    }

    [Fact]
    public async Task SoundCopyConvertsWavAndOggInBothDirections()
    {
        using var w = new Workspace();
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools);
        string wav = Path.Combine(w.Root, "source.wav");
        string ogg = Path.Combine(w.Root, "copy.ogg");
        string convertedWav = Path.Combine(w.Root, "converted.wav");
        WriteWave(wav, .4, sampleRate: 384000);
        byte[] original = File.ReadAllBytes(wav);

        await SoundFileCopy.ConvertAsync(wav, ogg, false, audio.Ffmpeg, default);
        Assert.Equal("VORBIS · 48000 Hz · 2 channels", await audio.ProbeAsync(ogg));
        byte[] oggBytes = File.ReadAllBytes(ogg);
        Assert.Equal("OggS"u8.ToArray(), oggBytes[..4]);

        await SoundFileCopy.ConvertAsync(ogg, convertedWav, false, audio.Ffmpeg, default);
        Assert.Equal("PCM_F32LE · 48000 Hz · 2 channels", await audio.ProbeAsync(convertedWav));
        Assert.Equal("WAVE"u8.ToArray(), File.ReadAllBytes(convertedWav)[8..12]);
        Assert.InRange((await audio.LoadAsync(convertedWav)).Duration, .39, .41);
        Assert.Equal(original, File.ReadAllBytes(wav));
        Assert.Equal(oggBytes, File.ReadAllBytes(ogg));

        string invalid = w.FileAt("broken.wav", "not audio");
        await Assert.ThrowsAsync<IOException>(() => SoundFileCopy.ConvertAsync(invalid, ogg, true, audio.Ffmpeg, default));
        Assert.Equal(oggBytes, File.ReadAllBytes(ogg));
        Assert.Empty(Directory.EnumerateFiles(w.Root, "*.tmp"));
    }

    [Fact]
    public async Task ExportToNestedLibraryFolderIsImmediatelySearchable()
    {
        using var w = new Workspace(); var library = w.Catalog.AddLibrary(w.DirectoryAt("library")); w.DirectoryAt("library/Exports/Nested");
        string sourcePath = Path.Combine(w.Root, "source.wav"); WriteWave(sourcePath, .2);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools);
        string target = ExportDestination.InLibrary(library, "Exports/Nested", "New mix", false);
        await audio.ExportAsync(Mix(sourcePath, .2), target, false, null, default); var sound = w.Catalog.RegisterExport(library.Id, target);
        Assert.Equal(sound.Id, Assert.Single(w.Catalog.Browse(library.Id, "", "New mix", null, 0).Entries).Sound!.Id);
        Assert.Equal(target, Assert.Single(w.Catalog.Browse(library.Id, "Exports/Nested", "", null, 0).Entries).Sound!.FullPath);
        Assert.InRange((await audio.LoadAsync(target)).Duration, .199, .201);
    }
    [Fact]
    public async Task MultipleClipsUseIndependentPitchAndVolumeAndMatchLiveAndExport()
    {
        using var w = new Workspace(); string path = Path.Combine(w.Root, "clips.wav"); WriteWave(path, .3);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools); var source = await audio.LoadAsync(path);
        var first = new MixClip { SourcePath = path, SourceDuration = .3, TrimStart = .02, TrimEnd = .12, Start = .01, Gain = .6f, Volume = new() { Mode = VolumeMode.EaseOut } };
        var second = new MixClip { SourcePath = path, SourceDuration = .3, TrimEnd = .2, Start = .16, Semitones = 12, Gain = .3f, Volume = new() { Mode = VolumeMode.EaseIn } };
        var track = new MixTrack { Gain = .4f, Clips = new() { first, second } };
        var mix = new MixProject { MasterGain = .5f, Tracks = new() { track, new() } };
        var sources = await audio.PrepareAsync(mix, includeMuted: true); Assert.Single(sources);
        int frames = (int)Math.Ceiling(mix.Duration * 48000); var whole = new float[frames * 2];
        new MixRenderer(mix, sources).Render(0, frames, whole);
        Assert.All(whole.Take(960), sample => Assert.Equal(0, sample));
        Assert.All(whole.Skip(10560).Take(4800), sample => Assert.Equal(0, sample));
        foreach (var clip in track.Clips)
        {
            var preview = mix.ForClipPreview(track.Id, clip.Id); var isolated = new float[whole.Length];
            new MixRenderer(preview, sources).Render(0, frames, isolated);
            int start = (int)Math.Round(clip.Start * 48000), end = (int)Math.Round(clip.End * 48000);
            Assert.Equal(whole[(start * 2)..(end * 2)], isolated[(start * 2)..(end * 2)]);
        }
        using var live = new LiveMixer(mix, sources, 0); var blocks = new List<float>(); var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!live.Finished && DateTime.UtcNow < timeout) { if (live.TryRead(out var block)) blocks.AddRange(block!.Samples); else await Task.Delay(1); }
        Assert.Equal(whole, blocks.ToArray());
        var unity = mix.Clone(); unity.MasterGain = 1; unity.Tracks[0].Gain = 1;
        var loud = new float[whole.Length]; new MixRenderer(unity, sources).Render(0, frames, loud);
        for (int i = 0; i < whole.Length; i++) Assert.InRange(Math.Abs(whole[i] - loud[i] * .2f), 0, 1e-7f);
        string output = Path.Combine(w.Root, "multi.wav"); await audio.ExportAsync(mix, output, false, null, default);
        var exported = await audio.LoadAsync(output); Assert.Equal(frames, exported.Frames);
        for (int f = 0; f < frames; f++) Assert.InRange(Math.Abs(whole[f * 2] - exported.Sample(f, 0)), 0, 1e-7f);
    }

    [Fact]
    public async Task TouchingFractionalFrameClipsNeverPlaySimultaneously()
    {
        using var w = new Workspace(); string path = Path.Combine(w.Root, "boundary.wav"); WriteWave(path, .1);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools); var source = await audio.LoadAsync(path);
        var first = new MixClip { SourcePath = path, Start = .25 / 48000, TrimEnd = .025, SourceDuration = .1 };
        var second = first.Clone(); second.Id = Guid.NewGuid(); second.Start = first.End;
        var track = new MixTrack { Clips = new() { first, second } }; var mix = new MixProject { MasterGain = 1, Tracks = new() { track } };
        var sources = new Dictionary<string, PcmSource> { [path] = source };
        long boundary = (long)Math.Round(second.Start * 48000);
        var expected = new float[128]; var actual = new float[128];
        new MixRenderer(mix.ForClipPreview(track.Id, second.Id), sources).Render(boundary, 64, expected);
        new MixRenderer(mix, sources).Render(boundary, 64, actual); Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ThirtyTwoTracksRenderFasterThanRealtime()
    {
        using var w = new Workspace(); string path = Path.Combine(w.Root, "test.wav"); WriteWave(path, 1);
        using var audio = new AudioLibrary(Path.Combine(w.Root, "cache"), Tools); var source = await audio.LoadAsync(path); var mix = Mix(path);
        for (int i = 0; i < 31; i++) { var track = mix.Tracks[0].Clone(); track.Clips[0].Semitones = (i % 5) - 2; track.Gain = .02f; mix.Tracks.Add(track); }
        var renderer = new MixRenderer(mix, new Dictionary<string, PcmSource> { [path] = source }); var block = new float[1024];
        renderer.Render(0, 512, block); var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int frame = 0; frame < 48000; frame += 512) renderer.Render(frame, Math.Min(512, 48000 - frame), block);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"32-track render took {watch.Elapsed}");
    }
    [Fact]
    public async Task CreatesOptionalDemonstrationProfile()
    {
        string? root = Environment.GetEnvironmentVariable("SOUNDSHELF_DEMO_DIR"); if (root == null) return;
        Directory.CreateDirectory(root); var catalog = new Catalog(Path.Combine(root, "soundshelf.sqlite")); if (catalog.Libraries().Count > 0) return;
        string libraryRoot = Path.Combine(root, "Demo sound library"); Directory.CreateDirectory(libraryRoot);
        WriteWave(Path.Combine(libraryRoot, "Door latch.wav"), .8, 120); WriteWave(Path.Combine(libraryRoot, "Crystal chime.wav"), 2.2, 660); WriteWave(Path.Combine(libraryRoot, "Forest", "Night ambience.wav"), 5, 90); WriteWave(Path.Combine(libraryRoot, "Sci-fi", "Laser charge.wav"), 1.4, 880);
        var library = catalog.AddLibrary(libraryRoot); await catalog.ScanAsync(library, null, default); var sound = catalog.Browse(library.Id, "", "Crystal", null, 0).Entries[0].Sound!;
        var tag = catalog.SaveTag("Magic", "#a89af5"); catalog.AssignTag(sound.Id, tag.Id, true); var impact = catalog.SaveTag("Impact", "#efb16e"); catalog.AssignTag(sound.Id, impact.Id, true);
        var demoClips = new List<MixClip>
        {
            new() { Name="Door latch.wav",SourcePath=Path.Combine(libraryRoot,"Door latch.wav"), SourceDuration=.8, TrimEnd=.8, Start=.2, Gain=.7f },
            new() { Name="Crystal chime.wav",SourcePath=sound.FullPath, SourceDuration=2.2,TrimEnd=2.2,Start=.7,Semitones=3,Envelope=new(){new(0,1),new(1.8,.2f),new(2.2,0)} },
            new() { Name="Night ambience.wav",SourcePath=Path.Combine(libraryRoot,"Forest","Night ambience.wav"),SourceDuration=5,TrimEnd=5,Gain=.3f }
        }; var mix = new MixProject { Name = "Crystal doorway", Tracks = demoClips.Select(c => new MixTrack { Name = c.Name, Clips = new() { c } }).ToList() }; catalog.SaveMix(mix); catalog.SetSetting("library", library.Id.ToString());
    }
}
