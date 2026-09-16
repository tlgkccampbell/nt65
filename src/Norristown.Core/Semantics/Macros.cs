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
        if (LineOf(call) is not { Parent: { } block } || block.Green is not GreenBlock opened
            || opened.BlockKind != BlockKind.MacroBlock || block.Parent is not { } container)
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
    /// every expansion bounded (§11.1).
    /// </summary>
    public static void CheckRecursion(IEnumerable<Symbol> macros, List<Diagnostic> diagnostics)
    {
        var done = new HashSet<Symbol>();
        var path = new List<Symbol>();
        var reported = new HashSet<Symbol>();

        void Visit(Symbol macro)
        {
            if (!done.Add(macro))
                return;
            path.Add(macro);
            foreach (var (callee, at) in macro.Calls)
            {
                var start = path.IndexOf(callee);
                if (start < 0)
                {
                    Visit(callee);
                    continue;
                }
                if (!reported.Add(callee))
                    continue;

                // The cycle is named by what it goes through, so a reader can see which
                // call to break rather than only that something is circular.
                var through = path.Skip(start + 1).Select(step => $"`{step.Name}`").ToList();
                diagnostics.Add(new Diagnostic(at, Severity.Error,
                    through.Count == 0
                        ? $"`{callee.Name}` calls itself, and every expansion has to be bounded"
                        : $"`{callee.Name}` calls itself through {string.Join(", ", through)}, "
                            + "and every expansion has to be bounded"));
            }
            path.RemoveAt(path.Count - 1);
        }

        foreach (var macro in macros)
            Visit(macro);
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
    /// expansion lands in another file, where an unexported name means nothing (§11.1).
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
                diagnostics.Add(new Diagnostic(at, Severity.Error,
                    $"`{macro.Name}` is exported and uses `{used.DisplayName}`, which is not. A macro "
                    + "expands in the file that calls it, and what it names there has to be reachable",
                    [new RelatedSpan(used.DeclarationSpan, "declared here")]));
            }
        }
    }

    /// <summary>
    /// Why a macro body may not hold this statement, or null when it may. Each of these
    /// would either declare a name in the caller or make something program-wide depend on
    /// how many times the macro is called (§11.3).
    /// </summary>
    public static string? Forbidden(SyntaxNode statement) => statement.Kind switch
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
        _ => null,
    };
}
