namespace SoundShelf.Core;

public static class SoundFileCopy
{
    public static async Task CopyAsync(string source, string destination, bool overwrite, CancellationToken cancellation)
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
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        try
        {
            await using (output)
                await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite);
        }
        finally { File.Delete(temp); }
    }
}
