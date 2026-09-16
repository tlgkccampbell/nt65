using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One declared name, with what analysis needs of it: whether it is a constant or an
/// address, its value where nt65 knows it, its address size, and the segment it sits in.
/// </summary>
public sealed class Symbol
{
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

    /// <summary>Whether it is a cheap local, <c>@name</c>, private to its proc or scope.</summary>
    public bool IsCheapLocal { get; internal init; }

    /// <summary>
    /// Whether it is a build-configuration define rather than something a source file
    /// declared. A define is an ordinary constant everywhere but in the output, which always
    /// writes one as its value so that a <c>-D</c> given to ca65 cannot collide with it.
    /// </summary>
    public bool IsDefine { get; internal set; }

    /// <summary>The segment the declaration sits in, for an address; null for a constant.</summary>
    public string? Segment { get; internal set; }

    /// <summary>The expression after <c>=</c>, or null for a label, proc body or scope.</summary>
    public SyntaxNode? ValueExpression { get; internal init; }

    /// <summary>The scope a <c>.proc</c> or <c>.scope</c> opens; null for everything else.</summary>
    public Scope? Body { get; internal set; }

    /// <summary>The block a <c>.macro</c> body is written in, which every call expands.</summary>
    public SyntaxNode? Definition { get; internal set; }

    /// <summary>The value, where nt65 knows it. For a struct member, its offset.</summary>
    public Value Value { get; internal set; }

    /// <summary>How many bytes the symbol stands for, where that is a question with an answer.</summary>
    public long? Size { get; internal set; }

    /// <summary>How many elements those bytes are, for a type, an array or a data label.</summary>
    public long? Count { get; internal set; }

    /// <summary>
    /// The <c>T</c> of a <c>.tag T</c>, before it is resolved. A member or an instance takes
    /// its size and its fields from the type it names.
    /// </summary>
    public SyntaxNode? TypeExpression { get; internal init; }

    /// <summary>The type a <c>.tag</c> names, once resolved.</summary>
    public Symbol? Type { get; internal set; }

    /// <summary>A function's parameters, in order, as the symbols its body names.</summary>
    public IReadOnlyList<Symbol> ParameterSymbols { get; internal set; } = [];

    /// <summary>A macro's parameters, in the order they are written.</summary>
    public IReadOnlyList<MacroParameter> Parameters { get; internal set; } = [];

    /// <summary>What a macro parameter accepts; meaningless for every other kind of symbol.</summary>
    public MacroParameter? Parameter { get; internal set; }

    /// <summary>
    /// The macros this macro's body calls, with the call each was named at. A macro may not
    /// reach itself through them, which is what makes every expansion bounded (§11.1).
    /// </summary>
    public List<(Symbol Callee, Span At)> Calls { get; } = [];

    /// <summary>A list's items, or a function's body as its single item.</summary>
    public IReadOnlyList<SyntaxNode> Items { get; internal init; } = [];

    /// <summary>A charmap's entry lines, read into a mapping when it is first applied.</summary>
    public IReadOnlyList<SyntaxNode> Entries { get; internal init; } = [];

    /// <summary>The data directive a label sits on, which is what gives it a size and a count.</summary>
    public SyntaxNode? Data { get; internal init; }

    /// <summary>
    /// The enum member written before this one, or null for the first. A member with no value
    /// of its own follows it.
    /// </summary>
    public Symbol? PreviousMember { get; internal set; }

    /// <summary>
    /// Whether this is an enum member that was given no value, and so is the member before it
    /// plus one, or zero when it is the first.
    /// </summary>
    public bool FollowsPrevious { get; internal init; }

    /// <summary>The address size, or null where nt65 cannot tell yet.</summary>
    public AddressSize? AddressSize { get; internal set; }

    /// <summary>Whether the symbol names an address rather than a value.</summary>
    public bool IsAddress => Kind is SymbolKind.Label or SymbolKind.AddressAlias or SymbolKind.Proc
        or SymbolKind.ExternProc or SymbolKind.ImportedAddress or SymbolKind.Instance;

    /// <summary>Whether the symbol is a layout whose members are offsets.</summary>
    public bool IsLayout => Kind is SymbolKind.Struct or SymbolKind.Union;

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
        SymbolKind.Instance => "instance",
        SymbolKind.Charmap => "character mapping",
        SymbolKind.List => "list",
        SymbolKind.Binding => "repetition binding",
        SymbolKind.Macro => "macro",
        SymbolKind.MacroParameter => "macro parameter",
        _ => "function",
    };

    /// <summary>The symbol's kind and name, for debugging.</summary>
    public override string ToString() => $"{KindText} {QualifiedName}";
}
