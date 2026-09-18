using Norristown.Flow;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Who calls a routine, and what it calls. The edges are the ones the flow analysis already
/// found for the cost lenses, so the hierarchy and the counts are the same reading of the
/// program: a <c>jsr</c>, a tail jump, a <c>.next</c> under a call, and a <c>per</c>/branch
/// pair, each named where the operand names a routine.
/// <para>
/// A call the analysis cannot follow — through a pointer, or to an address no declaration
/// stands at — is in no list, because there is no routine to put in one.
/// </para>
/// </summary>
internal static class CallHierarchy
{
    /// <summary>The routines a hierarchy may start from at <paramref name="position"/>, which is none or one.</summary>
    public static IReadOnlyList<Protocol.CallHierarchyItem> Prepare(
        ProgramAnalysis analysis, SemanticModel model, int position)
    {
        if (model.ReferenceAt(position)?.Symbol is not { } symbol)
            return [];

        // A path that ends in the name a family binds is written once and stands for every
        // instance, and the line the caret is on declares them all.
        var named = symbol.Kind == SymbolKind.Binding
            ? model.Families.Where(family => family.Binding == symbol).SelectMany(family => family.Instances)
            : [analysis.Program.Current(symbol)];
        return [.. named.Where(IsRoutine).Select(Describe)];
    }

    /// <summary>Everything that calls the routine <paramref name="item"/> names, across the program.</summary>
    public static IReadOnlyList<Protocol.CallHierarchyIncomingCall> Incoming(
        ProgramAnalysis analysis, Protocol.CallHierarchyItem item)
    {
        if (Routine(analysis, item) is not { } asked)
            return [];
        var callers = new Dictionary<Symbol, List<Protocol.Range>>();
        foreach (var (caller, callee, at) in Calls(analysis))
        {
            if (Same(callee, asked))
                Note(callers, caller, at);
        }
        return [.. Ordered(callers).Select(found =>
            new Protocol.CallHierarchyIncomingCall(Describe(found.Routine), found.Ranges))];
    }

    /// <summary>Everything the routine <paramref name="item"/> names calls.</summary>
    public static IReadOnlyList<Protocol.CallHierarchyOutgoingCall> Outgoing(
        ProgramAnalysis analysis, Protocol.CallHierarchyItem item)
    {
        if (Routine(analysis, item) is not { } asked)
            return [];
        var callees = new Dictionary<Symbol, List<Protocol.Range>>();
        foreach (var (caller, callee, at) in Calls(analysis))
        {
            if (Same(caller, asked))
                Note(callees, callee, at);
        }
        return [.. Ordered(callees).Select(found =>
            new Protocol.CallHierarchyOutgoingCall(Describe(found.Routine), found.Ranges))];
    }

    /// <summary>Whether a symbol is something a call hierarchy is about.</summary>
    private static bool IsRoutine(Symbol symbol) => symbol.Kind is SymbolKind.Proc or SymbolKind.ExternProc;

    /// <summary>
    /// Every call in the program: which routine writes it, which it names, and where it is
    /// written. A call ends the block it is in, so the block's last statement is the call.
    /// </summary>
    private static IEnumerable<(Symbol Caller, Symbol Callee, Protocol.Range At)> Calls(ProgramAnalysis analysis)
    {
        foreach (var flow in analysis.Flows)
        {
            foreach (var region in flow.Regions)
            {
                foreach (var block in region.Blocks)
                {
                    if (block.Calls.Count == 0 || Written(block) is not { } at)
                        continue;
                    foreach (var callee in block.Calls)
                        yield return (region.Routine, callee, Lsp.ToRange(at.Tree, at.Span));
                }
            }
        }
    }

    /// <summary>The statement a block's calls are written on, or null for a block with none.</summary>
    private static SyntaxNode? Written(BasicBlock block) =>
        block.Steps.Count == 0 ? null : block.Steps[^1].Statement;

    /// <summary>
    /// Whether two symbols are the same declaration. An analysis that kept the files an edit
    /// did not touch holds a different object for the same routine, so where it is declared is
    /// what says so.
    /// </summary>
    private static bool Same(Symbol a, Symbol b) =>
        a.Tree.Path == b.Tree.Path && a.NameSpan.Start == b.NameSpan.Start && a.Name == b.Name;

    private static void Note(Dictionary<Symbol, List<Protocol.Range>> found, Symbol routine, Protocol.Range at)
    {
        if (!found.TryGetValue(routine, out var ranges))
            found[routine] = ranges = [];
        if (!ranges.Contains(at))
            ranges.Add(at);
    }

    /// <summary>The routines found, by file and then by where each is declared, so a list never shuffles.</summary>
    private static IEnumerable<(Symbol Routine, List<Protocol.Range> Ranges)> Ordered(
        Dictionary<Symbol, List<Protocol.Range>> found) =>
        found
            .OrderBy(entry => entry.Key.Tree.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.NameSpan.Start)
            .ThenBy(entry => entry.Key.Name, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value));

    /// <summary>
    /// The routine an item stands for. A client sends an item back as it was given, so it is
    /// found again by where it is declared; every instance of a family is declared on one
    /// line, so the name tells them apart.
    /// </summary>
    private static Symbol? Routine(ProgramAnalysis analysis, Protocol.CallHierarchyItem item)
    {
        var path = Workspace.PathOf(item.Uri);
        if (analysis.ModelFor(path) is not { } model)
            return null;
        var start = model.Tree.GetPosition(item.SelectionRange.Start.Line, item.SelectionRange.Start.Character);
        return analysis.Flows
            .SelectMany(flow => flow.Regions)
            .Select(region => region.Routine)
            .Concat(model.Symbols)
            .FirstOrDefault(symbol =>
                IsRoutine(symbol) && symbol.Tree.Path == path && symbol.NameSpan.Start == start && symbol.Name == item.Name);
    }

    private static Protocol.CallHierarchyItem Describe(Symbol routine)
    {
        // The whole declaration is what the client reveals when the item is picked, and the
        // outline already knows how far each one reaches.
        var covered = Covering(routine) ?? routine.NameSpan;
        return new Protocol.CallHierarchyItem(
            routine.Name,
            Protocol.SymbolKind.Function,
            Lsp.ToUri(routine.Tree.Path),
            Lsp.ToRange(routine.Tree, covered),
            Lsp.ToRange(routine.Tree, routine.NameSpan),
            routine.PathName);
    }

    /// <summary>Everything the declaration covers, its body included, or null when the outline has no such item.</summary>
    private static TextSpan? Covering(Symbol routine)
    {
        var pending = new Stack<OutlineItem>(Outline.Build(routine.Tree));
        while (pending.TryPop(out var item))
        {
            if (item.NameSpan == routine.NameSpan)
                return item.Span;
            foreach (var child in item.Children)
                pending.Push(child);
        }
        return null;
    }
}
