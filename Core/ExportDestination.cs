namespace SoundShelf.Core;

public static class ExportDestination
{
    public static string FileName(string name, bool ogg)
    {
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl))
            throw new ArgumentException("Enter a file name without directory separators or invalid characters.");
        string extension = ogg ? ".ogg" : ".wav";
        string existing = Path.GetExtension(name);
        if (existing.Equals(".wav", StringComparison.OrdinalIgnoreCase) || existing.Equals(".ogg", StringComparison.OrdinalIgnoreCase)) return Path.ChangeExtension(name, extension);
        return name + extension;
    }
    public static string InLibrary(Library library, string relativeFolder, string name, bool ogg)
    {
        string root = Paths.CanonicalDirectory(library.Path);
        string directory = Paths.CanonicalDirectory(Paths.Join(root, relativeFolder));
        if (!Paths.Within(directory, root)) throw new ArgumentException("Choose a folder inside the selected library.");
        string destination = Path.Combine(directory, FileName(name, ogg));
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Choose a file name that does not refer to a symbolic link.");
        return destination;
    }
}
