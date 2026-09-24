namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a routine that the routine a request asked about calls.</summary>
/// <param name="To">The called routine.</param>
/// <param name="FromRanges">The ranges in the caller where each call to it appears.</param>
internal sealed record CallHierarchyOutgoingCall(CallHierarchyItem To, IReadOnlyList<Range> FromRanges);
