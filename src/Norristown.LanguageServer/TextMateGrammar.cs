using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Builds the VS Code grammar, editors/vscode/syntaxes/nt65.tmLanguage.json, from the lexer's
/// tables. The tests check the file against <see cref="Generate"/>, and scope lines with
/// <see cref="Root"/> and <see cref="Repository"/> to check the grammar against the parser's
/// reading of each line.
/// <para>
/// The language server colours every name by what it refers to, and the client draws that
/// colour over the grammar once the server answers. So that a name does not change colour at that
/// point, the grammar gives each declaration the scope that VS Code maps the server's token type
/// to. For example, the name after <c>.proc</c> is <c>entity.name.function</c>, as a
/// <c>function</c> token is. A declaration is recognized from its own line, or from the block it
/// is in, as the members of an enum, a struct or a union and the member names of a record are. A
/// use, such as <c>jsr init</c> or <c>Joy::A</c>, names something only the server can see, so it
/// keeps the plain scope.
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
    public const string Kind = "storage.type.nt65";
    public const string Mode = "constant.language.mode.nt65";

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

    private static readonly Lazy<Dictionary<string, IReadOnlyList<TextMateRule>>> repository = new(Build);

    /// <summary>Gets the rules every line is scoped with, and the blocks that reach past a line.</summary>
    public static Dictionary<string, IReadOnlyList<TextMateRule>> Repository => repository.Value;

    /// <summary>Gets the top level of the grammar, which includes every line's rules.</summary>
    public static IReadOnlyList<TextMateRule> Root => [TextMateRule.Including(LineRules)];

    /// <summary>Returns the text of the grammar file.</summary>
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

    private static Dictionary<string, IReadOnlyList<TextMateRule>> Build()
    {
        var line = new List<TextMateRule>();
        var rules = new Dictionary<string, IReadOnlyList<TextMateRule>> { [LineRules] = line };
        var include = TextMateRule.Including(LineRules);

        // Parentheses nested inside a macro header or a call, matched as a block of their own so
        // that their `)` does not end the header or the call.
        var parentheses = new TextMateRule(Begin: @"\(", End: @"\)", Patterns: [include]);

        // These rules scope what a macro parameter takes, from its `:` to the `,`, `)` or `=`
        // after the kind. That text holds the kind's word, the modes an `operand(...)` lists, the
        // words a `one(...)` lists, the kind of each item of a `list(...)`, or the range of a
        // `const(...)`. An enum's name is a use.
        var kind = TextMateRule.Including("kind");
        rules["kind"] =
        [
            TextMateRule.Block(@"(?i)\b(operand)\s*(\()", @"\)", [Kind, null],
                [TextMateRule.Scoped($@"(?i)\b({string.Join("|", ArgumentKind.OperandModes)})\b", Mode), include]),
            TextMateRule.Block(@"(?i)\b(one)\s*(\()", @"\)", [Kind, null], [TextMateRule.Scoped($@"\b({Word})", EnumMember), include]),
            TextMateRule.Block(@"(?i)\b(list)\s*(\()", @"\)", [Kind, null], [kind]),
            TextMateRule.Block(@"(?i)\b(const)\s*(\()", @"\)", [Kind, null], [include]),
            TextMateRule.Scoped(@"(?i)\b(expr|const|ident|operand|block)\b", Kind),
            include,
        ];

        // These rules scope a record's values, on the directive's line or the lines after it. A
        // `{` inside a record opens a record or a list of its own. A member's match starts with
        // the space before it, so that it starts where a constant declaration's would and wins
        // the tie.
        var record = TextMateRule.Including("record");
        rules["record"] = [new TextMateRule(Begin: @"\{", End: @"\}",
            Patterns: [record, TextMateRule.Scoped($@"\s*({Word})(?=\s*=(?!=))", Property), include])];

        // A conditional inside an enum body holds members too, so its `}` closes the
        // conditional rather than the enum. `} .else {` opens the next one on the line whose
        // `}` closed the last.
        var members = TextMateRule.Including("members");
        rules["members"] = [TextMateRule.Block($@"(?i)(\.(?:if|elseif|else))\b", @"\}", [Directive],
            [members, TextMateRule.Scoped($@"^\s*({Word})", EnumMember), include])];

        // These rules scope a struct or union body, which may hold anonymous structs and unions.
        var layout = TextMateRule.Including("layout");
        rules["layout"] = [TextMateRule.Block($@"(?i)(\.(?:struct|union))(?:\s+({Word}))?\s*(\{{)", @"\}", [Directive, Struct],
            [layout, TextMateRule.Scoped($@"^\s*(@?{Word})(?=\s*:(?!:))", Property), include])];

        line.AddRange(
        [
            new TextMateRule(Match: ";.*$", Name: Comment),
            new TextMateRule(Match: """
                "(?:[^"\\]|\\.)*"?
                """, Name: String),
            new TextMateRule(Match: """
                '(?:[^'\\]|\\.)*'?
                """, Name: Character),
            new TextMateRule(Match: @"(?i)\.mod\b", Name: OperatorWord),

            // Blocks, each opened by a directive on its line.
            TextMateRule.Block($@"(?i)(\.enum)(?:\s+({Word}))?\s*(\{{)", @"\}", [Directive, Enum],
                [members, TextMateRule.Scoped($@"^\s*({Word})", EnumMember), include]),
            layout,
            TextMateRule.Block($@"(?i)(\.type)\b", "$", [Directive], [record, include]),
            TextMateRule.Block($@"(?i)(\.macro)\s+({Word})\s*(\()", @"\)", [Directive, Macro],
                [new TextMateRule(Begin: ":", End: @"(?=[,)=])", Patterns: [kind]), parentheses, TextMateRule.Scoped($@"(?<=[(,])\s*({Word})(?=\s*[,):=])", Parameter), include]),

            // An import declares names, each first in its item. Each name is a constant given a
            // value, or an address. A routine's signature is in brackets of its own. "First in its
            // item" is a lookbehind, as it is for a macro's parameters, because to VS Code's
            // tokenizer `\G` means only "just after the block opened".
            TextMateRule.Block($@"(?i)(\.import)\b", "$", [Directive],
                [parentheses, TextMateRule.Scoped($@"(?i)(?<=\.import\s|,)\s*({Word})(?=\s*=(?!=))", Constant),
                    TextMateRule.Scoped($@"(?i)(?<=\.import\s|,)\s*({Word})", Variable), include]),

            // The word after a module name's `:` says whether the module may be placed.
            TextMateRule.Block($@"(?i)(\.module)\b", "$", [Directive],
                [TextMateRule.Scoped(@"(?i)(?<!:):(?!:)\s*\b(placed|placeable)\b", Kind), include]),

            // The name a repetition binds, last before its brace.
            TextMateRule.Block($@"(?i)(\.(?:repeat|each))\b", @"(?=\{)|$", [Directive],
                [TextMateRule.Scoped($@",\s*({Word})(?=\s*\{{)", Constant), include]),

            // The name a `.multiproc` binds is the one after its comma: a signature may follow
            // it, so it is not the last name before the brace as a repetition's is.
            TextMateRule.Scoped($@"(?i)(\.multiproc)\s+(?:{Word}\s*::\s*)*({Word})\s*,\s*({Word})",
                Directive, Identifier, Constant),
            TextMateRule.Block($@"(?i)(\.func)\s+({Word})\s*(\()", @"\)", [Directive, Function],
                [TextMateRule.Scoped($@"\b({Word})", Parameter), include]),

            // A declaration's name, after the directive that declares it.
            TextMateRule.Scoped($@"(?i)(\.proc)\s+({Word})", Directive, Function),
            TextMateRule.Scoped($@"(?i)(\.macro)\s+({Word})", Directive, Macro),
            TextMateRule.Scoped($@"(?i)(\.scope)\s+({Word})", Directive, Namespace),
            TextMateRule.Scoped($@"(?i)(\.(?:charmap|signature))\s+({Word})", Directive, Type),
            TextMateRule.Scoped($@"(?i)(\.(?:data|list|frame))\s+({Word})", Directive, Variable),
            TextMateRule.Scoped($@"(?i)(\.(?:config|export))\s+({Word})(?=\s*=(?!=))", Directive, Constant),
            new TextMateRule(Match: @"\.[A-Za-z_][A-Za-z0-9_]*", Name: Directive),
            TextMateRule.Scoped($@"^\s*({Word})(?=\s*=(?!=))", Constant),

            // A label sits at the start of a line. `name::` is a scoped name, not a label.
            TextMateRule.Scoped($@"^\s*(@?{Word})(?=\s*:(?!:))", Label),
            new TextMateRule(Match: "@" + Word, Name: CheapLocal),
            new TextMateRule(Match: @"(?i)\b65c02\b", Name: Cpu),
            new TextMateRule(Match: @"\$[A-Za-z0-9_]*|%[A-Za-z0-9_]*|\b[0-9][A-Za-z0-9_]*", Name: Number),

            // After `::` a word is a member, even one spelled like a mnemonic or a register.
            TextMateRule.Scoped($@"::({Word})", Identifier),
            new TextMateRule(Match: $@"(?i)\b(?:{string.Join("|", SyntaxFacts.Mnemonics.Select(SyntaxFacts.TextOf))})\b", Name: Mnemonic),
            new TextMateRule(Match: $@"(?i)\b(?:{string.Join("|", SyntaxFacts.Registers)})\b", Name: Register),

            // A macro call, after mnemonics and registers, which keep their scope even before a
            // `!`. Its named arguments are its parameters.
            TextMateRule.Block($@"\b({Word})\s*(!)\s*(\()", @"\)", [Macro, Operator],
                [parentheses, TextMateRule.Scoped($@"({Word})(?=\s*=(?!=))", Parameter), include]),
            TextMateRule.Scoped($@"\b({Word})(?=\s*!(?!=))", Macro),
            new TextMateRule(Match: @"\b" + Word, Name: Identifier),
            new TextMateRule(Match: @"->|\.\.|<<|>>|<=|>=|==|!=|&&|\|\||\^\^|[-+*/&|^~!<>=#?]", Name: Operator),
        ]);
        return rules;
    }

    private static void WritePatterns(Utf8JsonWriter json, IReadOnlyList<TextMateRule> rules)
    {
        json.WriteStartArray("patterns");
        foreach (var rule in rules)
            WriteRule(json, rule);
        json.WriteEndArray();
    }

    private static void WriteRule(Utf8JsonWriter json, TextMateRule rule)
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
}

