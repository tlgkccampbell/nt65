namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// <c>nt65/outputChanged</c>: analysis has finished after an edit, so any output a client is
/// showing is out of date. It is sent only to a client that has asked for output at least once.
/// It does not say whose output changed, because an edit in one file can change what another
/// file becomes; each view asks again for its own file.
/// </summary>
/// <param name="Uri">The file the edit was in, or null when the change was not an edit.</param>
internal sealed record OutputChangedParams(string? Uri);
