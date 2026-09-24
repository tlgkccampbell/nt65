namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>callHierarchy/outgoingCalls</c> request, which asks what a routine calls.
/// </summary>
/// <param name="Item">The routine, as an earlier request returned it.</param>
internal sealed record CallHierarchyOutgoingCallsParams(CallHierarchyItem Item);
