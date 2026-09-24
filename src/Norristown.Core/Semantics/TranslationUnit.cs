using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents one ca65 translation unit: a module, every module it places, and every module
/// those place, emitted as one <c>.s</c> file named after the module at the root. A module that
/// no module places and that places nothing is a unit of its own, as most modules are.
/// </summary>
/// <param name="Root">The file of the module whose output the unit is.</param>
/// <param name="Members">
/// Every file of the unit, the root first and then each placed module in the order the unit
/// emits them. Each placed module is followed by the modules it places, before the next one.
/// </param>
public sealed record TranslationUnit(SyntaxTree Root, IReadOnlyList<SyntaxTree> Members)
{
    /// <summary>
    /// Gets a value indicating whether the unit holds more than its root, meaning that some
    /// module is placed in it.
    /// </summary>
    public bool IsPlaced => Members.Count > 1;
}
