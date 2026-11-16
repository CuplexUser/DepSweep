namespace DepSweep.Core.Models;

public enum PackageManagerKind
{
    Npm,
    Pnpm,
}

/// <summary>Advisory severity, ordered from least to most severe.</summary>
public enum Severity
{
    Info,
    Low,
    Moderate,
    High,
    Critical,
}

/// <summary>Whether an automatic fix exists for a vulnerable package.</summary>
public enum FixKind
{
    /// <summary>Fixable within declared semver ranges (safe fix).</summary>
    Fixable,

    /// <summary>Fix requires a breaking (semver-major) change, i.e. --force.</summary>
    NeedsForce,

    /// <summary>No patched version is available.</summary>
    NoFix,
}

public enum ProjectStatus
{
    NotScanned,
    Queued,
    Scanning,
    Clean,
    Vulnerable,
    Fixing,
    Fixed,
    Partial,
    Unchanged,
    WouldFix,
    Skipped,
    Error,
}

public static class SeverityExtensions
{
    public static Severity ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "critical" => Severity.Critical,
        "high" => Severity.High,
        "moderate" or "medium" => Severity.Moderate,
        "low" => Severity.Low,
        _ => Severity.Info,
    };

    public static string ToDisplay(this Severity severity) => severity.ToString().ToLowerInvariant();
}
