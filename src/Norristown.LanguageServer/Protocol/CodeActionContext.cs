namespace Norristown.LanguageServer.Protocol;

/// <summary>The context of a code-action request: what the client shows at the range, and what it wants back.</summary>
/// <param name="Diagnostics">The diagnostics the client shows at the range.</param>
/// <param name="Only">The kinds it will show, or null for all of them.</param>
internal sealed record CodeActionContext(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string>? Only = null);
