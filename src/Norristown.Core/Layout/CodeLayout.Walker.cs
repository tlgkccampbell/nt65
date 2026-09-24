using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

public sealed partial class CodeLayout
{
    /// <summary>
    /// Walks a file once and records what every line assembles to in a <see cref="CodeLayout"/>.
    /// The walker holds the state that matters only while the walk is in progress. That includes
    /// where in the file the walk is, how far it has filled each run of bytes, and what it has
    /// found for the next walk to use.
    /// </summary>
    private sealed partial class Walker
    {
        /// <summary>
        /// The number of statements one file's expansions may lay out before nt65 gives up. The
        /// recursion check bounds each expansion on its own, but it does not bound a chain of macros
        /// over long lists, nor a program that simply asks for too much.
        /// </summary>
        private const int MaximumStatements = 65536;

        // The layout this walk fills in, which becomes the published result once the walks
        // reach a fixed point.
        private readonly CodeLayout layout;

        // The visitor that laying out a statement dispatches through, with one method per kind of
        // statement.
        private readonly Statements statements;
        private readonly SemanticModel model;
        private readonly Cpu cpu;

        // The processor state the analysis found reaching each statement, which sizes a 65816
        // immediate and times its instructions. It is null on the first walk, before any analysis
        // has run.
        private readonly IProcessorStates? states;
        private readonly List<Diagnostic> diagnostics = [];

        // These record how far each run of bytes is filled, and the branches whose reach depends
        // on it. A distance between two positions is known only when both are in the same run of
        // bytes, and branch range checks and long-branch sizing use these distances.
        private readonly Dictionary<int, int> filled = [];
        private readonly List<Branch> branches = [];

        // The long branches already found out of reach. This set is passed from one walk to the
        // next, because lengthening a branch moves everything after it, so the file is laid out
        // again.
        private readonly HashSet<(int Position, Expansion? On)> lengthened;

        // The routines and data declarations the file measures, and the number of bytes this walk
        // finds each one takes. A `.spanof` may appear before the thing it measures, so the spans
        // one walk works out are the values the next walk answers with.
        private readonly IReadOnlySet<Symbol> measured;
        private readonly Dictionary<Symbol, long> extents = [];

        // The streams the walk is inside, innermost last. Each region and each segment block is a
        // stream of its own, so that the flow analysis can tell a detour from the code around it. A
        // nested segment block is a detour, so the stream around it resumes where it left off.
        private readonly List<int> streams = [0];
        private int nextStream = 1;

        // The run of known distances that each segment's bytes are in, which is the run each line's
        // BytePosition refers to. ca65 emits a segment's bytes in the order they appear in the file,
        // regardless of the region or block they are in, so a segment's regions and blocks form one
        // run of bytes. Only an `.align` or a `.place` ends a run. Run numbers are taken from the
        // same count as the streams.
        private readonly Dictionary<string, int> runs = new(StringComparer.Ordinal);

        // For bytes outside every segment, the run in which each stream's distances are measured
        // once an `.align` or `.place` has ended the stream's first run. Such bytes have already
        // been reported, and are recorded only so that the rest of the layout can proceed.
        private readonly Dictionary<int, int> measuredIn = [];

        // The segment the walk is laying out bytes in, or null before any region or block names one.
        // This field, the three after it and the depth of the streams make up the walk's
        // context, which Save and Restore put back after every block and expansion.
        private string? segment;

        // The routine the walk is inside, which every statement of it belongs to.
        private Symbol? routine;

        // How many data declarations the walk is nested inside. While it is above zero, the bytes
        // being laid out belong to a declaration, not to loose data outside a routine.
        private int inData;

        // The expansion the walk is inside, which identifies the iteration of each enclosing
        // repetition and the expansion of each enclosing macro. A body is laid out once per
        // expansion, and the same line can have a different length in each.
        private Expansion? expansion;

        // How many statements the expansions have laid out. An expansion is bounded by the
        // recursion check, but a chain of macros over long lists is not, so it is counted too.
        private int expanded;

        /// <summary>
        /// Initializes a new instance of the <see cref="Walker"/> class, which fills in
        /// <paramref name="layout"/>.
        /// </summary>
        /// <param name="layout">The layout to fill in.</param>
        /// <param name="states">The processor state reaching each statement, or null before any analysis.</param>
        /// <param name="lengthened">The long branches earlier walks found out of reach.</param>
        /// <param name="measured">The routines and data declarations the file measures.</param>
        public Walker(
            CodeLayout layout, IProcessorStates? states,
            HashSet<(int Position, Expansion? On)> lengthened, IReadOnlySet<Symbol> measured)
        {
            this.layout = layout;
            statements = new Statements(this);
            model = layout.model;
            cpu = layout.cpu;
            this.states = states;
            this.lengthened = lengthened;
            this.measured = measured;
        }

        /// <summary>Gets the layout this walk fills in.</summary>
        public CodeLayout Layout => layout;

        /// <summary>
        /// Gets a value indicating whether anything asked for a cycle span while there was no
        /// completed walk to count over. When it is set, the file is laid out once more, with this
        /// walk's steps to count over.
        /// </summary>
        public bool WantsCycles { get; private set; }

        /// <summary>Lays out the whole file.</summary>
        public void Walk() => Walk(model.Tree.Root.Members, from: 0);

        /// <summary>
        /// Finishes the layout once the walks have reached a fixed point, and returns it. The
        /// short branches that cannot reach their targets are reported here, because only the last
        /// walk's distances are the ones emitted.
        /// </summary>
        public CodeLayout Finish()
        {
            CheckBranchRange();
            layout.Diagnostics = Norristown.Diagnostics.Ordered(diagnostics);
            layout.ExpansionsExceeded = expanded > MaximumStatements;
            return layout;
        }

        /// <summary>
        /// Lays out a run of sibling lines and blocks, starting at index <paramref name="from"/>.
        /// The <c>.if</c> chains among them are resolved here, because a chain is a sequence of
        /// sibling blocks and only the code walking the siblings in order can see it.
        /// </summary>
        private void Walk(IReadOnlyList<SyntaxNode> children, int from)
        {
            var chain = new ConditionChain();
            for (var i = from; i < children.Count; i++)
            {
                var child = children[i];
                if (child is not BlockSyntax block)
                {
                    chain.Break();
                    if (child is LineSyntax line)
                        Statement(line.Statement);
                    continue;
                }
                if (chain.Includes(model, block, expansion, diagnostics))
                    WalkBlock(block, block.BlockKind);
            }
        }

        private void WalkBlock(BlockSyntax block, BlockKind kind)
        {
            // A macro body generates nothing where it is declared. It is laid out at every call that
            // expands it, in the segment that call is in. A type's members reserve room in each
            // declaration made with the type, and generate nothing where they are declared.
            if (kind is BlockKind.Macro or BlockKind.Struct or BlockKind.Union or BlockKind.Enum)
                return;

            // A block argument belongs to the call. The line that opens it is the call, which is
            // laid out here, and its lines are laid out where the body splices them.
            if (kind == BlockKind.MacroBlock)
            {
                if (Macros.CallIn(block.Opener.Statement) is not null)
                    Statement(block.Opener.Statement);
                return;
            }

            // A repetition's body is laid out once per iteration, because both what `.res n`
            // reserves and how wide an address `lda n` reaches depend on the iteration.
            if (Constructs.Repeats(kind))
            {
                var iterations = Repetitions.Of(model, block, expansion, diagnostics);

                // A repetition inside an expansion emits its body once per iteration, and every
                // iteration counts towards the bound, which is checked before any is laid out.
                if (expansion?.NearestCall is { } call && Exceeds(iterations.Count * (block.Members.Length - 1), call))
                    return;
                var saved = Save();
                try
                {
                    foreach (var iteration in iterations)
                    {
                        expansion = iteration;
                        Walk(block.Members, from: 1);
                    }
                }
                finally
                {
                    Restore(saved);
                }
                return;
            }

            // `.multiproc` is a repetition whose body is one routine. It is laid out once per
            // member, as the `.each` around a `.proc` that it stands for would lay it out.
            if (kind == BlockKind.MultiProc)
            {
                if (model.FamilyAt(block.Opener.Statement) is null)
                    return;
                var saved = Save();
                try
                {
                    foreach (var iteration in Repetitions.Of(model, block, saved.Expansion, diagnostics))
                    {
                        expansion = iteration;
                        WalkBlock(block, BlockKind.Proc);
                    }
                }
                finally
                {
                    Restore(saved);
                }
                return;
            }

            var context = Save();
            try
            {
                LayOutBody(block, kind);
            }
            finally
            {
                Restore(context);
            }
        }

        /// <summary>
        /// Lays out a block that opens a routine, a data declaration, a segment block or a region,
        /// along with every line inside it. The caller saves the walk's context before and
        /// restores it after.
        /// </summary>
        private void LayOutBody(BlockSyntax block, BlockKind kind)
        {
            var opener = block.Opener.Statement;
            if (kind == BlockKind.Proc && opener is ProcDeclarationSyntax or MultiProcDeclarationSyntax)
                routine = NameOf(opener);

            // The span of a routine or data declaration is the bytes between the two ends of its
            // block, in its own segment's run. A nested segment block is somewhere else and does not
            // count.
            var spanning = kind is BlockKind.Proc or BlockKind.Data or BlockKind.DataBody or BlockKind.RecordInitializer
                && NameOf(opener) is { } named && measured.Contains(named)
                ? named
                : null;
            var opened = (Stream: Measured, Offset: filled.GetValueOrDefault(Measured));
            if (kind is BlockKind.Segment or BlockKind.Region)
            {
                // A nested segment block naming the segment the bytes are already in does not move
                // them anywhere: its contents stay inline, where execution falls through into them,
                // so the block is reported as redundant.
                if (Constructs.SegmentOf(opener) == segment
                    && (routine is not null || streams.Count > 1) && opener is SegmentStatementSyntax detour)
                {
                    Report(detour.Keyword, Catalogue.SegmentBlockRedundant.Message(segment));
                }
                segment = Constructs.SegmentOf(opener) ?? segment;
                streams.Add(nextStream++);
            }
            else
            {
                Statement(opener);
            }

            if (kind is BlockKind.Data or BlockKind.DataBody or BlockKind.RecordInitializer)
                inData++;
            Walk(block.Members, from: 1);
            if (spanning is not null && Measured == opened.Stream)
                extents[spanning] = filled.GetValueOrDefault(Measured) - opened.Offset;
        }

        /// <summary>
        /// Lays out a macro call as the body it expands to. The body belongs to the file that
        /// declares the macro. It is read there and laid out here, in this call's segment and with
        /// this call's arguments.
        /// </summary>
        private void Expand(MacroCallSyntax call)
        {
            if (model.MacroAt(call) is not { Definition: BlockSyntax definition } || Expansion.Expanding(expansion, definition))
                return;
            if (Exceeds(definition.Members.Length, call))
                return;

            // An argument the parameter rejects is reported at the call, and laying out the body
            // with it would only report the same problem again from inside.
            if (!ArgumentChecks.Check(model, call, expansion, segment, (node, message) => Report(node, message)))
                return;

            // A macro with a state signature is checked where its expansion starts and where it
            // ends, so both are steps of their own.
            var outer = expansion;
            var marked = cpu == Cpu.Wdc65816 && model.MacroAt(call) is { MacroSignature: not null };
            if (marked)
                layout.steps.Add(new Step(call, outer, routine, Stream, segment, null));
            var saved = Save();
            try
            {
                expansion = Expansion.Of(outer, call, definition);
                Walk(definition.Members, from: 1);
            }
            finally
            {
                Restore(saved);
            }
            if (marked)
                layout.steps.Add(new Step(call, outer, routine, Stream, segment, null, Closes: true));
        }

        /// <summary>
        /// Adds <paramref name="statements"/> to the count laid out by expansions, and returns
        /// whether that takes the file past the bound. The bound is reported once, at
        /// <paramref name="call"/>.
        /// </summary>
        private bool Exceeds(int statements, SyntaxNode call)
        {
            if (expanded > MaximumStatements)
                return true;
            expanded += statements;
            if (expanded <= MaximumStatements)
                return false;
            Report(call, Catalogue.ExpansionLimit.Message(MaximumStatements));
            return true;
        }

        /// <summary>
        /// Lays out a line naming a <c>block</c> parameter, which stands for the lines the call
        /// supplied. Those lines are the caller's own code, so they are laid out outside the
        /// expansion that spliced them, at a level of their own. The same block may be spliced more
        /// than once, and each splice emits the lines again.
        /// </summary>
        private void Splice(BlockSpliceSyntax statement)
        {
            if (model.SymbolAt(statement.Name) is not { Parameter: { } parameter }
                || model.ArgumentFor(parameter.Symbol, expansion) is not { Block: { } block })
            {
                return;
            }

            // A block spliced into a macro with a state signature has to leave the state as it
            // found it, which is checked across the two ends of the splice.
            var outer = expansion;
            var marked = cpu == Cpu.Wdc65816 && outer?.NearestCall is { } call && model.MacroAt(call) is { MacroSignature: not null };
            if (marked)
                layout.steps.Add(new Step(statement, outer, routine, Stream, segment, null));
            var saved = Save();
            try
            {
                expansion = Expansion.Spliced(outer, statement, block);
                Walk(Macros.LinesOf(block), 0);
            }
            finally
            {
                Restore(saved);
            }
            if (marked)
                layout.steps.Add(new Step(statement, outer, routine, Stream, segment, null, Closes: true));
        }

        private void Statement(StatementSyntax statement) => statements.Visit(statement);

        /// <summary>Marks the routine being walked as one in which an instruction could not be laid out.</summary>
        private void Unlayable()
        {
            if (routine is not null)
                layout.unlaid.Add(routine);
        }

        /// <summary>
        /// Lays out one instruction in the addressing mode it calls for, which is the narrowest mode
        /// the instruction offers that is at least as wide as the operand. When the instruction
        /// offers more than one width for that operand shape, the choice is emitted into the output
        /// as a prefix.
        /// </summary>
        private void Instruction(InstructionStatementSyntax statement)
        {
            var mnemonic = statement.Mnemonic;

            // A long branch is not one of the CPU's instructions but a choice between two of
            // them, so it is laid out before the CPU's instruction table is consulted.
            if (SyntaxFacts.IsLongBranch(statement.MnemonicKind))
            {
                LongBranch(statement, mnemonic);
                return;
            }

            // An instruction outside a routine is code nothing runs, and is not laid out. Binding has
            // already reported it where it appears in the source, but binding could not see what a
            // macro expands there.
            if (routine is null)
            {
                if (expansion?.NearestCall is not null)
                    Report(mnemonic, Catalogue.InstructionOutsideARoutine.Message("an instruction belongs"));
                return;
            }

            var available = Instructions.Modes(cpu, statement.MnemonicKind);
            if (available.Count == 0)
            {
                var having = CpuNames.All.Where(other => Instructions.Has(other, statement.MnemonicKind)).ToList();
                var formatted = having.Select(CpuNames.Format).ToList();

                // The only CPU with it is the 6502 with its undocumented opcodes, so the reader is
                // looking at one of those rather than at an instruction they have misplaced.
                var undocumented = having is [Cpu.Mos6502X];
                Report(mnemonic, Catalogue.InstructionNotOnCpu.Message(
                    mnemonic.Text,
                    CpuNames.Format(cpu),
                    formatted.Count == 0 ? ""
                        : undocumented
                            ? $", and is an undocumented opcode of the NMOS 6502, which the {CpuNames.Format(Cpu.Mos6502X)} has"
                            : ", and is on the " + (formatted.Count == 1
                                ? formatted[0]
                                : string.Join(", ", formatted.SkipLast(1)) + " and " + formatted[^1])));
                Unlayable();
                return;
            }

            // In a macro body an `operand` parameter stands as a whole operand, so the mode and the
            // address size come from the argument the call passed rather than from the body's text.
            var sourceOperand = statement.Operand;
            var substituted = Operands.Substituted(model, sourceOperand, expansion);
            CheckSubstitution(substituted);

            // The values of an operand's expressions are checked here, as a data directive's are,
            // because no symbol holds them and no other pass evaluates them and reports problems.
            foreach (var expression in sourceOperand?.ChildNodes.OfType<ExpressionSyntax>() ?? [])
                model.Check(expression, diagnostics, expansion, SpanOf, CyclesOf);
            var operand = substituted?.Operand ?? sourceOperand;

            var candidates = Plausible(operand).Where(available.Contains).ToArray();
            if (candidates.Length == 0 && operand is null)
            {
                Report(mnemonic, Catalogue.OperandMissing.Message(mnemonic.Text));
                Unlayable();
                return;
            }
            if (candidates.Length == 0)
            {
                Report(operand?.Tree ?? mnemonic.Parent.Tree, operand?.Span ?? mnemonic.Span,
                    Catalogue.OperandNotTaken.Message(mnemonic.Text, CpuNames.Format(cpu)));
                Unlayable();
                return;
            }

            // On the 65816 an immediate is as wide as the register it goes to, which is what the
            // analysis found reaching it. Where it found no width it has already reported that,
            // and the immediate is laid out a byte wide so the rest of the file can be laid out.
            var state = states?.Before(statement, expansion);
            int? bits = cpu == Cpu.Wdc65816 && Instructions.SizedBy(statement.MnemonicKind) is { } register
                ? state?.Of(register) == Width.Sixteen ? 16 : 8
                : null;

            // An unknown width has already been reported, so a value too large for a byte is not
            // reported as a second error, because nobody knows the immediate really is one byte.
            var sizeUnknown = cpu == Cpu.Wdc65816 && Instructions.SizedBy(statement.MnemonicKind) is { } sized
                && state?.Of(sized) is not (Width.Eight or Width.Sixteen);

            var mode = Choose(mnemonic, operand, candidates, substituted, bits, sizeUnknown);
            var prefix = candidates.Length > 1 ? Instructions.Prefix(mode) : null;
            if (mode != AddressingMode.Immediate)
                bits = null;
            var length = Instructions.Length(mode) + (bits == 16 ? 1 : 0);
            var direct = operand is not null && ThroughDirectPage(operand) ? DirectOffset(mnemonic, operand, mode, state) : null;
            if (cpu == Cpu.Wdc65816 && operand is not null && mode != AddressingMode.Immediate)
                CheckDirectPageSymbols(mnemonic, operand, mode);
            CheckSpaces(mnemonic, operand, mode);
            var timing = Cycles.Of(cpu, statement.MnemonicKind, mode, state);
            IReadOnlyList<string>? causes = timing is { } counted ? counted.Causes : null;
            Laid(statement, new LineLayout(
                length, mode, prefix, false, timing?.Count, bits,
                Slot: states?.SlotAt(statement, expansion), Direct: direct, Causes: causes));
            Place(statement, length);
            layout.steps.Add(new Step(statement, expansion, routine, Stream, segment, null));

            // `bbr0 flags, @skip` branches to the second of its two expressions; every other
            // relative form branches to its only one.
            if (mode is AddressingMode.Relative or AddressingMode.DirectRelative && operand is not null)
            {
                var target = mode == AddressingMode.DirectRelative
                    ? (operand as AbsoluteOperandSyntax)?.Second
                    : Expression(operand);
                if (target is not null)
                    branches.Add(new Branch(statement, expansion, target, Long: false));
            }
        }

        /// <summary>
        /// Lays out a long branch, which branches like its short form but reaches any near target. It is
        /// laid out short and lengthened only where the target turns out to be out of reach, so
        /// a forward branch to a near target keeps the short form. ca65's own long-branch macro
        /// package can choose the short form only for a target it has already seen, so its
        /// forward branches are always long.
        /// </summary>
        private void LongBranch(InstructionStatementSyntax statement, SyntaxToken mnemonic)
        {
            var operand = statement.Operand;
            if (operand is null || Expression(operand) is not { } target
                || !Plausible(operand).Contains(AddressingMode.Relative))
            {
                Report(operand?.Tree ?? mnemonic.Parent.Tree, operand?.Span ?? mnemonic.Span,
                    Catalogue.BranchOperandNotTaken.Message(mnemonic.Text));
                return;
            }
            if (Operands.PrefixSize(operand) is not null)
            {
                Report(operand,
                    Catalogue.TransferPrefix.Message(mnemonic.Text));
                return;
            }
            if (model.AddressSizeOf(target, segment, expansion) == AddressSize.Far)
            {
                Report(target, Catalogue.TargetTooFar.Message(mnemonic.Text));
                return;
            }

            var over = lengthened.Contains((statement.Position, expansion));
            var length = Instructions.Length(AddressingMode.Relative)
                + (over ? Instructions.Length(AddressingMode.Absolute) : 0);
            branches.Add(new Branch(statement, expansion, target, Long: true));
            Laid(statement, new LineLayout(
                length, AddressingMode.Relative, null, over, Cycles.OfLongBranch(over)));
            Place(statement, length);
            layout.steps.Add(new Step(statement, expansion, routine, Stream, segment, null));
        }

        /// <summary>
        /// Checks an assertion. Assertions are checked here because this is the pass that walks every
        /// statement of a file with the whole program worked out. An assertion nt65 cannot evaluate
        /// is left for ca65 and ld65, which know the final addresses nt65 never sees.
        /// </summary>
        private void Assertion(AssertDirectiveSyntax directive)
        {
            var assertion = Constructs.AssertionOf(directive);
            if (assertion.Condition is not { } condition)
                return;
            if (model.ValueOf(condition, expansion, SpanOf, CyclesOf).AsNumber() is not { } value)
            {
                model.Check(condition, diagnostics, expansion, SpanOf, CyclesOf);
                return;
            }
            if (value == 0)
                Report(directive, Catalogue.AssertionFailed.Message(assertion.Message ?? "this assertion does not hold"));
        }

        /// <summary>
        /// Lays out an <c>.ensure</c>, which emits the <c>rep</c> and <c>sep</c> the analysis found
        /// it needs. Before the analysis has run, it is laid out as emitting every instruction it
        /// could.
        /// </summary>
        private void Ensure(EnsureDirectiveSyntax directive)
        {
            var state = states?.Before(directive, expansion);

            // On the 6502 and its CMOS variants there is no processor state to set, and no `rep` or
            // `sep` to set it with, so an `.ensure` is accepted and emits nothing. This lets one
            // routine be written for both CPUs.
            var ensured = cpu == Cpu.Wdc65816 ? Ensured.Of(directive, state) : default;
            var cycles = new CycleCount(0);
            foreach (var flags in new[] { ensured.Reset, ensured.Set }.Where(flags => flags != StatusFlags.None))
                cycles += Cycles.Of(cpu, MnemonicKind.Rep, AddressingMode.Immediate, state)?.Count ?? new CycleCount(3);
            Laid(directive, new LineLayout(ensured.Length, null, null, Cycles: cycles, Ensured: ensured));
            Place(directive, ensured.Length);
            layout.steps.Add(new Step(directive, expansion, routine, Stream, segment, null));
        }

        /// <summary>
        /// Reports an <c>.error</c> the build reached, which marks a configuration the file refuses
        /// to be built in, or a <c>.warning</c>, which is reported as a warning and does not stop the
        /// build.
        /// </summary>
        private void Refuse(ErrorDirectiveSyntax directive)
        {
            var warns = directive.Keyword.DirectiveKind == DirectiveKind.Warning;
            var message = Constructs.AssertionOf(directive).Message ?? "this configuration is not supported";
            Report(directive, warns ? Catalogue.ConfigWarned.Message(message) : Catalogue.ConfigRefused.Message(message));
        }

        private void Data(StatementSyntax directive)
        {
            if (DataLengths.Of(directive, model, diagnostics, expansion) is not { } length)
                return;
            if (routine is null && inData == 0 && directive is DataDirectiveSyntax { Parent: not DataDeclarationSyntax } loose)
            {
                // Bytes that a macro expands outside a routine need a declaration as much as bytes in
                // the source there do. Binding could not see them, because it sees only the macro body
                // as declared.
                if (expansion?.NearestCall is not null && loose.Directive.DirectiveKind is not (DirectiveKind.Res or DirectiveKind.Align))
                    Report(directive, Catalogue.PaddingOutsideARoutine.Message(loose.Directive.Text, ""));
                else if (segment is null && length != 0)
                    Report(directive, Catalogue.OutsideEverySegment.Message("this"));
            }
            Laid(directive, new LineLayout(length, null, null));
            Place(directive, length);
            layout.steps.Add(new Step(directive, expansion, routine, Stream, segment, null));
        }

        /// <summary>
        /// Returns the cost in cycles of one pass from <paramref name="from"/> to
        /// <paramref name="to"/>, as <see cref="CodeLayout.CyclesOf"/> does. On a walk with no
        /// completed walk to count over, it returns no count and asks for one more walk.
        /// </summary>
        private CycleSpan CyclesOf(Symbol from, Symbol to, bool upperBound)
        {
            if (layout.counted is null)
            {
                // The walk in progress has not necessarily reached the code yet. Setting the flag
                // puts the file through one more walk, in which the whole file is there to count.
                WantsCycles = true;
                return default;
            }
            return layout.CyclesOf(from, to, upperBound);
        }

        /// <summary>
        /// Returns the number of bytes <paramref name="symbol"/> took in the previous walk, or null
        /// when nt65 cannot tell.
        /// </summary>
        private long? SpanOf(Symbol symbol) => layout.SpanOf(symbol);

        /// <summary>Returns the walk's current context, for <see cref="Restore"/> to go back to.</summary>
        private Context Save() => new(segment, routine, inData, expansion, streams.Count);

        /// <summary>
        /// Puts back a context that <see cref="Save"/> returned, leaving every stream opened since.
        /// </summary>
        private void Restore(Context saved)
        {
            segment = saved.Segment;
            routine = saved.Routine;
            inData = saved.InData;
            expansion = saved.Expansion;
            streams.RemoveRange(saved.Streams, streams.Count - saved.Streams);
        }

        /// <summary>
        /// Returns the symbol a declaration declares at this point of the walk. For a
        /// <see cref="Family"/> declaration, that is the instance the current iteration of the
        /// repetition emits. Any other declaration has only one symbol.
        /// </summary>
        private Symbol? NameOf(SyntaxNode declaration) => model.DeclaredBy(declaration, expansion);

        private void Report(SyntaxNode node, DiagnosticMessage message, Severity? severity = null) =>
            Report(node.Tree, node.Span, message, severity);

        private void Report(SyntaxToken token, DiagnosticMessage message, Severity? severity = null) =>
            Report(token.Parent.Tree, token.Span, message, severity);

        private void Report(SyntaxTree tree, TextSpan span, DiagnosticMessage message, Severity? severity = null) =>
            diagnostics.Add(Expansion.Problem(model.Tree, tree, span, expansion, severity, message));

        /// <summary>
        /// Reports a problem with a line that may come from another file's macro body. A body's line
        /// is reported at the call, which is in this file and is the side that chose the arguments,
        /// and the body line is named beside it.
        /// </summary>
        private void ReportOnLine(SyntaxNode node, Expansion? on, DiagnosticMessage message, DiagnosticFix? fix = null)
        {
            if (node.Tree == model.Tree)
            {
                diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), message) { Fix = fix });
            }
            else if (on?.NearestCall is { } call)
            {
                diagnostics.Add(new Diagnostic(call.Tree.GetSpan(call.Span), message,
                    [new RelatedSpan(node.Tree.GetSpan(node.Span), "in the macro body")]));
            }
        }

        /// <summary>
        /// Dispatches the layout of one statement by kind, with one method per kind. The work itself
        /// is done by the walker, and this visitor chooses which part of it each kind calls for. A
        /// kind with no method here emits no bytes and has no effect on the processor state. Such
        /// kinds include a declaration that only names something, a directive read where its block is
        /// walked, and a blank or closing line.
        /// </summary>
        /// <param name="walker">The walker laying out the file.</param>
        private sealed class Statements(Walker walker) : SyntaxVisitor
        {
            /// <inheritdoc/>
            public override void VisitInstructionStatement(InstructionStatementSyntax node) =>
                walker.Instruction(node);

            /// <inheritdoc/>
            public override void VisitDataDirective(DataDirectiveSyntax node) => walker.Data(node);

            /// <inheritdoc/>
            public override void VisitDataValues(DataValuesSyntax node) => walker.Data(node);

            /// <summary>
            /// Records a data declaration's name where its first byte stands, and lays out what it
            /// holds. The data is laid out on the declaration's own line, or in the body it opens.
            /// </summary>
            /// <param name="node">The declaration.</param>
            public override void VisitDataDeclaration(DataDeclarationSyntax node)
            {
                walker.Mark(node);
                if (node.Directive is { } element)
                {
                    walker.Data(element);
                    if (DataSyntax.BodyOf(element) is null && walker.NameOf(node) is { } declared
                        && walker.measured.Contains(declared)
                        && walker.layout.positions.GetValueOrDefault((element.Position, walker.expansion)) is { Length: >= 0 } position)
                    {
                        walker.extents[declared] = position.Length;
                    }
                }
            }

            /// <inheritdoc/>
            public override void VisitAssertDirective(AssertDirectiveSyntax node) => walker.Assertion(node);

            /// <inheritdoc/>
            public override void VisitMacroCall(MacroCallSyntax node) => walker.Expand(node);

            /// <inheritdoc/>
            public override void VisitBlockSplice(BlockSpliceSyntax node) => walker.Splice(node);

            /// <inheritdoc/>
            public override void VisitErrorDirective(ErrorDirectiveSyntax node) => walker.Refuse(node);

            /// <inheritdoc/>
            public override void VisitLabeledLine(LabeledLineSyntax node)
            {
                walker.Mark(node.Label);
                if (node.Statement is { } labelled)
                    walker.Statement(labelled);
            }

            /// <summary>
            /// Records a routine's name where its first byte stands, which is the address a branch
            /// to it reaches. One iteration of a <c>.multiproc</c> is a routine, and the member it
            /// is named after stands there.
            /// </summary>
            /// <param name="node">The declaration.</param>
            public override void VisitProcDeclaration(ProcDeclarationSyntax node) => walker.Mark(node);

            /// <inheritdoc cref="VisitProcDeclaration"/>
            public override void VisitMultiProcDeclaration(MultiProcDeclarationSyntax node) => walker.Mark(node);

            /// <summary>
            /// Records, as a step for the flow analysis, a directive that generates no bytes. Such a
            /// directive is an annotation such as <c>.next</c> or <c>.patch</c>, which applies to the
            /// statement above it; a <c>.state</c>, which declares the processor state at that
            /// point; or a <c>.frame</c>.
            /// </summary>
            /// <param name="node">The directive.</param>
            public override void VisitNextDirective(NextDirectiveSyntax node) => NoBytes(node);

            /// <inheritdoc cref="VisitNextDirective"/>
            public override void VisitPatchDirective(PatchDirectiveSyntax node) => NoBytes(node);

            /// <summary>
            /// Records the end of a routine's body, where the routine a <c>.fallthrough</c> names has
            /// to start. A <c>.fallthrough</c> anywhere else has already been reported, and is ignored
            /// here.
            /// </summary>
            /// <param name="node">The directive.</param>
            public override void VisitFallthroughDirective(FallthroughDirectiveSyntax node)
            {
                if (node.Parent is LineSyntax line && Fallthrough.EndsABody(line))
                    walker.FallsThrough(node);
            }

            /// <inheritdoc cref="VisitNextDirective"/>
            public override void VisitStateDirective(StateDirectiveSyntax node) => NoBytes(node);

            /// <inheritdoc cref="VisitNextDirective"/>
            public override void VisitFrameDirective(FrameDirectiveSyntax node) => NoBytes(node);

            /// <inheritdoc/>
            public override void VisitEnsureDirective(EnsureDirectiveSyntax node) => walker.Ensure(node);

            /// <inheritdoc/>
            public override void VisitPlaceDirective(PlaceDirectiveSyntax node) => walker.PlaceModule(node);

            /// <summary>
            /// Records a statement that emits no bytes as a step, so that the flow analysis still sees it.
            /// </summary>
            private void NoBytes(StatementSyntax statement) => walker.layout.steps.Add(
                new Step(statement, walker.expansion, walker.routine, walker.Stream, walker.segment, null));
        }

        /// <summary>
        /// Represents the part of the walk's state that a block, an iteration or an expansion
        /// changes for the lines inside it, and that is put back when the walk leaves them.
        /// </summary>
        /// <param name="Segment">The segment the bytes are laid out in.</param>
        /// <param name="Routine">The routine the walk is inside.</param>
        /// <param name="InData">How many data declarations the walk is nested inside.</param>
        /// <param name="Expansion">The expansion the walk is inside.</param>
        /// <param name="Streams">How many streams the walk is inside.</param>
        private readonly record struct Context(
            string? Segment, Symbol? Routine, int InData, Expansion? Expansion, int Streams);
    }
}
