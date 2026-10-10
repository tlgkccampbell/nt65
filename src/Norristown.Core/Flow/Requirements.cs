using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Checks that each construct the analysis cannot follow has the annotation it needs beside it.
/// Each such construct can be recognized from its syntax, such as an indirect jump, a computed
/// target, a label used as data, or a store into code. Each needs an annotation that says what
/// the analysis cannot see. A <c>.next</c> says where flow goes, a <c>.state</c> declares the
/// state at a label, and a <c>.patch</c> acknowledges a store. What each routine reads and keeps
/// depends on the paths through it on every CPU, so the same annotations are required on every
/// CPU.
/// </summary>
internal sealed class Requirements
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly ControlFlow flow;
    private readonly List<Diagnostic> diagnostics = [];

    // Every label that starts a block in some routine, with where it is and whether what it
    // labels is code rather than data.
    private readonly Dictionary<Symbol, Labeled> labels = [];

    // The labels a `.next` names, keyed by the routine the `.next` is in.
    private readonly HashSet<(Symbol Routine, Symbol Label)> named = [];

    // Where data that code runs into is already reported, and the body line beside a report at a
    // macro call. A routine that ends in such data is not reported as running off its end too,
    // because the two describe the same edge.
    private readonly HashSet<Span> runIntoData;

    private Requirements(SemanticModel model, CodeLayout layout, ControlFlow flow, HashSet<Span> runIntoData)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
        this.runIntoData = runIntoData;
    }

    /// <summary>
    /// Reports each construct in <paramref name="flow"/>'s file that lacks the annotation it needs.
    /// <paramref name="diagnostics"/> holds what the other flow checks found, and receives these.
    /// </summary>
    public static void Check(SemanticModel model, CodeLayout layout, ControlFlow flow, List<Diagnostic> diagnostics)
    {
        var requirements = new Requirements(model, layout, flow,
            [.. diagnostics.Where(d => d.Id == Catalog.RunsIntoData.Id)
                .SelectMany(d => d.Related.Select(related => related.Span).Prepend(d.Span))]);
        requirements.Collect();
        foreach (var region in flow.Regions)
        {
            foreach (var block in region.Blocks)
                requirements.CheckTail(block);
            requirements.CheckEnd(region);
        }
        requirements.CheckUses();
        requirements.CheckPadding();
        diagnostics.AddRange(requirements.diagnostics.Unrepeated());
    }

    /// <summary>Returns the statement's source text in backticks, for a message that quotes it.</summary>
    private static string Quoted(SyntaxNode statement) => $"`{statement.GetText().Trim()}`";

    /// <summary>
    /// Returns the message for a routine that runs off the end of its own stream, when
    /// <paramref name="own"/> is set, or off the end of a nested segment block. Where the last
    /// statement is a conditional <paramref name="branch"/>, the message offers the <c>.next</c>
    /// that says it is always taken.
    /// </summary>
    private static DiagnosticMessage RunsOff(string routine, bool own, bool branch)
    {
        var otherwise = own
            ? "add a `.fallthrough` naming the routine it runs into"
            : "add a `.next` saying where flow goes";
        var fix = branch
            ? "where the branch is always taken, add a `.next` naming its own target; otherwise " + otherwise
            : otherwise + ", or use `.next ?` where that cannot be named";
        return own
            ? Catalog.RoutineRunsOffTheEnd.Message(routine, "its end", "is emitted after it", fix)
            : Catalog.RoutineRunsOffTheEnd.Message(routine, "the end of a segment block", "that segment holds next", fix);
    }

    /// <summary>
    /// Returns whether a label stands on code. It does when the first thing after it, past any
    /// <c>.state</c> and through any labels that run straight into the next, is an instruction.
    /// A <c>.label</c> inside an instruction stands on code too. Its block holds only the
    /// <c>.label</c> itself, whose <see cref="HiddenPath"/> runs as the instructions its bytes
    /// decode as.
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
                return first is InstructionStatementSyntax or LabelDirectiveSyntax;
            }
        }
        return false;
    }

    /// <summary>Returns the first data statement a data label labels, or null when it labels none.</summary>
    private static Step? DataAt(Labeled labeled)
    {
        var first = labeled.Block.Steps.FirstOrDefault(step => step.Statement is not StateDirectiveSyntax);
        return first.Statement is DataDirectiveSyntax or DataValuesSyntax ? first : null;
    }

    /// <summary>
    /// Returns whether control may enter the run of blocks that falls through into a block. A
    /// label opens a block even where nothing ends the one before it, so a run of data labels is a
    /// chain of blocks that only fall into each other. Control enters the chain where a block in it
    /// is the routine's entry, is declared, or is reached by anything other than falling in.
    /// </summary>
    private static bool Enters(IReadOnlyList<BasicBlock> blocks, BasicBlock last)
    {
        for (var block = last; ; block = blocks[block.Index - 1])
        {
            if (block.Index == 0 || block.IsDeclared
                || block.Predecessors.Any(from => !block.IsFallenInto || from != block.Index - 1))
            {
                return true;
            }
            if (!block.IsFallenInto)
                return false;
        }
    }

    /// <summary>
    /// Returns how source inside <paramref name="from"/> names <paramref name="routine"/>. That is
    /// its own name where the two are declared in one scope, and its qualified name anywhere else.
    /// </summary>
    private static string Named(Symbol routine, Symbol from) =>
        routine.Scope == from.Scope ? routine.Name : routine.QualifiedName;

    /// <summary>
    /// Returns whether a name is only measured rather than used as an address, as it is inside
    /// <c>.sizeof</c>, <c>.countof</c>, <c>.endof</c>, <c>.spanof</c>, <c>.addrsize</c>,
    /// <c>.mincycles</c> or <c>.maxcycles</c>, or asked which bank it is in, as it is inside
    /// <c>.bankof</c>.
    /// </summary>
    private static bool Measured(NameExpressionSyntax name, SyntaxNode statement)
    {
        for (var node = name.Parent; node is not null && node != statement; node = node.Parent)
        {
            if (node is CallExpressionSyntax
                {
                    BuiltinKind: BuiltinKind.Sizeof or BuiltinKind.Countof or BuiltinKind.Endof or BuiltinKind.Spanof
                        or BuiltinKind.Addrsize or BuiltinKind.Mincycles or BuiltinKind.Maxcycles or BuiltinKind.Bankof,
                })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns whether an instruction reads or writes the memory its operand names. An immediate
    /// operand, the address that <c>pea</c> or <c>per</c> pushes, and the target of a branch, a
    /// jump or a call are values rather than memory the instruction accesses.
    /// </summary>
    private static bool Accesses(SyntaxNode statement, AddressingMode? mode) =>
        statement is InstructionStatementSyntax { MnemonicKind: not (MnemonicKind.Pea or MnemonicKind.Per) }
        && Mnemonic(statement).Control == Control.Through
        && mode is not (null or AddressingMode.Immediate or AddressingMode.Accumulator or AddressingMode.Implied);

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

    /// <summary>Records which labels there are, and which labels each routine's <c>.next</c> names.</summary>
    private void Collect()
    {
        foreach (var region in flow.Regions)
        {
            var blocks = region.Blocks;
            foreach (var block in blocks)
            {
                if (block.Label is { Kind: SymbolKind.Label } label && !labels.ContainsKey(label))
                    labels[label] = new Labeled(region, block, IsCode(blocks, block.Index));
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
    /// Reports a diagnostic where the last statement of a block lacks what it needs. An indirect
    /// or computed transfer needs a <c>.next</c>, and so does a return used as a jump. A jump to a
    /// label that has to be declared needs that declaration.
    /// </summary>
    private void CheckTail(BasicBlock block)
    {
        if (block.Steps.Count == 0 || block.Next is not null)
            return;
        var step = block.Steps[^1];
        if (step.Statement is not InstructionStatementSyntax statement)
            return;
        var mode = layout.Of(statement, step.On)?.Mode;
        switch (Transfers.Of(statement, mode))
        {
            case Transfer.Elsewhere when Instructions.IsCall(statement.MnemonicKind):
                Report(statement, step.On, Catalog.IndirectCallUnchecked.Message(Quoted(statement)));
                break;

            case Transfer.Elsewhere:
                Report(statement, step.On, Catalog.IndirectJumpUnchecked.Message(Quoted(statement)), EndPath(step));
                break;

            case Transfer.Return when statement.MnemonicKind is MnemonicKind.Rts or MnemonicKind.Rtl && PushesCode(block):
                Report(statement, step.On, Catalog.PushedReturnUnchecked.Message(SyntaxFacts.TextOf(statement.MnemonicKind)));
                break;

            case Transfer.Jump or Transfer.Branch when flow.RelativeCallAt(step) is null:
                CheckTarget(step, statement, mode, calls: false);
                break;

            case Transfer.Call:
                CheckTarget(step, statement, mode, calls: true);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Reports a conditional branch whose target is computed rather than named. A target written
    /// as an offset from the branch, such as <c>*+4</c>, is placed against the routine's layout.
    /// Where it lands on the start of an instruction of the same routine, the fix labels that
    /// instruction and branches to the label. Where it lands inside an instruction or outside the
    /// routine, the message says so and no fix is offered.
    /// </summary>
    private void ReportComputedBranch(Step step, InstructionStatementSyntax statement, SyntaxNode target)
    {
        if (OffsetFromBranch(target, step.On) is not { } offset)
        {
            Report(statement, step.On, Catalog.ComputedBranchUnchecked.Message(
                Quoted(statement), "a computed address", "write the label it goes to as its operand"));
            return;
        }
        var written = $"`{target.GetText().Trim()}`, an offset from the branch rather than a label";
        var landing = Landing(step, offset);
        var why = landing switch
        {
            { Inside: { } inside, Into: var into } => $"it lands {(into == 1 ? "1 byte" : $"{into} bytes")} into "
                + $"`{inside.Statement.GetText().Trim()}`, where no instruction starts, so name that position with a "
                + "`.label` and branch to it",
            { Start: { } start, Label: { } label } => $"write `{label.DisplayName}`, the label of the instruction it "
                + $"lands on, `{start.Statement.GetText().Trim()}`, as its operand",
            { Start: { } start } => $"label the instruction it lands on, `{start.Statement.GetText().Trim()}`, and branch "
                + "to the label",
            _ when step.Routine is { } routine => $"it lands outside `{routine.DisplayName}`, so write the label of the "
                + "code there as its operand",
            _ => "write the label it goes to as its operand",
        };
        DiagnosticFix? fix = null;
        if (landing.Start is { } instruction && step.On is null && statement.Tree == model.Tree
            && instruction.On is null && instruction.Statement.Tree == model.Tree)
        {
            fix = landing.Label is { } label
                ? new DiagnosticFix(FixKind.LandingLabel, label.DisplayName)
                : new DiagnosticFix(FixKind.LandingLabel, FreeLabel(step.Routine),
                    instruction.Statement.Tree.GetSpan(instruction.Statement.Span));
        }
        Report(statement, step.On, Catalog.ComputedBranchUnchecked.Message(Quoted(statement), written, why), fix);
    }

    /// <summary>
    /// Returns the number of bytes from a branch's first byte to its target, for a target written
    /// as <c>*</c> plus or minus a constant, or null for any other target.
    /// </summary>
    private long? OffsetFromBranch(SyntaxNode target, Expansion? on)
    {
        switch (target)
        {
            case CurrentAddressExpressionSyntax:
                return 0;
            case ParenthesizedExpressionSyntax parenthesized:
                return OffsetFromBranch(parenthesized.Expression, on);
            case BinaryExpressionSyntax { Left: CurrentAddressExpressionSyntax } binary
                when binary.OperatorToken.Kind is SyntaxKind.Plus or SyntaxKind.Minus
                && model.ValueOf(binary.Right, on).AsNumber() is { } distance:
                return binary.OperatorToken.Kind == SyntaxKind.Plus ? distance : -distance;
            case BinaryExpressionSyntax { Right: CurrentAddressExpressionSyntax } binary
                when binary.OperatorToken.Kind == SyntaxKind.Plus && model.ValueOf(binary.Left, on).AsNumber() is { } distance:
                return distance;
            default:
                return null;
        }
    }

    /// <summary>
    /// Returns where a branch's target lands among the steps of its own routine, given as the
    /// number of bytes from the branch's first byte.
    /// </summary>
    private BranchLanding Landing(Step branch, long offset)
    {
        if (branch.Routine is not { } routine || layout.PositionOf(branch.Statement, branch.On) is not { } from)
            return default;
        var at = from.Offset + offset;
        var landing = default(BranchLanding);
        foreach (var step in layout.Steps)
        {
            if (step.Routine != routine)
                continue;
            if (step.Label is { Kind: SymbolKind.Label } label)
            {
                if (step.On is null && layout.PositionOf(label) is { } labeled
                    && labeled.Stream == from.Stream && labeled.Offset == at)
                {
                    landing = landing with { Label = label };
                }
                continue;
            }
            if (layout.PositionOf(step.Statement, step.On) is not { } position || position.Stream != from.Stream)
                continue;
            if (step.Statement is InstructionStatementSyntax && position.Length > 0)
            {
                if (position.Offset == at)
                    return landing with { Start = step };
                if (position.Offset < at && at < position.End)
                    return new BranchLanding(Inside: step, Into: (int)(at - position.Offset));
            }
        }
        return default;
    }

    /// <summary>
    /// Returns a cheap local label that <paramref name="routine"/> does not declare yet, for a fix
    /// that labels an instruction in it.
    /// </summary>
    private string FreeLabel(Symbol? routine)
    {
        var taken = model.Symbols.Where(symbol => symbol.IsCheapLocal && symbol.Routine == routine)
            .Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);
        var name = "skip";
        for (var n = 2; taken.Contains(name); n++)
            name = $"skip{n}";
        return "@" + name;
    }

    /// <summary>
    /// Reports a diagnostic where a direct transfer's target is computed, is not a label, or is
    /// data the transfer is not declared to reach.
    /// </summary>
    private void CheckTarget(Step step, InstructionStatementSyntax statement, AddressingMode? mode, bool calls)
    {
        if (Transfers.TargetOf(statement, mode) is not { } targetExpression)
            return;
        var target = Targets.Of(model, targetExpression, step.On);

        // A branch always goes to its operand or on to the next statement, so its target can
        // always be written as a label, and a `.next ?` under it is an error.
        var branch = Transfers.Of(statement, mode) == Transfer.Branch;

        // A jump to a constant address, such as a ROM entry point, is a tail call to a routine
        // nothing is known about, as a call to one is a call to such a routine. The flow analysis
        // already ends the path there and keeps nothing across it, so it needs no annotation.
        if (!calls && !branch && Targets.IsConstantAddress(model, targetExpression, step.On))
            return;

        // A name that resolves to nothing has already been reported where it appears. A call to
        // anything but a routine is reported by the state analysis, with the call's other checks.
        if (target is null)
        {
            if (!calls && targetExpression is not NameExpressionSyntax)
            {
                if (branch)
                    ReportComputedBranch(step, statement, targetExpression);
                else
                    Report(statement, step.On, Catalog.ComputedJumpUnchecked.Message(Quoted(statement)), EndPath(step));
            }
            return;
        }
        var symbol = target.Value.Symbol;
        if (!symbol.IsAddress)
        {
            if (!calls)
            {
                Report(statement, step.On, Catalog.JumpTargetNotALabel.Message(
                    Quoted(statement), symbol.DisplayName, symbol.KindPhrase,
                    branch ? "write the label it goes to as its operand"
                        : "add a `.next` naming the labels it reaches, or `.next ?` where they cannot be named"),
                    branch ? null : EndPath(step));
            }
            return;
        }
        if (calls)
            return;
        if (!labels.TryGetValue(symbol, out var labeled))
            return;
        if (!labeled.IsCode && DataAt(labeled) is { } data
            && (!labeled.Block.IsDeclared || flow.AnnotationsOf(data).All(a => a is not NextDirectiveSyntax)))
        {
            Report(statement, step.On, Catalog.JumpIntoData.Message(symbol.DisplayName));
        }
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
                    && (symbol.Signature is not null || labels.TryGetValue(symbol, out var labeled) && labeled.IsCode))
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
        if (!Enters(region.Blocks, last))
            return;

        var routine = region.Routine.DisplayName;
        if (last.Steps.Count == 0)
        {
            var at = last.Label ?? region.Routine;
            diagnostics.Add(new Diagnostic(at.DeclarationSpan, RunsOff(routine, own, branch: false)) { Fix = RunsInto(region, own) });
            return;
        }

        // A `.next` on the last statement says where flow goes, so the routine is not reported
        // even where the statement is a call, which returns and so runs on. Data that code runs
        // into is reported already, as the same edge.
        var step = last.Steps[^1];
        if (last.Next is not null || !last.RunsOn
            || last.Steps.Any(each => runIntoData.Contains(each.Statement.Tree.GetSpan(each.Statement.Span))))
        {
            return;
        }

        // A routine that ends in a conditional branch nearly always means the branch is taken,
        // so the `.next` naming its own target comes first. A `.next ?` is an error after a
        // branch, so it is not offered there.
        var runsInto = RunsInto(region, own);
        var statement = step.Statement as InstructionStatementSyntax;
        var mode = statement is null ? null : layout.Of(statement, step.On)?.Mode;
        var branch = statement is not null && flow.RelativeCallAt(step) is null
            && Transfers.Of(statement, mode) == Transfer.Branch;
        var taken = branch && step.On is null && statement!.Tree == model.Tree
            && Transfers.TargetOf(statement, mode) is { } target
                ? new DiagnosticFix(FixKind.AlwaysTaken, target.GetText().Trim(), statement.Tree.GetSpan(statement.Span))
                : null;

        // Where the last statement is a line of a macro body, the call that ends the routine is
        // what runs off, and the `.fallthrough` the fix adds still goes at the routine's end.
        var found = Expansion.BodyLine(step.Statement, step.On, model.Tree) is var (call, _, _)
            ? new Diagnostic(call.Tree.GetSpan(call.Span), RunsOff(routine, own, branch),
                [new RelatedSpan(step.Statement.Tree.GetSpan(step.Statement.Span), Expansion.InTheMacroBody)])
            : new Diagnostic(step.Statement.Tree.GetSpan(step.Statement.Span), RunsOff(routine, own, branch));
        diagnostics.Add(found with
        {
            Fix = taken ?? runsInto ?? (branch ? null : EndPath(step)),
            Also = taken is null ? null : runsInto,
        });
    }

    /// <summary>
    /// Returns the fix that adds a <c>.fallthrough</c> naming the routine emitted after
    /// <paramref name="region"/>, or null where that routine is not known. Finding it searches the
    /// whole layout, so it is looked for only for a routine that runs off its end.
    /// </summary>
    private DiagnosticFix? RunsInto(FlowRegion region, bool own) =>
        own && flow.EmittedAfter(region) is { } next
            ? new DiagnosticFix(FixKind.Fallthrough, Named(next.Routine, region.Routine), next.Closer)
            : null;

    /// <summary>
    /// Reports each place a label on code is named, other than as the target of a branch, a jump
    /// or a call, without the annotation it needs. A store into the label needs a <c>.patch</c>,
    /// and an instruction that only reads the label's bytes needs nothing. Any other use hands
    /// out the label's address, so flow may arrive at the label without the analysis seeing it,
    /// which needs a declaration or a <c>.next</c>.
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
                    || !labels.TryGetValue(symbol, out var labeled) || !labeled.IsCode)
                {
                    continue;
                }

                if (Stores(statement, mode))
                {
                    var patched = flow.AnnotationsOf(step)
                        .Where(a => a is PatchDirectiveSyntax)
                        .SelectMany(Annotations.TargetsOf)
                        .Any(target => Targets.Of(model, target, step.On)?.Symbol == symbol);
                    if (!patched && !flow.CoveredStores.Contains(step.Key))
                    {
                        Report(statement, step.On, Catalog.SelfModifyingUnchecked.Message(
                            Quoted(statement), symbol.DisplayName, symbol.DisplayName));
                    }
                    continue;
                }

                // An instruction that reads the bytes at the label, rather than handing out its
                // address, gives nothing a way to jump there.
                if (Accesses(statement, mode))
                    continue;
                if (labeled.Block.IsDeclared || named.Contains((labeled.Region.Routine, symbol)))
                    continue;

                // The path from a `.label` inside an instruction starts inside the instruction's
                // bytes, where no `.state` can stand, so only a `.next` is offered for it.
                var routine = labeled.Region.Routine.DisplayName;
                if (labeled.Block.Steps is [var first, ..] && layout.HiddenPathAt(first) is not null)
                {
                    Report(name, step.On, Catalog.CodeLabelAsData.Message(
                        symbol.DisplayName, $"name it in a `.next` in `{routine}`"));
                    continue;
                }
                Report(name, step.On, Catalog.CodeLabelAsData.Message(
                        symbol.DisplayName, $"add a `.state` after the label, or name it in a `.next` in `{routine}`"),
                    new DiagnosticFix(FixKind.State, At: symbol.DeclarationSpan));
            }
        }
    }

    /// <summary>
    /// Reports each <c>.next</c> that follows a <c>.res</c> or <c>.align</c> in a routine whose
    /// <see cref="PaddingFill"/> does not run on. Execution never runs through such padding to the
    /// labels the <c>.next</c> names.
    /// </summary>
    private void CheckPadding()
    {
        foreach (var step in layout.Steps)
        {
            if (step is not { Routine: not null, Label: null, Statement: DataDirectiveSyntax padding }
                || PaddingFill.Of(step, model) is not { } fill || fill.RunsOn(layout.Cpu))
            {
                continue;
            }
            foreach (var next in flow.AnnotationsOf(step).OfType<NextDirectiveSyntax>())
                Report(next, step.On, Catalog.NextAfterPadding.Message(padding.GetText().Trim(), fill.WhyNot()));
        }
    }

    /// <summary>
    /// Reports a problem with <paramref name="node"/>, found in the expansion <paramref name="on"/>.
    /// A line of a macro body is reported at the call that expanded it, which is the side that
    /// chose to expand it there, with the body line as a note. Such a report has no fix, since a
    /// fix would change a line that every call expands.
    /// </summary>
    private void Report(SyntaxNode node, Expansion? on, DiagnosticMessage message, DiagnosticFix? fix = null) =>
        diagnostics.Add(Expansion.Problem(model.Tree, node, on, Severity.Error, message, fix));

    /// <summary>
    /// Returns a fix that adds a <c>.next ?</c> after the step's statement, or null where the
    /// programmer cannot add it there. The fix is offered only in this file and not in a macro body,
    /// where a <c>.next ?</c> would give up the path of every call.
    /// </summary>
    private DiagnosticFix? EndPath(Step step) =>
        step.On is null && step.Statement.Tree == model.Tree ? new DiagnosticFix(FixKind.EndPath) : null;

    /// <summary>
    /// Represents where a branch's target lands among the steps of its routine. Every part is
    /// unset where the target lands nowhere in the routine.
    /// </summary>
    /// <param name="Start">The instruction whose first byte the target is, or null.</param>
    /// <param name="Label">A label written in the file at the target, which a fix can name, or null.</param>
    /// <param name="Inside">The instruction the target lands inside, past its first byte, or null.</param>
    /// <param name="Into">How many bytes into that instruction the target lands.</param>
    private readonly record struct BranchLanding(Step? Start = null, Symbol? Label = null, Step? Inside = null, int Into = 0);

    /// <summary>
    /// Represents a label that starts a block, with the region and block it starts and whether it
    /// labels code.
    /// </summary>
    private readonly record struct Labeled(FlowRegion Region, BasicBlock Block, bool IsCode);
}
