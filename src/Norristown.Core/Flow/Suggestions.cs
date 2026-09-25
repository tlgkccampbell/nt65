using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Finds the places in a file where the same code could be smaller or faster, for an editor to
/// suggest. Nothing here is wrong, so no build reports it. Each suggestion carries the fix that
/// makes the change, and is offered only on a line of the file itself outside every expansion,
/// because a macro body's line serves every call and may be needed by another.
/// <para>
/// The analysis sees only the branches this build takes. A routine with a branch this build
/// leaves out gets no suggestions, because another build may take that branch and need the code.
/// </para>
/// </summary>
public static class Suggestions
{
    /// <summary>
    /// Returns the suggestions for <paramref name="file"/>, in the order they are reported.
    /// <paramref name="omitted"/> holds the branches of the file that this build leaves out, and
    /// <paramref name="readsCallerStack"/> says whether a routine depends on the depth of the stack
    /// it was entered with.
    /// </summary>
    public static IReadOnlyList<Diagnostic> For(
        FileAnalysis file, IReadOnlyList<TextSpan> omitted, Func<Symbol, bool> readsCallerStack)
    {
        var regions = file.Flow.Regions.Where(region => Unconditional(region, omitted)).ToList();
        var found = new List<Diagnostic>();
        found.AddRange(TailCalls(file, regions, readsCallerStack));
        if (file.State is { } states)
            found.AddRange(RedundantWidths(file, regions, states));
        return Norristown.Diagnostics.Ordered(found);
    }

    /// <summary>
    /// Returns whether none of the branches this build leaves out lies within the routine's own
    /// lines.
    /// </summary>
    private static bool Unconditional(FlowRegion region, IReadOnlyList<TextSpan> omitted)
    {
        var own = region.Blocks.SelectMany(block => block.Steps)
            .Where(step => step.On is null && step.Statement.Tree == region.Routine.Tree)
            .Select(step => step.Statement.Span)
            .ToList();
        if (own.Count == 0)
            return true;
        var start = own.Min(span => span.Start);
        var end = own.Max(span => span.End);
        return !omitted.Any(span => span.Start < end && span.End > start);
    }

    /// <summary>
    /// Returns a suggestion for each <c>jsr</c> directly followed by <c>rts</c>, or <c>jsl</c> by
    /// <c>rtl</c>. A jump to the routine does the same, because the routine's own return then goes
    /// straight to this routine's caller. The return is removed where nothing else reaches it, and
    /// kept where a label or another path does.
    /// <para>
    /// A jump leaves the stack one return address shallower than a call does. The suggestion is
    /// therefore made only where this routine has nothing of its own on the stack at the call, and
    /// where the routine called does not depend on the depth of the stack it was entered with.
    /// </para>
    /// </summary>
    private static IEnumerable<Diagnostic> TailCalls(
        FileAnalysis file, IReadOnlyList<FlowRegion> regions, Func<Symbol, bool> readsCallerStack)
    {
        var model = file.Model;
        foreach (var region in regions)
        {
            var blocks = region.Blocks;
            foreach (var block in blocks)
            {
                if (!block.IsReached || block.Next is not null || block.CallsUnknown
                    || block.Calls is not [{ Signature: { } callee } target]
                    || block.Steps is not [.., { Statement: InstructionStatementSyntax call } calling]
                    || !Own(model, calling)
                    || JumpFor(call.MnemonicKind) is not { } jump
                    || callee is { IsInterrupt: true } or { NeverReturns: true } or { Inline: not null } or { Arguments: > 0 }
                    || callee.IsFar != (call.MnemonicKind == MnemonicKind.Jsl)
                    || readsCallerStack(target)
                    || file.Flow.Registers?.Before(call) is not { Stack.Depth: 0 }
                    || block.Index + 1 >= blocks.Count)
                {
                    continue;
                }

                var after = blocks[block.Index + 1];
                if (!after.IsFallenInto || after.Next is not null
                    || after.Steps.FirstOrDefault(step => step.Label is null) is not { Statement: InstructionStatementSyntax returned } returning
                    || !Own(model, returning)
                    || returned.MnemonicKind != (call.MnemonicKind == MnemonicKind.Jsl ? MnemonicKind.Rtl : MnemonicKind.Rts)
                    || !Adjacent(call, returned))
                {
                    continue;
                }

                // A return that a label or another path also reaches has to stay for them.
                var alone = after.Label is null && after.Predecessors.Count == 1;
                var saved = call.MnemonicKind == MnemonicKind.Jsl ? 10 : 9;
                var text = SyntaxFacts.TextOf(call.MnemonicKind);
                yield return new Diagnostic(
                    call.Tree.GetSpan(call.Span),
                    Catalogue.TailCall.Message(
                        $"{text} {target.DisplayName}",
                        SyntaxFacts.TextOf(returned.MnemonicKind),
                        $"{jump} {target.DisplayName}",
                        saved,
                        alone ? " and a byte" : ""))
                {
                    Fix = new DiagnosticFix(FixKind.TailCall, jump, alone ? returned.Tree.GetSpan(returned.Span) : null),
                };
            }
        }
    }

    /// <summary>
    /// Returns whether nothing but blank lines, comments and labels stands between two statements
    /// in the source. The analysis sees only the branches this build takes, and a branch another
    /// build takes may put code between them.
    /// </summary>
    private static bool Adjacent(SyntaxNode first, SyntaxNode second)
    {
        var tree = first.Tree;
        var from = tree.GetSpan(first.Span).LineIndex;
        var to = tree.GetSpan(second.Span).LineIndex;
        for (var line = from + 1; line < to; line++)
        {
            if (tree.GetLine(line).Statement is not (BlankLineSyntax or LabeledLineSyntax { Statement: null }))
                return false;
        }
        return true;
    }

    /// <summary>Returns the jump that makes a tail call in place of a call, or null for anything else.</summary>
    private static string? JumpFor(MnemonicKind mnemonic) => mnemonic switch
    {
        MnemonicKind.Jsr => "jmp",
        MnemonicKind.Jsl => "jml",
        _ => null,
    };

    /// <summary>
    /// Returns a suggestion for each <c>rep</c> or <c>sep</c> that sets a width the register
    /// already has on every path to it. One that changes nothing at all can go, and one that
    /// changes only some of what it names can name less. A statement a family's instances all
    /// share is suggested only where the width is already set in every one of them.
    /// </summary>
    private static IEnumerable<Diagnostic> RedundantWidths(
        FileAnalysis file, IReadOnlyList<FlowRegion> regions, StateAnalysis states)
    {
        var model = file.Model;
        var redundant = new Dictionary<InstructionStatementSyntax, (long Flags, StatusFlags Unneeded)>();
        foreach (var step in regions.SelectMany(region => region.Blocks).SelectMany(block => block.Steps))
        {
            if (step.Statement is not InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rep or MnemonicKind.Sep } statement
                || !Own(model, step)
                || file.Layout.Of(statement, step.On)?.Mode != AddressingMode.Immediate
                || StepOperands.Constant(model, step) is not { } flags)
            {
                continue;
            }
            var unneeded = Unneeded(
                statement.MnemonicKind == MnemonicKind.Rep, flags, states.Before(statement, step.On)?.Processor);

            // Another instance of the same statement keeps only what both find unneeded.
            redundant[statement] = redundant.TryGetValue(statement, out var earlier)
                ? earlier with { Unneeded = earlier.Unneeded & unneeded }
                : (flags, unneeded);
        }

        foreach (var (statement, (flags, unneeded)) in redundant)
        {
            if (unneeded == StatusFlags.None)
                continue;
            var text = statement.GetText().Trim();
            var why = Why(statement.MnemonicKind == MnemonicKind.Rep, unneeded);
            var left = flags & 0xff & ~(long)unneeded;
            if (left == 0)
            {
                yield return new Diagnostic(
                    statement.Tree.GetSpan(statement.Span),
                    Catalogue.WidthAlreadySet.Message(text, "changes nothing", why))
                {
                    Fix = new DiagnosticFix(FixKind.Redundant),
                    IsUnnecessary = true,
                };
                continue;
            }
            var expression = CodeLayout.Expression(statement.Operand!)!;
            var narrowed = StateValue.Hex(left, 2);
            yield return new Diagnostic(
                expression.Tree.GetSpan(expression.Span),
                Catalogue.WidthAlreadySet.Message(text, $"needs only `#{narrowed}`", why))
            {
                Fix = new DiagnosticFix(FixKind.Flags, narrowed),
            };
        }
    }

    /// <summary>
    /// Returns the width flags among <paramref name="flags"/> that a <c>rep</c> or <c>sep</c>
    /// would set to what they already are in <paramref name="state"/>. Only native mode is asked
    /// about, where a width is what M or X says. Flags other than M and X always change something,
    /// and are never unneeded.
    /// </summary>
    private static StatusFlags Unneeded(bool reset, long flags, ProcessorState? state)
    {
        if (state is not { E: ProcessorMode.Native } known)
            return StatusFlags.None;
        var target = reset ? Width.Sixteen : Width.Eight;
        var unneeded = StatusFlags.None;
        if ((flags & (long)StatusFlags.M) != 0 && known.A == target)
            unneeded |= StatusFlags.M;
        if ((flags & (long)StatusFlags.X) != 0 && known.Index == target)
            unneeded |= StatusFlags.X;
        return unneeded;
    }

    /// <summary>
    /// Returns why the <paramref name="unneeded"/> flags of a <c>rep</c> or <c>sep</c> change
    /// nothing, in the words a message uses.
    /// </summary>
    private static string Why(bool reset, StatusFlags unneeded)
    {
        var width = StateChecks.Format(reset ? Width.Sixteen : Width.Eight);
        var registers = (unneeded & (StatusFlags.M | StatusFlags.X)) switch
        {
            StatusFlags.M => "A is",
            StatusFlags.X => "X and Y are",
            _ => "A, X and Y are",
        };
        return $"{registers} already {width} here";
    }

    /// <summary>
    /// Returns whether a step is a line of the file itself, outside every expansion, which is the
    /// only kind of line a suggestion can change.
    /// </summary>
    private static bool Own(SemanticModel model, Step step) => step.On is null && step.Statement.Tree == model.Tree;
}
