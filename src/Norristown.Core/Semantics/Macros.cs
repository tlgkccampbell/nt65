using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a macro's syntax says: the pieces of a definition and of a call, and the items a
/// body may not hold. Reading them is shared, because binding, expansion and the editor
/// all ask the same questions of the same lines.
/// </summary>
public static class Macros
{
    /// <summary>What a call that leaves the parameter out gets, or null when it has no default.</summary>
    public static SyntaxNode? DefaultOf(MacroParameterSyntax parameter) =>
        parameter.Default is { } written and not EmptyBlockSyntax ? written : null;

    /// <summary>One parameter as the analysis reads it, given the symbol its name declares.</summary>
    public static MacroParameter Describe(MacroParameterSyntax parameter, Symbol symbol) =>
        new(symbol,
            ArgumentKind.Read(parameter.ParameterKind),
            DefaultOf(parameter),
            parameter.Default is EmptyBlockSyntax);

    /// <summary>The macro a call names.</summary>
    public static SyntaxToken? CalleeOf(MacroCallSyntax call) =>
        call.Name is { Kind: SyntaxKind.Identifier } name ? name : null;

    /// <summary>The call a line holds, whether it stands alone or follows a label.</summary>
    public static MacroCallSyntax? CallIn(StatementSyntax? statement) => statement switch
    {
        MacroCallSyntax call => call,
        LabeledLineSyntax labeled => labeled.Statement as MacroCallSyntax,
        _ => null,
    };

    /// <summary>
    /// The blocks a call opens, in order: the one its own line opens, and each
    /// <c>} name {</c> after it, which the block layer makes a sibling rather than a child.
    /// Empty when the call takes no block.
    /// </summary>
    public static IReadOnlyList<BlockSyntax> BlocksOf(MacroCallSyntax call)
    {
        // The block around the call's line is the call's only when that line opens it: a call
        // written inside another call's block argument is in that block, and opens none.
        if (call.FirstAncestorOrSelf<LineSyntax>() is not { Parent: BlockSyntax { BlockKind: BlockKind.MacroBlock, Parent: { } container } block } line
            || block.Opener != line)
        {
            return [];
        }

        var blocks = new List<BlockSyntax> { block };
        foreach (var sibling in container.ChildNodes.SkipWhile(node => node != block).Skip(1))
        {
            if (sibling is not BlockSyntax { BlockKind: BlockKind.MacroBlock, Opener.Statement: BlockContinuationSyntax } next)
                break;
            blocks.Add(next);
        }
        return blocks;
    }

    /// <summary>The lines of a block argument: everything between the braces around it.</summary>
    public static IReadOnlyList<SyntaxNode> LinesOf(BlockSyntax block)
    {
        var lines = block.ChildNodes;
        var last = lines.Length;

        // The line that opens the block is its first; the `}` that closes it is a line of its
        // own, and a `} name {` belongs to the block it opens rather than to this one.
        if (last > 1 && lines[last - 1] is LineSyntax { Statement: BlockCloseLineSyntax })
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
                report(macro, new Diagnostic(at, Catalogue.MacroRecursive.Says(
                    macro.Name,
                    through.Count == 0 ? "" : $" through {string.Join(", ", through)}")));
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
                diagnostics.Add(new Diagnostic(macro.DeclarationSpan,
                    Catalogue.MacroNamesUnexported.Says(macro.Name, used.DisplayName),
                    [new RelatedSpan(at, "named here")]));
            }
        }
    }

    /// <summary>
    /// Why a macro body may not hold this statement, or null when it may. Each of these
    /// would either declare a name in the caller or make something program-wide depend on
    /// how many times the macro is called.
    /// </summary>
    public static DiagnosticMessage? Forbidden(StatementSyntax statement) => Refused(statement) is { } why
        ? Catalogue.DeclarationInAMacroBody.Says(why.What, why.Because)
        : (DiagnosticMessage?)null;

    /// <summary>The two halves of that sentence, or null where the statement may stand.</summary>
    private static (string What, string Because)? Refused(StatementSyntax statement) => statement switch
    {
        { IsExported: true } or ExportDirectiveSyntax =>
            ("`.export`", "other files resolve names through the export map, and a body cannot add to it"),
        ImportDirectiveSyntax =>
            ("`.import`", "a body resolves names where the macro is declared, and the output imports whatever "
                + "an expansion uses"),
        CpuDirectiveSyntax => ("`.cpu`", "the CPU is program-wide"),
        SegmentDeclarationSyntax =>
            ("a segment declaration",
                "a segment is declared exactly once for the program, and this one would be declared once per call"),
        MultiProcDeclarationSyntax =>
            ("`.multiproc`", "a routine's name and signature are part of the file's interface, and a body declares "
                + "nothing in its caller"),
        ProcDeclarationSyntax or ExternProcDeclarationSyntax =>
            ("`.proc`", "a routine's name and signature are part of the file's interface. Take a `block` parameter "
                + "and let the caller declare the routine"),
        MacroDeclarationSyntax =>
            ("`.macro`", "a definition there could capture the enclosing macro's parameters"),
        FuncDeclarationSyntax =>
            ("`.func`", "a definition there could capture the enclosing macro's parameters"),
        SignatureDeclarationSyntax =>
            ("`.signature`", "a signature set is used in signatures, which are part of the file's interface"),
        _ => null,
    };
}
