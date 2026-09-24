namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>nt65/outputChanged</c> notification. The server sends it when analysis
/// finishes after an edit, to tell the client that any output it is showing is out of date. It is
/// sent only to a client that has requested output at least once. It does not identify whose
/// output changed, because an edit in one file can change the output of another; each view
/// requests its own file's output again.
/// </summary>
/// <param name="Uri">The file the edit was in, or null when the change did not come from an edit.</param>
internal sealed record OutputChangedParams(string? Uri);
