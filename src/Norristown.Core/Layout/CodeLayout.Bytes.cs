using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Where the bytes land: which stream the walk is writing into, how far it has filled it,
/// where each line's bytes and each label fall among them, and how many bytes a measured
/// routine or declaration came to.
/// <para>
/// The distance between two positions is known only within one run of a segment's bytes; the
/// branch range check, long-branch sizing and <c>.fallthrough</c> checks all rely on such
/// distances. A segment's bytes form one run across all its regions and blocks, in the order
/// the file writes them, which is how ca65 writes them; an <c>.align</c> or a <c>.place</c>
/// ends the run. A long branch starts short and is lengthened where its target turns
/// out to be out of reach, which moves everything after it, so the file is laid out again until
/// none changes.
/// </para>
/// </summary>
public sealed partial class CodeLayout
{
    /// <summary>The stream the walk is writing into.</summary>
    private int Stream => streams[^1];

    /// <summary>
    /// The run the walk is writing into: the current segment's run, or, for bytes outside
    /// every segment, the current stream's.
    /// </summary>
    private int Measured => segment is { } named ? RunOf(named) : measuredIn.GetValueOrDefault(Stream, Stream);

    /// <summary>Whether a distance is one a branch can reach.</summary>
    private static bool InRange(int reach) => reach is >= -128 and <= 127;

    /// <summary>
    /// A <c>.place</c>, where another module's bytes go. Whatever that module writes comes
    /// between the bytes before the line and the bytes after it, so this file cannot know the
    /// distance from anything before it to anything after it; only the translation unit can
    /// work that out, once every module in it is laid out. A <c>.place</c> written anywhere
    /// but at file level places nothing, and has already been reported.
    /// </summary>
    private void PlaceModule(PlaceDirectiveSyntax directive)
    {
        if (expansion is not null || !Placements.AtFileLevel(directive))
            return;

        // The placed module may write to any segment, so every segment's run ends here.
        foreach (var named in runs.Keys.ToList())
            runs[named] = nextStream++;
        measuredIn[Stream] = nextStream++;
        placePoints.Add(new PlacePoint(directive, steps.Count, Measured, segment));
    }

    /// <summary>The run a segment's bytes are in now, starting one for a segment nothing has written to yet.</summary>
    private int RunOf(string named)
    {
        if (!runs.TryGetValue(named, out var run))
            runs[named] = run = nextStream++;
        return run;
    }

    /// <summary>Records what a line assembles to on this writing of it.</summary>
    private void Laid(StatementSyntax statement, LineLayout laid)
    {
        lines[(statement.Position, expansion)] = laid;
        anyWriting.TryAdd((statement.Tree, statement.Position), laid);
    }

    /// <summary>
    /// Records where a line's bytes land and moves the stream on. An <c>.align</c> starts a
    /// new run of distances instead: how many bytes it generates depends on an address, so
    /// nothing after it stands at a distance nt65 knows from anything before it.
    /// </summary>
    private void Place(StatementSyntax statement, int length)
    {
        var offset = filled.GetValueOrDefault(Measured);
        placements[(statement.Position, expansion)] = new Placement(Measured, offset, length);
        if (length == DataLengths.Unpredictable && segment is { } named)
            runs[named] = nextStream++;
        else if (length == DataLengths.Unpredictable)
            measuredIn[Stream] = nextStream++;
        else
            filled[Measured] = offset + length;
    }

    /// <summary>
    /// A <c>.fallthrough</c>, which generates nothing and is placed where the routine's bytes
    /// end. That is where the routine it names has to start, and its placement is looked up
    /// the same way a label's is.
    /// </summary>
    private void FallsThrough(FallthroughDirectiveSyntax directive)
    {
        placements[(directive.Position, expansion)] = new Placement(Measured, filled.GetValueOrDefault(Measured), 0);
        steps.Add(new Step(directive, expansion, routine, Stream, segment, null));
    }

    /// <summary>
    /// Records where a label stands: at the first byte generated after it, which is the
    /// address a branch to it reaches.
    /// </summary>
    private void Mark(SyntaxNode declaration)
    {
        if (NameOf(declaration) is not { } symbol)
            return;

        // What has an address needs a segment to have one in. A routine or data outside every
        // segment is reported where it is declared, and what is inside them is not reported again.
        if (segment is null && inData == 0
            && (declaration is ProcDeclarationSyntax or MultiProcDeclarationSyntax
                || (routine is null && declaration is DataDeclarationSyntax)))
        {
            Report(declaration.Tree, symbol.NameSpan, Catalogue.OutsideEverySegment.Says($"`{symbol.DisplayName}`"));
        }

        // A label that a macro expansion places outside any routine marks a position in no
        // code. Binding could not report it, because it only sees the macro body as written.
        if (routine is null && inData == 0 && symbol.Kind == SymbolKind.Label && expansion?.NearestCall is not null)
        {
            Report(declaration.Tree, symbol.NameSpan, Catalogue.LabelOutsideARoutine.Says(symbol.DisplayName, ""));
        }
        labels[(symbol, Expansion.Owning(expansion, symbol))] =
            new Placement(Measured, filled.GetValueOrDefault(Measured), 0);
        steps.Add(new Step(declaration, expansion, routine, Stream, segment, symbol));
    }

    /// <summary>
    /// How far a branch reaches: from the instruction after it to its target, or null when
    /// the two are not in one run of a segment's bytes or the target is no label this file placed.
    /// </summary>
    private int? Distance(Branch branch)
    {
        if (placements.GetValueOrDefault((branch.Statement.Position, branch.On)) is not { Length: > 0 } from)
            return null;
        return Located(branch.Target, branch.On) is { } to && to.Stream == from.Stream
            ? to.Offset - from.End
            : null;
    }

    /// <summary>
    /// Where the label an expression names is placed. A macro parameter is resolved to the
    /// argument the call passed, and that argument was written in the caller, so it is looked
    /// up at the caller's expansion level rather than the macro body's.
    /// </summary>
    private Placement? Located(SyntaxNode expression, Expansion? on)
    {
        if (expression is not NameExpressionSyntax || model.SymbolOf(expression) is not { } symbol)
            return null;
        if (symbol.Kind == SymbolKind.MacroParameter)
        {
            return model.GivenAt(symbol, on) is { Argument.Value: { } given, Caller: var caller }
                ? Located(given, caller)
                : null;
        }
        return labels.TryGetValue((symbol, Expansion.Owning(on, symbol)), out var placement)
            ? placement
            : null;
    }

    /// <summary>
    /// Lengthens every long branch this walk found out of reach, and says whether any
    /// changed. A branch only ever grows, so repeating the walk until nothing changes ends.
    /// </summary>
    private bool Lengthen()
    {
        var changed = false;
        foreach (var branch in branches)
        {
            var at = (branch.Statement.Position, branch.On);
            // A long branch whose distance nt65 does not know is always lengthened: nothing
            // shows the target is near enough, and a short branch that cannot reach is wrong.
            if (!branch.Long || lengthened.Contains(at) || Distance(branch) is { } reach && InRange(reach))
                continue;
            lengthened.Add(at);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Reports the short branches that cannot reach their targets. A distance nt65 does not
    /// know is left to ca65, which checks the range itself; a long branch out of reach has
    /// been lengthened rather than reported.
    /// </summary>
    private void CheckBranchRange()
    {
        foreach (var branch in branches)
        {
            if (branch.Long || Distance(branch) is not { } reach || InRange(reach))
                continue;
            var mnemonic = branch.Statement.Mnemonic.Text;
            var longer = "j" + mnemonic[1..];
            var reaches = SyntaxFacts.LongBranches.Contains(longer);
            var fix = reaches ? $". `{longer}` reaches any near target" : "";
            ReportOnLine(branch.Target, branch.On,
                Catalogue.BranchOutOfReach.Says(mnemonic, reach, fix),

                // The change is to the branch, so it is offered only where the branch is
                // written: an expansion's is the macro body's line, which is not this file's.
                reaches && branch.On is null && branch.Statement.Tree == model.Tree
                    ? new DiagnosticFix(FixKind.Branch, longer)
                    : null);
        }
    }

    /// <summary>
    /// Takes the measured spans this walk worked out, and says whether any of them changed.
    /// No length in the layout depends on a span, so one more walk is enough for them to stop
    /// changing.
    /// </summary>
    private bool Settle()
    {
        var changed = false;
        foreach (var symbol in measured)
        {
            var now = extents.TryGetValue(symbol, out var span) ? span : (long?)null;
            var before = settled.TryGetValue(symbol, out var was) ? was : (long?)null;
            if (now == before)
                continue;
            if (now is { } value)
                settled[symbol] = value;
            else
                settled.Remove(symbol);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// A branch whose reach nt65 can check: where it stands, and the target it was written
    /// with. A long branch is here too, because the same distance is what decides its form.
    /// </summary>
    private readonly record struct Branch(InstructionStatementSyntax Statement, Expansion? On, ExpressionSyntax Target, bool Long);
}
