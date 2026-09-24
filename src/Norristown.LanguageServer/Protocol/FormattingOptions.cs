namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the client's formatting preferences. nt65 has a single layout and always uses it, so
/// these options are accepted but ignored. Formatting a file therefore gives byte-identical output
/// on every machine, and a diff shows only what somebody changed, not differences in editor
/// settings.
/// </summary>
/// <param name="TabSize">The number of columns the client counts a tab as.</param>
/// <param name="InsertSpaces">Whether the client would indent with spaces.</param>
internal sealed record FormattingOptions(int TabSize = 4, bool InsertSpaces = true);
