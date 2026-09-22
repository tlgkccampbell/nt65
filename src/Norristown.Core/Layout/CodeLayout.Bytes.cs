using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Where the bytes land: which stream the walk is writing into, how far it has filled it,
/// where each line's bytes and each label stand among them, and how much room a measured
/// routine or declaration came to.
/// <para>
/// A distance is known only within one stream, which is what branch range reads. A long
/// branch starts short and is lengthened where its target turns out to be out of reach,
/// which moves everything after it, so the file is laid out again until none changes.
/// </para>
/// </summary>
public sealed partial class CodeLayout
{
    /// <summary>The stream the walk is writing into.</summary>
    private int Stream => streams[^1];

    /// <summary>The run of the stream the walk is writing into whose distances are known.</summary>
    private int Measured => measuredIn.GetValueOrDefault(Stream, Stream);

    /// <summary>Whether a distance is one a branch can reach.</summary>
    private static bool InRange(int reach) => reach is >= -128 and <= 127;

    /// <summary>
    /// A <c>.place</c>, where another module's bytes go. Whatever that module writes stands
    /// between the bytes before the line and the bytes after it, so nothing after it is at a
    /// distance this file knows from anything before it: what is known across it is the
    /// translation unit's to say, once every module in it is laid out. One written anywhere
    /// but at file level places nothing, and has been reported.
    /// </summary>
    private void PlaceModule(PlaceDirectiveSyntax directive)
    {
        if (expansion is not null || !Placements.AtFileLevel(directive))
            return;
        measuredIn[Stream] = nextStream++;
        placePoints.Add(new PlacePoint(directive, steps.Count, Measured, segment));
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
        if (length == DataLengths.Unpredictable)
            measuredIn[Stream] = nextStream++;
        else
            filled[Measured] = offset + length;
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

        // A label a macro expands outside a routine is a position in no code, which binding
        // could not see where the body was written.
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
    /// the two are not in one stream or the target is no label this file placed.
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
    /// Where the label an expression names stands. A macro parameter stands for what the
    /// call gave it, and what the call gave was written in the caller, so it is placed at
    /// the caller's level rather than at the body's.
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
    /// changed. A branch only ever grows, so asking again settles.
    /// </summary>
    private bool Lengthen()
    {
        var changed = false;
        foreach (var branch in branches)
        {
            var at = (branch.Statement.Position, branch.On);
            // A target at a distance nt65 does not know is always long: nothing says it is
            // near enough, and a branch that cannot reach is no branch at all.
            if (!branch.Long || lengthened.Contains(at) || Distance(branch) is { } reach && InRange(reach))
                continue;
            lengthened.Add(at);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// The short branches that cannot reach what they name. A distance nt65 does not know is
    /// left to ca65, whose own check stands; a long branch has been lengthened rather than
    /// reported.
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
    /// Takes what this walk worked out about the measured spans, and says whether any of
    /// them changed. A span is not what any length depends on here, so one more walk
    /// settles them.
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
