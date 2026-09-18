namespace Norristown.LanguageServer.Protocol;

/// <summary>One routine the one asked about calls.</summary>
/// <param name="To">The callee.</param>
/// <param name="FromRanges">Where in the caller each of the calls is written.</param>
internal sealed record CallHierarchyOutgoingCall(CallHierarchyItem To, IReadOnlyList<Range> FromRanges);
