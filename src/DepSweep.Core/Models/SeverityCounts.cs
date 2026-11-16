namespace DepSweep.Core.Models;

public sealed record SeverityCounts(int Info, int Low, int Moderate, int High, int Critical)
{
    public static SeverityCounts Empty { get; } = new(0, 0, 0, 0, 0);

    public int Total => Info + Low + Moderate + High + Critical;

    public int this[Severity severity] => severity switch
    {
        Severity.Critical => Critical,
        Severity.High => High,
        Severity.Moderate => Moderate,
        Severity.Low => Low,
        _ => Info,
    };

    /// <summary>Number of findings at or above <paramref name="threshold"/>.</summary>
    public int AtOrAbove(Severity threshold) =>
        Enum.GetValues<Severity>().Where(s => s >= threshold).Sum(s => this[s]);

    /// <summary>Per-severity reduction from this count to <paramref name="after"/>, never negative.</summary>
    public SeverityCounts Reduction(SeverityCounts after) => new(
        Math.Max(0, Info - after.Info),
        Math.Max(0, Low - after.Low),
        Math.Max(0, Moderate - after.Moderate),
        Math.Max(0, High - after.High),
        Math.Max(0, Critical - after.Critical));

    public static SeverityCounts operator +(SeverityCounts a, SeverityCounts b) => new(
        a.Info + b.Info, a.Low + b.Low, a.Moderate + b.Moderate, a.High + b.High, a.Critical + b.Critical);

    /// <summary>Human summary such as "2 critical, 1 high". Returns "none" when empty.</summary>
    public string ToSummary()
    {
        var parts = Enum.GetValues<Severity>()
            .Reverse()
            .Where(s => this[s] > 0)
            .Select(s => $"{this[s]} {s.ToDisplay()}")
            .ToList();
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>Compact form "C/H/M/L", e.g. "1/2/0/3".</summary>
    public string ToCompact() => $"{Critical}/{High}/{Moderate}/{Low}";
}
