using System.Globalization;

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

        // Returns an attribute's value, or null when the entry does not give the attribute.
        long? ValueOf(Attributes attributes, string name) =>
            attributes.TryGetValue(name, out var tokens) ? Value(tokens) : null;

        // Returns an attribute as a number given, or null when the entry does not give it.
        Given? GivenOf(Attributes attributes, string name) =>
            attributes.TryGetValue(name, out var tokens) ? new Given(Value(tokens)) : null;

        // ld65 takes a `fillval` from 0 to 255 and refuses anything else, so a value outside
        // that range is as unknown as one nt65 cannot work out.
        Given? FillOf(Attributes attributes) =>
            attributes.TryGetValue("fillval", out var fill)
                ? new Given(Value(fill) is { } value and >= 0 and <= 0xFF ? value : null)
                : null;

        var memory = new Dictionary<string, MemoryArea>(StringComparer.Ordinal);
        foreach (var (name, at, attributes) in reader.Memory)
        {
            memory.TryAdd(name, new MemoryArea(name, ValueOf(attributes, "start"), ValueOf(attributes, "size"), at)
            {
                // ld65 writes an area to the output file unless its `file` is empty. A `file` given
                // as anything nt65 cannot read is taken as written, so nothing is reported for it.
                IsWritten = Word(attributes, "file") != "",
                Fill = FillOf(attributes),
            });
        }

        var segments = new List<PlacedSegment>();
        foreach (var (name, at, attributes) in reader.Segments)
        {
            segments.Add(new PlacedSegment(name, at)
            {
                Load = Word(attributes, "load"),
                Run = Word(attributes, "run"),
                Type = Word(attributes, "type")?.ToLowerInvariant(),
                Start = GivenOf(attributes, "start"),
                Offset = GivenOf(attributes, "offset"),
                Align = GivenOf(attributes, "align"),
                Defines = Word(attributes, "define")?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true,
                Fill = FillOf(attributes),
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
    private static string? Word(Attributes attributes, string name) =>
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
    public sealed record MemoryArea(string Name, long? Start, long? Size, Span Declaration)
    {
        /// <summary>
        /// Gets a value indicating whether ld65 writes the area to an output file. It does unless
        /// the area gives <c>file = ""</c>.
        /// </summary>
        public bool IsWritten { get; init; } = true;

        /// <summary>
        /// Gets the area's <c>fillval</c>, or null when it gives none. ld65 fills the padding of
        /// each segment that loads into the area with it, unless the segment gives its own.
        /// </summary>
        public Given? Fill { get; init; }
    }

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

        /// <summary>Gets the alignment of the segment's start that <c>align</c> asks for, or null when it gives none.</summary>
        public Given? Align { get; init; }

        /// <summary>
        /// Gets a value indicating whether the segment has <c>define = yes</c>, so that ld65
        /// defines its load address, run address and size.
        /// </summary>
        public bool Defines { get; init; }

        /// <summary>
        /// Gets the segment's <c>fillval</c>, or null when it gives none. ld65 fills the space a
        /// <c>.res</c> or <c>.align</c> in the segment leaves with it.
        /// </summary>
        public Given? Fill { get; init; }

        /// <summary>Gets the memory area the segment's addresses are in, which is where it runs.</summary>
        public string? RunsIn => Run ?? Load;
    }

    /// <summary>Identifies what a token of a configuration is.</summary>
    private enum TokenKind
    {
        /// <summary>A block, entry, attribute or symbol name.</summary>
        Name,

        /// <summary>A decimal, <c>$</c> hexadecimal or <c>%</c> binary number.</summary>
        Number,

        /// <summary>A quoted string, or <c>%O</c>, which stands for the output file's name.</summary>
        String,

        /// <summary>A brace, a bracket, an operator or a separator.</summary>
        Punctuation,

        /// <summary>A value only ld65's command line gives, such as <c>%S</c>.</summary>
        Unknown,
    }

    /// <summary>Represents one token of a configuration and where it is written.</summary>
    private sealed record Token(TokenKind Kind, string Text, Span Span)
    {
        /// <summary>Returns a value indicating whether the token is the punctuation <paramref name="punctuation"/>.</summary>
        public bool Is(string punctuation) => Kind == TokenKind.Punctuation && Text == punctuation;
    }

    /// <summary>
    /// Represents the attributes of one entry, by name, each as the tokens of its value. ld65
    /// reads attribute names without regard to case.
    /// </summary>
    private sealed class Attributes() : Dictionary<string, IReadOnlyList<Token>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Represents one entry of a <c>MEMORY</c> or <c>SEGMENTS</c> block.</summary>
    /// <param name="Name">The entry's name.</param>
    /// <param name="At">Where the configuration names the entry.</param>
    /// <param name="Attributes">The entry's attributes.</param>
    private sealed record Entry(string Name, Span At, Attributes Attributes);

    /// <summary>
    /// Reads the blocks of a configuration into named entries, each with its attributes as the
    /// tokens of their values.
    /// </summary>
    private sealed class Reader(string path, List<Token> tokens)
    {
        private int at;

        /// <summary>Gets the entries of the <c>MEMORY</c> block, in order.</summary>
        public List<Entry> Memory { get; } = [];

        /// <summary>Gets the entries of the <c>SEGMENTS</c> block, in order.</summary>
        public List<Entry> Segments { get; } = [];

        /// <summary>Gets the value of each symbol the <c>SYMBOLS</c> block defines, by name.</summary>
        public Dictionary<string, IReadOnlyList<Token>> Symbols { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the problems found so far.</summary>
        public List<Diagnostic> Diagnostics { get; } = [];

        /// <summary>Gets the token being read, or null at the end of the configuration.</summary>
        private Token? Current => at < tokens.Count ? tokens[at] : null;

        /// <summary>
        /// Reads every block of the configuration, stopping at the first problem, since the rest
        /// of a configuration that does not parse cannot be trusted.
        /// </summary>
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

        /// <summary>
        /// Reads the entries of the block <paramref name="block"/> up to its closing brace, and
        /// returns false when the block does not parse.
        /// </summary>
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
                var attributes = new Attributes();
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

                var entry = new Entry(name.Text, name.Span, attributes);
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

        /// <summary>Skips the rest of a block nt65 does not read, through its closing brace.</summary>
        private void SkipBlock()
        {
            var depth = 1;
            while (Current is { } token && depth > 0)
            {
                depth += token.Is("{") ? 1 : token.Is("}") ? -1 : 0;
                at++;
            }
        }

        /// <summary>
        /// Reads the punctuation <paramref name="punctuation"/>, or reports that it is missing
        /// after <paramref name="after"/> and returns false.
        /// </summary>
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

        /// <summary>Reports that the configuration does not parse, saying what is wrong at <paramref name="span"/>.</summary>
        private void Report(Span span, string problem) =>
            Diagnostics.Add(new Diagnostic(span, Catalog.LinkedConfigInvalid.Message(problem)));
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

        /// <summary>Returns the value of the tokens, or null when it is unknown or they are not one expression.</summary>
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
                    _ => long.Parse(text, CultureInfo.InvariantCulture),
                };
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
            {
                return null;
            }
        }
    }
}
