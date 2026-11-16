using System.Text.Json;
using DepSweep.Core.Models;

namespace DepSweep.Core.PackageManagers;

public sealed class AuditFailedException(string message, string? output = null) : Exception(message)
{
    public string? Output { get; } = output;
}

/// <summary>Parses <c>npm audit --json</c> output (audit report version 2, npm 7+).</summary>
public static class NpmAuditParser
{
    /// <param name="json">Raw command output.</param>
    /// <param name="declared">Declared dependency specs from package.json, used to detect exact pins.</param>
    public static AuditResult Parse(string json, IReadOnlyDictionary<string, string>? declared = null)
    {
        using var doc = ParseJson(json, "npm audit");
        var root = doc.RootElement;

        if (!root.TryGetProperty("vulnerabilities", out var vulns) || vulns.ValueKind != JsonValueKind.Object)
        {
            throw new AuditFailedException("npm audit failed: " + ExtractError(root), json);
        }

        var list = new List<Vulnerability>();
        foreach (var prop in vulns.EnumerateObject())
        {
            var v = prop.Value;
            var advisories = new List<Advisory>();
            if (v.TryGetProperty("via", out var via) && via.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in via.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    advisories.Add(new Advisory(
                        GetString(item, "title") ?? "(untitled advisory)",
                        GetString(item, "url"),
                        SeverityExtensions.ParseSeverity(GetString(item, "severity")),
                        GetString(item, "range")));
                }
            }

            var (fix, fixVersion) = ParseFixAvailable(v);
            var name = GetString(v, "name") ?? prop.Name;
            var isDirect = v.TryGetProperty("isDirect", out var direct) && direct.ValueKind == JsonValueKind.True;
            list.Add(new Vulnerability(
                name,
                SeverityExtensions.ParseSeverity(GetString(v, "severity")),
                isDirect,
                PackageJson.AdjustForPinnedDirect(fix, name, isDirect, declared),
                fixVersion,
                advisories));
        }

        var counts = root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("vulnerabilities", out var mv)
            ? ReadCounts(mv)
            : CountBySeverity(list);

        return new AuditResult(counts, Sort(list));
    }

    private static (FixKind, string?) ParseFixAvailable(JsonElement v)
    {
        if (!v.TryGetProperty("fixAvailable", out var fa))
        {
            return (FixKind.NoFix, null);
        }

        return fa.ValueKind switch
        {
            JsonValueKind.True => (FixKind.Fixable, null),
            JsonValueKind.Object => (
                fa.TryGetProperty("isSemVerMajor", out var major) && major.ValueKind == JsonValueKind.True
                    ? FixKind.NeedsForce
                    : FixKind.Fixable,
                GetString(fa, "name") is { } n && GetString(fa, "version") is { } ver ? $"{n}@{ver}" : null),
            _ => (FixKind.NoFix, null),
        };
    }

    internal static JsonDocument ParseJson(string output, string tool)
    {
        // Tools occasionally print warnings before the JSON payload; start at the first brace.
        var start = output.IndexOf('{');
        if (start < 0)
        {
            throw new AuditFailedException($"{tool} produced no JSON output.", output);
        }

        try
        {
            return JsonDocument.Parse(output.AsMemory(start));
        }
        catch (JsonException ex)
        {
            throw new AuditFailedException($"{tool} produced invalid JSON: {ex.Message}", output);
        }
    }

    internal static string ExtractError(JsonElement root)
    {
        if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
        {
            return m.GetString()!;
        }

        if (root.TryGetProperty("error", out var e))
        {
            if (e.ValueKind == JsonValueKind.String)
            {
                return e.GetString()!;
            }

            if (e.ValueKind == JsonValueKind.Object)
            {
                var parts = new[] { GetString(e, "code"), GetString(e, "message"), GetString(e, "summary"), GetString(e, "detail") }
                    .Where(s => !string.IsNullOrWhiteSpace(s));
                var text = string.Join(" - ", parts);
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }

        return "unexpected output format";
    }

    internal static SeverityCounts ReadCounts(JsonElement e) => new(
        GetInt(e, "info"), GetInt(e, "low"), GetInt(e, "moderate"), GetInt(e, "high"), GetInt(e, "critical"));

    internal static SeverityCounts CountBySeverity(IEnumerable<Vulnerability> list)
    {
        var g = list.GroupBy(v => v.Severity).ToDictionary(x => x.Key, x => x.Count());
        return new SeverityCounts(
            g.GetValueOrDefault(Severity.Info), g.GetValueOrDefault(Severity.Low), g.GetValueOrDefault(Severity.Moderate),
            g.GetValueOrDefault(Severity.High), g.GetValueOrDefault(Severity.Critical));
    }

    internal static IReadOnlyList<Vulnerability> Sort(IEnumerable<Vulnerability> list) =>
        list.OrderByDescending(v => v.Severity).ThenByDescending(v => v.IsDirect).ThenBy(v => v.Package, StringComparer.Ordinal).ToList();

    internal static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.TryGetInt32(out var i) ? i : 0;
}

/// <summary>Parses <c>pnpm audit --json</c> output (npm v1-style "advisories" map).</summary>
public static class PnpmAuditParser
{
    public static AuditResult Parse(string json, IReadOnlyDictionary<string, string>? declared = null)
    {
        using var doc = NpmAuditParser.ParseJson(json, "pnpm audit");
        var root = doc.RootElement;

        if (!root.TryGetProperty("advisories", out var advisories) || advisories.ValueKind != JsonValueKind.Object)
        {
            throw new AuditFailedException("pnpm audit failed: " + NpmAuditParser.ExtractError(root), json);
        }

        var byPackage = new Dictionary<string, (List<Advisory> Advisories, bool Direct, bool NoFix, string? Patched)>(StringComparer.Ordinal);
        foreach (var prop in advisories.EnumerateObject())
        {
            var a = prop.Value;
            var module = NpmAuditParser.GetString(a, "module_name") ?? prop.Name;
            var patched = NpmAuditParser.GetString(a, "patched_versions");
            var noFix = string.IsNullOrWhiteSpace(patched) || patched.Trim() == "<0.0.0";
            var direct = a.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array
                && findings.EnumerateArray().Any(f =>
                    f.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array
                    && paths.EnumerateArray().Any(p => p.GetString() is { } s && s.Count(c => c == '>') == 1));

            var advisory = new Advisory(
                NpmAuditParser.GetString(a, "title") ?? "(untitled advisory)",
                NpmAuditParser.GetString(a, "url"),
                SeverityExtensions.ParseSeverity(NpmAuditParser.GetString(a, "severity")),
                NpmAuditParser.GetString(a, "vulnerable_versions"));

            if (!byPackage.TryGetValue(module, out var entry))
            {
                entry = ([], false, true, null);
            }

            entry.Advisories.Add(advisory);
            byPackage[module] = (entry.Advisories, entry.Direct || direct, entry.NoFix && noFix, noFix ? entry.Patched : patched);
        }

        var list = byPackage.Select(kv => new Vulnerability(
                kv.Key,
                kv.Value.Advisories.Max(a => a.Severity),
                kv.Value.Direct,
                PackageJson.AdjustForPinnedDirect(kv.Value.NoFix ? FixKind.NoFix : FixKind.Fixable, kv.Key, kv.Value.Direct, declared),
                kv.Value.Patched is null ? null : $"{kv.Key}@{kv.Value.Patched}",
                kv.Value.Advisories))
            .ToList();

        var counts = root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("vulnerabilities", out var mv)
            ? NpmAuditParser.ReadCounts(mv)
            : NpmAuditParser.CountBySeverity(list);

        return new AuditResult(counts, NpmAuditParser.Sort(list));
    }
}
