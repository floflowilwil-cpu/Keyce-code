using System;
using App.Application.Git;
using App.Core.Contracts;
using CommunityToolkit.Mvvm.Input;

namespace App.Desktop.ViewModels;

/// <summary>ViewModel Avalonia du panneau Git (Source Control).</summary>
public sealed class GitPanelViewModel : ViewModelBase
{
    public GitPanelViewModel(IGitService gitService)
    {
        ArgumentNullException.ThrowIfNull(gitService);
        Controller = new GitPanelController(gitService);

        RefreshCommand = new AsyncRelayCommand(ct => Controller.RefreshAsync(ct));
        StageCommand = new AsyncRelayCommand<string>(
            (path, ct) => Controller.StageAsync(path ?? string.Empty, ct));
        UnstageCommand = new AsyncRelayCommand<string>(
            (path, ct) => Controller.UnstageAsync(path ?? string.Empty, ct));
        StageAllCommand = new AsyncRelayCommand(ct => Controller.StageAllAsync(ct));
        CommitCommand = new AsyncRelayCommand(ct => Controller.CommitAsync(ct));
        CheckoutBranchCommand = new AsyncRelayCommand<string>(
            (branch, ct) => Controller.CheckoutBranchAsync(branch ?? string.Empty, ct));
        CreateBranchCommand = new AsyncRelayCommand(ct => Controller.CreateNewBranchAsync(ct));
        MergeBranchCommand = new AsyncRelayCommand<string>(
            (branch, ct) => Controller.MergeBranchAsync(branch ?? string.Empty, ct));
        AbortMergeCommand = new AsyncRelayCommand(ct => Controller.AbortMergeAsync(ct));
        LoadDiffCommand = new AsyncRelayCommand<string>(
            (path, ct) => Controller.LoadDiffAsync(path, ct));
        PushCommand = new AsyncRelayCommand(ct => Controller.PushAsync(ct));
        PullCommand = new AsyncRelayCommand(ct => Controller.PullAsync(ct));
    }

    public GitPanelController Controller { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<string> StageCommand { get; }
    public IAsyncRelayCommand<string> UnstageCommand { get; }
    public IAsyncRelayCommand StageAllCommand { get; }
    public IAsyncRelayCommand CommitCommand { get; }
    public IAsyncRelayCommand<string> CheckoutBranchCommand { get; }
    public IAsyncRelayCommand CreateBranchCommand { get; }
    public IAsyncRelayCommand<string> MergeBranchCommand { get; }
    public IAsyncRelayCommand AbortMergeCommand { get; }
    public IAsyncRelayCommand<string> LoadDiffCommand { get; }
    public IAsyncRelayCommand PushCommand { get; }
    public IAsyncRelayCommand PullCommand { get; }
}
