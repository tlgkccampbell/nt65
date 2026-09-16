using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>One document the client has open, at the revision the client last sent.</summary>
/// <param name="Uri">The document's URI, which is how the client names it.</param>
/// <param name="Version">The revision this tree was parsed from.</param>
/// <param name="Tree">The document's syntax.</param>
internal sealed record Document(string Uri, int Version, SyntaxTree Tree);
