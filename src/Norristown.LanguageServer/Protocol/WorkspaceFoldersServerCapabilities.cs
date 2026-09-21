namespace Norristown.LanguageServer.Protocol;

/// <summary>What the server does about the folders the client has open.</summary>
/// <param name="Supported">Whether the server works with more than one folder at a time.</param>
/// <param name="ChangeNotifications">Whether the client is to say when the folders change.</param>
internal sealed record WorkspaceFoldersServerCapabilities(bool Supported, bool ChangeNotifications);
