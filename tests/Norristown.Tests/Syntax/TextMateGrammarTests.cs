using Norristown.Syntax;
using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Syntax;

public sealed class TextMateGrammarTests
{
    // Lines that exercise the places a regex and the lexer could disagree.
    private const string Tricky = """
        z: ::foo
        lda z:ptr+1
        x: .word
        @loop: bne @loop
        .byte 'A'..'Z', ';', "a\"b;c", $1F, %1010, 65c02, 6502
        lda #a->b
        lda #a - >b
        v = 10 .mod 3 .modx
        set16!(ptr, SCREEN)
        lda !flag
        x != y
        gfx::init::x
        a8: ldax bbr7 tad
        """;

    [Fact]
    public void GrammarFileIsUpToDate()
    {
        var generated = TextMateGrammar.Generate();
        if (FixtureRunner.UpdateMode)
        {
            Repo.WriteText(TextMateGrammar.Path, generated);
            return;
        }
        Assert.True(File.Exists(TextMateGrammar.Path) && Repo.ReadText(TextMateGrammar.Path) == generated,
            "editors/vscode/syntaxes/nt65.tmLanguage.json is out of date; run scripts/test.ps1 -Update");
    }

    [Fact]
    public void GrammarScopesEveryTokenAsTheLexerClassifiesIt()
    {
        var sources = DesignCorpus.Blocks.Select(b => (Name: b.ToString(), b.Text)).Append(("tricky", Tricky));
        var failures = new List<string>();
        foreach (var (name, text) in sources)
        {
            var tree = SyntaxTree.Parse(name, text);
            for (var l = 0; l < tree.Lines.Length; l++)
            {
                var line = tree.Lines[l];
                var lineText = line.ToFullString().TrimEnd('\r', '\n');
                var scopes = TextMateGrammar.Scope(lineText);
                var offset = 0;
                for (var t = 0; t < line.Tokens.Length; t++)
                {
                    var token = line.Tokens[t];
                    var start = offset + token.LeadingWidth;
                    var triviaStart = start + token.Text.Length;
                    offset += token.FullWidth;
                    var comment = token.LeadingTrivia.Concat(token.TrailingTrivia).FirstOrDefault(x => x.Kind == SyntaxKind.CommentTrivia);
                    if (comment is not null)
                    {
                        var at = lineText.IndexOf(comment.Text, token.Kind == SyntaxKind.EndOfLine ? 0 : triviaStart, StringComparison.Ordinal);
                        if (scopes[at..].Any(s => s != TextMateGrammar.Comment))
                            failures.Add($"{name}:{l + 1}: comment `{comment.Text}` is not scoped as a comment");
                    }
                    if (token.Kind is SyntaxKind.EndOfLine or SyntaxKind.BadToken || token.Error is not null)
                        continue;
                    var expected = TextMateGrammar.Expected(line, t);
                    var actual = scopes[start..triviaStart].Distinct().ToList();
                    if (actual.Count != 1 || actual[0] != expected)
                        failures.Add($"{name}:{l + 1}: `{token.Text}` is {token.Kind}, expected {expected ?? "no scope"}, grammar gives {string.Join(" + ", actual.Select(s => s ?? "no scope"))}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
