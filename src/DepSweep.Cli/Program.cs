using System.CommandLine;
using System.Text;
using DepSweep.Cli;
using DepSweep.Core.Config;
using DepSweep.Core.Models;
using DepSweep.Core.Processes;
using DepSweep.Core.Reporting;
using DepSweep.Core.Sweep;

Console.OutputEncoding = Encoding.UTF8;

var store = new ConfigStore();
var reportStore = new ReportStore();

// ---- shared options ----
var pathsArgument = new Argument<string[]>("paths")
{
    Description = "Folders to search (recursively). Defaults to the configured roots.",
    Arity = ArgumentArity.ZeroOrMore,
};
var minSeverityOption = new Option<Severity>("--min-severity")
{
    Description = "Lowest severity that makes the exit code non-zero (info, low, moderate, high, critical).",
    DefaultValueFactory = _ => Severity.Low,
};
var concurrencyOption = new Option<int?>("--concurrency", "-j") { Description = "Projects processed in parallel (default from config: 4)." };

// ---- roots ----
var rootsCommand = new Command("roots", "Manage the folders DepSweep searches for projects.");

var rootsList = new Command("list", "List configured root folders.");
rootsList.SetAction(_ =>
{
    var config = store.Load();
    if (config.Roots.Count == 0)
    {
        Console.WriteLine("No roots configured. Add one with: depsweep roots add <path>");
        return 0;
    }

    foreach (var r in config.Roots)
    {
        var flags = string.Join(", ", new[] { r.Recursive ? "recursive" : "this folder only", r.Enabled ? null : "disabled" }.Where(f => f is not null));
        Console.WriteLine($"{r.Path}  ({flags}){(Directory.Exists(r.Path) ? "" : "  [missing]")}");
    }

    Console.WriteLine();
    Console.WriteLine($"Config: {store.FilePath}");
    return 0;
});

var rootPathArgument = new Argument<string>("path") { Description = "Folder path." };
var noRecurseOption = new Option<bool>("--no-recurse") { Description = "Only treat the folder itself as a project; do not search sub-folders." };
var rootsAdd = new Command("add", "Add a root folder.") { rootPathArgument, noRecurseOption };
rootsAdd.SetAction(parse =>
{
    var path = PathUtil.Normalize(parse.GetValue(rootPathArgument)!);
    if (!Directory.Exists(path))
    {
        ConsoleUi.Error($"Folder does not exist: {path}");
        return 2;
    }

    var config = store.Load();
    var existing = config.FindRoot(path);
    if (existing is null)
    {
        config.Roots.Add(new RootFolder { Path = path, Recursive = !parse.GetValue(noRecurseOption) });
    }
    else
    {
        existing.Recursive = !parse.GetValue(noRecurseOption);
        existing.Enabled = true;
    }

    store.Save(config);
    Console.WriteLine(existing is null ? $"Added {path}" : $"Updated {path}");
    return 0;
});

var rootsRemove = new Command("remove", "Remove a root folder.") { rootPathArgument };
rootsRemove.SetAction(parse =>
{
    var path = parse.GetValue(rootPathArgument)!;
    var config = store.Load();
    var removed = config.Roots.RemoveAll(r => PathUtil.Equal(r.Path, path));
    store.Save(config);
    Console.WriteLine(removed > 0 ? $"Removed {PathUtil.Normalize(path)}" : $"Not a configured root: {path}");
    return removed > 0 ? 0 : 2;
});

rootsCommand.Subcommands.Add(rootsList);
rootsCommand.Subcommands.Add(rootsAdd);
rootsCommand.Subcommands.Add(rootsRemove);

// ---- list ----
var listCommand = new Command("list", "List discovered projects without auditing them.") { pathsArgument };
listCommand.SetAction(parse =>
{
    var config = store.Load();
    var (projects, ignored) = ConsoleUi.Discover(config, parse.GetValue(pathsArgument), includeExcluded: true);
    foreach (var p in projects)
    {
        var excluded = config.IsExcluded(p.Path) ? "  [excluded]" : "";
        Console.WriteLine($"{p.PackageManager.ToString().ToLowerInvariant(),-5} {p.Name,-30} {p.Path}{excluded}");
    }

    foreach (var i in ignored)
    {
        ConsoleUi.WriteColored($"skip  {i.Path}  ({i.Reason})", ConsoleColor.DarkGray);
    }

    Console.WriteLine($"{projects.Count} project(s), {ignored.Count} skipped folder(s).");
    return 0;
});

// ---- exclude / include ----
var projectPathArgument = new Argument<string>("project") { Description = "Project folder path." };
var excludeCommand = new Command("exclude", "Exclude a project from scan/fix runs over the configured roots.") { projectPathArgument };
excludeCommand.SetAction(parse => ConsoleUi.SetExcluded(store, parse.GetValue(projectPathArgument)!, true));
var includeCommand = new Command("include", "Re-include a previously excluded project.") { projectPathArgument };
includeCommand.SetAction(parse => ConsoleUi.SetExcluded(store, parse.GetValue(projectPathArgument)!, false));

// ---- scan ----
var jsonOption = new Option<bool>("--json") { Description = "Write machine-readable JSON to stdout." };
var scanCommand = new Command("scan", "Audit projects and show vulnerabilities. Does not modify anything.")
{
    pathsArgument, minSeverityOption, concurrencyOption, jsonOption,
};
scanCommand.SetAction(async (parse, ct) =>
{
    var config = store.Load();
    var json = parse.GetValue(jsonOption);
    var (projects, _) = ConsoleUi.Discover(config, parse.GetValue(pathsArgument));
    if (projects.Count == 0)
    {
        ConsoleUi.Error("No projects found. Add a root with 'depsweep roots add <path>' or pass a folder.");
        return 2;
    }

    var options = ConsoleUi.Options(config, parse.GetValue(concurrencyOption));
    var service = new SweepService(new ProcessRunner());
    var results = await service.ScanAsync(projects, options, new ConsoleProgress(quiet: json), ct);

    if (json)
    {
        Console.WriteLine(JsonReportWriter.WriteScan(results));
    }
    else
    {
        ConsoleUi.PrintScanTable(results);
    }

    return ConsoleUi.ExitCode(results, parse.GetValue(minSeverityOption));
});

// ---- fix ----
var forceOption = new Option<bool>("--force") { Description = "Allow breaking (semver-major) fixes: npm audit fix --force / pnpm overrides." };
var allowDirtyOption = new Option<bool>("--allow-dirty") { Description = "Also fix projects whose package files have uncommitted changes." };
var dryRunOption = new Option<bool>("--dry-run") { Description = "Audit and report what would be fixed without changing files." };
var formatOption = new Option<string>("--format")
{
    Description = "Report format written to stdout: console, md or json.",
    DefaultValueFactory = _ => "console",
};
formatOption.AcceptOnlyFromAmong("console", "md", "json");
var outOption = new Option<FileInfo?>("--out", "-o") { Description = "Also write the report (md or json, by extension) to this file." };

var fixCommand = new Command("fix", "Audit and fix projects, then print a per-repository report of what to commit.")
{
    pathsArgument, forceOption, allowDirtyOption, dryRunOption, concurrencyOption, minSeverityOption, formatOption, outOption,
};
fixCommand.SetAction(async (parse, ct) =>
{
    var config = store.Load();
    var format = parse.GetValue(formatOption)!;
    var (projects, _) = ConsoleUi.Discover(config, parse.GetValue(pathsArgument));
    if (projects.Count == 0)
    {
        ConsoleUi.Error("No projects found. Add a root with 'depsweep roots add <path>' or pass a folder.");
        return 2;
    }

    var options = ConsoleUi.Options(config, parse.GetValue(concurrencyOption)) with
    {
        Force = parse.GetValue(forceOption),
        AllowDirty = parse.GetValue(allowDirtyOption),
        DryRun = parse.GetValue(dryRunOption),
    };

    if (options.Force && !options.DryRun)
    {
        ConsoleUi.WriteColored("--force enabled: fixes may include breaking (semver-major) upgrades.", ConsoleColor.Yellow);
    }

    var service = new SweepService(new ProcessRunner());
    var report = await service.FixAsync(projects, options, new ConsoleProgress(quiet: format != "console"), ct);

    switch (format)
    {
        case "md":
            Console.WriteLine(MarkdownReportWriter.Write(report));
            break;
        case "json":
            Console.WriteLine(JsonReportWriter.Write(report));
            break;
        default:
            ConsoleUi.PrintReport(report);
            break;
    }

    if (!options.DryRun)
    {
        var saved = reportStore.Save(report);
        if (format == "console")
        {
            Console.WriteLine($"Report saved: {saved}");
        }
    }

    if (parse.GetValue(outOption) is { } outFile)
    {
        var content = outFile.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonReportWriter.Write(report)
            : MarkdownReportWriter.Write(report);
        File.WriteAllText(outFile.FullName, content);
        Console.Error.WriteLine($"Report written: {outFile.FullName}");
    }

    return ConsoleUi.ExitCode(report.Projects, parse.GetValue(minSeverityOption));
});

// ---- report ----
var reportFormatOption = new Option<string>("--format") { Description = "md or json.", DefaultValueFactory = _ => "md" };
reportFormatOption.AcceptOnlyFromAmong("md", "json");
var reportCommand = new Command("report", "Print the most recent saved fix report.") { reportFormatOption };
reportCommand.SetAction(parse =>
{
    var path = reportStore.Latest("." + parse.GetValue(reportFormatOption));
    if (path is null)
    {
        ConsoleUi.Error($"No saved reports in {reportStore.Directory}. Run 'depsweep fix' first.");
        return 2;
    }

    Console.WriteLine(File.ReadAllText(path));
    Console.Error.WriteLine($"({path})");
    return 0;
});

var root = new RootCommand("DepSweep: audit and fix npm/pnpm vulnerabilities across many projects, then report which repositories to commit.")
{
    rootsCommand, listCommand, scanCommand, fixCommand, reportCommand, excludeCommand, includeCommand,
};

try
{
    return await root.Parse(args).InvokeAsync();
}
catch (InvalidOperationException ex)
{
    ConsoleUi.Error(ex.Message);
    return 2;
}
