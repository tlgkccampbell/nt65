using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// A word a macro body's condition compares with what a parameter stands for: a mode, in
/// <c>.mode(src) == imm</c>, or a word a <c>one(...)</c> lists, in <c>reg == x</c> or, over a
/// <c>list(one(...))</c>, in <c>.each regs, r { .if r == a }</c>. A word is never looked up, so
/// a misspelt one is no error of its own; what the parameter may be says whether the
/// comparison can ever hold.
/// </summary>
/// <param name="Word">The word, as the condition writes it.</param>
/// <param name="Compared">What it is compared with, as the condition writes it: <c>.mode(src)</c> or <c>reg</c>.</param>
/// <param name="Name">The parameter, or the repetition's binding, it is compared with.</param>
/// <param name="Accepts">What that takes: the <c>operand</c> or the <c>one</c> kind.</param>
/// <param name="IsMode">Whether the word is compared with <c>.mode</c>, and so is a mode.</param>
public sealed record ComparedWord(SyntaxToken Word, string Compared, string Name, ArgumentKind Accepts, bool IsMode)
{
    /// <summary>The modes <c>.mode</c> gives: those an <c>operand(...)</c> may list but the direct-page three.</summary>
    public static IReadOnlyList<string> Modes { get; } =
        [.. ArgumentKind.OperandModes.Where(mode => !mode.StartsWith("zp", StringComparison.Ordinal))];

    /// <summary>What the word may be for the comparison ever to hold.</summary>
    public IReadOnlyList<string> Choices => ChoicesFor(Accepts, IsMode);

    /// <summary>Whether the parameter may ever be the word, so that the comparison may hold.</summary>
    public bool CanHold => Choices.Contains(Word.Text, IsMode ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// What a word compared with a parameter that takes <paramref name="accepts"/> may be: the
    /// modes <c>.mode</c> may give for an operand, those of the modes it lists when it lists them,
    /// or the words a <c>one</c> lists.
    /// </summary>
    public static IReadOnlyList<string> ChoicesFor(ArgumentKind accepts, bool isMode)
    {
        if (!isMode)
            return accepts.Words;
        if (accepts.Words.Count == 0)
            return Modes;

        // A direct-page mode is one `.mode` spells as the absolute mode it is among.
        return [.. accepts.Words
            .Select(mode => mode.StartsWith("zp", StringComparison.Ordinal) ? "abs" + mode[2..] : mode)
            .Distinct()];
    }

    /// <summary>
    /// The words <paramref name="symbol"/> may stand for, when a condition may compare it with one:
    /// a <c>one(...)</c> parameter, or a repetition's binding over a <c>list(one(...))</c> parameter.
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

    /// <summary>Every word the conditions in <paramref name="node"/> compare with a parameter.</summary>
    /// <param name="node">What to look in: a macro's definition, or a whole file.</param>
    /// <param name="symbolOf">What a name refers to, or null for a word nothing is declared as.</param>
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
    /// The compared word <paramref name="side"/> of <paramref name="comparison"/> is, or null when it
    /// is no bare word, the comparison is not <c>==</c> or <c>!=</c>, or the other side stands for
    /// no parameter a word is compared with.
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
        if (other is CallExpressionSyntax { Function: { } function } call
            && function.Text.Equals(".mode", StringComparison.OrdinalIgnoreCase)
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
