namespace Norristown.LanguageServer.Protocol;

/// <summary>A request for what calls a routine.</summary>
/// <param name="Item">The routine, as a previous request gave it back.</param>
internal sealed record CallHierarchyIncomingCallsParams(CallHierarchyItem Item);
