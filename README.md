# DepSweep

Audit and fix npm / pnpm vulnerabilities across many projects from one place, then get a short report of
which git repositories changed so you can review, commit and push them yourself.

- **WPF app** (`DepSweepGui.exe`): add folders and projects (several at once), check projects, Scan, Fix, then read
  the per-repository report.
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

## Using the app

- **Add folders…** opens a multi-select folder picker. A folder with a lockfile is added as a *Project*; any other
  folder is added as a *Search folder* whose sub-folders are searched. You can also **drop folders** on the window.
- **Import…** lets you pick (or paste, or drop) one or more parent folders, lists every npm/pnpm project inside them
  with its repository, and adds the ones you check. You can add them individually or keep the parent folders
  as search folders.
- In the folders list, use Ctrl/Shift-click to highlight several entries, then right-click to enable, disable,
  remove or switch between *Search folder* and *Project* (or press Delete to remove).
- In the projects grid, filter by text or status, **Group by repository**, and use Ctrl/Shift-click plus Space (or
  right-click) to check, uncheck, scan or fix the highlighted rows. **Scan checked** / **Fix checked** act on the
  checked projects.

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

## Install

Download or build `DepSweep-<version>-win-x64.msi` and run it. It installs both executables to
`C:\Program Files\DepSweep`, adds a Start menu shortcut, and adds the folder to the system `PATH` so `depsweep`
works in any new terminal. Installing a newer version upgrades in place; uninstall from *Apps & features*.
The standard MSI needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

## Build and run

Requires the .NET 10 SDK, plus npm and/or pnpm on `PATH` (git is optional but needed for the repository report).

```powershell
dotnet build
dotnet test
dotnet run --project src/DepSweep.App        # GUI
dotnet run --project src/DepSweep.Cli -- scan  # CLI
```

Build the installer (publishes both single-file executables, then packages them with WiX 5; the WiX SDK is
restored from NuGet, nothing to install):

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1                 # needs .NET 10 runtime on target
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -SelfContained  # bundles the runtime
# -> artifacts\DepSweep-<version>-win-x64.msi   (executables alone: artifacts\publish\)
```

## Versioning and releases

The version comes from **git tags** via [MinVer](https://github.com/adamralph/minver); there is no version
number to edit in the code.

- The latest tag `vX.Y.Z` is the version of every build until the next tag. With no tag at all it is `0.1.0`.
- The commit hash is kept as build metadata. The app shows `v0.1.0 (5f021f7)`, `depsweep --version` prints
  `0.1.0+<sha>`, and reports record the version that produced them.
- Release:

  ```powershell
  git tag v0.2.0
  git push origin v0.2.0
  powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
  ```

- The MSI uses the same `major.minor.patch`. Bump at least the patch for each release you distribute, because
  Windows Installer only upgrades to a higher version. Rebuilding the same version replaces it.
- Record user-visible changes in `CHANGELOG.md`.

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
- The icons are generated by `tools\make-icons.ps1` (Windows PowerShell, System.Drawing). Re-run it after changing it.

## Layout

```text
src/DepSweep.Core    discovery, npm/pnpm adapters + audit parsers, git inspection, SweepService, report writers
src/DepSweep.Cli     System.CommandLine front end
src/DepSweep.App     WPF (MVVM, CommunityToolkit.Mvvm, Fluent theme)
tests/               xUnit tests with real npm/pnpm audit JSON fixtures
installer/           WiX 5 MSI project (Package.wxs, License.rtf)
tools/               build-installer.ps1, make-icons.ps1
```
