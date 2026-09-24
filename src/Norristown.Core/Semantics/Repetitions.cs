using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Unrolls a <c>.repeat</c> or an <c>.each</c> into its iterations, one per count, per list item
/// or per enum member. None of this is kept for the output, so layout and emission each ask for
/// the iterations and walk the body once per iteration. The emitter decides, from the
/// iterations, whether the lines a counted repetition produces can be emitted once rather than
/// once per iteration.
/// </summary>
public static class Repetitions
{
    /// <summary>
    /// The maximum number of iterations a repetition is unrolled for. A body is emitted once per
    /// iteration, so this is the same bound, with the same number, that already limits the
    /// statements a file's expansions produce. Past it, nt65 stops rather than filling memory
    /// with iterations nobody could assemble.
    /// </summary>
    public const int MaximumIterations = 65536;

    /// <summary>
    /// Returns the iterations of <paramref name="block"/> inside <paramref name="outer"/>, one
    /// <see cref="Expansion"/> each. A count or a list nt65 cannot read is reported into
    /// <paramref name="diagnostics"/>, when it is not null, and gives no iterations at all.
    /// </summary>
    public static IReadOnlyList<Expansion> Of(
        SemanticModel model, BlockSyntax block, Expansion? outer, List<Diagnostic>? diagnostics)
    {
        var opener = block.Opener.Statement;
        var binding = BindingOf(model, opener);
        return opener switch
        {
            RepeatDirectiveSyntax repeat => Counted(model, block, repeat.Expression, binding, outer, diagnostics),
            EachDirectiveSyntax each => Walked(model, block, each.Expression, binding, outer, diagnostics),
            MultiProcDeclarationSyntax family =>
                Walked(model, block, family.Expression, binding, outer, diagnostics, folded: true),

            // Any other opener, such as a `.repeat` after a label, opens no repetition and gives
            // no iterations. The parser has already reported its problem, and reading it as an
            // `.each` would only report a different problem instead.
            _ => [],
        };
    }

    /// <summary>
    /// Returns the diagnostic for a repetition of <paramref name="count"/> iterations, which is
    /// too many.
    /// </summary>
    /// <param name="count">The number of iterations the repetition runs.</param>
    public static DiagnosticMessage Beyond(long count) => Catalogue.RepeatTooMany.Message(count, MaximumIterations);

    /// <summary>Returns the name a repetition binds, or null when it names none.</summary>
    public static Symbol? BindingOf(SemanticModel model, StatementSyntax opener)
    {
        var name = opener switch
        {
            RepetitionDirectiveSyntax repetition => repetition.Name,
            MultiProcDeclarationSyntax family => family.Name,
            _ => null,
        };
        // The name is looked up in the file that contains it, not at a position in the model's
        // own file. A macro another module exports is expanded here, and its body's repetitions
        // bind names of that module's file.
        return name is { IsMissing: false } bound && model.SymbolAt(bound) is { Kind: SymbolKind.Binding } declared
            ? declared
            : null;
    }

    /// <summary>
    /// Returns the iterations of <c>.repeat count, i</c>. The name counts from zero, as an index
    /// does.
    /// </summary>
    private static IReadOnlyList<Expansion> Counted(
        SemanticModel model, BlockSyntax block, SyntaxNode counted, Symbol? binding, Expansion? outer,
        List<Diagnostic>? diagnostics)
    {
        if (model.ValueOf(counted, outer).AsNumber() is not { } count)
        {
            Report(model, diagnostics, counted, outer, Catalogue.RepeatCountNotConstant);
            return [];
        }
        if (count < 0)
        {
            Report(model, diagnostics, counted, outer, Catalogue.RepeatCountNegative.Message(count));
            return [];
        }
        if (count > MaximumIterations)
        {
            Report(model, diagnostics, counted, outer, Beyond(count));
            return [];
        }

        var iterations = new List<Expansion>((int)count);
        for (var i = 0; i < count; i++)
            iterations.Add(Expansion.Iteration(outer, block, binding, Value.Of(i), null, i));
        return iterations;
    }

    /// <summary>
    /// Returns the iterations of <c>.each what, h</c>. The name takes each item of a list, or
    /// each member of an enum, in source order.
    /// </summary>
    private static IReadOnlyList<Expansion> Walked(
        SemanticModel model, BlockSyntax block, SyntaxNode walked, Symbol? binding, Expansion? outer,
        List<Diagnostic>? diagnostics, bool folded = false)
    {
        // `.multiproc` names its routines after an enum's members, so a list gives no iterations.
        // The binder has already reported the list where the family is declared.
        if (folded)
        {
            return model.SymbolOf(walked) is { Kind: SymbolKind.Enum, Body: { } enumerated }
                ? [.. enumerated.Symbols.Where(member => member.IsEnumMember).Select(
                    (member, i) => Expansion.Iteration(outer, block, binding, member.Value, null, i, member))]
                : [];
        }

        // A `list` parameter iterates over the items the call gave it. Where its items are words,
        // the binding is the word itself, because a condition can do nothing else with a word.
        if (model.SymbolOf(walked) is { Kind: SymbolKind.MacroParameter, Parameter: { } parameter }
            && parameter.Kind == ParameterKind.List)
        {
            if (model.ArgumentFor(parameter.Symbol, outer) is not { } argument)
                return [];
            var words = parameter.Accepts.Element?.Kind == ParameterKind.One;
            return [.. argument.Items.Select((item, i) => Expansion.Iteration(
                outer, block, binding, words ? Value.Word(Word(item)) : Value.Unknown, words ? null : item, i))];
        }

        // A list item is kept as it was written, because the items may be labels, which have no
        // value at all, and a name bound to a label must behave as that label.
        if (model.ItemsOf(walked) is { } items)
            return [.. items.Select((item, i) => Expansion.Iteration(outer, block, binding, Value.Unknown, item, i))];

        if (model.SymbolOf(walked) is { Kind: SymbolKind.Enum, Body: { } members })
            return [.. members.Symbols.Where(member => member.IsEnumMember).Select(
                (member, i) => Expansion.Iteration(outer, block, binding, member.Value, null, i, member))];

        Report(model, diagnostics, walked, outer, Catalogue.EachNotOverAList);
        return [];
    }

    /// <summary>
    /// Returns the message explaining why a <c>.repeat</c> or <c>.each</c> body may not contain
    /// <paramref name="statement"/>, or null when it may. What the body declares is a different
    /// name on every iteration, and each forbidden statement is one thing for the whole file.
    /// </summary>
    public static DiagnosticMessage? Forbidden(StatementSyntax statement) => Refused(statement) is { } why
        ? Catalogue.DeclarationInARepetition.Message(why.What, why.Because)
        : (DiagnosticMessage?)null;

    /// <summary>
    /// Returns the two halves of the <see cref="Forbidden"/> message, what is refused and why, or
    /// null when the statement is allowed.
    /// </summary>
    private static (string What, string Because)? Refused(StatementSyntax statement) => statement switch
    {
        { IsExported: true } or ExportDirectiveSyntax or ImportDirectiveSyntax =>
            ("an export or an import", "it names one symbol, and the body is expanded once per iteration"),
        CpuDirectiveSyntax => ("`.cpu`", "the CPU is program-wide"),
        SegmentDeclarationSyntax =>
            ("a segment declaration",
                "a segment is declared exactly once for the program, and this one would be declared once per iteration"),
        MultiProcDeclarationSyntax =>
            ("`.multiproc`",
                "it declares one routine per member of an enum, and this one would declare them again on every iteration"),
        ProcDeclarationSyntax or ExternProcDeclarationSyntax =>
            ("`.proc`", "a routine's name and signature are part of the file's interface, and this one would be "
                + "a different routine on every iteration"),
        MacroDeclarationSyntax or FuncDeclarationSyntax or SignatureDeclarationSyntax =>
            ("a definition", "it would be a different one on every iteration, and nothing outside the body could name "
                + "any of them"),
        _ => null,
    };

    /// <summary>Returns the word <paramref name="item"/> was written as, for a list of words.</summary>
    private static string Word(SyntaxNode item) =>
        item is NameExpressionSyntax { Names: [var name, ..] } ? name.Text
        : item.ChildTokens is [var first, ..] ? first.Text
        : item.GetText().Trim();

    private static void Report(
        SemanticModel model, List<Diagnostic>? diagnostics, SyntaxNode node, Expansion? outer, DiagnosticMessage message) =>
        diagnostics?.Add(Expansion.Problem(model.Tree, node.Tree, node.Span, outer, Severity.Error, message));
}
