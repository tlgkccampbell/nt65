using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// The VS Code grammar, editors/vscode/syntaxes/nt65.tmLanguage.json, is generated from the
/// lexer's tables and checked against the parser's reading of each line.
/// <para>
/// The language server colours every name by what it refers to, and the client draws that over
/// the grammar once the server answers. So that a name does not change colour then, the grammar
/// gives each declaration the scope VS Code maps the server's token type to: the name after
/// <c>.proc</c> is <c>entity.name.function</c>, as a <c>function</c> token is. A declaration is
/// known from its own line, or from the block it is in: the members of an enum, a struct or a
/// union, and the member names of a record. A use, <c>jsr init</c> or <c>Joy::A</c>, names
/// something only the server can see, and keeps the plain scope.
/// </para>
/// </summary>
internal static class TextMateGrammar
{
    public const string Comment = "comment.line.semicolon.nt65";
    public const string String = "string.quoted.double.nt65";
    public const string Character = "constant.character.nt65";
    public const string OperatorWord = "keyword.operator.word.nt65";
    public const string Directive = "keyword.control.directive.nt65";
    public const string Label = "entity.name.label.nt65";
    public const string CheapLocal = "variable.other.local.nt65";
    public const string Cpu = "constant.language.cpu.nt65";
    public const string Number = "constant.numeric.nt65";
    public const string Mnemonic = "keyword.other.mnemonic.nt65";
    public const string Register = "variable.language.register.nt65";
    public const string Identifier = "variable.other.nt65";
    public const string Operator = "keyword.operator.nt65";

    // The scopes VS Code gives the server's token types, which a theme colours them by.
    public const string Macro = "entity.name.function.preprocessor.nt65";
    public const string Function = "entity.name.function.nt65";
    public const string Namespace = "entity.name.namespace.nt65";
    public const string Enum = "entity.name.type.enum.nt65";
    public const string Struct = "entity.name.type.struct.nt65";
    public const string Type = "entity.name.type.nt65";
    public const string Variable = "variable.other.readwrite.nt65";
    public const string Constant = "variable.other.constant.nt65";
    public const string EnumMember = "variable.other.enummember.nt65";
    public const string Property = "variable.other.property.nt65";
    public const string Parameter = "variable.parameter.nt65";

    private const string Word = "[A-Za-z_][A-Za-z0-9_]*";
    private const string LineRules = "line";

    public static readonly string Path = Repo.Path("editors", "vscode", "syntaxes", "nt65.tmLanguage.json");

    private static readonly Lazy<Dictionary<Rule, Compiled>> compiled = new(() =>
    {
        var all = new Dictionary<Rule, Compiled>(ReferenceEqualityComparer.Instance);
        void Add(IEnumerable<Rule> rules)
        {
            foreach (var rule in rules)
            {
                if (rule.Include is not null || all.ContainsKey(rule))
                    continue;
                all[rule] = new Compiled(
                    new Regex(rule.Match ?? rule.Begin!, RegexOptions.CultureInvariant),
                    rule.End is null ? null : new Regex(rule.End, RegexOptions.CultureInvariant));
                Add(rule.Patterns);
            }
        }
        Add(Repository.Values.SelectMany(rules => rules));
        return all;
    });

    private static readonly Lazy<Dictionary<string, IReadOnlyList<Rule>>> repository = new(Build);

    /// <summary>The rules every line is scoped with, and the blocks that reach past a line.</summary>
    private static Dictionary<string, IReadOnlyList<Rule>> Repository => repository.Value;

    /// <summary>The top level of the grammar: every line's rules.</summary>
    private static IReadOnlyList<Rule> Root => [Rule.Including(LineRules)];

    public static string Generate()
    {
        var stream = new MemoryStream();
        var options = new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var json = new Utf8JsonWriter(stream, options))
        {
            json.WriteStartObject();
            json.WriteString("comment", "Generated from the lexer's tables by tests/Norristown.Tests/Syntax/TextMateGrammarTests.cs; " +
                "scripts/test.ps1 -Update rewrites it.");
            json.WriteString("name", "nt65");
            json.WriteString("scopeName", "source.nt65");
            WritePatterns(json, Root);
            json.WriteStartObject("repository");
            foreach (var (name, rules) in Repository)
            {
                json.WriteStartObject(name);
                WritePatterns(json, rules);
                json.WriteEndObject();
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>
    /// Scopes each character of each line the way a TextMate tokenizer runs the grammar. From the
    /// current position, the end of the innermost open block and every rule it holds are tried,
    /// the leftmost match wins, and an earlier one breaks a tie, the end first. A block's end may
    /// be <c>$</c>, which matches at the end of a line.
    /// </summary>
    public static string?[][] Scope(IReadOnlyList<string> lines)
    {
        var rules = compiled.Value;
        var open = new Stack<Rule>();
        var result = new string?[lines.Count][];
        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            var scopes = result[l] = new string?[line.Length];
            var pos = 0;
            while (true)
            {
                // The end of the innermost block is tried even at the end of the line, where `$` matches.
                Match? best = null;
                Rule? chosen = null;
                if (open.TryPeek(out var inside) && rules[inside].End!.Match(line, pos) is { Success: true } end)
                    best = end;
                if (pos < line.Length)
                {
                    foreach (var rule in Flatten(open.Count > 0 ? open.Peek().Patterns : Root))
                    {
                        var m = rules[rule].Regex.Match(line, pos);
                        if (m.Success && (best is null || m.Index < best.Index))
                            (best, chosen) = (m, rule);
                    }
                }
                if (best is null)
                    break;
                if (chosen is null)
                {
                    open.Pop();
                }
                else
                {
                    if (chosen.Captures is { } captures)
                    {
                        for (var g = 1; g < captures.Count; g++)
                            Paint(scopes, best.Groups[g], captures[g]);
                    }
                    else
                    {
                        Paint(scopes, best.Groups[0], chosen.Name);
                    }
                    if (chosen.Begin is not null)
                        open.Push(chosen);
                }
                pos = best.Length == 0 && chosen is { Begin: null } ? best.Index + 1 : best.Index + best.Length;
            }
        }
        return result;
    }

    /// <summary>
    /// The scope the grammar should give a token, or null for none (punctuation), from what the
    /// parser reads the line as and the block it is in.
    /// </summary>
    public static string? Expected(LineSyntax line, int index, BlockKind body)
    {
        var tokens = Tokens(line);
        var token = tokens[index];
        if (IsName(token) && NameScope(tokens, index, body) is { } name)
            return name;
        return token.Kind switch
        {
            SyntaxKind.Identifier => Identifier,
            SyntaxKind.CheapLocal => CheapLocal,
            SyntaxKind.Mnemonic => Mnemonic,
            SyntaxKind.Register => Register,
            SyntaxKind.Directive when token.Text.Equals(".mod", StringComparison.OrdinalIgnoreCase) => OperatorWord,
            SyntaxKind.Directive => Directive,
            SyntaxKind.NumberLiteral => Number,
            SyntaxKind.CharacterLiteral => Character,
            SyntaxKind.StringLiteral => String,
            SyntaxKind.CpuName => Cpu,
            SyntaxKind.ColonColon or SyntaxKind.Colon or SyntaxKind.Comma or SyntaxKind.OpenParen or SyntaxKind.CloseParen
                or SyntaxKind.OpenBracket or SyntaxKind.CloseBracket or SyntaxKind.OpenBrace or SyntaxKind.CloseBrace => null,
            _ => Operator,
        };
    }

    /// <summary>
    /// The scope of a name the parser reads as a declaration, a member or a parameter, or null
    /// for a name the grammar cannot place.
    /// </summary>
    private static string? NameScope(List<SyntaxToken> tokens, int index, BlockKind body)
    {
        if (index > 0 && tokens[index - 1].Kind == SyntaxKind.ColonColon)
            return Identifier;

        // The first name a node holds is the one a declaration declares.
        var token = tokens[index];
        var first = !token.Parent.ChildTokens.TakeWhile(earlier => earlier != token).Any(IsName);
        return token.Parent switch
        {
            LabelSyntax when first => body is BlockKind.Struct or BlockKind.Union ? Property : Label,
            ProcDeclarationSyntax or ExternProcDeclarationSyntax or FuncDeclarationSyntax when first => Function,
            MacroDeclarationSyntax when first => Macro,
            MacroParameterSyntax when first => Parameter,
            ImportItemSyntax item when first => item.EqualsToken is not null ? Constant : Variable,
            RepeatDirectiveSyntax or EachDirectiveSyntax or MultiProcDeclarationSyntax => Constant,
            ParameterListSyntax => Parameter,
            EnumDeclarationSyntax when first => Enum,
            StructDeclarationSyntax or UnionDeclarationSyntax when first => Struct,
            ScopeDeclarationSyntax when first => Namespace,
            CharmapDeclarationSyntax or SignatureDeclarationSyntax when first => Type,
            DataDeclarationSyntax or ListDeclarationSyntax or FrameDirectiveSyntax when first => Variable,
            ConstantDeclarationSyntax or ConfigDeclarationSyntax when first => Constant,
            EnumMemberSyntax when first => EnumMember,
            MemberValueSyntax when first => Property,
            NamedArgumentSyntax when first => Parameter,
            MacroCallSyntax when first && index + 1 < tokens.Count && tokens[index + 1].Kind == SyntaxKind.Bang => Macro,
            _ => token.Kind == SyntaxKind.Identifier && index + 1 < tokens.Count && tokens[index + 1].Kind == SyntaxKind.Bang
                ? Macro
                : null,
        };
    }

    /// <summary>
    /// Every token of a line in source order, which is every token of its statement; an
    /// exported declaration's include the <c>.export</c> before it.
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
                else
                    tokens.Add(child.AsToken());
            }
        }
        var statement = line.Statement;
        Walk(statement.IsExported ? statement.Parent! : statement);
        return tokens;
    }

    private static bool IsName(SyntaxToken token) =>
        token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic or SyntaxKind.CheapLocal;

    private static void Paint(string?[] scopes, Group group, string? scope)
    {
        if (!group.Success || scope is null)
            return;
        for (var i = group.Index; i < group.Index + group.Length; i++)
            scopes[i] = scope;
    }

    private static IEnumerable<Rule> Flatten(IEnumerable<Rule> rules) =>
        rules.SelectMany(rule => rule.Include is { } name ? Flatten(Repository[name]) : [rule]);

    private static Dictionary<string, IReadOnlyList<Rule>> Build()
    {
        var line = new List<Rule>();
        var rules = new Dictionary<string, IReadOnlyList<Rule>> { [LineRules] = line };
        var include = Rule.Including(LineRules);

        // Brackets inside a header or a call, so that their `)` does not close it.
        var parentheses = new Rule(Begin: @"\(", End: @"\)", Patterns: [include]);

        // A record's values, on the directive's line or the lines after it. A `{` inside one is a
        // record or a list of its own. A member's match starts with the space before it, so that
        // it starts where a constant declaration's would and wins the tie.
        var record = Rule.Including("record");
        rules["record"] = [new Rule(Begin: @"\{", End: @"\}",
            Patterns: [record, Rule.Scoped($@"\s*({Word})(?=\s*=(?!=))", Property), include])];

        // A conditional inside an enum body holds members too, so its `}` closes the
        // conditional rather than the enum. `} .else {` opens the next one on the line whose
        // `}` closed the last.
        var members = Rule.Including("members");
        rules["members"] = [Rule.Block($@"(?i)(\.(?:if|elseif|else))\b", @"\}", [Directive],
            [members, Rule.Scoped($@"^\s*({Word})", EnumMember), include])];

        // A struct or union body, which may hold anonymous ones.
        var layout = Rule.Including("layout");
        rules["layout"] = [Rule.Block($@"(?i)(\.(?:struct|union))(?:\s+({Word}))?\s*(\{{)", @"\}", [Directive, Struct],
            [layout, Rule.Scoped($@"^\s*(@?{Word})(?=\s*:(?!:))", Property), include])];

        line.AddRange(
        [
            new Rule(Match: ";.*$", Name: Comment),
            new Rule(Match: """
                "(?:[^"\\]|\\.)*"?
                """, Name: String),
            new Rule(Match: """
                '(?:[^'\\]|\\.)*'?
                """, Name: Character),
            new Rule(Match: @"(?i)\.mod\b", Name: OperatorWord),

            // Blocks, each opened by a directive on its line.
            Rule.Block($@"(?i)(\.enum)(?:\s+({Word}))?\s*(\{{)", @"\}", [Directive, Enum],
                [members, Rule.Scoped($@"^\s*({Word})", EnumMember), include]),
            layout,
            Rule.Block($@"(?i)(\.type)\b", "$", [Directive], [record, include]),
            Rule.Block($@"(?i)(\.macro)\s+({Word})\s*(\()", @"\)", [Directive, Macro],
                [parentheses, Rule.Scoped($@"(?<=[(,])\s*({Word})(?=\s*[,):=])", Parameter), include]),

            // The names an import declares, each first in its item: a constant given a value, or
            // an address. A routine's signature is in brackets of its own. "First in its item" is
            // a lookbehind, as a macro's parameters are, because `\G` means only "just after the
            // block opened" to VS Code's tokenizer.
            Rule.Block($@"(?i)(\.import)\b", "$", [Directive],
                [parentheses, Rule.Scoped($@"(?i)(?<=\.import\s|,)\s*({Word})(?=\s*=(?!=))", Constant),
                    Rule.Scoped($@"(?i)(?<=\.import\s|,)\s*({Word})", Variable), include]),

            // The name a repetition binds, last before its brace.
            Rule.Block($@"(?i)(\.(?:repeat|each))\b", @"(?=\{)|$", [Directive],
                [Rule.Scoped($@",\s*({Word})(?=\s*\{{)", Constant), include]),

            // The name a `.multiproc` binds is the one after its comma: a signature may follow
            // it, so it is not the last name before the brace as a repetition's is.
            Rule.Scoped($@"(?i)(\.multiproc)\s+(?:{Word}\s*::\s*)*({Word})\s*,\s*({Word})",
                Directive, Identifier, Constant),
            Rule.Block($@"(?i)(\.func)\s+({Word})\s*(\()", @"\)", [Directive, Function],
                [Rule.Scoped($@"\b({Word})", Parameter), include]),

            // A declaration's name, after the directive that declares it.
            Rule.Scoped($@"(?i)(\.proc)\s+({Word})", Directive, Function),
            Rule.Scoped($@"(?i)(\.macro)\s+({Word})", Directive, Macro),
            Rule.Scoped($@"(?i)(\.scope)\s+({Word})", Directive, Namespace),
            Rule.Scoped($@"(?i)(\.(?:charmap|signature))\s+({Word})", Directive, Type),
            Rule.Scoped($@"(?i)(\.(?:data|list|frame))\s+({Word})", Directive, Variable),
            Rule.Scoped($@"(?i)(\.(?:config|export))\s+({Word})(?=\s*=(?!=))", Directive, Constant),
            new Rule(Match: @"\.[A-Za-z_][A-Za-z0-9_]*", Name: Directive),
            Rule.Scoped($@"^\s*({Word})(?=\s*=(?!=))", Constant),

            // A label at the start of a line; `name::` is a scoped name, not a label.
            Rule.Scoped($@"^\s*(@?{Word})(?=\s*:(?!:))", Label),
            new Rule(Match: "@" + Word, Name: CheapLocal),
            new Rule(Match: @"(?i)\b65c02\b", Name: Cpu),
            new Rule(Match: @"\$[A-Za-z0-9_]*|%[A-Za-z0-9_]*|\b[0-9][A-Za-z0-9_]*", Name: Number),

            // After `::` a word is a member, however it is spelled.
            Rule.Scoped($@"::({Word})", Identifier),
            new Rule(Match: $@"(?i)\b(?:{string.Join("|", SyntaxFacts.Mnemonics)})\b", Name: Mnemonic),
            new Rule(Match: $@"(?i)\b(?:{string.Join("|", SyntaxFacts.Registers)})\b", Name: Register),

            // A macro call, after mnemonics and registers, which keep their scope even before a
            // `!`. Its named arguments are its parameters.
            Rule.Block($@"\b({Word})\s*(!)\s*(\()", @"\)", [Macro, Operator],
                [parentheses, Rule.Scoped($@"({Word})(?=\s*=(?!=))", Parameter), include]),
            Rule.Scoped($@"\b({Word})(?=\s*!(?!=))", Macro),
            new Rule(Match: @"\b" + Word, Name: Identifier),
            new Rule(Match: @"->|\.\.|<<|>>|<=|>=|==|!=|&&|\|\||\^\^|[-+*/&|^~!<>=#?]", Name: Operator),
        ]);
        return rules;
    }

    private static void WritePatterns(Utf8JsonWriter json, IReadOnlyList<Rule> rules)
    {
        json.WriteStartArray("patterns");
        foreach (var rule in rules)
            WriteRule(json, rule);
        json.WriteEndArray();
    }

    private static void WriteRule(Utf8JsonWriter json, Rule rule)
    {
        json.WriteStartObject();
        if (rule.Include is { } name)
        {
            json.WriteString("include", "#" + name);
            json.WriteEndObject();
            return;
        }
        json.WriteString(rule.Begin is null ? "match" : "begin", rule.Match ?? rule.Begin);
        if (rule.End is { } end)
            json.WriteString("end", end);
        if (rule.Name is { } scope)
            json.WriteString("name", scope);
        if (rule.Captures is { } captures)
        {
            json.WriteStartObject(rule.Begin is null ? "captures" : "beginCaptures");
            for (var g = 1; g < captures.Count; g++)
            {
                if (captures[g] is not { } captured)
                    continue;
                json.WriteStartObject(g.ToString(System.Globalization.CultureInfo.InvariantCulture));
                json.WriteString("name", captured);
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }
        if (rule.Begin is not null)
            WritePatterns(json, rule.Patterns);
        json.WriteEndObject();
    }

    /// <summary>
    /// One grammar rule: a <c>match</c> scoped by its name or by its groups, a <c>begin</c>/<c>end</c>
    /// block with the rules inside it, or an include of the repository. <c>Captures</c> holds the
    /// scope of each group by number, and its index 0 is unused.
    /// </summary>
    private sealed record Rule(
        string? Match = null, string? Name = null, IReadOnlyList<string?>? Captures = null,
        string? Begin = null, string? End = null, IReadOnlyList<Rule>? Patterns = null, string? Include = null)
    {
        public IReadOnlyList<Rule> Patterns { get; } = Patterns ?? [];

        public static Rule Including(string name) => new(Include: name);

        public static Rule Scoped(string match, params string?[] groups) => new(Match: match, Captures: [null, .. groups]);

        public static Rule Block(string begin, string end, IReadOnlyList<string?> groups, IReadOnlyList<Rule> patterns) =>
            new(Begin: begin, End: end, Captures: [null, .. groups], Patterns: patterns);
    }

    private sealed record Compiled(Regex Regex, Regex? End);
}
