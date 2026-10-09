using System.Globalization;
using Norristown.Emit;
using Norristown.Flow;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers the <c>nt65/directPages</c> request. It converts the <see cref="DataMap"/> of a
/// program into what the client's tree and grid show, which is every direct page and every
/// segment of data. Under each location, it arranges the
/// routines that reach it into call trees, from the outermost callers down.
/// </summary>
internal static class DirectPages
{
    /// <summary>The most routines one location's call trees hold, so that a large program's tree stays readable.</summary>
    private const int MostNodes = 200;

    /// <summary>The deepest a call tree goes.</summary>
    private const int Deepest = 16;

    /// <summary>
    /// Returns the map of <paramref name="analysis"/>'s program, with each file's URI from
    /// <paramref name="uriOf"/>. The addresses of <paramref name="built"/>, when there is a build,
    /// replace the map's predictions.
    /// </summary>
    public static Protocol.DirectPagesResult Of(
        ProgramAnalysis analysis, BuiltAddresses? built, Func<string, string> uriOf, CancellationToken cancellation)
    {
        var map = DataMap.Of(analysis, built?.Addresses, cancellation);
        var graph = new Graph(map.Calls, analysis.Contexts(), uriOf);
        var cpu = analysis.Files.Count > 0 ? analysis.Files[0].Layout.Cpu : analysis.Cpu;
        return new Protocol.DirectPagesResult(
            CpuNames.Format(cpu),
            [.. map.Pages.Select(page => Page(page, graph, uriOf))],
            [.. map.Segments.Select(segment => Segment(segment, graph, uriOf))],
            built is null ? null : new Protocol.DirectPageBuild(built.DebugFilePath, built.BuiltAtUtc.ToString("o", CultureInfo.InvariantCulture), built.IsStale));
    }

    private static Protocol.DirectPageItem Page(DirectPage page, Graph graph, Func<string, string> uriOf) => new(
        Id(page),
        page.Base,
        page.Segments,
        page.IsHardware,
        page.Base is null ? "unknown" : Name(page.Relation),
        page.IsHazard,
        page.Used,
        page.Direct,
        [.. page.Overlaps.Select(overlap => new Protocol.DirectPageOverlap(
            Id(overlap.Page), overlap.First, overlap.Last, [.. overlap.Shared.Select(Shared)]))],
        [.. page.Locations.Select(location => Location(location, graph, uriOf))],
        [.. page.Notes.Select(note => Note(note, uriOf))],
        [.. page.Unknown
            .GroupBy(use => use.Reason)
            .OrderBy(group => group.Key)
            .Select(group => new Protocol.DirectPageGroup(
                group.Key == UnknownPageReason.Interrupted ? "interrupted" : "unknown",
                [.. group.GroupBy(use => use.Use.Routine).Select(routine => new Protocol.DirectPageUnknownRoutine(
                    routine.Key.Name,
                    Declaration(routine.Key, uriOf),
                    routine.First().Use.IsHandler,
                    routine.First().Use.InInterrupt,
                    routine.First().Use.InMain,
                    [.. routine.Select(use => new Protocol.DirectPageUnknownUse(
                        use.Location.Name,
                        use.Home is { } home ? Id(home) : null,
                        use.Offset,
                        Name(use.Use.Role),
                        [.. use.Use.Accesses.Select(access => Access(access, uriOf))],
                        use.Use.Hazards.Count > 0,
                        [.. use.Use.Hazards.Select(note => Note(note, uriOf))]))]))]))]);

    private static Protocol.DirectPageSegment Segment(DataSegment segment, Graph graph, Func<string, string> uriOf) => new(
        segment.Name ?? (segment.IsHardware ? "hardware" : "fixed"),
        segment.Name,
        segment.IsHardware,
        Name(segment.Relation),
        segment.IsHazard,
        [.. segment.Locations.Select(location => Location(location, graph, uriOf))]);

    private static Protocol.DirectPageLocation Location(DataLocation location, Graph graph, Func<string, string> uriOf)
    {
        var trees = graph.Trees(location.Uses);
        var (perPass, uncounted) = (0L, 0);
        var pending = new Stack<Protocol.DirectPageRoutine>(trees);
        while (pending.TryPop(out var node))
        {
            foreach (var access in node.Accesses)
            {
                perPass = Saturated(perPass + Saturated(access.Times * node.Runs));
                if (access.Uncounted || node.RunsUncounted)
                    uncounted++;
            }
            foreach (var child in node.Children)
                pending.Push(child);
        }
        return new(
            location.Name,
            location.Symbol is { } symbol ? Declaration(symbol, uriOf) : null,
            location.Offset,
            location.Size,
            location.Address,
            Name(location.Layout),
            location.Type,
            Name(location.Relation),
            location.IsHazard,
            location.IsReached,
            [.. location.Shared.Select(Shared)],
            [.. location.References.Select(reference => Line(reference.Line, uriOf))],
            [.. location.References
                .Where(reference => reference.Routine is not null)
                .GroupBy(reference => reference.Routine!)
                .Select(routine => new Protocol.DirectPageReferrer(
                    routine.Key.Name, Declaration(routine.Key, uriOf), [.. routine.Select(reference => Line(reference.Line, uriOf))]))],
            location.Uses.Sum(use => use.Accesses.Count),
            perPass,
            uncounted,
            trees);
    }

    /// <summary>
    /// Returns a count held below a trillion, which is far past anything a person reads as
    /// different, so that loops nested through many calls cannot overflow.
    /// </summary>
    private static long Saturated(long count) => Math.Clamp(count, 0, 999_999_999_999);

    private static Protocol.DirectPageShared Shared(SharedBytes shared) =>
        new(shared.Here, shared.There, Id(shared.Page), shared.First, shared.Last, Name(shared.Kind));

    /// <summary>Returns a page's name, such as <c>$0080</c>, or <c>?</c> for the page whose D is not known.</summary>
    private static string Id(DirectPage page) => page.Base is { } at ? $"${at:X4}" : "?";

    private static string Name(DataRelation relation) => relation switch
    {
        DataRelation.Nested => "nested",
        DataRelation.Interrupt => "irq",
        DataRelation.Shared => "shared",
        DataRelation.Own => "own",
        DataRelation.Hardware => "hw",
        _ => "unused",
    };

    private static string Name(SharedBytesKind kind) => kind switch
    {
        SharedBytesKind.Deliberate => "deliberate",
        SharedBytesKind.Authored => "authored",
        SharedBytesKind.Collision => "collision",
        SharedBytesKind.Unverified => "unverified",
        _ => "page",
    };

    private static string Name(DataLayout layout) => layout switch
    {
        DataLayout.Fixed => "fixed",
        DataLayout.Built => "built",
        DataLayout.Configured => "configured",
        DataLayout.Guessed => "guessed",
        _ => "unknown",
    };

    private static string Name(DataRole role) => role switch
    {
        DataRole.In => "in",
        DataRole.Out => "out",
        DataRole.InOut => "inout",
        DataRole.Temp => "temp",
        DataRole.Read => "read",
        _ => "write",
    };

    private static Protocol.Location Declaration(Symbol symbol, Func<string, string> uriOf) =>
        new(uriOf(symbol.Tree.Path), Lsp.ToRange(symbol.Tree, symbol.NameSpan));

    private static Protocol.DirectPageAccess Access(DataAccess access, Func<string, string> uriOf) =>
        new(Line(access.Line, uriOf), access.Reads, access.Writes, access.Times, access.InUncountedLoop);

    private static Protocol.DirectPageNote Note(DataNote note, Func<string, string> uriOf) =>
        new(note.Glyph, note.Text, note.At is { } at ? Line(at, uriOf) : null);

    /// <summary>
    /// Returns the range of the whole line that starts <paramref name="node"/>, without its line
    /// break, which is what the client highlights.
    /// </summary>
    private static Protocol.Location Line(SyntaxNode node, Func<string, string> uriOf)
    {
        var tree = node.Tree;
        var line = tree.GetLineIndex(node.Position);
        var start = tree.LineStarts[line];
        var end = line + 1 < tree.LineStarts.Length ? tree.LineStarts[line + 1] : tree.Text.Length;
        while (end > start && tree.Text[end - 1] is '\n' or '\r')
            end--;
        return new(uriOf(tree.Path), new Protocol.Range(new Protocol.Position(line, 0), new Protocol.Position(line, end - start)));
    }

    /// <summary>Represents the program's calls, from which the call trees under each location are built.</summary>
    private sealed class Graph
    {
        private readonly Dictionary<Symbol, List<Callee>> callees = [];
        private readonly Dictionary<Symbol, HashSet<Symbol>> callers = [];
        private readonly Func<string, string> uriOf;
        private readonly RoutineContexts contexts;

        public Graph(IReadOnlyList<DataMap.DataCall> calls, RoutineContexts contexts, Func<string, string> uriOf)
        {
            this.uriOf = uriOf;
            this.contexts = contexts;
            foreach (var call in calls)
            {
                if (!callees.TryGetValue(call.Caller, out var list))
                    callees[call.Caller] = list = [];
                // A routine called from several places runs once for each of them.
                var callee = list.Find(item => item.Routine == call.Callee);
                if (callee is null)
                    list.Add(callee = new Callee(call.Callee));
                if (!callee.At.Contains(call.At))
                {
                    callee.At.Add(call.At);
                    callee.Times = Saturated(callee.Times + call.Times);
                    callee.Uncounted |= call.InUncountedLoop;
                }
                if (call.Caller == call.Callee)
                    continue;
                if (!callers.TryGetValue(call.Callee, out var set))
                    callers[call.Callee] = set = [];
                set.Add(call.Caller);
            }
        }

        /// <summary>
        /// Returns the call trees that lead to the routines of <paramref name="uses"/>. Each tree
        /// starts at a routine that no other routine on the way calls, and holds only the routines
        /// that lead to a use.
        /// </summary>
        public IReadOnlyList<Protocol.DirectPageRoutine> Trees(IReadOnlyList<DataUse> uses)
        {
            var byRoutine = uses.GroupBy(use => use.Routine).ToDictionary(group => group.Key, group => group.ToList());
            var leading = new HashSet<Symbol>();
            var pending = new Stack<Symbol>(byRoutine.Keys);
            while (pending.TryPop(out var routine))
            {
                if (!leading.Add(routine))
                    continue;
                foreach (var caller in callers.GetValueOrDefault(routine) ?? [])
                    pending.Push(caller);
            }

            var roots = leading.Where(routine => !(callers.GetValueOrDefault(routine) ?? []).Any(leading.Contains)).ToList();
            if (roots.Count == 0)
                roots = [.. byRoutine.Keys];
            var count = 0;
            return [.. roots
                .OrderBy(root => root.Tree.Path, StringComparer.Ordinal)
                .ThenBy(root => root.NameSpan.Start)
                .Select(root => Node(root, [], 1, false, []))];

            Protocol.DirectPageRoutine Node(Symbol routine, List<SyntaxNode> via, long runs, bool uncounted, HashSet<Symbol> path)
            {
                count++;
                var own = byRoutine.GetValueOrDefault(routine) ?? [];
                var use = own.FirstOrDefault(item => !item.IsUnknownPage) ?? own.FirstOrDefault();
                var children = new List<Protocol.DirectPageRoutine>();
                if (path.Count < Deepest)
                {
                    path.Add(routine);
                    foreach (var callee in callees.GetValueOrDefault(routine) ?? [])
                    {
                        if (count >= MostNodes)
                            break;
                        if (leading.Contains(callee.Routine) && !path.Contains(callee.Routine))
                            children.Add(Node(callee.Routine, callee.At, Saturated(runs * callee.Times), uncounted || callee.Uncounted, path));
                    }
                    path.Remove(routine);
                }
                var first = own.FirstOrDefault();
                return new Protocol.DirectPageRoutine(
                    routine.Name,
                    Declaration(routine, uriOf),
                    first?.IsHandler ?? contexts.Handles(routine),
                    first?.InInterrupt ?? false,
                    first?.InMain ?? false,
                    use?.IsUnknownPage ?? false,
                    use is null ? null : Name(use.Role),
                    [.. via.Select(at => Line(at, uriOf))],
                    runs,
                    uncounted,
                    [.. own.SelectMany(item => item.Accesses).Select(access => Access(access, uriOf))],
                    [.. own.SelectMany(item => item.Hazards).Select(note => Note(note, uriOf))],
                    children);
            }
        }

        /// <summary>Represents the calls one routine makes to another, wherever it makes them.</summary>
        private sealed class Callee(Symbol routine)
        {
            public Symbol Routine { get; } = routine;

            public List<SyntaxNode> At { get; } = [];

            public long Times { get; set; }

            public bool Uncounted { get; set; }
        }
    }
}
