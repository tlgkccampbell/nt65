using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a macro's syntax says: the pieces of a definition and of a call, and the items a
/// body may not hold. Reading them is shared, because binding, expansion and the editor
/// all ask the same questions of the same lines.
/// </summary>
public static class Macros
{
    /// <summary>The parameters written on a <c>.macro</c> opener, in order.</summary>
    public static IReadOnlyList<SyntaxNode> ParametersOf(SyntaxNode opener) =>
        [.. opener.ChildNodes
            .FirstOrDefault(child => child.Kind == SyntaxKind.MacroParameterList)?.ChildNodes
            .Where(child => child.Kind == SyntaxKind.MacroParameter) ?? []];

    /// <summary>The name of one parameter, which is the first thing written in it.</summary>
    public static SyntaxToken? NameOf(SyntaxNode parameter)
    {
        foreach (var token in parameter.ChildTokens)
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic)
                return token;
        }
        return null;
    }

    /// <summary>What a call that leaves the parameter out gets, or null when it has no default.</summary>
    public static SyntaxNode? DefaultOf(SyntaxNode parameter) =>
        parameter.ChildNodes.FirstOrDefault(child =>
            child.Kind is not (SyntaxKind.ParameterKind or SyntaxKind.EmptyBlock));

    /// <summary>One parameter as the analysis reads it, given the symbol its name declares.</summary>
    public static MacroParameter Describe(SyntaxNode parameter, Symbol symbol) =>
        new(symbol,
            ArgumentKind.Read(parameter.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ParameterKind)),
            DefaultOf(parameter),
            parameter.ChildNodes.Any(child => child.Kind == SyntaxKind.EmptyBlock));

    /// <summary>The macro a call names.</summary>
    public static SyntaxToken? CalleeOf(SyntaxNode call) =>
        call.ChildTokens.Length > 0 && call.ChildTokens[0].Kind == SyntaxKind.Identifier
            ? call.ChildTokens[0]
            : null;

    /// <summary>The arguments written in a call's parentheses, in order.</summary>
    public static IReadOnlyList<SyntaxNode> ArgumentsOf(SyntaxNode call) =>
        [.. call.ChildNodes
            .FirstOrDefault(child => child.Kind == SyntaxKind.ArgumentList)?.ChildNodes ?? []];

    /// <summary>The call a line holds, whether it stands alone or follows a label.</summary>
    public static SyntaxNode? CallIn(SyntaxNode? statement) => statement switch
    {
        { Kind: SyntaxKind.MacroCall } => statement,
        { Kind: SyntaxKind.LabeledLine } =>
            statement.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.MacroCall),
        _ => null,
    };

    /// <summary>
    /// The blocks a call opens, in order: the one its own line opens, and each
    /// <c>} name {</c> after it, which the block layer makes a sibling rather than a child.
    /// Empty when the call takes no block.
    /// </summary>
    public static IReadOnlyList<SyntaxNode> BlocksOf(SyntaxNode call)
    {
        // The block around the call's line is the call's only when that line opens it: a call
        // written inside another call's block argument is in that block, and opens none.
        if (LineOf(call) is not { Parent: { } block } line || block.Green is not GreenBlock opened
            || opened.BlockKind != BlockKind.MacroBlock || block.Parent is not { } container
            || block.ChildNodes.Length == 0 || block.ChildNodes[0] != line)
        {
            return [];
        }

        var blocks = new List<SyntaxNode>();
        var started = false;
        foreach (var sibling in container.ChildNodes)
        {
            if (sibling == block)
                started = true;
            else if (!started)
                continue;
            else if (sibling.Green is not GreenBlock { BlockKind: BlockKind.MacroBlock }
                || sibling.ChildNodes is not [{ Statement.Kind: SyntaxKind.BlockContinuation }, ..])
            {
                break;
            }
            blocks.Add(sibling);
        }
        return blocks;
    }

    /// <summary>The line a node was written on.</summary>
    public static SyntaxNode? LineOf(SyntaxNode node)
    {
        for (var above = node; above is not null; above = above.Parent)
        {
            if (above.Green is GreenLine)
                return above;
        }
        return null;
    }

    /// <summary>The lines of a block argument: everything between the braces around it.</summary>
    public static IReadOnlyList<SyntaxNode> LinesOf(SyntaxNode block)
    {
        var lines = block.ChildNodes;
        var last = lines.Length;

        // The line that opens the block is its first; the `}` that closes it is a line of its
        // own, and a `} name {` belongs to the block it opens rather than to this one.
        if (last > 1 && lines[last - 1].Statement is { Kind: SyntaxKind.BlockCloseLine })
            last--;
        return [.. lines.Take(last).Skip(1)];
    }

    /// <summary>
    /// Reports any macro that can reach itself, directly or through others. nt65 checks this
    /// from the names its bodies resolved to, without expanding anything, which is what makes
    /// every expansion bounded.
    /// <para>
    /// Macros that reach one another are one problem, reported once, at the one of them that
    /// comes first in the program by file and position, on the first call on the way back to
    /// it. Which macro the check starts from does not change what is said or where, so a
    /// program checked a few files at a time says what it says checked whole.
    /// </para>
    /// <para>
    /// <paramref name="current"/> is what a callee stands for now: a macro of a file that was
    /// not read again may still name an earlier version of another file's macro.
    /// <paramref name="report"/> is given the macro each problem is reported for.
    /// </para>
    /// </summary>
    public static void CheckRecursion(
        IEnumerable<Symbol> macros, Func<Symbol, Symbol> current, Action<Symbol, Diagnostic> report)
    {
        var reaches = new Dictionary<Symbol, HashSet<Symbol>>();

        HashSet<Symbol> Reaches(Symbol macro)
        {
            if (reaches.TryGetValue(macro, out var known))
                return known;
            var found = new HashSet<Symbol>();
            var pending = new Stack<Symbol>([macro]);
            while (pending.TryPop(out var next))
            {
                foreach (var (callee, _) in next.Calls)
                {
                    if (found.Add(current(callee)))
                        pending.Push(current(callee));
                }
            }
            return reaches[macro] = found;
        }

        foreach (var macro in macros.Distinct())
        {
            if (!Reaches(macro).Contains(macro))
                continue;
            var cycle = Reaches(macro).Where(other => Reaches(other).Contains(macro)).ToHashSet();
            if (cycle.Any(other => First(other, macro)))
                continue;

            // The cycle is named by what it goes through, so a reader can see which call to
            // break rather than only that something is circular.
            var path = new List<Symbol>();
            var visited = new HashSet<Symbol>();
            if (Back(macro) is { } at)
            {
                var through = path.Skip(1).Select(step => $"`{step.Name}`").ToList();
                report(macro, new Diagnostic(at, Severity.Error,
                    through.Count == 0
                        ? $"`{macro.Name}` calls itself, and every expansion has to be bounded"
                        : $"`{macro.Name}` calls itself through {string.Join(", ", through)}, "
                            + "and every expansion has to be bounded"));
            }

            // The first call, in the order the bodies are written, that leads back to the macro.
            Span? Back(Symbol from)
            {
                visited.Add(from);
                path.Add(from);
                foreach (var (named, at) in from.Calls)
                {
                    var callee = current(named);
                    if (callee == macro)
                        return at;
                    if (cycle.Contains(callee) && !visited.Contains(callee) && Back(callee) is { } found)
                        return found;
                }
                path.RemoveAt(path.Count - 1);
                return null;
            }
        }

        static bool First(Symbol other, Symbol than) =>
            string.CompareOrdinal(other.Tree.Path, than.Tree.Path) is var byPath
            && (byPath < 0 || byPath == 0 && other.NameSpan.Start < than.NameSpan.Start);
    }

    /// <summary>
    /// Every macro an expansion of <paramref name="called"/> can reach, the ones they call
    /// included. What all of their bodies use is what a file that calls them has to bring in.
    /// </summary>
    public static IReadOnlyList<Symbol> Reachable(IEnumerable<Symbol> called)
    {
        var found = new List<Symbol>();
        var seen = new HashSet<Symbol>();

        void Visit(Symbol macro)
        {
            if (!seen.Add(macro))
                return;
            found.Add(macro);
            foreach (var (callee, _) in macro.Calls)
                Visit(callee);
        }

        foreach (var macro in called)
            Visit(macro);
        return found;
    }

    /// <summary>
    /// A symbol an exported macro uses without being given it must itself be exported: the
    /// expansion lands in another file, where an unexported name means nothing.
    /// </summary>
    public static void CheckExportedUses(
        IEnumerable<Symbol> macros, Func<Symbol, bool> isExported, List<Diagnostic> diagnostics)
    {
        foreach (var macro in macros)
        {
            if (!isExported(macro))
                continue;
            foreach (var (used, at) in macro.Uses)
            {
                if (used.Tree != macro.Tree || used.IsDefine || isExported(used))
                    continue;
                diagnostics.Add(new Diagnostic(macro.DeclarationSpan, Severity.Error,
                    $"`{macro.Name}!` is exported but names `{used.DisplayName}`, which is not: a macro expands "
                    + "in the module that calls it, and what it names there has to be exported",
                    [new RelatedSpan(at, "named here")]));
            }
        }
    }

    /// <summary>
    /// Why a macro body may not hold this statement, or null when it may. Each of these
    /// would either declare a name in the caller or make something program-wide depend on
    /// how many times the macro is called.
    /// </summary>
    public static string? Forbidden(SyntaxNode statement) => (statement.IsExported ? SyntaxKind.ExportDirective : statement.Kind) switch
    {
        SyntaxKind.ExportDirective =>
            "`.export` belongs outside a macro body: other files resolve names through the "
            + "export map, and a body cannot add to it",
        SyntaxKind.ImportDirective =>
            "`.import` belongs outside a macro body: a body resolves names where the macro is "
            + "declared, and the output imports whatever an expansion uses",
        SyntaxKind.CpuDirective => "`.cpu` belongs outside a macro body: the CPU is program-wide",
        SyntaxKind.SegmentDeclaration =>
            "a segment declaration belongs outside a macro body: a segment is declared exactly "
            + "once for the program, and this one would be declared once per call",
        SyntaxKind.ProcDeclaration or SyntaxKind.ExternProcDeclaration =>
            "`.proc` belongs outside a macro body: a routine's name and signature are part of "
            + "the file's interface. Take a `block` parameter and let the caller declare the routine",
        SyntaxKind.MacroDeclaration =>
            "`.macro` belongs outside a macro body: a definition there could capture the "
            + "enclosing macro's parameters",
        SyntaxKind.FuncDeclaration =>
            "`.func` belongs outside a macro body: a definition there could capture the "
            + "enclosing macro's parameters",
        SyntaxKind.SignatureDeclaration =>
            "`.signature` belongs outside a macro body: a signature set is used in signatures, which "
            + "are part of the file's interface",
        _ => null,
    };
}
