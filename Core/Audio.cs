using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoundShelf.Core;

public sealed unsafe class PcmSource : IDisposable
{
    public const int SampleRate = 48000;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private readonly string _path;
    private readonly object _peakLock = new();
    public long Frames { get; }
    public double Duration => Frames / (double)SampleRate;
    public PcmSource(string path)
    {
        _path = path; Frames = new FileInfo(path).Length / 8;
        if (Frames == 0) throw new InvalidDataException("The sound contains no audio frames.");
        _map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read); _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer); _pointer += _view.PointerOffset;
    }
    public float Sample(long frame, int channel) => frame < 0 || frame >= Frames ? 0 : ((float*)_pointer)[frame * 2 + channel];
    public float[] Peaks(int buckets = 1024)
    {
        lock (_peakLock)
        {
        string cache = _path + $".peaks-{buckets}";
        if (File.Exists(cache)) { var bytes = File.ReadAllBytes(cache); if (bytes.Length == buckets * 4) { var cached = new float[buckets]; Buffer.BlockCopy(bytes, 0, cached, 0, bytes.Length); return cached; } }
        var result = new float[buckets];
        // At most 256 samples per bucket, independent of source length.
        for (int i = 0; i < buckets; i++)
        {
            long first = Frames * i / buckets, last = Frames * (i + 1) / buckets;
            long stride = Math.Max(1, (last - first) / 256);
            for (long f = first; f < last; f += stride) result[i] = Math.Max(result[i], Math.Max(Math.Abs(Sample(f, 0)), Math.Abs(Sample(f, 1))));
        }
        try { var bytes = new byte[buckets * 4]; Buffer.BlockCopy(result, 0, bytes, 0, bytes.Length); File.WriteAllBytes(cache, bytes); } catch (IOException) { }
        return result;
        }
    }
    public void Dispose() { lock (_peakLock) { if (_pointer == null) return; _view.SafeMemoryMappedViewHandle.ReleasePointer(); _pointer = null; _view.Dispose(); _map.Dispose(); } }
}

public sealed class AudioLibrary : IDisposable
{
    private readonly string _cache;
    private readonly ConcurrentDictionary<string, Lazy<Task<PcmSource>>> _sources = new();
    private readonly SemaphoreSlim _decoders = new(2);
    private readonly CancellationTokenSource _shutdown = new();
    public string Ffmpeg { get; }
    public string Ffprobe { get; }
    public AudioLibrary(string cache, string toolsDirectory)
    {
        _cache = cache; Directory.CreateDirectory(cache);
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        Ffmpeg = Path.Combine(toolsDirectory, "ffmpeg" + suffix); Ffprobe = Path.Combine(toolsDirectory, "ffprobe" + suffix);
        if (!File.Exists(Ffmpeg) || !File.Exists(Ffprobe)) throw new FileNotFoundException($"Bundled audio tools are missing. Run the setup script first.\nExpected ffmpeg and ffprobe in:\n{toolsDirectory}");
        TrimCache();
    }
    public static string RuntimeId => OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64") : "linux-x64";
    public static Process Start(string executable, IEnumerable<string> args, bool input = false)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = input };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Could not start audio tool.");
    }
    public Task<PcmSource> LoadAsync(string path, CancellationToken cancel = default)
    {
        var file = new FileInfo(path); if (!file.Exists) throw new FileNotFoundException("Source sound is unavailable.", path);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Path.GetFullPath(path)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|48000-stereo-v1")));
        var lazy = _sources.GetOrAdd(key, _ => new Lazy<Task<PcmSource>>(() => DecodeAsync(path, key, cancel)));
        return Await();
        async Task<PcmSource> Await() { try { return await lazy.Value.WaitAsync(cancel).ConfigureAwait(false); } catch { if (lazy.Value.IsFaulted || lazy.Value.IsCanceled) _sources.TryRemove(key, out _); throw; } }
    }
    private async Task<PcmSource> DecodeAsync(string path, string key, CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _shutdown.Token); cancel = linked.Token;
        await _decoders.WaitAsync(cancel).ConfigureAwait(false);
        string target = Path.Combine(_cache, key + ".f32"), temp = target + "." + Guid.NewGuid() + ".tmp";
        try
        {
            if (!File.Exists(target))
            {
                using var process = Start(Ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-i", path, "-map", "0:a:0", "-vn", "-ar", "48000", "-ac", "2", "-f", "f32le", temp });
                using var registration = cancel.Register(() => Kill(process));
                var errorTask = process.StandardError.ReadToEndAsync(cancel); var outputTask = process.StandardOutput.ReadToEndAsync(cancel);
                await process.WaitForExitAsync(cancel).ConfigureAwait(false); string error = await errorTask.ConfigureAwait(false); await outputTask.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidDataException("Cannot decode sound: " + error);
                cancel.ThrowIfCancellationRequested(); File.Move(temp, target, true);
            }
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            return new PcmSource(target);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); _decoders.Release(); }
    }
    public async Task<string> ProbeAsync(string path, CancellationToken cancel = default)
    {
        using var p = Start(Ffprobe, new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels:format=duration", "-of", "json", path });
        using var reg = cancel.Register(() => Kill(p));
        var output = p.StandardOutput.ReadToEndAsync(cancel); var errors = p.StandardError.ReadToEndAsync(cancel); await p.WaitForExitAsync(cancel).ConfigureAwait(false);
        if (p.ExitCode != 0) throw new InvalidDataException(await errors.ConfigureAwait(false));
        using var json = JsonDocument.Parse(await output.ConfigureAwait(false)); var stream = json.RootElement.GetProperty("streams")[0];
        return $"{stream.GetProperty("codec_name").GetString()?.ToUpperInvariant()} · {stream.GetProperty("sample_rate").GetString()} Hz · {stream.GetProperty("channels").GetInt32()} channels";
    }
    public async Task<Dictionary<string, PcmSource>> PrepareAsync(MixProject mix, CancellationToken cancel = default, bool includeMuted = false)
    {
        var paths = (includeMuted ? mix.Clips : mix.AudibleClips).Select(t => t.SourcePath).Distinct().ToArray();
        var result = new ConcurrentDictionary<string, PcmSource>();
        await Task.WhenAll(paths.Select(async path => { if (File.Exists(path)) result[path] = await LoadAsync(path, cancel).ConfigureAwait(false); })).ConfigureAwait(false);
        return new(result);
    }
    public async Task ExportAsync(MixProject snapshot, string destination, bool ogg, IProgress<double>? progress, CancellationToken cancel, bool overwrite = true)
    {
        if (!snapshot.AudibleClips.Any(t => t.Duration > 0)) throw new InvalidOperationException("Add an unmuted sound before exporting.");
        if (snapshot.AudibleClips.Any(t => !File.Exists(t.SourcePath))) throw new IOException("Resolve, remove, or mute the unavailable tracks before exporting.");
        string full = Path.Combine(Paths.CanonicalDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!), Path.GetFileName(destination));
        if (File.Exists(full)) full = new FileInfo(full).ResolveLinkTarget(true)?.FullName ?? full;
        foreach (var track in snapshot.Clips)
        {
            if (!Directory.Exists(Path.GetDirectoryName(track.SourcePath))) continue;
            string source = Path.Combine(Paths.CanonicalDirectory(Path.GetDirectoryName(track.SourcePath)!), Path.GetFileName(track.SourcePath));
            if (File.Exists(source)) source = new FileInfo(source).ResolveLinkTarget(true)?.FullName ?? source;
            if (string.Equals(source, full, Paths.Comparison)) throw new IOException("An export cannot overwrite a source sound.");
        }
        var sources = await PrepareAsync(snapshot, cancel).ConfigureAwait(false);
        string temp = Path.Combine(Path.GetDirectoryName(full)!, ".soundshelf-" + Guid.NewGuid() + ".tmp");
        try
        {
            // FFmpeg writes 24-bit integer PCM with an extensible header that Godot rejects.
            using var p = Start(Ffmpeg, new[] { "-v", "error", "-y", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", "pipe:0", "-c:a", ogg ? "libvorbis" : "pcm_f32le", ogg ? "-q:a" : "-rf64", ogg ? "6" : "auto", "-f", ogg ? "ogg" : "wav", temp }, true);
            using var reg = cancel.Register(() => Kill(p));
            var errors = p.StandardError.ReadToEndAsync(); var output = p.StandardOutput.ReadToEndAsync();
            await Task.Run(async () =>
            {
                var kernel = new MixRenderer(snapshot, sources); long total = (long)Math.Ceiling(snapshot.Duration * PcmSource.SampleRate);
                var buffer = new float[2048 * 2]; var bytes = new byte[buffer.Length * sizeof(float)];
                for (long start = 0; start < total; start += 2048)
                {
                    cancel.ThrowIfCancellationRequested(); int count = (int)Math.Min(2048, total - start);
                    kernel.Render(start, count, buffer); Buffer.BlockCopy(buffer, 0, bytes, 0, count * 8);
                    await p.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(0, count * 8), cancel).ConfigureAwait(false);
                    if (start % (2048 * 24) == 0) progress?.Report(start / (double)total);
                }
                p.StandardInput.Close();
            }, cancel).ConfigureAwait(false);
            await p.WaitForExitAsync(cancel).ConfigureAwait(false); string error = await errors.ConfigureAwait(false); await output.ConfigureAwait(false);
            if (p.ExitCode != 0) throw new IOException("Export failed: " + error);
            cancel.ThrowIfCancellationRequested(); File.Move(temp, full, overwrite); progress?.Report(1);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void TrimCache()
    {
        var files = new DirectoryInfo(_cache).GetFiles("*.f32").OrderByDescending(f => f.LastWriteTimeUtc).ToList(); long total = 0;
        foreach (var file in files) { total += file.Length; if (total <= 4L * 1024 * 1024 * 1024) continue; try { file.Delete(); } catch (IOException) { } }
        foreach (var file in new DirectoryInfo(_cache).GetFiles("*.tmp")) try { file.Delete(); } catch (IOException) { }
    }
    public static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }
    public void Dispose()
    {
        _shutdown.Cancel();
        try { Task.WhenAll(_sources.Values.Where(l => l.IsValueCreated).Select(l => l.Value)).GetAwaiter().GetResult(); } catch (Exception) { /* Canceled or failed decodes have no mapped source to release. */ }
        foreach (var lazy in _sources.Values) if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) lazy.Value.Result.Dispose();
        _decoders.Dispose(); _shutdown.Dispose();
    }
}

public sealed class MixRenderer
{
    private static readonly ConcurrentDictionary<double, float[][]> Filters = new();
    private sealed record Prepared(MixClip Clip, float Gain, long EndFrame, PcmSource Source, float[][] Filter);
    private readonly Prepared[] _tracks;
    private readonly float _master;
    public bool Clipped { get; private set; }
    public MixRenderer(MixProject mix, IReadOnlyDictionary<string, PcmSource> sources)
    {
        _master = mix.MasterGain;
        _tracks = mix.AudibleTracks.SelectMany(track =>
        {
            var clips = track.Clips.OrderBy(c => c.Start).ToArray();
            return clips.Select((clip, index) => new { Clip = clip, Next = index + 1 < clips.Length ? (long)Math.Round(clips[index + 1].Start * PcmSource.SampleRate) : long.MaxValue })
                .Where(item => sources.ContainsKey(item.Clip.SourcePath))
                .Select(item => new Prepared(item.Clip.Clone(), track.Gain, item.Next, sources[item.Clip.SourcePath], Filters.GetOrAdd(item.Clip.Rate, Filter)));
        }).ToArray();
    }
    // A polyphase windowed-sinc interpolator reduces aliasing when pitching upward.
    private static float[][] Filter(double rate)
    {
        var filters = new float[256][]; double cutoff = Math.Min(1, 1 / rate);
        for (int phase = 0; phase < filters.Length; phase++)
        {
            var weights = new float[16]; double sum = 0;
            for (int tap = 0; tap < 16; tap++)
            {
                double x = tap - 7 - phase / 256.0, sx = x * cutoff;
                double value = (Math.Abs(sx) < 1e-8 ? 1 : Math.Sin(Math.PI * sx) / (Math.PI * sx)) * (0.5 + 0.5 * Math.Cos(Math.PI * x / 8)) * cutoff;
                weights[tap] = (float)value; sum += value;
            }
            for (int tap = 0; tap < 16; tap++) weights[tap] /= (float)sum;
            filters[phase] = weights;
        }
        return filters;
    }
    public static float EnvelopeGain(IReadOnlyList<EnvelopePoint> points, double sourceSeconds)
    {
        if (points.Count == 0) return 1;
        if (sourceSeconds <= points[0].SourceSeconds) return points[0].Gain;
        for (int i = 1; i < points.Count; i++)
        {
            if (sourceSeconds > points[i].SourceSeconds) continue;
            var a = points[i - 1]; var b = points[i]; double length = b.SourceSeconds - a.SourceSeconds;
            return length <= 0 ? b.Gain : (float)(a.Gain + (b.Gain - a.Gain) * ((sourceSeconds - a.SourceSeconds) / length));
        }
        return points[^1].Gain;
    }
    public void Render(long firstFrame, int frames, float[] destination)
    {
        Array.Clear(destination, 0, frames * 2); Clipped = false;
        foreach (var prepared in _tracks)
        {
            var t = prepared.Clip; var source = prepared.Source; double rate = t.Rate;
            long start = (long)Math.Round(t.Start * PcmSource.SampleRate); double trim = t.TrimStart * PcmSource.SampleRate, end = t.TrimEnd * PcmSource.SampleRate;
            for (int f = 0; f < frames; f++)
            {
                if (firstFrame + f >= prepared.EndFrame) break;
                long elapsed = firstFrame + f - start; if (elapsed < 0) continue;
                double position = trim + elapsed * rate; if (position >= end) break;
                long index = (long)position; float left = 0, right = 0;
                if (Math.Abs(rate - 1) < 1e-9 && Math.Abs(position - index) < 1e-8) { left = source.Sample(index, 0); right = source.Sample(index, 1); }
                else
                {
                    var weights = prepared.Filter[Math.Clamp((int)((position - index) * 256), 0, 255)];
                    for (int tap = 0; tap < 16; tap++)
                    {
                        long sample = index + tap - 7; if (sample < trim || sample >= end) continue;
                        left += source.Sample(sample, 0) * weights[tap]; right += source.Sample(sample, 1) * weights[tap];
                    }
                }
                float gain = ClipVolume.Gain(t, position / PcmSource.SampleRate) * prepared.Gain;
                destination[f * 2] += left * gain; destination[f * 2 + 1] += right * gain;
            }
        }
        for (int i = 0; i < frames * 2; i++)
        {
            float v = destination[i] * _master;
            if (Math.Abs(v) > 1) Clipped = true;
            destination[i] = float.IsFinite(v) ? Math.Clamp(v, -1, 1) : 0;
        }
    }
}

public sealed class LiveMixer : IDisposable
{
    public sealed record Block(long Frame, float[] Samples, bool Clipped);
    private readonly ConcurrentQueue<Block> _queue = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread _thread;
    private MixRenderer _renderer;
    private long _frame;
    private long _end;
    public LiveMixer(MixProject mix, IReadOnlyDictionary<string, PcmSource> sources, double start)
    {
        _renderer = new(mix, sources); _frame = (long)(start * PcmSource.SampleRate); _end = (long)Math.Ceiling(mix.TimelineDuration * PcmSource.SampleRate);
        _thread = new Thread(Produce) { IsBackground = true, Name = "SFXplore mixer" }; _thread.Start();
    }
    public bool Finished => Volatile.Read(ref _frame) >= Volatile.Read(ref _end) && _queue.IsEmpty;
    public void Update(MixProject mix, IReadOnlyDictionary<string, PcmSource> sources)
    { Volatile.Write(ref _renderer, new(mix, sources)); Volatile.Write(ref _end, (long)Math.Ceiling(mix.TimelineDuration * PcmSource.SampleRate)); }
    public bool TryRead(out Block? block) => _queue.TryDequeue(out block);
    private void Produce()
    {
        while (!_cancel.IsCancellationRequested)
        {
            long frame = Volatile.Read(ref _frame), end = Volatile.Read(ref _end);
            if (_queue.Count >= 3 || frame >= end) { _cancel.Token.WaitHandle.WaitOne(2); continue; }
            var renderer = Volatile.Read(ref _renderer); int count = (int)Math.Min(512, end - frame); var data = new float[count * 2];
            renderer.Render(frame, count, data); _queue.Enqueue(new(frame, data, renderer.Clipped)); Volatile.Write(ref _frame, frame + count);
        }
    }
    public void Dispose() { _cancel.Cancel(); _thread.Join(); _cancel.Dispose(); }
}
