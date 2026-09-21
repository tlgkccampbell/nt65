namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// <c>nt65/outputChanged</c>: the program has settled after an edit, so whatever a client is
/// showing of what it became is out of date. It is sent only to a client that has asked for
/// output at least once, and says nothing about which file: an edit in one file changes what
/// another becomes, so a view asks again for its own.
/// </summary>
/// <param name="Uri">The file the edit was in, or null when the change was not an edit.</param>
internal sealed record OutputChangedParams(string? Uri);
