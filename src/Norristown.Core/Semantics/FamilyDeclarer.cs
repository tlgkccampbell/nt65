using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Declares the instances of each <see cref="Family"/> in one file, once every file of the
/// program has been collected. The binder hands over each family it finds as it walks the file,
/// and the instances are declared after the walk, because the enum a family walks may belong to
/// another module.
/// <para>
/// The instances are declared before any module exports, because what a module exports includes
/// them. The enum may be one that a <c>.use</c> brings in, so this reads the file's <c>.use</c>
/// items for itself, against the program as it will be once every module has exported. That
/// reading reports nothing and records no reference. The binder reads the items again, and
/// reports on them, once the program is complete.
/// </para>
/// </summary>
/// <param name="tree">The file whose families are declared.</param>
/// <param name="report">The action that reports a diagnostic in the file.</param>
/// <param name="touched">The action told each name looked for in another module.</param>
internal sealed class FamilyDeclarer(
    SyntaxTree tree,
    Action<TextSpan, DiagnosticMessage, RelatedSpan[]> report,
    Action<string?, string> touched)
{
    // The declarations named by a repetition's binding, waiting for the enum walked to be known.
    private readonly List<PendingFamily> pending = [];
    private readonly List<Family> declared = [];

    /// <summary>
    /// Gets a value indicating whether the file holds any declaration named by a repetition's
    /// binding that has not been declared yet.
    /// </summary>
    public bool HasPending => pending.Count > 0;

    /// <summary>Gets the families declared so far, in the order the file gives them.</summary>
    public IReadOnlyList<Family> Declared => declared;

    /// <summary>
    /// Records a declaration named by a repetition's binding, to be declared once the enum is
    /// known. A routine family gives the scope of its body, and a data family has no body to give.
    /// A repetition where no family may be declared has the declaration reported instead.
    /// </summary>
    /// <param name="each">The repetition the declaration is directly inside.</param>
    /// <param name="declaration">The statement that declares the family.</param>
    /// <param name="at">The span of the name the statement declares, which is where it is reported.</param>
    /// <param name="body">The scope of a routine family's body, or null for a data family.</param>
    /// <param name="kind">The kind of symbol each instance is.</param>
    /// <param name="signature">The signature each routine instance takes, or null.</param>
    /// <param name="data">The data each data instance lays out, or null.</param>
    /// <param name="type">The type that data names with <c>.type</c>, or null.</param>
    public void Add(
        Repeated each, StatementSyntax declaration, TextSpan at, Scope? body, SymbolKind kind,
        ProcSignatureSyntax? signature, DataDirectiveSyntax? data, NameExpressionSyntax? type)
    {
        if (each.Why is { } refused)
        {
            if (declaration is not MultiProcDeclarationSyntax)
                report(at, refused, []);
            return;
        }
        pending.Add(new PendingFamily(declaration, at, each, body, kind, signature, data, type));
    }

    /// <summary>
    /// Declares the instances of each family recorded so far, one declaration per member of the
    /// enum it walks, named after the member, in the scope around the repetition.
    /// </summary>
    /// <param name="provisional">
    /// The program as it will be once every module has exported, before any has.
    /// </param>
    /// <param name="uses">The file's <c>.use</c> items, in source order.</param>
    /// <param name="fileScope">The file's top-level scope.</param>
    /// <returns>
    /// Each instance declared, in order, with the span of the <c>.export</c> that exports it or
    /// null when nothing does.
    /// </returns>
    public IReadOnlyList<(Symbol Instance, TextSpan? ExportedAt)> Declare(
        ProgramSymbols provisional, IReadOnlyList<UseDirectiveSyntax> uses, Scope fileScope)
    {
        var instances = new List<(Symbol, TextSpan?)>();
        if (pending.Count == 0)
            return instances;

        var brought = new Dictionary<string, Resolution>(StringComparer.Ordinal);
        var globs = new List<ProgramSymbols.Module>();
        var paths = new NamePaths(provisional, brought, globs, touched, keepsTypes: false);
        foreach (var use in uses)
            Read(use, provisional, paths, fileScope, brought, globs);

        foreach (var family in pending)
            DeclareFamily(family, paths, instances);
        pending.Clear();
        return instances;
    }

    /// <summary>Returns how a message describes a symbol that is not the kind that was expected.</summary>
    private static string Named(Symbol symbol) =>
        symbol.Kind == SymbolKind.Enum ? "an anonymous enum, whose members are ordinary names" : $"a {symbol.KindText}";

    /// <summary>
    /// Reads one <c>.use</c> as the binder resolves it, recording what it brings in without
    /// reporting anything. A name the binder would refuse to bring in is left out here too.
    /// </summary>
    private void Read(
        UseDirectiveSyntax use, ProgramSymbols program, NamePaths paths, Scope fileScope,
        Dictionary<string, Resolution> brought, List<ProgramSymbols.Module> globs)
    {
        var path = use.Path.Names;
        if (path.Length == 0)
            return;
        var start = Lookup.ModuleRoot(path[0].Text, program);
        if (Lookup.Walk(start, [.. path.Select(part => part.Text)], program, paths.BodyOf, touched) is not { IsReported: false } target)
            return;

        if (use.StarToken is not null)
        {
            if (!use.IsExported && target.Module is { } name && program.ModuleNamed(name) is { } module)
                globs.Add(module);
            return;
        }
        if (use.Items.Count == 0)
        {
            BringIn(use.Alias ?? path[^1], target);
            return;
        }
        foreach (var item in use.Items)
        {
            if (paths.Step(target, item.Name.Text) is { } found)
                BringIn(item.Alias ?? item.Name, found);
        }

        // An exported `.use` of a module re-exports nothing, and a name the file also declares
        // is not brought in, so a lookup finds the declaration.
        void BringIn(SyntaxToken name, Resolution found)
        {
            if ((use.IsExported && found.Symbol is null) || fileScope.FindMember(name.Text) is not null)
                return;
            brought.TryAdd(name.Text, found);
        }
    }

    /// <summary>
    /// Declares one family by finding the enum it walks and making its declaration for each
    /// member.
    /// </summary>
    private void DeclareFamily(PendingFamily family, NamePaths paths, List<(Symbol, TextSpan?)> declaredInstances)
    {
        var walked = family.Each.Walked;
        var walkedText = walked?.GetText().Trim();
        var found = walked is null ? null : paths.NamedByPath(walked, family.Each.Around);
        if (found is not { Kind: SymbolKind.Enum, Body: { } members })
        {
            report(walked?.Span ?? family.At, Catalogue.FamilyNotOverAnEnum.Message(
                walkedText, found is null ? "is not declared" : $"is {Named(found)}"), []);
            return;
        }

        var instances = new List<(Symbol Member, Symbol Instance)>();
        foreach (var member in members.Symbols.Where(symbol => symbol.IsEnumMember))
        {
            var instance = DeclareInstance(family, member);
            instances.Add((member, instance));
            declaredInstances.Add((instance, family.Declaration.ExportToken?.Span));
        }

        // The family's line contains the repetition's binding, and that is the name declared
        // there. Each instance is looked up by its own member name, but its declaration span is
        // that line, which is where go-to-definition lands. References may not overlap, so no
        // reference to an instance is recorded at that line. A routine family's body belongs to
        // its first instance, and a data family has no body.
        if (instances.Count > 0 && family.Body is { } body)
            body.Owner = instances[0].Instance;
        declared.Add(new Family(family.Declaration, family.Each.Block, family.Each.Binding, found, instances));
    }

    /// <summary>Declares one instance of a family under its member's name.</summary>
    private Symbol DeclareInstance(PendingFamily family, Symbol member)
    {
        var around = family.Each.Around;
        var instance = new Symbol(member.Name, family.Kind, around, tree, family.At)
        {
            Segment = family.Each.Segment,
            Data = family.Data,
            TypeExpression = family.Type,
        };
        if (family.Kind == SymbolKind.Proc)
            instance.Signature = Signature.Read(family.Signature);

        // The signature may name the binding, as in `dbr = Bank::b`, and each instance's
        // signature is that expression with its own member's value.
        instance.Bound = (family.Each.Binding, new Expansion.Bound(member.Value, null, Member: member));
        if (around.Declare(instance) is { } existing)
        {
            report(family.At, Catalogue.FamilyMemberCollides.Message(member.Name, family.Each.Walked?.GetText().Trim()),
                [new RelatedSpan(existing.DeclarationSpan, "declared here")]);
        }
        return instance;
    }

    /// <summary>
    /// Represents a repetition whose body is being read, with what a declaration named after its
    /// binding needs to know. This is what it walks, where the declarations would go, and what is
    /// wrong with that location when something is.
    /// </summary>
    /// <param name="Block">The repetition's block, which each iteration expands.</param>
    /// <param name="Walked">The enum or list the repetition walks.</param>
    /// <param name="Binding">The name it binds, which the declarations are named from.</param>
    /// <param name="Around">The scope the declarations go in, which is the one around the repetition.</param>
    /// <param name="Segment">The segment that scope is putting declarations in.</param>
    /// <param name="Why">Why a declaration named after the binding is not allowed here, or null when it is.</param>
    public sealed record Repeated(
        BlockSyntax Block, ExpressionSyntax? Walked, Symbol Binding, Scope Around, string? Segment,
        DiagnosticMessage? Why);

    /// <summary>
    /// Represents a declaration named by a repetition's binding, waiting for the enum it walks to
    /// be known.
    /// </summary>
    private sealed record PendingFamily(
        StatementSyntax Declaration, TextSpan At, Repeated Each, Scope? Body, SymbolKind Kind,
        ProcSignatureSyntax? Signature, DataDirectiveSyntax? Data, NameExpressionSyntax? Type);
}
