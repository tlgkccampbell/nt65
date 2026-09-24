namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a macro call left unexpanded in an expansion, and how to request its expansion one
/// level down.
/// </summary>
/// <param name="Line">The zero-based line of the expansion that the call is on.</param>
/// <param name="Text">The call as written, which a client uses to label the link.</param>
/// <param name="Into">The value to send back as <c>into</c> to have this call expanded as well.</param>
internal sealed record ExpansionLink(int Line, string Text, IReadOnlyList<int> Into);
