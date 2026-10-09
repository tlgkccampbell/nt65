using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents one store into code that <see cref="PatchLinks"/> reports. A <c>.patch</c> under
/// the store names the instruction the store writes into, and may list what it turns it into.
/// </summary>
/// <param name="Store">The span of the store, in the caret's file.</param>
/// <param name="Target">The span of the instruction the store writes into, in the caret's file.</param>
/// <param name="Name">The label the <c>.patch</c> names, as it is written.</param>
/// <param name="Variants">
/// The instructions the <c>.patch</c> lists after <c>as</c>, as they are written, or an empty list
/// where it lists none and so says nothing about what is written.
/// </param>
public sealed record PatchLink(TextSpan Store, TextSpan Target, string Name, IReadOnlyList<string> Variants);
