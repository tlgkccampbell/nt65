namespace Norristown.LanguageServer.Protocol;

/// <summary>One macro call left unexpanded in an expansion, and how to ask for its expansion one level down.</summary>
/// <param name="Line">Which line of the expansion it is on, counting from zero.</param>
/// <param name="Text">The call as written, for a client to label the link with.</param>
/// <param name="Into">What to send back as <c>into</c> to have this call expanded too.</param>
internal sealed record ExpansionLink(int Line, string Text, IReadOnlyList<int> Into);
