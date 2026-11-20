namespace App.Core.Models;

public enum AiChatRole { System, User, Assistant }

public sealed record AiChatMessage(AiChatRole Role, string Content);

public sealed record AiCodeContext(
    string? FilePath = null,
    string? Language = null,
    string? SelectedCode = null);

public sealed record AiChatRequest(
    IReadOnlyList<AiChatMessage> Messages,
    AiCodeContext? Context = null);

public sealed record AiChatResponse(string Content, string Model);

public sealed record GitHubPullRequest(
    int Number,
    string Title,
    string State,
    string HeadBranch,
    string BaseBranch,
    string HtmlUrl);

public sealed record CreatePullRequestOptions(
    string Owner,
    string Repository,
    string Title,
    string HeadBranch,
    string BaseBranch,
    string? Body = null);
