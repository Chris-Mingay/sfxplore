namespace SoundShelf.Core;

public static class SoundFileCopy
{
    public static Task CopyAsync(string source, string destination, bool overwrite, CancellationToken cancellation) =>
        SaveAsync(source, destination, overwrite, CopyBytesAsync, cancellation);

    public static Task ConvertAsync(string source, string destination, bool overwrite, string ffmpeg, CancellationToken cancellation)
    {
        string[] format = Path.GetExtension(destination).ToLowerInvariant() switch
        {
            ".wav" => new[] { "-c:a", "pcm_f32le", "-rf64", "auto", "-f", "wav" },
            ".ogg" => new[] { "-ar", "48000", "-c:a", "libvorbis", "-q:a", "6", "-f", "ogg" },
            _ => throw new ArgumentException("Choose WAV or OGG for conversion.", nameof(destination))
        };

        return SaveAsync(source, destination, overwrite, async (input, temp, cancel) =>
        {
            string[] args = ["-v", "error", "-nostdin", "-y", "-i", input, "-map", "0:a:0", ..format, temp];
            using var process = AudioLibrary.Start(ffmpeg, args);
            using var registration = cancel.Register(() => AudioLibrary.Kill(process));
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            string error = await errors.ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("Could not convert sound: " + error);
        }, cancellation);
    }

    private static async Task CopyBytesAsync(string source, string temp, CancellationToken cancellation)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
    }

    private static async Task SaveAsync(string source, string destination, bool overwrite, Func<string, string, CancellationToken, Task> write, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        source = Path.GetFullPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("The source sound is unavailable.", source);
        source = new FileInfo(source).ResolveLinkTarget(true)?.FullName ?? source;
        source = Path.Combine(Paths.CanonicalDirectory(Path.GetDirectoryName(source)!), Path.GetFileName(source));

        destination = Path.GetFullPath(destination);
        string directory = Paths.CanonicalDirectory(Path.GetDirectoryName(destination)!);
        destination = Path.Combine(directory, Path.GetFileName(destination));
        if (string.Equals(source, destination, Paths.Comparison))
            throw new IOException("Choose a different location or name for the copy.");

        string temp = Path.Combine(directory, ".soundshelf-" + Guid.NewGuid() + ".tmp");
        try
        {
            await write(source, temp, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite);
        }
        finally { File.Delete(temp); }
    }
}
