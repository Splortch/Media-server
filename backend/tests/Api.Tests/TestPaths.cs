namespace Api.Tests;

internal static class TestPaths
{
    // macOS commonly exposes its temporary directory through /var -> /private/var.
    // Resolve only the fixture's system temp root, never application-supplied media
    // paths: production PathPolicy must still reject symlinks and junctions.
    internal static string TempRoot
    {
        get
        {
            var full = Path.GetFullPath(Path.GetTempPath());
            var root = Path.GetPathRoot(full)!;
            var current = root;
            foreach (
                var segment in full[root.Length..]
                    .Split(
                        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries
                    )
            )
            {
                var directory = new DirectoryInfo(Path.Combine(current, segment));
                current = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
            }
            return current;
        }
    }
}
