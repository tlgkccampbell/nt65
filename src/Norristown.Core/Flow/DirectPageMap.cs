using System.Collections.Immutable;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents how a program's routines share the zero page, or on the 65816 each direct page, for
/// an editor to show. It groups every location the program reaches through a page by the value of
/// D, and says which routines use each one and how.
/// <para>
/// The map is only for showing, so it may track memory on a best-effort basis. Where data lands is
/// decided by the linker, so an offset that the source does not fix is predicted. A segment is
/// taken to start where its linked configuration's memory area starts, or at the start of its page
/// without one. The files' bytes follow in the order the program lists the files. Nothing warns or
/// errors because of the map.
/// </para>
/// </summary>
public sealed class DirectPageMap
{
    private DirectPageMap(IReadOnlyList<DirectPage> pages, bool predicted, IReadOnlyList<PageCall> calls)
    {
        Pages = pages;
        IsPredicted = predicted;
        Calls = calls;
    }

    /// <summary>
    /// Gets the pages. Those with a base come first, ordered by base, and hardware pages after them.
    /// The page for D not being known is last.
    /// </summary>
    public IReadOnlyList<DirectPage> Pages { get; }

    /// <summary>Gets a value indicating whether any location's offset is a prediction rather than fixed by the source.</summary>
    public bool IsPredicted { get; }

    /// <summary>Gets every call in the program that names a routine, in the order the files list them.</summary>
    public IReadOnlyList<PageCall> Calls { get; }

    /// <summary>Returns the map of <paramref name="analysis"/>'s program.</summary>
    public static DirectPageMap Of(ProgramAnalysis analysis, CancellationToken cancellation = default) =>
        new Builder(analysis, cancellation).Build();

    /// <summary>Represents one call from one routine to another.</summary>
    /// <param name="Caller">The routine that makes the call.</param>
    /// <param name="Callee">The routine called.</param>
    /// <param name="At">The statement that makes the call, as it appears in the caller's file.</param>
    /// <param name="Times">
    /// How many times one pass through the caller makes the call, counting only the loops whose
    /// iteration counts nt65 knows. It is 1 outside every counted loop.
    /// </param>
    /// <param name="InUncountedLoop">Whether the call is inside a loop whose iteration count nt65 does not know.</param>
    public sealed record PageCall(Symbol Caller, Symbol Callee, SyntaxNode At, long Times, bool InUncountedLoop);

    /// <summary>Builds a map from one analysis.</summary>
    private sealed class Builder(ProgramAnalysis analysis, CancellationToken cancellation)
    {
        // The routines by their canonical symbols, each with its region and its file.
        private readonly Dictionary<Symbol, (FlowRegion Region, FileAnalysis File)> routines = [];
        private readonly List<PageCall> calls = [];
        private readonly Dictionary<Symbol, List<Symbol>> callees = [];
        private readonly HashSet<Symbol> hasCaller = [];

        // The locations that live on a page, by key, before their uses are known.
        private readonly Dictionary<LocationKey, Home> homes = [];

        // Every access found, in the order the walk found them.
        private readonly List<Found> found = [];
        private readonly Dictionary<long, int> direct = [];
        private readonly MemoryInference memory = new(analysis);

        private bool predicted;

        /// <summary>Returns the map.</summary>
        public DirectPageMap Build()
        {
            CollectRoutines();
            var (interrupt, main) = Contexts();
            CollectHomes();
            CollectAccesses();

            var uses = Uses(interrupt, main);
            var pages = Pages(uses);
            Overlap(pages);
            return new DirectPageMap(pages, predicted, calls);
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
                            calls.Add(new PageCall(caller, callee, at, block.Iterations ?? 1, uncounted[block.Index]));
                            if (!callees.TryGetValue(caller, out var list))
                                callees[caller] = list = [];
                            if (!list.Contains(callee))
                                list.Add(callee);
                            if (callee != caller)
                                hasCaller.Add(callee);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Returns the routines that interrupt handlers reach and the routines that the rest of the
        /// program reaches. A routine nothing calls is where the rest of the program starts, unless
        /// it is a handler.
        /// </summary>
        private (HashSet<Symbol> Interrupt, HashSet<Symbol> Main) Contexts()
        {
            var handlers = routines.Keys.Where(IsHandler);
            var starts = routines.Keys.Where(routine => !IsHandler(routine) && !hasCaller.Contains(routine));
            return (Reach(handlers), Reach(starts));

            HashSet<Symbol> Reach(IEnumerable<Symbol> from)
            {
                var reached = new HashSet<Symbol>();
                var pending = new Stack<Symbol>(from);
                while (pending.TryPop(out var routine))
                {
                    if (!reached.Add(routine))
                        continue;
                    foreach (var callee in callees.GetValueOrDefault(routine) ?? [])
                    {
                        if (!IsHandler(callee))
                            pending.Push(callee);
                    }
                }
                return reached;
            }
        }

        /// <summary>Returns whether <paramref name="routine"/> is an interrupt handler.</summary>
        private static bool IsHandler(Symbol routine) => routine.Signature?.IsInterrupt == true;

        /// <summary>
        /// Collects the locations that live on a page: the data declared in a zero-page segment, and
        /// the addresses below $100 that a data alias or <c>.mmio</c> names. Data is given its
        /// predicted offset, segment by segment.
        /// </summary>
        private void CollectHomes()
        {
            var segments = analysis.Program.Segments;
            var laid = new Dictionary<string, List<(Symbol Symbol, long Offset, string Type)>>(StringComparer.Ordinal);
            var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
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
                    if (step.Segment is not { } name || segments.Find(name) is not { Size: AddressSize.ZeroPage })
                        continue;
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
                        homes.TryAdd(Key(symbol), new Home(0, address, symbol.Size, true, TypeOf(declaration), IsMmio(declaration)));
                    }
                }
            }

            var starts2 = SegmentStarts(lengths);
            foreach (var (name, symbols) in laid)
            {
                if (segments.Find(name) is not { } segment)
                    continue;
                var page = BaseOf(segment);
                foreach (var (symbol, offset, type) in symbols)
                {
                    predicted = true;
                    homes.TryAdd(Key(symbol), new Home(page, starts2.GetValueOrDefault(name) + offset, symbol.Size, false, type, false));
                }
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
        /// Returns the predicted offset from its page at which each zero-page segment starts. With
        /// linked configurations, the segments that run in one memory area follow one another in
        /// the order the first configuration places them, from where the area starts. Without
        /// them, the segments of one page follow one another, <c>ZEROPAGE</c> first.
        /// </summary>
        private Dictionary<string, long> SegmentStarts(Dictionary<string, long> lengths)
        {
            var starts = new Dictionary<string, long>(StringComparer.Ordinal);
            var zeroPage = analysis.Program.Segments.Segments.Where(segment => segment.Size == AddressSize.ZeroPage).ToList();
            var linked = zeroPage.Where(segment => segment.Runs.Count > 0)
                .GroupBy(segment => (segment.Runs[0].Config, segment.Runs[0].Area));
            foreach (var area in linked)
            {
                var first = area.First().Runs[0].First;
                var at = 0L;
                foreach (var segment in area.OrderBy(segment => segment.Placements.Count > 0 ? segment.Placements[0].Line : int.MaxValue))
                {
                    var page = BaseOf(segment);
                    var origin = first >= page && first <= page + 0xff ? first - page : first < 0x100 ? first : 0;
                    starts[segment.Name] = origin + at;
                    at += lengths.GetValueOrDefault(segment.Name);
                }
            }
            foreach (var page in zeroPage.Where(segment => segment.Runs.Count == 0).GroupBy(BaseOf))
            {
                var at = 0L;
                foreach (var segment in page.OrderBy(segment => segment.Name == "ZEROPAGE" ? 0 : 1).ThenBy(segment => segment.Name, StringComparer.Ordinal))
                {
                    starts[segment.Name] = at;
                    at += lengths.GetValueOrDefault(segment.Name);
                }
            }
            return starts;
        }

        /// <summary>Returns the page a zero-page segment's symbols are reached through.</summary>
        private long BaseOf(Segment segment) => HasDirectPage ? segment.DirectPage ?? 0 : 0;

        /// <summary>
        /// Gets a value indicating whether the program is built for the 65816, whose D register
        /// moves the direct page. A file's <c>.cpu</c> decides that, as it does for the layout.
        /// </summary>
        private bool HasDirectPage => analysis.Files.Any(file => file.Layout.Cpu == Cpu.Wdc65816);

        /// <summary>Collects every instruction that reaches a location, with the page it reaches it through.</summary>
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

            // A location named by its address grows with the widest access to it, so whether an
            // access covers it is only known once every access is found.
            for (var i = 0; i < found.Count; i++)
            {
                var access = found[i];
                var size = homes[access.Location].Size;
                found[i] = access with { Whole = access.Direct && access.Offset == 0 && (size is null || access.Width >= size) };
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
                or AddressingMode.AbsoluteY or AddressingMode.LongX;
            var state = file.Layout.Cpu == Cpu.Wdc65816 ? file.State?.Before(step.Statement, step.On)?.Processor : null;
            var width = state is { } processor && RegisterOf(mnemonic) is { } register && processor.Of(register) == Width.Sixteen ? 2 : 1;
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
                    // An address the source fixes that only a known direct page reaches, such as a
                    // hardware register reached with D at the start of the registers.
                    if (page is not { } at || target.Symbol.Value.AsNumber() is not { } address || address + target.Offset - at is < 0 or > 0xff
                        || target.Symbol.Kind != SymbolKind.AddressAlias)
                    {
                        return null;
                    }
                    var declaration = target.Symbol.ValueExpression?.Parent as DataDeclarationSyntax;
                    homes[key] = new Home(at, address - at, target.Symbol.Size, true, TypeOf(declaration), IsMmio(declaration));
                }
            }
            else if (Anonymous(file.Model, operand, expression, step.On, page, !indirect ? width
                : mode is AddressingMode.DirectIndirectLong or AddressingMode.DirectIndirectLongY ? 3 : 2) is { } anonymous)
            {
                key = anonymous;
                offset = 0;
            }
            else
            {
                return null;
            }
            if (throughPage && page is { } reached)
                direct[reached] = direct.GetValueOrDefault(reached) + 1;

            return new Found(key, page, unknown, Shown(step, file.Model.Tree), step, reads, writes, !indirect && !indexed, offset, width);
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
                homes[key] = new Home(at, offset, size, true, "", false);
            return key;
        }

        /// <summary>
        /// Returns the register whose width sets how many bytes <paramref name="mnemonic"/> reaches
        /// in memory on the 65816, or null when it always reaches one byte.
        /// </summary>
        private static WidthRegister? RegisterOf(MnemonicKind mnemonic) => Instructions.SizedBy(mnemonic) ?? mnemonic switch
        {
            MnemonicKind.Sta or MnemonicKind.Stz or MnemonicKind.Inc or MnemonicKind.Dec or MnemonicKind.Asl or MnemonicKind.Lsr
                or MnemonicKind.Rol or MnemonicKind.Ror or MnemonicKind.Tsb or MnemonicKind.Trb => WidthRegister.A,
            MnemonicKind.Stx or MnemonicKind.Sty => WidthRegister.Index,
            _ => null,
        };

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
        private Dictionary<(Symbol Routine, LocationKey Location, bool Unknown), PageUse> Uses(HashSet<Symbol> interrupt, HashSet<Symbol> main)
        {
            var regions = found.GroupBy(access => access.Region).Select(group => (
                Region: group.Key!,
                Accesses: group.ToList(),
                ByStep: group.ToLookup(access => access.Step.Key),
                Tracked: group.Where(access => !access.Unknown).Select(access => access.Location).ToHashSet())).ToList();

            // The role each routine gives each location it reaches while D is known.
            var roles = new Dictionary<(Symbol Routine, LocationKey Location), PageRole>();
            var readsFirst = new Dictionary<FlowRegion, HashSet<LocationKey>>();
            foreach (var (region, accesses, byStep, tracked) in regions)
            {
                cancellation.ThrowIfCancellationRequested();
                var first = Walk(region, byStep, tracked, null).ReadsFirst;
                readsFirst[region] = first;
                foreach (var location in accesses.Where(access => !access.Unknown).GroupBy(access => access.Location))
                    roles.TryAdd((Current(region.Routine), location.Key), RoleOf(location.ToList(), location.Key, false, first));
            }

            var uses = new Dictionary<(Symbol, LocationKey, bool), PageUse>();
            foreach (var (region, accesses, byStep, tracked) in regions)
            {
                cancellation.ThrowIfCancellationRequested();
                var routine = Current(region.Routine);
                var hazards = Walk(region, byStep, tracked, roles).Hazards;
                var handler = IsHandler(routine);
                var inInterrupt = handler || interrupt.Contains(routine);

                // A routine that neither the program's starts nor a handler reaches, such as one
                // only a loop of calls reaches, is taken as the rest of the program's.
                var inMain = !handler && (main.Contains(routine) || !interrupt.Contains(routine));
                foreach (var location in accesses.GroupBy(access => (access.Location, access.Unknown)))
                {
                    var list = location.ToList();
                    var role = RoleOf(list, location.Key.Location, location.Key.Unknown, readsFirst[region]);
                    var notes = location.Key.Unknown
                        ? UnknownNotes(inInterrupt)
                        : hazards.GetValueOrDefault(location.Key.Location) ?? [];
                    notes = [.. notes, .. Strays(list)];
                    var use = new PageUse(
                        routine, role,
                        [.. list.Select(access => new PageAccess(access.Line, access.Reads, access.Writes, access.Times, access.InUncountedLoop))],
                        notes, handler, inInterrupt, inMain, location.Key.Unknown);
                    uses[(routine, location.Key.Location, location.Key.Unknown)] = use;
                }
            }
            return uses;
        }

        /// <summary>
        /// Returns the role a routine gives a location, from the routine's accesses to it and the
        /// locations it reads before it writes them.
        /// </summary>
        private PageRole RoleOf(List<Found> accesses, LocationKey location, bool unknown, HashSet<LocationKey> readsFirst)
        {
            var reads = accesses.Any(access => access.Reads);
            var writes = accesses.Any(access => access.Writes);
            if (homes[location].IsMmio)
                return writes ? PageRole.Write : PageRole.Read;
            var first = unknown ? accesses[0].Reads : readsFirst.Contains(location) || (reads && !writes);
            return first && writes ? PageRole.InOut
                : first ? PageRole.In
                : reads ? PageRole.Temp
                : PageRole.Out;
        }

        /// <summary>
        /// Returns the notes for a routine that reaches memory through D while it is not known.
        /// When an interrupt reaches the routine, D there is whatever page the interrupted code held.
        /// </summary>
        private static IReadOnlyList<PageNote> UnknownNotes(bool inInterrupt) =>
            inInterrupt ? [new PageNote("⚠", "D is the interrupted code's", null)] : [];

        /// <summary>
        /// Returns a note for each access that reaches a location through a page other than its own,
        /// which lands on whatever that page holds there.
        /// </summary>
        private IEnumerable<PageNote> Strays(IEnumerable<Found> accesses)
        {
            foreach (var access in accesses)
            {
                if (access.Page is not { } page || homes[access.Location] is not { } home || home.Page == page || home.Offset is not { } offset)
                    continue;
                yield return new PageNote("⚠", $"D = {StateValue.Hex(page, 4)} here, so it reaches {StateValue.Hex(page + offset, 4)}", access.Line);
            }
        }

        /// <summary>
        /// Walks one routine's blocks and returns the locations it reads before it writes them, and
        /// the hazards where it relies on a location across a call that uses it as a temporary.
        /// </summary>
        /// <param name="region">The routine's region.</param>
        /// <param name="byStep">The routine's accesses, by the step that makes them.</param>
        /// <param name="tracked">The locations the routine reaches while D is known.</param>
        /// <param name="roles">
        /// The role each routine gives each location, or null to look for no hazards. A call
        /// clobbers a location only when it writes the location without reading it first, and
        /// the callee or a routine it reaches gives the location the <see cref="PageRole.Temp"/>
        /// role. A call that only sets the location is how a routine returns a value, which is
        /// no hazard.
        /// </param>
        private (HashSet<LocationKey> ReadsFirst, Dictionary<LocationKey, List<PageNote>> Hazards) Walk(
            FlowRegion region, ILookup<StepKey, Found> byStep, HashSet<LocationKey> tracked,
            Dictionary<(Symbol Routine, LocationKey Location), PageRole>? roles)
        {
            var readsFirst = new HashSet<LocationKey>();
            var hazards = new Dictionary<LocationKey, List<PageNote>>();
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
                            if (collect && !held.Must.Contains(location))
                                readsFirst.Add(location);
                            if (collect && held.Clobbered.TryGetValue(location, out var call))
                                Hazard(location, call, access.Line);
                        }
                        if (access.Writes && access.Whole)
                            held = new Held(held.Must.Add(location), held.May.Add(location), held.Clobbered.Remove(location));
                        else if (access.Writes)
                            held = held with { May = held.May.Add(location) };
                    }
                }
                if (!RegisterWalk.CallsAtEnd(block) || block.Steps.Count == 0)
                    return held;
                var at = Shown(block.Steps[^1], region.Routine.Tree);
                var always = (ImmutableHashSet<LocationKey>?)null;
                var clobbered = held.Clobbered;
                foreach (var target in block.Calls)
                {
                    if (roles is not null)
                    {
                        var callee = Current(RegisterWalk.Owner(target) ?? target);
                        var reads = memory.ReadsOf(target).Select(location => location.Root).OfType<Symbol>().Select(Key).ToHashSet();
                        var writes = memory.WritesOf(target).Select(location => location.Root).OfType<Symbol>().Select(Key).ToHashSet();
                        foreach (var location in held.May)
                        {
                            if (tracked.Contains(location) && writes.Contains(location) && !reads.Contains(location) && !clobbered.ContainsKey(location)
                                && IsTempBelow(callee, location, roles))
                            {
                                clobbered = clobbered.Add(location, (callee, at));
                            }
                        }
                    }
                    var stored = memory.AlwaysWrittenBy(target).Select(location => location.Root).OfType<Symbol>().Select(Key).ToImmutableHashSet();
                    always = always is null ? stored : always.Intersect(stored);
                }
                return new Held(block.CallsUnknown || always is null ? held.Must : held.Must.Union(always), held.May, clobbered);
            }

            void Hazard(LocationKey location, (Symbol Callee, SyntaxNode At) call, SyntaxNode read)
            {
                if (!hazards.TryGetValue(location, out var notes))
                    hazards[location] = notes = [];
                var callText = call.At.GetText().Trim();
                if (notes.Any(note => note.At == call.At))
                    return;
                notes.Add(new PageNote("⚠", $"live across `{callText}`", call.At));
                notes.Add(new PageNote("⚠", $"`{call.Callee.DisplayName}` sets it", WriteIn(call.Callee, location)));
                notes.Add(new PageNote("◦", "read again", read));
            }
        }

        /// <summary>
        /// Returns the first instruction in <paramref name="routine"/> that writes
        /// <paramref name="location"/>, or the routine's declaration where only a routine it calls does.
        /// </summary>
        private SyntaxNode? WriteIn(Symbol routine, LocationKey location) =>
            found.FirstOrDefault(access => access.Routine == routine && access.Location == location && access.Writes && !access.Unknown)?.Line
            ?? (routines.TryGetValue(routine, out var known) && known.Region.Blocks is [{ Steps: [var first, ..] }, ..] ? first.Statement : null);

        /// <summary>
        /// Returns whether <paramref name="routine"/>, or a routine it reaches through its calls,
        /// gives <paramref name="location"/> the <see cref="PageRole.Temp"/> role.
        /// </summary>
        private bool IsTempBelow(Symbol routine, LocationKey location, Dictionary<(Symbol Routine, LocationKey Location), PageRole> roles)
        {
            var seen = new HashSet<Symbol>();
            var pending = new Stack<Symbol>([routine]);
            while (pending.TryPop(out var next))
            {
                if (!seen.Add(next))
                    continue;
                if (roles.GetValueOrDefault((next, location), PageRole.In) == PageRole.Temp)
                    return true;
                foreach (var callee in callees.GetValueOrDefault(next) ?? [])
                    pending.Push(callee);
            }
            return false;
        }

        /// <summary>Returns the pages, each with its locations and their uses.</summary>
        private List<DirectPage> Pages(Dictionary<(Symbol Routine, LocationKey Location, bool Unknown), PageUse> uses)
        {
            var segments = analysis.Program.Segments.Segments
                .Where(segment => segment.Size == AddressSize.ZeroPage)
                .ToLookup(BaseOf, segment => segment.Name);
            var reached = found.ToLookup(access => access.Location);
            var pages = new List<DirectPage>();
            foreach (var page in homes.GroupBy(home => home.Value.Page).OrderBy(page => page.All(home => home.Value.IsMmio)).ThenBy(page => page.Key))
            {
                var locations = new List<PageLocation>();
                foreach (var (key, home) in page.OrderBy(home => home.Value.Offset ?? long.MaxValue).ThenBy(home => home.Key.DisplayName, StringComparer.Ordinal))
                {
                    var routineUses = reached[key].Select(access => (access.Routine, access.Unknown)).Distinct()
                        .Select(use => uses.GetValueOrDefault((use.Routine!, key, use.Unknown))).OfType<PageUse>().ToList();
                    var location = new PageLocation(key.Symbol, key.Name, home.Offset, home.Size, home.IsFixed, home.Type, routineUses);
                    location.Relation = RelationOf(location, home);
                    locations.Add(location);
                }
                var hardware = locations.Count > 0 && locations.All(location => location.Relation == PageRelation.Hardware);
                var named = segments[page.Key].Where(name => locations.Any(location => location.Symbol?.Segment == name)).ToList();
                pages.Add(new DirectPage(page.Key, named, hardware, locations, [], direct.GetValueOrDefault(page.Key)));
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
                pages.Add(new DirectPage(null, [], false, [], list, 0));
            }
            return pages;
        }

        /// <summary>Returns how the routines that use a location share it.</summary>
        private static PageRelation RelationOf(PageLocation location, Home home)
        {
            if (home.IsMmio)
                return PageRelation.Hardware;
            var uses = location.Uses;
            if (uses.Count == 0)
                return PageRelation.Unused;
            if (uses.Any(use => use.Hazards.Any(note => note.Text.StartsWith("live across", StringComparison.Ordinal))))
                return PageRelation.Nested;
            // One routine reached from both is enough, because the interrupt can stop the
            // routine part-way through its use and run it again.
            if (uses.Any(use => use.InInterrupt) && uses.Any(use => use.InMain))
                return PageRelation.Interrupt;
            return uses.Count > 1 ? PageRelation.Shared : PageRelation.Own;
        }

        /// <summary>Works out which pages cover the same addresses, and which locations on them take the same bytes.</summary>
        private static void Overlap(List<DirectPage> pages)
        {
            var based = pages.Where(page => page.Base is not null).ToList();
            var overlaps = based.ToDictionary(page => page, _ => new List<PageOverlap>());
            var shared = new Dictionary<PageLocation, List<SharedBytes>>();
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
                    foreach (var here in a.Locations)
                    {
                        foreach (var there in b.Locations)
                        {
                            if (Range(a, here) is not { } x || Range(b, there) is not { } y)
                                continue;
                            var start = Math.Max(x.First, y.First);
                            var end = Math.Min(x.Last, y.Last);
                            if (start > end)
                                continue;
                            fromA.Add(new SharedBytes(here.Name, there.Name, b, start, end));
                            fromB.Add(new SharedBytes(there.Name, here.Name, a, start, end));
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

            static (long First, long Last)? Range(DirectPage page, PageLocation location) =>
                location.Offset is { } offset && location.Size is { } size and > 0
                    ? (page.Base!.Value + offset, page.Base.Value + offset + size - 1)
                    : null;

            static void Add(Dictionary<PageLocation, List<SharedBytes>> shared, PageLocation location, SharedBytes bytes)
            {
                if (!shared.TryGetValue(location, out var list))
                    shared[location] = list = [];
                list.Add(bytes);
            }
        }

        /// <summary>Returns the element a data declaration names, such as <c>.word</c>, without any values it gives.</summary>
        private static string TypeOf(DataDeclarationSyntax? declaration)
        {
            var text = declaration?.Directive?.GetText().Trim() ?? "";
            var equals = text.IndexOf('=', StringComparison.Ordinal);
            return (equals < 0 ? text : text[..equals]).Trim();
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

        /// <summary>Represents a location on its page before its uses are known.</summary>
        /// <param name="Page">The page's base.</param>
        /// <param name="Offset">The offset from the page's base, or null when it is not known.</param>
        /// <param name="Size">The number of bytes, or null when it is not known.</param>
        /// <param name="IsFixed">Whether the source fixes the address.</param>
        /// <param name="Type">The element it is declared with.</param>
        /// <param name="IsMmio">Whether it is a hardware register.</param>
        private sealed record Home(long Page, long? Offset, long? Size, bool IsFixed, string Type, bool IsMmio);

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
        /// <param name="Direct">Whether it names the location directly, rather than through an index or a pointer.</param>
        /// <param name="Offset">The offset from the location's first byte to the first byte it reaches.</param>
        /// <param name="Width">The number of bytes it reaches, which is 2 for a 16-bit register on the 65816 and 1 otherwise.</param>
        private sealed record Found(
            LocationKey Location, long? Page, bool Unknown, SyntaxNode Line, Step Step, bool Reads, bool Writes, bool Direct, long Offset, long Width)
        {
            /// <summary>
            /// Gets a value indicating whether the access reaches every byte of the location, so that
            /// a write leaves the whole of it set. A write of only some of its bytes leaves the rest
            /// as they were.
            /// </summary>
            public bool Whole { get; init; }

            public Symbol? Routine { get; init; }

            public BasicBlock? Block { get; init; }

            public FlowRegion? Region { get; init; }

            public long Times { get; init; } = 1;

            public bool InUncountedLoop { get; init; }
        }

        /// <summary>
        /// Represents what a point in a routine has stored: the locations every path has written,
        /// those some path has written, and those a call has since overwritten, with the call.
        /// </summary>
        private sealed record Held(
            ImmutableHashSet<LocationKey> Must, ImmutableHashSet<LocationKey> May, ImmutableDictionary<LocationKey, (Symbol Callee, SyntaxNode At)> Clobbered)
        {
            public static Held Nothing { get; } = new([], [], ImmutableDictionary<LocationKey, (Symbol, SyntaxNode)>.Empty);

            public static Held Merge(Held? known, Held arriving) =>
                known is null ? arriving : new Held(
                    known.Must.Intersect(arriving.Must), known.May.Union(arriving.May), known.Clobbered.SetItems(arriving.Clobbered.Where(item => !known.Clobbered.ContainsKey(item.Key))));

            public bool Equals(Held? other) =>
                other is not null && Must.SetEquals(other.Must) && May.SetEquals(other.May)
                && Clobbered.Count == other.Clobbered.Count && Clobbered.Keys.All(other.Clobbered.ContainsKey);

            public override int GetHashCode() => HashCode.Combine(Must.Count, May.Count, Clobbered.Count);
        }
    }
}
