namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>textDocument/publishDiagnostics</c> notification, which carries every
/// current diagnostic for one document.
/// </summary>
/// <param name="Uri">The document.</param>
/// <param name="Version">The version the diagnostics were computed from.</param>
/// <param name="Diagnostics">The diagnostics, which replace any the client already holds.</param>
internal sealed record PublishDiagnosticsParams(string Uri, int? Version, IReadOnlyList<Diagnostic> Diagnostics);
