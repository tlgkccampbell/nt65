namespace Norristown.LanguageServer.Protocol;

/// <summary>The file operations a server takes part in.</summary>
/// <param name="WillRename">
/// What it is asked about before a file is moved, so that the edits it answers go with the
/// move, or null where it takes no part.
/// </param>
internal sealed record FileOperationsServerCapabilities(FileOperationRegistrationOptions? WillRename);
