namespace Norristown.Semantics;

/// <summary>
/// Represents one level of naming, such as a file, a <c>.proc</c> body or a <c>.scope</c> body.
/// Lookup runs from the innermost scope outward. A <c>.proc</c> or <c>.scope</c> also owns the
/// cheap locals declared inside it, which live in a namespace of their own.
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

    /// <summary>Gets the kind of scope this is.</summary>
    public ScopeKind Kind { get; }

    /// <summary>Gets the scope's name, or null for a file or an anonymous <c>.scope</c>.</summary>
    public string? Name { get; }

    /// <summary>Gets the scope around this one, or null for a file.</summary>
    public Scope? Parent { get; }

    /// <summary>
    /// Gets the symbol this scope belongs to, or null for a file or an anonymous <c>.scope</c>.
    /// The body of a <see cref="Family"/> belongs to the first of its instances, which are
    /// declared after the body is read.
    /// </summary>
    public Symbol? Owner { get; internal set; }

    /// <summary>
    /// Gets the module a file's <c>.module</c> names, for a file scope; null for every other scope.
    /// </summary>
    public string? Module { get; internal set; }

    /// <summary>
    /// Gets the <c>.if</c> chain a <see cref="ScopeKind.Branch"/> is one branch of, which its
    /// sibling branches share; null for every other scope.
    /// </summary>
    internal object? Chain { get; init; }

    /// <summary>Gets every symbol declared in this scope, including cheap locals, in source order.</summary>
    public IReadOnlyList<Symbol> Symbols => order;

    /// <summary>
    /// Gets a value indicating whether every scope from here out to the file has a name, so that
    /// what is declared here can be reached with <c>::</c>. An anonymous <c>.scope { }</c> is
    /// inline code, and nothing outside it can name what it declares. Nothing outside a macro body
    /// can name what it declares either, because those declarations are local to each expansion
    /// and so none of them is a single name.
    /// </summary>
    public bool IsReachableByPath
    {
        get
        {
            for (var scope = this; scope.Kind != ScopeKind.File; scope = scope.Parent!)
            {
                if (scope.Name is null || scope.Kind == ScopeKind.Macro)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Returns the nearest scope of <paramref name="kind"/> from here outward, including this
    /// one, or null when no scope out to the file has that kind. Examples are the macro body that
    /// contains a name, or the repetition that contains a line.
    /// </summary>
    public Scope? Enclosing(ScopeKind kind)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope.Kind == kind)
                return scope;
        }
        return null;
    }

    /// <summary>
    /// Returns the nearest scope with a name, which is the routine or scope a name lives in.
    /// </summary>
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
    /// Returns the symbol <paramref name="name"/> means in this scope, without looking outward.
    /// </summary>
    public Symbol? FindMember(string name) => members.GetValueOrDefault(name);

    /// <summary>
    /// Returns the cheap local <paramref name="name"/> declared in this scope, without looking
    /// outward.
    /// </summary>
    public Symbol? FindCheapLocal(string name) => cheapLocals.GetValueOrDefault(name);

    /// <summary>
    /// Returns the symbol <paramref name="name"/> means, looking from here outward to the file. A
    /// nested scope can therefore name what its proc declares, and a proc can name what its file
    /// declares.
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
    /// Returns the cheap local <paramref name="name"/>, looking from here outward, so that a
    /// nested <c>.scope</c> can branch to its proc's <c>@done</c>.
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

    /// <summary>Returns the scope's kind and name, for debugging.</summary>
    public override string ToString() => Name is null ? Kind.ToString() : $"{Kind} {Name}";

    /// <summary>
    /// Adds <paramref name="symbol"/>, or returns the declaration already using that name.
    /// A name may be declared once in its scope, and cheap locals count separately. What a
    /// <see cref="ScopeKind.Branch"/> declares is also seen by the scopes around it, unless one of
    /// them already has the name from another branch of the same chain, which is no clash
    /// because only one of the two is ever expanded.
    /// </summary>
    internal Symbol? Declare(Symbol symbol)
    {
        var table = symbol.IsCheapLocal ? cheapLocals : members;
        if (table.TryGetValue(symbol.Name, out var existing))
            return existing;
        for (var branch = this; branch.Kind == ScopeKind.Branch && branch.Parent is { } around; branch = around)
        {
            var outer = symbol.IsCheapLocal ? around.cheapLocals : around.members;
            if (!outer.TryGetValue(symbol.Name, out var seen))
                continue;
            if (!Exclusive(seen.Scope, branch))
                return seen;
            break;
        }
        table.Add(symbol.Name, symbol);
        order.Add(symbol);
        for (var branch = this; branch.Kind == ScopeKind.Branch && branch.Parent is { } around; branch = around)
            (symbol.IsCheapLocal ? around.cheapLocals : around.members).TryAdd(symbol.Name, symbol);
        return null;

        // Two scopes are exclusive where each is inside a different branch of one chain.
        static bool Exclusive(Scope declared, Scope branch)
        {
            for (var at = declared; at is not null; at = at.Parent)
            {
                if (at.Parent == branch.Parent)
                    return at != branch && at.Kind == ScopeKind.Branch && at.Chain == branch.Chain;
            }
            return false;
        }
    }
}
