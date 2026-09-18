using System.Globalization;
using System.Text;
using Norristown.Flow;
using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The analysis as the protocol spells it. Everything the server sends is built here, so
/// the analysis stays free of LSP and this file holds all of the 0-based counting.
/// <para>
/// Protocol types are written out in full, because several of them share a name with
/// something in the analysis core (<c>Diagnostic</c>, <c>SymbolKind</c>) or in the framework
/// (<c>Range</c>).
/// </para>
/// </summary>
internal static class Lsp
{
    /// <summary>What the client shows as the origin of every diagnostic nt65 reports.</summary>
    private const string SourceName = "nt65";

    /// <summary>
    /// Everything wrong with one file, and the branches this build leaves out. An omitted
    /// branch is not a problem, so it is a hint the client renders faded rather than
    /// anything that appears in a problem list.
    /// </summary>
    public static IReadOnlyList<Protocol.Diagnostic> ToDiagnostics(
        IEnumerable<Diagnostic> diagnostics, SyntaxTree? tree, Configuration configuration) =>
        [
            .. diagnostics.Select(ToDiagnostic),
            .. (tree is null ? [] : configuration.Omitted(tree)).Select(span => new Protocol.Diagnostic(
                ToRange(tree!, span),
                Protocol.DiagnosticSeverity.Hint,
                SourceName,
                "the build configuration leaves this branch out",
                null,
                [Protocol.DiagnosticTag.Unnecessary])),
        ];

    /// <summary>The file's outline, nested the way its blocks are.</summary>
    public static IReadOnlyList<Protocol.DocumentSymbol> ToSymbols(SyntaxTree tree) =>
        ToSymbols(tree, Outline.Build(tree));

    /// <summary>One foldable range per block.</summary>
    public static IReadOnlyList<Protocol.FoldingRange> ToFoldingRanges(SyntaxTree tree) =>
        [.. Folding.Build(tree).Select(range => new Protocol.FoldingRange(range.StartLine, range.EndLine))];

    /// <summary>
    /// What to show at <paramref name="position"/>: the name under the caret, or, where
    /// there is none, how long the instruction on that line takes, how long the block
    /// around it takes, and on the 65816 the processor state that reaches it.
    /// </summary>
    public static Protocol.Hover? ToHover(
        ProgramModel program, SemanticModel model, CodeLayout? layout, ControlFlow? flow, StateAnalysis? states,
        int position)
    {
        if (model.ReferenceAt(position) is { } reference)
        {
            // The declaration as the program has it now: an edit that leaves what other files
            // see of a file alone keeps their models, and with them the symbols they resolved
            // to, whose file is the one from before the edit.
            var symbol = program.Current(reference.Symbol);
            return new Protocol.Hover(
                Protocol.MarkupContent.Markdown(
                    Describe(symbol, model.Tree) + Declares(model, reference)
                    + Preserves(flow, reference) + Documented(symbol)),
                ToRange(model.Tree, reference.Span));
        }
        return ToScope(model, flow, position) ?? ToTiming(model, layout, flow, states, position);
    }

    /// <summary>
    /// What a routine hands back, where the caret is on the line that declares it. A lens says
    /// the same thing above the line, and a lens is something an editor can be told not to
    /// show, so the hover does not leave it to one.
    /// </summary>
    private static string Preserves(ControlFlow? flow, SymbolReference reference)
    {
        if (!reference.IsDeclaration || flow is null)
            return "";
        var found = flow.Regions
            .Where(region => region.Routine.NameSpan == reference.Symbol.NameSpan && region.Total.Ends)
            .Select(region => (region.Routine.Name, Text: Spell(region.Registers.Kept, region.Registers.Complete)))
            .ToList();
        if (found.Count == 0)
            return "";

        // Every instance of a family is declared on one line. Where they all hand back the
        // same, the line says it once.
        var texts = found.Select(region => region.Text).Distinct(StringComparer.Ordinal).ToList();
        return texts.Count == 1
            ? $"\n- {texts[0]}"
            : string.Concat(found.Select(region => $"\n- {region.Name}: {region.Text}"));
    }

    /// <summary>
    /// What an inline <c>.scope</c> block costs and hands on, where the caret is on the line
    /// that opens it. The block has no name to hover, so this is the only place to say it
    /// other than the lens.
    /// </summary>
    private static Protocol.Hover? ToScope(SemanticModel model, ControlFlow? flow, int position)
    {
        foreach (var region in flow?.Regions ?? [])
        {
            foreach (var scope in region.ScopeRegisters)
            {
                if (position < scope.Opener.Start || position >= scope.Opener.End)
                    continue;
                var cost = region.Scopes.FirstOrDefault(costed => costed.Opener == scope.Opener).Cost;
                var text = cost is { Least: { } least }
                    ? $"**{Spell(new CycleCount(least, cost.Most ?? least))}**\n\n{Spell(scope.Kept, scope.Complete)}"
                    : Spell(scope.Kept, scope.Complete);
                return new Protocol.Hover(
                    Protocol.MarkupContent.Markdown(text), ToRange(model.Tree, scope.Opener));
            }
        }
        return null;
    }

    /// <summary>The comment written above the declaration, which is what its author had to say.</summary>
    private static string Documented(Symbol symbol) =>
        DocComments.Of(symbol) is { } written ? $"\n\n{written}" : "";

    /// <summary>
    /// What a family declares, for the name its repetition binds: the instances it stands for,
    /// which is what the line the caret is on is worth knowing.
    /// </summary>
    private static string Declares(SemanticModel model, SymbolReference reference)
    {
        if (reference is not { IsDeclaration: true, Symbol.Kind: SymbolKind.Binding })
            return "";
        var declared = model.Families
            .Where(family => family.Binding == reference.Symbol)
            .SelectMany(family => family.Instances)
            .Select(instance => $"`{instance.QualifiedName}`")
            .ToList();
        return declared.Count == 0 ? "" : $"\n- declares: {string.Join(", ", declared)}";
    }

    /// <summary>
    /// How long the instruction at <paramref name="position"/> takes, and how long the block
    /// it is in takes. The count is an interval wherever it depends on something the program
    /// does not say, such as whether an indexed read crosses a page.
    /// </summary>
    private static Protocol.Hover? ToTiming(
        SemanticModel model, CodeLayout? layout, ControlFlow? flow, StateAnalysis? states, int position)
    {
        if (layout is null || Statement(model.Tree, position) is not { } statement)
            return null;
        if (statement.Kind is not (SyntaxKind.InstructionStatement or SyntaxKind.EnsureDirective)
            || layout.AnyOf(statement) is not { Cycles: { } cycles } laid)
        {
            return null;
        }

        var text = new StringBuilder($"**{Spell(cycles)}**");

        // An `.ensure` writes what the analysis found it needs, which is worth seeing.
        if (laid.Ensured is { } ensured)
        {
            var written = new[] { (Mnemonic: "rep", Flags: ensured.Reset), (Mnemonic: "sep", Flags: ensured.Set) }
                .Where(pair => pair.Flags != 0)
                .Select(pair => $"`{pair.Mnemonic} #${pair.Flags.ToString("x2", CultureInfo.InvariantCulture)}`")
                .ToList();
            text.Append(written.Count == 0
                ? "\n\nwrites nothing: the widths already hold"
                : $"\n\nwrites {string.Join(" and ", written)}");
        }
        if (Around(flow, statement)?.Cycles is { } block)
            text.Append($"\n\nthis block: {Spell(block)}");

        // What the analysis found reaching the line, which is what sized its immediate.
        if (states?.AnyBefore(statement) is { } state)
        {
            text.Append($"\n\nstate here: `{state.Processor}`");
            text.Append(state.Stack is { } stack ? $", {stack.Depth} pushed" : ", stack not known");
        }
        if (flow?.Registers?.AnyBefore(statement) is { } registers)
            text.Append($"\n\nregisters here:\n\n```text\n{Spell(registers)}\n```");
        return new Protocol.Hover(
            Protocol.MarkupContent.Markdown(text.ToString()), ToRange(model.Tree, statement.Span));
    }

    /// <summary>The statement on the line <paramref name="position"/> is in, or null.</summary>
    private static SyntaxNode? Statement(SyntaxTree tree, int position)
    {
        foreach (var node in tree.Root.DescendantNodes())
        {
            if (node.Green is GreenLine && position >= node.Position
                && position < node.Position + node.Green.FullWidth)
            {
                return node.Statement;
            }
        }
        return null;
    }

    /// <summary>The block a statement is in, wherever in the file it was written.</summary>
    private static BasicBlock? Around(ControlFlow? flow, SyntaxNode statement) =>
        flow?.Regions
            .SelectMany(region => region.Blocks)
            .FirstOrDefault(block => block.Steps.Any(step =>
                step.Statement.Tree == statement.Tree && step.Statement.Position == statement.Position));

    /// <summary>
    /// What each register holds at a point, one to a line and always all four, for the hover to
    /// show in a fenced block. A hover has room to lay it out, and a column a reader's eye can
    /// run down beats a sentence they have to take apart:
    /// <code>
    /// A  as X entered
    /// X  as entered
    /// Y  set
    /// C  not known
    /// </code>
    /// </summary>
    internal static string Spell(RegisterState state) => string.Join("\n", RegisterEffects
        .Each(Registers.All)
        .Select(register => $"{RegisterEffects.Spell(register)}  {Held(register, state.Of(register))}"));

    /// <summary>
    /// What one register holds. A 6502 saves X through the accumulator, so a register may hold
    /// what another was entered with, and saying which one is what makes a save readable.
    /// </summary>
    private static string Held(Registers register, RegisterValue value)
    {
        if (value.Holds(register))
            return "as entered";
        if (value is { IsWritten: false, IsUnknown: false } && value.Entry != Registers.None)
            return $"as {RegisterEffects.Spell(value.Entry)} entered";
        return value is { IsWritten: true, IsUnknown: false } && value.Entry == Registers.None ? "set" : "not known";
    }

    /// <summary>
    /// The registers a routine or a block hands back, as a lens and a hover both say it:
    /// <c>preserves A, X, Y, C</c>, <c>preserves X, Y</c>, <c>preserves none</c>. It is a list
    /// and not a sentence about one, because it is read at a glance.
    /// <para>
    /// What nt65 works out is a floor, so where a call could not be followed the list ends with
    /// <c>?</c>: those registers and perhaps more, which is what <c>?</c> means everywhere else.
    /// </para>
    /// </summary>
    internal static string Spell(Registers kept, bool complete)
    {
        var names = RegisterEffects.Each(kept).Select(RegisterEffects.Spell).ToList();
        if (!complete)
            names.Add("?");
        return "preserves " + (names.Count == 0 ? "none" : string.Join(", ", names));
    }

    /// <summary>A cycle count as it is shown: <c>4 cycles</c>, or <c>4-5 cycles</c>.</summary>
    internal static string Spell(CycleCount cycles) =>
        cycles is { IsExact: true, Least: 1 } ? "1 cycle" : $"{cycles} cycles";

    /// <summary>
    /// Where the name at <paramref name="position"/> is declared, or null. The declaration
    /// may be in another file of the program, so the location carries its own URI.
    /// </summary>
    public static Protocol.Location? ToDefinition(ProgramModel program, SemanticModel model, int position) =>
        model.ReferenceAt(position)?.Symbol is { } named && program.Current(named) is var symbol
            ? new Protocol.Location(ToUri(symbol.Tree.Path), ToRange(symbol.Tree, symbol.NameSpan))
            : null;

    /// <summary>Every place the name at <paramref name="position"/> is written, in every file.</summary>
    public static IReadOnlyList<Protocol.Location> ToReferences(
        ProgramModel program, SemanticModel model, int position, bool includeDeclaration) =>
        [.. Everywhere(program, model, position, renaming: false)
            .Where(found => includeDeclaration || !found.Reference.IsDeclaration)
            .Select(found => new Protocol.Location(
                ToUri(found.File.Tree.Path), ToRange(found.File.Tree, found.Reference.Span)))];

    /// <summary>The same places, for a client to mark while the caret is on one of them.</summary>
    public static IReadOnlyList<Protocol.DocumentHighlight> ToHighlights(SemanticModel model, int position) =>
        [.. Occurrences(model, position).Select(reference => new Protocol.DocumentHighlight(
            ToRange(model.Tree, reference.Span),
            reference.IsDeclaration ? Protocol.DocumentHighlightKind.Write : Protocol.DocumentHighlightKind.Read))];

    /// <summary>
    /// Lines <paramref name="first"/> to <paramref name="last"/> laid out as nt65 writes them,
    /// one edit per line that moves. The whole file decides where a line goes, and only these
    /// lines come back, which is what a client asking about a selection means.
    /// </summary>
    public static IReadOnlyList<Protocol.TextEdit> ToFormatting(SyntaxTree tree, int first, int last) =>
        [.. Formatter.Changes(tree, first, last).Select(change => new Protocol.TextEdit(
            ToRange(tree, new TextSpan(change.Start, change.Length)), change.NewText))];

    /// <summary>The name at <paramref name="position"/>, which is what a rename would replace.</summary>
    public static Protocol.Range? ToRenameRange(SemanticModel model, int position) =>
        model.ReferenceAt(position) is { } reference ? ToRange(model.Tree, reference.Span) : null;

    /// <summary>
    /// Renaming every occurrence of the name at <paramref name="position"/>, or the reason
    /// it cannot be renamed to <paramref name="newName"/>.
    /// </summary>
    public static (Protocol.WorkspaceEdit? Edit, string? Problem) ToRename(
        ProgramModel program, SemanticModel model, int position, string newName)
    {
        if (model.ReferenceAt(position) is not { } reference)
            return (null, "there is no name here to rename");
        if (CheckNewName(program.Current(reference.Symbol), newName, reference.IsAlias ? model.FileScope : null) is { } problem)
            return (null, problem);

        // An exported name is written in every file that uses it, so the edit spans the
        // program rather than the file the caret is in.
        var edits = new Dictionary<string, IReadOnlyList<Protocol.TextEdit>>(StringComparer.Ordinal);
        foreach (var byFile in Everywhere(program, model, position, renaming: true).GroupBy(found => found.File))
        {
            edits[ToUri(byFile.Key.Tree.Path)] =
                [.. byFile.Select(found => new Protocol.TextEdit(
                    ToRange(byFile.Key.Tree, found.Reference.Span), newName))];
        }
        return (new Protocol.WorkspaceEdit(edits), null);
    }

    /// <summary>
    /// Why <paramref name="newName"/> will not do, or null when it will: a rename
    /// that leaves the file not compiling is not a rename. <paramref name="alias"/> is the
    /// top level of the module whose <c>.use ... as</c> name is being renamed, where the new
    /// name has to be free instead.
    /// </summary>
    private static string? CheckNewName(Symbol symbol, string newName, Scope? alias)
    {
        var name = newName;
        if (symbol.IsCheapLocal)
        {
            if (!name.StartsWith('@'))
                return $"`{symbol.DisplayName}` is a cheap local, so its new name must start with `@`";
            name = name[1..];
        }
        else if (name.StartsWith('@'))
        {
            return $"`{symbol.DisplayName}` is not a cheap local, so its new name may not start with `@`";
        }

        if (name.Length == 0 || !SyntaxFacts.IsIdentifierStart(name[0]) || !name.All(SyntaxFacts.IsIdentifierPart))
            return $"`{newName}` is not a name: names are a letter or `_` followed by letters, digits and `_`";
        if (!symbol.IsCheapLocal && (SyntaxFacts.IsMnemonic(name) || SyntaxFacts.IsRegister(name)))
            return $"`{newName}` is a reserved word";

        var taken = symbol.IsCheapLocal ? symbol.Scope.FindCheapLocal(name) : (alias ?? symbol.Scope).FindMember(name);
        return taken is null || taken == symbol ? null : $"`{newName}` is already declared in this scope";
    }

    /// <summary>Everywhere the symbol at <paramref name="position"/> is written in this file.</summary>
    private static IReadOnlyList<SymbolReference> Occurrences(SemanticModel model, int position) =>
        model.ReferenceAt(position) is { } reference ? model.ReferencesTo(reference.Symbol) : [];

    /// <summary>
    /// The same, across every file of the program, in file and source order. A name a
    /// <c>.use ... as</c> gives is written instead of the symbol's own, so for a rename it is
    /// kept apart: renaming the symbol leaves those alone, and renaming one renames only the
    /// names that module wrote the same way.
    /// </summary>
    private static IEnumerable<(SemanticModel File, SymbolReference Reference)> Everywhere(
        ProgramModel program, SemanticModel model, int position, bool renaming)
    {
        if (model.ReferenceAt(position) is not { } asked)
            return [];

        // A file kept from before an edit elsewhere names what the edited file declared then,
        // so the symbols are compared as what they stand for now.
        var symbol = program.Current(asked.Symbol);
        if (renaming && symbol.Bound?.Value.Member is { } member)
            symbol = member;
        if (renaming && asked.IsAlias)
        {
            var written = model.Tree.Text.Substring(asked.Span.Start, asked.Span.Length);
            return model.References
                .Where(reference => reference.IsAlias && program.Current(reference.Symbol) == symbol
                    && model.Tree.Text.Substring(reference.Span.Start, reference.Span.Length) == written)
                .Select(reference => (model, reference));
        }
        // An instance of a family is named after an enum's member, so renaming either is
        // renaming the member and every use of every instance named after it.
        var renamed = new HashSet<Symbol> { symbol };
        if (renaming && symbol.IsEnumMember)
        {
            foreach (var file in program.Files)
                renamed.UnionWith(file.Symbols.Where(instance => instance.Bound?.Value.Member == symbol));
        }
        return program.Files
            .OrderBy(file => file.Tree.Path, StringComparer.Ordinal)
            .SelectMany(file => file.References
                .Where(reference => renamed.Contains(program.Current(reference.Symbol))
                    && !(renaming && reference.IsAlias))
                .Select(reference => (file, reference)));
    }

    /// <summary>
    /// What an editor shows about a symbol: what kind it is, what it is worth, how wide an
    /// address it is and, for an address, the segment it sits in.
    /// </summary>
    private static string Describe(Symbol symbol, SyntaxTree asked)
    {
        var text = new StringBuilder($"**{symbol.KindText}** `{(symbol.Tree != asked ? symbol.PathName : symbol.QualifiedName)}`\n");

        // A name from another module is worth naming that module for: it is the file the
        // declaration is in, and the file whose `.export` makes it nameable here.
        if (symbol.Tree != asked)
            text.Append($"\n- from: `{symbol.Tree.Path[(symbol.Tree.Path.LastIndexOf('/') + 1)..]}`");

        // A name no path can reach is shown as it is written, so the routine or scope it is
        // private to is worth saying instead.
        if (!symbol.IsReachableByPath && symbol.Scope.NearestNamed()?.Name is { } owner)
            text.Append($"\n- private to: `{owner}`");
        if (symbol.Kind == SymbolKind.Member)
            text.Append($"\n- offset: `{symbol.Value}`");
        else if (symbol.Value.IsKnown)
            text.Append($"\n- value: `{symbol.Value}`");
        if (symbol.Type is { } type)
            text.Append($"\n- type: `{type.QualifiedName}`");
        if (symbol.Size is { } room)
            text.Append($"\n- size: `{room}` byte{(room == 1 ? "" : "s")}");
        if (symbol.Count is { } count && symbol.Kind != SymbolKind.Member)
            text.Append($"\n- count: `{count}`");
        if (symbol.AddressSize is { } size)
            text.Append($"\n- address size: `{Spell(size)}` ({(int)size} byte{((int)size == 1 ? "" : "s")})");
        if (symbol.IsAddress && symbol.Segment is { } segment)
            text.Append($"\n- segment: `{segment}`");
        return text.ToString();
    }

    /// <summary>An address size as the language writes it.</summary>
    private static string Spell(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "zp",
        AddressSize.Absolute => "abs",
        _ => "far",
    };

    internal static Protocol.Diagnostic ToDiagnostic(Diagnostic diagnostic) => new(
        ToRange(diagnostic.Span),
        ToSeverity(diagnostic.Severity),
        SourceName,
        diagnostic.Message,
        diagnostic.Related.Count == 0
            ? null
            : [.. diagnostic.Related.Select(related => new Protocol.DiagnosticRelatedInformation(
                new Protocol.Location(ToUri(related.Span.File), ToRange(related.Span)), related.Message))],
        diagnostic.IsUnnecessary ? [Protocol.DiagnosticTag.Unnecessary] : null);

    private static IReadOnlyList<Protocol.DocumentSymbol> ToSymbols(SyntaxTree tree, IReadOnlyList<OutlineItem> items) =>
        [.. items.Select(item => new Protocol.DocumentSymbol(
            item.Name,
            item.Detail,
            ToSymbolKind(item.Kind),
            ToRange(tree, item.Span),
            ToRange(tree, item.NameSpan),
            item.Children.Count == 0 ? null : ToSymbols(tree, item.Children)))];

    /// <summary>A diagnostic span, which is 1-based and on one line, as a 0-based range.</summary>
    private static Protocol.Range ToRange(Span span) => new(
        new Protocol.Position(span.Line - 1, span.StartColumn - 1),
        new Protocol.Position(span.Line - 1, span.EndColumn - 1));

    internal static Protocol.Range ToRange(SyntaxTree tree, TextSpan span) =>
        new(ToPosition(tree, span.Start), ToPosition(tree, span.End));

    internal static Protocol.Position ToPosition(SyntaxTree tree, int position)
    {
        var line = tree.GetLineIndex(position);
        return new Protocol.Position(line, position - tree.LineStarts[line]);
    }

    private static Protocol.DiagnosticSeverity ToSeverity(Severity severity) => severity switch
    {
        Severity.Error => Protocol.DiagnosticSeverity.Error,
        Severity.Warning => Protocol.DiagnosticSeverity.Warning,
        _ => Protocol.DiagnosticSeverity.Information,
    };

    internal static Protocol.SymbolKind ToSymbolKind(OutlineKind kind) => kind switch
    {
        OutlineKind.Proc => Protocol.SymbolKind.Function,
        OutlineKind.Scope => Protocol.SymbolKind.Namespace,
        OutlineKind.Segment => Protocol.SymbolKind.Module,
        OutlineKind.Macro => Protocol.SymbolKind.Function,
        OutlineKind.Constant => Protocol.SymbolKind.Constant,
        OutlineKind.Data => Protocol.SymbolKind.Variable,
        OutlineKind.Type => Protocol.SymbolKind.Struct,
        OutlineKind.Function => Protocol.SymbolKind.Function,
        _ => Protocol.SymbolKind.Field,
    };

    /// <summary>A logical path back as a URI, for a diagnostic that points into another file.</summary>
    internal static string ToUri(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : path;
}
