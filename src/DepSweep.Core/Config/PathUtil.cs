namespace DepSweep.Core.Config;

public static class PathUtil
{
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool Equal(string a, string b) => Comparer.Equals(Normalize(a), Normalize(b));

    /// <summary>True when <paramref name="path"/> equals or is inside <paramref name="folder"/>.</summary>
    public static bool IsUnder(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return Comparer.Equals(p, f) || p.StartsWith(f + Path.DirectorySeparatorChar, Comparison);
    }

    /// <summary>Relative path using forward slashes, as git prints it.</summary>
    public static string ToRelativeForwardSlash(string relativeTo, string path) =>
        Path.GetRelativePath(relativeTo, path).Replace('\\', '/');
}
