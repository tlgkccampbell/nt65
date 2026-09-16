namespace Norristown.LanguageServer.Protocol;

/// <summary>A run of lines the client can fold. Both ends are 0-based and included.</summary>
/// <param name="StartLine">The line that stays visible when the range is folded.</param>
/// <param name="EndLine">The last line the range covers.</param>
internal sealed record FoldingRange(int StartLine, int EndLine);
