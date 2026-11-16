using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepSweep.Core.Config;

/// <summary>Loads and saves the shared config (used by both the CLI and the GUI).</summary>
public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public ConfigStore(string? path = null)
    {
        FilePath = path
            ?? Environment.GetEnvironmentVariable("DEPSWEEP_CONFIG")
            ?? Path.Combine(AppDataDirectory, "config.json");
    }

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DepSweep");

    public string FilePath { get; }

    public DepSweepConfig Load()
    {
        if (!File.Exists(FilePath))
        {
            return new DepSweepConfig();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<DepSweepConfig>(json, JsonOptions) ?? new DepSweepConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Config file '{FilePath}' is not valid JSON: {ex.Message}", ex);
        }
    }

    public void Save(DepSweepConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
