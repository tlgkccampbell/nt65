namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the context of a <c>textDocument/codeAction</c> request, which gives the diagnostics
/// the client shows at the range and the kinds of action it wants back.
/// </summary>
/// <param name="Diagnostics">The diagnostics the client shows at the range.</param>
/// <param name="Only">The kinds of action the client will show, or null for all kinds.</param>
internal sealed record CodeActionContext(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string>? Only = null);
