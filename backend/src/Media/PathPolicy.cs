namespace MediaServer.Media;

public sealed class PathPolicy(string[] allowedRoots)
{
    public string ValidateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Use an absolute media directory.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.GetPathRoot(full) || !Directory.Exists(full))
            throw new ArgumentException(
                "Choose an existing media subdirectory, not a filesystem root."
            );
        if (!allowedRoots.Any(root => Within(root, full)))
            throw new ArgumentException(
                "Directory is outside the server's configured allowed roots."
            );
        RejectLinks(full);
        return full;
    }

    public string Resolve(string root, string relative)
    {
        ValidateDirectory(root);
        if (Path.IsPathFullyQualified(relative))
            throw new ArgumentException("Relative paths required.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(root, full))
            throw new ArgumentException("Path escapes library.");
        RejectLinks(full);
        return full;
    }

    public FileStream OpenRead(string root, string relative) =>
        SafeMediaFile.Open(Resolve(root, relative));

    public static bool Within(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return full.Equals(fullRoot, comparison)
            || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    public static void RejectLinks(string path)
    {
        for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Symbolic links and junctions are not accepted.");
    }
}
