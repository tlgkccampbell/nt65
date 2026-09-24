using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Reads a macro's syntax, including the parts of a definition and of a call and the statements a
/// body may not contain. Binding, expansion and the editor all ask the same questions of the
/// same lines, so they share these methods.
/// </summary>
public static class Macros
{
    /// <summary>
    /// Returns the value <paramref name="parameter"/> gets when a call leaves it out, or null when
    /// it has no default.
    /// </summary>
    public static SyntaxNode? DefaultOf(MacroParameterSyntax parameter) =>
        parameter.Default is { } given and not EmptyBlockSyntax ? given : null;

    /// <summary>
    /// Returns <paramref name="parameter"/> as the analysis sees it, given the symbol its name
    /// declares.
    /// </summary>
    public static MacroParameter Describe(MacroParameterSyntax parameter, Symbol symbol) =>
        new(symbol,
            ArgumentKind.Read(parameter.ParameterKind),
            DefaultOf(parameter),
            parameter.Default is EmptyBlockSyntax);

    /// <summary>Returns the name of the macro <paramref name="call"/> calls, or null if it has none.</summary>
    public static SyntaxToken? CalleeOf(MacroCallSyntax call) =>
        call.Name is { Kind: SyntaxKind.Identifier or SyntaxKind.Mnemonic } name ? name : null;

    /// <summary>
    /// Returns the macro call <paramref name="statement"/> contains, whether it stands alone or
    /// follows a label, or null if there is none.
    /// </summary>
    public static MacroCallSyntax? CallIn(StatementSyntax? statement) => statement switch
    {
        MacroCallSyntax call => call,
        LabeledLineSyntax labeled => labeled.Statement as MacroCallSyntax,
        _ => null,
    };

    /// <summary>
    /// Returns the blocks <paramref name="call"/> opens, in order. These are the block its own
    /// line opens and each <c>} name {</c> block after it, which the block layer makes a sibling
    /// rather than a child. The list is empty when the call takes no block.
    /// </summary>
    public static IReadOnlyList<BlockSyntax> BlocksOf(MacroCallSyntax call)
    {
        // The block around the call's line belongs to the call only when that line opens it. A
        // call inside another call's block argument is in that block and opens none.
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

    /// <summary>
    /// Returns the lines of a block argument, which are everything between the braces around it.
    /// </summary>
    public static IReadOnlyList<SyntaxNode> LinesOf(BlockSyntax block)
    {
        var lines = block.ChildNodes;
        var last = lines.Length;

        // The line that opens the block is its first line. The `}` that closes it is a line of
        // its own, and a `} name {` belongs to the block it opens, not to this one.
        if (last > 1 && lines[last - 1] is LineSyntax { Statement: BlockCloseLineSyntax })
            last--;
        return [.. lines.Take(last).Skip(1)];
    }

    /// <summary>
    /// Reports a diagnostic for each macro that can reach itself, directly or through others.
    /// nt65 checks this from the names the bodies resolved to, without expanding anything, and
    /// this check guarantees that every expansion is bounded.
    /// <para>
    /// Macros that reach one another form one problem, which is reported once. It is reported for
    /// the macro that comes first in the program by file and position, at the first call on the
    /// path back to it. The macro the check starts from does not change what is reported or
    /// where, so a program checked a few files at a time gets the same report as one checked
    /// whole.
    /// </para>
    /// <para>
    /// <paramref name="current"/> maps a callee to its current symbol, because a macro in a file
    /// that was not read again may still name an earlier version of another file's macro.
    /// <paramref name="report"/> receives the macro each problem is reported for.
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

            // The message lists the macros the cycle goes through, so a reader can see which call
            // to break rather than only that something is circular.
            var path = new List<Symbol>();
            var visited = new HashSet<Symbol>();
            if (Back(macro) is { } at)
            {
                var through = path.Skip(1).Select(step => $"`{step.Name}`").ToList();
                report(macro, new Diagnostic(at, Catalogue.MacroRecursive.Message(
                    macro.Name,
                    through.Count == 0 ? "" : $" through {string.Join(", ", through)}")));
            }

            // Finds the first call, in source order across the bodies, that leads back to the
            // macro.
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
    /// Returns every macro an expansion of <paramref name="called"/> can reach, including the
    /// macros in <paramref name="called"/> themselves. A file that calls them must bring in
    /// everything that all of their bodies use.
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
    /// Reports a diagnostic for each symbol of its own file that an exported macro uses without
    /// being given it, when that symbol is not exported. The expansion lands in another file,
    /// where an unexported name means nothing.
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
                if (used.Tree != macro.Tree || isExported(used))
                    continue;
                diagnostics.Add(new Diagnostic(macro.DeclarationSpan,
                    Catalogue.MacroNamesUnexported.Message(macro.Name, used.DisplayName),
                    [new RelatedSpan(at, "named here")]));
            }
        }
    }

    /// <summary>
    /// Returns the message explaining why a macro body may not contain
    /// <paramref name="statement"/>, or null when it may. Each forbidden statement would either
    /// declare a name in the caller or make something program-wide depend on how many times the
    /// macro is called.
    /// </summary>
    public static DiagnosticMessage? Forbidden(StatementSyntax statement) => Refused(statement) is { } why
        ? Catalogue.DeclarationInAMacroBody.Message(why.What, why.Because)
        : (DiagnosticMessage?)null;

    /// <summary>
    /// Returns the two halves of the <see cref="Forbidden"/> message, what is refused and why, or
    /// null when the statement is allowed. Whether a directive is refused comes from its
    /// placement, which the editor reads too. A declaration after <c>.export</c> counts as the
    /// <c>.export</c>. A <c>.use</c> or a <c>.fallthrough</c> is reported by its own rule.
    /// </summary>
    private static (string What, string Because)? Refused(StatementSyntax statement)
    {
        var kind = statement.IsExported ? DirectiveKind.Export : statement.DirectiveKind;
        if (!SyntaxFacts.PlacementOf(kind).IsBarredBy(DirectiveNesting.MacroBody))
            return null;
        return kind switch
        {
            DirectiveKind.Export =>
                ("`.export`", "other files resolve names through the export map, and a body cannot add to it"),
            DirectiveKind.Import =>
                ("`.import`", "a body resolves names where the macro is declared, and the output imports whatever "
                    + "an expansion uses"),
            DirectiveKind.Cpu => ("`.cpu`", "the CPU is program-wide"),

            // A segment block or region only places what the body holds, so only a declaration
            // is refused.
            DirectiveKind.Segment when statement is SegmentDeclarationSyntax =>
                ("a segment declaration",
                    "a segment is declared exactly once for the program, and this one would be declared once per call"),
            DirectiveKind.MultiProc =>
                ("`.multiproc`", "a routine's name and signature are part of the file's interface, and a body declares "
                    + "nothing in its caller"),
            DirectiveKind.Proc =>
                ("`.proc`", "a routine's name and signature are part of the file's interface. Take a `block` parameter "
                    + "and let the caller declare the routine"),
            DirectiveKind.Macro =>
                ("`.macro`", "a definition there could capture the enclosing macro's parameters"),
            DirectiveKind.Func =>
                ("`.func`", "a definition there could capture the enclosing macro's parameters"),
            DirectiveKind.Signature =>
                ("`.signature`", "a signature set is used in signatures, which are part of the file's interface"),
            _ => null,
        };
    }
}
