namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// How the client would lay a file out. nt65 has a single layout and always writes it, so these
/// are accepted but ignored: formatting a file gives byte-identical output on every machine,
/// and a diff shows only what somebody changed, not differences in editor settings.
/// </summary>
/// <param name="TabSize">How many columns the client counts a tab as.</param>
/// <param name="InsertSpaces">Whether the client would indent with spaces.</param>
internal sealed record FormattingOptions(int TabSize = 4, bool InsertSpaces = true);
