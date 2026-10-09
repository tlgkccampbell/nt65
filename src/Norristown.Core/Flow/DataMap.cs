using System.Collections.Immutable;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents how a program's routines share its data, for an editor to show. It groups every
/// location the program reaches through the zero page, or on the 65816 a direct page, by the value
/// of D, and every other data declaration by its segment. For each location, it says which
/// routines use it and how.
/// <para>
/// The map is only for showing, so it may track memory on a best-effort basis. Where data lands is
/// decided by ld65, which nt65 does not run, so each location says where its address comes from as
/// a <see cref="DataLayout"/>. An address on a page that neither the source nor the last build
/// fixes is predicted. A segment starts where its linked configuration puts it, or at the start of
/// its page without one, and the files' bytes follow in the order the program lists the files.
/// Data off the pages is never predicted, so its address is known only from the source or a build.
/// Pages are found to share bytes only through addresses the map trusts, which excludes guessed
/// ones. Nothing warns or errors because of the map.
/// </para>
/// <para>
/// An instruction is attributed to the location its operand names, and an indexed one to the
/// location it starts from. An access through a pointer names no location, so it is never
/// attributed. The statements that take a location's address are listed instead, as
/// <see cref="DataLocation.References"/>.
/// </para>
/// </summary>
public sealed class DataMap
{
    // The endings of the note that marks a call between a routine's use of a location and its
    // read of it again, where the call uses the location as a temporary.
    private const string NestedAfterWrite = "runs between a write and a read of it";
    private const string NestedAfterRead = "runs between two reads of it";

    private DataMap(IReadOnlyList<DirectPage> pages, IReadOnlyList<DataSegment> segments, IReadOnlyList<DataCall> calls)
    {
        Pages = pages;
        Segments = segments;
        Calls = calls;
    }

    /// <summary>
    /// Gets the pages. Those with a base come first, ordered by base, and hardware pages after them.
    /// The page for D not being known is last.
    /// </summary>
    public IReadOnlyList<DirectPage> Pages { get; }

    /// <summary>
    /// Gets the data that lies on no page, by segment, in the order the program declares its
    /// segments. The locations at addresses the source fixes come after the segments, and the
    /// hardware registers last.
    /// </summary>
    public IReadOnlyList<DataSegment> Segments { get; }

    /// <summary>Gets every call in the program that names a routine, in the order the files list them.</summary>
    public IReadOnlyList<DataCall> Calls { get; }

    /// <summary>Returns the map of <paramref name="analysis"/>'s program.</summary>
    /// <param name="analysis">The analysis of the program.</param>
    /// <param name="built">
    /// The absolute address the last build gave each symbol, or null when there is no build. A
    /// zero-page data symbol found here is laid out at that address rather than at a predicted one,
    /// and a data symbol off the pages has an address only when it is found here.
    /// </param>
    /// <param name="cancellation">The token that cancels the work.</param>
    public static DataMap Of(
        ProgramAnalysis analysis, IReadOnlyDictionary<Symbol, long>? built = null, CancellationToken cancellation = default) =>
        new Builder(analysis, built, cancellation).Build();

    /// <summary>Represents one call from one routine to another.</summary>
    /// <param name="Caller">The routine that makes the call.</param>
    /// <param name="Callee">The routine called.</param>
    /// <param name="At">The statement that makes the call, as it appears in the caller's file.</param>
    /// <param name="Times">
    /// How many times one pass through the caller makes the call, counting only the loops whose
    /// iteration counts nt65 knows. It is 1 outside every counted loop.
    /// </param>
    /// <param name="InUncountedLoop">Whether the call is inside a loop whose iteration count nt65 does not know.</param>
    public sealed record DataCall(Symbol Caller, Symbol Callee, SyntaxNode At, long Times, bool InUncountedLoop);

    /// <summary>Builds a map from one analysis.</summary>
    private sealed class Builder(ProgramAnalysis analysis, IReadOnlyDictionary<Symbol, long>? built, CancellationToken cancellation)
    {
        // The routines by their canonical symbols, each with its region and its file.
        private readonly Dictionary<Symbol, (FlowRegion Region, FileAnalysis File)> routines = [];
        private readonly List<DataCall> calls = [];
        private readonly Dictionary<Symbol, List<Symbol>> callees = [];

        // The locations, on a page or off every page, by key, before their uses are known.
        private readonly Dictionary<LocationKey, Home> homes = [];

        // The routine each instruction step belongs to, by the step's key.
        private readonly Dictionary<StepKey, Symbol> owners = [];

        // Every access found, in the order the walk found them.
        private readonly List<Found> found = [];
        private readonly Dictionary<long, int> direct = [];
        private readonly MemoryInference memory = new(analysis);

        // Where each routine runs from, and which routines are walked as interrupt handlers.
        private readonly RoutineContexts contexts = analysis.Contexts();

        // The statements that take each location's address without reaching it, in the order the
        // walk found them.
        private readonly Dictionary<LocationKey, List<DataReference>> references = [];

        // The notes about each page's layout, by the page's base.
        private readonly Dictionary<long, List<DataNote>> notes = [];

        /// <summary>Returns the map.</summary>
        public DataMap Build()
        {
            CollectRoutines();
            var (interrupt, main) = Contexts();
            CollectHomes();
            CollectAccesses();
            CollectRegisters();

            var uses = Uses(interrupt, main);
            var pages = Pages(uses);
            Overlap(pages);
            return new DataMap(pages, Segments(uses), calls);
        }

        /// <summary>Returns the canonical symbol for <paramref name="symbol"/>, the one every file's model agrees on.</summary>
        private Symbol Current(Symbol symbol) => analysis.Program.Current(symbol);

        /// <summary>Collects the routines and the calls between them.</summary>
        private void CollectRoutines()
        {
            foreach (var file in analysis.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var region in file.Flow.Regions)
                {
                    var caller = Current(region.Routine);
                    routines.TryAdd(caller, (region, file));
                    var uncounted = Uncounted(region.Blocks);
                    foreach (var block in region.Blocks)
                    {
                        if (block.Calls.Count == 0 || block.Steps.Count == 0)
                            continue;
                        var at = Shown(block.Steps[^1], file.Model.Tree);
                        foreach (var target in block.Calls)
                        {
                            var callee = Current(RegisterWalk.Owner(target) ?? target);
                            calls.Add(new DataCall(caller, callee, at, block.Iterations ?? 1, uncounted[block.Index]));
                            if (!callees.TryGetValue(caller, out var list))
                                callees[caller] = list = [];
                            if (!list.Contains(callee))
                                list.Add(callee);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Returns the routines that interrupt handlers reach and the routines that the rest of the
        /// program reaches, as <see cref="RoutineContexts"/> finds them.
        /// </summary>
        private (HashSet<Symbol> Interrupt, HashSet<Symbol> Main) Contexts()
        {
            return (Where(RoutineContext.Interrupt), Where(RoutineContext.Main));

            HashSet<Symbol> Where(RoutineContext context) => [.. routines.Keys.Where(routine => contexts.Of(routine).HasFlag(context))];
        }

        /// <summary>
        /// Collects the locations that live on a page, which are the data declared in a zero-page
        /// segment and the addresses below $100 that a data alias or <c>.mmio</c> names. Data on a
        /// page is given the address the last build gave it, or else its predicted address, segment
        /// by segment. The data declared in any other segment is collected too, off the pages.
        /// </summary>
        private void CollectHomes()
        {
            var segments = analysis.Program.Segments;
            var laid = new Dictionary<string, List<(Symbol Symbol, long Offset, string Type)>>(StringComparer.Ordinal);
            var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
            var files = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var file in analysis.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                var layout = file.Layout;

                // The runs of each zero-page segment in this file, in the order they first appear,
                // each with its length.
                var runs = new Dictionary<string, List<(int Stream, long Length)>>(StringComparer.Ordinal);
                var placed = new List<(string Segment, int Stream, Symbol Symbol, long Offset, string Type)>();
                foreach (var step in layout.Steps)
                {
                    if (step.Segment is not { } name || segments.Find(name) is not { } segment)
                        continue;
                    if (segment.Size != AddressSize.ZeroPage)
                    {
                        if (step.Label is { Kind: SymbolKind.Data } elsewhere && step.Statement is DataDeclarationSyntax declared)
                            homes.TryAdd(Key(elsewhere), OffThePages(elsewhere, TypeOf(declared)));
                        continue;
                    }
                    if (step.Label is { Kind: SymbolKind.Data } data && layout.PositionOf(data, step.On) is { } start)
                    {
                        placed.Add((name, start.Stream, data, start.Offset, TypeOf(step.Statement as DataDeclarationSyntax)));
                        Grow(runs, name, start.Stream, start.Offset + (data.Size ?? 0));
                    }
                    if (layout.PositionOf(step.Statement, step.On) is { } position)
                        Grow(runs, name, position.Stream, position.End);
                }

                // A file's runs in one segment follow one another, and the file's bytes follow
                // those of the files before it.
                foreach (var (name, list) in runs)
                {
                    var at = lengths.GetValueOrDefault(name);
                    var starts = new Dictionary<int, long>();
                    foreach (var (stream, length) in list)
                    {
                        starts[stream] = at;
                        at += length;
                    }
                    lengths[name] = at;
                    files[name] = files.GetValueOrDefault(name) + 1;
                    if (!laid.TryGetValue(name, out var symbols))
                        laid[name] = symbols = [];
                    foreach (var item in placed.Where(item => item.Segment == name))
                        symbols.Add((item.Symbol, starts[item.Stream] + item.Offset, item.Type));
                }

                foreach (var symbol in file.Model.Symbols)
                {
                    if (symbol.Kind == SymbolKind.AddressAlias && symbol.ValueExpression?.Parent is DataDeclarationSyntax declaration
                        && symbol.Value.AsNumber() is { } address and >= 0 and < 0x100)
                    {
                        homes.TryAdd(Key(symbol), new Home(0, address, symbol.Size, DataLayout.Fixed, TypeOf(declaration), IsMmio(declaration), address));
                    }
                }
            }

            var segmentStarts = SegmentStarts(lengths);
            foreach (var (name, symbols) in laid)
            {
                if (segments.Find(name) is not { } segment || !segmentStarts.TryGetValue(name, out var start))
                    continue;
                var page = BaseOf(segment);
                var outside = (First: long.MaxValue, Last: long.MinValue);
                var predicted = false;
                foreach (var (symbol, offset, type) in symbols)
                {
                    // The last build says where ld65 put the symbol, which no prediction can better.
                    var (address, layout) = built is not null && (built.TryGetValue(symbol, out var at) || built.TryGetValue(Current(symbol), out at))
                        ? (at, DataLayout.Built)
                        : (start.Address + offset, start.Layout);
                    predicted |= layout != DataLayout.Built;

                    // A symbol the page cannot reach has no offset from D to show.
                    var fromPage = address - page;
                    var inPage = fromPage is >= 0 and <= 0xff;
                    if (!inPage)
                        outside = (Math.Min(outside.First, address), Math.Max(outside.Last, address + Math.Max(1, symbol.Size ?? 1) - 1));
                    homes.TryAdd(Key(symbol), new Home(page, inPage ? fromPage : null, symbol.Size, layout, type, false, inPage ? address : null));
                }

                if (outside.First <= outside.Last)
                {
                    Note(page, new DataNote(
                        "?", $"`{name}` is placed at {StateValue.Hex(outside.First, 4)}-{StateValue.Hex(outside.Last, 4)}, outside this page", null));
                }

                // The order of the object files on ld65's command line decides the order of their
                // bytes in a segment, and nt65 does not run ld65.
                if (predicted && files.GetValueOrDefault(name) > 1)
                    Note(page, new DataNote("◦", $"`{name}` has data in several files, whose order is a guess", null));
            }

            static void Grow(Dictionary<string, List<(int Stream, long Length)>> runs, string name, int stream, long end)
            {
                if (!runs.TryGetValue(name, out var list))
                    runs[name] = list = [];
                var index = list.FindIndex(run => run.Stream == stream);
                if (index < 0)
                    list.Add((stream, end));
                else if (list[index].Length < end)
                    list[index] = (stream, end);
            }
        }

        /// <summary>
        /// Returns the predicted address at which each zero-page segment starts, with where the
        /// prediction comes from.
        /// </summary>
        /// <remarks>
        /// With linked configurations, the segments that run in one memory area follow one another
        /// in the order the first configuration places them, from where the area starts, as ld65
        /// lays them out. A segment with <c>start</c> begins at that address, and one with
        /// <c>offset</c> that far into its area, and one with <c>align</c> at the next multiple of
        /// it, and the segments after it follow on from there. Without a configuration, the
        /// segments of one page follow one another from the page's base, <c>ZEROPAGE</c> first.
        /// </remarks>
        private Dictionary<string, (long Address, DataLayout Layout)> SegmentStarts(Dictionary<string, long> lengths)
        {
            var starts = new Dictionary<string, (long, DataLayout)>(StringComparer.Ordinal);
            var zeroPage = analysis.Program.Segments.Segments.Where(segment => segment.Size == AddressSize.ZeroPage).ToList();
            var linked = zeroPage.Where(segment => segment.Runs.Count > 0)
                .GroupBy(segment => (segment.Runs[0].Config, segment.Runs[0].Area));
            foreach (var area in linked)
            {
                var first = area.First().Runs[0].First;
                var at = first;
                foreach (var segment in area.OrderBy(segment => segment.Placements.Count > 0 ? segment.Placements[0].Line : int.MaxValue))
                {
                    at = segment.Start ?? (segment.Offset is { } offset ? first + offset : at);
                    if (segment.Align is { } align and > 0 && at % align != 0)
                        at += align - (at % align);
                    starts[segment.Name] = (at, DataLayout.Configured);
                    at += lengths.GetValueOrDefault(segment.Name);
                }
            }

            // A segment whose area nt65 cannot work out can still give its own start.
            foreach (var segment in zeroPage.Where(segment => segment.Runs.Count == 0 && segment.Start is not null))
                starts[segment.Name] = (segment.Start!.Value, DataLayout.Configured);

            foreach (var page in zeroPage.Where(segment => segment.Runs.Count == 0 && segment.Start is null).GroupBy(BaseOf))
            {
                var at = page.Key;
                foreach (var segment in page.OrderBy(segment => segment.Name == "ZEROPAGE" ? 0 : 1).ThenBy(segment => segment.Name, StringComparer.Ordinal))
                {
                    starts[segment.Name] = (at, DataLayout.Guessed);
                    at += lengths.GetValueOrDefault(segment.Name);
                }
            }
            return starts;
        }

        /// <summary>Adds a note about the layout of the page at <paramref name="page"/>.</summary>
        private void Note(long page, DataNote note)
        {
            if (!notes.TryGetValue(page, out var list))
                notes[page] = list = [];
            list.Add(note);
        }

        /// <summary>
        /// Returns the home of a data symbol that lies on no page. Its address is the one the last
        /// build gave it, or else not known, because the map predicts addresses only on a page.
        /// </summary>
        private Home OffThePages(Symbol symbol, string type) =>
            built is not null && (built.TryGetValue(symbol, out var at) || built.TryGetValue(Current(symbol), out at))
                ? new Home(null, null, symbol.Size, DataLayout.Built, type, false, at)
                : new Home(null, null, symbol.Size, DataLayout.Unknown, type, false, null);

        /// <summary>Returns the page a zero-page segment's symbols are reached through.</summary>
        private long BaseOf(Segment segment) => HasDirectPage ? segment.DirectPage ?? 0 : 0;

        /// <summary>
        /// Gets a value indicating whether the program is built for the 65816, whose D register
        /// moves the direct page. A file's <c>.cpu</c> decides that, as it does for the layout.
        /// </summary>
        private bool HasDirectPage => analysis.Files.Any(file => file.Layout.Cpu == Cpu.Wdc65816);

        /// <summary>
        /// Collects every instruction that reaches a location, with the page it reaches it through,
        /// and every statement that takes a location's address.
        /// </summary>
        private void CollectAccesses()
        {
            foreach (var file in analysis.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var region in file.Flow.Regions)
                {
                    var routine = Current(region.Routine);
                    var uncounted = Uncounted(region.Blocks);
                    foreach (var block in region.Blocks)
                    {
                        foreach (var step in block.Steps)
                        {
                            owners.TryAdd(step.Key, routine);
                            if (!step.Closes && Access(file, step) is { } access)
                                found.Add(access with
                                {
                                    Routine = routine,
                                    Block = block,
                                    Region = region,
                                    Times = block.Iterations ?? 1,
                                    InUncountedLoop = uncounted[block.Index],
                                });
                        }
                    }
                }
            }

            // Every location is known once the accesses are, because an access can give a page, or
            // the addresses off the pages, an address the source fixes.
            foreach (var file in analysis.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var step in file.Layout.Steps)
                {
                    if (step.Closes)
                        continue;
                    foreach (var value in TakenValues(file.Model, step))
                    {
                        if (Taken(file.Model, value, step.On, 0) is not { } target)
                            continue;
                        var key = Key(target.Symbol);
                        if (!homes.ContainsKey(key))
                            continue;
                        if (!references.TryGetValue(key, out var list))
                            references[key] = list = [];
                        // A declaration's directive is shown as the whole declaration.
                        var shown = Shown(step, file.Model.Tree);
                        if (shown is DataDirectiveSyntax { Parent: DataDeclarationSyntax declaration })
                            shown = declaration;
                        if (!list.Exists(reference => reference.Line == shown))
                            list.Add(new DataReference(shown, owners.GetValueOrDefault(step.Key)));
                    }
                }
            }
        }

        /// <summary>
        /// Collects the hardware registers, which <c>.mmio</c> declares, that lie on a page with a
        /// base but that no instruction reaches through it. The page then names every register it
        /// covers, so a note about where an access through an unknown D lands names a register
        /// that the page it points at shows.
        /// </summary>
        /// <remarks>
        /// Only the pages that already hold a location are looked at, so a declared register never
        /// makes a page of its own. A register on two overlapping pages goes on the one with the
        /// lower base.
        /// </remarks>
        private void CollectRegisters()
        {
            var bases = homes.Values.Select(home => home.Page).OfType<long>().Distinct().Order().ToList();
            foreach (var file in analysis.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var symbol in file.Model.Symbols)
                {
                    if (symbol is not { Kind: SymbolKind.AddressAlias, ValueExpression.Parent: DataDeclarationSyntax declaration }
                        || !IsMmio(declaration) || symbol.Value.AsNumber() is not { } address || homes.ContainsKey(Key(symbol)))
                    {
                        continue;
                    }
                    foreach (var page in bases)
                    {
                        if (address - page is >= 0 and <= 0xff)
                        {
                            homes[Key(symbol)] = new Home(page, address - page, symbol.Size, DataLayout.Fixed, TypeOf(declaration), true, address);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Returns the expressions of <paramref name="step"/> that give a value rather than reach
        /// memory. Those are the operand of an immediate instruction, such as <c>ldx #tmp</c>, and
        /// the values of a data directive, such as <c>.addr tmp</c>.
        /// </summary>
        private static IEnumerable<SyntaxNode> TakenValues(SemanticModel model, Step step)
        {
            var statement = step.Statement is LabeledLineSyntax { Statement: { } labeled } ? labeled : step.Statement;
            switch (statement)
            {
                case InstructionStatementSyntax:
                    if (StepOperands.Of(model, step) is ImmediateOperandSyntax immediate)
                        yield return immediate.Value;
                    break;

                // A data declaration's directive is a step of its own. An address alias has no
                // values, because its address is where it is rather than a value the program holds.
                case DataDirectiveSyntax { Tail: { } tail }:
                    foreach (var value in Values(tail))
                        yield return value;
                    break;
                case DataValuesSyntax line:
                    foreach (var value in line.Values.SelectMany(Values))
                        yield return value;
                    break;
                default:
                    break;
            }

            // A data body's lines are statements of their own, so its values are found there.
            static IEnumerable<SyntaxNode> Values(SyntaxNode node) => node switch
            {
                ExpressionSyntax expression => [expression],
                DataBodySyntax => [],
                _ => node.ChildNodes.SelectMany(Values),
            };
        }

        /// <summary>
        /// Returns the symbol whose address <paramref name="expression"/> takes, and the constant
        /// added to it, as <see cref="Target"/> finds them. The <c>&lt;</c>, <c>&gt;</c> and
        /// <c>^</c> operators, and the <c>.lobyte</c>, <c>.hibyte</c> and <c>.bankbyte</c>
        /// functions, take a part of the address, so the symbol is found inside them.
        /// </summary>
        private static (Symbol Symbol, long Offset)? Taken(SemanticModel model, SyntaxNode expression, Expansion? on, int depth)
        {
            if (depth > 8)
                return null;
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    return Taken(model, parenthesized.Expression, on, depth + 1);
                case UnaryExpressionSyntax unary when unary.OperatorToken.Kind is SyntaxKind.Less or SyntaxKind.Greater or SyntaxKind.Caret:
                    return Taken(model, unary.Operand, on, depth + 1);
                case CallExpressionSyntax { BuiltinKind: BuiltinKind.Lobyte or BuiltinKind.Hibyte or BuiltinKind.Bankbyte, Arguments.Arguments: [var argument] }:
                    return Taken(model, argument, on, depth + 1);
                case NameExpressionSyntax name when model.SymbolOf(name, on) is { Kind: SymbolKind.MacroParameter } parameter:
                    return model.GivenAt(parameter, on) is { Argument.Value: { } given, Caller: var caller }
                        ? Taken(model, given, caller, depth + 1)
                        : null;
                default:
                    return Target(model, expression, on, 0);
            }
        }

        /// <summary>
        /// Returns, for each of a routine's blocks, whether it is inside a loop whose iteration
        /// count nt65 does not know. A counted loop's latch repeats its header, which is how a
        /// counted loop is told from the others.
        /// </summary>
        private static bool[] Uncounted(IReadOnlyList<BasicBlock> blocks)
        {
            var uncounted = new bool[blocks.Count];
            foreach (var loop in Loops.In(blocks))
            {
                if (blocks[loop.Latch].Repeats == loop.Header)
                    continue;
                for (var i = 0; i < uncounted.Length; i++)
                    uncounted[i] |= loop.Inside[i];
            }
            return uncounted;
        }

        /// <summary>
        /// Returns how the instruction at <paramref name="step"/> reaches a location, or null where
        /// it reaches none that the map shows.
        /// </summary>
        private Found? Access(FileAnalysis file, Step step)
        {
            if (step.Statement is not InstructionStatementSyntax statement || file.Layout.Of(statement, step.On)?.Mode is not { } mode)
                return null;
            var mnemonic = statement.MnemonicKind;
            var indirect = mode is AddressingMode.DirectIndirect or AddressingMode.DirectIndirectX or AddressingMode.DirectIndirectY
                or AddressingMode.DirectIndirectLong or AddressingMode.DirectIndirectLongY or AddressingMode.AbsoluteIndirect
                or AddressingMode.AbsoluteIndirectX or AddressingMode.AbsoluteIndirectLong;
            var reads = indirect || MemoryAccess.ReadsMemory(mnemonic);
            var writes = !indirect && Instructions.Facts(mnemonic).Stores;
            if (!reads && !writes)
                return null;
            if (mode is AddressingMode.Implied or AddressingMode.Accumulator or AddressingMode.Immediate or AddressingMode.Relative
                or AddressingMode.RelativeLong or AddressingMode.DirectRelative or AddressingMode.BlockMove
                or AddressingMode.StackRelative or AddressingMode.StackRelativeIndirectY)
            {
                return null;
            }
            if (StepOperands.Of(file.Model, step) is not { } operand || CodeLayout.Expression(operand) is not { } expression)
                return null;

            var throughPage = Instructions.Width(mode) == AddressSize.ZeroPage;
            var indexed = mode is AddressingMode.DirectX or AddressingMode.DirectY or AddressingMode.AbsoluteX
                or AddressingMode.AbsoluteY or AddressingMode.LongX or AddressingMode.DirectIndirectX or AddressingMode.AbsoluteIndirectX;

            // An indirect access reaches its pointer, which is 3 bytes for the long forms and 2 for
            // the others. Any other access reaches as many bytes as its register holds.
            var state = file.Layout.Cpu == Cpu.Wdc65816 ? file.State?.Before(step.Statement, step.On)?.Processor : null;
            var width = indirect
                ? mode is AddressingMode.DirectIndirectLong or AddressingMode.DirectIndirectLongY or AddressingMode.AbsoluteIndirectLong ? 3 : 2
                : state is { } processor && Instructions.MemorySizedBy(mnemonic) is { } register && processor.Of(register) == Width.Sixteen ? 2 : 1;
            var page = (long?)null;
            var unknown = false;
            if (throughPage && state?.D is { } d)
            {
                if (d.IsKnown)
                    page = d.Value;
                else if (d.Kind == StateValueKind.Unknown)
                    unknown = true;
            }
            else if (throughPage)
            {
                page = 0;
            }

            LocationKey key;
            long offset;
            if (Target(file.Model, expression, step.On, 0) is { } target)
            {
                key = Key(target.Symbol);
                offset = target.Offset;
                if (!homes.ContainsKey(key))
                {
                    if (target.Symbol.Kind != SymbolKind.AddressAlias || target.Symbol.Value.AsNumber() is not { } address)
                        return null;

                    // An address the source fixes that only a known direct page reaches, such as a
                    // hardware register reached with D at the start of the registers. A data
                    // declaration's address reached without the page lies off the pages, such as
                    // the C64's screen.
                    var declaration = target.Symbol.ValueExpression?.Parent as DataDeclarationSyntax;
                    if (page is { } at && address + target.Offset - at is >= 0 and <= 0xff)
                        homes[key] = new Home(at, address - at, target.Symbol.Size, DataLayout.Fixed, TypeOf(declaration), IsMmio(declaration), address);
                    else if (!throughPage && declaration is not null)
                        homes[key] = new Home(null, null, target.Symbol.Size, DataLayout.Fixed, TypeOf(declaration), IsMmio(declaration), address);
                    else
                        return null;
                }
            }
            else if (!indexed && Anonymous(file.Model, operand, expression, step.On, page, width) is { } anonymous)
            {
                // A constant with an index register added, as in `lda 1,x`, is a field offset
                // from wherever the register points, so it names no address of its own.
                key = anonymous;
                offset = 0;
            }
            else
            {
                return null;
            }
            if (throughPage && page is { } reached)
                direct[reached] = direct.GetValueOrDefault(reached) + 1;

            return new Found(key, page, unknown, Shown(step, file.Model.Tree), step, reads, writes, indexed, offset, width);
        }

        /// <summary>
        /// Returns the location of a constant address that a direct operand reaches through
        /// <paramref name="page"/>, with no symbol to name it, or null when the operand is not one.
        /// Such a location is named by its address, and is as wide as the widest access to it. That
        /// is <paramref name="size"/> bytes for this access, which is the pointer for an indirect one.
        /// </summary>
        /// <remarks>
        /// An operand with a <c>d:</c> prefix gives the address itself, and any other direct operand
        /// gives the offset from D, as the layout encodes them. While D is not known the address is
        /// not known either, so such an access is left out of the map.
        /// </remarks>
        private LocationKey? Anonymous(SemanticModel model, SyntaxNode operand, SyntaxNode expression, Expansion? on, long? page, long size)
        {
            if (page is not { } at || AddressSymbols.In(model, expression, on).Any() || model.ValueOf(expression, on).AsNumber() is not { } value)
                return null;
            var offset = CodeLayout.ThroughDirectPage(operand) ? value - at : value;
            if (offset is < 0 or > 0xff)
                return null;
            var key = new LocationKey(null, at + offset);
            if (!homes.TryGetValue(key, out var home) || home.Size < size)
                homes[key] = new Home(at, offset, size, DataLayout.Fixed, "", false, at + offset);
            return key;
        }

        /// <summary>Returns the key of the location that <paramref name="symbol"/> names.</summary>
        private LocationKey Key(Symbol symbol) => new(Current(symbol), null);

        /// <summary>
        /// Returns the symbol <paramref name="expression"/> names and the constant added to it,
        /// following macro parameters to the arguments their calls gave. A member of a data
        /// declaration is taken as the declaration, and an alias of another symbol as that symbol.
        /// </summary>
        private static (Symbol Symbol, long Offset)? Target(SemanticModel model, SyntaxNode expression, Expansion? on, int depth)
        {
            if (depth > 8)
                return null;
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    return Target(model, parenthesized.Expression, on, depth + 1);

                case BinaryExpressionSyntax binary when binary.OperatorToken.Kind is SyntaxKind.Plus or SyntaxKind.Minus:
                    var sign = binary.OperatorToken.Kind == SyntaxKind.Minus ? -1 : 1;
                    if (Target(model, binary.Left, on, depth + 1) is { } left && model.ValueOf(binary.Right, on).AsNumber() is { } right)
                        return (left.Symbol, left.Offset + (sign * right));
                    if (sign == 1 && model.ValueOf(binary.Left, on).AsNumber() is { } before && Target(model, binary.Right, on, depth + 1) is { } after)
                        return (after.Symbol, after.Offset + before);
                    return null;

                case NameExpressionSyntax name when model.SymbolOf(name, on) is { } symbol:
                    if (symbol.Kind == SymbolKind.MacroParameter)
                    {
                        return model.GivenAt(symbol, on) is { Argument.Value: { } given, Caller: var caller }
                            ? Target(model, given, caller, depth + 1)
                            : null;
                    }
                    if (symbol.Kind == SymbolKind.Member && name is { GlobalToken: null, Names: [var outermost, ..] }
                        && model.SymbolAt(outermost) is { Kind: SymbolKind.Data } instance)
                    {
                        return (instance, symbol.Value.AsNumber() ?? 0);
                    }
                    if (symbol.Kind == SymbolKind.AddressAlias && symbol.ValueExpression is NameExpressionSyntax or BinaryExpressionSyntax
                        && Target(model, symbol.ValueExpression, null, depth + 1) is { } aliased)
                    {
                        return aliased;
                    }
                    return symbol.Kind is SymbolKind.Data or SymbolKind.AddressAlias ? (symbol, 0) : null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Returns what each routine does with each location it reaches, worked out from its own
        /// instructions, with the hazards found in it.
        /// </summary>
        /// <remarks>
        /// The roles are worked out for every routine before any hazard is, because whether a call
        /// clobbers a location depends on the role the routines it reaches give it.
        /// </remarks>
        private Dictionary<(Symbol Routine, LocationKey Location, bool Unknown), DataUse> Uses(HashSet<Symbol> interrupt, HashSet<Symbol> main)
        {
            var regions = found.GroupBy(access => access.Region).Select(group => (
                Region: group.Key!,
                Accesses: group.ToList(),
                ByStep: group.ToLookup(access => access.Step.Key),
                Tracked: group.Where(access => !access.Unknown).Select(access => access.Location).ToHashSet())).ToList();

            // The role each routine gives each location it reaches while D is known.
            var roles = new Dictionary<(Symbol Routine, LocationKey Location), DataRole>();
            var readsFirst = new Dictionary<FlowRegion, HashSet<LocationKey>>();
            foreach (var (region, accesses, byStep, tracked) in regions)
            {
                cancellation.ThrowIfCancellationRequested();
                var first = Walk(region, byStep, tracked, null).ReadsFirst;
                readsFirst[region] = first;
                foreach (var location in accesses.Where(access => !access.Unknown).GroupBy(access => access.Location))
                    roles.TryAdd((Current(region.Routine), location.Key), RoleOf(location.ToList(), location.Key, false, first));
            }

            // The pages the interruptible code holds D at, which are where a handler that keeps the
            // interrupted code's D reaches.
            var held = found.Where(access => access is { Unknown: false, Page: not null } && InMain(access.Routine!))
                .Select(access => access.Page!.Value).Distinct().Order().ToList();

            var uses = new Dictionary<(Symbol, LocationKey, bool), DataUse>();
            foreach (var (region, accesses, byStep, tracked) in regions)
            {
                cancellation.ThrowIfCancellationRequested();
                var routine = Current(region.Routine);
                var hazards = Walk(region, byStep, tracked, roles).Hazards;
                var handler = contexts.Handles(routine);
                var inInterrupt = handler || interrupt.Contains(routine);
                var inMain = InMain(routine);
                foreach (var location in accesses.GroupBy(access => (access.Location, access.Unknown)))
                {
                    var list = location.ToList();
                    var role = RoleOf(list, location.Key.Location, location.Key.Unknown, readsFirst[region]);
                    var notes = location.Key.Unknown
                        ? UnknownNotes(inInterrupt, location.Key.Location, list[0].Line, held)
                        : hazards.GetValueOrDefault(location.Key.Location) ?? [];
                    notes = [.. notes, .. Strays(list)];
                    var use = new DataUse(
                        routine, role,
                        [.. list.Select(access => new DataAccess(access.Line, access.Reads, access.Writes, access.Times, access.InUncountedLoop))],
                        notes, handler, inInterrupt, inMain, location.Key.Unknown);
                    uses[(routine, location.Key.Location, location.Key.Unknown)] = use;
                }
            }
            InterruptHazards(uses, held);
            return uses;

            // A routine that neither the program's starts nor a handler reaches, such as one
            // only a loop of calls reaches, is taken as the rest of the program's.
            bool InMain(Symbol routine) => !contexts.Handles(routine) && (main.Contains(routine) || !interrupt.Contains(routine));
        }

        /// <summary>
        /// Adds the hazards where code that runs only in an interrupt can change a location between
        /// a write and a read that the code it interrupts relies on. The notes go on the use in the
        /// interrupt.
        /// </summary>
        /// <remarks>
        /// A use in the interrupt that only reads the location cannot change it. One that reads it
        /// first and then writes it either counts something the interrupted code reads, or saves
        /// and restores it, and the roles do not tell those apart, so neither is a hazard. A use as
        /// a temporary is a hazard to interrupted code that reads the location, and a use that
        /// only writes it is a hazard to interrupted code that both writes and reads it. The
        /// interrupted code's write and read may be in different routines, so where no one routine
        /// both writes and reads the location, the program's uses are pooled: one routine that
        /// writes it and another that reads it stand for code that does both. A routine that runs
        /// in both contexts can interrupt itself part-way through its own use, so its use is on
        /// both sides. A use in the program while D is not known names the location as any other
        /// does, so it can be interrupted as well. A handler's use through the interrupted code's D
        /// counts only when that code holds D at the location's own page.
        /// </remarks>
        /// <param name="uses">The uses, by routine and location, which this replaces with ones that carry the new notes.</param>
        /// <param name="held">The pages the interruptible code holds D at.</param>
        private void InterruptHazards(Dictionary<(Symbol Routine, LocationKey Location, bool Unknown), DataUse> uses, List<long> held)
        {
            foreach (var location in uses.GroupBy(item => item.Key.Location).ToList())
            {
                if (homes[location.Key].IsMmio)
                    continue;
                var mains = location.Where(item => item.Value.InMain).Select(item => item.Value).ToList();
                var writer = mains.FirstOrDefault(other => other.Accesses.Any(access => access.Writes));
                var reader = mains.FirstOrDefault(other => other.Routine != writer?.Routine && other.Accesses.Any(access => access.Reads));
                foreach (var (key, use) in location.Where(item => item.Value.InInterrupt).ToList())
                {
                    if (use.IsUnknownPage && (homes[location.Key].Page is not { } home || !held.Contains(home)))
                        continue;
                    var name = use.Routine.DisplayName;
                    DataNote whom;
                    if (mains.FirstOrDefault(other => Clobbers(use.Role, other.Role)) is { } victim)
                    {
                        whom = new DataNote("⚠", victim == use
                            ? "and can interrupt itself between its own write and read"
                            : $"and can interrupt `{victim.Routine.DisplayName}` between its write and its read",
                            victim.Accesses.FirstOrDefault(access => access.Reads).Line);
                    }
                    else if (writer is not null && reader is not null && Clobbers(use.Role, DataRole.InOut))
                    {
                        whom = new DataNote("⚠",
                            $"and can interrupt the program between `{writer.Routine.DisplayName}`'s write and `{reader.Routine.DisplayName}`'s read",
                            reader.Accesses.FirstOrDefault(access => access.Reads).Line);
                    }
                    else
                    {
                        continue;
                    }
                    var what = use.Role == DataRole.Temp ? $"`{name}` uses it as a temporary" : $"`{name}` writes it without reading it first";
                    uses[key] = use with
                    {
                        Hazards =
                        [
                            .. use.Hazards,
                            new DataNote("⚠", what, use.Accesses.FirstOrDefault(access => access.Writes).Line),
                            whom,
                        ],
                    };
                }
            }

            static bool Clobbers(DataRole interrupting, DataRole interrupted) => interrupting switch
            {
                DataRole.Temp => interrupted is DataRole.In or DataRole.InOut or DataRole.Temp,
                DataRole.Out => interrupted is DataRole.InOut or DataRole.Temp,
                _ => false,
            };
        }

        /// <summary>
        /// Returns the role a routine gives a location, from the routine's accesses to it and the
        /// locations it reads before it writes them.
        /// </summary>
        private DataRole RoleOf(List<Found> accesses, LocationKey location, bool unknown, HashSet<LocationKey> readsFirst)
        {
            var reads = accesses.Any(access => access.Reads);
            var writes = accesses.Any(access => access.Writes);
            if (homes[location].IsMmio)
                return writes ? DataRole.Write : DataRole.Read;
            var first = unknown ? accesses[0].Reads : readsFirst.Contains(location) || (reads && !writes);
            return first && writes ? DataRole.InOut
                : first ? DataRole.In
                : reads ? DataRole.Temp
                : DataRole.Out;
        }

        /// <summary>
        /// Returns the notes for a routine that reaches <paramref name="location"/> through D while
        /// it is not known. When an interrupt reaches the routine, D there is whatever page the
        /// interrupted code held, so there is a note for where the access lands on each such page.
        /// </summary>
        /// <param name="inInterrupt">Whether the routine runs in an interrupt.</param>
        /// <param name="location">The location the operand names.</param>
        /// <param name="at">The routine's first instruction that names the location.</param>
        /// <param name="held">The pages the interruptible code holds D at.</param>
        private List<DataNote> UnknownNotes(bool inInterrupt, LocationKey location, SyntaxNode at, List<long> held)
        {
            if (!inInterrupt)
                return [];
            List<DataNote> notes = [new DataNote("⚠", "D is left as the interrupted code had it", null)];
            if (homes[location] is not { Offset: { } offset } home)
                return notes;
            foreach (var page in held)
            {
                var address = page + offset;
                var reaches = $"with D = {StateValue.Hex(page, 4)} it reaches {StateValue.Hex(address, 4)}";
                if (page == home.Page)
                {
                    notes.Add(new DataNote("◦", $"{reaches}, `{location.Name}`", at));
                    continue;
                }
                if (LocationAt(address, page) is not { } landed)
                {
                    notes.Add(new DataNote("◦", $"{reaches}, free", at));
                    continue;
                }
                var into = address - landed.Start;
                notes.Add(new DataNote(
                    landed.Name == location.Name ? "◦" : "⚠", $"{reaches}, `{landed.Name}`{(into > 0 ? $"+{into}" : "")}", at));
            }
            return notes;
        }

        /// <summary>
        /// Returns the name and first address of the location that takes <paramref name="address"/>,
        /// or null when none does. A location on the page at <paramref name="page"/> is preferred.
        /// An address alias that lies on no page, which no instruction reaches, is looked for last.
        /// </summary>
        private (string Name, long Start)? LocationAt(long address, long page)
        {
            var home = homes.Where(item => item.Value is { Page: not null, Offset: not null })
                .Select(item => (item.Key.Name, item.Value.Page, Start: item.Value.Page!.Value + item.Value.Offset!.Value, item.Value.Size))
                .Where(item => Takes(item.Start, item.Size))
                .OrderBy(item => item.Page == page ? 0 : 1)
                .Select(item => ((string Name, long Start)?)(item.Name, item.Start))
                .FirstOrDefault();
            if (home is not null)
                return home;
            foreach (var file in analysis.Files)
            {
                foreach (var symbol in file.Model.Symbols)
                {
                    if (symbol is { Kind: SymbolKind.AddressAlias, ValueExpression.Parent: DataDeclarationSyntax }
                        && symbol.Value.AsNumber() is { } start && Takes(start, symbol.Size))
                    {
                        return (symbol.Name, start);
                    }
                }
            }
            return null;

            bool Takes(long start, long? size) => address >= start && address < start + Math.Max(1, size ?? 1);
        }

        /// <summary>
        /// Returns a note for each access that reaches a location through a page other than its own,
        /// which lands on whatever that page holds there.
        /// </summary>
        private IEnumerable<DataNote> Strays(IEnumerable<Found> accesses)
        {
            foreach (var access in accesses)
            {
                if (access.Page is not { } page || homes[access.Location] is not { } home || home.Page == page || home.Offset is not { } offset)
                    continue;
                yield return new DataNote(
                    "⚠", $"D is {StateValue.Hex(page, 4)} here, so this reaches {StateValue.Hex(page + offset, 4)} instead of `{access.Location.Name}`", access.Line);
            }
        }

        /// <summary>
        /// Walks one routine's blocks and returns the locations it reads before it writes them, and
        /// the hazards where it relies on a location across a call that uses it as a temporary. A
        /// routine relies on a location it has written or read before the call and reads again
        /// after it.
        /// </summary>
        /// <param name="region">The routine's region.</param>
        /// <param name="byStep">The routine's accesses, by the step that makes them.</param>
        /// <param name="tracked">The locations the routine reaches while D is known.</param>
        /// <param name="roles">
        /// The role each routine gives each location, or null to look for no hazards. A call
        /// clobbers a location only when it writes the location without reading it first, and
        /// the callee or a routine it reaches gives the location the <see cref="DataRole.Temp"/>
        /// role. A call that only sets the location is how a routine returns a value, which is
        /// no hazard.
        /// </param>
        private (HashSet<LocationKey> ReadsFirst, Dictionary<LocationKey, List<DataNote>> Hazards) Walk(
            FlowRegion region, ILookup<StepKey, Found> byStep, HashSet<LocationKey> tracked,
            Dictionary<(Symbol Routine, LocationKey Location), DataRole>? roles)
        {
            var readsFirst = new HashSet<LocationKey>();
            var hazards = new Dictionary<LocationKey, List<DataNote>>();
            var blocks = region.Blocks;
            if (blocks.Count == 0)
                return (readsFirst, hazards);
            var solver = new Dataflow<Held>(blocks, (block, held) => Through(block, held, false), Held.Merge, block => ControlFlow.Onward(blocks, block));
            solver.Enter(0, Held.Nothing);
            foreach (var block in blocks)
            {
                if (solver.Reached[block.Index] is { } held)
                    Through(block, held, true);
            }
            return (readsFirst, hazards);

            Held Through(BasicBlock block, Held held, bool collect)
            {
                foreach (var step in block.Steps)
                {
                    foreach (var access in byStep[step.Key])
                    {
                        if (access.Unknown)
                            continue;
                        var location = access.Location;
                        if (access.Reads)
                        {
                            // An indexed read may reach any byte, so it reads what the routine was
                            // given unless every byte is set.
                            var set = access.Indexed ? Covered(held.Must, location) : Covers(held.Must, access);
                            if (collect && !set)
                                readsFirst.Add(location);
                            if (collect && held.Clobbered.TryGetValue(location, out var call))
                                Hazard(location, call, access.Line);

                            // A value read is held as well as one written, since the routine may
                            // read it again after a call and rely on it being the same.
                            held = held with { Read = held.Read.Add(location) };
                        }
                        if (access.Writes && !access.Indexed)
                        {
                            var must = held.Must.Union(Bytes(location, access.Offset, access.Width));
                            held = held with
                            {
                                Must = must,
                                May = held.May.Add(location),
                                Clobbered = Covered(must, location) ? held.Clobbered.Remove(location) : held.Clobbered,
                            };
                        }
                        else if (access.Writes)
                        {
                            held = held with { May = held.May.Add(location) };
                        }
                    }
                }
                if (!RegisterWalk.CallsAtEnd(block) || block.Steps.Count == 0)
                    return held;
                var at = Shown(block.Steps[^1], region.Routine.Tree);
                var always = (ImmutableHashSet<(LocationKey, long)>?)null;
                var clobbered = held.Clobbered;
                foreach (var target in block.Calls)
                {
                    if (roles is not null)
                    {
                        var callee = Current(RegisterWalk.Owner(target) ?? target);
                        var reads = memory.ReadsOf(target).Select(location => location.Root).OfType<Symbol>().Select(Key).ToHashSet();
                        var writes = memory.WritesOf(target).Select(location => location.Root).OfType<Symbol>().Select(Key).ToHashSet();
                        foreach (var location in held.May.Union(held.Read))
                        {
                            if (tracked.Contains(location) && writes.Contains(location) && !reads.Contains(location) && !clobbered.ContainsKey(location)
                                && TempBelow(callee, location, roles) is { } temp)
                            {
                                clobbered = clobbered.Add(location, (callee, temp, at, held.May.Contains(location)));
                            }
                        }
                    }
                    // The inference keeps each byte a store reaches, so the high byte of a 16-bit
                    // store counts as set as well.
                    var stored = memory.AlwaysWrittenBy(target).Where(location => location.Root is not null)
                        .Select(location => (Key(location.Root!), location.Offset)).ToImmutableHashSet();
                    always = always is null ? stored : always.Intersect(stored);
                }
                return held with { Must = block.CallsUnknown || always is null ? held.Must : held.Must.Union(always), Clobbered = clobbered };
            }

            void Hazard(LocationKey location, (Symbol Callee, Symbol Temp, SyntaxNode At, bool Written) call, SyntaxNode read)
            {
                if (!hazards.TryGetValue(location, out var notes))
                    hazards[location] = notes = [];
                var callText = call.At.GetText().Trim();
                if (notes.Any(note => note.At == call.At))
                    return;
                var between = call.Written ? NestedAfterWrite : NestedAfterRead;
                notes.Add(new DataNote("⚠", $"`{callText}` {between}", call.At));
                notes.Add(new DataNote("⚠", $"`{call.Temp.DisplayName}` uses it as a temporary", WriteIn(call.Temp, location)));
                notes.Add(new DataNote("◦", "read again here, after the call", read));
            }
        }

        /// <summary>
        /// Returns the bytes of <paramref name="location"/> from <paramref name="offset"/> on, for
        /// <paramref name="width"/> bytes, as <see cref="Held.Must"/> holds them.
        /// </summary>
        private static IEnumerable<(LocationKey, long)> Bytes(LocationKey location, long offset, long width)
        {
            for (var i = 0L; i < width; i++)
                yield return (location, offset + i);
        }

        /// <summary>Returns whether <paramref name="must"/> holds every byte that <paramref name="access"/> reaches.</summary>
        private static bool Covers(ImmutableHashSet<(LocationKey, long)> must, Found access) =>
            Bytes(access.Location, access.Offset, access.Width).All(must.Contains);

        /// <summary>
        /// Returns whether <paramref name="must"/> holds every byte of <paramref name="location"/>.
        /// A location whose size is not known is taken as one byte.
        /// </summary>
        private bool Covered(ImmutableHashSet<(LocationKey, long)> must, LocationKey location) =>
            Bytes(location, 0, Math.Max(1, homes[location].Size ?? 1)).All(must.Contains);

        /// <summary>
        /// Returns the first instruction in <paramref name="routine"/> that writes
        /// <paramref name="location"/>, or the routine's declaration where only a routine it calls does.
        /// </summary>
        private SyntaxNode? WriteIn(Symbol routine, LocationKey location) =>
            found.FirstOrDefault(access => access.Routine == routine && access.Location == location && access.Writes && !access.Unknown)?.Line
            ?? (routines.TryGetValue(routine, out var known) && known.Region.Blocks is [{ Steps: [var first, ..] }, ..] ? first.Statement : null);

        /// <summary>
        /// Returns the first of <paramref name="routine"/> and the routines it reaches through its
        /// calls that gives <paramref name="location"/> the <see cref="DataRole.Temp"/> role, or
        /// null when none does.
        /// </summary>
        private Symbol? TempBelow(Symbol routine, LocationKey location, Dictionary<(Symbol Routine, LocationKey Location), DataRole> roles)
        {
            var seen = new HashSet<Symbol>();
            var pending = new Stack<Symbol>([routine]);
            while (pending.TryPop(out var next))
            {
                if (!seen.Add(next))
                    continue;
                if (roles.GetValueOrDefault((next, location), DataRole.In) == DataRole.Temp)
                    return next;
                foreach (var callee in callees.GetValueOrDefault(next) ?? [])
                    pending.Push(callee);
            }
            return null;
        }

        /// <summary>Returns the pages, each with its locations and their uses.</summary>
        private List<DirectPage> Pages(Dictionary<(Symbol Routine, LocationKey Location, bool Unknown), DataUse> uses)
        {
            var segments = analysis.Program.Segments.Segments
                .Where(segment => segment.Size == AddressSize.ZeroPage)
                .ToLookup(BaseOf, segment => segment.Name);
            var reached = found.ToLookup(access => access.Location);
            var pages = new List<DirectPage>();
            foreach (var page in homes.Where(home => home.Value.Page is not null).GroupBy(home => home.Value.Page!.Value).OrderBy(page => page.All(home => home.Value.IsMmio)).ThenBy(page => page.Key))
            {
                var locations = new List<DataLocation>();
                foreach (var (key, home) in page.OrderBy(home => home.Value.Offset ?? long.MaxValue).ThenBy(home => home.Key.DisplayName, StringComparer.Ordinal))
                    locations.Add(Location(key, home, uses, reached));
                var hardware = locations.Count > 0 && locations.All(location => location.Relation == DataRelation.Hardware);
                var named = segments[page.Key].Where(name => locations.Any(location => location.Symbol?.Segment == name)).ToList();
                List<DataNote> pageNotes = [.. notes.GetValueOrDefault(page.Key) ?? []];
                if (locations.Any(location => location.Layout == DataLayout.Guessed))
                {
                    // A guess comes from having no linked configuration, or from one that does not
                    // place the segment.
                    var unplaced = locations.Where(location => location.Layout == DataLayout.Guessed)
                        .Select(location => location.Symbol?.Segment).OfType<string>().Distinct().Select(name => $"`{name}`");
                    var configured = analysis.Program.Segments.Segments.Any(segment => segment.Placements.Count > 0);
                    pageNotes.Add(new DataNote(
                        "◦", configured ? $"config does not place {string.Join(", ", unplaced)} · layout guessed" : "no config · layout guessed", null));
                }

                // The 6502's stack lives at $0100-$01FF, and so does the 65816's in emulation mode.
                if (page.Key <= 0x1ff && page.Key + 0xff >= 0x100)
                {
                    pageNotes.Add(new DataNote(
                        "◦", HasDirectPage ? "covers $0100-$01FF, the stack page in emulation mode" : "covers the stack page, $0100-$01FF", null));
                }
                foreach (var (here, there, first, last) in SamePage(locations))
                {
                    if (KindOf(here, there) != SharedBytesKind.Collision)
                        continue;
                    var span = first == last
                        ? StateValue.Hex(page.Key + first, 4)
                        : $"{StateValue.Hex(page.Key + first, 4)}-{StateValue.Hex(page.Key + last, 4)}";
                    pageNotes.Add(new DataNote("⧉", $"`{here.Name}` and `{there.Name}` share {span} unintentionally", null));
                }
                pages.Add(new DirectPage(page.Key, named, hardware, locations, [], direct.GetValueOrDefault(page.Key), pageNotes));
            }

            var unknown = found.Where(access => access.Unknown).GroupBy(access => (access.Routine, access.Location)).ToList();
            if (unknown.Count > 0)
            {
                var list = new List<UnknownPageUse>();
                foreach (var group in unknown)
                {
                    var use = uses[(group.Key.Routine!, group.Key.Location, true)];
                    var home = homes[group.Key.Location];
                    var page = pages.FirstOrDefault(page => page.Base == home.Page);
                    var reason = use.InInterrupt ? UnknownPageReason.Interrupted : UnknownPageReason.Unknown;
                    // Only a symbol is reached while D is not known, because a constant address
                    // has no address to be named by until D is known.
                    list.Add(new UnknownPageUse(group.Key.Location.Symbol!, page, home.Offset, use, reason));
                }
                pages.Add(new DirectPage(null, [], false, [], list, 0, []));
            }
            return pages;
        }

        /// <summary>
        /// Returns the data that lies on no page, grouped by segment, then the locations at
        /// addresses the source fixes, then the hardware registers. A group's locations are in
        /// address order where the addresses are known, and otherwise in the order they are declared.
        /// </summary>
        private List<DataSegment> Segments(Dictionary<(Symbol Routine, LocationKey Location, bool Unknown), DataUse> uses)
        {
            var reached = found.ToLookup(access => access.Location);
            var order = analysis.Program.Segments.Segments.Select((segment, index) => (segment.Name, index))
                .ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
            var groups = homes.Where(home => home.Value.Page is null)
                .GroupBy(home => (Name: home.Value.Layout == DataLayout.Fixed ? null : home.Key.Symbol?.Segment, home.Value.IsMmio))
                .OrderBy(group => group.Key.Name is { } name ? order.GetValueOrDefault(name, int.MaxValue - 2) : group.Key.IsMmio ? int.MaxValue : int.MaxValue - 1)
                .ThenBy(group => group.Key.Name, StringComparer.Ordinal);
            var segments = new List<DataSegment>();
            foreach (var group in groups)
            {
                cancellation.ThrowIfCancellationRequested();
                var locations = group
                    .OrderBy(home => home.Value.Address ?? long.MaxValue)
                    .ThenBy(home => home.Key.Symbol?.Tree.Path, StringComparer.Ordinal)
                    .ThenBy(home => home.Key.Symbol?.NameSpan.Start)
                    .Select(home => Location(home.Key, home.Value, uses, reached))
                    .ToList();
                segments.Add(new DataSegment(group.Key.Name, group.Key.IsMmio, locations));
            }
            return segments;
        }

        /// <summary>Returns the location that <paramref name="key"/> names, with what each routine that reaches it does with it.</summary>
        private DataLocation Location(
            LocationKey key, Home home, Dictionary<(Symbol Routine, LocationKey Location, bool Unknown), DataUse> uses, ILookup<LocationKey, Found> reached)
        {
            var routineUses = reached[key].Select(access => (access.Routine, access.Unknown)).Distinct()
                .Select(use => uses.GetValueOrDefault((use.Routine!, key, use.Unknown))).OfType<DataUse>().ToList();
            var location = new DataLocation(key.Symbol, key.Name, home.Offset, home.Size, home.Layout, home.Type, routineUses)
            {
                Address = home.Page is { } page && home.Offset is { } offset ? page + offset : home.Page is null ? home.Address : null,
                References = references.GetValueOrDefault(key) ?? [],
            };
            location.Relation = RelationOf(location, home);
            return location;
        }

        /// <summary>Returns how the routines that use a location share it.</summary>
        private static DataRelation RelationOf(DataLocation location, Home home)
        {
            if (home.IsMmio)
                return DataRelation.Hardware;
            var uses = location.Uses;
            if (uses.Count == 0)
                return DataRelation.Unused;
            if (uses.Any(use => use.Hazards.Any(note => note.Text.EndsWith(NestedAfterWrite, StringComparison.Ordinal)
                || note.Text.EndsWith(NestedAfterRead, StringComparison.Ordinal))))
                return DataRelation.Nested;
            // One routine reached from both is enough, because the interrupt can stop the
            // routine part-way through its use and run it again.
            if (uses.Any(use => use.InInterrupt) && uses.Any(use => use.InMain))
                return DataRelation.Interrupt;
            return uses.Count > 1 ? DataRelation.Shared : DataRelation.Own;
        }

        /// <summary>
        /// Works out which pages cover the same addresses, and which locations take the same
        /// bytes, on two pages or on one. A finding is only as good as the addresses it rests on,
        /// so a guessed location takes part in none, and neither does a page whose locations are
        /// all guessed.
        /// </summary>
        private void Overlap(List<DirectPage> pages)
        {
            var based = pages.Where(page => page.Base is not null && page.Locations.Any(Trusted)).ToList();
            var overlaps = pages.Where(page => page.Base is not null).ToDictionary(page => page, _ => new List<PageOverlap>());
            var shared = new Dictionary<DataLocation, List<SharedBytes>>();
            foreach (var page in based)
            {
                foreach (var (here, there, first, last) in SamePage(page.Locations))
                {
                    var kind = KindOf(here, there);
                    Add(shared, here, new SharedBytes(here.Name, there.Name, page, page.Base!.Value + first, page.Base.Value + last, kind));
                    Add(shared, there, new SharedBytes(there.Name, here.Name, page, page.Base.Value + first, page.Base.Value + last, kind));
                }
            }
            for (var i = 0; i < based.Count; i++)
            {
                for (var j = i + 1; j < based.Count; j++)
                {
                    var (a, b) = (based[i], based[j]);
                    var first = Math.Max(a.Base!.Value, b.Base!.Value);
                    var last = Math.Min(a.Base.Value, b.Base.Value) + 0xff;
                    if (first > last)
                        continue;
                    var fromA = new List<SharedBytes>();
                    var fromB = new List<SharedBytes>();
                    foreach (var here in a.Locations.Where(Trusted))
                    {
                        foreach (var there in b.Locations.Where(Trusted))
                        {
                            if (Range(a, here) is not { } x || Range(b, there) is not { } y)
                                continue;
                            var start = Math.Max(x.First, y.First);
                            var end = Math.Min(x.Last, y.Last);
                            if (start > end)
                                continue;
                            fromA.Add(new SharedBytes(here.Name, there.Name, b, start, end, SharedBytesKind.OtherPage));
                            fromB.Add(new SharedBytes(there.Name, here.Name, a, start, end, SharedBytesKind.OtherPage));
                            Add(shared, here, fromA[^1]);
                            Add(shared, there, fromB[^1]);
                        }
                    }
                    overlaps[a].Add(new PageOverlap(b, first, last, fromA));
                    overlaps[b].Add(new PageOverlap(a, first, last, fromB));
                }
            }
            foreach (var (page, list) in overlaps)
                page.Overlaps = list;
            foreach (var (location, list) in shared)
                location.Shared = list;

            static (long First, long Last)? Range(DirectPage page, DataLocation location) =>
                location.Offset is { } offset && location.Size is { } size and > 0
                    ? (page.Base!.Value + offset, page.Base.Value + offset + size - 1)
                    : null;

            static void Add(Dictionary<DataLocation, List<SharedBytes>> shared, DataLocation location, SharedBytes bytes)
            {
                if (!shared.TryGetValue(location, out var list))
                    shared[location] = list = [];
                list.Add(bytes);
            }
        }

        /// <summary>Returns whether the map trusts <paramref name="location"/>'s address enough to find shared bytes with it.</summary>
        private static bool Trusted(DataLocation location) => location.Layout != DataLayout.Guessed;

        /// <summary>
        /// Returns each pair of trusted locations among <paramref name="locations"/>, which are on
        /// one page, that take some of the same bytes, with the first and last offset both take.
        /// </summary>
        private static IEnumerable<(DataLocation Here, DataLocation There, long First, long Last)> SamePage(IReadOnlyList<DataLocation> locations)
        {
            var placed = locations.Where(location => Trusted(location) && location.Offset is not null && location.Size is > 0).ToList();
            for (var i = 0; i < placed.Count; i++)
            {
                for (var j = i + 1; j < placed.Count; j++)
                {
                    var (here, there) = (placed[i], placed[j]);
                    var first = Math.Max(here.Offset!.Value, there.Offset!.Value);
                    var last = Math.Min(here.Offset.Value + here.Size!.Value, there.Offset.Value + there.Size!.Value) - 1;
                    if (first <= last)
                        yield return (here, there, first, last);
                }
            }
        }

        /// <summary>
        /// Returns how two locations on one page come to take the same bytes. Two addresses the
        /// source fixes alias the bytes on purpose, as a program that names one byte two ways does.
        /// A fixed address inside a segment's bytes is nearly always a mistake, so it collides. Two
        /// laid-out locations share bytes because the configuration says so when it places both, as
        /// <see cref="SharedBytesKind.Authored"/> describes. Otherwise one segment's predicted
        /// bytes run on into the other's, and the two collide.
        /// </summary>
        private SharedBytesKind KindOf(DataLocation here, DataLocation there)
        {
            if (here.Layout == DataLayout.Fixed && there.Layout == DataLayout.Fixed)
                return SharedBytesKind.Deliberate;
            if (here.Layout == DataLayout.Fixed || there.Layout == DataLayout.Fixed)
                return SharedBytesKind.Collision;

            // ld65 put both where the last build says, which is where the configuration told it to.
            if (here.Layout == DataLayout.Built && there.Layout == DataLayout.Built)
                return SharedBytesKind.Authored;
            if (SegmentOf(here) is not { } a || SegmentOf(there) is not { } b || a.Name == b.Name)
                return SharedBytesKind.Collision;
            return (Pinned(a) && Pinned(b)) || Apart(a, b) ? SharedBytesKind.Authored : SharedBytesKind.Collision;

            // A segment with `start` or `offset` begins where the configuration says, wherever the
            // segments before it end.
            static bool Pinned(Segment segment) => segment.Start is not null || segment.Offset is not null;

            // Two memory areas of one configuration that cover some of the same addresses are
            // overlapped on purpose, as a program that gives several machines' zero-page blocks does.
            static bool Apart(Segment a, Segment b) => a.Runs.Any(here => b.Runs.Any(there =>
                here.Config == there.Config && here.Area != there.Area && here.First <= there.Last && there.First <= here.Last));
        }

        /// <summary>Returns the segment <paramref name="location"/> is declared in, or null when it is in none.</summary>
        private Segment? SegmentOf(DataLocation location) =>
            location.Symbol?.Segment is { } name ? analysis.Program.Segments.Find(name) : null;

        /// <summary>Returns the element a data declaration names, such as <c>.word</c>, without any values it gives.</summary>
        private static string TypeOf(DataDeclarationSyntax? declaration)
        {
            // Values on the line, as in `.addr buf`, follow the element without an `=`, so the
            // element is put together from its parts rather than cut from the directive's text.
            if (declaration?.Directive is not { } directive)
                return "";
            var type = directive.Type is { } name ? $" {name.GetText().Trim()}" : "";
            return $"{directive.Directive.Text}{type}{directive.Count?.GetText().Trim()}";
        }

        /// <summary>Returns whether <paramref name="declaration"/> declares a hardware register.</summary>
        private static bool IsMmio(DataDeclarationSyntax? declaration) => declaration?.Keyword.DirectiveKind == DirectiveKind.Mmio;

        /// <summary>
        /// Returns the statement to show for <paramref name="step"/> in <paramref name="tree"/>.
        /// Inside a macro expansion that is the outermost call in the tree.
        /// </summary>
        private static SyntaxNode Shown(Step step, SyntaxTree tree)
        {
            if (step.Statement.Tree == tree)
                return step.Statement;
            SyntaxNode? shown = null;
            for (var expansion = step.On; expansion is not null; expansion = expansion.Outer)
            {
                if (expansion.Call is { } call && call.Tree == tree)
                    shown = call;
            }
            return shown ?? step.Statement;
        }

        /// <summary>Represents a location on its page, or off every page, before its uses are known.</summary>
        /// <param name="Page">The page's base, or null for a location on no page.</param>
        /// <param name="Offset">The offset from the page's base, or null when it is not known or the location is on no page.</param>
        /// <param name="Size">The number of bytes, or null when it is not known.</param>
        /// <param name="Layout">Where the address comes from.</param>
        /// <param name="Type">The element it is declared with.</param>
        /// <param name="IsMmio">Whether it is a hardware register.</param>
        /// <param name="Address">The address of its first byte, or null when it is not known.</param>
        private sealed record Home(long? Page, long? Offset, long? Size, DataLayout Layout, string Type, bool IsMmio, long? Address);

        /// <summary>
        /// Represents the key of a location in the builder. A location that a symbol names is keyed
        /// by the symbol's canonical symbol, and a constant address with no symbol by that address.
        /// </summary>
        /// <param name="Symbol">The canonical symbol that names the location, or null for a constant address.</param>
        /// <param name="Address">The address of a location with no symbol, or null for one that a symbol names.</param>
        private readonly record struct LocationKey(Symbol? Symbol, long? Address)
        {
            /// <summary>Gets the name the location is shown by, which is its symbol's name or its address, such as <c>$00FB</c>.</summary>
            public string Name => Symbol?.Name ?? $"${Address:X4}";

            /// <summary>Gets the name the locations of a page are ordered by when their offsets are the same.</summary>
            public string DisplayName => Symbol?.DisplayName ?? Name;
        }

        /// <summary>Represents one instruction's access to one location.</summary>
        /// <param name="Location">The location's key.</param>
        /// <param name="Page">The page the access goes through, or null when it goes through none or D is not known.</param>
        /// <param name="Unknown">Whether the access goes through the direct page while D is not known.</param>
        /// <param name="Line">The statement to show.</param>
        /// <param name="Step">The step of the instruction.</param>
        /// <param name="Reads">Whether it reads the location.</param>
        /// <param name="Writes">Whether it writes the location.</param>
        /// <param name="Indexed">
        /// Whether it adds an index register to the operand, as <c>lda table,x</c> and
        /// <c>lda (ptr,x)</c> do, so that which bytes it reaches is not known.
        /// </param>
        /// <param name="Offset">The offset from the location's first byte to the first byte it reaches.</param>
        /// <param name="Width">
        /// The number of bytes it reaches from <paramref name="Offset"/>. That is the pointer for an
        /// indirect access, and otherwise 2 for a 16-bit register on the 65816 and 1 for the rest.
        /// </param>
        private sealed record Found(
            LocationKey Location, long? Page, bool Unknown, SyntaxNode Line, Step Step, bool Reads, bool Writes, bool Indexed, long Offset, long Width)
        {
            public Symbol? Routine { get; init; }

            public BasicBlock? Block { get; init; }

            public FlowRegion? Region { get; init; }

            public long Times { get; init; } = 1;

            public bool InUncountedLoop { get; init; }
        }

        /// <summary>
        /// Represents what a point in a routine has stored: the bytes every path has written, the
        /// locations some path has written or read, and those a call has since overwritten, with
        /// the call and the routine below it that uses the location as a temporary.
        /// </summary>
        /// <remarks>
        /// Written bytes are kept one by one, each as its location and its offset in it, because
        /// the 6502 sets a pointer one byte at a time. A location is set only once each of its
        /// bytes is.
        /// </remarks>
        /// <param name="Must">The bytes every path has written, each as its location and its offset in it.</param>
        /// <param name="May">The locations some path has written, in whole or in part.</param>
        /// <param name="Read">The locations some path has read.</param>
        /// <param name="Clobbered">
        /// The locations a call has since overwritten, with the call, and whether the routine had
        /// written the location before it rather than only read it.
        /// </param>
        private sealed record Held(
            ImmutableHashSet<(LocationKey Location, long Byte)> Must, ImmutableHashSet<LocationKey> May, ImmutableHashSet<LocationKey> Read,
            ImmutableDictionary<LocationKey, (Symbol Callee, Symbol Temp, SyntaxNode At, bool Written)> Clobbered)
        {
            public static Held Nothing { get; } = new([], [], [], ImmutableDictionary<LocationKey, (Symbol, Symbol, SyntaxNode, bool)>.Empty);

            public static Held Merge(Held? known, Held arriving) =>
                known is null ? arriving : new Held(
                    known.Must.Intersect(arriving.Must), known.May.Union(arriving.May), known.Read.Union(arriving.Read),
                    known.Clobbered.SetItems(arriving.Clobbered.Where(item => !known.Clobbered.ContainsKey(item.Key))));

            public bool Equals(Held? other) =>
                other is not null && Must.SetEquals(other.Must) && May.SetEquals(other.May) && Read.SetEquals(other.Read)
                && Clobbered.Count == other.Clobbered.Count && Clobbered.Keys.All(other.Clobbered.ContainsKey);

            public override int GetHashCode() => HashCode.Combine(Must.Count, May.Count, Read.Count, Clobbered.Count);
        }
    }
}
