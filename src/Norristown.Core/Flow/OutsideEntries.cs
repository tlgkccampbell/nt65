using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Finds which of a file's labels control may reach from outside the routine they are in.
/// Another module may jump to an exported label, and this file may name a label from another
/// routine. The analysis treats an instance of the same <see cref="Family"/> as part of the same
/// routine, so a path from one is not from outside.
/// <para>
/// The labels this file names from other routines are worked out the first time any label is
/// asked about, and kept, because the set is the same for every routine in the file.
/// </para>
/// </summary>
public sealed class OutsideEntries
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;

    // The labels this file names from a routine other than the one they are in, worked out the
    // first time any label is asked about and kept for later questions.
    private HashSet<Symbol>? named;

    /// <summary>
    /// Initializes a new instance for the file that <paramref name="layout"/> laid out, as
    /// <paramref name="model"/> bound it.
    /// </summary>
    public OutsideEntries(SemanticModel model, CodeLayout layout)
    {
        this.model = model;
        this.layout = layout;
    }

    /// <summary>
    /// Returns why the stack at such a label is not known. The path from above the label has
    /// pushed bytes that a jump in from outside has not. A <c>.state</c> can declare the processor
    /// state there but has no way to declare what is on the stack, so nothing reconciles the two
    /// paths.
    /// </summary>
    public static Cause Carried(Symbol label, Symbol routine) => new(
        $"`{label.DisplayName}` can be entered from outside `{routine.DisplayName}`, and a jump in has not "
            + "pushed what the path above the label has",
        $"the pushes above `{label.DisplayName}` belong on the same side of it as what reads them, and `args n` "
            + $"on `{routine.DisplayName}` is how a second entry point says what its caller pushed");

    /// <summary>Returns whether control may reach <paramref name="block"/>'s label from outside its routine.</summary>
    public bool Reaches(BasicBlock block)
    {
        if (block.Label is not { } label)
            return false;
        if (label.IsExported)
            return true;
        named ??= Named();
        return named.Contains(label);
    }

    /// <summary>
    /// Returns every label this file names from a routine other than the one the label is in,
    /// whether as the target of a jump into that routine or anywhere else in an operand.
    /// </summary>
    private HashSet<Symbol> Named()
    {
        var found = new HashSet<Symbol>();
        foreach (var step in layout.Steps)
        {
            if (step.Routine is not { } routine)
                continue;
            foreach (var name in step.Statement.DescendantNodes().OfType<NameExpressionSyntax>())
            {
                if (Targets.Of(model, name, step.On)?.Symbol is { Kind: SymbolKind.Label, Routine: { } owner } label
                    && owner != routine && !owner.IsSiblingOf(routine))
                {
                    found.Add(label);
                }
            }
        }
        return found;
    }
}
