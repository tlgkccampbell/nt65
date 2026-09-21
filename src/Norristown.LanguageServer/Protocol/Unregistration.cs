namespace Norristown.LanguageServer.Protocol;

/// <summary>Something a server asked for and is taking back.</summary>
/// <param name="Id">What it was called when it was asked for.</param>
/// <param name="Method">The message it was about.</param>
internal sealed record Unregistration(string Id, string Method);
