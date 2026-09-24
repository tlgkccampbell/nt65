namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>callHierarchy/incomingCalls</c> request, which asks what calls a routine.
/// </summary>
/// <param name="Item">The routine, as an earlier request returned it.</param>
internal sealed record CallHierarchyIncomingCallsParams(CallHierarchyItem Item);
