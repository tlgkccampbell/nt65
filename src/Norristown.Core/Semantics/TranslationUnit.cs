using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One ca65 translation unit: a module and everything it places, and everything those place,
/// written as one <c>.s</c> named after the module at the root. A module nothing places and
/// that places nothing is a unit of its own, which is what most modules are.
/// </summary>
/// <param name="Root">The file of the module whose output the unit is.</param>
/// <param name="Members">
/// Every file of the unit, the root first and then each placed module in the order the unit
/// writes them: where a module is placed, what it places follows it before the next.
/// </param>
public sealed record TranslationUnit(SyntaxTree Root, IReadOnlyList<SyntaxTree> Members)
{
    /// <summary>Whether the unit holds more than its root, so that anything is placed in it.</summary>
    public bool IsPlaced => Members.Count > 1;
}
