using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepSweep.Core.Config;
using DepSweep.Core.Models;
using DepSweep.Core.Reporting;
using DepSweep.Core.Processes;

namespace DepSweep.App.ViewModels;

public sealed record RepoProjectLine(string Name, string RelativePath, ProjectStatus Status, string Counts, string? Message);

/// <summary>One repository in the report: what changed and what to commit.</summary>
public sealed partial class RepoCardViewModel(RepoGroup group) : ObservableObject
{
    public RepoGroup Group { get; } = group;

    public string Title => Group.DisplayName;

    public string? Branch => Group.Branch;

    public string? RepoRoot => Group.RepoRoot;

    public bool HasChanges => Group.HasChanges;

    public bool InGit => Group.RepoRoot is not null;

    public IReadOnlyList<string> ChangedFiles => Group.ChangedFiles;

    public string? CommitMessage => Group.SuggestedCommitMessage;

    public string? GitAddCommand => Group.GitAddCommand;

    public string Summary => Group.HasChanges
        ? $"Fixed {Group.Fixed.ToSummary()} · remaining {Group.After.ToSummary()}"
        : $"No changes · {Group.After.ToSummary()} remaining";

    public IReadOnlyList<RepoProjectLine> Projects => Group.Projects
        .Select(p => new RepoProjectLine(
            p.Project.Name,
            Group.RepoRoot is null ? p.Project.Path : PathUtil.ToRelativeForwardSlash(Group.RepoRoot, p.Project.Path),
            p.Status,
            p.After is not null ? $"{p.Before!.Counts.ToCompact()} → {p.After.Counts.ToCompact()}" : p.Before?.Counts.ToCompact() ?? "–",
            p.Message))
        .ToList();

    private string FolderToOpen => Group.RepoRoot ?? Group.Projects.FirstOrDefault()?.Project.Path ?? "";

    [RelayCommand]
    private void CopyCommitMessage()
    {
        if (CommitMessage is not null)
        {
            Clipboard.SetText(CommitMessage);
        }
    }

    [RelayCommand]
    private void CopyGitAdd()
    {
        if (GitAddCommand is not null)
        {
            Clipboard.SetText(GitAddCommand);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Directory.Exists(FolderToOpen))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FolderToOpen}\"") { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        if (!Directory.Exists(FolderToOpen))
        {
            return;
        }

        // Prefer Windows Terminal when installed, otherwise PowerShell.
        if (ExecutableResolver.Resolve("wt") is { } wt)
        {
            Process.Start(new ProcessStartInfo(wt) { ArgumentList = { "-d", FolderToOpen }, UseShellExecute = false });
        }
        else
        {
            var shell = ExecutableResolver.Resolve("pwsh") ?? "powershell.exe";
            Process.Start(new ProcessStartInfo(shell) { WorkingDirectory = FolderToOpen, UseShellExecute = true });
        }
    }
}
