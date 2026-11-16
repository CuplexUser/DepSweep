# DepSweep

Audit and fix npm / pnpm vulnerabilities across many projects from one place, then get a short report of
which git repositories changed so you can review, commit and push them yourself.

- **WPF app** (`DepSweepGui.exe`): pick root folders, select projects, Scan, Fix, then read the per-repository report.
- **CLI** (`depsweep.exe`): the same engine for scripting or CI.
- Both share one config: `%APPDATA%\DepSweep\config.json`.

DepSweep **never commits or pushes**. It only changes `package.json` / lockfiles through the package manager's
own fix command and tells you what changed.

## How it works

1. **Discover**: walks the root folders (skipping `node_modules`, dot-folders, `dist`, `build`, …). A folder is
   audited when it has a `package.json` **and** a lockfile (`package-lock.json` → npm, `pnpm-lock.yaml` → pnpm).
   Workspace members are covered by auditing the workspace root. Folders without a lockfile, or with
   yarn/bun lockfiles, are listed as skipped.
2. **Scan**: `npm audit --json` / `pnpm audit --json` in parallel (default 4 at a time).
3. **Fix**: per project:
   - Skips the project if its package files already have uncommitted changes (override with *Include uncommitted* / `--allow-dirty`).
   - Safe mode: `npm audit fix` / `pnpm audit --fix update` (stays within declared semver ranges).
   - Force mode (opt-in): `npm audit fix --force` / `pnpm audit --fix override` (may apply breaking upgrades).
   - Re-audits, then records which package files changed (by content, via `git diff`).
4. **Report**: grouped by git repository. Each entry shows the branch, changed files, before/after counts,
   remaining issues ("needs --force", "no fix available"), a suggested commit message and a `git add` line.
   Every fix run is saved to `%LOCALAPPDATA%\DepSweep\reports\<timestamp>.md` and `.json`.

## CLI

```text
depsweep roots add <path> [--no-recurse]   # register a folder (recursive by default)
depsweep roots list | remove <path>
depsweep list [paths...]                   # discovered projects, no auditing
depsweep exclude <project> | include <project>

depsweep scan [paths...] [--min-severity high] [--json]
depsweep fix  [paths...] [--force] [--allow-dirty] [--dry-run] [-j 4]
              [--format console|md|json] [--out report.md]
depsweep report [--format md|json]         # print the last saved report
```

With no `paths`, the configured roots are used (minus excluded projects).

Exit codes: `0` nothing at or above `--min-severity` remains; `1` vulnerabilities remain; `2` a project errored
or bad input.

## Build and run

Requires the .NET 10 SDK, plus npm and/or pnpm on `PATH` (git is optional but needed for the repository report).

```powershell
dotnet build
dotnet test
dotnet run --project src/DepSweep.App        # GUI
dotnet run --project src/DepSweep.Cli -- scan  # CLI
```

Publish both as single-file executables into one folder (needs the .NET 10 runtime on the target machine):

```powershell
dotnet publish src/DepSweep.Cli -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish
dotnet publish src/DepSweep.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish
```

## Configuration

`%APPDATA%\DepSweep\config.json` (override the path with `DEPSWEEP_CONFIG`; the reports folder with `DEPSWEEP_REPORT_DIR`):

| Key | Default | Meaning |
|---|---|---|
| `roots` | `[]` | `{ path, recursive, enabled }` folders to search |
| `excludedProjects` | `[]` | project folders left out of runs over the roots |
| `excludedDirectoryNames` | `node_modules`, `dist`, … | folder names never searched |
| `concurrency` | `4` | projects audited/fixed in parallel |
| `timeoutSeconds` | `300` | per command |
| `commitMessageTemplate` | `chore(deps): fix audit vulnerabilities ({fixedSummary})` | placeholders: `{fixedCount}`, `{fixedSummary}`, `{projects}` |

## Notes

- A vulnerable direct dependency pinned to an exact version (`"lodash": "4.17.15"`) is reported as
  *needs --force*: npm will not change an exact pin without `--force`.
- pnpm's `--fix update` may raise the lower bound of ranges in `package.json` (e.g. `^1.2.0` → `^1.2.8`);
  that is still within the original range.
- Debug builds of the GUI support `DEPSWEEP_SNAPSHOT_DIR` for automated UI screenshots (see `DebugSnapshots.cs`).

## Layout

```text
src/DepSweep.Core    discovery, npm/pnpm adapters + audit parsers, git inspection, SweepService, report writers
src/DepSweep.Cli     System.CommandLine front end
src/DepSweep.App     WPF (MVVM, CommunityToolkit.Mvvm, Fluent theme)
tests/               xUnit tests with real npm/pnpm audit JSON fixtures
```
