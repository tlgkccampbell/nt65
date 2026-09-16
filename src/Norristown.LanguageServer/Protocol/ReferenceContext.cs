namespace Norristown.LanguageServer.Protocol;

/// <summary>What a references request wants back.</summary>
/// <param name="IncludeDeclaration">Whether the declaration counts as a reference.</param>
internal sealed record ReferenceContext(bool IncludeDeclaration);
