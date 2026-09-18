namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// How the client would lay a file out. nt65 has one layout and writes it, so these are
/// recorded and not used: a file formatted on one machine is byte for byte what the next one
/// formats it to, and what a diff shows is what somebody wrote.
/// </summary>
/// <param name="TabSize">How many columns the client counts a tab as.</param>
/// <param name="InsertSpaces">Whether the client would indent with spaces.</param>
internal sealed record FormattingOptions(int TabSize = 4, bool InsertSpaces = true);
