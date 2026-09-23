using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What every name a file writes refers to. This half of the binder runs once the whole
/// program has been read: the walk over the file has finished, so nothing here depends on how
/// far it had got, and a name is resolved from the scopes around it, what the file's
/// <c>.use</c> items brought in, the defines and the other modules.
/// </summary>
internal sealed partial class Binder
{
    private void ResolveUses(IReadOnlyList<Use> list)
    {
        Place? previous = null;
        var broken = false;
        var steps = new List<(int Reference, Scope At, Symbol Symbol, SyntaxToken Token)>();
        foreach (var use in list)
        {
            var (token, at, _, first, last, splice, _, _) = use;
            if (first)
            {
                previous = null;
                broken = false;
                steps.Clear();
            }
            else if (broken)
            {
                // The part before this one did not resolve, and has been reported. The rest
                // of the path cannot be resolved either, and is not reported as wrong.
                continue;
            }

            // A name in one of a `.select`'s values only has to resolve if the condition
            // chooses that value, so its diagnostics are dropped here and evaluation reports it.
            var reported = diagnostics.Count;
            previous = Resolve(use, previous);
            if (use.Chosen)
                diagnostics.RemoveRange(reported, diagnostics.Count - reported);
            if (previous is not { IsReported: false } place)
            {
                broken = true;
                continue;
            }
            if (place.Symbol is not { } symbol)
            {
                if (last)
                {
                    Report(token.Span, Catalogue.ModuleUsedAsAName.Says(place.Module, place.Module));
                    broken = true;
                }
                continue;
            }
            var inMacro = RecordBodyUse(at, symbol, token, last);
            if (!last)
                steps.Add((references.Count, at, symbol, token));
            references.Add(new SymbolReference(symbol, token.Span, false, place.IsAlias, IsStep: !last, InMacro: inMacro));

            // A path to a member is an offset into the symbols it walks through, so those count
            // as used too: `oam::x` is the address of `oam` plus the offset of `x`.
            if (last && symbol.Kind == SymbolKind.Member)
            {
                foreach (var step in steps)
                {
                    references[step.Reference] = references[step.Reference] with { IsStep = false };
                    RecordBodyUse(step.At, step.Symbol, step.Token, last: true);
                }
            }
            if (splice && symbol.Parameter is not { Kind: ParameterKind.Block })
            {
                Report(token.Span, Catalogue.NameAloneOnALine.Says(token.Text, symbol.KindPhrase));
            }
        }
    }

    /// <summary>
    /// Records a name a macro body uses that it neither declared nor was given as a parameter.
    /// Every expansion needs that name, whichever file the macro is called from, so the macro
    /// keeps a list of them: the calling file brings them in, and an exported macro may only
    /// use names that are exported too.
    /// </summary>
    /// <returns>Whether the name is written in a macro body.</returns>
    private static bool RecordBodyUse(Scope at, Symbol used, SyntaxToken token, bool last)
    {
        var body = at.Enclosing(ScopeKind.Macro);
        if (body?.Owner is not { } macro)
            return false;

        // Names the body declares, and its parameters, are part of the macro and need no record.
        // Only the last part of a path is recorded; the steps before it are only walked through.
        for (var owner = used.Scope; owner is not null; owner = owner.Parent)
        {
            if (owner == body)
                return true;
        }
        if (last && !macro.Uses.Any(seen => seen.Used == used))
            macro.AddUse(used, token.Parent.Tree.GetSpan(token.Span));
        return true;
    }

    /// <summary>
    /// What one part of a written name means. <paramref name="previous"/> is what the part
    /// before it resolved to, so a path walks into a scope or a module instead of looking
    /// outward again.
    /// </summary>
    private Place? Resolve(Use use, Place? previous)
    {
        var (token, at, path, _, last, _, word, _) = use;
        if (token.Kind == SyntaxKind.CheapLocal)
        {
            if (path)
            {
                Report(token.Span, Catalogue.CheapLocalInAPath.Says(token.Text));
                return null;
            }
            var local = at.LookupCheapLocal(token.Text[1..]);
            if (local is null)
            {
                var near = NearestName(at, token.Text[1..], cheap: true);
                Report(token.Span, Catalogue.NotDeclared.Says(token.Text, Lookup.Suggesting(near is null ? null : "@" + near)));
                if (near is not null)
                    Fixed(new DiagnosticFix(FixKind.NearestName, "@" + near));
            }
            return local is null ? null : new Place(local);
        }

        if (!path)
        {
            // A register parses as a name so that a macro body may pass it as a word. Outside
            // one it can only be a mistake, and saying that it is a register beats saying the
            // name is not declared. A register that was declared anyway — which is an error —
            // has been reported where it was declared.
            if (at.Lookup(token.Text) is { } symbol)
                return new Place(symbol);
            if (!word && !CheckReservedWord(token))
                return null;
            if (Outside(token, last, report) is { } found)
                return found;

            // In a condition a bare name may be a word rather than a name at all, and a word
            // is compared, never looked up.
            if (!word)
                ReportUndeclared(token, last, at);
            return null;
        }

        // A leading `::` starts at the root of the modules.
        if (previous is not { } before)
            return ModuleRoot(token, report);
        if (before.Module is { } prefix)
            return InModule(token, prefix, last, report);

        // A part after `::`: the scope to look in is the one the part before it opened.
        var container = BodyOf(before.Symbol!);
        if (container is null)
        {
            Report(token.Span, Catalogue.NotAScope.Says(before.Symbol!.DisplayName, before.Symbol.KindPhrase));
            return null;
        }

        var member = container.FindMember(token.Text);
        if (member is null)
        {
            // A repetition's binding at the end of a path refers to the member of that scope
            // named by the binding's current value, which is a different member on each
            // iteration, so each iteration resolves it.
            if (last && at.Lookup(token.Text) is { Kind: SymbolKind.Binding } binding)
                return new Place(binding);
            var near = Spelling.Nearest(token.Text, Lookup.Members(container));
            Report(token.Span, Catalogue.NotDeclaredIn.Says(token.Text, $"`{container.Name}`", Lookup.Suggesting(near)));
            if (near is not null)
                Fixed(new DiagnosticFix(FixKind.NearestName, near));
            return null;
        }
        return new Place(CheckExported(token, member, last));
    }

    /// <summary>
    /// What a name the scopes around it do not declare means: what a <c>.use</c> brought in, a
    /// define, the first part of a module's path, or what a <c>.use module::*</c> brought in.
    /// </summary>
    private Place? Outside(SyntaxToken token, bool last, Action<TextSpan, DiagnosticMessage>? report) =>
        Lookup.Outside(token.Text, last, program, used, globs, Touch, At(token, report));

    /// <summary>
    /// Reports a name that no scope, <c>.use</c> or define declares, naming the modules that
    /// export a name spelt the same. A name that starts a path (<paramref name="last"/> is
    /// false) is most likely a module the build does not have, such as one left off the
    /// command line.
    /// </summary>
    private void ReportUndeclared(SyntaxToken token, bool last, Scope at)
    {
        lookedUp.Add(new LookedUpName(null, token.Text));
        var exporting = program.ModulesExporting(token.Text).ToList();
        var nearest = exporting.Count == 0 && last ? NearestName(at, token.Text, cheap: false) : null;
        Report(token.Span, exporting.Count > 0
            ? Catalogue.DeclaredInAnotherModule.Says(
                token.Text, exporting[0], exporting[0], token.Text, exporting[0], token.Text)
            : last
                ? Catalogue.NotDeclared.Says(token.Text, Lookup.Suggesting(nearest))
                : Catalogue.ModuleNotInTheBuild.Says(token.Text, token.Text));
        if (exporting.Count > 0)
            Fixed(new DiagnosticFix(FixKind.Use, $"{exporting[0]}::{token.Text}"));
        else if (nearest is not null)
            Fixed(new DiagnosticFix(FixKind.NearestName, nearest));
    }

    /// <summary>
    /// The declared name that <paramref name="written"/> is most likely a misspelling of: one in
    /// scope, or one a <c>.use</c> brought in, that differs from it by a letter or two.
    /// </summary>
    private string? NearestName(Scope at, string written, bool cheap) =>
        Spelling.Nearest(written, Candidates(at, cheap));

    /// <summary>The names a misspelling could have meant: what the scopes around it hold, and what a <c>.use</c> named.</summary>
    private IEnumerable<string> Candidates(Scope at, bool cheap)
    {
        for (var scope = at; scope is not null; scope = scope.Parent)
        {
            foreach (var symbol in scope.Symbols)
            {
                if (symbol.IsCheapLocal == cheap)
                    yield return symbol.Name;
            }
        }
        if (!cheap)
        {
            foreach (var name in used.Keys)
                yield return name;
        }
    }

    /// <summary>
    /// The callback a reporting lookup reports through: it puts each message on the token that
    /// was looked up, and records the fix the message suggests, if any.
    /// </summary>
    private Action<DiagnosticMessage, DiagnosticFix?>? At(SyntaxToken token, Action<TextSpan, DiagnosticMessage>? report) =>
        report is null ? null : (message, fix) =>
        {
            report(token.Span, message);
            if (fix is { } written)
                Fixed(written);
        };

    /// <summary>The first part of a path written from the root of the modules.</summary>
    private Place? ModuleRoot(SyntaxToken token, Action<TextSpan, DiagnosticMessage>? report) =>
        Lookup.ModuleRoot(token.Text, program, At(token, report));

    /// <summary>The part after <paramref name="prefix"/>, which is a module or the start of one's name.</summary>
    private Place? InModule(SyntaxToken token, string prefix, bool last, Action<TextSpan, DiagnosticMessage>? report)
    {
        var found = Lookup.InModule(token.Text, prefix, program, Touch, At(token, report));
        return found is { Symbol: { } member } && report is not null
            ? new Place(CheckExported(token, member, last))
            : found;
    }

    /// <summary>Remembers that resolving this file looked for <paramref name="name"/> in <paramref name="module"/>.</summary>
    private void Touch(string? module, string name) => lookedUp.Add(new LookedUpName(module, name));

    /// <summary>
    /// A symbol another module declares may only be named if that module exports it. The
    /// check is on the last part of a name: <c>hw::outer::inner</c> needs <c>inner</c>
    /// exported, and <c>outer</c> is only the way in. The symbol is returned either way, so an
    /// editor can still go to a declaration that is private rather than missing.
    /// </summary>
    private Symbol CheckExported(SyntaxToken token, Symbol symbol, bool last)
    {
        // Said once, where the file first names it: every other use is the same mistake.
        if (!last || symbol.Tree == tree || symbol.IsExported || symbol.IsDefine || !unexported.Add(symbol))
            return symbol;
        Report(token.Span, Catalogue.NotExported.Says(symbol.PathName, symbol.Module),
            new RelatedSpan(symbol.DeclarationSpan, "declared here"));
        Fixed(new DiagnosticFix(FixKind.Export, symbol.QualifiedName, symbol.DeclarationSpan));
        return symbol;
    }

    /// <summary>
    /// The scope a path can look into after <paramref name="symbol"/>, while the file is being
    /// bound: the scope a routine or a scope opens, or the one belonging to the type a member or
    /// a data declaration names, which is what makes the fields of <c>.type T</c> data reachable
    /// through it. The type is resolved here, on demand, because nothing has been evaluated yet.
    /// </summary>
    private Scope? BodyOf(Symbol symbol)
    {
        if (Lookup.BodyOf(symbol) is { } known)
            return known;
        if (symbol.Kind == SymbolKind.Macro || symbol.TypeExpression is null || !resolving.Add(symbol))
            return null;
        var type = TypeOf(symbol);
        resolving.Remove(symbol);
        return type?.Body;
    }

    /// <summary>
    /// The type a <c>.type</c> names, resolved from where it was written. This runs on demand
    /// rather than in order, because a name may reach into a type the file declares later.
    /// Nothing is reported from here: the names in the type are uses like any others, and are
    /// reported where they are resolved.
    /// </summary>
    private Symbol? TypeOf(Symbol symbol)
    {
        if (symbol.Type is { } known)
            return known;
        if (symbol.TypeExpression is not NameExpressionSyntax named)
            return null;

        Place? part = null;
        var path = named.GlobalToken is not null;
        var parts = named.Parts;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Name is not { IsMissing: false } token)
                break;
            var last = i == parts.Count - 1;
            part = !path ? (symbol.Scope.Lookup(token.Text) is { } local ? new Place(local) : Outside(token, last, null))
                : part is null ? ModuleRoot(token, null)
                : part.Value.Module is { } prefix ? InModule(token, prefix, last, null)
                : BodyOf(part.Value.Symbol!)?.FindMember(token.Text) is { } member ? new Place(member)
                : null;
            path = true;
            if (part is null or { IsReported: true })
                return null;
        }
        symbol.Type = part?.Symbol;
        return symbol.Type;
    }

    /// <summary>
    /// Resolves a <c>.use</c>: its path from the root of the modules, and each name it brings
    /// in. A name it brings in may not also be declared in the module, because then which one a
    /// use of it meant would depend on a rule rather than on what is written.
    /// </summary>
    private void ResolveUse(UseDirectiveSyntax statement)
    {
        var path = statement.Path.Names;
        var glob = statement.StarToken is not null;
        var items = statement.Items;
        var alias = statement.Alias;
        if (path.Length == 0)
            return;
        Place? place = null;
        for (var i = 0; i < path.Length && (i == 0 || place is not null); i++)
        {
            var last = i == path.Length - 1 && !glob && items.Count == 0;
            place = i == 0 ? ModuleRoot(path[i], report)
                : place!.Value.Module is { } prefix ? InModule(path[i], prefix, last, report)
                : BodyOf(place.Value.Symbol!)?.FindMember(path[i].Text) is { } member ? new Place(CheckExported(path[i], member, last))
                : NotIn(path[i], place.Value.Symbol!);
            if (place?.Symbol is { } symbol)
                references.Add(new SymbolReference(symbol, path[i].Span, false, InUse: true));
        }
        if (place is not { } target)
            return;

        if (glob)
        {
            if (statement.IsExported)
            {
                Report(statement.Span, Catalogue.ReexportStar);
            }
            else if (target.Module is { } name && program.ModuleNamed(name) is { } module)
            {
                globs.Add(module);
            }
            else
            {
                Report(path[^1].Span, Catalogue.UseStarNotAModule.Says(
                    target.Module ?? target.Symbol!.PathName,
                    path[^1].Text,
                    (target.Module is null ? "not a module" : "only the start of a module's name")));
            }
            return;
        }
        if (items.Count == 0)
        {
            BringIn(alias ?? path[^1], target, alias is not null, statement.IsExported);
            return;
        }
        foreach (var written in items)
        {
            var name = written.Name;
            var itemAlias = written.Alias;
            var found = target.Module is { } prefix ? InModule(name, prefix, last: true, report)
                : BodyOf(target.Symbol!)?.FindMember(name.Text) is { } member ? new Place(CheckExported(name, member, last: true))
                : NotIn(name, target.Symbol!);
            if (found is not { } item)
                continue;
            if (item.Symbol is { } symbol)
                references.Add(new SymbolReference(symbol, name.Span, false, InUse: true));
            BringIn(itemAlias ?? name, item, itemAlias is not null, statement.IsExported);
        }
    }

    /// <summary>A part of a <c>.use</c> path that names nothing in the symbol before it.</summary>
    private Place? NotIn(SyntaxToken token, Symbol container)
    {
        Report(token.Span, container.Body is null && container.TypeExpression is null
            ? Catalogue.NotAScope.Says(container.DisplayName, container.KindPhrase)
            : Catalogue.NotDeclaredIn.Says(token.Text, $"`{container.DisplayName}`", ""));
        return null;
    }

    /// <summary>One name a <c>.use</c> brings in, under the name <paramref name="name"/> writes.</summary>
    private void BringIn(SyntaxToken name, Place target, bool renamed, bool exported)
    {
        if (exported && target.Symbol is null)
        {
            Report(name.Span, Catalogue.ReexportModule.Says(target.Module));
            return;
        }
        if (renamed && target.Symbol is { } symbol)
            references.Add(new SymbolReference(symbol, name.Span, true, IsAlias: true, InUse: true));
        if (fileScope.FindMember(name.Text) is { } local)
        {
            Report(name.Span, Catalogue.UseCollidesWithDeclaration.Says(
                name.Text), new RelatedSpan(local.DeclarationSpan, "declared here"));
            return;
        }
        if (!used.TryAdd(name.Text, target))
        {
            Report(name.Span, Catalogue.UseBringsInTwice.Says(name.Text));
            return;
        }
        broughtAt[name.Text] = (name.Span, exported);
    }
}
