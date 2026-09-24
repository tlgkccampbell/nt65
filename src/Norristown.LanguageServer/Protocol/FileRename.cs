namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a file that is about to move.</summary>
/// <param name="OldUri">The file's current URI.</param>
/// <param name="NewUri">The file's URI after the move.</param>
internal sealed record FileRename(string OldUri, string NewUri);
