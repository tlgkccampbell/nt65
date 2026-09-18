namespace Norristown.LanguageServer.Protocol;

/// <summary>One routine that calls the one asked about.</summary>
/// <param name="From">The caller.</param>
/// <param name="FromRanges">Where in the caller each of its calls is written.</param>
internal sealed record CallHierarchyIncomingCall(CallHierarchyItem From, IReadOnlyList<Range> FromRanges);
