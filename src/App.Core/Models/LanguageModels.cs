namespace App.Core.Models;

public enum DiagnosticSeverity { Error, Warning, Information, Hint }

public sealed record Diagnostic(TextRange Range, DiagnosticSeverity Severity, string Message, string? Source = null);

public sealed record CompletionItem(string Label, string InsertText, string? Kind = null, string? Detail = null);

public sealed class DiagnosticsPublishedEventArgs(string path, IReadOnlyList<Diagnostic> diagnostics) : EventArgs
{
    public string Path { get; } = path;
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
}
