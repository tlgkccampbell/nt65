using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Finds which of a file's labels control may reach from outside the routine's own paths.
/// Another module may jump to an exported label, this file may name a label from another
/// routine, and a call to a label enters it from wherever the call is made, even inside the same
/// routine. The analysis treats an instance of the same <see cref="Family"/> as part of the same
/// routine, so a jump from one is not from outside.
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
    public static Cause UnknownStack(Symbol label, Symbol routine) => new(
        $"`{label.DisplayName}` can be entered from outside `{routine.DisplayName}`, and a jump to it has not "
            + "pushed what the path above the label pushes",
        $"move the pushes above `{label.DisplayName}` to the same side of it as the code that reads them; on "
            + $"`{routine.DisplayName}`, `pushed n` declares what the caller pushed beneath the return address, and "
            + "`pulls n` the bytes handed above it");

    /// <summary>
    /// Returns whether control may reach <paramref name="block"/>'s label other than along its
    /// routine's own paths. Only a label inside a routine counts, because the routine's own name is
    /// where its callers are meant to enter it.
    /// </summary>
    public bool Reaches(BasicBlock block)
    {
        if (block.Label is not { Kind: SymbolKind.Label } label)
            return false;
        if (label.IsExported)
            return true;
        named ??= Named();
        return named.Contains(label);
    }

    /// <summary>
    /// Returns every label this file names from a routine other than the one the label is in,
    /// whether as the target of a jump into that routine or anywhere else in an operand, and every
    /// label this file calls. A call enters the label with whatever state the caller has, so the
    /// label is an entry point even when its own routine makes the call.
    /// </summary>
    private HashSet<Symbol> Named()
    {
        var found = new HashSet<Symbol>();
        foreach (var step in layout.Steps)
        {
            if (step.Routine is not { } routine)
                continue;
            var calls = step.Statement is InstructionStatementSyntax instruction
                && Instructions.Facts(instruction.MnemonicKind).Control == Control.Calls;
            foreach (var name in step.Statement.DescendantNodes().OfType<NameExpressionSyntax>())
            {
                if (Targets.Of(model, name, step.On)?.Symbol is { Kind: SymbolKind.Label, Routine: { } owner } label
                    && (calls || (owner != routine && !owner.IsSiblingOf(routine))))
                {
                    found.Add(label);
                }
            }
        }
        return found;
    }
}
