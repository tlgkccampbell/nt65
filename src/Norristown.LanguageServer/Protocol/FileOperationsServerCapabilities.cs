namespace Norristown.LanguageServer.Protocol;

/// <summary>The file operations a server takes part in.</summary>
/// <param name="WillRename">
/// Which files the server is to be asked about before they are moved or renamed, so that the
/// edits it returns are applied together with the move; null where it takes no part.
/// </param>
internal sealed record FileOperationsServerCapabilities(FileOperationRegistrationOptions? WillRename);
