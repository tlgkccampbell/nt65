namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes the file operations the server takes part in.</summary>
/// <param name="WillRename">
/// The files the client asks the server about before moving or renaming them, so that the edits
/// the server returns are applied together with the move; null if the server takes no part.
/// </param>
internal sealed record FileOperationsServerCapabilities(FileOperationRegistrationOptions? WillRename);
