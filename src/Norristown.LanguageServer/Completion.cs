using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides completion, which offers what could be typed at the caret and only that. It offers
/// the statements the enclosing context accepts, the forms an instruction has on this CPU and
/// the registers that index them, and the names a path leads to after <c>::</c> and in a
/// <c>.use</c>. It also offers the names in scope where an operand or an expression goes, the
/// items of a processor-state signature, and a macro's parameters as named arguments.
/// <para>
/// All of it is read from <see cref="LineContext"/>, which lexes the line up to the caret, rather
/// than from the nodes the file parsed to. That is deliberate. The caret cuts the line in the
/// middle of what is being typed, so <c>$10</c> reads as <c>$1</c> and <c>.byt</c> is not yet
/// <c>.byte</c>, and completion is about what has been typed so far. The tree answers questions
/// about the line's surroundings, such as which blocks hold it and what context they give it.
/// </para>
/// </summary>
internal static class Completion
{
    /// <summary>The processor-state items a signature may give for a point in the code, which a <c>.state</c> may give too.</summary>
    private static readonly string[] PointItems = ["a8", "a16", "a?", "i8", "i16", "i?", "native", "emu", "e?", "dp?", "dbr?"];

    /// <summary>
    /// The items that take a value, each inserted with its <c>=</c> so that the value follows.
    /// </summary>
    private static readonly string[] ValuedItems = ["dp = ", "dbr = "];

    /// <summary>
    /// The items with which a signature states that a routine or a macro leaves part of the
    /// processor state unchanged.
    /// </summary>
    private static readonly string[] KeepItems = ["a*", "i*", "e*", "dp*", "dbr*"];

    /// <summary>
    /// The items only a routine's signature may give, which describe how the routine is called and
    /// how it returns.
    /// </summary>
    private static readonly string[] RoutineItems = ["near", "far", "inline", "args", "interrupt", "noreturn"];

    /// <summary>
    /// The item that states which registers a routine preserves, inserted so that the registers
    /// follow it. A macro is expanded into the routine that calls it, so it has no such item.
    /// </summary>
    private static readonly string[] PromiseItems = ["keeps "];

    /// <summary>The register widths an <c>.ensure</c> can require.</summary>
    private static readonly string[] Widths = ["a8", "a16", "i8", "i16"];

    /// <summary>The attributes a segment declaration gives about where the segment is placed.</summary>
    private static readonly string[] SegmentAttributes = ["dp = ", "bank = ", "mirrors = "];

    /// <summary>
    /// The directives that declare the name after them. Nothing is offered for that new name.
    /// </summary>
    private static readonly HashSet<DirectiveKind> Declaring =
    [
        DirectiveKind.Module, DirectiveKind.Proc, DirectiveKind.Scope, DirectiveKind.Data, DirectiveKind.Enum,
        DirectiveKind.Struct, DirectiveKind.Union, DirectiveKind.Macro, DirectiveKind.Func, DirectiveKind.List,
        DirectiveKind.Charmap, DirectiveKind.Signature, DirectiveKind.Segment, DirectiveKind.Frame,
        DirectiveKind.Const, DirectiveKind.Import,
    ];

    /// <summary>
    /// The command the client runs after inserting an unfinished item. It reopens the
    /// completion list in VS Code, so that choosing <c>lda</c> offers its operand forms straight
    /// away.
    /// </summary>
    private static readonly Protocol.Command Again = new("Suggest", "editor.action.triggerSuggest");

    /// <summary>
    /// The handlers that <see cref="Collect"/> tries in turn, each for one place the caret can be.
    /// The first that recognizes the place offers what goes there and the rest are not tried, so
    /// the order matters. The last, <see cref="TryExpression"/>, recognizes every place.
    /// </summary>
    private static readonly Func<Site, bool>[] Handlers =
    [
        TryUse, TryPath, TryAfterBrace, TryEnsure, TryCpu, TryState, TrySignature, TryParameterKind,
        TryAfterMark, TryDeclaredName, TryRepetitionBinding, TryStatementStart, TryOperand, TryExpression,
    ];

    /// <summary>
    /// Returns the completion items at <paramref name="position"/> in <paramref name="model"/>'s
    /// file, and each item's documentation keyed by label. The client fetches the documentation
    /// for the one item it highlights rather than receiving it with every item.
    /// </summary>
    /// <param name="program">
    /// Every file, for the names a path leads to and the text a block opener inserts.
    /// </param>
    /// <param name="model">The file the caret is in.</param>
    /// <param name="cpu">The processor, which decides the instructions and the shape of a routine.</param>
    /// <param name="position">The position in the file's text.</param>
    /// <param name="snippets">Whether the client accepts snippets with tab stops.</param>
    public static (IReadOnlyList<Protocol.CompletionItem> Items, IReadOnlyDictionary<string, string> About) At(
        ProgramModel program, SemanticModel model, Cpu cpu, int position, bool snippets)
    {
        var line = LineContext.At(model.Tree, position);
        var items = new Dictionary<string, Suggestion>(StringComparer.Ordinal);
        if (!line.InText)
            Collect(new Site(program, model, line, cpu, items));
        if (snippets)
            Shaped(program, cpu, items);
        var range = Lsp.ToRange(model.Tree, line.Replaced);
        var about = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, suggestion) in items)
        {
            if (suggestion.Documentation is { Length: > 0 } documentation)
                about[label] = documentation;
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
    /// Returns the macro or function a call names, given as the name or path that ends at token
    /// index <paramref name="end"/>, exclusive.
    /// </summary>
    public static Symbol? Callee(SemanticModel model, LineContext line, int end)
    {
        var before = line.Before;
        if (end < 1 || !LineContext.IsWord(before[end - 1].Kind))
            return null;
        var path = line.PathBefore(end - 1) ?? [];
        return model.GetSymbolInfo(line.Caret, [.. path, before[end - 1].Text]).Symbol;
    }

    /// <summary>
    /// Makes each directive that opens a block insert the whole block as a snippet rather than
    /// just the directive, for a client that accepts snippets. Nothing else is a snippet.
    /// </summary>
    private static void Shaped(ProgramModel program, Cpu cpu, Dictionary<string, Suggestion> items)
    {
        foreach (var (name, suggestion) in items.ToList())
        {
            if (suggestion.Source == SuggestionSource.Directive
                && Snippets.Of(SyntaxFacts.DirectiveKindOf(name), program, cpu) is { } snippet)
            {
                items[name] = suggestion with { Text = snippet, IsSnippet = true };
            }
        }
    }

    /// <summary>
    /// Offers what goes at the caret by trying each of <see cref="Handlers"/> in turn until one
    /// recognizes the place.
    /// </summary>
    private static void Collect(Site site)
    {
        foreach (var handler in Handlers)
        {
            if (handler(site))
                return;
        }
    }

    /// <summary>
    /// Offers the names a path leads to in a <c>.use</c>. There a path starts at the root of the
    /// module tree, and inside the <c>{ }</c> the members of what the path before the brace leads
    /// to are offered.
    /// </summary>
    /// <returns>Whether the line is a <c>.use</c>.</returns>
    private static bool TryUse(Site site)
    {
        var (program, model, line, _, items) = site;
        if (line.Directive != DirectiveKind.Use)
            return false;
        var before = line.Before;
        var brace = LastIndex(before, SyntaxKind.OpenBrace);
        var path = brace >= 0 ? line.PathBefore(brace) : line.PathBefore(before.Count);
        if (path is null && brace < 0 && before.Count == line.Start + 1)
            AddModules(program, "", items);
        else if (path is not null && model.GetSymbolInfo(line.Caret, path, fromRoot: true) is { IsNone: false } found)
            AddMembers(program, model, found, modulesToo: brace < 0, items);
        return true;
    }

    /// <summary>Offers the members of what the path before a <c>::</c> leads to.</summary>
    /// <returns>Whether the caret follows a path.</returns>
    private static bool TryPath(Site site)
    {
        var (program, model, line, _, items) = site;
        if (line.PathBefore(line.Before.Count) is not { } walked)
            return false;
        if (model.GetSymbolInfo(line.Caret, walked) is { IsNone: false } found)
            AddMembers(program, model, found, modulesToo: true, items);
        return true;
    }

    /// <summary>
    /// Offers the next branch of a condition after a <c>}</c>, which closes a block and may be
    /// followed by nothing else.
    /// </summary>
    /// <returns>Whether the line so far is a lone <c>}</c>.</returns>
    private static bool TryAfterBrace(Site site)
    {
        if (site.Line.Before is not [(SyntaxKind.CloseBrace, _, _)])
            return false;
        AddDirectives([DirectiveKind.Else, DirectiveKind.ElseIf], site.Items);
        return true;
    }

    /// <summary>Offers the register widths an <c>.ensure</c> can require.</summary>
    /// <returns>Whether the line is an <c>.ensure</c>.</returns>
    private static bool TryEnsure(Site site)
    {
        if (site.Line.Directive != DirectiveKind.Ensure)
            return false;
        AddWords(Widths, "width", site.Items);
        return true;
    }

    /// <summary>Offers the processors after <c>.cpu</c>.</summary>
    /// <returns>Whether the caret is where a <c>.cpu</c> names its processor.</returns>
    private static bool TryCpu(Site site)
    {
        var line = site.Line;
        if (line.Directive != DirectiveKind.Cpu || line.Before.Count != line.Start + 1)
            return false;
        AddWords([.. CpuNames.All.Select(CpuNames.Format)], "processor", site.Items);
        return true;
    }

    /// <summary>
    /// Offers the items of a <c>.state</c>. Where the value of a <c>dp =</c> or <c>dbr =</c> goes,
    /// it offers what <see cref="TryExpression"/> does, since the value is an expression.
    /// </summary>
    /// <returns>Whether the line is a <c>.state</c>.</returns>
    private static bool TryState(Site site)
    {
        var (_, _, line, _, items) = site;
        if (line.Directive != DirectiveKind.State)
            return false;
        if (AfterValuedItem(line.Before))
            return TryExpression(site);
        AddWords(PointItems, "processor state", items);
        AddWords(ValuedItems, "processor state", items);
        AddWords(PromiseItems, "registers kept", items);
        return true;
    }

    /// <summary>
    /// Offers the items of a signature and the signature sets in scope. A macro's signature has
    /// no routine items. Where the value of a valued item goes, it offers what
    /// <see cref="TryExpression"/> does, since the value is an expression.
    /// </summary>
    /// <returns>Whether the caret is in a signature.</returns>
    private static bool TrySignature(Site site)
    {
        var (_, model, line, _, items) = site;
        if (InSignature(line, line.Directive) is not { } signature)
            return false;
        if (AfterValuedItem(line.Before))
            return TryExpression(site);
        AddWords(PointItems, "processor state", items);
        AddWords(ValuedItems, "processor state", items);
        AddWords(KeepItems, "processor state", items);
        if (signature != DirectiveKind.Macro)
        {
            AddWords(RoutineItems, "processor state", items);
            AddWords(PromiseItems, "registers kept", items);
        }
        AddInScope(model, line.Caret, items, symbol => symbol.Kind == SymbolKind.SignatureSet);
        return true;
    }

    /// <summary>Offers what a parameter's kind may be in a macro's header.</summary>
    /// <returns>Whether the caret is in a parameter's kind.</returns>
    private static bool TryParameterKind(Site site) =>
        site.Line.Directive == DirectiveKind.Macro && InParameterKind(site.Model, site.Line, site.Items);

    /// <summary>Offers the words that follow punctuation in a declaration, which <see cref="AfterMark"/> lists.</summary>
    /// <returns>Whether the caret follows such punctuation.</returns>
    private static bool TryAfterMark(Site site)
    {
        if (AfterMark(site.Line, site.Line.Directive) is not { } mark)
            return false;
        AddWords(mark.Words, mark.Detail, site.Items);
        return true;
    }

    /// <summary>
    /// Offers nothing where a declaration's name goes. The name is being made up, so until the
    /// <c>:</c> or <c>=</c> that follows it there is nothing to offer.
    /// </summary>
    /// <returns>Whether the caret is where a declaration's name goes.</returns>
    private static bool TryDeclaredName(Site site) =>
        site.Line.Directive is { } declaring && Declaring.Contains(declaring)
            && !site.Line.Before.Any(token => token.Kind is SyntaxKind.Colon or SyntaxKind.Equals);

    /// <summary>
    /// Offers nothing past the comma of a repetition, where it declares the name it binds and
    /// names nothing else.
    /// </summary>
    /// <returns>Whether the caret is past a repetition's comma.</returns>
    private static bool TryRepetitionBinding(Site site) =>
        site.Line.Directive is DirectiveKind.Repeat or DirectiveKind.Each
            && site.Line.Before.Any(token => token.Kind == SyntaxKind.Comma);

    /// <summary>
    /// Offers the first word of a statement, which depends on the context the line is in.
    /// </summary>
    /// <returns>Whether the caret is where a statement starts.</returns>
    private static bool TryStatementStart(Site site)
    {
        if (site.Line.Before.Count != site.Line.Start)
            return false;
        Starting(site.Program, site.Model, site.Line, site.Cpu, site.Items);
        return true;
    }

    /// <summary>Offers an instruction's operand, which is all that may follow a mnemonic.</summary>
    /// <returns>Whether the statement is an instruction.</returns>
    private static bool TryOperand(Site site)
    {
        if (site.Line.Before[site.Line.Start].Kind != SyntaxKind.Mnemonic)
            return false;
        Operand(site.Program, site.Model, site.Line, site.Cpu, site.Items);
        return true;
    }

    /// <summary>
    /// Offers what may go where an expression or a macro's argument goes. An argument of a macro
    /// call is offered what its parameter takes, and where an argument starts, the parameters it
    /// may name. A comparison with a parameter's argument is offered the words that parameter
    /// accepts. Anywhere else a name may follow, the start of an expression is offered.
    /// </summary>
    /// <returns>Always true, since this is the last handler and recognizes every place.</returns>
    private static bool TryExpression(Site site)
    {
        var (program, model, line, _, items) = site;
        var before = line.Before;
        if (line.OpenCall() is { } call && call.Open >= 2 && before[call.Open - 1].Kind == SyntaxKind.Bang
            && before[^1].Kind is SyntaxKind.OpenParen or SyntaxKind.Comma or SyntaxKind.Equals
            && Callee(model, line, call.Open - 1) is { Kind: SymbolKind.Macro } macro)
        {
            if (before[^1].Kind != SyntaxKind.Equals)
            {
                foreach (var parameter in macro.Parameters)
                {
                    items.TryAdd(parameter.Symbol.Name, new Suggestion(
                        SuggestionSource.Symbol, Protocol.CompletionItemKind.Property,
                        $"parameter: {parameter.Symbol.KindText}", parameter.Symbol.Name + " = ",
                        Band: Suggestion.InScope, Order: 1));
                }
            }
            if (CallHelp.ParameterAt(macro, before, call.Open, before.Count, call.Argument) is { } taking)
                Accepted(model, taking, items);
        }

        // The words of a comparison replace the expression rather than joining it.
        if (Compared(model, line) is var (name, accepts, isMode))
        {
            foreach (var word in ComparedWord.ChoicesFor(accepts, isMode))
            {
                items.TryAdd(word, new Suggestion(
                    SuggestionSource.Word, Protocol.CompletionItemKind.EnumMember,
                    isMode ? ParameterKinds.Mode(word) : $"a word {name} takes", word, Band: Suggestion.InScope));
            }
            return true;
        }

        if (!Ends(before[^1].Kind))
            AddExpression(program, model, line, items);
        return true;
    }

    /// <summary>
    /// Returns what the comparison at the caret compares against, as the parameter's name, what
    /// it accepts, and whether the word to compare with is a mode. This applies when the caret
    /// follows <c>==</c> or <c>!=</c> after <c>.mode(p)</c> of an <c>operand</c> parameter, or
    /// after the name of a <c>one</c> parameter or of a repetition's binding over a
    /// <c>list(one(...))</c>. Otherwise, returns null.
    /// </summary>
    private static (string Name, ArgumentKind Accepts, bool IsMode)? Compared(SemanticModel model, LineContext line)
    {
        var before = line.Before;
        if (before is not [.., var left, (SyntaxKind.EqualsEquals or SyntaxKind.BangEquals, _, _)])
            return null;
        if (left.Kind == SyntaxKind.CloseParen && before is [.., (SyntaxKind.Directive, var mode, _), (SyntaxKind.OpenParen, _, _),
                (SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic, var name, _), _, _]
            && SyntaxFacts.BuiltinKindOf(mode) == BuiltinKind.Mode)
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
    /// Offers completions for a parameter's kind in a macro's header. After a parameter's
    /// <c>:</c> and inside a <c>list(...)</c>, it offers the kinds and the enums in scope. Inside
    /// an <c>operand(...)</c>, it offers the modes the kind may list.
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
                            SuggestionSource.Word, Protocol.CompletionItemKind.EnumMember, ParameterKinds.Mode(mode), mode));
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

    /// <summary>
    /// Offers the kinds a parameter may be, and the enums in scope, whose members a parameter may
    /// take.
    /// </summary>
    private static void Kinds(SemanticModel model, LineContext line, Dictionary<string, Suggestion> items)
    {
        foreach (var (keyword, takes) in ParameterKinds.Keywords)
            AddWord(keyword, takes, items);
        AddInScope(model, line.Caret, items, symbol => symbol.Kind == SymbolKind.Enum);
    }

    /// <summary>
    /// Offers what an argument for <paramref name="parameter"/> may be, ahead of every other
    /// name. That is the members of the enum an enum kind names, by their bare names, and the
    /// words a <c>one(...)</c> lists. A <c>list</c> of either kind takes them too.
    /// </summary>
    private static void Accepted(SemanticModel model, MacroParameter parameter, Dictionary<string, Suggestion> items)
    {
        var accepts = parameter.Accepts.Kind == ParameterKind.List ? parameter.Accepts.Element : parameter.Accepts;
        if (accepts is { Kind: ParameterKind.Enum } && model.EnumOf(accepts) is { Body: { } body })
        {
            foreach (var member in body.Symbols.Where(member => member.Kind == SymbolKind.Constant))
            {
                items.TryAdd(member.Name, new Suggestion(
                    SuggestionSource.Symbol, Protocol.CompletionItemKind.EnumMember, Detail(member), member.Name, DocComments.Of(member),
                    Suggestion.InScope));
            }
        }
        else if (accepts is { Kind: ParameterKind.One })
        {
            foreach (var word in accepts.Words)
            {
                items.TryAdd(word, new Suggestion(
                    SuggestionSource.Word, Protocol.CompletionItemKind.EnumMember, $"a word {parameter.Name} takes", word, Band: Suggestion.InScope));
            }
        }
    }

    /// <summary>
    /// Checks whether a token finishes an expression, so that what may follow it is an operator or
    /// a separator and never a name of its own.
    /// </summary>
    private static bool Ends(SyntaxKind kind) =>
        kind is SyntaxKind.NumberLiteral or SyntaxKind.Identifier or SyntaxKind.CheapLocal or SyntaxKind.Register
            or SyntaxKind.Mnemonic or SyntaxKind.StringLiteral or SyntaxKind.CharacterLiteral
            or SyntaxKind.CloseParen or SyntaxKind.CloseBracket;

    /// <summary>Offers what may begin a statement where the caret is.</summary>
    private static void Starting(
        ProgramModel program, SemanticModel model, LineContext line, Cpu cpu,
        Dictionary<string, Suggestion> items)
    {
        foreach (var (name, detail) in Directives.At(line))
            items.TryAdd(name, new Suggestion(SuggestionSource.Directive, Protocol.CompletionItemKind.Keyword, detail, name));

        switch (line.Context)
        {
            // Code may begin with an instruction this CPU has, a macro in scope, or a block
            // that a macro body splices in by naming its parameter.
            case ContextKind.Code or ContextKind.Unknown:
                foreach (var mnemonic in SyntaxFacts.Mnemonics)
                {
                    if (!Instructions.Available(cpu, mnemonic))
                        continue;
                    var takes = ModesOf(cpu, mnemonic).Any(Takes);
                    var text = SyntaxFacts.TextOf(mnemonic);
                    items.TryAdd(text, new Suggestion(
                        SuggestionSource.Instruction, Protocol.CompletionItemKind.Text, "instruction", takes ? text + " " : text,
                        Band: Suggestion.Instruction));
                }
                AddInScope(model, line.Caret, items, symbol => symbol.Kind == SymbolKind.Macro
                    || (symbol.Kind == SymbolKind.MacroParameter && symbol.Parameter is { IsBlock: true }));
                Called(items);
                break;

            // A `.data` block holds data, the declarations that name it, and macro calls.
            case ContextKind.Data:
                AddInScope(model, line.Caret, items, symbol => symbol.Kind == SymbolKind.Macro);
                Called(items);
                break;

            // A line of values, of a list or of a charmap starts with an expression.
            case ContextKind.Values:
                AddExpression(program, model, line, items);
                break;

            // A record initializer gives the type's members their values, one a line.
            case ContextKind.Record:
                if (line.RecordType is { } path && model.GetSymbolInfo(line.Caret, path) is { IsNone: false } found)
                    AddMembers(program, model, found, modulesToo: false, items);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Makes each macro, at the start of a statement, insert the <c>!(</c> that calls it, so that
    /// its arguments are offered next.
    /// </summary>
    private static void Called(Dictionary<string, Suggestion> items)
    {
        foreach (var name in items
            .Where(item => item.Value.Source == SuggestionSource.Macro)
            .Select(item => item.Key)
            .ToList())
        {
            items[name] = items[name] with { Text = name + "!(" };
        }
    }

    /// <summary>
    /// Offers what may follow a mnemonic, which is the forms the instruction has on this CPU, the
    /// registers that index them, and the names an address or a value is built from.
    /// </summary>
    private static void Operand(
        ProgramModel program, SemanticModel model, LineContext line, Cpu cpu,
        Dictionary<string, Suggestion> items)
    {
        var mnemonic = SyntaxFacts.MnemonicKindOf(line.Before[line.Start].Text);
        var modes = ModesOf(cpu, mnemonic);
        if (modes.Count == 0)
            return;

        // The operand so far is checked for whether it starts inside a `(` or a `[`, whether
        // that has been closed again, and whether a `#` has made it a value.
        var operand = line.Before.Skip(line.Start + 1).ToList();
        var opened = operand.Count == 0 ? '\0' : operand[0].Kind switch
        {
            SyntaxKind.OpenParen => '(',
            SyntaxKind.OpenBracket => '[',
            _ => '\0',
        };
        var depth = 0;
        var value = false;
        foreach (var token in operand)
        {
            depth += token.Kind switch
            {
                SyntaxKind.OpenParen or SyntaxKind.OpenBracket => 1,
                SyntaxKind.CloseParen or SyntaxKind.CloseBracket => -1,
                _ => 0,
            };
            value |= token.Kind == SyntaxKind.Hash;
        }

        if (operand.Count == 0)
        {
            Forms(modes, cpu, mnemonic, items);

            // An instruction whose only operand is a value is written with the `#`, so a name
            // on its own is not something that could go there.
            if (modes.Any(mode => Takes(mode) && mode is not (AddressingMode.Immediate or AddressingMode.BlockMove)))
                AddExpression(program, model, line, items);
            return;
        }
        if (operand[^1].Kind == SyntaxKind.Comma)
        {
            Indexing(modes, opened, depth, value, items);

            // `bbr0 flags, @skip` is the one operand whose comma is followed by a target.
            if (modes.Contains(AddressingMode.DirectRelative))
                AddExpression(program, model, line, items);
            return;
        }
        if (!Ends(operand[^1].Kind))
            AddExpression(program, model, line, items);
    }

    /// <summary>
    /// Offers the operand forms an instruction has, each as the character or prefix that begins it.
    /// </summary>
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

    /// <summary>
    /// Offers what may follow the comma of an operand, which is the register that indexes it or a
    /// second value.
    /// </summary>
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
    /// Returns the addressing modes an instruction has on this CPU, or, for the long branches,
    /// which nt65 accepts on every CPU, a single relative-long target.
    /// </summary>
    private static IReadOnlySet<AddressingMode> ModesOf(Cpu cpu, MnemonicKind mnemonic) =>
        SyntaxFacts.IsLongBranch(mnemonic)
            ? new HashSet<AddressingMode> { AddressingMode.RelativeLong }
            : Instructions.Modes(cpu, mnemonic);

    /// <summary>Checks whether a form takes an operand of its own.</summary>
    private static bool Takes(AddressingMode mode) =>
        mode is not (AddressingMode.Implied or AddressingMode.Accumulator);

    /// <summary>Describes what an address-size prefix makes of the address after it.</summary>
    private static string Sized(string prefix) => prefix switch
    {
        "z:" => "the direct page",
        "a:" => "an absolute address",
        _ => "a long address",
    };

    /// <summary>
    /// Returns the words offered after punctuation in a declaration, or null if there are none.
    /// After a <c>:</c>, these are an address size or what a declaration or member holds. After a
    /// <c>,</c> in a <c>.segment</c>, they are the segment's attributes.
    /// </summary>
    private static (IEnumerable<string> Words, string Detail)? AfterMark(LineContext line, DirectiveKind? directive)
    {
        if (directive == DirectiveKind.Segment && line.Before is [.., (SyntaxKind.Comma, _, _)])
            return (SegmentAttributes, "where the segment lands");
        if (line.Before is not [.., (SyntaxKind.Colon, _, _)])
            return null;
        return directive switch
        {
            DirectiveKind.Import => ([.. Directives.Sizes, "proc("], "how the name is reached"),
            DirectiveKind.Export or DirectiveKind.Segment => (Directives.Sizes, "address size"),
            DirectiveKind.Data => (Directives.Data, "what it holds"),
            null when line.Context == ContextKind.TypeMembers => ([.. Directives.Elements, ".res"], "what it holds"),
            _ => null,
        };
    }

    /// <summary>
    /// Returns the scope a name leads into with <c>::</c>, which is its own body or its type's.
    /// </summary>
    private static Scope? BodyOf(Symbol symbol) => symbol.Body ?? symbol.Type?.Body;

    /// <summary>
    /// Offers the names after <c>::</c> where a path leads, and the modules below it when it is a
    /// module path.
    /// </summary>
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
                        SuggestionSource.Symbol, Protocol.CompletionItemKind.Reference, $"from `{string.Join("::", reexport.Path)}`",
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

    /// <summary>
    /// Offers the next part of every module path that starts with <paramref name="prefix"/>.
    /// </summary>
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
                SuggestionSource.Module, Protocol.CompletionItemKind.Module, next == rest ? "module" : "modules", next,
                Band: Suggestion.Module));
        }
    }

    /// <summary>
    /// Offers what an expression may start with. That is the prefix characters of numbers that
    /// are not plain decimal, the names in scope, the modules a path may lead into, and the
    /// built-in functions, including those only a macro body may use when the caret is in one.
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
        foreach (var builtin in SyntaxFacts.Builtins.Where(builtin => line.InMacro || !builtin.MacroOnly))
            items.TryAdd(builtin.Name, new Suggestion(
                SuggestionSource.Builtin, Protocol.CompletionItemKind.Function, "built-in function", builtin.Name + "("));
    }

    /// <summary>
    /// Offers every name that may appear alone at <paramref name="position"/> and that
    /// <paramref name="wanted"/> accepts, each under the name that reaches it there. The list
    /// and its order come from the model, which gets them from the binder, so what is offered is
    /// what the name will mean once it is typed.
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
                        SourceOf(symbol), KindOf(symbol), Detail(symbol), name, DocComments.Of(symbol), Suggestion.InScope, order));
                }
            }
            else if (means.Module is { } module)
            {
                items.TryAdd(name, new Suggestion(
                    SuggestionSource.Module, Protocol.CompletionItemKind.Module, $"module `{module}`", name,
                    Band: Suggestion.InScope, Order: order));
            }
        }
    }

    private static void AddDirectives(
        IEnumerable<DirectiveKind> names,
        Dictionary<string, Suggestion> items)
    {
        foreach (var (name, detail) in Directives.Described(names))
            items.TryAdd(name, new Suggestion(SuggestionSource.Directive, Protocol.CompletionItemKind.Keyword, detail, name));
    }

    private static void AddWords(
        IEnumerable<string> words, string detail,
        Dictionary<string, Suggestion> items)
    {
        foreach (var word in words)
            AddWord(word, detail, items);
    }

    /// <summary>
    /// Offers a word, inserted with whatever must follow it (a space, <c>=</c> or <c>(</c>) but
    /// listed without it, since the label is what the client filters on.
    /// </summary>
    private static void AddWord(
        string word, string detail,
        Dictionary<string, Suggestion> items)
    {
        var label = word.TrimEnd(' ', '=', '(');
        items.TryAdd(label.Length > 0 ? label : word, new Suggestion(
            SuggestionSource.Word, Protocol.CompletionItemKind.Keyword, detail, word));
    }

    private static void Add(Symbol symbol, Dictionary<string, Suggestion> items) =>
        items.TryAdd(symbol.DisplayName, new Suggestion(
            SourceOf(symbol), KindOf(symbol), Detail(symbol), symbol.DisplayName, DocComments.Of(symbol), Suggestion.InScope));

    private static string Detail(Symbol symbol) =>
        symbol.IsSetting ? $"setting = {symbol.Value}" : symbol.Value.IsKnown && !symbol.IsAddress ? $"{symbol.KindText} = {symbol.Value}" : symbol.KindText;

    /// <summary>
    /// Checks whether an item's text leaves the caret where something else must be typed, so that
    /// the client is asked to reopen the completion list as soon as it has inserted the item.
    /// </summary>
    private static bool Unfinished(string text) => text.Length > 0 && text[^1] is ' ' or ':' or '#' or '(' or '[';

    /// <summary>
    /// Returns the directive that declares the signature the line contains, when the caret is
    /// past where the signature starts, or null otherwise. A signature starts after <c>:</c> in a
    /// <c>.proc</c> or a <c>.macro</c>, after <c>=</c> in a <c>.signature</c>, and in the
    /// <c>proc(...)</c> of an <c>.import</c>.
    /// </summary>
    private static DirectiveKind? InSignature(LineContext line, DirectiveKind? directive)
    {
        var before = line.Before;
        var start = line.Start;
        switch (directive)
        {
            case DirectiveKind.Proc:
                for (var i = start + 2; i < before.Count; i++)
                {
                    if (before[i].Kind == SyntaxKind.Colon)
                        return directive;
                }
                return null;
            case DirectiveKind.Macro:
                var depth = 0;
                for (var i = start; i < before.Count; i++)
                {
                    depth += before[i].Kind switch { SyntaxKind.OpenParen => 1, SyntaxKind.CloseParen => -1, _ => 0 };
                    if (depth == 0 && before[i].Kind == SyntaxKind.Colon && i > start && before[i - 1].Kind != SyntaxKind.Identifier)
                        return directive;
                }
                return null;
            case DirectiveKind.Signature:
                return before.Any(token => token.Kind == SyntaxKind.Equals) ? directive : null;
            case DirectiveKind.Import:
                return line.OpenCall() is { Open: >= 1 } call
                    && before[call.Open - 1].Text.Equals("proc", StringComparison.OrdinalIgnoreCase)
                    ? directive
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Checks whether the caret is where the value of a <c>dp =</c> or <c>dbr =</c> goes, which
    /// is an expression. The value after <c>inline</c> or <c>args</c> counts too.
    /// </summary>
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

    /// <summary>
    /// Returns the source of a suggestion that names <paramref name="symbol"/>, which sets a macro
    /// apart from every other name.
    /// </summary>
    private static SuggestionSource SourceOf(Symbol symbol) =>
        symbol.Kind == SymbolKind.Macro ? SuggestionSource.Macro : SuggestionSource.Symbol;

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

    /// <summary>
    /// Represents the place completion was asked for, with the list its handlers add to.
    /// </summary>
    /// <param name="Program">Every file, for the names a path leads to and the text a block opener inserts.</param>
    /// <param name="Model">The file the caret is in.</param>
    /// <param name="Line">The line up to the caret.</param>
    /// <param name="Cpu">The processor, which decides the instructions and the shape of a routine.</param>
    /// <param name="Items">The suggestions gathered so far, keyed by label.</param>
    private readonly record struct Site(
        ProgramModel Program, SemanticModel Model, LineContext Line, Cpu Cpu, Dictionary<string, Suggestion> Items);
}
