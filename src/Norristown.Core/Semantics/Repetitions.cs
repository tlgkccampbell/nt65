using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a <c>.repeat</c> or an <c>.each</c> unrolls to: one turn per count, per list item or
/// per enum member. None of this is kept for the output, so layout and emission each ask for
/// the turns and walk the body once per turn; whether the lines a counted repetition's turns
/// produce can be written out once rather than per turn is for the emitter to decide from them.
/// </summary>
public static class Repetitions
{
    /// <summary>
    /// The most turns a repetition is unrolled for. A body is written out once per turn, so
    /// this is the same bound, with the same number, that already limits the statements a
    /// file's expansions produce: past it nt65 stops rather than filling memory with turns
    /// nobody could assemble.
    /// </summary>
    public const int MaximumTurns = 65536;

    /// <summary>
    /// The turns of <paramref name="block"/>, inside <paramref name="outer"/>. A count or a
    /// list nt65 cannot read is reported into <paramref name="diagnostics"/>, when it is not
    /// null, and gives no turns at all.
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

            // An opener that is none of them, such as a `.repeat` written after a label, opens
            // no repetition and gives no turns. The parser has already reported what is wrong
            // with it, and reading it as an `.each` would only report something else instead.
            _ => [],
        };
    }

    /// <summary>The diagnostic for a repetition of <paramref name="count"/> turns, which is too many.</summary>
    /// <param name="count">How many turns it runs.</param>
    public static DiagnosticMessage Beyond(long count) => Catalogue.RepeatTooMany.Says(count, MaximumTurns);

    /// <summary>The name a repetition binds, or null when it names none.</summary>
    public static Symbol? BindingOf(SemanticModel model, StatementSyntax opener)
    {
        var name = opener switch
        {
            RepetitionDirectiveSyntax repetition => repetition.Name,
            MultiProcDeclarationSyntax family => family.Name,
            _ => null,
        };
        // The name is looked up where it was written rather than at a position in the model's
        // own file: a macro another module exports is expanded here, and its body's repetitions
        // bind names of that module's file.
        return name is { IsMissing: false } bound && model.SymbolAt(bound) is { Kind: SymbolKind.Binding } declared
            ? declared
            : null;
    }

    /// <summary><c>.repeat count, i</c>: the name counts from zero, as an index does.</summary>
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
            Report(model, diagnostics, counted, outer, Catalogue.RepeatCountNegative.Says(count));
            return [];
        }
        if (count > MaximumTurns)
        {
            Report(model, diagnostics, counted, outer, Beyond(count));
            return [];
        }

        var turns = new List<Expansion>((int)count);
        for (var i = 0; i < count; i++)
            turns.Add(Expansion.Turn(outer, block, binding, Value.Of(i), null, i));
        return turns;
    }

    /// <summary>
    /// <c>.each what, h</c>: the name is each item of a list, or each member of an enum, in
    /// the order they are written.
    /// </summary>
    private static IReadOnlyList<Expansion> Walked(
        SemanticModel model, BlockSyntax block, SyntaxNode walked, Symbol? binding, Expansion? outer,
        List<Diagnostic>? diagnostics, bool folded = false)
    {
        // `.multiproc` names its routines after an enum's members, so a list gives no turns; the
        // binder has already reported it where the family is declared.
        if (folded)
        {
            return model.SymbolOf(walked) is { Kind: SymbolKind.Enum, Body: { } enumerated }
                ? [.. enumerated.Symbols.Where(member => member.IsEnumMember).Select(
                    (member, i) => Expansion.Turn(outer, block, binding, member.Value, null, i, member))]
                : [];
        }

        // A `list` parameter walks whatever the call gave it. Where its items are words, the
        // binding is the word itself, which is all a condition can do with one.
        if (model.SymbolOf(walked) is { Kind: SymbolKind.MacroParameter, Parameter: { } parameter }
            && parameter.Kind == ParameterKind.List)
        {
            if (model.ArgumentFor(parameter.Symbol, outer) is not { } argument)
                return [];
            var words = parameter.Accepts.Element?.Kind == ParameterKind.One;
            return [.. argument.Items.Select((item, i) => Expansion.Turn(
                outer, block, binding, words ? Value.Word(Word(item)) : Value.Unknown, words ? null : item, i))];
        }

        // A list item is kept as it was written: the items may be labels, which have no
        // value at all, and a name bound to one has to behave as that label.
        if (model.ItemsOf(walked) is { } items)
            return [.. items.Select((item, i) => Expansion.Turn(outer, block, binding, Value.Unknown, item, i))];

        if (model.SymbolOf(walked) is { Kind: SymbolKind.Enum, Body: { } members })
            return [.. members.Symbols.Where(member => member.IsEnumMember).Select(
                (member, i) => Expansion.Turn(outer, block, binding, member.Value, null, i, member))];

        Report(model, diagnostics, walked, outer, Catalogue.EachNotOverAList);
        return [];
    }

    /// <summary>
    /// Why a <c>.repeat</c> or <c>.each</c> body may not hold this statement, or null when it
    /// may. What the body declares is a different name on every turn, and each of these is
    /// one thing for the whole file.
    /// </summary>
    public static DiagnosticMessage? Forbidden(StatementSyntax statement) => Refused(statement) is { } why
        ? Catalogue.DeclarationInARepetition.Says(why.What, why.Because)
        : (DiagnosticMessage?)null;

    /// <summary>The two halves of that message, what is refused and why, or null when the statement is allowed.</summary>
    private static (string What, string Because)? Refused(StatementSyntax statement) => statement switch
    {
        { IsExported: true } or ExportDirectiveSyntax or ImportDirectiveSyntax =>
            ("an export or an import", "it names one symbol, and the body is written out once per iteration"),
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

    /// <summary>The word an item was written as, for a list of them.</summary>
    private static string Word(SyntaxNode item) =>
        item is NameExpressionSyntax { Names: [var name, ..] } ? name.Text
        : item.ChildTokens is [var first, ..] ? first.Text
        : item.GetText().Trim();

    private static void Report(
        SemanticModel model, List<Diagnostic>? diagnostics, SyntaxNode node, Expansion? outer, DiagnosticMessage message) =>
        diagnostics?.Add(Expansion.Problem(model.Tree, node.Tree, node.Span, outer, Severity.Error, message));
}
