namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a routine that calls the routine a request asked about.</summary>
/// <param name="From">The calling routine.</param>
/// <param name="FromRanges">The ranges in the caller where each of its calls appears.</param>
internal sealed record CallHierarchyIncomingCall(CallHierarchyItem From, IReadOnlyList<Range> FromRanges);
