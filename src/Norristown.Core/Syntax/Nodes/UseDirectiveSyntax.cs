using System.Collections.Immutable;

namespace Norristown.Syntax;

// The half of the node the table cannot say: reading the path a .use starts with. The rest of
// the class, and its summary, are generated.
public sealed partial class UseDirectiveSyntax
{
    private ImmutableArray<SyntaxToken> ReadPath()
    {
        var names = ImmutableArray.CreateBuilder<SyntaxToken>();
        var tokens = ChildTokens;
        if (NameAt(1) is not { } first)
            return [];
        names.Add(first);
        for (var i = 2; i + 1 < tokens.Length && tokens[i].Kind == SyntaxKind.ColonColon && NameAt(i + 1) is { } next; i += 2)
            names.Add(next);
        return names.ToImmutable();
    }
}
