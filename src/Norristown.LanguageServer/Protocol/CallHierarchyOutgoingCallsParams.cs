namespace Norristown.LanguageServer.Protocol;

/// <summary>A request for what a routine calls.</summary>
/// <param name="Item">The routine, as a previous request gave it back.</param>
internal sealed record CallHierarchyOutgoingCallsParams(CallHierarchyItem Item);
