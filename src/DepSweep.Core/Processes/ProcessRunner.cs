using System.Collections.Concurrent;
using System.Text;
using CliWrap;

namespace DepSweep.Core.Processes;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Last non-empty lines of stderr (or stdout when stderr is empty), for error messages.</summary>
    public string Tail(int lines = 6)
    {
        var source = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        return string.Join(
            Environment.NewLine,
            source.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).TakeLast(lines));
    }
}

public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="tool"/> (resolved via PATH/PATHEXT) and captures its output. A non-zero exit code is
    /// returned, not thrown. Throws <see cref="ToolNotFoundException"/> when the tool cannot be found and
    /// <see cref="TimeoutException"/> when <paramref name="timeout"/> elapses.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default);
}

public sealed class ToolNotFoundException(string tool)
    : Exception($"'{tool}' was not found on PATH.")
{
    public string Tool { get; } = tool;
}

public sealed class ProcessRunner : IProcessRunner
{
    private static readonly IReadOnlyDictionary<string, string?> QuietEnvironment = new Dictionary<string, string?>
    {
        ["NO_COLOR"] = "1",
        ["FORCE_COLOR"] = "0",
        ["npm_config_color"] = "false",
        ["npm_config_fund"] = "false",
        ["npm_config_update_notifier"] = "false",
        ["npm_config_progress"] = "false",
    };

    public async Task<ProcessResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var executable = ExecutableResolver.Resolve(tool) ?? throw new ToolNotFoundException(tool);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTarget = PipeTarget.ToStringBuilder(stdout);
        var stderrTarget = PipeTarget.ToStringBuilder(stderr);
        if (onOutputLine is not null)
        {
            stdoutTarget = PipeTarget.Merge(stdoutTarget, PipeTarget.ToDelegate(onOutputLine));
            stderrTarget = PipeTarget.Merge(stderrTarget, PipeTarget.ToDelegate(onOutputLine));
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var result = await Cli.Wrap(executable)
                .WithArguments(arguments)
                .WithWorkingDirectory(workingDirectory)
                .WithEnvironmentVariables(QuietEnvironment)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(stdoutTarget)
                .WithStandardErrorPipe(stderrTarget)
                .ExecuteAsync(linked.Token)
                .ConfigureAwait(false);

            return new ProcessResult(result.ExitCode, stdout.ToString(), stderr.ToString(), result.RunTime);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"'{tool} {string.Join(' ', arguments)}' timed out after {timeout.TotalSeconds:0}s in {workingDirectory}.");
        }
    }
}

/// <summary>Resolves a command name to a full path using PATH and (on Windows) PATHEXT, e.g. npm -> npm.cmd.</summary>
public static class ExecutableResolver
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(string tool)
    {
        if (Path.IsPathRooted(tool))
        {
            return File.Exists(tool) ? tool : null;
        }

        return Cache.GetOrAdd(tool, static t =>
        {
            var extensions = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                : [""];

            // On Windows, a bare name such as "npm" is a POSIX shell script; prefer the PATHEXT variants.
            var candidates = Path.HasExtension(t) ? [t] : extensions.Select(e => t + e.ToLowerInvariant()).ToArray();
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (var dir in dirs)
            {
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var full = Path.Combine(dir.Trim('"'), candidate);
                        if (File.Exists(full))
                        {
                            return full;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Ignore malformed PATH entries.
                    }
                }
            }

            return null;
        });
    }
}
