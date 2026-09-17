namespace Norristown.LanguageServer.Protocol;

/// <summary>The diagnostics the client shows where it asks for code actions.</summary>
internal sealed record CodeActionContext(IReadOnlyList<Diagnostic> Diagnostics);
