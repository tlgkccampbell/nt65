namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/publishDiagnostics</c>: everything now wrong with one document.</summary>
/// <param name="Uri">The document.</param>
/// <param name="Version">The revision the diagnostics were computed from.</param>
/// <param name="Diagnostics">The diagnostics, which replace whatever the client held.</param>
internal sealed record PublishDiagnosticsParams(string Uri, int? Version, IReadOnlyList<Diagnostic> Diagnostics);
