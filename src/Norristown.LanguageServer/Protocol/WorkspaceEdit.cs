namespace Norristown.LanguageServer.Protocol;

/// <summary>Changes to make, by document.</summary>
/// <param name="Changes">The edits for each document URI.</param>
internal sealed record WorkspaceEdit(IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> Changes);
