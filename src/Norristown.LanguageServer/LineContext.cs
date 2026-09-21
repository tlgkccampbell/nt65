using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The line at the caret as far as the caret, lexed on its own. What is being written there is
/// read off these tokens rather than off the parsed file, because a line being typed is seldom
/// one that parses.
/// </summary>
internal sealed class LineContext
{
    private LineContext(
        IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> tokens, int caret, bool partial,
        Surrounding around, bool inText, bool first)
    {
        Caret = caret;
        Place = around.Place;
        InProc = around.InProc;
        InMacro = around.InMacro;
        InRepetition = around.InRepetition;
        InBlock = around.InBlock;
        RecordType = around.RecordType;
        InText = inText;
        IsFirstLine = first;
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

    /// <summary>The caret, as a position in the file.</summary>
    public int Caret { get; }

    /// <summary>The name the caret is at the end of, which a completion replaces, or null.</summary>
    public (SyntaxKind Kind, string Text, int Start)? Partial { get; }

    /// <summary>The tokens before that name, or before the caret when there is none.</summary>
    public IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> Before { get; }

    /// <summary>The kind of place the line is written in, which decides what may be written there.</summary>
    public Place Place { get; }

    /// <summary>Whether a routine holds the line, where a <c>.proc</c> and a <c>.macro</c> may not go.</summary>
    public bool InProc { get; }

    /// <summary>Whether a macro body holds the line, which declares nothing the rest of the program shares.</summary>
    public bool InMacro { get; }

    /// <summary>Whether a repetition holds the line, where a definition would differ on every turn.</summary>
    public bool InRepetition { get; }

    /// <summary>
    /// Whether any block holds the line at all, an open <c>.segment</c> region included. A
    /// <c>.config</c> is written where none does: which settings a program has is part of what
    /// a build sets, and may not itself depend on where in a file it was written.
    /// </summary>
    public bool InBlock { get; }

    /// <summary>The type a <c>.type T { }</c> initializer gives values to, or null outside one.</summary>
    public IReadOnlyList<string>? RecordType { get; }

    /// <summary>Whether the caret is inside a comment or a text literal, where nothing is written.</summary>
    public bool InText { get; }

    /// <summary>Whether this is the file's first line, the only place a <c>.module</c> goes.</summary>
    public bool IsFirstLine { get; }

    /// <summary>Where a completion's text goes: over the name being typed, or at the caret.</summary>
    public TextSpan Replaced => Partial is { } partial ? new TextSpan(partial.Start, Caret - partial.Start) : new TextSpan(Caret, 0);

    /// <summary>
    /// The directive the statement starts with, lower case, past an <c>.export</c> or a label;
    /// null for a statement that starts with none.
    /// </summary>
    public string? Directive
    {
        get
        {
            var at = Start;
            return at < Before.Count && Before[at].Kind == SyntaxKind.Directive ? Before[at].Text.ToLowerInvariant() : null;
        }
    }

    /// <summary>The index of the statement's first token, past an <c>.export</c> and a label.</summary>
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
                && export.Text.Equals(".export", StringComparison.OrdinalIgnoreCase))
            {
                at++;
            }
            return at;
        }
    }

    /// <summary>The line of <paramref name="tree"/> up to <paramref name="position"/>.</summary>
    public static LineContext At(SyntaxTree tree, int position)
    {
        var line = tree.GetLineIndex(position);
        var lineStart = tree.LineStarts[line];
        var tokens = Lexed(tree, lineStart, position);

        // A name that runs right up to the caret is the one being typed. A `.` on its own is
        // not yet a directive and lexes as nothing, and is the start of one all the same.
        var partial = tokens.Count > 0
            && (tokens[^1].Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal or SyntaxKind.Mnemonic
                    or SyntaxKind.Register or SyntaxKind.Directive
                || tokens[^1] is { Kind: SyntaxKind.BadToken, Text: "." })
            && tokens[^1].Start + tokens[^1].Text.Length == position;
        return new LineContext(
            tokens, position, partial, Around(tree, line), IsText(tree.Text, lineStart, position), line == 0);
    }

    /// <summary>
    /// The call the caret is in the arguments of, innermost first: the index of the <c>(</c> that
    /// opens them, and how many arguments come before the caret's. Null outside every call.
    /// <paramref name="end"/> looks from before that token instead of the caret, for the call
    /// around one.
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
    /// The names written before the <c>::</c> the caret follows, <c>hw::vic::</c> being
    /// <c>hw</c> and <c>vic</c>, or null when the caret follows no <c>::</c>.
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

    /// <summary>A whole line's tokens, but for its line break, with where each starts in the file.</summary>
    public static List<(SyntaxKind Kind, string Text, int Start)> TokensOf(SyntaxTree tree, int line) =>
        [.. tree.GetLine(line).Tokens
            .Where(token => token.Kind != SyntaxKind.EndOfLine)
            .Select(token => (token.Kind, token.Text, token.Span.Start))];

    /// <summary>Where the code on a line ends, before any comment: its start, for a line with none.</summary>
    public static int CodeEnd(SyntaxTree tree, int line) =>
        TokensOf(tree, line) is [.., var last] ? last.Start + last.Text.Length : tree.LineStarts[line];

    /// <summary>Where the text of a line ends, after any comment and before the line's break.</summary>
    public static int TextEnd(SyntaxTree tree, int line) =>
        tree.GetLine(line).Tokens is [.., { Kind: SyntaxKind.EndOfLine } broken]
            ? broken.Span.Start
            : CodeEnd(tree, line);

    /// <summary>Whether a token is one a name can be written as.</summary>
    public static bool IsWord(SyntaxKind kind) =>
        kind is SyntaxKind.Identifier or SyntaxKind.Mnemonic or SyntaxKind.Register;

    /// <summary>
    /// What surrounds the line: the innermost block holding it decides the place, and the
    /// blocks on the way in decide what may be declared there. The line that opens a block is
    /// written in the block around it, not in the one it opens.
    /// </summary>
    private static Surrounding Around(SyntaxTree tree, int line)
    {
        var found = new Surrounding(Place.Item, false, false, false, false, null);
        foreach (var block in Edits.BlockAround(tree, line)?.AncestorsAndSelf().OfType<BlockSyntax>().Reverse() ?? [])
            found = found.Within(block);
        return found;
    }

    /// <summary>
    /// Whether the caret is inside a comment or a text literal, which hold prose rather than
    /// code. The line is read to the caret the way the lexer reads it, far enough to say.
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
    /// The line's text from <paramref name="start"/> to <paramref name="end"/> read on its own,
    /// with where each token starts in the file. It is read again rather than taken off the tree
    /// because the caret cuts the line: what the file has as <c>$10</c> is <c>$1</c> to someone
    /// who has typed that far, and it is what they have typed that a completion is about.
    /// </summary>
    private static List<(SyntaxKind Kind, string Text, int Start)> Lexed(SyntaxTree tree, int start, int end) =>
        [.. SyntaxTree.Parse(tree.Path, tree.Text[start..end]).GetLine(0).Tokens
            .Where(token => token.Kind != SyntaxKind.EndOfLine)
            .Select(token => (token.Kind, token.Text, start + token.Span.Start))];

    /// <summary>What the blocks around a line say about what may be written in it.</summary>
    /// <param name="Place">The kind of place the innermost block makes.</param>
    /// <param name="InProc">Whether a routine holds the line.</param>
    /// <param name="InMacro">Whether a macro body holds the line.</param>
    /// <param name="InRepetition">Whether a repetition holds the line.</param>
    /// <param name="InBlock">Whether any block holds the line, whatever kind it is.</param>
    /// <param name="RecordType">The type a record initializer gives values to.</param>
    private readonly record struct Surrounding(
        Place Place, bool InProc, bool InMacro, bool InRepetition, bool InBlock, IReadOnlyList<string>? RecordType)
    {
        /// <summary>
        /// The same, one block further in. Every kind of block answers that one holds the line,
        /// whatever else it says about it, because what may be written only at file level is
        /// ruled out by the block being there rather than by which block it is.
        /// </summary>
        public Surrounding Within(BlockSyntax block)
        {
            var inside = block.BlockKind switch
            {
                BlockKind.Proc => this with { Place = Place.Code, InProc = true },
                BlockKind.Macro => this with { Place = Place.Code, InMacro = true },
                BlockKind.MacroBlock => this with { Place = Place.Code },
                BlockKind.Scope or BlockKind.Segment or BlockKind.Region or BlockKind.If => this,
                BlockKind.Repeat or BlockKind.Each => this with { InRepetition = true },
                BlockKind.Data => this with { Place = Place.Data },
                BlockKind.DataBody or BlockKind.List or BlockKind.Charmap => this with { Place = Place.Values },
                BlockKind.RecordInitializer => this with { Place = Place.Record, RecordType = TypeOf(block.Opener) },
                BlockKind.Enum => this with { Place = Place.EnumMembers },
                BlockKind.Struct or BlockKind.Union => this with { Place = Place.TypeMembers },
                _ => this with { Place = Place.Unknown },
            };
            return inside with { InBlock = true };
        }

        /// <summary>The path written after the <c>.type</c> of a record initializer's opener.</summary>
        private static IReadOnlyList<string>? TypeOf(LineSyntax opener)
        {
            var tokens = opener.Tokens;
            var at = 0;
            while (at < tokens.Count && !tokens[at].Text.Equals(".type", StringComparison.OrdinalIgnoreCase))
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
