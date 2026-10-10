using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Reads the numbers of a semantic tokens answer back into tokens. Each token is five numbers:
/// the lines down from the token before it, the character it starts at, counted from the token
/// before it when both are on one line, its length, its type and its modifiers.
/// </summary>
internal static class DecodedTokens
{
    /// <summary>
    /// Returns each token of <paramref name="tokens"/> as its text in <paramref name="source"/>,
    /// its type and its modifiers, separated by spaces.
    /// </summary>
    public static List<string> Describe(SemanticTokensLegend legend, string source, SemanticTokens tokens)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var described = new List<string>();
        foreach (var (line, character, length, type, modifiers) in Each(tokens))
        {
            var named = Enumerable.Range(0, legend.TokenModifiers.Count)
                .Where(bit => (modifiers & (1 << bit)) != 0)
                .Select(bit => legend.TokenModifiers[bit]);
            described.Add(string.Join(' ', [lines[line].Substring(character, length), legend.TokenTypes[type], .. named]));
        }
        return described;
    }

    /// <summary>Returns the type of the token at each position that has one.</summary>
    public static Dictionary<(int Line, int Character), string> TypesAt(SemanticTokensLegend legend, SemanticTokens tokens)
    {
        var at = new Dictionary<(int Line, int Character), string>();
        foreach (var (line, character, _, type, _) in Each(tokens))
            at[(line, character)] = legend.TokenTypes[type];
        return at;
    }

    private static IEnumerable<(int Line, int Character, int Length, int Type, int Modifiers)> Each(SemanticTokens tokens)
    {
        var (line, character) = (0, 0);
        for (var i = 0; i < tokens.Data.Count; i += 5)
        {
            line += tokens.Data[i];
            character = tokens.Data[i] == 0 ? character + tokens.Data[i + 1] : tokens.Data[i + 1];
            yield return (line, character, tokens.Data[i + 2], tokens.Data[i + 3], tokens.Data[i + 4]);
        }
    }
}
