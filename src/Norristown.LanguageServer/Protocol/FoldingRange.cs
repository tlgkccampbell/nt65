namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a range of lines the client can fold. Both line numbers are zero-based and inclusive.
/// </summary>
/// <param name="StartLine">The line that stays visible when the range is folded.</param>
/// <param name="EndLine">The last line the range covers.</param>
internal sealed record FoldingRange(int StartLine, int EndLine);
