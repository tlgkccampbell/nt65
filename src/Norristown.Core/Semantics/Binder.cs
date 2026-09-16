using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Builds one file's scopes and declarations (§6.1, §6.2) and resolves the names it uses.
/// <para>
/// Declarations are collected first and resolved afterwards, so a name may be used before
/// the line that declares it. Blocks belonging to a later stage — macros, <c>.if</c>,
/// <c>.repeat</c>, <c>.each</c> and the type and text blocks of §6.3, §6.4 and §8 — are not
/// bound at all: their contents mean something this stage does not implement (a macro's
/// names are the expansion's, and §10 lets the same name be declared under two <c>.if</c>
/// branches), and half a rule would be worse than none.
/// </para>
/// </summary>
internal sealed class Binder
{
    private readonly SyntaxTree tree;
    private readonly SegmentTable segments;
    private readonly List<Diagnostic> diagnostics = [];
    private readonly List<Symbol> symbols = [];
    private readonly List<SymbolReference> references = [];
    private readonly List<Use> uses = [];
    private readonly Scope fileScope;
    private Scope scope;
    private string segment = SegmentTable.DefaultSegment;

    private Binder(SyntaxTree tree, SegmentTable segments)
    {
        this.tree = tree;
        this.segments = segments;
        fileScope = new Scope(ScopeKind.File, null, null, null);
        scope = fileScope;
    }

    /// <summary>Binds <paramref name="tree"/> against the program's <paramref name="segments"/>.</summary>
    public static Result Bind(SyntaxTree tree, SegmentTable segments)
    {
        var binder = new Binder(tree, segments);
        binder.WalkContainer(tree.Root);
        binder.ResolveUses();
        return new Result(binder.fileScope, binder.symbols, binder.references, binder.diagnostics);
    }

    /// <summary>Whether a block's contents belong to a layer this stage does not bind.</summary>
    private static bool IsDeferred(BlockKind kind) => kind is BlockKind.Macro or BlockKind.MacroBlock
        or BlockKind.If or BlockKind.Repeat or BlockKind.Each or BlockKind.Enum or BlockKind.Struct
        or BlockKind.Union or BlockKind.Charmap or BlockKind.List or BlockKind.TagInitializer;

    /// <summary>The first token of a statement that could be a declared name.</summary>
    private static SyntaxToken? NameToken(SyntaxNode statement)
    {
        foreach (var token in statement.ChildTokens)
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic)
            {
                return token;
            }
        }
        return null;
    }

    private static SyntaxToken? FirstToken(SyntaxNode statement, SyntaxKind kind)
    {
        foreach (var token in statement.ChildTokens)
        {
            if (token.Kind == kind)
                return token;
        }
        return null;
    }

    private void WalkContainer(SyntaxNode container)
    {
        foreach (var child in container.ChildNodes)
        {
            if (child.Green is GreenBlock block)
                WalkBlock(child, block.BlockKind);
            else
                WalkLine(child);
        }
    }

    /// <summary>
    /// A block: what its opener declares, and its contents in whatever scope and segment the
    /// opener puts them. A segment block changes the segment of its contents, not their
    /// scope (§5.2).
    /// </summary>
    private void WalkBlock(SyntaxNode block, BlockKind kind)
    {
        if (IsDeferred(kind))
            return;

        var lines = block.ChildNodes;
        var opener = lines.Length > 0 ? lines[0].Statement : null;
        var outerScope = scope;
        var outerSegment = segment;

        switch (kind)
        {
            case BlockKind.Proc:
                scope = OpenScope(ScopeKind.Proc, opener, SymbolKind.Proc);
                break;
            case BlockKind.Scope:
                scope = OpenScope(ScopeKind.Scope, opener, SymbolKind.Scope);
                break;
            case BlockKind.Segment:
                segment = SegmentOf(opener) ?? segment;
                break;
            default:
                if (opener is not null)
                    BindStatement(opener);
                break;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Green is GreenBlock inner)
                WalkBlock(lines[i], inner.BlockKind);
            else
                WalkLine(lines[i]);
        }

        scope = outerScope;
        segment = outerSegment;
    }

    /// <summary>
    /// The scope a <c>.proc</c> or <c>.scope</c> opens. A block whose opener is broken — a
    /// missing name, or a <c>.proc</c> written after a label — still opens a scope, so the
    /// cheap locals inside it have an owner and one bad line stays one bad line.
    /// </summary>
    private Scope OpenScope(ScopeKind kind, SyntaxNode? opener, SymbolKind symbolKind)
    {
        var expected = kind == ScopeKind.Proc ? SyntaxKind.ProcDeclaration : SyntaxKind.ScopeDeclaration;
        if (opener is null || opener.Kind != expected)
        {
            if (opener is not null)
                BindStatement(opener);
            return new Scope(kind, null, scope, null);
        }

        // `.scope { }` is anonymous, and declares nothing.
        if (NameToken(opener) is not { } name)
            return new Scope(kind, null, scope, null);

        var symbol = Declare(name, symbolKind);
        var body = new Scope(kind, symbol?.Name ?? name.Text, scope, symbol);
        if (symbol is not null)
            symbol.Body = body;
        return body;
    }

    /// <summary>The segment a block puts its contents in, or null when its opener does not say.</summary>
    private string? SegmentOf(SyntaxNode? opener)
    {
        if (opener is null || opener.Kind != SyntaxKind.SegmentBlock)
            return null;

        if (opener.ChildTokens.Length > 0 && SegmentNames.Shortcut(opener.ChildTokens[0].Text) is { } standard)
            return standard;
        if (FirstToken(opener, SyntaxKind.StringLiteral) is not { } quoted)
            return null;

        // A block that names a segment declared nowhere is an error, so a misspelled name is
        // caught before ld65 runs (§5.2). Its contents still go there, which keeps the
        // mistake to one diagnostic.
        var name = SegmentNames.Unquote(quoted.Text);
        if (segments.Find(name) is null)
            Report(quoted.Span, $"segment \"{name}\" is not declared");
        return name;
    }

    private void WalkLine(SyntaxNode line)
    {
        if (line.Statement is { } statement)
            BindStatement(statement);
    }

    private void BindStatement(SyntaxNode statement)
    {
        switch (statement.Kind)
        {
            case SyntaxKind.LabeledLine:
                foreach (var child in statement.ChildNodes)
                {
                    if (child.Kind != SyntaxKind.Label)
                        CollectUses(child);
                    else if (child.ChildTokens.Length > 0)
                        Declare(child.ChildTokens[0], SymbolKind.Label);
                }
                break;

            case SyntaxKind.ConstantDeclaration:
                // A constant or an address alias: which one depends on the expression, so the
                // kind is settled once the names in it resolve.
                var value = statement.ChildNodes.FirstOrDefault();
                if (NameToken(statement) is { } constant)
                    Declare(constant, SymbolKind.Constant, value);
                CollectUses(value);
                break;

            case SyntaxKind.ExternProcDeclaration:
                var address = statement.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.ProcSignature);
                if (NameToken(statement) is { } routine)
                    Declare(routine, SymbolKind.ExternProc, address);
                CollectUses(address);
                break;

            case SyntaxKind.ExportDirective:
                // The names are written as bare tokens rather than as expressions, so they
                // are collected here rather than by looking for name expressions.
                // A cheap local can neither be reached with `::` nor exported (§6.2), and
                // the parser has already refused one here.
                foreach (var token in statement.ChildTokens)
                {
                    if (token.Kind == SyntaxKind.Identifier)
                        uses.Add(new Use(token, scope, Path: false, First: true));
                }
                break;

            case SyntaxKind.ImportDirective:
                foreach (var item in statement.ChildNodes)
                    BindImportItem(item);
                break;

            case SyntaxKind.InstructionStatement:
            case SyntaxKind.DataDirective:
                CollectUses(statement);
                break;

            // Everything else either declares nothing and names nothing — `.cpu`, a segment
            // declaration, a blank or closing line — or belongs to a later stage.
            default:
                break;
        }
    }

    /// <summary><c>name</c>, <c>name: size</c>, <c>name: proc(...)</c> or a checked <c>name = expr</c> (§12).</summary>
    private void BindImportItem(SyntaxNode item)
    {
        if (item.Kind != SyntaxKind.ImportItem || NameToken(item) is not { } name)
            return;

        var checkedValue = item.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.ImportSignature);
        var kind = checkedValue is null ? SymbolKind.ImportedAddress : SymbolKind.ImportedConstant;
        if (Declare(name, kind, checkedValue) is { } symbol && kind == SymbolKind.ImportedAddress)
        {
            // An import states its own address size. An unqualified import and a routine are
            // both absolute (§12); a routine declared `far` is Stage 11's to read.
            symbol.AddressSize = AddressSize.Absolute;
            foreach (var token in item.ChildTokens)
            {
                if (token.Kind == SyntaxKind.Identifier && SegmentNames.ParseSize(token.Text) is { } size)
                    symbol.AddressSize = size;
            }
        }
        CollectUses(checkedValue);
    }

    /// <summary>Records every name written inside <paramref name="node"/>, to resolve once the file is read.</summary>
    private void CollectUses(SyntaxNode? node)
    {
        if (node is null)
            return;
        if (node.Kind == SyntaxKind.NameExpression)
        {
            // A leading `::` starts the path at file scope, which the first name sees by
            // already being part of a path.
            var path = false;
            var first = true;
            foreach (var token in node.ChildTokens)
            {
                if (token.Kind == SyntaxKind.ColonColon)
                {
                    path = true;
                }
                else if (token.Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                    or SyntaxKind.Register or SyntaxKind.Mnemonic)
                {
                    uses.Add(new Use(token, scope, path, first));
                    path = true;
                    first = false;
                }
            }
            return;
        }
        foreach (var child in node.ChildNodes)
            CollectUses(child);
    }

    private Symbol? Declare(SyntaxToken name, SymbolKind kind, SyntaxNode? value = null)
    {
        if (!CheckReservedWord(name))
            return null;

        var cheap = name.Kind == SyntaxKind.CheapLocal;
        var owner = cheap ? CheapLocalOwner(name) : scope;
        var symbol = new Symbol(cheap ? name.Text[1..] : name.Text, kind, owner, tree, name.Span)
        {
            IsCheapLocal = cheap,
            ValueExpression = value,
            Segment = segment,
        };

        if (owner.Declare(symbol) is { } existing)
        {
            Report(name.Span, $"`{symbol.DisplayName}` is already declared in this scope",
                new RelatedSpan(existing.DeclarationSpan, "declared here"));
        }
        symbols.Add(symbol);
        references.Add(new SymbolReference(symbol, name.Span, true));
        return symbol;
    }

    /// <summary>
    /// The <c>.proc</c> or <c>.scope</c> a cheap local belongs to (§6.2). One written outside
    /// any of them is an error, and is kept at file scope so that uses of it still resolve.
    /// </summary>
    private Scope CheapLocalOwner(SyntaxToken name)
    {
        for (var owner = scope; owner is not null; owner = owner.Parent)
        {
            if (owner.Kind != ScopeKind.File)
                return owner;
        }
        Report(name.Span, $"`{name.Text}` is a cheap local, which needs an enclosing `.proc` or `.scope`");
        return fileScope;
    }

    /// <summary>
    /// The reserved words of §4: a symbol may not be named after a mnemonic or a register.
    /// Members of a named struct, union or enum are exempt, and arrive with Stage 7.
    /// </summary>
    private bool CheckReservedWord(SyntaxToken name)
    {
        var what = name.Kind switch
        {
            SyntaxKind.Mnemonic => "a mnemonic",
            SyntaxKind.Register => "a register name",
            _ => null,
        };
        if (what is null)
            return true;
        Report(name.Span, $"`{name.Text}` is {what} and cannot be used as a name");
        return false;
    }

    private void ResolveUses()
    {
        Symbol? previous = null;
        var broken = false;
        foreach (var (token, at, path, first) in uses)
        {
            if (first)
            {
                previous = null;
                broken = false;
            }
            else if (broken)
            {
                // The part before this one did not resolve, and has been reported. What the
                // rest of the path would mean is unanswerable, not wrong.
                continue;
            }

            previous = Resolve(token, at, path, previous);
            if (previous is null)
                broken = true;
            else
                references.Add(new SymbolReference(previous, token.Span, false));
        }
        references.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
    }

    /// <summary>
    /// What one part of a written name means. <paramref name="previous"/> is what the part
    /// before it resolved to, so a path walks into a scope instead of looking outward again.
    /// </summary>
    private Symbol? Resolve(SyntaxToken token, Scope at, bool path, Symbol? previous)
    {
        if (token.Kind == SyntaxKind.CheapLocal)
        {
            if (path)
            {
                Report(token.Span, $"`{token.Text}` is a cheap local and cannot be reached with `::`");
                return null;
            }
            var local = at.LookupCheapLocal(token.Text[1..]);
            if (local is null)
                Report(token.Span, $"`{token.Text}` is not declared");
            return local;
        }

        if (!path)
        {
            var symbol = at.Lookup(token.Text);
            if (symbol is null)
                Report(token.Span, $"`{token.Text}` is not declared");
            return symbol;
        }

        // A part after `::`: the scope to look in is the one the part before it opened, and a
        // leading `::` starts at the file's own top level.
        var container = previous is null ? fileScope : previous.Body;
        if (container is null)
        {
            Report(token.Span, $"`{previous!.DisplayName}` is a {previous.KindText}, not a scope");
            return null;
        }

        var member = container.FindMember(token.Text);
        if (member is null)
        {
            Report(token.Span, container.Kind == ScopeKind.File
                ? $"`{token.Text}` is not declared at file scope"
                : $"`{token.Text}` is not declared in `{container.Name}`");
        }
        return member;
    }

    private void Report(TextSpan span, string message, params RelatedSpan[] related) =>
        diagnostics.Add(new Diagnostic(tree.GetSpan(span), Severity.Error, message, related));

    /// <summary>What binding one file produced.</summary>
    /// <param name="FileScope">The file's top-level scope.</param>
    /// <param name="Symbols">Every symbol declared in the file, in source order.</param>
    /// <param name="References">Declarations and uses, ordered by position.</param>
    /// <param name="Diagnostics">What binding found wrong.</param>
    public sealed record Result(
        Scope FileScope,
        IReadOnlyList<Symbol> Symbols,
        IReadOnlyList<SymbolReference> References,
        List<Diagnostic> Diagnostics);

    /// <summary>One written name, waiting for the whole file to be read before it is resolved.</summary>
    /// <param name="Token">The name.</param>
    /// <param name="Scope">The scope it was written in.</param>
    /// <param name="Path">Whether a <c>::</c> comes before it, so it names a member of a scope.</param>
    /// <param name="First">Whether it is the first part of the name it belongs to.</param>
    private readonly record struct Use(SyntaxToken Token, Scope Scope, bool Path, bool First);
}
