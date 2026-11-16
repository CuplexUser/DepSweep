using System.Reflection;

namespace DepSweep.Core;

/// <summary>Version information stamped at build time by MinVer (from git tags).</summary>
public static class AppInfo
{
    private static readonly string Informational =
        (Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>Semantic version without build metadata, e.g. "1.2.0" or "1.2.1-alpha.0.3".</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>Short commit id from the build metadata, when available.</summary>
    public static string? Commit =>
        Informational.Split('+') is [_, var meta, ..] && meta.Length > 0 ? meta[..Math.Min(7, meta.Length)] : null;

    /// <summary>Display form, e.g. "1.2.0 (362a11c)".</summary>
    public static string DisplayVersion => Commit is null ? Version : $"{Version} ({Commit})";

    public static bool IsPreRelease => Version.Contains('-');
}
