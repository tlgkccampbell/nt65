using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One declared name, with what analysis needs of it: whether it is a constant or an
/// address, its value where nt65 knows it, its address size, and the segment it sits in.
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

    /// <summary>The name as the source writes it, without the <c>@</c> of a cheap local.</summary>
    public string Name { get; }

    /// <summary>What the declaration declares. A <c>NAME = expr</c> is classified once its expression is read.</summary>
    public SymbolKind Kind { get; internal set; }

    /// <summary>The scope holding the declaration.</summary>
    public Scope Scope { get; }

    /// <summary>The file it was declared in.</summary>
    public SyntaxTree Tree { get; }

    /// <summary>Where the name is written, which is what an editor selects and renames.</summary>
    public TextSpan NameSpan { get; }

    /// <summary>
    /// Whether other modules may name it: an <c>.export</c> names it, is written before its
    /// declaration, or exports the scope, the data or the type it is declared in.
    /// </summary>
    public bool IsExported { get; internal set; }

    /// <summary>
    /// The name the linker knows it by, for an export: the <c>as</c> name its <c>.export</c>
    /// gives, an import's own name, or its path with the module's in front, joined with
    /// <c>__</c>. Null for a symbol that is not exported.
    /// </summary>
    public string? LinkerName { get; internal set; }

    /// <summary>The address size an <c>.export</c> gives it, <c>.export K: abs</c>, or null when it gives none.</summary>
    public AddressSize? ExportSize { get; internal set; }

    /// <summary>Where the <c>.export</c> that exports it is written, or null for a symbol that is not exported.</summary>
    public TextSpan? ExportSpan { get; internal set; }

    /// <summary>Whether it is a cheap local, <c>@name</c>, private to its proc or scope.</summary>
    public bool IsCheapLocal { get; internal init; }

    /// <summary>
    /// Whether it is a build-configuration define rather than something a source file
    /// declared. A define is an ordinary constant everywhere but in the output, which always
    /// writes one as its value so that a <c>-D</c> given to ca65 cannot collide with it.
    /// </summary>
    public bool IsDefine { get; internal set; }

    /// <summary>
    /// Whether the symbol is a <c>.config</c> setting: a constant whose value the build may set,
    /// which the output, as with a define, writes as its value rather than by name.
    /// </summary>
    public bool IsConfig { get; internal set; }

    /// <summary>The segment the declaration sits in, for an address; null for a constant.</summary>
    public string? Segment { get; internal set; }

    /// <summary>
    /// For one instance of a family, the name its repetition binds and what that name is worth
    /// here, which is what a signature naming the binding is read with. Null for everything else.
    /// </summary>
    public (Symbol Binding, Expansion.Bound Value)? Bound { get; internal set; }

    /// <summary>The expression after <c>=</c>, or null for a label, proc body or scope.</summary>
    public ExpressionSyntax? ValueExpression { get; internal init; }

    /// <summary>The scope a <c>.proc</c>, a <c>.scope</c> or mixed data opens; null for everything else.</summary>
    public Scope? Body { get; internal set; }

    /// <summary>
    /// The block a <c>.macro</c> body is written in, which every call expands, or the block of
    /// a <c>.data name { }</c>, which holds its members.
    /// </summary>
    public SyntaxNode? Definition { get; internal set; }

    /// <summary>The value, where nt65 knows it. For a struct member, its offset.</summary>
    public Value Value { get; internal set; }

    /// <summary>How many bytes the symbol stands for, where that is a question with an answer.</summary>
    public long? Size { get; internal set; }

    /// <summary>How many elements those bytes are, for a type, an array or a data label.</summary>
    public long? Count { get; internal set; }

    /// <summary>
    /// The <c>T</c> of a <c>.type T</c>, before it is resolved. A member or a data declaration
    /// takes its size and its fields from the type it names.
    /// </summary>
    public ExpressionSyntax? TypeExpression { get; internal init; }

    /// <summary>The type a <c>.type</c> names, once resolved.</summary>
    public Symbol? Type { get; internal set; }

    /// <summary>A function's parameters, in order, as the symbols its body names.</summary>
    public IReadOnlyList<Symbol> ParameterSymbols { get; internal set; } = [];

    /// <summary>A macro's parameters, in the order they are written.</summary>
    public IReadOnlyList<MacroParameter> Parameters { get; internal set; } = [];

    /// <summary>What a macro parameter accepts; meaningless for every other kind of symbol.</summary>
    public MacroParameter? Parameter { get; internal set; }

    /// <summary>
    /// The macros this macro's body calls, with the call each was named at. A macro may not
    /// reach itself through them, which is what makes every expansion bounded.
    /// </summary>
    public IReadOnlyList<(Symbol Callee, Span At)> Calls => calls;

    /// <summary>Remembers that this macro's body calls <paramref name="callee"/>, named at <paramref name="at"/>.</summary>
    internal void AddCall(Symbol callee, Span at) => calls.Add((callee, at));

    /// <summary>
    /// The names a macro's body uses that it did not declare and was not given: what an
    /// expansion of it needs wherever it lands. A file that calls the macro has to be able to
    /// reach all of them, and its output brings in the ones another file declares.
    /// </summary>
    public IReadOnlyList<(Symbol Used, Span At)> Uses => uses;

    /// <summary>Remembers that this macro's body uses <paramref name="used"/>, written at <paramref name="at"/>.</summary>
    internal void AddUse(Symbol used, Span at) => uses.Add((used, at));

    /// <summary>A list's items, or a function's body as its single item.</summary>
    public IReadOnlyList<SyntaxNode> Items { get; internal init; } = [];

    /// <summary>A charmap's entry lines, read into a mapping when it is first applied.</summary>
    public IReadOnlyList<CharmapEntrySyntax> Entries { get; internal init; } = [];

    /// <summary>
    /// The element directive of a data declaration or a struct member, which is what gives it a
    /// size and a count; null for mixed data, whose block is its <see cref="Definition"/>.
    /// </summary>
    public StatementSyntax? Data { get; internal init; }

    /// <summary>
    /// The enum member written before this one, or null for the first. A member with no value
    /// of its own follows it.
    /// </summary>
    public Symbol? PreviousMember { get; internal set; }

    /// <summary>Whether this is a member of an enum, named or anonymous, whose value must be a constant.</summary>
    public bool IsEnumMember { get; internal set; }

    /// <summary>
    /// Whether this is an enum member that was given no value, and so is the member before it
    /// plus one, or zero when it is the first.
    /// </summary>
    public bool FollowsPrevious { get; internal init; }

    /// <summary>
    /// The processor state a routine declares, for a proc, an extern proc or a
    /// <c>proc(...)</c> import; null for everything else, which is what makes a symbol a
    /// routine rather than an address.
    /// </summary>
    public Signature? Signature { get; internal set; }

    /// <summary>
    /// The processor state a macro declares it expects and leaves, or null for a macro that
    /// declares none, whose expansions are analyzed as the code they contain. Kept apart from
    /// <see cref="Signature"/>, because a macro is expanded rather than called.
    /// </summary>
    public Signature? MacroSignature { get; internal set; }

    /// <summary>
    /// The <c>.state</c> written directly after a label, which declares the label an entry
    /// point with that state; null for a label with none and for every other symbol.
    /// </summary>
    public StateDirectiveSyntax? StateDeclaration { get; internal set; }

    /// <summary>The routine a label is written inside, or null for one at file level or in no routine.</summary>
    public Symbol? Routine => Scope.Enclosing(ScopeKind.Proc)?.Owner;

    /// <summary>
    /// Whether this and <paramref name="other"/> are instances of one family: one body written
    /// once and declared under every member's name, so a label inside it belongs to whichever
    /// of them is being read rather than to the first.
    /// </summary>
    public bool IsSiblingOf(Symbol other) =>
        Bound is not null && other.Bound is not null && Scope == other.Scope && NameSpan == other.NameSpan;

    /// <summary>The address size, or null where nt65 cannot tell yet.</summary>
    public AddressSize? AddressSize { get; internal set; }

    /// <summary>Whether the symbol names an address rather than a value.</summary>
    public bool IsAddress => Kind is SymbolKind.Label or SymbolKind.AddressAlias or SymbolKind.Proc
        or SymbolKind.ExternProc or SymbolKind.ImportedAddress or SymbolKind.Data;

    /// <summary>Whether the symbol is a layout whose members are offsets.</summary>
    public bool IsLayout => Kind is SymbolKind.Struct or SymbolKind.Union;

    /// <summary>
    /// Whether the symbol is defined in terms of itself, which evaluation reported once for
    /// the whole ring. It has no value and, for a type, no layout, so nothing that walks into
    /// one may walk into this.
    /// </summary>
    public bool IsCyclic { get; internal set; }

    /// <summary>
    /// Whether the symbol can be reached from outside its scope with <c>::</c>: a
    /// cheap local never can, and neither can anything inside an anonymous <c>.scope</c>.
    /// </summary>
    public bool IsReachableByPath => !IsCheapLocal && Scope.IsReachableByPath;

    /// <summary>The name as it is written, with the <c>@</c> of a cheap local.</summary>
    public string DisplayName => IsCheapLocal ? "@" + Name : Name;

    /// <summary>
    /// The name qualified by the scopes around it, as another file would write it; the
    /// file's own top level contributes nothing. A name no path can reach is just itself.
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
    /// The name the output gives it: the scopes that can name it, joined with
    /// <c>__</c>, so <c>outer::inner</c> becomes <c>outer__inner</c>. For a symbol a path
    /// cannot reach — a cheap local, or a name inside an anonymous scope — this is only the
    /// name it starts from, and the file it is emitted into makes it unique.
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

    /// <summary>The module the symbol is declared in, or null for a define or a file that names none.</summary>
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

    /// <summary>The qualified name with the module's in front, as another module would write it.</summary>
    public string PathName => Module is { } module && IsReachableByPath ? $"{module}::{QualifiedName}" : QualifiedName;

    /// <summary>
    /// What the output calls it: the linker name of an export, and otherwise the name
    /// <see cref="FlatName"/> starts from.
    /// </summary>
    public string OutputName => LinkerName ?? FlatName;

    /// <summary>Where the declaration is, as a diagnostic names it.</summary>
    public Span DeclarationSpan => Tree.GetSpan(NameSpan);

    /// <summary>What a programmer calls this kind of symbol.</summary>
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
    /// The same with its article, <c>a routine</c> or <c>an enumeration</c>. The kinds are a
    /// fixed list, so which article each takes is decided here rather than at every message
    /// that names one.
    /// </summary>
    public string KindPhrase => $"{(KindText[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an" : "a")} {KindText}";

    /// <summary>
    /// How wide an address it is to code in <paramref name="tree"/>. Another module sees the
    /// size its export gives it, which may be wider than the size it has.
    /// </summary>
    public AddressSize? AddressSizeIn(SyntaxTree tree) => tree != Tree && ExportSize is { } exported ? exported : AddressSize;

    /// <summary>The symbol's kind and name, for debugging.</summary>
    public override string ToString() => $"{KindText} {QualifiedName}";
}
