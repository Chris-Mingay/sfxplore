namespace SoundShelf.Core;

public static class Paths
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static string CanonicalDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        // Resolve every component, not only a link at the final path component.
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var info = new DirectoryInfo(Path.Combine(current, component));
            var target = info.ResolveLinkTarget(true);
            // A link's absolute target can itself contain linked ancestors
            // (for example macOS /var -> /private/var).
            current = target == null ? info.FullName : CanonicalDirectory(target.FullName);
            // Recover the on-disk spelling on case-insensitive macOS volumes too.
            if (OperatingSystem.IsMacOS())
            {
                var parent = Directory.GetParent(current);
                if (parent != null)
                {
                    var names = parent.EnumerateDirectories().ToArray();
                    string resolvedName = Path.GetFileName(current);
                    current = (names.FirstOrDefault(d => d.Name == resolvedName) ?? names.FirstOrDefault(d => string.Equals(d.Name, resolvedName, StringComparison.OrdinalIgnoreCase)))?.FullName ?? current;
                }
            }
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    public static bool Overlaps(string a, string b) => Within(a, b) || Within(b, a);
    public static bool Within(string path, string parent) => string.Equals(path, parent, Comparison) || path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, Comparison);
    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    public static string Join(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
}
