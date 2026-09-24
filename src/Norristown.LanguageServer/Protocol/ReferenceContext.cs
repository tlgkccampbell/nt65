namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes what a <c>textDocument/references</c> request wants back.</summary>
/// <param name="IncludeDeclaration">Whether the declaration counts as a reference.</param>
internal sealed record ReferenceContext(bool IncludeDeclaration);
