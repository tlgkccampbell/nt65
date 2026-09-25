using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Resolves every name in a file. This half of the binder runs once the whole program has been
/// read. The walk over the file has finished, so nothing here depends on how far it had
/// progressed. A name is resolved from the scopes around it, the names the file's <c>.use</c>
/// items brought in and the other modules.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>
    /// Records a name that a macro body uses without declaring it or receiving it as a
    /// parameter. Every expansion needs that name, regardless of which file calls the macro, so
    /// the macro keeps a list of such names. The calling file brings them in, and an exported
    /// macro may only use names that are also exported.
    /// </summary>
    /// <returns>True if the name appears in a macro body.</returns>
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

    private void ResolveUses(IReadOnlyList<Use> list)
    {
        Resolution? previous = null;
        var broken = false;
        var steps = new List<(int Reference, Scope At, Symbol Symbol, SyntaxToken Token)>();
        foreach (var use in list)
        {
            var token = use.Token;
            var at = use.Scope;
            if (use.First)
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
            previous = use.Chosen ? Unreported(use, previous) : Resolve(use, previous);
            if (previous is not { IsReported: false } place)
            {
                broken = true;
                continue;
            }
            if (place.Symbol is not { } symbol)
            {
                if (use.Last)
                {
                    Report(token.Span, Catalogue.ModuleUsedAsAName.Message(place.Module, place.Module));
                    broken = true;
                }
                continue;
            }
            var inMacro = RecordBodyUse(at, symbol, token, use.Last);
            if (!use.Last)
                steps.Add((references.Count, at, symbol, token));
            references.Add(new SymbolReference(symbol, token.Span, false, place.IsAlias, IsStep: !use.Last, InMacro: inMacro));

            // A path to a member is an offset into the symbols it walks through, so those count
            // as used too: `oam::x` is the address of `oam` plus the offset of `x`.
            if (use.Last && symbol.Kind == SymbolKind.Member)
            {
                foreach (var step in steps)
                {
                    references[step.Reference] = references[step.Reference] with { IsStep = false };
                    RecordBodyUse(step.At, step.Symbol, step.Token, last: true);
                }
            }
            if (use.Splice && symbol.Parameter is not { Kind: ParameterKind.Block })
            {
                Report(token.Span, Catalogue.NameAloneOnALine.Message(token.Text, symbol.KindPhrase));
            }
        }
    }

    /// <summary>
    /// Resolves one part of a name. <paramref name="previous"/> is what the part before it
    /// resolved to, so that a path walks into a scope or a module instead of looking outward
    /// again.
    /// </summary>
    private Resolution? Resolve(Use use, Resolution? previous)
    {
        var token = use.Token;
        var at = use.Scope;
        if (token.Kind == SyntaxKind.CheapLocal)
        {
            if (use.Path)
            {
                Report(token.Span, Catalogue.CheapLocalInAPath.Message(token.Text));
                return null;
            }
            var local = at.LookupCheapLocal(token.Text[1..]);
            if (local is null)
            {
                var near = NearestName(at, token.Text[1..], cheap: true);
                Report(token.Span, Catalogue.NotDeclared.Message(token.Text, Lookup.Suggesting(near is null ? null : "@" + near)));
                if (near is not null)
                    Fixed(new DiagnosticFix(FixKind.NearestName, "@" + near));
            }
            return local is null ? null : new Resolution(local);
        }

        if (!use.Path)
        {
            // A register parses as a name so that a macro body may pass it as a word. Outside a
            // macro body it can only be a mistake, and reporting that it is a register is more
            // helpful than reporting that the name is not declared. A register that was declared
            // anyway, which is an error, has already been reported at its declaration.
            if (at.Lookup(token.Text) is { } symbol)
                return new Resolution(symbol);
            if (!use.Word && !CheckReservedWord(token))
                return null;
            if (Outside(token, use.Last, report) is { } found)
                return found;

            // In a condition a bare name may be a word rather than a name at all, and a word
            // is compared, never looked up.
            if (!use.Word)
                ReportUndeclared(token, use.Last, at);
            return null;
        }

        // A leading `::` starts at the root of the modules.
        if (previous is not { } before)
            return ModuleRoot(token, report);
        if (before.Module is { } prefix)
            return InModule(token, prefix, use.Last, report);

        // For a part after `::`, the scope to look in is the one the part before it opened.
        var container = paths.BodyOf(before.Symbol!);
        if (container is null)
        {
            if (before.Symbol is { Kind: SymbolKind.AddressAlias, ValueExpression.Parent: DataDeclarationSyntax { Directive: null } })
                Report(token.Span, Catalogue.FieldsNeedAStatedType.Message(before.Symbol.DisplayName));
            else
                Report(token.Span, Catalogue.NotAScope.Message(before.Symbol!.DisplayName, before.Symbol.KindPhrase));
            return null;
        }

        var member = container.FindMember(token.Text);
        if (member is null)
        {
            // A repetition's binding at the end of a path refers to the member of that scope
            // named by the binding's current value, which is a different member on each
            // iteration, so each iteration resolves it.
            if (use.Last && at.Lookup(token.Text) is { Kind: SymbolKind.Binding } binding)
                return new Resolution(binding);
            var near = Spelling.Nearest(token.Text, Lookup.Members(container));
            Report(token.Span, Catalogue.NotDeclaredIn.Message(token.Text, $"`{container.Name}`", Lookup.Suggesting(near)));
            if (near is not null)
                Fixed(new DiagnosticFix(FixKind.NearestName, near));
            return null;
        }
        return new Resolution(CheckExported(token, member, use.Last));
    }

    /// <summary>
    /// Resolves one part of a name as <see cref="Resolve(Use, Resolution?)"/> does, with the
    /// diagnostics it reports going to a list that is then dropped. Everything else that
    /// resolving it records is kept, except that a name it finds unexported is not marked as
    /// reported.
    /// </summary>
    private Resolution? Unreported(Use use, Resolution? previous)
    {
        var kept = diagnostics;
        diagnostics = [];
        dropping = true;
        try
        {
            return Resolve(use, previous);
        }
        finally
        {
            diagnostics = kept;
            dropping = false;
        }
    }

    /// <summary>
    /// Resolves a name that the scopes around it do not declare. The name may be one that a
    /// <c>.use</c> brought in, the first part of a module's path, or one that a
    /// <c>.use module::*</c> brought in.
    /// </summary>
    private Resolution? Outside(SyntaxToken token, bool last, Action<TextSpan, DiagnosticMessage>? report) =>
        Lookup.Outside(token.Text, last, program, used, globs, Touch, At(token, report));

    /// <summary>
    /// Reports a name that no scope or <c>.use</c> declares, naming the modules that
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
            ? Catalogue.DeclaredInAnotherModule.Message(
                token.Text, exporting[0], exporting[0], token.Text, exporting[0], token.Text)
            : last
                ? Catalogue.NotDeclared.Message(token.Text, Lookup.Suggesting(nearest))
                : Catalogue.ModuleNotInTheBuild.Message(token.Text, token.Text));
        if (exporting.Count > 0)
            Fixed(new DiagnosticFix(FixKind.Use, $"{exporting[0]}::{token.Text}"));
        else if (nearest is not null)
            Fixed(new DiagnosticFix(FixKind.NearestName, nearest));
    }

    /// <summary>
    /// Returns the declared name that <paramref name="typed"/> is most likely a misspelling of.
    /// The candidate is a name in scope, or one that a <c>.use</c> brought in, that differs from
    /// it by a letter or two.
    /// </summary>
    private string? NearestName(Scope at, string typed, bool cheap) =>
        Spelling.Nearest(typed, Candidates(at, cheap));

    /// <summary>
    /// Returns the names a misspelling could have meant, which are the names in the enclosing
    /// scopes and the names a <c>.use</c> brought in.
    /// </summary>
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
    /// Returns the callback through which a reporting lookup reports, or null when
    /// <paramref name="report"/> is null. The callback puts each message on the token that was
    /// looked up and records the fix the message suggests, if any.
    /// </summary>
    private Action<DiagnosticMessage, DiagnosticFix?>? At(SyntaxToken token, Action<TextSpan, DiagnosticMessage>? report) =>
        report is null ? null : (message, fix) =>
        {
            report(token.Span, message);
            if (fix is { } suggested)
                Fixed(suggested);
        };

    /// <summary>Resolves the first part of a path that starts at the root of the modules.</summary>
    private Resolution? ModuleRoot(SyntaxToken token, Action<TextSpan, DiagnosticMessage>? report) =>
        Lookup.ModuleRoot(token.Text, program, At(token, report));

    /// <summary>
    /// Resolves the part after <paramref name="prefix"/>, which is a module or the start of a
    /// module's name.
    /// </summary>
    private Resolution? InModule(SyntaxToken token, string prefix, bool last, Action<TextSpan, DiagnosticMessage>? report)
    {
        var found = Lookup.InModule(token.Text, prefix, program, Touch, At(token, report));
        return found is { Symbol: { } member } && report is not null
            ? new Resolution(CheckExported(token, member, last))
            : found;
    }

    /// <summary>
    /// Records that resolving this file looked for <paramref name="name"/> in
    /// <paramref name="module"/>.
    /// </summary>
    private void Touch(string? module, string name) => lookedUp.Add(new LookedUpName(module, name));

    /// <summary>
    /// Reports a symbol that another module declares but does not export, since such a symbol
    /// may not be named. The check applies to the last part of a name, so <c>hw::outer::inner</c>
    /// needs <c>inner</c> exported, and <c>outer</c> is only the way in. The symbol is returned
    /// either way, so that an editor can still go to a declaration that is private rather than
    /// missing.
    /// </summary>
    private Symbol CheckExported(SyntaxToken token, Symbol symbol, bool last)
    {
        // Reported once, where the file first names the symbol, because every other use is the
        // same mistake. A report that is being dropped does not count, or the use that should be
        // reported later would not be.
        if (!last || symbol.Tree == tree || symbol.IsExported || dropping || !unexported.Add(symbol))
            return symbol;
        Report(token.Span, Catalogue.NotExported.Message(symbol.PathName, symbol.Module),
            new RelatedSpan(symbol.DeclarationSpan, "declared here"));
        Fixed(new DiagnosticFix(FixKind.Export, symbol.QualifiedName, symbol.DeclarationSpan));
        return symbol;
    }

    /// <summary>
    /// Resolves a <c>.use</c>, including its path from the root of the modules and each name it
    /// brings in. A name it brings in may not also be declared in the module, because otherwise
    /// the declaration a use of that name meant would depend on a rule rather than on the source.
    /// </summary>
    private void ResolveUse(UseDirectiveSyntax statement)
    {
        var path = statement.Path.Names;
        var glob = statement.StarToken is not null;
        var items = statement.Items;
        var alias = statement.Alias;
        if (path.Length == 0)
            return;
        Resolution? place = null;
        for (var i = 0; i < path.Length && (i == 0 || place is not null); i++)
        {
            var last = i == path.Length - 1 && !glob && items.Count == 0;
            place = Resolve(new Use(path[i], fileScope, Path: true, First: i == 0, Last: last), place);
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
                Report(path[^1].Span, Catalogue.UseStarNotAModule.Message(
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
        foreach (var useItem in items)
        {
            var name = useItem.Name;
            var itemAlias = useItem.Alias;
            if (Resolve(new Use(name, fileScope, Path: true, First: false, Last: true), target) is not { } item)
                continue;
            if (item.Symbol is { } symbol)
                references.Add(new SymbolReference(symbol, name.Span, false, InUse: true));
            BringIn(itemAlias ?? name, item, itemAlias is not null, statement.IsExported);
        }
    }

    /// <summary>Brings in one name from a <c>.use</c>, under the name that <paramref name="name"/> gives.</summary>
    private void BringIn(SyntaxToken name, Resolution target, bool renamed, bool exported)
    {
        if (exported && target.Symbol is null)
        {
            Report(name.Span, Catalogue.ReexportModule.Message(target.Module));
            return;
        }
        if (renamed && target.Symbol is { } symbol)
            references.Add(new SymbolReference(symbol, name.Span, true, IsAlias: true, InUse: true));
        if (fileScope.FindMember(name.Text) is { } local)
        {
            Report(name.Span, Catalogue.UseCollidesWithDeclaration.Message(
                name.Text), new RelatedSpan(local.DeclarationSpan, "declared here"));
            return;
        }
        if (!used.TryAdd(name.Text, target))
        {
            Report(name.Span, Catalogue.UseBringsInTwice.Message(name.Text));
            return;
        }
        broughtAt[name.Text] = (name.Span, exported);
    }
}
