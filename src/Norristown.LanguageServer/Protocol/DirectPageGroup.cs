namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the routines that reach memory while D is not known, for one reason.</summary>
/// <param name="Reason">Why D is not known, which is <c>interrupted</c> or <c>unknown</c>.</param>
/// <param name="Routines">The routines.</param>
internal sealed record DirectPageGroup(string Reason, IReadOnlyList<DirectPageUnknownRoutine> Routines);
