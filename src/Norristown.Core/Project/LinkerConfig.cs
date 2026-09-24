namespace Norristown.Project;

/// <summary>
/// Represents an ld65 linker configuration, read for what nt65 needs from it: the memory areas
/// and where each segment loads and runs. The <c>MEMORY</c>, <c>SEGMENTS</c> and <c>SYMBOLS</c>
/// blocks are read, and every other block is skipped. A value that depends on ld65's command
/// line, such as <c>%S</c> or a symbol that <c>-D</c> defines, is unknown rather than guessed.
/// </summary>
public sealed class LinkerConfig
{
    private readonly Dictionary<string, MemoryArea> memory;

    private LinkerConfig(
        string path, Dictionary<string, MemoryArea> memory, IReadOnlyList<PlacedSegment> segments,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Path = path;
        this.memory = memory;
        Segments = segments;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the configuration's logical path.</summary>
    public string Path { get; }

    /// <summary>Gets the segments the <c>SEGMENTS</c> block places, in the order it lists them.</summary>
    public IReadOnlyList<PlacedSegment> Segments { get; }

    /// <summary>Gets the problems found while reading the configuration.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// Reads the configuration in <paramref name="text"/>. <paramref name="path"/> is the logical
    /// path that diagnostics and declarations use to refer to it.
    /// </summary>
    public static LinkerConfig Parse(string path, string text)
    {
        var reader = new Reader(path, Tokens(path, text));
        reader.ReadFile();
        var symbols = reader.Symbols;
        long? Value(IReadOnlyList<Token> tokens) => new Evaluator(tokens, symbols).Evaluate();

        var memory = new Dictionary<string, MemoryArea>(StringComparer.Ordinal);
        foreach (var (name, at, attributes) in reader.Memory)
        {
            memory.TryAdd(name, new MemoryArea(
                name,
                attributes.TryGetValue("start", out var start) ? Value(start) : null,
                attributes.TryGetValue("size", out var size) ? Value(size) : null,
                at));
        }

        var segments = new List<PlacedSegment>();
        foreach (var (name, at, attributes) in reader.Segments)
        {
            segments.Add(new PlacedSegment(name, at)
            {
                Load = Word(attributes, "load"),
                Run = Word(attributes, "run"),
                Type = Word(attributes, "type")?.ToLowerInvariant(),
                Start = attributes.TryGetValue("start", out var start) ? new Given(Value(start)) : null,
                Offset = attributes.TryGetValue("offset", out var offset) ? new Given(Value(offset)) : null,
                Defines = Word(attributes, "define")?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true,
            });
        }
        return new LinkerConfig(path, memory, segments, reader.Diagnostics);
    }

    /// <summary>Returns the memory area <paramref name="name"/>, or null when the configuration has none.</summary>
    public MemoryArea? Area(string name) => memory.GetValueOrDefault(name);

    /// <summary>
    /// Returns the word an attribute is set to, such as <c>ROM</c> for <c>load = ROM</c>, or null
    /// when the attribute is not given or is not a single word.
    /// </summary>
    private static string? Word(Dictionary<string, IReadOnlyList<Token>> attributes, string name) =>
        attributes.TryGetValue(name, out var tokens) && tokens is [{ Kind: TokenKind.Name or TokenKind.String } token]
            ? token.Text
            : null;

    /// <summary>Splits <paramref name="text"/> into tokens, skipping white space and <c>#</c> comments.</summary>
    private static List<Token> Tokens(string path, string text)
    {
        var tokens = new List<Token>();
        var (line, lineStart, i) = (1, 0, 0);
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                line++;
                lineStart = ++i;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }

            var start = i;
            TokenKind kind;
            if (char.IsAsciiLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                kind = TokenKind.Name;
            }
            else if (char.IsAsciiDigit(c) || (c == '$' && i + 1 < text.Length && char.IsAsciiHexDigit(text[i + 1]))
                || (c == '%' && i + 1 < text.Length && text[i + 1] is '0' or '1'))
            {
                i++;
                while (i < text.Length && char.IsAsciiHexDigit(text[i]))
                    i++;
                kind = TokenKind.Number;
            }
            else if (c == '%' && i + 1 < text.Length && char.IsAsciiLetter(text[i + 1]))
            {
                // `%O` is the output file's name and `%S` the start address, both from ld65's
                // command line.
                i += 2;
                kind = text[i - 1] is 'O' or 'o' ? TokenKind.String : TokenKind.Unknown;
            }
            else if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] is not ('"' or '\n'))
                    i++;
                if (i < text.Length && text[i] == '"')
                    i++;
                tokens.Add(new Token(TokenKind.String, text[(start + 1)..Math.Max(start + 1, i - 1)],
                    new Span(path, line, start - lineStart + 1, i - lineStart + 1)));
                continue;
            }
            else
            {
                i++;
                kind = TokenKind.Punctuation;
            }
            tokens.Add(new Token(kind, text[start..i], new Span(path, line, start - lineStart + 1, i - lineStart + 1)));
        }
        return tokens;
    }

    /// <summary>
    /// Represents one area of the <c>MEMORY</c> block. A start or size that nt65 cannot work out
    /// is null.
    /// </summary>
    /// <param name="Name">The area's name.</param>
    /// <param name="Start">The address where the area starts.</param>
    /// <param name="Size">The area's size in bytes.</param>
    /// <param name="Declaration">Where the configuration names the area.</param>
    public sealed record MemoryArea(string Name, long? Start, long? Size, Span Declaration);

    /// <summary>Represents one entry of the <c>SEGMENTS</c> block.</summary>
    /// <param name="Name">The segment's name.</param>
    /// <param name="Declaration">Where the configuration names the segment.</param>
    public sealed record PlacedSegment(string Name, Span Declaration)
    {
        /// <summary>Gets the memory area the segment loads into, or null when none is given.</summary>
        public string? Load { get; init; }

        /// <summary>Gets the memory area the segment runs in, or null when it runs where it loads.</summary>
        public string? Run { get; init; }

        /// <summary>Gets the segment's <c>type</c>, such as <c>zp</c> or <c>bss</c>, in lower case.</summary>
        public string? Type { get; init; }

        /// <summary>Gets the segment's <c>start</c>, or null when it gives none.</summary>
        public Given? Start { get; init; }

        /// <summary>Gets the segment's <c>offset</c> into its memory area, or null when it gives none.</summary>
        public Given? Offset { get; init; }

        /// <summary>
        /// Gets a value indicating whether the segment has <c>define = yes</c>, so that ld65
        /// defines its load address, run address and size.
        /// </summary>
        public bool Defines { get; init; }

        /// <summary>Gets the memory area the segment's addresses are in, which is where it runs.</summary>
        public string? RunsIn => Run ?? Load;
    }

    private enum TokenKind
    {
        Name,
        Number,
        String,
        Punctuation,

        // A value only ld65's command line gives, such as `%S`.
        Unknown,
    }

    private sealed record Token(TokenKind Kind, string Text, Span Span)
    {
        public bool Is(string punctuation) => Kind == TokenKind.Punctuation && Text == punctuation;
    }

    /// <summary>
    /// Reads the blocks of a configuration into named entries, each with its attributes as the
    /// tokens of their values.
    /// </summary>
    private sealed class Reader(string path, List<Token> tokens)
    {
        private int at;

        public List<(string Name, Span At, Dictionary<string, IReadOnlyList<Token>> Attributes)> Memory { get; } = [];

        public List<(string Name, Span At, Dictionary<string, IReadOnlyList<Token>> Attributes)> Segments { get; } = [];

        public Dictionary<string, IReadOnlyList<Token>> Symbols { get; } = new(StringComparer.Ordinal);

        public List<Diagnostic> Diagnostics { get; } = [];

        private Token? Current => at < tokens.Count ? tokens[at] : null;

        public void ReadFile()
        {
            while (Current is { } section)
            {
                at++;
                if (section.Kind != TokenKind.Name || Current?.Is("{") != true)
                {
                    Report(section.Span, $"expected a block such as `MEMORY {{`, not `{section.Text}`");
                    return;
                }
                at++;
                var kind = section.Text.ToUpperInvariant();
                if (kind is "MEMORY" or "SEGMENTS" or "SYMBOLS")
                {
                    if (!ReadEntries(kind))
                        return;
                }
                else
                {
                    SkipBlock();
                }
            }
        }

        private bool ReadEntries(string block)
        {
            while (Current is { } name && !name.Is("}"))
            {
                at++;
                if (name.Kind is not (TokenKind.Name or TokenKind.String))
                {
                    Report(name.Span, $"expected a name in `{block}`, not `{name.Text}`");
                    return false;
                }

                // `SYMBOLS` also takes the older `NAME = value;`.
                if (block == "SYMBOLS" && Current?.Is("=") == true)
                {
                    at++;
                    Symbols.TryAdd(name.Text, Value());
                    if (!Expect(";", name))
                        return false;
                    continue;
                }
                if (!Expect(":", name))
                    return false;
                var attributes = new Dictionary<string, IReadOnlyList<Token>>(StringComparer.OrdinalIgnoreCase);
                while (Current is { } attribute && !attribute.Is(";"))
                {
                    at++;
                    if (attribute.Kind != TokenKind.Name || Current?.Is("=") != true)
                    {
                        Report(attribute.Span, $"expected an attribute such as `start = $0000` for `{name.Text}`");
                        return false;
                    }
                    at++;
                    attributes.TryAdd(attribute.Text, Value());
                    if (Current?.Is(",") == true)
                        at++;
                }
                if (!Expect(";", name))
                    return false;

                var entry = (name.Text, name.Span, attributes);
                if (block == "MEMORY")
                    Memory.Add(entry);
                else if (block == "SEGMENTS")
                    Segments.Add(entry);
                else if (attributes.TryGetValue("value", out var value))
                    Symbols.TryAdd(name.Text, value);
            }
            if (Current is null)
            {
                Report(tokens.Count > 0 ? tokens[^1].Span : new Span(path, 1, 1, 2), $"`{block}` is not closed with `}}`");
                return false;
            }
            at++;
            return true;
        }

        /// <summary>
        /// Returns the tokens of one attribute's value. It ends at a <c>,</c> or <c>;</c>, or where
        /// the next attribute starts, since ld65 does not need the comma between attributes.
        /// </summary>
        private List<Token> Value()
        {
            var value = new List<Token>();
            var depth = 0;
            while (Current is { } token)
            {
                if (depth == 0 && (token.Is(",") || token.Is(";") || token.Is("}")))
                    break;
                if (depth == 0 && value.Count > 0 && token.Kind == TokenKind.Name
                    && at + 1 < tokens.Count && tokens[at + 1].Is("="))
                {
                    break;
                }
                depth += token.Is("(") ? 1 : token.Is(")") ? -1 : 0;
                value.Add(token);
                at++;
            }
            return value;
        }

        private void SkipBlock()
        {
            var depth = 1;
            while (Current is { } token && depth > 0)
            {
                depth += token.Is("{") ? 1 : token.Is("}") ? -1 : 0;
                at++;
            }
        }

        private bool Expect(string punctuation, Token after)
        {
            if (Current?.Is(punctuation) == true)
            {
                at++;
                return true;
            }
            Report(Current?.Span ?? after.Span, $"expected `{punctuation}` after `{after.Text}`");
            return false;
        }

        private void Report(Span span, string problem) =>
            Diagnostics.Add(new Diagnostic(span, Catalogue.LinkedConfigInvalid.Message(problem)));
    }

    /// <summary>
    /// Works out the value of an attribute from its tokens: numbers, symbols from the
    /// <c>SYMBOLS</c> block, <c>+ - * /</c>, <c>&amp;</c>, <c>|</c> and parentheses. Anything
    /// else makes the value unknown.
    /// </summary>
    private sealed class Evaluator(
        IReadOnlyList<Token> tokens, Dictionary<string, IReadOnlyList<Token>> symbols, HashSet<string>? evaluating = null)
    {
        // The symbols being worked out, so that one defined in terms of itself is unknown
        // rather than endless.
        private readonly HashSet<string> evaluating = evaluating ?? new(StringComparer.Ordinal);
        private int at;

        public long? Evaluate()
        {
            if (tokens.Count == 0)
                return null;
            var value = Sum();
            return at == tokens.Count ? value : null;
        }

        private long? Sum()
        {
            var value = Product();
            while (at < tokens.Count && (tokens[at].Is("+") || tokens[at].Is("-") || tokens[at].Is("|")))
            {
                var op = tokens[at++].Text;
                var right = Product();
                value = value is { } l && right is { } r ? op switch { "+" => l + r, "-" => l - r, _ => l | r } : null;
            }
            return value;
        }

        private long? Product()
        {
            var value = Unary();
            while (at < tokens.Count && (tokens[at].Is("*") || tokens[at].Is("/") || tokens[at].Is("&")))
            {
                var op = tokens[at++].Text;
                var right = Unary();
                value = value is { } l && right is { } r
                    ? op switch { "*" => l * r, "/" => r == 0 ? null : l / r, _ => l & r }
                    : null;
            }
            return value;
        }

        private long? Unary()
        {
            if (at >= tokens.Count)
                return null;
            var token = tokens[at++];
            if (token.Is("-"))
                return -Unary();
            if (token.Is("+"))
                return Unary();
            if (token.Is("~"))
                return ~Unary();
            if (token.Is("("))
            {
                var value = Sum();
                if (at < tokens.Count && tokens[at].Is(")"))
                {
                    at++;
                    return value;
                }
                return null;
            }
            return token.Kind switch
            {
                TokenKind.Number => Number(token.Text),
                TokenKind.Name => Symbol(token.Text),
                _ => null,
            };
        }

        private long? Symbol(string name)
        {
            if (!symbols.TryGetValue(name, out var value) || !evaluating.Add(name))
                return null;
            var result = new Evaluator(value, symbols, evaluating).Evaluate();
            evaluating.Remove(name);
            return result;
        }

        private static long? Number(string text)
        {
            try
            {
                return text[0] switch
                {
                    '$' => Convert.ToInt64(text[1..], 16),
                    '%' => Convert.ToInt64(text[1..], 2),
                    _ => long.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
                };
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
            {
                return null;
            }
        }
    }
}
