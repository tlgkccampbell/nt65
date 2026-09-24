using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents the line at the caret, up to the caret, lexed on its own. What is being typed there
/// is read from these tokens rather than from the parsed file, because a line being typed seldom
/// parses.
/// </summary>
internal sealed class LineContext
{
    private LineContext(
        IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> tokens, int caret, bool partial,
        Surrounding around, bool inText, bool first)
    {
        Caret = caret;
        Context = around.Context;
        Nesting = around.Nesting | (first ? DirectiveNesting.FirstLine : DirectiveNesting.None);
        RecordType = around.RecordType;
        InText = inText;
        if (partial)
        {
            Partial = tokens[^1];
            Before = [.. tokens.Take(tokens.Count - 1)];
        }
        else
        {
            Before = tokens;
        }
    }

    /// <summary>Gets the caret's position in the file.</summary>
    public int Caret { get; }

    /// <summary>
    /// Gets the name the caret is at the end of, which a completion replaces, or null if there is
    /// none.
    /// </summary>
    public (SyntaxKind Kind, string Text, int Start)? Partial { get; }

    /// <summary>Gets the tokens before that name, or before the caret when there is no name.</summary>
    public IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> Before { get; }

    /// <summary>Gets the kind of context the line is in, which decides what may appear there.</summary>
    public ContextKind Context { get; }

    /// <summary>
    /// Gets what surrounds the line, which rules directives in or out whatever the context. It
    /// includes <see cref="DirectiveNesting.FirstLine"/> on the file's first line, the only place a
    /// <c>.module</c> goes.
    /// </summary>
    public DirectiveNesting Nesting { get; }

    /// <summary>
    /// Gets a value indicating whether the line is inside a macro body, which may declare nothing
    /// the rest of the program shares.
    /// </summary>
    public bool InMacro => (Nesting & DirectiveNesting.MacroBody) != 0;

    /// <summary>
    /// Gets the type a <c>.type T { }</c> initializer gives values to, or null outside one.
    /// </summary>
    public IReadOnlyList<string>? RecordType { get; }

    /// <summary>
    /// Gets a value indicating whether the caret is inside a comment or a text literal, where
    /// nothing is completed.
    /// </summary>
    public bool InText { get; }

    /// <summary>
    /// Gets the span a completion's text replaces, which is the name being typed, or an empty span
    /// at the caret.
    /// </summary>
    public TextSpan Replaced => Partial is { } partial ? new TextSpan(partial.Start, Caret - partial.Start) : new TextSpan(Caret, 0);

    /// <summary>
    /// Gets the directive the statement starts with, after any <c>.export</c> or label. The result
    /// is null for a statement that starts with no directive token, and
    /// <see cref="DirectiveKind.None"/> for one whose directive token names no directive.
    /// </summary>
    public DirectiveKind? Directive
    {
        get
        {
            var at = Start;
            return at < Before.Count && Before[at].Kind == SyntaxKind.Directive
                ? SyntaxFacts.DirectiveKindOf(Before[at].Text)
                : null;
        }
    }

    /// <summary>Gets the index of the statement's first token, after any label and <c>.export</c>.</summary>
    public int Start
    {
        get
        {
            var at = 0;
            if (at + 1 < Before.Count && Before[at].Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                && Before[at + 1].Kind == SyntaxKind.Colon)
            {
                at += 2;
            }
            if (at < Before.Count && Before[at] is { Kind: SyntaxKind.Directive } export
                && SyntaxFacts.DirectiveKindOf(export.Text) == DirectiveKind.Export)
            {
                at++;
            }
            return at;
        }
    }

    /// <summary>
    /// Returns the context of the line of <paramref name="tree"/> up to
    /// <paramref name="position"/>.
    /// </summary>
    public static LineContext At(SyntaxTree tree, int position)
    {
        var line = tree.GetLineIndex(position);
        var lineStart = tree.LineStarts[line];
        var tokens = Lexed(tree, lineStart, position);

        // A name that runs right up to the caret is the one being typed. A `.` on its own lexes
        // as a bad token rather than a directive, but it is the start of one all the same.
        var partial = tokens.Count > 0
            && (tokens[^1].Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal or SyntaxKind.Mnemonic
                    or SyntaxKind.Register or SyntaxKind.Directive
                || tokens[^1] is { Kind: SyntaxKind.BadToken, Text: "." })
            && tokens[^1].Start + tokens[^1].Text.Length == position;
        return new LineContext(
            tokens, position, partial, Around(tree, line), IsText(tree.Text, lineStart, position), line == 0);
    }

    /// <summary>
    /// Finds the innermost call whose argument list holds the caret. Returns the index of the
    /// <c>(</c> that opens the list and how many arguments come before the caret's argument, or
    /// null outside every call.
    /// Passing <paramref name="end"/> searches back from before that token index instead of from
    /// the caret, which finds the call enclosing another.
    /// </summary>
    public (int Open, int Argument)? OpenCall(int? end = null)
    {
        var depth = 0;
        var commas = 0;
        for (var i = (end ?? Before.Count) - 1; i >= 0; i--)
        {
            switch (Before[i].Kind)
            {
                case SyntaxKind.CloseParen or SyntaxKind.CloseBracket or SyntaxKind.CloseBrace:
                    depth++;
                    break;
                case SyntaxKind.OpenParen or SyntaxKind.OpenBracket or SyntaxKind.OpenBrace when depth > 0:
                    depth--;
                    break;
                case SyntaxKind.OpenParen:
                    return (i, commas);
                case SyntaxKind.OpenBracket or SyntaxKind.OpenBrace:
                    return null;
                case SyntaxKind.Comma when depth == 0:
                    commas++;
                    break;
                default:
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the names before the <c>::</c> that token index <paramref name="end"/> follows, or
    /// null when it follows no <c>::</c>. For <c>hw::vic::</c> the names are <c>hw</c> and
    /// <c>vic</c>.
    /// </summary>
    public IReadOnlyList<string>? PathBefore(int end)
    {
        if (end == 0 || Before[end - 1].Kind != SyntaxKind.ColonColon)
            return null;
        var parts = new List<string>();
        var at = end - 1;
        while (at >= 1 && Before[at].Kind == SyntaxKind.ColonColon && IsWord(Before[at - 1].Kind))
        {
            parts.Insert(0, Before[at - 1].Text);
            at -= 2;
        }
        return parts.Count == 0 ? null : parts;
    }

    /// <summary>
    /// Returns all of a line's tokens except its line break, each with its start position in the
    /// file.
    /// </summary>
    public static List<(SyntaxKind Kind, string Text, int Start)> TokensOf(SyntaxTree tree, int line) =>
        [.. tree.GetLine(line).Tokens
            .Where(token => token.Kind != SyntaxKind.EndOfLine)
            .Select(token => (token.Kind, token.Text, token.Span.Start))];

    /// <summary>
    /// Returns the position where the code on a line ends, before any comment, or the line's
    /// start for a line with no code.
    /// </summary>
    public static int CodeEnd(SyntaxTree tree, int line) =>
        TokensOf(tree, line) is [.., var last] ? last.Start + last.Text.Length : tree.LineStarts[line];

    /// <summary>Checks whether a token of this kind can be a name.</summary>
    public static bool IsWord(SyntaxKind kind) =>
        kind is SyntaxKind.Identifier or SyntaxKind.Mnemonic or SyntaxKind.Register;

    /// <summary>
    /// Returns the line's surroundings. The innermost block holding the line determines the
    /// context, and all the enclosing blocks together determine what may be declared there. The
    /// line that opens a block belongs to the block around it, not to the one it opens.
    /// </summary>
    private static Surrounding Around(SyntaxTree tree, int line)
    {
        var around = Edits.BlockAround(tree, line);
        var found = new Surrounding(ContextKind.Item, SyntaxFacts.NestingWithin(around), null);
        foreach (var block in around?.AncestorsAndSelf().OfType<BlockSyntax>().Reverse() ?? [])
            found = found.Within(block);
        return found;
    }

    /// <summary>
    /// Checks whether the caret is inside a comment or a text literal, which hold prose rather than
    /// code. The line is scanned up to the caret, following the lexer's quoting rules only as
    /// far as needed to tell.
    /// </summary>
    private static bool IsText(string text, int start, int caret)
    {
        var quote = '\0';
        for (var at = start; at < caret; at++)
        {
            var c = text[at];
            if (quote != '\0')
            {
                if (c == '\\')
                    at++;
                else if (c == quote)
                    quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ';')
            {
                return true;
            }
        }
        return quote != '\0';
    }

    /// <summary>
    /// Lexes the line's text from <paramref name="start"/> to <paramref name="end"/> on its own,
    /// recording where each token starts in the file. The text is lexed again rather than taken
    /// from the tree because the caret cuts the line. What the file has as <c>$10</c> is
    /// <c>$1</c> to someone who has typed that far, and a completion is about what they have
    /// typed.
    /// </summary>
    private static List<(SyntaxKind Kind, string Text, int Start)> Lexed(SyntaxTree tree, int start, int end) =>
        [.. SyntaxTree.Parse(tree.Path, tree.Text[start..end]).GetLine(0).Tokens
            .Where(token => token.Kind != SyntaxKind.EndOfLine)
            .Select(token => (token.Kind, token.Text, start + token.Span.Start))];

    /// <summary>Represents what the blocks around a line determine about what may appear in it.</summary>
    /// <param name="Context">The kind of context the innermost block gives.</param>
    /// <param name="Nesting">What all the blocks around the line together rule in or out.</param>
    /// <param name="RecordType">The type a record initializer gives values to.</param>
    private readonly record struct Surrounding(
        ContextKind Context, DirectiveNesting Nesting, IReadOnlyList<string>? RecordType)
    {
        /// <summary>Returns the surroundings one block further in.</summary>
        public Surrounding Within(BlockSyntax block) => block.BlockKind switch
        {
            BlockKind.Proc or BlockKind.Macro or BlockKind.MacroBlock => this with { Context = ContextKind.Code },
            BlockKind.Scope or BlockKind.Segment or BlockKind.Region or BlockKind.If
                or BlockKind.Repeat or BlockKind.Each => this,
            BlockKind.Data => this with { Context = ContextKind.Data },
            BlockKind.DataBody or BlockKind.List or BlockKind.Charmap => this with { Context = ContextKind.Values },
            BlockKind.RecordInitializer =>
                this with { Context = ContextKind.Record, RecordType = TypeOf(block.Opener) },
            BlockKind.Enum => this with { Context = ContextKind.EnumMembers },
            BlockKind.Struct or BlockKind.Union => this with { Context = ContextKind.TypeMembers },
            _ => this with { Context = ContextKind.Unknown },
        };

        /// <summary>Returns the path after the <c>.type</c> of a record initializer's opener.</summary>
        private static IReadOnlyList<string>? TypeOf(LineSyntax opener)
        {
            var tokens = opener.Tokens;
            var at = 0;
            while (at < tokens.Count && tokens[at].DirectiveKind != DirectiveKind.Type)
                at++;
            var parts = new List<string>();
            for (at++; at < tokens.Count && IsWord(tokens[at].Kind); at += 2)
            {
                parts.Add(tokens[at].Text);
                if (at + 1 >= tokens.Count || tokens[at + 1].Kind != SyntaxKind.ColonColon)
                    break;
            }
            return parts.Count > 0 ? parts : null;
        }
    }
}
