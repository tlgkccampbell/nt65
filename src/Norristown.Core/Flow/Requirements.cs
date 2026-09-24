using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Checks that each construct the 65816's analysis cannot follow has the annotation it needs
/// beside it. Each such construct can be recognised from its syntax, such as an indirect jump, a
/// computed target, a label used as data, or a store into code. Each needs an annotation that
/// says what the analysis cannot see. A <c>.next</c> says where flow goes, a <c>.state</c>
/// declares the state at a label, and a <c>.patch</c> acknowledges a store. On the 6502 and its
/// CMOS variants nothing depends on the processor state, so none of this is required there, and
/// a routine that runs off its end is only a warning.
/// </summary>
internal sealed class Requirements
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly ControlFlow flow;
    private readonly List<Diagnostic> diagnostics = [];

    // The severity of running off the end of a routine. On the 65816 the analysis would pass
    // the state on to whatever comes next, and elsewhere running off is only likely a mistake.
    private readonly Severity runningOff;

    // Every label that starts a block in some routine, with where it is and whether what it
    // labels is code rather than data.
    private readonly Dictionary<Symbol, Labelled> labels = [];

    // The labels a `.next` names, keyed by the routine the `.next` is in.
    private readonly HashSet<(Symbol Routine, Symbol Label)> named = [];

    private Requirements(SemanticModel model, CodeLayout layout, ControlFlow flow, Severity runningOff)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
        this.runningOff = runningOff;
    }

    /// <summary>Reports each construct in <paramref name="flow"/>'s file that lacks the annotation it needs.</summary>
    public static void Check(SemanticModel model, CodeLayout layout, ControlFlow flow, List<Diagnostic> diagnostics)
    {
        var requirements = new Requirements(model, layout, flow, Severity.Error);
        requirements.Collect();
        foreach (var region in flow.Regions)
        {
            foreach (var block in region.Blocks)
                requirements.CheckTail(region, block);
            requirements.CheckEnd(region);
        }
        requirements.CheckUses();
        requirements.CheckExports();
        diagnostics.AddRange(requirements.diagnostics.DistinctBy(d => (d.Span, d.Id, d.Message)));
    }

    /// <summary>
    /// Reports a warning for each routine in <paramref name="flow"/>'s file that runs off its end,
    /// which is the only check a CPU without the 65816's analysis needs. A <c>.fallthrough</c>
    /// declares that the routine meant to run on.
    /// </summary>
    public static void CheckEnds(SemanticModel model, CodeLayout layout, ControlFlow flow, List<Diagnostic> diagnostics)
    {
        var requirements = new Requirements(model, layout, flow, Severity.Warning);
        foreach (var region in flow.Regions)
            requirements.CheckEnd(region);
        diagnostics.AddRange(requirements.diagnostics.DistinctBy(d => (d.Span, d.Id, d.Message)));
    }

    /// <summary>Returns the statement's source text in backticks, for a message that quotes it.</summary>
    private static string Quoted(SyntaxNode statement) => $"`{statement.GetText().Trim()}`";

    /// <summary>Records which labels there are, and which labels each routine's <c>.next</c> names.</summary>
    private void Collect()
    {
        foreach (var region in flow.Regions)
        {
            var blocks = region.Blocks;
            foreach (var block in blocks)
            {
                if (block.Label is { Kind: SymbolKind.Label } label && !labels.ContainsKey(label))
                    labels[label] = new Labelled(region, block, IsCode(blocks, block.Index));
            }
        }
        foreach (var step in layout.Steps)
        {
            if (step is { Routine: { } routine, Statement: NextDirectiveSyntax next })
            {
                foreach (var target in flow.Named(next, step.On))
                    named.Add((routine, target.Symbol));
            }
        }
    }

    /// <summary>
    /// Returns whether a label stands on code. It does when the first thing after it, past any
    /// <c>.state</c> and through any labels that run straight into the next, is an instruction.
    /// </summary>
    private static bool IsCode(IReadOnlyList<BasicBlock> blocks, int index)
    {
        for (var i = index; i < blocks.Count; i++)
        {
            if (i > index && !blocks[i].IsFallenInto)
                return false;
            // Step is a struct, so the search runs over the statements instead, where
            // FirstOrDefault gives null for a block of nothing but `.state` lines.
            if (blocks[i].Steps
                    .Select(step => step.Statement)
                    .FirstOrDefault(statement => statement is not StateDirectiveSyntax) is { } first)
            {
                return first is InstructionStatementSyntax;
            }
        }
        return false;
    }

    /// <summary>
    /// Reports a diagnostic where the last statement of a block lacks what it needs. An indirect
    /// or computed transfer needs a <c>.next</c>, and so does a return used as a jump. A jump to a
    /// label that has to be declared needs that declaration.
    /// </summary>
    private void CheckTail(FlowRegion region, BasicBlock block)
    {
        if (block.Steps.Count == 0 || block.Next is not null)
            return;
        var step = block.Steps[^1];
        if (step.Statement is not InstructionStatementSyntax statement)
            return;
        var mode = layout.Of(statement, step.On)?.Mode;
        switch (Transfers.Of(statement, mode))
        {
            case Transfer.Elsewhere when Mnemonic(statement).Control == Control.Calls:
                Report(statement, Catalogue.IndirectCallUnchecked.Message(Quoted(statement)));
                break;

            case Transfer.Elsewhere:
                Report(statement, Catalogue.IndirectJumpUnchecked.Message(Quoted(statement)), EndPath(step));
                break;

            case Transfer.Return when statement.MnemonicKind is MnemonicKind.Rts or MnemonicKind.Rtl && PushesCode(block):
                Report(statement, Catalogue.PushedReturnUnchecked.Message(SyntaxFacts.TextOf(statement.MnemonicKind)));
                break;

            case Transfer.Jump or Transfer.Branch when flow.RelativeCallAt(step) is null:
                CheckTarget(region, step, statement, mode, calls: false);
                break;

            case Transfer.Call:
                CheckTarget(region, step, statement, mode, calls: true);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Reports a diagnostic where a direct transfer's target is computed, is not a label, is
    /// another routine's undeclared label, or is data the transfer is not declared to reach.
    /// </summary>
    private void CheckTarget(FlowRegion region, Step step, InstructionStatementSyntax statement, AddressingMode? mode, bool calls)
    {
        if (Transfers.TargetOf(statement, mode) is not { } targetExpression)
            return;
        var target = Targets.Of(model, targetExpression, step.On);

        // A name that resolves to nothing has already been reported where it appears. A call to
        // anything but a routine is reported by the state analysis, with the call's other checks.
        if (target is null)
        {
            if (!calls && targetExpression is not NameExpressionSyntax)
            {
                Report(statement, Catalogue.ComputedJumpUnchecked.Message(Quoted(statement)), EndPath(step));
            }
            return;
        }
        var symbol = target.Value.Symbol;
        if (!symbol.IsAddress)
        {
            if (!calls)
            {
                Report(statement, Catalogue.JumpTargetNotALabel.Message(
                    Quoted(statement), symbol.DisplayName, symbol.KindPhrase), EndPath(step));
            }
            return;
        }
        if (calls)
            return;

        // The label may be in another file, so what routine it is in and whether it is
        // declared are read off the label itself.
        if (symbol is { Kind: SymbolKind.Label, Routine: { } owner, StateDeclaration: null }
            && owner != region.Routine && !owner.IsSiblingOf(region.Routine))
        {
            Report(statement, Catalogue.EntryNotDeclared.Message(symbol.DisplayName, owner.DisplayName),
                new DiagnosticFix(FixKind.State, At: symbol.DeclarationSpan));
        }
        if (!labels.TryGetValue(symbol, out var labelled))
            return;
        if (!labelled.IsCode && DataAt(labelled) is { } data
            && (!labelled.Block.IsDeclared || flow.AnnotationsOf(data).All(a => a is not NextDirectiveSyntax)))
        {
            Report(statement, Catalogue.JumpIntoData.Message(symbol.DisplayName));
        }
    }

    /// <summary>Returns the first data statement a data label labels, or null when it labels none.</summary>
    private static Step? DataAt(Labelled labelled)
    {
        var first = labelled.Block.Steps.FirstOrDefault(step => step.Statement is not StateDirectiveSyntax);
        return first.Statement is DataDirectiveSyntax or DataValuesSyntax ? first : null;
    }

    /// <summary>
    /// Returns whether a block pushes the address of code, which is what a return used as a jump
    /// returns to. The block must contain a push, and an operand that names a label on code or a
    /// routine.
    /// </summary>
    private bool PushesCode(BasicBlock block)
    {
        var pushes = false;
        var names = false;
        foreach (var step in block.Steps.Take(block.Steps.Count - 1))
        {
            if (step.Statement is not InstructionStatementSyntax statement)
                continue;
            if (statement.MnemonicKind is MnemonicKind.Pha or MnemonicKind.Phx or MnemonicKind.Phy
                or MnemonicKind.Pea or MnemonicKind.Pei or MnemonicKind.Per)
                pushes = true;
            if (statement.Operand is not { } operand)
                continue;
            foreach (var name in operand.DescendantNodes().OfType<NameExpressionSyntax>())
            {
                if (Targets.Of(model, name, step.On)?.Symbol is { } symbol
                    && (symbol.Signature is not null || labels.TryGetValue(symbol, out var labelled) && labelled.IsCode))
                {
                    names = true;
                }
            }
        }
        return pushes && names;
    }

    /// <summary>
    /// Reports a diagnostic for each stream of an entered routine that runs off its end. A routine
    /// that does not end in a transfer of control runs off its end into whatever is emitted after
    /// it. A <c>.fallthrough</c> naming the routine it runs into declares that this is intended,
    /// and the routine is then checked as a tail call.
    /// </summary>
    private void CheckEnd(FlowRegion region)
    {
        if (!region.IsEntered)
            return;
        foreach (var stream in region.Blocks.GroupBy(block => block.Stream))
            CheckEnd(region, stream.Last(), stream.Key == region.Blocks[0].Stream);
    }

    /// <summary>
    /// Reports a diagnostic where the end of one stream of a routine's bytes runs on. The stream
    /// is either the routine's own, which runs into whatever is emitted after the routine, or a
    /// nested segment block's, which runs into whatever that segment holds next.
    /// </summary>
    private void CheckEnd(FlowRegion region, BasicBlock last, bool own)
    {
        if (last.Index != 0 && !last.IsFallenInto && last.Predecessors.Count == 0 && !last.IsDeclared)
            return;

        var routine = region.Routine.DisplayName;
        var message = own
            ? Catalogue.RoutineRunsOffTheEnd.Message(
                routine, "its end", "is written after it",
                "add a `.fallthrough` naming the routine it runs into")
            : Catalogue.RoutineRunsOffTheEnd.Message(
                routine, "the end of a segment block", "that segment holds next", "add a `.next` saying where flow goes");

        // Where the routine emitted next is known, the fix names it. Anywhere else the fix is a
        // `.next ?`, which ends the path without claiming anything about what comes next.
        var after = own ? flow.EmittedAfter(region) : null;
        var runsInto = after is { } next
            ? new DiagnosticFix(FixKind.Fallthrough, Named(next.Routine, region.Routine), next.Closer)
            : null;
        if (last.Steps.Count == 0)
        {
            var at = last.Label ?? region.Routine;
            diagnostics.Add(new Diagnostic(at.DeclarationSpan, runningOff, message) { Fix = runsInto });
            return;
        }
        var step = last.Steps[^1];
        if (last.Next is not null || step.Statement is FallthroughDirectiveSyntax)
            return;
        var transfer = Transfers.Of(step.Statement, layout.Of(step.Statement, step.On)?.Mode);
        var runsOn = (transfer is Transfer.Through or Transfer.Branch or Transfer.Call
            || flow.RelativeCallAt(step) is not null
            || transfer == Transfer.Elsewhere && Mnemonic(step.Statement).Control == Control.Calls)
            && !flow.CallsWhatNeverReturns(step);
        if (runsOn)
        {
            diagnostics.Add(new Diagnostic(step.Statement.Tree.GetSpan(step.Statement.Span), runningOff, message)
            {
                Fix = runsInto ?? EndPath(step),
            });
        }
    }

    /// <summary>
    /// Returns how source inside <paramref name="from"/> names <paramref name="routine"/>. That is
    /// its own name where the two are declared in one scope, and its qualified name anywhere else.
    /// </summary>
    private static string Named(Symbol routine, Symbol from) =>
        routine.Scope == from.Scope ? routine.Name : routine.QualifiedName;

    /// <summary>
    /// Reports each place a label on code is named, other than as the target of a branch, a jump
    /// or a call, without the annotation it needs. A store into the label needs a <c>.patch</c>.
    /// Any other use means flow may arrive at the label without the analysis seeing it, which
    /// needs a declaration or a <c>.next</c>.
    /// </summary>
    private void CheckUses()
    {
        foreach (var step in layout.Steps)
        {
            var statement = step.Statement;
            if (step.Label is not null
                || statement is not (InstructionStatementSyntax or DataDirectiveSyntax or DataValuesSyntax))
                continue;
            if (flow.IsReturnAddress(step))
                continue;
            var mode = statement is InstructionStatementSyntax ? layout.Of(statement, step.On)?.Mode : null;
            var direct = statement is InstructionStatementSyntax
                && Transfers.Of(statement, mode) is Transfer.Branch or Transfer.Jump or Transfer.Call
                ? Transfers.TargetOf(statement, mode)
                : null;

            foreach (var name in statement.DescendantNodes().OfType<NameExpressionSyntax>())
            {
                if (name == direct || Measured(name, statement)
                    || Targets.Of(model, name, step.On)?.Symbol is not { } symbol
                    || !labels.TryGetValue(symbol, out var labelled) || !labelled.IsCode)
                {
                    continue;
                }

                if (Stores(statement, mode))
                {
                    var patched = flow.AnnotationsOf(step)
                        .Where(a => a is PatchDirectiveSyntax)
                        .SelectMany(Annotations.TargetsOf)
                        .Any(target => Targets.Of(model, target, step.On)?.Symbol == symbol);
                    if (!patched)
                    {
                        Report(statement, Catalogue.SelfModifyingUnchecked.Message(
                            Quoted(statement), symbol.DisplayName, symbol.DisplayName));
                    }
                    continue;
                }

                if (labelled.Block.IsDeclared || named.Contains((labelled.Region.Routine, symbol)))
                    continue;
                Report(name, Catalogue.CodeLabelAsData.Message(symbol.DisplayName, labelled.Region.Routine.DisplayName),
                    new DiagnosticFix(FixKind.State, At: symbol.DeclarationSpan));
            }
        }
    }

    /// <summary>
    /// Returns whether a name is only measured rather than used as an address, as it is inside
    /// <c>.sizeof</c>, <c>.endof</c> or <c>.spanof</c>.
    /// </summary>
    private static bool Measured(NameExpressionSyntax name, SyntaxNode statement)
    {
        for (var node = name.Parent; node is not null && node != statement; node = node.Parent)
        {
            if (node is CallExpressionSyntax { Function: { } function }
                && function.Text.ToLowerInvariant() is ".sizeof" or ".endof" or ".spanof")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Returns whether an instruction stores to the memory its operand names.</summary>
    private static bool Stores(SyntaxNode statement, AddressingMode? mode) =>
        mode is not (null or AddressingMode.Immediate or AddressingMode.Accumulator or AddressingMode.Implied)
        && Mnemonic(statement).Stores;

    /// <summary>
    /// Returns the facts about the statement's instruction, or <see cref="InstructionFacts.None"/>
    /// where the statement is not an instruction.
    /// </summary>
    private static InstructionFacts Mnemonic(SyntaxNode statement) =>
        statement is InstructionStatementSyntax instruction
            ? Instructions.Facts(instruction.MnemonicKind)
            : InstructionFacts.None;

    /// <summary>
    /// Reports each exported label inside a routine that is not declared. An exported label lets
    /// other files jump into the routine, where this file's analysis never sees them arrive, so the
    /// label has to be declared.
    /// </summary>
    private void CheckExports()
    {
        foreach (var symbol in model.Symbols)
        {
            if (symbol is not { IsExported: true, ExportSpan: { } at } || !labels.TryGetValue(symbol, out var labelled)
                || labelled.Block.IsDeclared)
            {
                continue;
            }
            diagnostics.Add(new Diagnostic(model.Tree.GetSpan(at),
                Catalogue.ExportedEntryNotDeclared.Message(symbol.DisplayName, labelled.Region.Routine.DisplayName))
            {
                Fix = new DiagnosticFix(FixKind.State, At: symbol.DeclarationSpan),
            });
        }
    }

    private void Report(SyntaxNode node, DiagnosticMessage message, DiagnosticFix? fix = null) =>
        diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message) { Fix = fix });

    /// <summary>
    /// Returns a fix that adds a <c>.next ?</c> after the step's statement, or null where the
    /// programmer cannot add it there. The fix is offered only in this file and not in a macro body,
    /// where a <c>.next ?</c> would end the path of every call.
    /// </summary>
    private DiagnosticFix? EndPath(Step step) =>
        step.On is null && step.Statement.Tree == model.Tree ? new DiagnosticFix(FixKind.EndPath) : null;

    /// <summary>
    /// Represents a label that starts a block, with the region and block it starts and whether it
    /// labels code.
    /// </summary>
    private readonly record struct Labelled(FlowRegion Region, BasicBlock Block, bool IsCode);
}
