namespace Norristown.LanguageServer.Protocol;

/// <summary>One call left as a call in an expansion, and the way to ask for it one level down.</summary>
/// <param name="Line">Which line of the expansion it is on, counting from zero.</param>
/// <param name="Text">The call as it stands, for whatever offers it.</param>
/// <param name="Into">What to send back as <c>into</c> to have this one written out too.</param>
internal sealed record ExpansionLink(int Line, string Text, IReadOnlyList<int> Into);
