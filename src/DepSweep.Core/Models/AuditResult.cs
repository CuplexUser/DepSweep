namespace DepSweep.Core.Models;

public sealed record Advisory(string Title, string? Url, Severity Severity, string? VulnerableRange);

public sealed record Vulnerability(
    string Package,
    Severity Severity,
    bool IsDirect,
    FixKind Fix,
    string? FixVersion,
    IReadOnlyList<Advisory> Advisories)
{
    public string FixDescription => Fix switch
    {
        FixKind.Fixable => FixVersion is null ? "fix available" : $"fix available ({FixVersion})",
        FixKind.NeedsForce => FixVersion is null ? "needs --force (breaking)" : $"needs --force (breaking: {FixVersion})",
        _ => "no fix available",
    };
}

public sealed record AuditResult(SeverityCounts Counts, IReadOnlyList<Vulnerability> Vulnerabilities)
{
    public static AuditResult Clean { get; } = new(SeverityCounts.Empty, []);

    public bool IsClean => Counts.Total == 0;
}
