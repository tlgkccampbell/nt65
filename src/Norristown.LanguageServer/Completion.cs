using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What could be written at the caret: the names a path leads to after <c>::</c> and in a
/// <c>.use</c>, the names in scope where an operand or an expression goes, the items of a
/// processor-state signature, and a macro's parameters as named arguments.
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
    private static readonly string[] RoutineItems = ["near", "far", "inline", "args", "interrupt", "none"];

    /// <summary>What an <c>.ensure</c> makes hold.</summary>
    private static readonly string[] Widths = ["a8", "a16", "i8", "i16"];

    /// <summary>The directives that declare the name written after them, where nothing is completed.</summary>
    private static readonly HashSet<string> Declaring = new(StringComparer.Ordinal)
    {
        ".module", ".proc", ".scope", ".data", ".enum", ".struct", ".union", ".macro", ".func", ".list",
        ".charmap", ".signature", ".segment", ".frame", ".config", ".repeat", ".each", ".import",
    };

    /// <summary>What could be written at <paramref name="position"/> in the file <paramref name="model"/> is of.</summary>
    public static IReadOnlyList<Protocol.CompletionItem> At(ProgramModel program, SemanticModel model, int position)
    {
        var line = LineContext.At(model.Tree, position);
        var items = new Dictionary<string, (Protocol.CompletionItemKind Kind, string? Detail, string Text)>(StringComparer.Ordinal);
        Collect(program, model, line, items);
        var range = Lsp.ToRange(model.Tree, line.Replaced);
        return [.. items
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new Protocol.CompletionItem(item.Key, item.Value.Kind, item.Value.Detail,
                new Protocol.TextEdit(range, item.Value.Text)))];
    }

    private static void Collect(
        ProgramModel program, SemanticModel model, LineContext line,
        Dictionary<string, (Protocol.CompletionItemKind Kind, string? Detail, string Text)> items)
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

        if (directive == ".ensure")
        {
            AddWords(Widths, "width", items);
            return;
        }
        if (directive == ".state")
        {
            if (!AfterValuedItem(before))
            {
                AddWords(PointItems, "processor state", items);
                AddWords(ValuedItems, "processor state", items);
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
                    AddWords(RoutineItems, "processor state", items);
                AddInScope(program, model, scope, items, symbol => symbol.Kind == SymbolKind.SignatureSet);
                return;
            }
        }
        else if (directive is { } declaring && Declaring.Contains(declaring) && before.Count == line.Start + 1)
        {
            return;
        }

        // A statement's first word is an instruction or a macro call.
        if (before.Count == line.Start)
        {
            foreach (var mnemonic in SyntaxFacts.Mnemonics)
                items.TryAdd(mnemonic, (Protocol.CompletionItemKind.Text, "instruction", mnemonic));
            AddInScope(program, model, scope, items, symbol => symbol.Kind == SymbolKind.Macro);
            return;
        }

        // An argument of a macro call may name the parameter it is for.
        if (line.OpenCall() is { } call && call.Open >= 2 && before[call.Open - 1].Kind == SyntaxKind.Bang
            && before[^1].Kind is SyntaxKind.OpenParen or SyntaxKind.Comma
            && Callee(program, model, scope, line, call.Open - 1) is { Kind: SymbolKind.Macro } macro)
        {
            foreach (var parameter in macro.Parameters)
                items.TryAdd(parameter.Symbol.Name, (Protocol.CompletionItemKind.Property, $"parameter: {parameter.Symbol.KindText}", parameter.Symbol.Name + " = "));
        }

        AddInScope(program, model, scope, items, symbol => symbol.Kind is not (SymbolKind.Macro or SymbolKind.SignatureSet));
        AddModules(program, "", items);
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
                    : model.Brought.TryGetValue(part, out var brought) ? brought
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
        Dictionary<string, (Protocol.CompletionItemKind Kind, string? Detail, string Text)> items)
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
                    items.TryAdd(reexport.Name, (Protocol.CompletionItemKind.Reference, $"from `{string.Join("::", reexport.Path)}`", reexport.Name));
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
        Dictionary<string, (Protocol.CompletionItemKind Kind, string? Detail, string Text)> items)
    {
        foreach (var module in program.Symbols.Modules)
        {
            if (module.Name is not { } name || !name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length)
                continue;
            var rest = name[prefix.Length..];
            var next = rest.Split("::")[0];
            items.TryAdd(next, (Protocol.CompletionItemKind.Module, next == rest ? "module" : "modules", next));
        }
    }

    /// <summary>
    /// Every name <paramref name="scope"/> can write alone that <paramref name="wanted"/> accepts:
    /// what the scopes out to the file declare, the nearest first, what <c>.use</c> brought in, and
    /// the defines.
    /// </summary>
    private static void AddInScope(
        ProgramModel program, SemanticModel model, Scope scope,
        Dictionary<string, (Protocol.CompletionItemKind Kind, string? Detail, string Text)> items, Func<Symbol, bool> wanted)
    {
        for (var around = scope; around is not null; around = around.Parent)
        {
            foreach (var symbol in around.Symbols.Where(wanted))
                Add(symbol, items);
        }
        foreach (var (name, brought) in model.Brought)
        {
            if (brought.Symbol is { } symbol && wanted(symbol))
                items.TryAdd(name, (KindOf(symbol), Detail(symbol), name));
            else if (brought.Module is { } module)
                items.TryAdd(name, (Protocol.CompletionItemKind.Module, $"module `{module}`", name));
        }
        foreach (var module in model.Globs)
        {
            foreach (var symbol in module.FileScope.Symbols.Where(symbol => symbol.IsExported && wanted(symbol)))
                Add(symbol, items);
        }
        foreach (var define in program.Symbols.Defines.Where(wanted))
            Add(define, items);
    }

    private static void AddWords(
        IEnumerable<string> words, string detail,
        Dictionary<string, (Protocol.CompletionItemKind Kind, string? Detail, string Text)> items)
    {
        foreach (var word in words)
            items.TryAdd(word.TrimEnd(' ', '='), (Protocol.CompletionItemKind.Keyword, detail, word));
    }

    private static void Add(Symbol symbol, Dictionary<string, (Protocol.CompletionItemKind Kind, string? Detail, string Text)> items) =>
        items.TryAdd(symbol.DisplayName, (KindOf(symbol), Detail(symbol), symbol.DisplayName));

    private static string Detail(Symbol symbol) =>
        symbol.IsDefine ? "define" : symbol.Value.IsKnown && !symbol.IsAddress ? $"{symbol.KindText} = {symbol.Value}" : symbol.KindText;

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
