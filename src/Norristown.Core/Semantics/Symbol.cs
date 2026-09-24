using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents one declared name, with what analysis needs to know about it. This includes
/// whether it is a constant or an address, its value where nt65 knows it, its address size, and
/// the segment it is in.
/// <para>
/// Binding and evaluation fill a symbol in while its <see cref="ProgramModel"/> is built. Once
/// the model is complete the symbol is frozen, and it never changes again.
/// </para>
/// </summary>
public sealed class Symbol
{
    private readonly List<(Symbol Callee, Span At)> calls = [];
    private readonly List<(Symbol Used, Span At)> uses = [];

    internal Symbol(string name, SymbolKind kind, Scope scope, SyntaxTree tree, TextSpan nameSpan)
    {
        Name = name;
        Kind = kind;
        Scope = scope;
        Tree = tree;
        NameSpan = nameSpan;
    }

    /// <summary>Gets the name as it appears in the source, without the <c>@</c> of a cheap local.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the kind of thing the declaration declares. A <c>NAME = expr</c> is classified once
    /// its expression is read.
    /// </summary>
    public SymbolKind Kind { get; internal set => field = Unfrozen(value); }

    /// <summary>Gets the scope that contains the declaration.</summary>
    public Scope Scope { get; }

    /// <summary>Gets the file the symbol was declared in.</summary>
    public SyntaxTree Tree { get; }

    /// <summary>Gets the span of the declared name, which an editor selects and renames.</summary>
    public TextSpan NameSpan { get; }

    /// <summary>
    /// Gets a value indicating whether other modules may name the symbol. That is the case when an
    /// <c>.export</c> names it, precedes its declaration, or exports the scope, the data or the
    /// type it is declared in.
    /// </summary>
    public bool IsExported { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets the name the linker knows an export by. This is the <c>as</c> name its
    /// <c>.export</c> gives, an import's own name, or its path prefixed with the module's, joined
    /// with <c>__</c>. Null for a symbol that is not exported.
    /// </summary>
    public string? LinkerName { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets the address size an <c>.export</c> gives the symbol, as in <c>.export K: abs</c>, or
    /// null when it gives none.
    /// </summary>
    public AddressSize? ExportSize { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets the span of the <c>.export</c> that exports the symbol, or null for a symbol that is
    /// not exported.
    /// </summary>
    public TextSpan? ExportSpan { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets a value indicating whether the symbol is a cheap local (<c>@name</c>), private to its
    /// proc or scope.
    /// </summary>
    public bool IsCheapLocal { get; internal init; }

    /// <summary>
    /// Gets a value indicating whether the symbol is a build-configuration define rather than
    /// something a source file declared. A define is an ordinary constant everywhere except in
    /// the output, which always emits it as its value so that a <c>-D</c> given to ca65 cannot
    /// collide with it.
    /// </summary>
    public bool IsDefine { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets a value indicating whether the symbol is a <c>.config</c> setting, which is a constant
    /// whose value the build may set. As with a define, the output emits it as its value rather
    /// than by name.
    /// </summary>
    public bool IsConfig { get; internal set => field = Unfrozen(value); }

    /// <summary>Gets the segment the declaration is in, for an address; null for a constant.</summary>
    public string? Segment { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets, for one instance of a <see cref="Family"/>, the repetition's binding and its value
    /// for this instance, with which a signature naming the binding is read. Null for every other
    /// symbol.
    /// </summary>
    public (Symbol Binding, Expansion.Bound Value)? Bound { get; internal set => field = Unfrozen(value); }

    /// <summary>Gets the expression after <c>=</c>, or null for a label, proc body or scope.</summary>
    public ExpressionSyntax? ValueExpression { get; internal init; }

    /// <summary>
    /// Gets the scope the symbol's declaration opens, which is the body of a <c>.proc</c>, a
    /// <c>.scope</c>, mixed data, a macro, a <c>.func</c>, or a named enum, struct or union. Null
    /// for everything else.
    /// </summary>
    public Scope? Body { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets the block that contains a <c>.macro</c> body, which every call expands, or the block
    /// of a <c>.data name { }</c>, which holds its members.
    /// </summary>
    public SyntaxNode? Definition { get; internal set => field = Unfrozen(value); }

    /// <summary>Gets the value, where nt65 knows it. For a struct member, this is its offset.</summary>
    public Value Value { get; internal set => field = Unfrozen(value); }

    /// <summary>Gets how many bytes the symbol occupies, where that is both meaningful and known.</summary>
    public long? Size { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets how many elements those bytes form, for a type, an array or a data label.
    /// </summary>
    public long? Count { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets the <c>T</c> of a <c>.type T</c>, before it is resolved. A member or a data
    /// declaration takes its size and its fields from the type it names.
    /// </summary>
    public ExpressionSyntax? TypeExpression { get; internal init; }

    /// <summary>Gets the type a <c>.type</c> names, once resolved.</summary>
    public Symbol? Type { get; internal set => field = Unfrozen(value); }

    /// <summary>Gets a function's parameters, in order, as the symbols its body names.</summary>
    public IReadOnlyList<Symbol> ParameterSymbols { get; internal set => field = Unfrozen(value); } = [];

    /// <summary>Gets a macro's parameters, in declaration order.</summary>
    public IReadOnlyList<MacroParameter> Parameters { get; internal set => field = Unfrozen(value); } = [];

    /// <summary>
    /// Gets what a macro parameter accepts; meaningless for every other kind of symbol.
    /// </summary>
    public MacroParameter? Parameter { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets the macros this macro's body calls, each with the call that names it. A macro may not
    /// reach itself through them, which guarantees that every expansion is bounded.
    /// </summary>
    public IReadOnlyList<(Symbol Callee, Span At)> Calls => calls;

    /// <summary>
    /// Records that this macro's body calls <paramref name="callee"/>, named at
    /// <paramref name="at"/>.
    /// </summary>
    internal void AddCall(Symbol callee, Span at) => calls.Add((Unfrozen(callee), at));

    /// <summary>
    /// Gets the names a macro's body uses that it neither declared nor was given. Every expansion
    /// of the macro needs them, in whichever file it is. A file that calls the macro must be able
    /// to reach all of them, and its output brings in the ones another file declares.
    /// </summary>
    public IReadOnlyList<(Symbol Used, Span At)> Uses => uses;

    /// <summary>
    /// Records that this macro's body uses <paramref name="used"/>, named at <paramref name="at"/>.
    /// </summary>
    internal void AddUse(Symbol used, Span at) => uses.Add((Unfrozen(used), at));

    /// <summary>Gets a list's items, or a function's body as its single item.</summary>
    public IReadOnlyList<SyntaxNode> Items { get; internal init; } = [];

    /// <summary>Gets a charmap's entry lines, which are read into a mapping when it is first applied.</summary>
    public IReadOnlyList<CharmapEntrySyntax> Entries { get; internal init; } = [];

    /// <summary>
    /// Gets the element directive of a data declaration or a struct member, which gives it a size
    /// and a count. Null for mixed data, whose block is its <see cref="Definition"/>.
    /// </summary>
    public StatementSyntax? Data { get; internal init; }

    /// <summary>
    /// Gets the enum member declared before this one, or null for the first. A member with no
    /// value of its own follows it.
    /// </summary>
    public Symbol? PreviousMember { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets a value indicating whether this is a member of an enum, named or anonymous, whose
    /// value must be a constant.
    /// </summary>
    public bool IsEnumMember { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets a value indicating whether this is an enum member that was given no value, and so is
    /// the member before it plus one, or zero when it is the first.
    /// </summary>
    public bool FollowsPrevious { get; internal init; }

    /// <summary>
    /// Gets the processor state a routine declares, for a proc, an extern proc or a
    /// <c>proc(...)</c> import. It is null for everything else, and a signature is what makes a
    /// symbol a routine rather than an address.
    /// </summary>
    public Signature? Signature { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets the processor state a macro declares that it expects and leaves, or null for a macro
    /// that declares none, whose expansions are analyzed as the code they contain. It is kept
    /// separate from <see cref="Signature"/>, because a macro is expanded rather than called.
    /// </summary>
    public Signature? MacroSignature { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets the <c>.state</c> directly after a label, which declares the label an entry point with
    /// that state. Null for a label with none and for every other symbol.
    /// </summary>
    public StateDirectiveSyntax? StateDeclaration { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets the routine that contains a label, or null for a label at file level or outside every
    /// routine.
    /// </summary>
    public Symbol? Routine => Scope.Enclosing(ScopeKind.Proc)?.Owner;

    /// <summary>
    /// Returns a value indicating whether this symbol and <paramref name="other"/> are instances
    /// of the same <see cref="Family"/>. Such instances share one body, declared under every
    /// member's name, so a label inside it belongs to whichever instance is being read rather
    /// than to the first.
    /// </summary>
    public bool IsSiblingOf(Symbol other) =>
        Bound is not null && other.Bound is not null && Scope == other.Scope && NameSpan == other.NameSpan;

    /// <summary>Gets the address size, or null where nt65 cannot tell yet.</summary>
    public AddressSize? AddressSize { get; internal set => field = Unfrozen(value); }

    /// <summary>Gets a value indicating whether the symbol names an address rather than a value.</summary>
    public bool IsAddress => Kind is SymbolKind.Label or SymbolKind.AddressAlias or SymbolKind.Proc
        or SymbolKind.ExternProc or SymbolKind.ImportedAddress or SymbolKind.Data;

    /// <summary>
    /// Gets a value indicating whether the symbol declares what its bytes are. This is true of a
    /// data declaration and of an import that gives an element type. Both are sized and counted
    /// from what they declare, and both reach the fields of the type they name.
    /// </summary>
    public bool IsTypedStorage => Kind == SymbolKind.Data || (Kind == SymbolKind.ImportedAddress && Data is not null);

    /// <summary>Gets a value indicating whether the symbol is a layout whose members are offsets.</summary>
    public bool IsLayout => Kind is SymbolKind.Struct or SymbolKind.Union;

    /// <summary>
    /// Gets a value indicating whether the symbol is defined in terms of itself, which evaluation
    /// reported once for the whole cycle. It has no value and, for a type, no layout, so code that
    /// follows symbols' values or layouts must not follow this one.
    /// </summary>
    public bool IsCyclic { get; internal set => field = Unfrozen(value); }

    /// <summary>
    /// Gets a value indicating whether the symbol can be reached from outside its scope with
    /// <c>::</c>. A cheap local never can, and neither can anything inside an anonymous
    /// <c>.scope</c>.
    /// </summary>
    public bool IsReachableByPath => !IsCheapLocal && Scope.IsReachableByPath;

    /// <summary>Gets the name as it appears in the source, with the <c>@</c> of a cheap local.</summary>
    public string DisplayName => IsCheapLocal ? "@" + Name : Name;

    /// <summary>
    /// Gets the name qualified by the scopes around it, as another file would write it. The
    /// file's own top level contributes nothing, and a name no path can reach is unqualified.
    /// </summary>
    public string QualifiedName
    {
        get
        {
            if (!IsReachableByPath)
                return DisplayName;
            var name = DisplayName;
            for (var scope = Scope; scope is { Kind: not ScopeKind.File }; scope = scope.Parent!)
                name = $"{scope.Name}::{name}";
            return name;
        }
    }

    /// <summary>
    /// Gets the name the output gives the symbol, which is its name prefixed with the named
    /// scopes around it, joined with <c>__</c>, so <c>outer::inner</c> becomes
    /// <c>outer__inner</c>. For a symbol no path can reach, such as a cheap local or a name inside
    /// an anonymous scope, this is only the starting point, and the file it is emitted into makes
    /// it unique.
    /// </summary>
    public string FlatName
    {
        get
        {
            var name = Name;
            for (var scope = IsReachableByPath ? Scope : Scope.NearestNamed();
                scope is { Kind: not ScopeKind.File };
                scope = scope.Parent)
            {
                if (scope.Name is { } outer)
                    name = $"{outer}__{name}";
            }
            return name;
        }
    }

    /// <summary>
    /// Gets the module the symbol is declared in, or null for a define or a file that names no
    /// module.
    /// </summary>
    public string? Module
    {
        get
        {
            var scope = Scope;
            while (scope.Parent is { } outer)
                scope = outer;
            return scope.Module;
        }
    }

    /// <summary>
    /// Gets the qualified name prefixed with the module's name, as another module would write it.
    /// </summary>
    public string PathName => Module is { } module && IsReachableByPath ? $"{module}::{QualifiedName}" : QualifiedName;

    /// <summary>
    /// Gets the name the output uses for the symbol, which is the linker name of an export and
    /// otherwise <see cref="FlatName"/>.
    /// </summary>
    public string OutputName => LinkerName ?? FlatName;

    /// <summary>Gets the span of the declaration, for a diagnostic to point at.</summary>
    public Span DeclarationSpan => Tree.GetSpan(NameSpan);

    /// <summary>Gets the name a programmer uses for this kind of symbol.</summary>
    public string KindText => Kind switch
    {
        SymbolKind.Label => "label",
        SymbolKind.Constant => "constant",
        SymbolKind.AddressAlias => "address alias",
        SymbolKind.Proc => "routine",
        SymbolKind.ExternProc => "extern routine",
        SymbolKind.Scope => "scope",
        SymbolKind.ImportedAddress => "imported address",
        SymbolKind.ImportedConstant => "imported constant",
        SymbolKind.Enum => "enumeration",
        SymbolKind.Struct => "structure",
        SymbolKind.Union => "union",
        SymbolKind.Member => "member",
        SymbolKind.Data => "data declaration",
        SymbolKind.Charmap => "character mapping",
        SymbolKind.List => "list",
        SymbolKind.Binding => "repetition binding",
        SymbolKind.Macro => "macro",
        SymbolKind.MacroParameter => "macro parameter",
        SymbolKind.Frame => "stack frame",
        SymbolKind.SignatureSet => "signature set",

        _ => "function",
    };

    /// <summary>
    /// Gets <see cref="KindText"/> with its article, such as <c>a routine</c> or
    /// <c>an enumeration</c>. The kinds are a fixed list, so the article for each is decided here
    /// rather than in every message that names one.
    /// </summary>
    public string KindPhrase => $"{(KindText[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an" : "a")} {KindText}";

    /// <summary>
    /// Gets a value indicating whether the symbol belongs to a completed <see cref="ProgramModel"/>.
    /// Other threads may then be reading it, so every setter throws rather than change it. A
    /// program built from an earlier one after an edit keeps the unchanged files' symbols as they
    /// are.
    /// </summary>
    internal bool IsFrozen { get; private set; }

    /// <summary>
    /// Returns the symbol's address size as seen by code in <paramref name="tree"/>. Another
    /// module sees the size the export gives it, which may be wider than its own size.
    /// </summary>
    public AddressSize? AddressSizeIn(SyntaxTree tree) => tree != Tree && ExportSize is { } exported ? exported : AddressSize;

    /// <summary>Returns the symbol's kind and name, for debugging.</summary>
    public override string ToString() => $"{KindText} {QualifiedName}";

    /// <summary>
    /// Freezes the symbol once its program model is complete, after which nothing may change it.
    /// </summary>
    internal void Freeze() => IsFrozen = true;

    /// <summary>
    /// Returns <paramref name="value"/>, the new value of a setter, once it has checked that the
    /// symbol may still change.
    /// </summary>
    /// <exception cref="InvalidOperationException">The symbol is frozen.</exception>
    private T Unfrozen<T>(T value) => IsFrozen
        ? throw new InvalidOperationException($"The {KindText} {QualifiedName} belongs to a completed program model and cannot change.")
        : value;
}
