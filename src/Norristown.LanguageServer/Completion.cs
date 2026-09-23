using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What could be written at the caret, and only that: the statements the enclosing context
/// accepts, the forms an instruction has on this CPU and the registers that index them, the
/// names a path leads to after <c>::</c> and in a <c>.use</c>, the names in scope where an
/// operand or an expression goes, the items of a processor-state signature, and a macro's
/// parameters as named arguments.
/// <para>
/// All of it is read off <see cref="LineContext"/>, which lexes the line as far as the caret,
/// rather than off the nodes the file parsed to. That is deliberate: the caret cuts the line in
/// the middle of what is being typed, so <c>$10</c> reads as <c>$1</c> and <c>.byt</c> is not
/// yet <c>.byte</c>, and completion is about what has been typed so far. The tree answers
/// questions about the line's surroundings: which blocks hold it, and what context they give it.
/// </para>
/// </summary>
internal static class Completion
{
    /// <summary>The processor-state items a signature may give for a point in the code, which a <c>.state</c> may give too.</summary>
    private static readonly string[] PointItems = ["a8", "a16", "a?", "i8", "i16", "i?", "native", "emu", "e?", "dp?", "dbr?"];

    /// <summary>The items that take a value, written with their <c>=</c> so the value follows.</summary>
    private static readonly string[] ValuedItems = ["dp = ", "dbr = "];

    /// <summary>The items with which a signature says a routine or a macro leaves part of the processor state unchanged.</summary>
    private static readonly string[] KeepItems = ["a*", "i*", "e*", "dp*", "dbr*"];

    /// <summary>The items only a routine's signature may give: how it is called and how it returns.</summary>
    private static readonly string[] RoutineItems = ["near", "far", "inline", "args", "interrupt", "noreturn"];

    /// <summary>
    /// The item that says which registers a routine preserves, written so that the registers
    /// follow it. A macro is expanded into the routine that calls it, so it has no such item.
    /// </summary>
    private static readonly string[] PromiseItems = ["keeps "];

    /// <summary>The register widths an <c>.ensure</c> can require.</summary>
    private static readonly string[] Widths = ["a8", "a16", "i8", "i16"];

    /// <summary>The attributes a segment declaration gives about where the segment is placed.</summary>
    private static readonly string[] SegmentAttributes = ["dp = ", "bank = ", "mirrors = "];

    /// <summary>The directives that declare the name written after them; nothing is offered for that new name.</summary>
    private static readonly HashSet<string> Declaring = new(StringComparer.Ordinal)
    {
        ".module", ".proc", ".scope", ".data", ".enum", ".struct", ".union", ".macro", ".func", ".list",
        ".charmap", ".signature", ".segment", ".frame", ".config", ".import",
    };

    /// <summary>
    /// The command the client runs after inserting an unfinished item. It reopens the
    /// completion list in VS Code, so that choosing <c>lda</c> offers its operand forms straight
    /// away.
    /// </summary>
    private static readonly Protocol.Command Again = new("Suggest", "editor.action.triggerSuggest");

    /// <summary>
    /// The completion items at <paramref name="position"/> in <paramref name="model"/>'s file,
    /// and, keyed by label, each item's documentation, which the client fetches for the one item
    /// it highlights rather than receiving it with every item.
    /// </summary>
    /// <param name="program">Every file, for the names a path leads to and what a block opener writes.</param>
    /// <param name="model">The file the caret is in.</param>
    /// <param name="cpu">The processor, which decides the instructions and the shape of a routine.</param>
    /// <param name="position">Where in the file's text.</param>
    /// <param name="snippets">Whether the client accepts snippets with tab stops.</param>
    public static (IReadOnlyList<Protocol.CompletionItem> Items, IReadOnlyDictionary<string, string> About) At(
        ProgramModel program, SemanticModel model, Cpu cpu, int position, bool snippets)
    {
        var line = LineContext.At(model.Tree, position);
        var items = new Dictionary<string, Suggestion>(StringComparer.Ordinal);
        if (!line.InText)
            Collect(program, model, line, cpu, items);
        if (snippets)
            Shaped(program, cpu, items);
        var range = Lsp.ToRange(model.Tree, line.Replaced);
        var about = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, suggestion) in items)
        {
            if (suggestion.Documentation is { Length: > 0 } written)
                about[label] = written;
        }
        return (
            [.. items
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new Protocol.CompletionItem(item.Key, item.Value.Kind, item.Value.Detail,
                    new Protocol.TextEdit(range, item.Value.Text),
                    Unfinished(item.Value.Text) ? Again : null,
                    null,
                    item.Value.SortText(item.Key),
                    item.Value.IsSnippet ? Protocol.InsertTextFormat.Snippet : null))],
            about);
    }

    /// <summary>
    /// Makes each directive that opens a block insert the whole block as a snippet rather than
    /// just the directive, for a client that accepts snippets. Nothing else is a snippet.
    /// </summary>
    private static void Shaped(ProgramModel program, Cpu cpu, Dictionary<string, Suggestion> items)
    {
        foreach (var (name, suggestion) in items.ToList())
        {
            if (suggestion.Kind == Protocol.CompletionItemKind.Keyword
                && Snippets.Of(name, program, cpu) is { } written)
            {
                items[name] = suggestion with { Text = written, IsSnippet = true };
            }
        }
    }

    /// <summary>
    /// The macro or function a call names, written as the name or path that ends at
    /// <paramref name="end"/>, exclusive.
    /// </summary>
    public static Symbol? Callee(SemanticModel model, LineContext line, int end)
    {
        var before = line.Before;
        if (end < 1 || !LineContext.IsWord(before[end - 1].Kind))
            return null;
        var path = line.PathBefore(end - 1) ?? [];
        return model.GetSymbolInfo(line.Caret, [.. path, before[end - 1].Text]).Symbol;
    }

    private static void Collect(
        ProgramModel program, SemanticModel model, LineContext line, Cpu cpu,
        Dictionary<string, Suggestion> items)
    {
        var before = line.Before;
        var directive = line.Directive;

        // After `::`, offer the members of whatever the path leads to. In a `.use`, a path
        // starts at the root of the module tree, and inside its `{ }` the members of what the
        // path before the brace leads to are offered.
        if (directive == ".use")
        {
            var brace = LastIndex(before, SyntaxKind.OpenBrace);
            var path = brace >= 0 ? line.PathBefore(brace) : line.PathBefore(before.Count);
            if (path is null && brace < 0 && before.Count == line.Start + 1)
                AddModules(program, "", items);
            else if (path is not null && model.GetSymbolInfo(line.Caret, path, fromRoot: true) is { IsNone: false } found)
                AddMembers(program, model, found, modulesToo: brace < 0, items);
            return;
        }
        if (line.PathBefore(before.Count) is { } walked)
        {
            if (model.GetSymbolInfo(line.Caret, walked) is { IsNone: false } found)
                AddMembers(program, model, found, modulesToo: true, items);
            return;
        }

        // A `}` closes a block, and only the next branch of a condition may follow it.
        if (before.Count == 1 && before[0].Kind == SyntaxKind.CloseBrace)
        {
            AddDirectives([".else", ".elseif"], items);
            return;
        }

        if (directive == ".ensure")
        {
            AddWords(Widths, "width", items);
            return;
        }
        if (directive == ".cpu" && before.Count == line.Start + 1)
        {
            AddWords([.. CpuNames.All.Select(CpuNames.Spell)], "processor", items);
            return;
        }
        if (directive == ".state")
        {
            if (!AfterValuedItem(before))
            {
                AddWords(PointItems, "processor state", items);
                AddWords(ValuedItems, "processor state", items);
                AddWords(PromiseItems, "registers kept", items);
                return;
            }
        }
        else if (InSignature(line, directive) is { } signature)
        {
            if (!AfterValuedItem(before))
            {
                AddWords(PointItems, "processor state", items);
                AddWords(ValuedItems, "processor state", items);
                AddWords(KeepItems, "processor state", items);
                if (signature != ".macro")
                {
                    AddWords(RoutineItems, "processor state", items);
                    AddWords(PromiseItems, "registers kept", items);
                }
                AddInScope(model, line.Caret, items, symbol => symbol.Kind == SymbolKind.SignatureSet);
                return;
            }
        }
        else if (directive == ".macro" && InParameterKind(model, line, items))
        {
            return;
        }
        else if (AfterMark(line, directive) is { } written)
        {
            AddWords(written.Words, written.Detail, items);
            return;
        }
        else if (directive is { } declaring && Declaring.Contains(declaring)
            && !before.Any(token => token.Kind is SyntaxKind.Colon or SyntaxKind.Equals))
        {
            // The name a declaration introduces is being made up, so until the `:` or `=` that
            // follows it there is nothing to offer.
            return;
        }
        else if (directive is ".repeat" or ".each" && before.Any(token => token.Kind == SyntaxKind.Comma))
        {
            // Past the comma a repetition declares the name it binds, and names nothing else.
            return;
        }

        // The first word of a statement, which depends on the context the line is in.
        if (before.Count == line.Start)
        {
            Starting(program, model, line, cpu, items);
            return;
        }

        // What follows a mnemonic is that instruction's operand, and nothing else.
        if (before[line.Start].Kind == SyntaxKind.Mnemonic)
        {
            Operand(program, model, line, cpu, items);
            return;
        }

        // An argument of a macro call is offered what the parameter it is for takes, and where an
        // argument starts, the parameters it may name.
        if (line.OpenCall() is { } call && call.Open >= 2 && before[call.Open - 1].Kind == SyntaxKind.Bang
            && before[^1].Kind is SyntaxKind.OpenParen or SyntaxKind.Comma or SyntaxKind.Equals
            && Callee(model, line, call.Open - 1) is { Kind: SymbolKind.Macro } macro)
        {
            if (before[^1].Kind != SyntaxKind.Equals)
            {
                foreach (var parameter in macro.Parameters)
                {
                    items.TryAdd(parameter.Symbol.Name, new Suggestion(
                        Protocol.CompletionItemKind.Property, $"parameter: {parameter.Symbol.KindText}",
                        parameter.Symbol.Name + " = ", Band: Suggestion.InScope, Order: 1));
                }
            }
            if (CallHelp.ParameterAt(macro, before, call.Open, before.Count, call.Argument) is { } taking)
                Accepted(model, taking, items);
        }

        // A comparison with a parameter's argument takes one of the words that parameter accepts.
        if (Compared(model, line) is var (name, accepts, isMode))
        {
            foreach (var word in ComparedWord.ChoicesFor(accepts, isMode))
            {
                items.TryAdd(word, new Suggestion(
                    Protocol.CompletionItemKind.EnumMember,
                    isMode ? ParameterKinds.Mode(word) : $"a word {name} takes", word, Band: Suggestion.InScope));
            }
            return;
        }

        if (!Ends(before[^1].Kind))
            AddExpression(program, model, line, items);
    }

    /// <summary>
    /// What the comparison at the caret compares against, when the caret follows <c>==</c> or
    /// <c>!=</c> after <c>.mode(p)</c> of an <c>operand</c> parameter, or after the name of a
    /// <c>one</c> parameter or of a repetition's binding over a <c>list(one(...))</c>: the
    /// parameter's name, what it accepts, and whether the word to compare with is a mode.
    /// </summary>
    private static (string Name, ArgumentKind Accepts, bool IsMode)? Compared(SemanticModel model, LineContext line)
    {
        var before = line.Before;
        if (before is not [.., var left, (SyntaxKind.EqualsEquals or SyntaxKind.BangEquals, _, _)])
            return null;
        if (left.Kind == SyntaxKind.CloseParen && before is [.., (SyntaxKind.Directive, var mode, _), (SyntaxKind.OpenParen, _, _),
                (SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic, var name, _), _, _]
            && mode.Equals(".mode", StringComparison.OrdinalIgnoreCase))
        {
            return model.GetSymbolInfo(line.Caret, [name]).Symbol is { Parameter: { Kind: ParameterKind.Operand } operand }
                ? (operand.Name, operand.Accepts, true)
                : null;
        }
        return LineContext.IsWord(left.Kind)
            && model.GetSymbolInfo(line.Caret, [left.Text]).Symbol is { } symbol
            && ComparedWord.WordsOf(symbol, name => model.SymbolOf(name)) is { } words
                ? (symbol.Name, words, false)
                : null;
    }

    /// <summary>
    /// Completions for a parameter's kind in a macro's header: after a parameter's <c>:</c> and
    /// inside a <c>list(...)</c>, the kinds and the enums in scope; inside an
    /// <c>operand(...)</c>, the modes it may list.
    /// </summary>
    /// <returns>Whether the caret is in a kind, so that nothing else is offered there.</returns>
    private static bool InParameterKind(SemanticModel model, LineContext line, Dictionary<string, Suggestion> items)
    {
        var before = line.Before;
        if (line.OpenCall() is not { } open || before.Count == 0)
            return false;

        // A parenthesis nested inside the header's parameter list belongs to a kind, and the
        // word before it says which kind.
        if (open.Open >= 1 && line.OpenCall(open.Open) is not null
            && before[open.Open - 1] is { Kind: SyntaxKind.Identifier } word)
        {
            switch (word.Text.ToLowerInvariant())
            {
                case "operand":
                    foreach (var mode in ArgumentKind.OperandModes)
                    {
                        items.TryAdd(mode, new Suggestion(
                            Protocol.CompletionItemKind.EnumMember, ParameterKinds.Mode(mode), mode));
                    }
                    return true;
                case "list":
                    Kinds(model, line, items);
                    return true;
                case "one":
                    // The words a `one` accepts are made up by the macro's author, so there is
                    // nothing to offer.
                    return true;
                default:
                    return false;
            }
        }
        if (before[^1].Kind != SyntaxKind.Colon)
            return false;
        Kinds(model, line, items);
        return true;
    }

    /// <summary>The kinds a parameter may be, and the enums in scope, whose members a parameter may take.</summary>
    private static void Kinds(SemanticModel model, LineContext line, Dictionary<string, Suggestion> items)
    {
        foreach (var (written, takes) in ParameterKinds.Written)
            AddWord(written, takes, items);
        AddInScope(model, line.Caret, items, symbol => symbol.Kind == SymbolKind.Enum);
    }

    /// <summary>
    /// What an argument for <paramref name="parameter"/> may be, offered before every other name:
    /// the members of the enum an enum kind names, by their bare names, and the words a
    /// <c>one(...)</c> lists. A <c>list</c> of either takes them too.
    /// </summary>
    private static void Accepted(SemanticModel model, MacroParameter parameter, Dictionary<string, Suggestion> items)
    {
        var accepts = parameter.Accepts.Kind == ParameterKind.List ? parameter.Accepts.Element : parameter.Accepts;
        if (accepts is { Kind: ParameterKind.Enum } && model.EnumOf(accepts) is { Body: { } body })
        {
            foreach (var member in body.Symbols.Where(member => member.Kind == SymbolKind.Constant))
            {
                items.TryAdd(member.Name, new Suggestion(
                    Protocol.CompletionItemKind.EnumMember, Detail(member), member.Name, DocComments.Of(member),
                    Suggestion.InScope));
            }
        }
        else if (accepts is { Kind: ParameterKind.One })
        {
            foreach (var word in accepts.Words)
            {
                items.TryAdd(word, new Suggestion(
                    Protocol.CompletionItemKind.EnumMember, $"a word {parameter.Name} takes", word, Band: Suggestion.InScope));
            }
        }
    }

    /// <summary>
    /// Whether a token finishes an expression, so that what may follow it is an operator or a
    /// separator and never a name of its own.
    /// </summary>
    private static bool Ends(SyntaxKind kind) =>
        kind is SyntaxKind.NumberLiteral or SyntaxKind.Identifier or SyntaxKind.CheapLocal or SyntaxKind.Register
            or SyntaxKind.Mnemonic or SyntaxKind.StringLiteral or SyntaxKind.CharacterLiteral
            or SyntaxKind.CloseParen or SyntaxKind.CloseBracket;

    /// <summary>What may begin a statement where the caret is.</summary>
    private static void Starting(
        ProgramModel program, SemanticModel model, LineContext line, Cpu cpu,
        Dictionary<string, Suggestion> items)
    {
        foreach (var (name, detail) in Directives.At(line))
            items.TryAdd(name, new Suggestion(Protocol.CompletionItemKind.Keyword, detail, name));

        switch (line.Place)
        {
            // Code: the instructions this CPU has, the macros in scope, and a block a macro
            // body splices in by naming its parameter.
            case Place.Code or Place.Unknown:
                foreach (var mnemonic in SyntaxFacts.Mnemonics)
                {
                    if (!Instructions.Writable(cpu, mnemonic))
                        continue;
                    var takes = ModesOf(cpu, mnemonic).Any(Takes);
                    var written = SyntaxFacts.TextOf(mnemonic);
                    items.TryAdd(written, new Suggestion(
                        Protocol.CompletionItemKind.Text, "instruction", takes ? written + " " : written,
                        Band: Suggestion.Instruction));
                }
                AddInScope(model, line.Caret, items, symbol => symbol.Kind == SymbolKind.Macro
                    || (symbol.Kind == SymbolKind.MacroParameter && symbol.Parameter is { IsBlock: true }));
                Called(items);
                break;

            // A `.data` block holds data, the declarations that name it, and macro calls.
            case Place.Data:
                AddInScope(model, line.Caret, items, symbol => symbol.Kind == SymbolKind.Macro);
                Called(items);
                break;

            // A line of values, of a list or of a charmap starts with an expression.
            case Place.Values:
                AddExpression(program, model, line, items);
                break;

            // A record initializer gives the type's members their values, one a line.
            case Place.Record:
                if (line.RecordType is { } path && model.GetSymbolInfo(line.Caret, path) is { IsNone: false } found)
                    AddMembers(program, model, found, modulesToo: false, items);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// At the start of a statement a macro is inserted with the <c>!(</c> that calls it, so that
    /// its arguments are offered next. Macros are the only items whose kind is
    /// <c>Snippet</c>, which is how they are found here.
    /// </summary>
    private static void Called(Dictionary<string, Suggestion> items)
    {
        foreach (var name in items
            .Where(item => item.Value.Kind == Protocol.CompletionItemKind.Snippet)
            .Select(item => item.Key)
            .ToList())
        {
            items[name] = items[name] with { Text = name + "!(" };
        }
    }

    /// <summary>
    /// What may follow a mnemonic: the forms the instruction has on this CPU, the registers
    /// that index them, and the names an address or a value is written from.
    /// </summary>
    private static void Operand(
        ProgramModel program, SemanticModel model, LineContext line, Cpu cpu,
        Dictionary<string, Suggestion> items)
    {
        var mnemonic = SyntaxFacts.MnemonicKindOf(line.Before[line.Start].Text);
        var modes = ModesOf(cpu, mnemonic);
        if (modes.Count == 0)
            return;

        // The operand so far: whether it is written inside a `(` or a `[`, whether that has
        // been closed again, and whether a `#` has made it a value.
        var written = line.Before.Skip(line.Start + 1).ToList();
        var opened = written.Count == 0 ? '\0' : written[0].Kind switch
        {
            SyntaxKind.OpenParen => '(',
            SyntaxKind.OpenBracket => '[',
            _ => '\0',
        };
        var depth = 0;
        var value = false;
        foreach (var token in written)
        {
            depth += token.Kind switch
            {
                SyntaxKind.OpenParen or SyntaxKind.OpenBracket => 1,
                SyntaxKind.CloseParen or SyntaxKind.CloseBracket => -1,
                _ => 0,
            };
            value |= token.Kind == SyntaxKind.Hash;
        }

        if (written.Count == 0)
        {
            Forms(modes, cpu, mnemonic, items);

            // An instruction whose only operand is a value is written with the `#`, so a name
            // on its own is not something that could go there.
            if (modes.Any(mode => Takes(mode) && mode is not (AddressingMode.Immediate or AddressingMode.BlockMove)))
                AddExpression(program, model, line, items);
            return;
        }
        if (written[^1].Kind == SyntaxKind.Comma)
        {
            Indexing(modes, opened, depth, value, items);

            // `bbr0 flags, @skip` is the one operand whose comma is followed by a target.
            if (modes.Contains(AddressingMode.DirectRelative))
                AddExpression(program, model, line, items);
            return;
        }
        if (!Ends(written[^1].Kind))
            AddExpression(program, model, line, items);
    }

    /// <summary>The operand forms an instruction has, each offered as the character or prefix that begins it.</summary>
    private static void Forms(
        IReadOnlySet<AddressingMode> modes, Cpu cpu, MnemonicKind mnemonic,
        Dictionary<string, Suggestion> items)
    {
        if (modes.Contains(AddressingMode.Immediate))
            AddWord("#", "a value", items);
        else if (modes.Contains(AddressingMode.BlockMove))
            AddWord("#", "the source bank", items);
        if (modes.Contains(AddressingMode.Accumulator))
            AddWord("a", "the accumulator", items);
        if (modes.Any(mode => mode is AddressingMode.DirectIndirect or AddressingMode.DirectIndirectX
                or AddressingMode.DirectIndirectY or AddressingMode.AbsoluteIndirect
                or AddressingMode.AbsoluteIndirectX or AddressingMode.StackRelativeIndirectY))
        {
            AddWord("(", "through a pointer", items);
        }
        if (modes.Any(mode => mode is AddressingMode.DirectIndirectLong or AddressingMode.DirectIndirectLongY
                or AddressingMode.AbsoluteIndirectLong))
        {
            AddWord("[", "through a long pointer", items);
        }

        // A control transfer takes a near or a far target, so no prefix sizes it.
        if (Instructions.IsControlTransfer(mnemonic))
            return;
        foreach (var mode in modes)
        {
            if (Instructions.Prefix(mode) is { } prefix)
                AddWord(prefix, Sized(prefix), items);
        }
        if (cpu == Cpu.Wdc65816 && modes.Contains(AddressingMode.Direct))
            AddWord("d:", "a constant address in the direct page", items);
    }

    /// <summary>What may follow the comma of an operand: the register that indexes it, or a second value.</summary>
    private static void Indexing(
        IReadOnlySet<AddressingMode> modes, char opened, int depth, bool value,
        Dictionary<string, Suggestion> items)
    {
        if (opened == '(' && depth > 0)
        {
            if (modes.Contains(AddressingMode.DirectIndirectX) || modes.Contains(AddressingMode.AbsoluteIndirectX))
                AddWord("x", "a table of pointers", items);
            if (modes.Contains(AddressingMode.StackRelativeIndirectY))
                AddWord("s", "a pointer on the stack", items);
            return;
        }
        if (opened == '(')
        {
            if (modes.Contains(AddressingMode.DirectIndirectY) || modes.Contains(AddressingMode.StackRelativeIndirectY))
                AddWord("y", "indexed by Y", items);
            return;
        }
        if (opened == '[')
        {
            if (modes.Contains(AddressingMode.DirectIndirectLongY))
                AddWord("y", "indexed by Y", items);
            return;
        }
        if (value)
        {
            if (modes.Contains(AddressingMode.BlockMove))
                AddWord("#", "the destination bank", items);
            return;
        }
        if (modes.Contains(AddressingMode.DirectX) || modes.Contains(AddressingMode.AbsoluteX)
            || modes.Contains(AddressingMode.LongX))
        {
            AddWord("x", "indexed by X", items);
        }
        if (modes.Contains(AddressingMode.DirectY) || modes.Contains(AddressingMode.AbsoluteY))
            AddWord("y", "indexed by Y", items);
        if (modes.Contains(AddressingMode.StackRelative))
            AddWord("s", "an offset from the stack pointer", items);
    }

    /// <summary>
    /// The addressing modes an instruction has on this CPU, or, for the long branches, which
    /// nt65 accepts on every CPU, a single relative-long target.
    /// </summary>
    private static IReadOnlySet<AddressingMode> ModesOf(Cpu cpu, MnemonicKind mnemonic) =>
        SyntaxFacts.IsLongBranch(mnemonic)
            ? new HashSet<AddressingMode> { AddressingMode.RelativeLong }
            : Instructions.Modes(cpu, mnemonic);

    /// <summary>Whether a form is written with an operand of its own.</summary>
    private static bool Takes(AddressingMode mode) =>
        mode is not (AddressingMode.Implied or AddressingMode.Accumulator);

    /// <summary>What an address-size prefix makes of the address after it.</summary>
    private static string Sized(string prefix) => prefix switch
    {
        "z:" => "the direct page",
        "a:" => "an absolute address",
        _ => "a long address",
    };

    /// <summary>
    /// The words offered after punctuation in a declaration: an address size or what a
    /// declaration or member holds after a <c>:</c>, and a segment's attributes after a <c>,</c>.
    /// </summary>
    private static (IEnumerable<string> Words, string Detail)? AfterMark(LineContext line, string? directive)
    {
        if (directive == ".segment" && line.Before is [.., (SyntaxKind.Comma, _, _)])
            return (SegmentAttributes, "where the segment lands");
        if (line.Before is not [.., (SyntaxKind.Colon, _, _)])
            return null;
        return directive switch
        {
            ".import" => ([.. Directives.Sizes, "proc("], "how the name is reached"),
            ".export" or ".segment" => (Directives.Sizes, "address size"),
            ".data" => (Directives.Data, "what it holds"),
            null when line.Place == Place.TypeMembers => ([.. Directives.Elements, ".res"], "what it holds"),
            _ => null,
        };
    }

    /// <summary>What a name leads into with <c>::</c>: its own body, or its type's.</summary>
    private static Scope? BodyOf(Symbol symbol) => symbol.Body ?? symbol.Type?.Body;

    /// <summary>The names after <c>::</c> where a path leads, and the modules below it when it is a module path.</summary>
    private static void AddMembers(
        ProgramModel program, SemanticModel model, SymbolInfo at, bool modulesToo,
        Dictionary<string, Suggestion> items)
    {
        if (at.Module is { } prefix)
        {
            if (modulesToo)
                AddModules(program, prefix + "::", items);
            if (program.Symbols.ModuleNamed(prefix) is { } module)
            {
                var own = module.Tree == model.Tree;
                foreach (var declared in module.FileScope.Symbols.Where(declared => !declared.IsCheapLocal && (own || declared.IsExported)))
                    Add(declared, items);
                foreach (var reexport in module.Reexports)
                {
                    items.TryAdd(reexport.Name, new Suggestion(
                        Protocol.CompletionItemKind.Reference, $"from `{string.Join("::", reexport.Path)}`",
                        reexport.Name, Band: Suggestion.InScope));
                }
            }
            return;
        }
        if (at.Symbol is { } symbol && BodyOf(symbol) is { } body)
        {
            var own = symbol.Tree == model.Tree;
            foreach (var member in body.Symbols.Where(member => !member.IsCheapLocal && (own || member.IsExported)))
                Add(member, items);
        }
    }

    /// <summary>The next part of every module path that starts with <paramref name="prefix"/>.</summary>
    private static void AddModules(
        ProgramModel program, string prefix,
        Dictionary<string, Suggestion> items)
    {
        foreach (var module in program.Symbols.Modules)
        {
            if (module.Name is not { } name || !name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length)
                continue;
            var rest = name[prefix.Length..];
            var next = rest.Split("::")[0];
            items.TryAdd(next, new Suggestion(
                Protocol.CompletionItemKind.Module, next == rest ? "module" : "modules", next,
                Band: Suggestion.Module));
        }
    }

    /// <summary>
    /// What an expression may start with: the prefix characters of numbers that are not plain
    /// decimal, the names in scope, the modules a path may lead into, and the built-in
    /// functions, including those only a macro body may use when the caret is in one.
    /// </summary>
    private static void AddExpression(
        ProgramModel program, SemanticModel model, LineContext line,
        Dictionary<string, Suggestion> items)
    {
        AddWord("$", "a hexadecimal number", items);
        AddWord("%", "a binary number", items);
        AddWord("'", "a character", items);
        AddInScope(model, line.Caret, items, symbol => symbol.Kind is not (SymbolKind.Macro or SymbolKind.SignatureSet));
        AddModules(program, "", items);
        var builtins = line.InMacro
            ? SyntaxFacts.BuiltinFunctions.Concat(SyntaxFacts.MacroBuiltinFunctions)
            : SyntaxFacts.BuiltinFunctions;
        foreach (var builtin in builtins)
            items.TryAdd(builtin, new Suggestion(Protocol.CompletionItemKind.Function, "built-in function", builtin + "("));
    }

    /// <summary>
    /// Every name that may be written alone at <paramref name="position"/> and that
    /// <paramref name="wanted"/> accepts, each under the spelling that reaches it there. The
    /// list and its order are the model's, which is the binder's, so what is offered is what
    /// the name will mean once it is written.
    /// </summary>
    private static void AddInScope(
        SemanticModel model, int position,
        Dictionary<string, Suggestion> items, Func<Symbol, bool> wanted)
    {
        var order = 0;
        foreach (var (name, means) in model.LookupNames(position))
        {
            order++;
            if (means.Symbol is { } symbol)
            {
                if (wanted(symbol))
                {
                    items.TryAdd(name, new Suggestion(
                        KindOf(symbol), Detail(symbol), name, DocComments.Of(symbol), Suggestion.InScope, order));
                }
            }
            else if (means.Module is { } module)
            {
                items.TryAdd(name, new Suggestion(
                    Protocol.CompletionItemKind.Module, $"module `{module}`", name,
                    Band: Suggestion.InScope, Order: order));
            }
        }
    }

    private static void AddDirectives(
        IEnumerable<string> names,
        Dictionary<string, Suggestion> items)
    {
        foreach (var (name, detail) in Directives.Described(names))
            items.TryAdd(name, new Suggestion(Protocol.CompletionItemKind.Keyword, detail, name));
    }

    private static void AddWords(
        IEnumerable<string> words, string detail,
        Dictionary<string, Suggestion> items)
    {
        foreach (var word in words)
            AddWord(word, detail, items);
    }

    /// <summary>
    /// A word, inserted with whatever must follow it (a space, <c>=</c> or <c>(</c>) but listed
    /// without it, since the label is what the client filters on.
    /// </summary>
    private static void AddWord(
        string word, string detail,
        Dictionary<string, Suggestion> items)
    {
        var label = word.TrimEnd(' ', '=', '(');
        items.TryAdd(label.Length > 0 ? label : word, new Suggestion(Protocol.CompletionItemKind.Keyword, detail, word));
    }

    private static void Add(Symbol symbol, Dictionary<string, Suggestion> items) =>
        items.TryAdd(symbol.DisplayName, new Suggestion(
            KindOf(symbol), Detail(symbol), symbol.DisplayName, DocComments.Of(symbol), Suggestion.InScope));

    private static string Detail(Symbol symbol) =>
        symbol.IsDefine ? "define" : symbol.Value.IsKnown && !symbol.IsAddress ? $"{symbol.KindText} = {symbol.Value}" : symbol.KindText;

    /// <summary>
    /// Whether an item's text leaves the caret where something else must be written, so the
    /// client is asked to reopen the completion list as soon as it has inserted it.
    /// </summary>
    private static bool Unfinished(string text) => text.Length > 0 && text[^1] is ' ' or ':' or '#' or '(' or '[';

    /// <summary>
    /// The signature a line is writing, by the directive that declares it, when the caret is past
    /// where the signature starts: after <c>:</c> in a <c>.proc</c> or a <c>.macro</c>, after
    /// <c>=</c> in a <c>.signature</c>, and in the <c>proc(...)</c> of an <c>.import</c>.
    /// </summary>
    private static string? InSignature(LineContext line, string? directive)
    {
        var before = line.Before;
        var start = line.Start;
        switch (directive)
        {
            case ".proc":
                for (var i = start + 2; i < before.Count; i++)
                {
                    if (before[i].Kind == SyntaxKind.Colon)
                        return directive;
                }
                return null;
            case ".macro":
                var depth = 0;
                for (var i = start; i < before.Count; i++)
                {
                    depth += before[i].Kind switch { SyntaxKind.OpenParen => 1, SyntaxKind.CloseParen => -1, _ => 0 };
                    if (depth == 0 && before[i].Kind == SyntaxKind.Colon && i > start && before[i - 1].Kind != SyntaxKind.Identifier)
                        return directive;
                }
                return null;
            case ".signature":
                return before.Any(token => token.Kind == SyntaxKind.Equals) ? directive : null;
            case ".import":
                return line.OpenCall() is { Open: >= 1 } call
                    && before[call.Open - 1].Text.Equals("proc", StringComparison.OrdinalIgnoreCase)
                    ? directive
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Whether the caret is where the value of a <c>dp =</c> or <c>dbr =</c> goes, which is an expression.</summary>
    private static bool AfterValuedItem(IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> before)
    {
        for (var i = before.Count - 1; i >= 1; i--)
        {
            if (before[i].Kind == SyntaxKind.Comma)
                return false;
            if (before[i].Kind == SyntaxKind.Equals)
                return before[i - 1].Text.ToLowerInvariant() is "dp" or "dbr" or "inline" or "args";
            if (i == before.Count - 1 && before[i].Text.ToLowerInvariant() is "inline" or "args")
                return true;
        }
        return false;
    }

    private static int LastIndex(IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> tokens, SyntaxKind kind)
    {
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (tokens[i].Kind == kind)
                return i;
        }
        return -1;
    }

    private static Protocol.CompletionItemKind KindOf(Symbol symbol) => symbol.Kind switch
    {
        SymbolKind.Proc or SymbolKind.ExternProc or SymbolKind.Func => Protocol.CompletionItemKind.Function,
        SymbolKind.Label or SymbolKind.ImportedAddress or SymbolKind.AddressAlias => Protocol.CompletionItemKind.Reference,
        SymbolKind.Constant or SymbolKind.ImportedConstant => symbol.IsEnumMember
            ? Protocol.CompletionItemKind.EnumMember
            : Protocol.CompletionItemKind.Constant,
        SymbolKind.Scope => Protocol.CompletionItemKind.Module,
        SymbolKind.Enum => Protocol.CompletionItemKind.Enum,
        SymbolKind.Struct or SymbolKind.Union => Protocol.CompletionItemKind.Struct,
        SymbolKind.Member => Protocol.CompletionItemKind.Field,
        SymbolKind.Data or SymbolKind.List or SymbolKind.Charmap or SymbolKind.Frame => Protocol.CompletionItemKind.Variable,
        SymbolKind.Macro => Protocol.CompletionItemKind.Snippet,
        _ => Protocol.CompletionItemKind.TypeParameter,
    };
}
