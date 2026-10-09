namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one store into code that the caret's line takes part in.</summary>
/// <param name="Store">The whole line of the store, on one line.</param>
/// <param name="Target">The whole line of the instruction the store writes into, on one line.</param>
/// <param name="Name">The label the <c>.patch</c> names, as it is written.</param>
/// <param name="Variants">
/// The instructions the <c>.patch</c> lists after <c>as</c>, or an empty list where it lists none.
/// </param>
internal sealed record PatchSpan(Range Store, Range Target, string Name, IReadOnlyList<string> Variants);
