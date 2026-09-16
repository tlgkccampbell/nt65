using System.Collections.Immutable;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Builds one file's scopes and declarations and resolves the names it uses.
/// <para>
/// Declarations are collected first and resolved afterwards, so a name may be used before
/// the line that declares it.
/// </para>
/// <para>
/// A macro body is read once, wherever many times it is expanded. It sees the scope the
/// macro is declared in, and what it declares belongs to a scope of its own that nothing
/// outside can reach, so an expansion never declares a name in its caller. The block
/// argument of a call is the other way round: it is the caller's own code, and its names
/// resolve there.
/// </para>
/// <para>
/// An <c>.if</c> is neither: its conditions were answered before any of this ran, so a
/// branch the build takes is read as if the <c>.if</c> were not written and one it leaves
/// out is not read at all. That is what lets the same name be declared under two of them.
/// </para>
/// <para>
/// A <c>.repeat</c> or an <c>.each</c> body is read once, however many times it is written
/// out, with the name the repetition binds in a scope of its own. A name declared inside one
/// would have to be a different name on every turn, which is macro expansion's to give.
/// </para>
/// </summary>
internal sealed class Binder
{
    private readonly SyntaxTree tree;
    private readonly SegmentTable segments;
    private readonly Configuration configuration;
    private readonly List<Diagnostic> diagnostics = [];
    private readonly List<Symbol> symbols = [];
    private readonly List<SymbolReference> references = [];
    private readonly List<Use> uses = [];
    private readonly List<Use> exports = [];
    private readonly List<Invocation> calls = [];
    private readonly List<Symbol> called = [];
    private readonly HashSet<Symbol> resolving = [];
    private readonly Scope fileScope;
    private ProgramSymbols program = ProgramSymbols.Empty;
    private Scope scope;
    private string segment = SegmentTable.DefaultSegment;
    private Symbol? previousEnumMember;
    private SyntaxToken? repetition;

    private Binder(SyntaxTree tree, SegmentTable segments, Configuration configuration)
    {
        this.tree = tree;
        this.segments = segments;
        this.configuration = configuration;
        fileScope = new Scope(ScopeKind.File, null, null, null);
        scope = fileScope;
    }

    /// <summary>The file being bound.</summary>
    public SyntaxTree Tree => tree;

    /// <summary>The file's top-level scope, which is what another file can reach into.</summary>
    public Scope FileScope => fileScope;

    /// <summary>Binds <paramref name="tree"/> on its own, seeing no other file.</summary>
    public static Result Bind(SyntaxTree tree, SegmentTable segments) =>
        Collect(tree, segments, Configuration.Everything).Resolve(ProgramSymbols.Empty);

    /// <summary>
    /// Reads the declarations of <paramref name="tree"/>, leaving the names it uses to be
    /// resolved once every file of the program has been read.
    /// </summary>
    public static Binder Collect(SyntaxTree tree, SegmentTable segments, Configuration configuration)
    {
        var binder = new Binder(tree, segments, configuration);
        binder.WalkContainer(tree.Root);
        return binder;
    }

    /// <summary>
    /// What this file's <c>.export</c> items name. Nothing is reported from here:
    /// this answers what the program may see, before the program is known, and
    /// <see cref="Resolve(ProgramSymbols)"/> reports on the same names afterwards.
    /// </summary>
    public IReadOnlyList<Symbol> Exported() =>
        [.. exports.Select(export => export.Scope.Lookup(export.Token.Text)).OfType<Symbol>()];

    /// <summary>Resolves the names the file uses, with <paramref name="program"/> for the ones it does not declare.</summary>
    public Result Resolve(ProgramSymbols program)
    {
        this.program = program;
        ResolveUses(uses);

        // A call's arguments are resolved after everything else, because whether a name in
        // one is a name at all depends on the parameter it binds to: a `one` argument is a
        // word, and words are never looked up.
        ResolveCalls();
        references.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
        return new Result(fileScope, symbols, references, diagnostics);
    }

    /// <summary>The macros this file declares, which is what the recursion check reads.</summary>
    public IEnumerable<Symbol> DeclaredMacros() => symbols.Where(symbol => symbol.Kind == SymbolKind.Macro);

    /// <summary>
    /// The macros this file calls outright, rather than from inside another macro's body.
    /// What their bodies use is what the file's own output has to bring in.
    /// </summary>
    public IReadOnlyList<Symbol> CalledMacros() => called;

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
    /// scope.
    /// </summary>
    private void WalkBlock(SyntaxNode block, BlockKind kind)
    {
        var lines = block.ChildNodes;
        var opener = lines.Length > 0 ? lines[0].Statement : null;
        if (opener is not null)
            CheckAllowedHere(opener);
        var outerScope = scope;
        var outerSegment = segment;

        switch (kind)
        {
            case BlockKind.Macro:
                scope = OpenMacro(opener);
                if (scope.Owner is { Kind: SymbolKind.Macro } macro)
                    macro.Definition = block;
                break;

            // A block argument is written at the call and belongs to it: its names resolve
            // in the caller, and the cheap locals it declares are private to it, because the
            // same block may be spliced in more than one place.
            case BlockKind.MacroBlock:
                if (opener is { Kind: SyntaxKind.MacroCall or SyntaxKind.LabeledLine })
                    BindStatement(opener);
                else if (opener is { Kind: SyntaxKind.BlockContinuation })
                    CheckContinuation(block, opener);
                scope = new Scope(ScopeKind.BlockArgument, null, scope, null);
                break;

            case BlockKind.Proc:
                scope = OpenScope(ScopeKind.Proc, opener, SymbolKind.Proc);
                break;
            case BlockKind.Scope:
                scope = OpenScope(ScopeKind.Scope, opener, SymbolKind.Scope);
                break;
            case BlockKind.Segment:
                segment = SegmentOf(opener) ?? segment;
                break;
            case BlockKind.Enum:
                scope = OpenType(opener, SymbolKind.Enum);
                previousEnumMember = null;
                break;
            case BlockKind.Struct:
                scope = OpenType(opener, SymbolKind.Struct);
                break;
            case BlockKind.Union:
                scope = OpenType(opener, SymbolKind.Union);
                break;
            case BlockKind.Charmap:
                DeclareCollected(opener, SymbolKind.Charmap, lines);
                return;
            case BlockKind.List:
                DeclareCollected(opener, SymbolKind.List, lines);
                return;
            case BlockKind.TagInitializer:
                BindInitializer(opener, lines);
                return;
            case BlockKind.If:
                // Whatever an included branch declares belongs to the scope around it. The
                // condition is read for its names so that an editor can follow a define to
                // the configuration that gives it a value.
                if (!configuration.Includes(block))
                    return;
                // A condition may compare a `one` parameter or a repetition binding with a
                // bare word, which is never looked up, so a name here that turns
                // out to be no name is a word rather than a mistake.
                if (opener is not null)
                    CollectUses(opener, uses, words: true);
                break;
            case BlockKind.Repeat:
            case BlockKind.Each:
                scope = OpenRepetition(opener);
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
        if (kind == BlockKind.Enum)
            previousEnumMember = null;
        if (Constructs.Repeats(kind))
            repetition = null;
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
        if (symbol is not null && kind == ScopeKind.Proc)
            symbol.Signature = ReadSignature(opener);
        var body = new Scope(kind, symbol?.Name ?? name.Text, scope, symbol);
        if (symbol is not null)
            symbol.Body = body;
        return body;
    }

    /// <summary>The signature a proc or an extern proc writes after its name, or the default.</summary>
    private Signature ReadSignature(SyntaxNode declaration) =>
        Signature.Read(declaration.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ProcSignature),
            (span, message) => Report(span, message));

    /// <summary>
    /// The scope an <c>.enum</c>, <c>.struct</c> or <c>.union</c> opens. An anonymous one
    /// opens nothing: its members are declared where it is written, which is how an
    /// anonymous enum names constants and an anonymous struct groups fields.
    /// </summary>
    private Scope OpenType(SyntaxNode? opener, SymbolKind kind)
    {
        if (opener is null || NameToken(opener) is not { } name)
            return scope;
        var symbol = Declare(name, kind);
        var body = new Scope(ScopeKind.Type, symbol?.Name ?? name.Text, scope, symbol);
        if (symbol is not null)
            symbol.Body = body;
        return body;
    }

    /// <summary>
    /// The scope a <c>.repeat</c> or an <c>.each</c> opens, which holds the one name it binds
    /// and nothing else. The body is read in it once: what the name is worth differs from
    /// turn to turn, but what it refers to does not, so one reading answers for every turn.
    /// </summary>
    private Scope OpenRepetition(SyntaxNode? opener)
    {
        var body = new Scope(ScopeKind.Scope, null, scope, null);
        if (opener is null)
            return body;

        CollectUses(opener);
        if (NameToken(opener) is not { } name)
        {
            repetition = FirstToken(opener, SyntaxKind.Directive);
            return body;
        }

        var outer = scope;
        scope = body;
        Declare(name, SymbolKind.Binding);
        scope = outer;
        repetition = FirstToken(opener, SyntaxKind.Directive);
        return body;
    }

    /// <summary>
    /// The scope a <c>.macro</c> opens: its parameters, and everything its body declares. The
    /// scope carries the macro's name, so a label in the body is named after it in the
    /// output, but nothing outside can reach into it, which is what makes each expansion's
    /// locals its own.
    /// </summary>
    private Scope OpenMacro(SyntaxNode? opener)
    {
        if (opener is not { Kind: SyntaxKind.MacroDeclaration })
        {
            if (opener is not null)
                BindStatement(opener);
            return new Scope(ScopeKind.Macro, null, scope, null);
        }

        CheckMacroPlacement(opener);
        var written = NameToken(opener);
        var symbol = written is { } name ? Declare(name, SymbolKind.Macro) : null;
        var body = new Scope(ScopeKind.Macro, symbol?.Name ?? written?.Text, scope, symbol);
        if (symbol is not null)
            symbol.Body = body;

        // A default is written in the header, so it resolves where the macro is declared
        // rather than in the body it is used in.
        var declarations = Macros.ParametersOf(opener);
        foreach (var parameter in declarations)
            CollectUses(Macros.DefaultOf(parameter));

        var outer = scope;
        scope = body;
        var parameters = new List<MacroParameter>();
        foreach (var parameter in declarations)
        {
            if (Macros.NameOf(parameter) is not { } spelled
                || Declare(spelled, SymbolKind.MacroParameter) is not { } declared)
            {
                continue;
            }
            declared.Parameter = Macros.Describe(parameter, declared);
            parameters.Add(declared.Parameter);
        }
        scope = outer;
        if (symbol is not null)
            symbol.Parameters = parameters;
        CheckParameterOrder(declarations, parameters);
        return body;
    }

    /// <summary>
    /// <c>.macro</c> at file level or in a <c>.scope</c> outside any routine. One declared in
    /// a proc would see that proc's cheap locals, and an expansion in another proc would
    /// branch into them, out of sight of the first proc's flow analysis.
    /// </summary>
    private void CheckMacroPlacement(SyntaxNode opener)
    {
        for (var around = scope; around is { Kind: not ScopeKind.File }; around = around.Parent)
        {
            if (around.Kind is not (ScopeKind.Proc or ScopeKind.Macro))
                continue;
            Report(opener.ChildTokens[0].Span,
                around.Kind == ScopeKind.Proc
                    ? "a `.macro` belongs at file level or in a `.scope`, not inside a routine"
                    : "a `.macro` belongs at file level or in a `.scope`, not inside another macro");
            return;
        }
    }

    /// <summary>
    /// The order the parameters have to be written in: at most one <c>list</c>, which takes
    /// every remaining positional argument, and the blocks after it, which are written after
    /// the parentheses and so cannot be positional at all.
    /// </summary>
    private void CheckParameterOrder(IReadOnlyList<SyntaxNode> written, IReadOnlyList<MacroParameter> parameters)
    {
        MacroParameter? list = null;
        MacroParameter? block = null;
        for (var i = 0; i < parameters.Count && i < written.Count; i++)
        {
            var parameter = parameters[i];
            var at = written[i].Span;
            if (parameter.IsBlock)
            {
                block = parameter;
                continue;
            }
            if (block is not null)
            {
                Report(at, $"`{parameter.Name}` comes after the `block` parameter `{block.Name}`, "
                    + "and a block is written after the parentheses");
            }
            if (list is not null)
            {
                Report(at, $"`{parameter.Name}` comes after the `list` parameter `{list.Name}`, "
                    + "which takes every remaining argument");
            }
            if (parameter.Kind == ParameterKind.List)
                list = list is null ? parameter : list;
        }
    }

    /// <summary>
    /// <c>} name {</c>, which continues the block argument above it. Which parameter it
    /// names is the call's business; all that is left here is a continuation with no call
    /// above it at all, which the call never sees.
    /// </summary>
    private void CheckContinuation(SyntaxNode block, SyntaxNode opener)
    {
        if (block.Parent is { } container
            && container.ChildNodes.IndexOf(block) is > 0 and var at
            && container.ChildNodes[at - 1].Green is GreenBlock { BlockKind: BlockKind.MacroBlock })
        {
            return;
        }
        if (opener.ChildTokens.Length > 1)
            Report(opener.ChildTokens[1].Span, "this block continues no macro call");
    }

    /// <summary>
    /// What a macro body and a block argument may not hold. A body would declare in its
    /// caller or make something program-wide depend on how often it is called; a block
    /// argument is spliced wherever the body names it, so anything it declared would be
    /// declared once per splice.
    /// </summary>
    private void CheckAllowedHere(SyntaxNode statement)
    {
        if (!InMacroBody)
            return;
        if (Macros.Forbidden(statement) is not { } why)
            return;
        Report(statement.ChildTokens.Length > 0 ? statement.ChildTokens[0].Span : statement.Span, why);
    }

    /// <summary>
    /// An annotation stands between the statement it is about and whatever follows, so one
    /// with nothing above it is about nothing and is reported where it is written.
    /// </summary>
    private void CheckAnnotation(SyntaxNode line, SyntaxNode statement)
    {
        if (!Annotations.Is(statement) || statement.ChildTokens.Length == 0)
            return;
        if (Annotations.Misplaced(line, statement) is { } why)
            Report(statement.ChildTokens[0].Span, why);
    }

    /// <summary>Whether the walk is inside a macro body, however many scopes deep.</summary>
    private bool InMacroBody
    {
        get
        {
            for (var around = scope; around is not null; around = around.Parent)
            {
                if (around.Kind == ScopeKind.Macro)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Whether a declaration here would land in a block argument. A routine or a macro
    /// written inside one owns what it declares, so the walk stops at the first of those.
    /// </summary>
    private bool InABlockArgument
    {
        get
        {
            for (var around = scope; around is not null; around = around.Parent)
            {
                if (around.Kind == ScopeKind.BlockArgument)
                    return true;
                if (around.Kind is ScopeKind.Proc or ScopeKind.Macro or ScopeKind.Type)
                    return false;
            }
            return false;
        }
    }

    /// <summary>
    /// A <c>.charmap</c> or a <c>.list</c>, whose lines are entries rather than declarations:
    /// the block is one symbol holding them. A list's items name symbols, and those names
    /// belong to the scope the list is written in.
    /// </summary>
    private void DeclareCollected(SyntaxNode? opener, SymbolKind kind, ImmutableArray<SyntaxNode> lines)
    {
        var bodies = new List<SyntaxNode>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Statement is { Kind: SyntaxKind.CharmapEntry or SyntaxKind.ListItems } line)
                bodies.Add(line);
        }

        if (opener is null || NameToken(opener) is not { } name)
            return;
        if (kind == SymbolKind.List)
        {
            Declare(name, kind, value: null, items: [.. bodies.SelectMany(line => line.ChildNodes)]);
            foreach (var line in bodies)
                CollectUses(line);
            return;
        }
        Declare(name, kind, value: null, entries: bodies);
        foreach (var line in bodies)
            CollectUses(line);
    }

    /// <summary>
    /// An initialized instance. The label is an instance of the type; the member names its
    /// values give are checked against that type once it is known, so only the values
    /// themselves are names to resolve here.
    /// </summary>
    private void BindInitializer(SyntaxNode? opener, ImmutableArray<SyntaxNode> lines)
    {
        if (opener is not null)
            BindStatement(opener);
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Statement is { } line)
                CollectUses(line);
        }
    }

    /// <summary>The segment a block puts its contents in, or null when its opener does not say.</summary>
    private string? SegmentOf(SyntaxNode? opener)
    {
        if (opener is null || opener.Kind != SyntaxKind.SegmentBlock)
            return null;

        if (Constructs.SegmentOf(opener) is not { } name)
            return null;

        // A block that names a segment declared nowhere is an error, so a misspelled name is
        // caught before ld65 runs. Its contents still go there, which keeps the
        // mistake to one diagnostic.
        if (segments.Find(name) is null && FirstToken(opener, SyntaxKind.StringLiteral) is { } quoted)
            Report(quoted.Span, $"segment \"{name}\" is not declared");
        return name;
    }

    private void WalkLine(SyntaxNode line)
    {
        if (line.Statement is not { } statement)
            return;
        CheckAllowedHere(statement);
        CheckAnnotation(line, statement);
        BindStatement(statement);
    }

    private void BindStatement(SyntaxNode statement)
    {
        switch (statement.Kind)
        {
            case SyntaxKind.LabeledLine:
                BindLabeledLine(statement);
                break;

            case SyntaxKind.EnumMember:
                BindEnumMember(statement);
                break;

            case SyntaxKind.FuncDeclaration:
                BindFunc(statement);
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
                if (NameToken(statement) is { } routine
                    && Declare(routine, SymbolKind.ExternProc, address) is { } externProc)
                {
                    externProc.Signature = ReadSignature(statement);
                }
                CollectUses(address);
                break;

            case SyntaxKind.ExportDirective:
                // The names are written as bare tokens rather than as expressions, so they
                // are collected here rather than by looking for name expressions.
                // A cheap local can neither be reached with `::` nor exported, and
                // the parser has already refused one here.
                foreach (var token in statement.ChildTokens)
                {
                    if (token.Kind != SyntaxKind.Identifier)
                        continue;
                    var export = new Use(token, scope, Path: false, First: true, Last: true);
                    uses.Add(export);
                    exports.Add(export);
                }
                break;

            case SyntaxKind.ImportDirective:
                foreach (var item in statement.ChildNodes)
                    BindImportItem(item);
                break;

            case SyntaxKind.BlockSplice:
                BindSplice(statement);
                break;

            case SyntaxKind.MacroCall:
                BindCall(statement);
                break;

            case SyntaxKind.InstructionStatement:
            case SyntaxKind.DataDirective:
            case SyntaxKind.AssertDirective:

            // An annotation names labels and nothing else, so its names resolve as any
            // other use does; that they name labels rather than constants is the flow
            // analysis's business.
            case SyntaxKind.NextDirective:
            case SyntaxKind.PatchDirective:
                CollectUses(statement);
                break;

            // Everything else either declares nothing and names nothing — `.cpu`, a segment
            // declaration, a blank or closing line — or belongs to a later stage.
            default:
                break;
        }
    }

    /// <summary><c>name</c>, <c>name: size</c>, <c>name: proc(...)</c> or a checked <c>name = expr</c>.</summary>
    private void BindImportItem(SyntaxNode item)
    {
        if (item.Kind != SyntaxKind.ImportItem || NameToken(item) is not { } name)
            return;

        var checkedValue = item.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.ImportSignature);
        var kind = checkedValue is null ? SymbolKind.ImportedAddress : SymbolKind.ImportedConstant;
        if (Declare(name, kind, checkedValue) is { } symbol && kind == SymbolKind.ImportedAddress)
        {
            // An import states its own address size. An unqualified import is absolute, and so
            // is a routine, unless its signature says it is called far.
            symbol.AddressSize = AddressSize.Absolute;
            foreach (var token in item.ChildTokens)
            {
                if (token.Kind == SyntaxKind.Identifier && SegmentNames.ParseSize(token.Text) is { } size)
                    symbol.AddressSize = size;
            }
            if (item.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ImportSignature) is { } signature)
            {
                symbol.Signature = Signature.Read(signature, (span, message) => Report(span, message));
                if (symbol.Signature.IsFar)
                    symbol.AddressSize = AddressSize.Far;
            }
        }
        CollectUses(checkedValue);
    }

    /// <summary>
    /// A label and whatever follows it. Inside a type body the label is a member and the
    /// directive says how much room it takes; elsewhere it is a label, and one written on a
    /// <c>.tag</c> is an instance of that type.
    /// </summary>
    private void BindLabeledLine(SyntaxNode statement)
    {
        var label = statement.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.Label);
        var rest = statement.ChildNodes.FirstOrDefault(child => child.Kind != SyntaxKind.Label);
        if (label is { ChildTokens.Length: > 0 })
        {
            var kind = scope.Kind == ScopeKind.Type ? SymbolKind.Member
                : Constructs.IsTag(rest) ? SymbolKind.Instance
                : SymbolKind.Label;
            Declare(label.ChildTokens[0], kind, data: rest, type: Constructs.TagTypeOf(rest));
        }
        if (rest is { Kind: SyntaxKind.MacroCall })
            BindCall(rest);
        else
            CollectUses(rest);
    }

    /// <summary>
    /// One enum member. A member with no value of its own follows the one before it, so each
    /// keeps a link to its predecessor rather than a number nothing has worked out yet.
    /// </summary>
    private void BindEnumMember(SyntaxNode statement)
    {
        if (NameToken(statement) is not { } name)
            return;
        var value = statement.ChildNodes.FirstOrDefault();
        var member = Declare(name, SymbolKind.Constant, value, follows: value is null);
        if (member is not null)
        {
            member.PreviousMember = previousEnumMember;
            previousEnumMember = member;
        }
        CollectUses(value);
    }

    /// <summary>
    /// A function and the parameters its body names. The parameters live in a scope of their
    /// own, which nothing outside the body can reach, so the body reads as ordinary code and
    /// a call is the body with each parameter standing for its argument.
    /// </summary>
    private void BindFunc(SyntaxNode statement)
    {
        var body = statement.ChildNodes.LastOrDefault(child => child.Kind != SyntaxKind.ParameterList);
        var written = statement.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.ParameterList);
        if (NameToken(statement) is not { } name)
            return;

        var symbol = Declare(name, SymbolKind.Func, value: null, items: body is null ? [] : [body]);
        if (symbol is null)
            return;

        var inside = new Scope(ScopeKind.Type, null, scope, symbol);
        symbol.Body = inside;

        var outer = scope;
        scope = inside;
        var parameters = new List<Symbol>();
        foreach (var token in written?.ChildTokens ?? [])
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic
                && Declare(token, SymbolKind.Constant) is { } parameter)
            {
                parameters.Add(parameter);
            }
        }
        symbol.ParameterSymbols = parameters;
        CollectUses(body);
        scope = outer;
    }

    /// <summary>
    /// A name on its own, which splices the block argument bound to it. Only a macro body
    /// can hold one: everywhere else a name alone is the line the parser could not read.
    /// </summary>
    private void BindSplice(SyntaxNode statement)
    {
        if (statement.ChildTokens.Length == 0)
            return;
        var token = statement.ChildTokens[0];
        if (!InMacroBody)
        {
            Report(token.Span, "expected a label, a constant, an instruction or a directive");
            return;
        }
        uses.Add(new Use(token, scope, Path: false, First: true, Last: true, Splice: true));
    }

    /// <summary>
    /// A call, kept whole until the file is read. Nothing in it can be resolved yet: the
    /// macro it names may be declared further down or in another file, and what its
    /// arguments mean follows from the parameters they bind to.
    /// </summary>
    private void BindCall(SyntaxNode call) => calls.Add(new Invocation(call, scope, EnclosingMacro));

    /// <summary>The macro whose body the walk is inside, or null when it is in none.</summary>
    private Symbol? EnclosingMacro
    {
        get
        {
            for (var around = scope; around is not null; around = around.Parent)
            {
                if (around.Kind == ScopeKind.Macro)
                    return around.Owner;
            }
            return null;
        }
    }

    /// <summary>
    /// Matches each call to the macro it names and resolves the arguments that are names.
    /// A <c>one</c> argument is a word compared against the parameter's list and never
    /// looked up, so collecting it as a use would report a register or a mnemonic that is
    /// doing exactly what it is there for.
    /// </summary>
    private void ResolveCalls()
    {
        foreach (var (call, at, inside) in calls)
        {
            if (Macros.CalleeOf(call) is not { } callee)
                continue;
            if (Resolve(callee, at, path: false, previous: null, last: true) is not { } symbol)
                continue;
            references.Add(new SymbolReference(symbol, callee.Span, false));
            if (symbol.Kind != SymbolKind.Macro)
            {
                Report(callee.Span, $"`{callee.Text}` is a {symbol.KindText}, and `!` calls a macro");
                continue;
            }
            if (inside is not null)
                inside.Calls.Add((symbol, tree.GetSpan(callee.Span)));
            else
                called.Add(symbol);

            var invocation = MacroInvocation.Of(call, symbol, tree, diagnostics, at.Lookup);
            var written = new List<Use>();
            var outer = scope;
            scope = at;
            foreach (var argument in invocation.Arguments)
            {
                // A default was resolved where the macro is declared, so only what the call
                // itself wrote is collected here.
                if (!argument.Written)
                    continue;

                // A `one` argument is a word, which is never looked up — except that a word
                // may be passed on from a `one` parameter of the macro whose body writes the
                // call, and that is a name. So it is collected either way and stays silent
                // when it turns out to be no name at all.
                var words = argument.Parameter.Kind == ParameterKind.One
                    || argument.Parameter.Accepts.Element is { Kind: ParameterKind.One };
                CollectUses(argument.Value, written, words);
                foreach (var item in argument.Items)
                    CollectUses(item, written, words);
            }
            scope = outer;
            ResolveUses(written);
        }
    }

    /// <summary>Records every name written inside <paramref name="node"/>, to resolve once the file is read.</summary>
    private void CollectUses(SyntaxNode? node) => CollectUses(node, uses);

    private void CollectUses(SyntaxNode? node, List<Use> into, bool words = false)
    {
        if (node is null)
            return;
        // `.defined(NAME)` asks whether a name is a define. The name is not a use of
        // anything: one that is not declared is what the question is for.
        if (node.Kind == SyntaxKind.CallExpression && node.ChildTokens.Length > 0
            && node.ChildTokens[0].Text.Equals(".defined", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
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
                    into.Add(new Use(token, scope, path, first, Last: false, Word: words));
                    path = true;
                    first = false;
                }
            }

            // Which part is the last decides where an export is checked: another file has to
            // have exported the `inner` of `outer::inner`, not the `outer` that leads to it.
            if (!first)
                into[^1] = into[^1] with { Last = true };
            return;
        }
        foreach (var child in node.ChildNodes)
            CollectUses(child, into, words);
    }

    private Symbol? Declare(
        SyntaxToken name,
        SymbolKind kind,
        SyntaxNode? value = null,
        SyntaxNode? data = null,
        SyntaxNode? type = null,
        IReadOnlyList<SyntaxNode>? items = null,
        IReadOnlyList<SyntaxNode>? entries = null,
        bool follows = false)
    {
        // A member of a named type may be called after a register or a mnemonic: nothing can
        // be written there but a member name, so there is nothing for it to shadow.
        if (kind != SymbolKind.Member && !CheckReservedWord(name))
            return null;
        if (kind != SymbolKind.Binding && repetition is { } repeated)
        {
            Report(name.Span, $"`{name.Text}` is declared inside a `{repeated.Text}` body. A name that "
                + "is distinct on every turn arrives with macro expansion");
            return null;
        }

        var cheap = name.Kind == SyntaxKind.CheapLocal;
        if (!cheap && kind != SymbolKind.MacroParameter && InABlockArgument)
        {
            Report(name.Span, $"`{name.Text}` is declared in a block argument, which may declare only "
                + "cheap locals: the macro it is given to may splice it in more than one place");
            return null;
        }
        var owner = cheap ? CheapLocalOwner(name) : scope;
        var symbol = new Symbol(cheap ? name.Text[1..] : name.Text, kind, owner, tree, name.Span)
        {
            IsCheapLocal = cheap,
            ValueExpression = value,
            Segment = segment,
            Data = data,
            TypeExpression = type,
            Items = items ?? [],
            Entries = entries ?? [],
            FollowsPrevious = follows,
        };

        if (owner.Declare(symbol) is { } existing)
        {
            // A body that declared the name an `ident` parameter stands for would be
            // declaring a name in its caller, which is what says it plainly here.
            Report(name.Span,
                existing.Parameter is { Kind: ParameterKind.Ident }
                    ? $"`{symbol.DisplayName}` is an `ident` parameter, and a body may not declare "
                        + "the name it stands for"
                    : $"`{symbol.DisplayName}` is already declared in this scope",
                new RelatedSpan(existing.DeclarationSpan, "declared here"));
        }
        symbols.Add(symbol);
        references.Add(new SymbolReference(symbol, name.Span, true));
        return symbol;
    }

    /// <summary>
    /// The <c>.proc</c> or <c>.scope</c> a cheap local belongs to. One written outside
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
    /// The reserved words: a symbol may not be named after a mnemonic or a register.
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

    private void ResolveUses(IReadOnlyList<Use> list)
    {
        Symbol? previous = null;
        var broken = false;
        foreach (var (token, at, path, first, last, splice, word) in list)
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

            previous = Resolve(token, at, path, previous, last, word);
            if (previous is null)
            {
                broken = true;
            }
            else
            {
                references.Add(new SymbolReference(previous, token.Span, false));
                RecordBodyUse(at, previous, token);
                if (splice && previous.Parameter is not { Kind: ParameterKind.Block })
                {
                    Report(token.Span, $"`{token.Text}` is a {previous.KindText}; a name written on its "
                        + "own splices a `block` parameter, and nothing else belongs on a line alone");
                }
            }
        }
    }

    /// <summary>
    /// A name a macro body uses that it neither declared nor was given. An expansion needs it
    /// wherever it lands, so the macro remembers it: the file that calls the macro brings it
    /// in, and an exported macro may only use what is exported too.
    /// </summary>
    private static void RecordBodyUse(Scope at, Symbol used, SyntaxToken token)
    {
        Scope? body = null;
        for (var around = at; around is not null; around = around.Parent)
        {
            if (around.Kind == ScopeKind.Macro)
            {
                body = around;
                break;
            }
        }
        if (body?.Owner is not { } macro)
            return;

        // What the body declares, and the parameters it was given, travel with it.
        for (var owner = used.Scope; owner is not null; owner = owner.Parent)
        {
            if (owner == body)
                return;
        }
        if (!macro.Uses.Any(seen => seen.Used == used))
            macro.Uses.Add((used, used.Tree.GetSpan(token.Span)));
    }

    /// <summary>
    /// What one part of a written name means. <paramref name="previous"/> is what the part
    /// before it resolved to, so a path walks into a scope instead of looking outward again.
    /// </summary>
    private Symbol? Resolve(SyntaxToken token, Scope at, bool path, Symbol? previous, bool last, bool word = false)
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
            // A register or a mnemonic parses as a name so that a macro body may pass it as a
            // word. Outside one it can only be a mistake, and saying which reserved word it
            // is beats saying the name is not declared.
            if (!word && !CheckReservedWord(token))
                return null;
            if (at.Lookup(token.Text) is { } symbol)
                return symbol;

            // A name the file does not declare may belong to another file of the program.
            if (program.Lookup(token.Text, tree) is { } external)
                return CheckExported(token, external, last);

            // In a condition a bare name may be a word rather than a name at all, and a word
            // is compared, never looked up.
            if (!word)
                Report(token.Span, $"`{token.Text}` is not declared");
            return null;
        }

        // A part after `::`: the scope to look in is the one the part before it opened, and a
        // leading `::` starts at the file's own top level.
        var container = previous is null ? fileScope : BodyOf(previous);
        if (container is null)
        {
            Report(token.Span, $"`{previous!.DisplayName}` is a {previous.KindText}, not a scope");
            return null;
        }

        var member = container.FindMember(token.Text);
        if (member is null)
        {
            // A repetition's name at the end of a path means the member of that scope with
            // the same spelling, which is a different member on every turn. That needs the
            // same per-expansion naming a declaration inside a repetition does.
            if (at.Lookup(token.Text) is { Kind: SymbolKind.Binding } binding)
            {
                Report(token.Span, $"`{binding.Name}` is a repetition binding, and naming a member "
                    + "through one arrives with macro expansion");
                return null;
            }
            Report(token.Span, container.Kind == ScopeKind.File
                ? $"`{token.Text}` is not declared at file scope"
                : $"`{token.Text}` is not declared in `{container.Name}`");
            return null;
        }
        return CheckExported(token, member, last);
    }

    /// <summary>
    /// A symbol another file declares may only be named if that file exports it. The
    /// check is on the last part of a name: <c>outer::inner</c> needs <c>inner</c> exported,
    /// and <c>outer</c> is only the way in. The symbol is returned either way, so an editor
    /// can still go to a declaration that is private rather than missing.
    /// </summary>
    private Symbol? CheckExported(SyntaxToken token, Symbol symbol, bool last)
    {
        if (!last || symbol.Tree == tree || program.IsExported(symbol))
            return symbol;
        // The file is named by its own name rather than by its whole path: the related span
        // is what takes an editor there, and a path is long enough to bury the message.
        var file = symbol.Tree.Path[(symbol.Tree.Path.LastIndexOf('/') + 1)..];
        Report(token.Span, $"`{symbol.QualifiedName}` is declared in `{file}` and is not exported",
            new RelatedSpan(symbol.DeclarationSpan, "declared here"));
        return symbol;
    }

    /// <summary>
    /// What a name may reach into. A routine or a scope opens its own; a member or an
    /// instance opens the one belonging to the type it names, which is what makes the fields
    /// of a `.tag` reachable through it.
    /// </summary>
    private Scope? BodyOf(Symbol symbol)
    {
        // A macro has a body scope, but it is not one a path may reach into: what a body
        // declares is local to each expansion, so there is no one symbol to name from
        // outside.
        if (symbol.Kind == SymbolKind.Macro)
            return null;
        if (symbol.Body is { } own)
            return own;
        if (symbol.TypeExpression is null || !resolving.Add(symbol))
            return null;
        var type = TypeOf(symbol);
        resolving.Remove(symbol);
        return type?.Body;
    }

    /// <summary>
    /// The type a <c>.tag</c> names, resolved from where it was written. This runs on demand
    /// rather than in order, because a name may reach into a type the file declares later.
    /// </summary>
    private Symbol? TypeOf(Symbol symbol)
    {
        if (symbol.Type is { } known)
            return known;
        if (symbol.TypeExpression is not { } named)
            return null;

        Symbol? part = null;
        var path = false;
        foreach (var token in named.ChildTokens)
        {
            if (token.Kind == SyntaxKind.ColonColon)
            {
                path = true;
                continue;
            }
            part = path
                ? (part is null ? fileScope : BodyOf(part))?.FindMember(token.Text)
                : symbol.Scope.Lookup(token.Text) ?? program.Lookup(token.Text, tree);
            path = true;
            if (part is null)
                return null;
        }
        symbol.Type = part;
        return part;
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
    /// <param name="Last">Whether it is the last part, and so the symbol the whole name stands for.</param>
    /// <param name="Splice">Whether the name stands alone on a line, and so splices a block.</param>
    /// <param name="Word">Whether it is written where a bare word may stand, and so may be one.</param>
    private readonly record struct Use(
        SyntaxToken Token, Scope Scope, bool Path, bool First, bool Last,
        bool Splice = false, bool Word = false);

    /// <summary>One call, waiting for the whole program to be read before it is matched up.</summary>
    /// <param name="Call">The call.</param>
    /// <param name="Scope">The scope it was written in, which its arguments resolve in.</param>
    /// <param name="Inside">The macro whose body holds it, or null when it is called outright.</param>
    private readonly record struct Invocation(SyntaxNode Call, Scope Scope, Symbol? Inside);
}
