# Changelog

Versions come from git tags (`vX.Y.Z`); see "Versioning and releases" in the README.

## 0.1.0

- Discover npm and pnpm projects under configured folders, then audit them in parallel and fix them from one place
  (safe mode by default, opt-in `--force`).
- Report grouped by git repository, with branch, changed package files, before/after counts, a suggested commit
  message and a `git add` line. DepSweep never commits or pushes.
- CLI (`depsweep.exe`) and WPF app (`DepSweepGui.exe`) share one config.
- App: add several folders at once, drag and drop, an Import dialog to choose projects under parent folders,
  bulk actions on folders and projects, filter, group by repository.
- Application icons, an MSI installer (Start menu shortcut, CLI on `PATH`) and git-tag versioning.
