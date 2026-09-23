using Norristown.Syntax;
using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Syntax;

public sealed class TextMateGrammarTests
{
    // Lines that exercise the places a regex and the parser could disagree.
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
        .export .enum Joy { ; {
            A = $80
            inc
        }
        .enum {
            FIRST
        }
        .struct Point {
            x: .byte
            .union {
                y: .word
                @tag: .byte
            }
        }
        .data one: .type Point { x = 1, y = { a = 2 } }
        .data many: .type Point[4] {
            { x = 1, y = 2 }
        }
        .data long: .type Point {
            x = 3 ; x = 4
            y = x
        }
        .struct Box {
            corner: .type Point
        }
        .macro mac(dest: operand, count, w: one(a, b) = a): a8 {
            mac!(dest = 1, 2, w = b)
        }
        mac!(dest = {x}, w = (a))
        .macro typed(p: const(0..15), q: operand(imm, zp, nope), r: Joy, s: list(one(a, lda)), t: gfx::Kind, u: ident = x) {
        }
        .func twice(n, m) = n * 2
        .export .proc main: a8, dp = 0 {
        }
        .proc far = $1234
        .scope gfx {
        }
        .export .signature std = a8, dp = 0
        .charmap text {
        }
        .data table: .byte 1
        .list L {
        }
        .frame f: Point
        .export K = Joy::A
        .config C = 2
        K == 1
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

    /// <summary>
    /// Every token is scoped as the lexer classifies it, and every name as the parser reads it: a
    /// declaration by what it declares, a member of an enum, a struct, a union or a record as a
    /// member, a parameter as a parameter, and any other name plainly.
    /// </summary>
    [Fact]
    public void GrammarScopesEveryTokenAsTheParserReadsIt()
    {
        var sources = DesignCorpus.Blocks.Select(b => (Name: b.ToString(), b.Text)).Append(("tricky", Tricky));
        var failures = new List<string>();
        foreach (var (name, text) in sources)
        {
            var tree = SyntaxTree.Parse(name, text);
            var bodies = Bodies(tree);
            var syntax = tree.Root.DescendantNodes().OfType<LineSyntax>().ToList();
            var lineScopes = TextMateGrammar.Scope([.. tree.Lines.Select(line => line.ToFullString().TrimEnd('\r', '\n'))]);
            for (var l = 0; l < tree.Lines.Length; l++)
            {
                var line = tree.Lines[l];
                var lineText = line.ToFullString().TrimEnd('\r', '\n');
                var scopes = lineScopes[l];
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
                    if (token.Kind is SyntaxKind.EndOfLine or SyntaxKind.BadToken || token.ContainsDiagnostics)
                        continue;
                    var expected = TextMateGrammar.Expected(syntax[l], t, bodies.GetValueOrDefault(l));
                    var actual = scopes[start..triviaStart].Distinct().ToList();
                    if (actual.Count != 1 || actual[0] != expected)
                        failures.Add($"{name}:{l + 1}: `{token.Text}` is {token.Kind}, expected {expected ?? "no scope"}, grammar gives {string.Join(" + ", actual.Select(s => s ?? "no scope"))}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// For each line, the kind of block whose body it is in, looking through conditionals and
    /// repetitions, which the grammar does not track. A block's opener line belongs to the block
    /// around it, not to the block it opens.
    /// </summary>
    private static Dictionary<int, BlockKind> Bodies(SyntaxTree tree)
    {
        var bodies = new Dictionary<int, BlockKind>();
        foreach (var line in tree.Root.DescendantNodes().OfType<LineSyntax>())
        {
            SyntaxNode child = line;
            for (var parent = line.Parent; parent is not null; child = parent, parent = parent.Parent)
            {
                if (parent is not BlockSyntax block || block.Opener == child
                    || block.BlockKind is BlockKind.If or BlockKind.Repeat or BlockKind.Each)
                {
                    continue;
                }
                bodies[line.LineIndex] = block.BlockKind;
                break;
            }
        }
        return bodies;
    }
}
