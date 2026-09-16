using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// What the 65816's analysis needs written beside each construct it cannot follow. Every
/// trick has a syntactic fingerprint — an indirect jump, a computed target, a label used as
/// data, a store into code — and each needs an annotation that says what the analysis cannot
/// see: a <c>.next</c> saying where flow goes, a <c>.state</c> declaring the state at a label,
/// or a <c>.patch</c> acknowledging a store. On the 6502 and the 65C02 nothing consumes the
/// state, so none of this is required there.
/// </summary>
internal sealed class Requirements
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly ControlFlow flow;
    private readonly List<Diagnostic> diagnostics = [];

    // Every label that starts a block in some routine, with where it is and whether what it
    // labels is code rather than data.
    private readonly Dictionary<Symbol, Labelled> labels = [];

    // The labels a `.next` names, by the routine the `.next` is written in.
    private readonly HashSet<(Symbol Routine, Symbol Label)> named = [];

    private Requirements(SemanticModel model, CodeLayout layout, ControlFlow flow)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
    }

    /// <summary>Reports each construct in <paramref name="flow"/>'s file that lacks the annotation it needs.</summary>
    public static void Check(SemanticModel model, CodeLayout layout, ControlFlow flow, List<Diagnostic> diagnostics)
    {
        var requirements = new Requirements(model, layout, flow);
        requirements.Collect();
        foreach (var region in flow.Regions)
        {
            foreach (var block in region.Blocks)
                requirements.CheckTail(region, block);
            requirements.CheckEnd(region);
        }
        requirements.CheckUses();
        requirements.CheckExports();
        diagnostics.AddRange(requirements.diagnostics.DistinctBy(d => (d.Span, d.Message)));
    }

    private static bool Is(SyntaxNode statement, params string[] mnemonics) =>
        statement.Kind == SyntaxKind.InstructionStatement && statement.ChildTokens.Length > 0
        && mnemonics.Any(m => statement.ChildTokens[0].Text.Equals(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>The statement as it is written, for a message that quotes it.</summary>
    private static string Quoted(SyntaxNode statement) => $"`{statement.GetText().Trim()}`";

    /// <summary>Which labels there are, and which labels each routine's <c>.next</c> names.</summary>
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
            if (step is { Routine: { } routine, Statement.Kind: SyntaxKind.NextDirective })
            {
                foreach (var target in flow.Named(step.Statement, step.On))
                    named.Add((routine, target.Symbol));
            }
        }
    }

    /// <summary>
    /// Whether a label stands on code: the first thing after it, past any <c>.state</c> and
    /// through any labels that run straight into the next, is an instruction.
    /// </summary>
    private static bool IsCode(IReadOnlyList<BasicBlock> blocks, int index)
    {
        for (var i = index; i < blocks.Count; i++)
        {
            if (i > index && !blocks[i].IsFallenInto)
                return false;
            if (blocks[i].Steps.FirstOrDefault(step => step.Statement.Kind != SyntaxKind.StateDirective)
                is { } first)
            {
                return first.Statement.Kind == SyntaxKind.InstructionStatement;
            }
        }
        return false;
    }

    /// <summary>
    /// What the last statement of a block needs: an indirect or computed transfer a
    /// <c>.next</c>, a return that jumps a <c>.next</c>, and a jump to somewhere a declaration
    /// is needed a declaration.
    /// </summary>
    private void CheckTail(FlowRegion region, BasicBlock block)
    {
        if (block.Steps.Count == 0 || block.Next is not null)
            return;
        var step = block.Steps[^1];
        var statement = step.Statement;
        var mode = layout.Of(statement, step.On)?.Mode;
        switch (Transfers.Of(statement, mode))
        {
            case Transfer.Elsewhere when Is(statement, "jsr", "jsl"):
                Report(statement, $"{Quoted(statement)} calls where its operand points, which the analysis cannot "
                    + "see: `.next` names the routines it calls");
                break;

            case Transfer.Elsewhere:
                Report(statement, $"{Quoted(statement)} goes where its operand points, which the analysis cannot "
                    + "see: `.next` names the labels it reaches, or `.next ?` ends the path");
                break;

            case Transfer.Return when Is(statement, "rts", "rtl") && PushesCode(block):
                Report(statement, $"`{statement.ChildTokens[0].Text.ToLowerInvariant()}` here returns to an address "
                    + "this block pushed, which makes it a jump: `.next` names where it goes");
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

    /// <summary>What a direct transfer needs of the place it names.</summary>
    private void CheckTarget(FlowRegion region, Step step, SyntaxNode statement, AddressingMode? mode, bool calls)
    {
        if (Transfers.TargetOf(statement, mode) is not { } written)
            return;
        var target = Targets.Of(model, written, step.On);

        // A name that names nothing has been reported where it is written; a call to anything
        // but a routine is the state analysis's to report, with the call's other checks.
        if (target is null)
        {
            if (!calls && written.Kind != SyntaxKind.NameExpression)
            {
                Report(statement, $"{Quoted(statement)} goes to a computed address, which the analysis cannot "
                    + "follow: `.next` names the labels it reaches, or `.next ?` ends the path where it is not "
                    + "an instruction boundary");
            }
            return;
        }
        var symbol = target.Value.Symbol;
        if (!symbol.IsAddress)
        {
            if (!calls)
            {
                Report(statement, $"{Quoted(statement)} goes to `{symbol.DisplayName}`, a {symbol.KindText} rather "
                    + "than a label, which the analysis cannot follow: `.next` names the labels it reaches, or "
                    + "`.next ?` ends the path");
            }
            return;
        }
        if (calls)
            return;

        // The label may be in another file, so what routine it is in and whether it is
        // declared are read off the label itself.
        if (symbol is { Kind: SymbolKind.Label, Routine: { } owner, StateDeclaration: null } && owner != region.Routine)
        {
            Report(statement, $"`{symbol.DisplayName}` is inside `{owner.DisplayName}`, and a jump "
                + "into another routine needs the label declared: a `.state` after it says what the state is there");
        }
        if (!labels.TryGetValue(symbol, out var labelled))
            return;
        if (!labelled.IsCode && DataAt(labelled) is { } data
            && (!labelled.Block.IsDeclared || flow.AnnotationsOf(data).All(a => a.Kind != SyntaxKind.NextDirective)))
        {
            Report(statement, $"`{symbol.DisplayName}` labels data, and this jumps to it: the label needs a `.state` "
                + "after it saying what the state is there, and the data a `.next` saying where flow goes");
        }
    }

    /// <summary>The first data a data label stands on, or null when it stands on none.</summary>
    private static Step? DataAt(Labelled labelled)
    {
        var first = labelled.Block.Steps.FirstOrDefault(step => step.Statement.Kind != SyntaxKind.StateDirective);
        return first.Statement?.Kind == SyntaxKind.DataDirective ? first : null;
    }

    /// <summary>
    /// Whether a block pushes the address of code, which is what a return used as a jump
    /// returns to: a push, and an operand in the block that names a label on code or a routine.
    /// </summary>
    private bool PushesCode(BasicBlock block)
    {
        var pushes = false;
        var names = false;
        foreach (var step in block.Steps.Take(block.Steps.Count - 1))
        {
            var statement = step.Statement;
            if (statement.Kind != SyntaxKind.InstructionStatement)
                continue;
            if (Is(statement, "pha", "phx", "phy", "pea", "pei", "per"))
                pushes = true;
            if (statement.ChildNodes.FirstOrDefault() is not { } operand)
                continue;
            foreach (var name in operand.DescendantNodes().Where(node => node.Kind == SyntaxKind.NameExpression))
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
    /// A routine that does not end in a transfer of control runs off its end into whatever is
    /// written after it, which a <c>.next</c> naming that routine says, and is then checked
    /// as a tail call.
    /// </summary>
    private void CheckEnd(FlowRegion region)
    {
        if (!region.IsEntered)
            return;
        foreach (var stream in region.Blocks.GroupBy(block => block.Stream))
            CheckEnd(region, stream.Last(), stream.Key == region.Blocks[0].Stream);
    }

    /// <summary>
    /// The end of one stream of a routine's bytes: its own, which runs into whatever is written
    /// after the routine, or a nested segment block's, which runs into whatever that segment
    /// holds next.
    /// </summary>
    private void CheckEnd(FlowRegion region, BasicBlock last, bool own)
    {
        if (last.Index != 0 && !last.IsFallenInto && last.Predecessors.Count == 0 && !last.IsDeclared)
            return;

        var routine = region.Routine.DisplayName;
        var message = own
            ? $"`{routine}` runs off its end into whatever is written after it: `.next` naming the "
                + "routine it runs into says so, or `.next ?` ends the path"
            : $"`{routine}` runs off the end of a segment block into whatever that segment holds next: "
                + "`.next` says where flow goes, or `.next ?` ends the path";
        if (last.Steps.Count == 0)
        {
            var at = last.Label ?? region.Routine;
            diagnostics.Add(new Diagnostic(at.DeclarationSpan, Severity.Error, message));
            return;
        }
        var step = last.Steps[^1];
        if (last.Next is not null)
            return;
        var transfer = Transfers.Of(step.Statement, layout.Of(step.Statement, step.On)?.Mode);
        var runsOn = transfer is Transfer.Through or Transfer.Branch or Transfer.Call
            || flow.RelativeCallAt(step) is not null
            || transfer == Transfer.Elsewhere && Is(step.Statement, "jsr", "jsl");
        if (runsOn)
            Report(step.Statement, message);
    }

    /// <summary>
    /// Every place a label on code is named other than as the target of a branch, a jump or a
    /// call: a store into it needs a <c>.patch</c>, and any other use makes the label
    /// somewhere flow may arrive unseen, which needs a declaration or a <c>.next</c>.
    /// </summary>
    private void CheckUses()
    {
        foreach (var step in layout.Steps)
        {
            var statement = step.Statement;
            if (step.Label is not null || statement.Kind is not (SyntaxKind.InstructionStatement or SyntaxKind.DataDirective))
                continue;
            if (flow.IsReturnAddress(step))
                continue;
            var mode = statement.Kind == SyntaxKind.InstructionStatement ? layout.Of(statement, step.On)?.Mode : null;
            var direct = statement.Kind == SyntaxKind.InstructionStatement
                && Transfers.Of(statement, mode) is Transfer.Branch or Transfer.Jump or Transfer.Call
                ? Transfers.TargetOf(statement, mode)
                : null;

            foreach (var name in statement.DescendantNodes().Where(node => node.Kind == SyntaxKind.NameExpression))
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
                        .Where(a => a.Kind == SyntaxKind.PatchDirective)
                        .SelectMany(Annotations.TargetsOf)
                        .Any(target => Targets.Of(model, target, step.On)?.Symbol == symbol);
                    if (!patched)
                    {
                        Report(statement, $"{Quoted(statement)} writes into the instruction at "
                            + $"`{symbol.DisplayName}`: `.patch {symbol.DisplayName}` acknowledges it");
                    }
                    continue;
                }

                if (labelled.Block.IsDeclared || named.Contains((labelled.Region.Routine, symbol)))
                    continue;
                Report(name, $"`{symbol.DisplayName}` labels code and is used here as data, so flow may reach it "
                    + "where the analysis cannot see: a `.state` after the label says what the state is there, "
                    + $"or a `.next` in `{labelled.Region.Routine.DisplayName}` naming it carries the state to it");
            }
        }
    }

    /// <summary>
    /// Whether a name is only measured rather than used as an address: inside <c>.sizeof</c>,
    /// <c>.endof</c> or <c>.spanof</c>.
    /// </summary>
    private static bool Measured(SyntaxNode name, SyntaxNode statement)
    {
        for (var node = name.Parent; node is not null && node != statement; node = node.Parent)
        {
            if (node.Kind == SyntaxKind.CallExpression && node.ChildTokens.Length > 0
                && node.ChildTokens[0].Text.ToLowerInvariant() is ".sizeof" or ".endof" or ".spanof")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether an instruction writes to the memory its operand names.</summary>
    private static bool Stores(SyntaxNode statement, AddressingMode? mode) =>
        mode is not (null or AddressingMode.Immediate or AddressingMode.Accumulator or AddressingMode.Implied)
        && Is(statement, "sta", "stx", "sty", "stz", "inc", "dec", "asl", "lsr", "rol", "ror", "tsb", "trb");

    /// <summary>
    /// An exported label inside a routine lets other files jump into it, where this file's
    /// analysis never sees them arrive, so the label has to be declared.
    /// </summary>
    private void CheckExports()
    {
        foreach (var statement in model.Tree.Root.DescendantNodes().Select(node => node.Statement).OfType<SyntaxNode>())
        {
            if (statement.Kind != SyntaxKind.ExportDirective)
                continue;
            foreach (var token in statement.ChildTokens)
            {
                if (model.SymbolAt(token) is not { } symbol || !labels.TryGetValue(symbol, out var labelled)
                    || labelled.Block.IsDeclared)
                {
                    continue;
                }
                diagnostics.Add(new Diagnostic(model.Tree.GetSpan(token.Span), Severity.Error,
                    $"`{symbol.DisplayName}` is inside `{labelled.Region.Routine.DisplayName}`, and exporting it lets "
                    + "other files jump into the routine: a `.state` after the label says what the state is there"));
            }
        }
    }

    private void Report(SyntaxNode node, string message) =>
        diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message));

    /// <summary>A label that starts a block: the region and block it starts, and whether it stands on code.</summary>
    private readonly record struct Labelled(FlowRegion Region, BasicBlock Block, bool IsCode);
}
