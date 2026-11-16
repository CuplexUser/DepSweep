using System.Text.Json;
using System.Text.RegularExpressions;
using DepSweep.Core.Models;

namespace DepSweep.Core.PackageManagers;

public static partial class PackageJson
{
    private static readonly string[] DependencySections = ["dependencies", "devDependencies", "optionalDependencies"];

    /// <summary>Declared dependency specs (name → range) from the project's package.json; empty on any error.</summary>
    public static IReadOnlyDictionary<string, string> ReadDeclaredDependencies(string projectDir)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = File.OpenRead(Path.Combine(projectDir, "package.json"));
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
            foreach (var section in DependencySections)
            {
                if (doc.RootElement.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
                {
                    foreach (var dep in deps.EnumerateObject().Where(d => d.Value.ValueKind == JsonValueKind.String))
                    {
                        result.TryAdd(dep.Name, dep.Value.GetString()!);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Without declared specs the parser simply trusts the package manager's classification.
        }

        return result;
    }

    /// <summary>True for an exact version spec such as "1.2.0" or "=1.2.0" (no range operators).</summary>
    public static bool IsExactVersion(string spec) => ExactVersion().IsMatch(spec.Trim());

    /// <summary>
    /// A vulnerable direct dependency pinned to an exact version cannot be fixed without changing its declared
    /// spec, which npm only does with --force. Reclassify such "fixable" entries as needing --force.
    /// </summary>
    internal static FixKind AdjustForPinnedDirect(FixKind fix, string package, bool isDirect, IReadOnlyDictionary<string, string>? declared) =>
        fix == FixKind.Fixable && isDirect && declared is not null
            && declared.TryGetValue(package, out var spec) && IsExactVersion(spec)
            ? FixKind.NeedsForce
            : fix;

    [GeneratedRegex(@"^=?v?\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex ExactVersion();
}
