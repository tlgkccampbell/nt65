using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Decides which lists and tables only one routine's own jumps dispatch through. A label that
/// such a table names is reached only along the edges the flow graph gives those jumps, so no
/// other state arrives there. A label that any other table names may be entered with whatever
/// state the code that hands control to it has. Both <see cref="FlagAnalysis"/> and
/// <see cref="OutsideEntries"/> ask this class, so that the flag analysis and the register,
/// reads and processor-state analyses agree on which labels a table makes entries.
/// </summary>
/// <param name="model">The file the tables and the jumps are in.</param>
/// <param name="targets">The rule that spreads a <c>.next</c> target into the labels it names.</param>
internal sealed class DispatchTables(SemanticModel model, NextTargets targets)
{
    /// <summary>
    /// Returns whether only one routine's own jumps dispatch through <paramref name="table"/>.
    /// <para>
    /// A list or a table counts only where every item names a label or a routine, so the flow
    /// graph spreads all of it. It must not be exported. Every other name of it in the file must
    /// be one that <paramref name="own"/> accepts, or in an instruction that only reads its bytes,
    /// as <c>lda table,x</c> does. Anything else, such as a call's <c>.next</c> or another
    /// routine's, may hand control to its labels with other registers and flags.
    /// </para>
    /// </summary>
    /// <param name="table">The list or the table a <c>.next</c> names.</param>
    /// <param name="on">The expansion of the statement the <c>.next</c> is under.</param>
    /// <param name="own">
    /// Accepts the span of a name that is in one of the routine's own jumps, or in a
    /// <c>.next</c> under a statement of the routine other than a call.
    /// </param>
    public bool IsDispatchedOnlyBy(Symbol table, Expansion? on, Func<TextSpan, bool> own)
    {
        if (table.IsExported
            || model.ReferencesTo(table).Any(reference => !reference.IsDeclaration && !Touches(reference.Span) && !own(reference.Span)))
        {
            return false;
        }
        var items = targets.ItemsOf(table, on).Count();
        return items > 0 && targets.Spread(table, on).Count() == items;
    }

    /// <summary>
    /// Returns a value indicating whether the name at <paramref name="span"/> is in the operand of
    /// an instruction that transfers control without calling, such as <c>jmp (table,x)</c>.
    /// </summary>
    public bool InJump(TextSpan span) =>
        model.Tree.Root.FindToken(span.Start).Parent?.AncestorsAndSelf().OfType<InstructionStatementSyntax>().FirstOrDefault()
            is { } instruction
        && Instructions.IsControlTransfer(instruction.MnemonicKind) && !Instructions.IsCall(instruction.MnemonicKind);

    /// <summary>
    /// Returns a value indicating whether the name at <paramref name="span"/> names code only to
    /// touch its bytes, as a <c>.patch</c> target does, and as the address of an instruction that
    /// reads or writes memory there does. An immediate is a value that may be jumped to later, so
    /// it does not count.
    /// </summary>
    public bool Touches(TextSpan span)
    {
        foreach (var node in model.Tree.Root.FindToken(span.Start).Parent?.AncestorsAndSelf() ?? [])
        {
            switch (node)
            {
                case PatchDirectiveSyntax:
                    return true;
                case ImmediateOperandSyntax:
                    return false;
                case InstructionStatementSyntax instruction:
                    return !Instructions.IsControlTransfer(instruction.MnemonicKind);
            }
        }
        return false;
    }
}
