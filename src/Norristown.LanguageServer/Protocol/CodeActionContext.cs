namespace Norristown.LanguageServer.Protocol;

/// <summary>What the client asks for where it asks for code actions.</summary>
/// <param name="Diagnostics">The diagnostics it shows there.</param>
/// <param name="Only">The kinds it will show, or null for all of them.</param>
internal sealed record CodeActionContext(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string>? Only = null);
