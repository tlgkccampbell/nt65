using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents a word that a condition in a macro body compares with the value of a parameter.
/// The word is either a mode, as in <c>.mode(src) == imm</c>, or a word that a <c>one(...)</c>
/// lists, as in <c>reg == x</c>. Over a <c>list(one(...))</c> parameter, it can also appear as in
/// <c>.each regs, r { .if r == a }</c>. The word is never looked up as a name, so a misspelling
/// is not reported as an unknown name. Instead, the values the parameter can take decide whether
/// the comparison can ever hold.
/// </summary>
/// <param name="Word">The word, as it appears in the condition.</param>
/// <param name="Compared">
/// The expression the word is compared with, as it appears in the condition, such as
/// <c>.mode(src)</c> or <c>reg</c>.
/// </param>
/// <param name="Name">The parameter, or the repetition's binding, that the word is compared with.</param>
/// <param name="Accepts">The kind that the parameter accepts, which is an <c>operand</c> or a <c>one</c> kind.</param>
/// <param name="IsMode">Whether the word is compared with <c>.mode</c>, and so is a mode.</param>
public sealed record ComparedWord(SyntaxToken Word, string Compared, string Name, ArgumentKind Accepts, bool IsMode)
{
    /// <summary>
    /// Gets the modes <c>.mode</c> returns, which are those an <c>operand(...)</c> may list except
    /// the three direct-page modes.
    /// </summary>
    public static IReadOnlyList<string> Modes { get; } =
        [.. ArgumentKind.OperandModes.Where(mode => !mode.StartsWith("zp", StringComparison.Ordinal))];

    /// <summary>Gets the words for which the comparison can hold.</summary>
    public IReadOnlyList<string> Choices => ChoicesFor(Accepts, IsMode);

    /// <summary>
    /// Gets a value indicating whether the parameter can ever equal the word, so that the
    /// comparison can hold.
    /// </summary>
    public bool CanHold => Choices.Contains(Word.Text, IsMode ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Returns the values a word may have when it is compared with a parameter that accepts
    /// <paramref name="accepts"/>. For an operand, these are the modes <c>.mode</c> may return,
    /// limited to the modes the operand lists when it lists any. For a <c>one</c>, they are the
    /// words it lists.
    /// </summary>
    public static IReadOnlyList<string> ChoicesFor(ArgumentKind accepts, bool isMode)
    {
        if (!isMode)
            return accepts.Words;
        if (accepts.Words.Count == 0)
            return Modes;

        // `.mode` reports a direct-page mode as the matching absolute one (`zpx` as `absx`).
        return [.. accepts.Words
            .Select(mode => mode.StartsWith("zp", StringComparison.Ordinal) ? "abs" + mode[2..] : mode)
            .Distinct()];
    }

    /// <summary>
    /// Returns the kind listing the words <paramref name="symbol"/> may stand for, or null when a
    /// condition cannot compare it with a word. Only a <c>one(...)</c> parameter, or a repetition's
    /// binding over a <c>list(one(...))</c> parameter, can be compared with a word.
    /// </summary>
    public static ArgumentKind? WordsOf(Symbol symbol, Func<NameExpressionSyntax, Symbol?> symbolOf)
    {
        if (symbol.Parameter is { Kind: ParameterKind.One } one)
            return one.Accepts;
        return symbol.Kind == SymbolKind.Binding
            && symbol.Tree.Root.FindToken(symbol.NameSpan.Start).Parent is EachDirectiveSyntax { Expression: NameExpressionSyntax walked }
            && symbolOf(walked) is { Parameter: { Kind: ParameterKind.List, Accepts.Element: { Kind: ParameterKind.One } element } }
                ? element
                : null;
    }

    /// <summary>Returns every word that the conditions in <paramref name="node"/> compare with a parameter.</summary>
    /// <param name="node">The node to search, which is a macro's definition or a whole file.</param>
    /// <param name="symbolOf">
    /// A function that returns the symbol a name refers to, or null for a word that nothing is
    /// declared as.
    /// </param>
    public static IEnumerable<ComparedWord> In(SyntaxNode node, Func<NameExpressionSyntax, Symbol?> symbolOf)
    {
        foreach (var comparison in node.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (Of(comparison, comparison.Left, symbolOf) is { } left)
                yield return left;
            if (Of(comparison, comparison.Right, symbolOf) is { } right)
                yield return right;
        }
    }

    /// <summary>
    /// Returns the compared word that <paramref name="side"/> of <paramref name="comparison"/>
    /// forms. Returns null when the side is not a bare word, when the comparison is not <c>==</c>
    /// or <c>!=</c>, or when the other side does not refer to a parameter that a word can be
    /// compared with.
    /// </summary>
    public static ComparedWord? Of(
        BinaryExpressionSyntax comparison, ExpressionSyntax side, Func<NameExpressionSyntax, Symbol?> symbolOf)
    {
        if (comparison.OperatorToken.Kind is not (SyntaxKind.EqualsEquals or SyntaxKind.BangEquals)
            || side is not NameExpressionSyntax { Names.Length: 1, GlobalToken: null, SimpleName: { } word } name
            || symbolOf(name) is not null)
        {
            return null;
        }
        var other = side == comparison.Left ? comparison.Right : comparison.Left;
        if (other is CallExpressionSyntax { BuiltinKind: BuiltinKind.Mode } call
            && call.Arguments.Arguments is [NameExpressionSyntax argument]
            && symbolOf(argument) is { Parameter: { Kind: ParameterKind.Operand } operand })
        {
            return new ComparedWord(word, $".mode({operand.Name})", operand.Name, operand.Accepts, IsMode: true);
        }
        return other is NameExpressionSyntax compared && symbolOf(compared) is { } symbol
            && WordsOf(symbol, symbolOf) is { } words
                ? new ComparedWord(word, symbol.Name, symbol.Name, words, IsMode: false)
                : null;
    }
}
