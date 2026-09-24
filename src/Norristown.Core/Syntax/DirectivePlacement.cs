namespace Norristown.Syntax;

/// <summary>
/// Describes where a directive may begin a line. The binder reports a directive it finds where
/// its placement rules it out, and an editor offers a directive only where its placement allows
/// it, so both read the same facts.
/// </summary>
/// <param name="Contexts">The kinds of body the directive may appear in.</param>
/// <param name="Barred">What may not surround the directive, whatever its context.</param>
/// <param name="Required">What must surround the directive, whatever its context.</param>
public readonly record struct DirectivePlacement(
    DirectiveContexts Contexts, DirectiveNesting Barred = DirectiveNesting.None,
    DirectiveNesting Required = DirectiveNesting.None)
{
    /// <summary>
    /// Checks whether the directive may begin a line in <paramref name="context"/> with
    /// <paramref name="nesting"/> around it.
    /// </summary>
    public bool Allows(DirectiveContexts context, DirectiveNesting nesting) =>
        (Contexts & context) != 0 && (Barred & nesting) == 0 && (Required & ~nesting) == 0;

    /// <summary>Checks whether <paramref name="nesting"/> includes anything the directive may not be under.</summary>
    public bool IsBarredBy(DirectiveNesting nesting) => (Barred & nesting) != 0;
}
