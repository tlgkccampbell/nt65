namespace Norristown.Semantics;

/// <summary>
/// One level of naming: a file, a <c>.proc</c> body or a <c>.scope</c> body. Lookup
/// runs from the innermost scope outward, and a <c>.proc</c> or <c>.scope</c> also owns the
/// cheap locals written inside it, which live in a namespace of their own.
/// </summary>
public sealed class Scope
{
    private readonly Dictionary<string, Symbol> members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Symbol> cheapLocals = new(StringComparer.Ordinal);
    private readonly List<Symbol> order = [];

    internal Scope(ScopeKind kind, string? name, Scope? parent, Symbol? owner)
    {
        Kind = kind;
        Name = name;
        Parent = parent;
        Owner = owner;
    }

    /// <summary>What kind of scope this is.</summary>
    public ScopeKind Kind { get; }

    /// <summary>The scope's name, or null for a file or an anonymous <c>.scope</c>.</summary>
    public string? Name { get; }

    /// <summary>The scope around this one, or null for a file.</summary>
    public Scope? Parent { get; }

    /// <summary>The symbol this scope belongs to, or null for a file or an anonymous <c>.scope</c>.</summary>
    public Symbol? Owner { get; }

    /// <summary>Everything declared here, cheap locals included, in source order.</summary>
    public IReadOnlyList<Symbol> Symbols => order;

    /// <summary>
    /// Whether every scope from here out to the file has a name, so what is declared here
    /// can be reached with <c>::</c>. An anonymous <c>.scope { }</c> is inline code, and
    /// nothing outside it can name what it declares.
    /// </summary>
    public bool IsReachableByPath
    {
        get
        {
            for (var scope = this; scope.Kind != ScopeKind.File; scope = scope.Parent!)
            {
                if (scope.Name is null)
                    return false;
            }
            return true;
        }
    }

    /// <summary>The nearest scope with a name, which is the routine or scope a name lives in.</summary>
    public Scope? NearestNamed()
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope.Name is not null)
                return scope;
        }
        return null;
    }

    /// <summary>
    /// Adds <paramref name="symbol"/>, or returns the declaration already using that name.
    /// A name may be declared once in its scope, and cheap locals count separately.
    /// </summary>
    internal Symbol? Declare(Symbol symbol)
    {
        var table = symbol.IsCheapLocal ? cheapLocals : members;
        if (table.TryGetValue(symbol.Name, out var existing))
            return existing;
        table.Add(symbol.Name, symbol);
        order.Add(symbol);
        return null;
    }

    /// <summary>What <paramref name="name"/> means here, without looking outward.</summary>
    public Symbol? FindMember(string name) => members.GetValueOrDefault(name);

    /// <summary>The cheap local <paramref name="name"/> declared here, without looking outward.</summary>
    public Symbol? FindCheapLocal(string name) => cheapLocals.GetValueOrDefault(name);

    /// <summary>
    /// What <paramref name="name"/> means, from here outward to the file. A nested
    /// scope can therefore name what its proc declares, and a proc what its file declares.
    /// </summary>
    public Symbol? Lookup(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope.FindMember(name) is { } symbol)
                return symbol;
        }
        return null;
    }

    /// <summary>
    /// The cheap local <paramref name="name"/>, looked up from here outward, so a nested
    /// <c>.scope</c> can branch to its proc's <c>@done</c>.
    /// </summary>
    public Symbol? LookupCheapLocal(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope.FindCheapLocal(name) is { } symbol)
                return symbol;
        }
        return null;
    }

    /// <summary>The scope's kind and name, for debugging.</summary>
    public override string ToString() => Name is null ? Kind.ToString() : $"{Kind} {Name}";
}
