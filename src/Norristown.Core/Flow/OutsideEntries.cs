using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Which of a file's labels control may reach from outside the routine they are in: another
/// module may jump to an exported one, and this file may name one from another routine. An
/// instance of the same family is part of the same routine as far as the analysis goes, so a
/// path from one is not from outside.
/// <para>
/// The labels this file names are worked out the first time one is asked about and kept for
/// the rest of them: the answer is the same question of every routine in the file.
/// </para>
/// </summary>
public sealed class OutsideEntries
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;

    // The labels this file names from a routine other than the one they are in, worked out the
    // first time a label asks and kept for the rest of them.
    private HashSet<Symbol>? named;

    /// <summary>Reads the file <paramref name="layout"/> laid out, as <paramref name="model"/> bound it.</summary>
    public OutsideEntries(SemanticModel model, CodeLayout layout)
    {
        this.model = model;
        this.layout = layout;
    }

    /// <summary>
    /// Why the stack at such a label is not known: the path above it has pushed what a jump in
    /// has not. A <c>.state</c> says what the processor state there is and has no way to say
    /// what is on the stack, so there is nothing to meet the two sides in the middle.
    /// </summary>
    public static Cause Carried(Symbol label, Symbol routine) => new(
        $"`{label.DisplayName}` can be entered from outside `{routine.DisplayName}`, and a jump in has not "
            + "pushed what the path above the label has",
        $"the pushes above `{label.DisplayName}` belong on the same side of it as what reads them, and `args n` "
            + $"on `{routine.DisplayName}` is how a second entry point says what its caller pushed");

    /// <summary>Whether control may reach <paramref name="block"/>'s label from outside its routine.</summary>
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
    /// Every label this file names from a routine other than the one the label is in: a jump
    /// into another routine, and a path naming one as data.
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
