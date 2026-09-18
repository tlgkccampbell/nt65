using Norristown.Layout;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What could be written at the caret, and only that: the statements the place the caret is in
/// accepts, the forms an instruction has on this CPU and the registers that index them, the
/// names a path leads to after <c>::</c> and in a <c>.use</c>, the names in scope where an
/// operand or an expression goes, the items of a processor-state signature, and a macro's
/// parameters as named arguments.
/// </summary>
internal static class Completion
{
    /// <summary>What a signature may say about the processor at a point, which a <c>.state</c> may too.</summary>
    private static readonly string[] PointItems = ["a8", "a16", "a?", "i8", "i16", "i?", "native", "emu", "e?", "dp?", "dbr?"];

    /// <summary>The items that give a value, written with it.</summary>
    private static readonly string[] ValuedItems = ["dp = ", "dbr = "];

    /// <summary>What a signature says a routine or a macro leaves alone.</summary>
    private static readonly string[] KeepItems = ["a*", "i*", "e*", "dp*", "dbr*"];

    /// <summary>What only a routine's signature says: how it is called and left.</summary>
    private static readonly string[] RoutineItems = ["near", "far", "inline", "args", "interrupt", "noreturn"];

    /// <summary>
    /// What a routine hands back, written with the registers to follow. A macro is expanded
    /// into the routine that calls it, so it has none of its own.
    /// </summary>
    private static readonly string[] PromiseItems = ["keeps "];

    /// <summary>What an <c>.ensure</c> makes hold.</summary>
    private static readonly string[] Widths = ["a8", "a16", "i8", "i16"];

    /// <summary>What a segment declaration says about where it lands.</summary>
    private static readonly string[] SegmentAttributes = ["dp = ", "bank = ", "mirrors = "];

    /// <summary>What a macro parameter accepts.</summary>
    private static readonly string[] ParameterKinds = ["expr", "const", "ident", "operand", "block", "one(", "list("];

    /// <summary>The directives that declare the name written after them, where nothing is completed.</summary>
    private static readonly HashSet<string> Declaring = new(StringComparer.Ordinal)
    {
        ".module", ".proc", ".scope", ".data", ".enum", ".struct", ".union", ".macro", ".func", ".list",
        ".charmap", ".signature", ".segment", ".frame", ".config", ".import",
    };

    /// <summary>
    /// What the client runs once it has written an item. VS Code asks for the next list with
    /// this, so that choosing <c>lda</c> offers what its operand may be straight away.
    /// </summary>
    private static readonly Protocol.Command Again = new("Suggest", "editor.action.triggerSuggest");

    /// <summary>What could be written at <paramref name="position"/> in the file <paramref name="model"/> is of.</summary>
    public static IReadOnlyList<Protocol.CompletionItem> At(
        ProgramModel program, SemanticModel model, Cpu cpu, int position)
    {
        var line = LineContext.At(model.Tree, position);
        var items = new Dictionary<string, Suggestion>(StringComparer.Ordinal);
        if (!line.InText)
            Collect(program, model, line, cpu, items);
        var range = Lsp.ToRange(model.Tree, line.Replaced);
        return [.. items
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new Protocol.CompletionItem(item.Key, item.Value.Kind, item.Value.Detail,
                new Protocol.TextEdit(range, item.Value.Text),
                Unfinished(item.Value.Text) ? Again : null,
                item.Value.Documentation is { } written ? Protocol.MarkupContent.Markdown(written) : null))];
    }

    /// <summary>
    /// The macro or function a call names, written as the name or path that ends at
    /// <paramref name="end"/>, exclusive.
    /// </summary>
    public static Symbol? Callee(ProgramModel program, SemanticModel model, Scope scope, LineContext line, int end)
    {
        var before = line.Before;
        if (end < 1 || !LineContext.IsWord(before[end - 1].Kind))
            return null;
        var path = line.PathBefore(end - 1) ?? [];
        var found = Walk(program, model, scope, [.. path, before[end - 1].Text], fromRoot: false);
        return found?.Symbol;
    }

    private static void Collect(
        ProgramModel program, SemanticModel model, LineContext line, Cpu cpu,
        Dictionary<string, Suggestion> items)
    {
        var before = line.Before;
        var directive = line.Directive;
        var scope = model.ScopeAt(line.Caret);

        // After `::`, what the path leads to has the names; in a `.use`, a path starts at the
        // modules' root, and in its `{ }` names what the path before it leads to.
        if (directive == ".use")
        {
            var brace = LastIndex(before, SyntaxKind.OpenBrace);
            var path = brace >= 0 ? line.PathBefore(brace) : line.PathBefore(before.Count);
            if (path is null && brace < 0 && before.Count == line.Start + 1)
                AddModules(program, "", items);
            else if (path is not null && Walk(program, model, scope, path, fromRoot: true) is { } found)
                AddMembers(program, model, found, modulesToo: brace < 0, items);
            return;
        }
        if (line.PathBefore(before.Count) is { } walked)
        {
            if (Walk(program, model, scope, walked, fromRoot: false) is { } found)
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
                AddInScope(program, model, scope, items, symbol => symbol.Kind == SymbolKind.SignatureSet);
                return;
            }
        }
        else if (AfterMark(line, directive) is { } written)
        {
            AddWords(written.Words, written.Detail, items);
            return;
        }
        else if (directive is { } declaring && Declaring.Contains(declaring)
            && !before.Any(token => token.Kind is SyntaxKind.Colon or SyntaxKind.Equals))
        {
            // What a declaration names is a name being made up, and until the `:` or the `=`
            // that it goes on with, nothing but the rest of that name may be written.
            return;
        }
        else if (directive is ".repeat" or ".each" && before.Any(token => token.Kind == SyntaxKind.Comma))
        {
            // Past the comma a repetition declares the name it binds, and names nothing else.
            return;
        }

        // A statement's first word, which the place the line is in decides.
        if (before.Count == line.Start)
        {
            Starting(program, model, scope, line, cpu, items);
            return;
        }

        // What follows a mnemonic is that instruction's operand, and nothing else.
        if (before[line.Start].Kind == SyntaxKind.Mnemonic)
        {
            Operand(program, model, scope, line, cpu, items);
            return;
        }

        // An argument of a macro call may name the parameter it is for.
        if (line.OpenCall() is { } call && call.Open >= 2 && before[call.Open - 1].Kind == SyntaxKind.Bang
            && before[^1].Kind is SyntaxKind.OpenParen or SyntaxKind.Comma
            && Callee(program, model, scope, line, call.Open - 1) is { Kind: SymbolKind.Macro } macro)
        {
            foreach (var parameter in macro.Parameters)
                items.TryAdd(parameter.Symbol.Name, new Suggestion(Protocol.CompletionItemKind.Property, $"parameter: {parameter.Symbol.KindText}", parameter.Symbol.Name + " = "));
        }

        if (!Ends(before[^1].Kind))
            AddExpression(program, model, scope, line, items);
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
        ProgramModel program, SemanticModel model, Scope scope, LineContext line, Cpu cpu,
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
                    items.TryAdd(mnemonic, new Suggestion(Protocol.CompletionItemKind.Text, "instruction", takes ? mnemonic + " " : mnemonic));
                }
                AddInScope(program, model, scope, items, symbol => symbol.Kind == SymbolKind.Macro
                    || (symbol.Kind == SymbolKind.MacroParameter && symbol.Parameter is { IsBlock: true }));
                Called(items);
                break;

            // A `.data` block holds data, the declarations that name it, and macro calls.
            case Place.Data:
                AddInScope(program, model, scope, items, symbol => symbol.Kind == SymbolKind.Macro);
                Called(items);
                break;

            // A line of values, of a list or of a charmap starts with an expression.
            case Place.Values:
                AddExpression(program, model, scope, line, items);
                break;

            // A record initializer gives the type's members their values, one a line.
            case Place.Record:
                if (line.RecordType is { } path && Walk(program, model, scope, path, fromRoot: false) is { } found)
                    AddMembers(program, model, found, modulesToo: false, items);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// A macro is written where a statement goes with the <c>!(</c> that calls it, so that the
    /// arguments it takes are what is offered next. Only a macro is listed as a snippet.
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
        ProgramModel program, SemanticModel model, Scope scope, LineContext line, Cpu cpu,
        Dictionary<string, Suggestion> items)
    {
        var mnemonic = line.Before[line.Start].Text;
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
                AddExpression(program, model, scope, line, items);
            return;
        }
        if (written[^1].Kind == SyntaxKind.Comma)
        {
            Indexing(modes, opened, depth, value, items);

            // `bbr0 flags, @skip` is the one operand whose comma is followed by a target.
            if (modes.Contains(AddressingMode.DirectRelative))
                AddExpression(program, model, scope, line, items);
            return;
        }
        if (!Ends(written[^1].Kind))
            AddExpression(program, model, scope, line, items);
    }

    /// <summary>Every form an instruction has, written as the mark that begins it.</summary>
    private static void Forms(
        IReadOnlySet<AddressingMode> modes, Cpu cpu, string mnemonic,
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
    /// The forms an instruction has here: the CPU's, or a near target for the long branches,
    /// which nt65 writes on every CPU.
    /// </summary>
    private static IReadOnlySet<AddressingMode> ModesOf(Cpu cpu, string mnemonic) =>
        SyntaxFacts.LongBranches.Contains(mnemonic)
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
    /// The words that follow a mark in a declaration: how wide a name is after a <c>:</c>,
    /// what a declaration or a member holds, and what a segment says after a <c>,</c>.
    /// </summary>
    private static (IEnumerable<string> Words, string Detail)? AfterMark(LineContext line, string? directive)
    {
        if (directive == ".segment" && line.Before is [.., (SyntaxKind.Comma, _, _)])
            return (SegmentAttributes, "where the segment lands");
        if (line.Before is not [.., (SyntaxKind.Colon, _, _)])
            return null;
        return directive switch
        {
            ".macro" when line.OpenCall() is not null => (ParameterKinds, "what the argument may be"),
            ".import" => ([.. Directives.Sizes, "proc("], "how the name is reached"),
            ".export" or ".segment" => (Directives.Sizes, "address size"),
            ".data" => (Directives.Data, "what it holds"),
            null when line.Place == Place.TypeMembers => ([.. Directives.Elements, ".res"], "what it holds"),
            _ => null,
        };
    }

    /// <summary>
    /// Where a path leads: a symbol, or a module path. A path in code starts where a name does, in
    /// scope, among what <c>.use</c> brought in, or at the modules' root; one in a <c>.use</c>
    /// starts at the root.
    /// </summary>
    private static (Symbol? Symbol, string? Module)? Walk(
        ProgramModel program, SemanticModel model, Scope scope, IReadOnlyList<string> path, bool fromRoot)
    {
        var symbols = program.Symbols;
        (Symbol? Symbol, string? Module)? at = null;
        foreach (var part in path)
        {
            at = at switch
            {
                null when fromRoot => symbols.IsModulePath(part) ? (null, part) : null,
                null => scope.Lookup(part) is { } local ? (local, null)
                    : model.Brought.TryGetValue(part, out var brought) ? (brought.Symbol, brought.Module)
                    : model.Globs.Select(module => symbols.Member(module, part)).FirstOrDefault(found => found is { IsExported: true }) is { } globbed ? (globbed, null)
                    : symbols.IsModulePath(part) ? (null, part)
                    : symbols.Define(part) is { } define ? (define, null)
                    : null,
                { Module: { } prefix } when symbols.IsModulePath($"{prefix}::{part}") => (null, $"{prefix}::{part}"),
                { Module: { } prefix } => symbols.ModuleNamed(prefix) is { } module && symbols.Member(module, part) is { } member
                    ? (member, null)
                    : null,
                { Symbol: { } outer } => BodyOf(outer)?.FindMember(part) is { } inner ? (inner, null) : null,
                _ => null,
            };
            if (at is null)
                return null;
        }
        return at;
    }

    /// <summary>What a name leads into with <c>::</c>: its own body, or its type's.</summary>
    private static Scope? BodyOf(Symbol symbol) => symbol.Body ?? symbol.Type?.Body;

    /// <summary>The names after <c>::</c> where a path leads, and the modules below it when it is a module path.</summary>
    private static void AddMembers(
        ProgramModel program, SemanticModel model, (Symbol? Symbol, string? Module) at, bool modulesToo,
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
                    items.TryAdd(reexport.Name, new Suggestion(Protocol.CompletionItemKind.Reference, $"from `{string.Join("::", reexport.Path)}`", reexport.Name));
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
            items.TryAdd(next, new Suggestion(Protocol.CompletionItemKind.Module, next == rest ? "module" : "modules", next));
        }
    }

    /// <summary>
    /// What an expression may be written from: the marks a number that is not plain digits
    /// starts with, the names in scope, the modules a path may walk into, and the built-in
    /// functions, the last three of which only a macro body has.
    /// </summary>
    private static void AddExpression(
        ProgramModel program, SemanticModel model, Scope scope, LineContext line,
        Dictionary<string, Suggestion> items)
    {
        AddWord("$", "a hexadecimal number", items);
        AddWord("%", "a binary number", items);
        AddWord("'", "a character", items);
        AddInScope(program, model, scope, items, symbol => symbol.Kind is not (SymbolKind.Macro or SymbolKind.SignatureSet));
        AddModules(program, "", items);
        var builtins = line.InMacro
            ? SyntaxFacts.BuiltinFunctions.Concat(SyntaxFacts.MacroBuiltinFunctions)
            : SyntaxFacts.BuiltinFunctions;
        foreach (var builtin in builtins)
            items.TryAdd(builtin, new Suggestion(Protocol.CompletionItemKind.Function, "built-in function", builtin + "("));
    }

    /// <summary>
    /// Every name <paramref name="scope"/> can write alone that <paramref name="wanted"/> accepts:
    /// what the scopes out to the file declare, the nearest first, what <c>.use</c> brought in, and
    /// the defines.
    /// </summary>
    private static void AddInScope(
        ProgramModel program, SemanticModel model, Scope scope,
        Dictionary<string, Suggestion> items, Func<Symbol, bool> wanted)
    {
        for (var around = scope; around is not null; around = around.Parent)
        {
            foreach (var symbol in around.Symbols.Where(wanted))
                Add(symbol, items);
        }
        foreach (var (name, brought) in model.Brought)
        {
            if (brought.Symbol is { } symbol && wanted(symbol))
                items.TryAdd(name, new Suggestion(KindOf(symbol), Detail(symbol), name, DocComments.Of(symbol)));
            else if (brought.Module is { } module)
                items.TryAdd(name, new Suggestion(Protocol.CompletionItemKind.Module, $"module `{module}`", name));
        }
        foreach (var module in model.Globs)
        {
            foreach (var symbol in module.FileScope.Symbols.Where(symbol => symbol.IsExported && wanted(symbol)))
                Add(symbol, items);
        }
        foreach (var define in program.Symbols.Defines.Where(wanted))
            Add(define, items);
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
    /// A word, listed under itself and written with whatever it needs after it: a mark a word
    /// only leads up to is not part of the name the client filters on.
    /// </summary>
    private static void AddWord(
        string word, string detail,
        Dictionary<string, Suggestion> items)
    {
        var label = word.TrimEnd(' ', '=', '(');
        items.TryAdd(label.Length > 0 ? label : word, new Suggestion(Protocol.CompletionItemKind.Keyword, detail, word));
    }

    private static void Add(Symbol symbol, Dictionary<string, Suggestion> items) =>
        items.TryAdd(symbol.DisplayName,
            new Suggestion(KindOf(symbol), Detail(symbol), symbol.DisplayName, DocComments.Of(symbol)));

    private static string Detail(Symbol symbol) =>
        symbol.IsDefine ? "define" : symbol.Value.IsKnown && !symbol.IsAddress ? $"{symbol.KindText} = {symbol.Value}" : symbol.KindText;

    /// <summary>
    /// Whether what an item writes leaves the caret where something else goes, so the client is
    /// asked for that list as soon as it has written it.
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
