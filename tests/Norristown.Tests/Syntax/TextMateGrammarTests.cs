using Norristown.LanguageServer;
using Norristown.Semantics;
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
        .const C ?= 2
        K == 1
        """;

    private static readonly string GrammarPath = Repo.Path("editors", "vscode", "syntaxes", "nt65.tmLanguage.json");

    [Fact]
    public void GrammarFileIsUpToDate()
    {
        var generated = TextMateGrammar.Generate();
        if (FixtureRunner.UpdateMode)
        {
            Repo.WriteText(GrammarPath, generated);
            return;
        }
        Assert.True(File.Exists(GrammarPath) && Repo.ReadText(GrammarPath) == generated,
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
            // The grammar reads one line of the file at a time, as the lexer does. Where an
            // expression continues, the line the parser reads started some tokens earlier.
            var lineScopes = TextMateTokenizer.Nt65.Scope([.. tree.PhysicalLines.Select(line => line.ToFullString().TrimEnd('\r', '\n'))]);
            var earlier = 0;
            for (var l = 0; l < tree.PhysicalLines.Length; l++)
            {
                var line = tree.PhysicalLines[l];
                var statement = tree.GetLine(l);
                if (statement.LineIndex == l)
                    earlier = 0;
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
                    var expected = Expected(statement, earlier + t, bodies.GetValueOrDefault(statement.LineIndex));
                    var actual = scopes[start..triviaStart].Distinct().ToList();
                    if (actual.Count != 1 || actual[0] != expected)
                        failures.Add($"{name}:{l + 1}: `{token.Text}` is {token.Kind}, expected {expected ?? "no scope"}, grammar gives {string.Join(" + ", actual.Select(s => s ?? "no scope"))}");
                }
                earlier += line.Tokens.Length - 1;
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Returns the scope the grammar should give a token, or null for punctuation, which gets no
    /// scope. The scope follows from how the parser reads the line and from the block it is in.
    /// </summary>
    private static string? Expected(LineSyntax line, int index, BlockKind body)
    {
        var tokens = Tokens(line);
        var token = tokens[index];
        if (IsName(token) && NameScope(tokens, index, body) is { } name)
            return name;
        return token.Kind switch
        {
            SyntaxKind.Identifier => TextMateGrammar.Identifier,
            SyntaxKind.CheapLocal => TextMateGrammar.CheapLocal,
            SyntaxKind.Mnemonic => TextMateGrammar.Mnemonic,
            SyntaxKind.Register => TextMateGrammar.Register,
            SyntaxKind.Directive when token.Text.Equals(".mod", StringComparison.OrdinalIgnoreCase)
                || token.Text.Equals(".in", StringComparison.OrdinalIgnoreCase) => TextMateGrammar.OperatorWord,
            SyntaxKind.Directive => TextMateGrammar.Directive,
            SyntaxKind.NumberLiteral => TextMateGrammar.Number,
            SyntaxKind.CharacterLiteral => TextMateGrammar.Character,
            SyntaxKind.StringLiteral => TextMateGrammar.String,
            SyntaxKind.CpuName => TextMateGrammar.Cpu,
            SyntaxKind.ColonColon or SyntaxKind.Colon or SyntaxKind.Comma or SyntaxKind.OpenParen or SyntaxKind.CloseParen
                or SyntaxKind.OpenBracket or SyntaxKind.CloseBracket or SyntaxKind.OpenBrace or SyntaxKind.CloseBrace => null,
            _ => TextMateGrammar.Operator,
        };
    }

    /// <summary>
    /// Returns the scope of a name the parser reads as a declaration, a member or a parameter, or
    /// null for a name whose role the grammar cannot recognize.
    /// </summary>
    private static string? NameScope(List<SyntaxToken> tokens, int index, BlockKind body)
    {
        if (index > 0 && tokens[index - 1].Kind == SyntaxKind.ColonColon)
            return TextMateGrammar.Identifier;

        // The first name a node holds is the one a declaration declares.
        var token = tokens[index];
        var first = !token.Parent.ChildTokens.TakeWhile(earlier => earlier != token).Any(IsName);
        return token.Parent switch
        {
            // These are the parts of what a macro parameter takes: the kind's word, the modes an
            // `operand` lists and the words a `one` lists. The enum that an enum kind names is a
            // use, which only the server can see.
            ParameterKindSyntax kind when token == kind.Keyword => TextMateGrammar.Kind,
            ModuleDirectiveSyntax module when token == module.Placement => TextMateGrammar.Kind,
            IdentifierNameSyntax { Parent: ParameterKindSyntax { Keyword.Text: var keyword } }
                when keyword.Equals("one", StringComparison.OrdinalIgnoreCase) => TextMateGrammar.EnumMember,
            IdentifierNameSyntax { Parent: ParameterKindSyntax } =>
                ArgumentKind.OperandModes.Contains(token.Text.ToLowerInvariant()) ? TextMateGrammar.Mode : TextMateGrammar.Identifier,
            LabelSyntax when first => body is BlockKind.Struct or BlockKind.Union ? TextMateGrammar.Property : TextMateGrammar.Label,
            ProcDeclarationSyntax or ExternProcDeclarationSyntax or FuncDeclarationSyntax when first => TextMateGrammar.Function,
            MacroDeclarationSyntax when first => TextMateGrammar.Macro,
            MacroParameterSyntax when first => TextMateGrammar.Parameter,
            ImportItemSyntax item when first => item.EqualsToken is not null ? TextMateGrammar.Constant : TextMateGrammar.Variable,
            RepeatDirectiveSyntax or EachDirectiveSyntax or MultiProcDeclarationSyntax => TextMateGrammar.Constant,
            ParameterSyntax => TextMateGrammar.Parameter,
            EnumDeclarationSyntax when first => TextMateGrammar.Enum,
            StructDeclarationSyntax or UnionDeclarationSyntax when first => TextMateGrammar.Struct,
            ScopeDeclarationSyntax when first => TextMateGrammar.Namespace,
            CharmapDeclarationSyntax or SignatureDeclarationSyntax when first => TextMateGrammar.Type,
            DataDeclarationSyntax or ListDeclarationSyntax or FrameDirectiveSyntax when first => TextMateGrammar.Variable,
            ConstantDeclarationSyntax when first => TextMateGrammar.Constant,
            EnumMemberSyntax when first => TextMateGrammar.EnumMember,
            MemberValueSyntax when first => TextMateGrammar.Property,
            NamedArgumentSyntax when first => TextMateGrammar.Parameter,
            MacroCallSyntax when first && index + 1 < tokens.Count && tokens[index + 1].Kind == SyntaxKind.Bang => TextMateGrammar.Macro,
            _ => token.Kind == SyntaxKind.Identifier && index + 1 < tokens.Count && tokens[index + 1].Kind == SyntaxKind.Bang
                ? TextMateGrammar.Macro
                : null,
        };
    }

    /// <summary>
    /// Returns every token of a line in source order, taken from the line's child elements. Those
    /// are the <c>.export</c> that exports what the line declares, its statement, the tokens the
    /// statement could not take, and the line break that ends it. A missing token has no text, so
    /// the grammar has nothing to scope for it, and it is left out.
    /// </summary>
    private static List<SyntaxToken> Tokens(LineSyntax line)
    {
        var tokens = new List<SyntaxToken>();
        void Walk(SyntaxNode node)
        {
            foreach (var child in node.ChildNodesAndTokens())
            {
                if (child.AsNode() is { } inner)
                    Walk(inner);
                else if (child.AsToken() is { IsMissing: false } token)
                    tokens.Add(token);
            }
        }
        if (line.ExportKeyword is { } export)
            tokens.Add(export);
        Walk(line.Statement);
        if (line.SkippedTokens is { } skipped)
            Walk(skipped);
        tokens.Add(line.EndOfLineToken);
        return tokens;
    }

    private static bool IsName(SyntaxToken token) =>
        token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic or SyntaxKind.CheapLocal;

    /// <summary>
    /// Returns, for each line, the kind of block whose body it is in, looking through
    /// conditionals and repetitions, which the grammar does not track. A block's opener line
    /// belongs to the block around it, not to the block it opens.
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
