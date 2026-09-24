using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

public sealed partial class CodeLayout
{
    /// <summary>
    /// Tracks where the bytes land. This part records the stream the walk is emitting into, how far
    /// it has filled it, where each line's bytes and each label fall among them, and how many bytes
    /// a measured routine or declaration came to.
    /// <para>
    /// The distance between two positions is known only within one run of a segment's bytes. The
    /// branch range check, long-branch sizing and <c>.fallthrough</c> checks all rely on such
    /// distances. A segment's bytes form one run across all its regions and blocks, in the order
    /// they appear in the file, which is the order ca65 emits them. An <c>.align</c> or a
    /// <c>.place</c> ends the run. A long branch starts short and is lengthened where its target
    /// turns out to be out of reach. That moves everything after it, so the file is laid out again
    /// until no branch changes.
    /// </para>
    /// </summary>
    private sealed partial class Walker
    {
        /// <summary>Gets the stream the walk is emitting into.</summary>
        private int Stream => streams[^1];

        /// <summary>
        /// Gets the run the walk is emitting into, which is the current segment's run or, for bytes
        /// outside every segment, the current stream's.
        /// </summary>
        private int Measured => segment is { } named ? RunOf(named) : measuredIn.GetValueOrDefault(Stream, Stream);

        /// <summary>
        /// Lengthens every long branch this walk found out of reach, and returns whether any
        /// changed. A branch only ever grows, so repeating the walk until nothing changes terminates.
        /// </summary>
        public bool Lengthen()
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
        /// Records the measured spans this walk worked out, and returns whether any of them changed.
        /// No length in the layout depends on a span, so one more walk is enough for the spans to stop
        /// changing.
        /// </summary>
        public bool RecordSpans()
        {
            var changed = false;
            foreach (var symbol in measured)
            {
                var now = extents.TryGetValue(symbol, out var span) ? span : (long?)null;
                var before = layout.spans.TryGetValue(symbol, out var was) ? was : (long?)null;
                if (now == before)
                    continue;
                if (now is { } value)
                    layout.spans[symbol] = value;
                else
                    layout.spans.Remove(symbol);
                changed = true;
            }
            return changed;
        }

        /// <summary>Returns whether a branch can reach the distance <paramref name="reach"/>.</summary>
        private static bool InRange(int reach) => reach is >= -128 and <= 127;

        /// <summary>
        /// Records a <c>.place</c>, where another module's bytes go. Everything that module emits
        /// comes between the bytes before the line and the bytes after it, so this file cannot know
        /// the distance from anything before it to anything after it. Only the translation unit can
        /// work that out, once every module in it is laid out. A <c>.place</c> anywhere but at file
        /// level places nothing, and has already been reported.
        /// </summary>
        private void PlaceModule(PlaceDirectiveSyntax directive)
        {
            if (expansion is not null || !Placements.IsWellPlaced(directive))
                return;

            // The placed module may emit to any segment, so every segment's run ends here.
            foreach (var named in runs.Keys.ToList())
                runs[named] = nextStream++;
            measuredIn[Stream] = nextStream++;
            layout.placePoints.Add(new PlacePoint(directive, layout.steps.Count, Measured, segment));
        }

        /// <summary>
        /// Returns the run a segment's bytes are in now, starting a new run for a segment that nothing
        /// has emitted to yet.
        /// </summary>
        private int RunOf(string named)
        {
            if (!runs.TryGetValue(named, out var run))
                runs[named] = run = nextStream++;
            return run;
        }

        /// <summary>Records what a line assembles to in the current expansion.</summary>
        private void Laid(StatementSyntax statement, LineLayout laid)
        {
            layout.lines[(statement.Position, expansion)] = laid;
            layout.anyExpansion.TryAdd((statement.Tree, statement.Position), laid);
        }

        /// <summary>
        /// Records where a line's bytes land and advances the run past them. An <c>.align</c> starts
        /// a new run of distances instead. The number of bytes it generates depends on an address, so
        /// nothing after it stands at a distance nt65 knows from anything before it.
        /// </summary>
        private void Place(StatementSyntax statement, int length)
        {
            var offset = filled.GetValueOrDefault(Measured);
            layout.positions[(statement.Position, expansion)] = new BytePosition(Measured, offset, length);
            if (length == DataLengths.Unpredictable && segment is { } named)
                runs[named] = nextStream++;
            else if (length == DataLengths.Unpredictable)
                measuredIn[Stream] = nextStream++;
            else
                filled[Measured] = offset + length;
        }

        /// <summary>
        /// Records a <c>.fallthrough</c>, which generates nothing and is recorded where the routine's
        /// bytes end. That is where the routine it names has to start, and its position is looked up
        /// the same way a label's is.
        /// </summary>
        private void FallsThrough(FallthroughDirectiveSyntax directive)
        {
            layout.positions[(directive.Position, expansion)] = new BytePosition(Measured, filled.GetValueOrDefault(Measured), 0);
            layout.steps.Add(new Step(directive, expansion, routine, Stream, segment, null));
        }

        /// <summary>
        /// Records where a label stands, which is at the first byte generated after it. That is the
        /// address a branch to it reaches.
        /// </summary>
        private void Mark(SyntaxNode declaration)
        {
            if (NameOf(declaration) is not { } symbol)
                return;

            // Anything with an address needs a segment to have it in. A routine or data declaration
            // outside every segment is reported where it is declared, and its contents are not
            // reported again.
            if (segment is null && inData == 0
                && (declaration is ProcDeclarationSyntax or MultiProcDeclarationSyntax
                    || (routine is null && declaration is DataDeclarationSyntax)))
            {
                Report(declaration.Tree, symbol.NameSpan, Catalogue.OutsideEverySegment.Message($"`{symbol.DisplayName}`"));
            }

            // A label that a macro expansion puts outside any routine marks a position in no code.
            // Binding could not report it, because it sees only the macro body as declared.
            if (routine is null && inData == 0 && symbol.Kind == SymbolKind.Label && expansion?.NearestCall is not null)
            {
                Report(declaration.Tree, symbol.NameSpan, Catalogue.LabelOutsideARoutine.Message(symbol.DisplayName, ""));
            }
            layout.labels[(symbol, Expansion.Owning(expansion, symbol))] =
                new BytePosition(Measured, filled.GetValueOrDefault(Measured), 0);
            layout.steps.Add(new Step(declaration, expansion, routine, Stream, segment, symbol));
        }

        /// <summary>
        /// Returns how far a branch reaches, from the instruction after it to its target. Returns null
        /// when the two are not in one run of a segment's bytes, or when the target is not a label
        /// this file recorded.
        /// </summary>
        private int? Distance(Branch branch)
        {
            if (layout.positions.GetValueOrDefault((branch.Statement.Position, branch.On)) is not { Length: > 0 } from)
                return null;
            return Located(branch.Target, branch.On) is { } to && to.Stream == from.Stream
                ? to.Offset - from.End
                : null;
        }

        /// <summary>
        /// Returns where the label an expression names stands. A macro parameter is resolved to the
        /// argument the call passed. That argument appears in the caller, so it is looked up at the
        /// caller's expansion level rather than the macro body's.
        /// </summary>
        private BytePosition? Located(SyntaxNode expression, Expansion? on)
        {
            if (expression is not NameExpressionSyntax || model.SymbolOf(expression) is not { } symbol)
                return null;
            if (symbol.Kind == SymbolKind.MacroParameter)
            {
                return model.GivenAt(symbol, on) is { Argument.Value: { } given, Caller: var caller }
                    ? Located(given, caller)
                    : null;
            }
            return layout.labels.TryGetValue((symbol, Expansion.Owning(on, symbol)), out var position)
                ? position
                : null;
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
                var longer = Instructions.LongFormOf(branch.Statement.MnemonicKind) is { } form ? SyntaxFacts.TextOf(form) : null;
                var fix = longer is not null ? $": use `{longer}`, which reaches any near target" : "";
                ReportOnLine(branch.Target, branch.On,
                    Catalogue.BranchOutOfReach.Message(mnemonic, reach, fix),

                    // The fix changes the branch, so it is offered only where the branch appears in
                    // this file. In an expansion, the branch is the macro body's line, which is not
                    // in this file.
                    longer is not null && branch.On is null && branch.Statement.Tree == model.Tree
                        ? new DiagnosticFix(FixKind.Branch, longer)
                        : null);
            }
        }

        /// <summary>
        /// Represents a branch whose reach nt65 can check, with the statement and expansion it stands
        /// at and the target expression it names. Long branches are included, because the same
        /// distance decides their form.
        /// </summary>
        private readonly record struct Branch(InstructionStatementSyntax Statement, Expansion? On, ExpressionSyntax Target, bool Long);
    }
}
