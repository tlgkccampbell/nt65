using System.Globalization;
using Norristown.Flow;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Conversions from the analysis to protocol types. Everything the server sends is built here, so
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
    /// The language tag on the fenced code block that holds a hover's key/value grid. Markdown
    /// formatting does not apply inside a fenced block, so the only way to style the grid's
    /// parts is to give it a grammar of its own; an editor without that grammar renders it as
    /// plain monospace.
    /// </summary>
    private const string Grid = "nt65-hover";

    /// <summary>The column on the <c>cycles</c> row where the enclosing block's count starts, after the line's own.</summary>
    private const int BlockColumn = 10;

    /// <summary>The width allowed for the block's count, so the reason after it always starts in the same column.</summary>
    private const int ReasonColumn = 14;

    /// <summary>
    /// How many pushes a hover lists before it says how many more there are. A reader cares
    /// most about the top of the stack, which holds what the routine will pull next.
    /// </summary>
    private const int MostPushes = 6;

    /// <summary>
    /// The rows that lead an instruction's hover, being what a reader hovers an instruction to
    /// learn: its cycle count, and the processor state on entry that sized its operand. The
    /// flags it writes, the register contents and the stack are supporting detail and go below
    /// the rule.
    /// </summary>
    private static readonly IReadOnlySet<string> TimingAsked =
        new HashSet<string>(["cycles", "state"], StringComparer.Ordinal);

    /// <summary>
    /// The same for an <c>.ensure</c>, which a reader hovers to see what it became: the line
    /// states the requirement, not the instructions it writes.
    /// </summary>
    private static readonly IReadOnlySet<string> EnsureAsked =
        new HashSet<string>(["writes", "state"], StringComparer.Ordinal);

    /// <summary>The rows that lead an inline <c>.scope</c>'s hover, which are the only rows it has.</summary>
    private static readonly IReadOnlySet<string> ScopeAsked =
        new HashSet<string>(["cost", "excluding", "preserves"], StringComparer.Ordinal);

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
                Catalogue.OmittedBranch.Id,
                SourceName,
                Catalogue.OmittedBranch.Format,
                null,
                [Protocol.DiagnosticTag.Unnecessary])),
        ];

    /// <summary>
    /// Adds an <c>in the output</c> row with the symbol's name in the ca65 output, where that
    /// differs from its name in the source. Only two kinds of name differ: ca65 reads a word of
    /// its own instruction table at the start of a line as an instruction, so the emitter
    /// prefixes such a name with its module; and a module that another module places has every
    /// name it does not export prefixed the same way. Every other name keeps its spelling, so no
    /// row is added for it.
    /// </summary>
    private static void Written(Card card, ProgramAnalysis analysis, Symbol symbol)
    {
        if (symbol is { IsReachableByPath: true, LinkerName: null }
            && Emit.FlatNames.Prefixed(symbol.FlatName, analysis.Cpu, symbol.Module) is { } prefixed)
        {
            card.Row("in the output", $"`{prefixed}`");
            return;
        }

        // A module placed by another module writes each name it does not export with the
        // module's name in front.
        if (symbol is { IsReachableByPath: true, LinkerName: null, Module: { } module }
            && analysis.Placements.PlacerOf(symbol.Tree) is not null)
        {
            card.Row("in the output", $"`{module.Replace("::", "__", StringComparison.Ordinal)}__{symbol.FlatName}`");
        }
    }

    /// <summary>The file's outline, nested the way its blocks are.</summary>
    public static IReadOnlyList<Protocol.DocumentSymbol> ToSymbols(SyntaxTree tree) =>
        ToSymbols(tree, Outline.Build(tree));

    /// <summary>One foldable range per block.</summary>
    public static IReadOnlyList<Protocol.FoldingRange> ToFoldingRanges(SyntaxTree tree) =>
        [.. Folding.Build(tree).Select(range => new Protocol.FoldingRange(range.StartLine, range.EndLine))];

    /// <summary>
    /// What to show at <paramref name="position"/>: the name under the caret, or, where there
    /// is none, the <c>.scope</c> block the line opens or the instruction written on it.
    /// <para>
    /// Every hover reads from the top down, and each section is omitted when it is empty: the
    /// line under the caret in nt65 syntax, the comment its author left above it, the one or two
    /// facts most often wanted for that kind of thing, a horizontal rule, and below it everything
    /// else the analysis worked out. Nothing is dropped for being far down; the first screenful
    /// answers the question and the rest is supporting detail.
    /// </para>
    /// </summary>
    public static Protocol.Hover? ToHover(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var flow = analysis.FlowFor(model.Tree.Path);
        return model.ReferenceAt(position) is { } reference
            ? ToName(analysis, model, reference)
            : ToPlaced(analysis, model, position) ?? ToComparedWord(model, position)
                ?? ToParameterKind(analysis, model, position) ?? ToScope(model, flow, position)
                ?? ToTiming(analysis, model, flow, position);
    }

    /// <summary>
    /// Where the module a <c>.place</c> names is declared, with the caret on its path. A module
    /// is not a symbol, so there is no symbol reference there to answer from.
    /// </summary>
    public static Protocol.Location? ToPlacedDefinition(ProgramAnalysis analysis, SemanticModel model, int position) =>
        PlacedAt(analysis, model, position) is { Tree: var tree } && Placements.Declaration(tree) is { } declared
            ? new Protocol.Location(ToUri(tree.Path), ToRange(tree, declared.Name.Span))
            : null;

    /// <summary>
    /// The module a <c>.place</c> names, with the caret on its path: its file, what its
    /// declaration says about placing it, and which translation unit it is written in.
    /// </summary>
    private static Protocol.Hover? ToPlaced(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        if (PlacedAt(analysis, model, position) is not { } placed)
            return null;
        var placements = analysis.Placements;
        var card = new Card($"module {placed.Path}", new HashSet<string>(["declared", "written in"], StringComparer.Ordinal));
        card.Row("declared", placements.DeclaredFor(placed.Tree) switch
        {
            ModulePlacement.Placed => "placed: its bytes are written where it is placed",
            ModulePlacement.Placeable => "placeable: placed at most once, and alone where nothing places it",
            _ => "alone: it has an output of its own, and is not placed",
        });
        if (placements.UnitOf(placed.Tree) is { IsPlaced: true, Root: var root }
            && analysis.ModelFor(root.Path)?.FileScope.Module is { } unit)
        {
            card.Row("written in", $"the output of `{unit}`");
        }
        card.Row("file", placed.Tree.Path);
        return new Protocol.Hover(Protocol.MarkupContent.Markdown(card.ToString()), ToRange(model.Tree, placed.Span));
    }

    /// <summary>The module whose path a <c>.place</c> writes at <paramref name="position"/>, or null.</summary>
    private static (SyntaxTree Tree, string Path, TextSpan Span)? PlacedAt(
        ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var token = model.Tree.Root.FindToken(position);
        if (token.Parent?.AncestorsAndSelf().OfType<PlaceDirectiveSyntax>().FirstOrDefault() is not { } place
            || !place.Name.Span.Contains(position) && place.Name.Span.End != position
            || Placements.PathOf(place.Name) is not { } path
            || analysis.Placements.ModuleNamed(path) is not { } tree)
        {
            return null;
        }
        return (tree, path, place.Name.Span);
    }

    /// <summary>
    /// The hover for a bare word that a condition compares a parameter's argument with, such as
    /// <c>imm</c> in <c>.mode(src) == imm</c> or <c>x</c> in <c>reg == x</c>: what the word is,
    /// what it is compared with and what that parameter accepts, and, when the parameter can
    /// never take that value, that the comparison never holds.
    /// </summary>
    private static Protocol.Hover? ToComparedWord(SemanticModel model, int position)
    {
        if (ComparedWords.At(model, position) is not { } compared)
            return null;
        var (word, isMode) = (compared.Word, compared.IsMode);
        var text = word.Text.ToLowerInvariant();
        var card = new Card(isMode ? $"mode {text}" : $"word {word.Text}", new HashSet<string>(["mode", "never"], StringComparer.Ordinal));
        if (isMode)
            card.Row("mode", ParameterKinds.Mode(text) is { } written ? $"{text}: {written}" : null);
        card.Row("compared with", compared.Compared);
        card.Row("takes", ParameterKinds.Takes(compared.Accepts));
        if (!compared.CanHold)
        {
            card.Row("never", isMode && !ComparedWord.Modes.Contains(text)
                ? $"{text} is not a mode .mode gives: {string.Join(", ", ComparedWord.Modes)}"
                : $"{compared.Name} is never {word.Text}: it may be {string.Join(", ", compared.Choices)}");
        }
        return new Protocol.Hover(Protocol.MarkupContent.Markdown(card.ToString()), ToRange(model.Tree, word.Span));
    }

    /// <summary>
    /// The hover for the kind written after a macro parameter's <c>:</c>, wherever in the kind
    /// the caret is: the parameter's own hover, which says what the kind accepts, plus, for a
    /// mode listed in <c>operand(...)</c>, how an operand in that mode is written. A kind is made
    /// of keywords rather than names, so there is no symbol reference there to answer from; the
    /// exception is the enum an enum kind names, which is a reference and gets its own hover.
    /// </summary>
    private static Protocol.Hover? ToParameterKind(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var token = model.Tree.Root.FindToken(position);
        if (token.Parent?.AncestorsAndSelf().OfType<ParameterKindSyntax>().FirstOrDefault() is not { } kind
            || kind.Ancestors().OfType<MacroParameterSyntax>().FirstOrDefault() is not { } parameter
            || model.ReferenceAt(parameter.Name.Span.Start) is not { } declared)
        {
            return null;
        }
        var mode = token.Parent is IdentifierNameSyntax { Parent: ParameterKindSyntax { Keyword.Text: var keyword } }
            && keyword.Equals("operand", StringComparison.OrdinalIgnoreCase)
            && ParameterKinds.Mode(token.Text) is { } written
                ? $"{token.Text.ToLowerInvariant()}: {written}"
                : null;
        var hover = ToName(analysis, model, declared, mode);
        return hover with { Range = ToRange(model.Tree, token.Span) };
    }

    /// <summary>
    /// What an editor shows about a name: the line that declares it, the facts the analysis
    /// worked out, and the comment written above it. What a routine costs and which registers
    /// it preserves are shown wherever it is named, not only at its declaration, because the
    /// cost of a call is what a reader wants to know at the call.
    /// </summary>
    private static Protocol.Hover ToName(
        ProgramAnalysis analysis, SemanticModel model, SymbolReference reference, string? mode = null)
    {
        // Use the declaration as the program has it now. An edit that does not change what
        // other files see of a file leaves their models in place, and those models still hold
        // symbols resolved against the file as it was before the edit.
        var symbol = analysis.Program.Current(reference.Symbol);
        var card = new Card(Headline(symbol, model.Tree), Asked(symbol));
        card.Prose(DocComments.Of(symbol));

        // For a name from another module, show that module's file: it holds the declaration and
        // the `.export` that makes the name visible here.
        if (symbol.Tree != model.Tree)
            card.Row("from", symbol.Tree.Path[(symbol.Tree.Path.LastIndexOf('/') + 1)..]);

        // A name that no qualified path can reach is shown unqualified, so say which routine or
        // scope it is private to instead.
        if (!symbol.IsReachableByPath && symbol.Scope.NearestNamed()?.Name is { } owner)
            card.Row("private to", owner);

        // For a macro parameter, describe in words what arguments its kind accepts.
        card.Row("mode", mode);
        if (symbol.Parameter is { } parameter)
            card.Row("takes", ParameterKinds.Takes(parameter.Accepts));
        if (symbol.Kind == SymbolKind.Member)
            card.Row("offset", symbol.Value.ToString());
        else if (symbol.Value.IsKnown)
            card.Row("value", Spell(symbol.Value));
        else if (symbol.Kind == SymbolKind.Func && Called(model, reference) is { IsKnown: true } called)
            card.Row("value", Spell(called));
        if (symbol.Type is { } type)
            card.Row("type", type.QualifiedName);

        // A routine's cost and preserved registers are what a caller wants to know, so they come
        // before where the routine lives. What a macro call expands to is the same question
        // asked of a macro, so its row goes here too.
        Routine(card, analysis, symbol);
        card.Row("expands to", MacroCallHover.Becomes(analysis, model, reference));

        // Size and element count answer one question, so they share a row. A count of one is
        // what a declaration without a count means, so it is not shown.
        if (symbol.Size is { } room)
        {
            card.Row("size", $"{room} byte{(room == 1 ? "" : "s")}"
                + (symbol.Count is > 1 and { } count && symbol.Kind != SymbolKind.Member ? $" x {count}" : ""));
        }
        if (symbol.AddressSize is { } size)
        {
            card.Row("address", $"{Spell(size)} ({(int)size} byte{((int)size == 1 ? "" : "s")})"
                + (symbol.IsAddress && symbol.Segment is { } segment ? $" in {segment}" : ""));
        }
        Declares(card, model, reference);
        Written(card, analysis, symbol);

        // A macro call is the only name whose hover says more than its declaration does: what
        // it expands to. MacroCallHover computes the summary row above and appends the
        // expansion listing to the hover text here.
        return new Protocol.Hover(
            Protocol.MarkupContent.Markdown(MacroCallHover.Added(card.ToString(), analysis, model, reference)),
            ToRange(model.Tree, reference.Span));
    }

    /// <summary>
    /// The keys of the rows most wanted for this kind of name, which lead the hover; every other
    /// row goes below the rule, in the order the hover writes them. A reader hovers a constant
    /// for its value, a member for its offset, a routine for what a call to it costs and which
    /// registers it preserves, and a name from another module to find out which module.
    /// </summary>
    private static IReadOnlySet<string> Asked(Symbol symbol)
    {
        var kind = symbol.Kind switch
        {
            SymbolKind.Member => new[] { "offset" },
            SymbolKind.Proc or SymbolKind.ExternProc => ["cost", "excluding", "preserves"],

            // At a call, a reader hovers a function to see the value of the call.
            SymbolKind.Func => ["value"],
            // A reader hovers a macro to see what the call expands to, so that row leads; it is
            // listed even where the row is not written, as at the declaration.
            SymbolKind.Macro => ["expands to"],
            SymbolKind.Binding => ["declares"],
            SymbolKind.MacroParameter => ["mode", "takes"],
            SymbolKind.Data or SymbolKind.List or SymbolKind.Charmap or SymbolKind.Frame
                or SymbolKind.Label or SymbolKind.ImportedAddress or SymbolKind.AddressAlias => ["address", "size"],
            SymbolKind.Struct or SymbolKind.Union or SymbolKind.Enum => ["size"],
            _ => ["value"],
        };
        return new HashSet<string>(["from", "private to", .. kind], StringComparer.Ordinal);
    }

    /// <summary>
    /// The value of the call a function's name appears in, which may be text, or an unknown
    /// value when the name is not called there or nt65 cannot evaluate the call, as in a macro
    /// body whose parameters have no arguments yet.
    /// </summary>
    private static Value Called(SemanticModel model, SymbolReference reference)
    {
        var token = model.Tree.Root.FindToken(reference.Span.Start);
        var name = token.Parent?.AncestorsAndSelf().OfType<NameExpressionSyntax>().FirstOrDefault();
        return name?.Parent is CallExpressionSyntax call && call.Callee == name ? model.ValueOf(call) : Value.Unknown;
    }

    /// <summary>
    /// The line a name is declared on, as written in the source, with the name replaced by the
    /// one a reader would write for it. Where the declaration is not a line of its own (a member
    /// of a layout, a macro parameter, the name a repetition binds and the instances it
    /// declares), there is no line to show, so the kind and the name are shown instead.
    /// </summary>
    private static string Headline(Symbol symbol, SyntaxTree asked)
    {
        var named = symbol.Tree != asked ? symbol.PathName : symbol.QualifiedName;
        if (symbol.Parameter is { Accepts.Kind: not ParameterKind.Expr } parameter)
            return $"{symbol.KindText} {named}: {parameter.Accepts}";
        return symbol.Kind is SymbolKind.Member or SymbolKind.MacroParameter or SymbolKind.Binding
            || symbol.Bound is not null
            || Declaring(symbol, named) is not { Length: > 0 } line
                ? $"{symbol.KindText} {named}"
                : line;
    }

    /// <summary>
    /// The declaring line as it is written, with the name on it replaced by
    /// <paramref name="named"/>, since outside the declaring scope or module a reader has to
    /// write a longer, qualified name than the one on the line.
    /// </summary>
    private static string Declaring(Symbol symbol, string named)
    {
        var tree = symbol.Tree;
        var index = tree.GetLineIndex(symbol.NameSpan.Start);
        var start = tree.LineStarts[index];
        var end = index + 1 < tree.LineStarts.Length ? tree.LineStarts[index + 1] : tree.Text.Length;
        var line = tree.Text[start..end].TrimEnd('\n', '\r');
        var at = symbol.NameSpan.Start - start;

        // A cheap local is written with an `@` that its symbol name lacks, so it is not
        // replaced; the line already shows it the way a reader would write it.
        if (!symbol.IsCheapLocal && at >= 0 && at + symbol.NameSpan.Length <= line.Length
            && line.AsSpan(at, symbol.NameSpan.Length).SequenceEqual(symbol.Name.AsSpan()))
        {
            line = line[..at] + named + line[(at + symbol.NameSpan.Length)..];
        }
        return Written(line);
    }

    /// <summary>
    /// A line as a headline shows it: without the indentation before it, the comment at the
    /// end of it or the brace that opens the block it heads, none of which is what was asked
    /// about.
    /// </summary>
    private static string Written(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == ';' && !quoted)
            {
                line = line[..i];
                break;
            }
        }
        return line.TrimEnd().TrimEnd('{').Trim();
    }

    /// <summary>
    /// Adds the cost and preserved-register rows for a routine, wherever its name is written. A
    /// code lens shows the same above the declaration, but an editor can be told to hide lenses,
    /// and a lens is nowhere near a call anyway. The control flow used is the declaring file's,
    /// since a call from another file is not part of it.
    /// </summary>
    private static void Routine(Card card, ProgramAnalysis analysis, Symbol symbol)
    {
        if (analysis.FlowFor(symbol.Tree.Path) is not { } flow)
            return;
        var found = flow.Regions
            .Where(region => region.Routine.Tree == symbol.Tree && region.Routine.NameSpan == symbol.NameSpan)
            .Select(region => (
                region.Routine.Name,
                Cost: CodeLenses.Spell(region.Cost, region.Total, "never returns", false),
                Excluded: region.Cost.IsKnown ? region.Total.Excluded ?? [] : [],
                Kept: region.Total.Ends ? Spell(region.Registers.Kept, region.Registers.Complete) : null))
            .ToList();
        Rows(card, "cost", found.Select(region => (region.Name, region.Cost)));

        // What the cost with calls leaves out, each once however many instances leave it out,
        // with why, which the lens has no room for.
        var excluded = found.SelectMany(region => region.Excluded).DistinctBy(exclusion => exclusion.What).ToList();
        for (var i = 0; i < excluded.Count; i++)
            card.Row(i == 0 ? "excluding" : "", $"{excluded[i].What}: {excluded[i].Why}");
        Rows(card, "preserves", found.Select(region => (region.Name, region.Kept)));
    }

    /// <summary>
    /// One row of the grid, or one row per instance where the instances of a family (the
    /// routines a repetition declares) differ. Every instance is declared on the family's single
    /// line, so where they all agree the row is shown once.
    /// </summary>
    private static void Rows(Card card, string key, IEnumerable<(string Name, string? Text)> found)
    {
        var named = found.Where(region => region.Text is { Length: > 0 }).ToList();
        var texts = named.Select(region => region.Text!).Distinct(StringComparer.Ordinal).ToList();
        if (texts.Count == 1)
        {
            card.Row(key, texts[0]);
            return;
        }
        for (var i = 0; i < named.Count; i++)
            card.Row(i == 0 ? key : "", $"{named[i].Name}: {named[i].Text}");
    }

    /// <summary>
    /// At the declaration of the name a repetition binds: the family instances it declares,
    /// which is what a reader wants to know about that line.
    /// </summary>
    private static void Declares(Card card, SemanticModel model, SymbolReference reference)
    {
        if (reference is not { IsDeclaration: true, Symbol.Kind: SymbolKind.Binding })
            return;
        var declared = model.Families
            .Where(family => family.Binding == reference.Symbol)
            .SelectMany(family => family.Instances)
            .Select(instance => instance.QualifiedName)
            .ToList();
        if (declared.Count > 0)
            card.Row("declares", string.Join(", ", declared));
    }

    /// <summary>
    /// What an inline <c>.scope</c> block costs and which registers it preserves, where the
    /// caret is on the line that opens it. The block has no name to hover, so apart from the
    /// lens this is the only place that shows it.
    /// </summary>
    private static Protocol.Hover? ToScope(SemanticModel model, ControlFlow? flow, int position)
    {
        foreach (var region in flow?.Regions ?? [])
        {
            foreach (var scope in region.ScopeRegisters)
            {
                if (position < scope.Opener.Start || position >= scope.Opener.End)
                    continue;
                var card = new Card(
                    Written(model.Tree.Text[scope.Opener.Start..scope.Opener.End]), ScopeAsked);
                var cost = region.Scopes.FirstOrDefault(costed => costed.Opener == scope.Opener).Cost;
                card.Row("cost", CodeLenses.Spell(cost, null, null, false));
                card.Row("preserves", Spell(scope.Kept, scope.Complete));
                return new Protocol.Hover(
                    Protocol.MarkupContent.Markdown(card.ToString()), ToRange(model.Tree, scope.Opener));
            }
        }
        return null;
    }

    /// <summary>
    /// The instruction at <paramref name="position"/>: how long it takes and how long the
    /// block around it takes, and, where the analysis followed control to it, the processor
    /// state that reaches it, what each register holds and what the routine has pushed. The
    /// count is an interval wherever it depends on something the program does not say, such
    /// as whether an indexed read crosses a page.
    /// </summary>
    private static Protocol.Hover? ToTiming(
        ProgramAnalysis analysis, SemanticModel model, ControlFlow? flow, int position)
    {
        if (analysis.LayoutFor(model.Tree.Path) is not { } layout
            || Statement(model.Tree, position) is not { } statement)
        {
            return null;
        }
        if (statement is not (InstructionStatementSyntax or EnsureDirectiveSyntax)
            || layout.AnyOf(statement) is not { Cycles: { } cycles } laid)
        {
            return null;
        }

        // Show the instruction's full datasheet name as a trailing comment, since a reader who
        // already knows what `xba` stands for is not the one hovering it.
        var mnemonic = (statement as InstructionStatementSyntax)?.Mnemonic.Text.ToLowerInvariant();
        var line = Written(model.Tree.Text[statement.Span.Start..statement.Span.End]);
        var card = new Card(
            mnemonic is { } named && Mnemonics.Name(named) is { } called ? $"{line}  ; {called}" : line,
            laid.Ensured is null ? TimingAsked : EnsureAsked);

        // The line's cycles, the enclosing basic block's cycles and, where the line's count is
        // an interval, what causes the higher figure: three views of one question, read across
        // one row rather than down three.
        card.Row("cycles", Cost(cycles, Around(flow, statement)?.Cycles, laid.Causes));

        // An `.ensure` emits whatever `rep`/`sep` the analysis found it needs; show what that is.
        if (laid.Ensured is { } ensured)
        {
            var written = new[] { (Mnemonic: "rep", Flags: ensured.Reset), (Mnemonic: "sep", Flags: ensured.Set) }
                .Where(pair => pair.Flags != 0)
                .Select(pair => $"{pair.Mnemonic} #${pair.Flags.ToString("x2", CultureInfo.InvariantCulture)}")
                .ToList();
            card.Row("writes", written.Count == 0
                ? "nothing: the widths already hold"
                : string.Join(" and ", written));
        }

        // The processor state the analysis found on entry to the line, which determined the
        // size of its immediate.
        var state = analysis.StatesFor(model.Tree.Path)?.AnyBefore(statement);
        if (state is not null)
            card.Row("state", state.Processor.ToString());
        if (mnemonic is { } flagged)
        {
            card.Row("flags", Mnemonics.Flags(
                analysis.Cpu, flagged, laid.Mode ?? AddressingMode.Implied, Immediate(model, statement, laid)));
        }

        // A column a reader can scan beats a sentence they have to take apart, so wherever
        // anything is known about the registers all four are listed, set apart by a gap from
        // the rows about the line itself.
        var registers = flow?.Registers?.AnyBefore(statement);
        if (registers is { } held)
        {
            card.Gap();
            foreach (var register in RegisterEffects.Each(Registers.All))
                card.Row(RegisterEffects.Spell(register), Held(held.Of(register), register));
        }
        Pushed(card, analysis, registers, state);
        return new Protocol.Hover(
            Protocol.MarkupContent.Markdown(card.ToString()), ToRange(model.Tree, statement.Span));
    }

    /// <summary>
    /// The text of the <c>cycles</c> row: the line's own count, the count of the enclosing
    /// basic block, and the reason the line's own count is an interval. The reason belongs to the instruction,
    /// so a line whose own count is exact shows none even where its block's is not.
    /// </summary>
    private static string Cost(CycleCount cycles, CycleCount? block, IReadOnlyList<string>? causes)
    {
        var row = cycles.ToString();
        var reason = causes is { Count: > 0 } why && !cycles.IsExact ? string.Join(", ", why) : "";
        if (block is null && reason.Length == 0)
            return row;
        row = row.PadRight(BlockColumn) + (block is { } around ? $"block {around}" : "");
        return reason.Length == 0 ? row : row.PadRight(BlockColumn + ReasonColumn) + reason;
    }

    /// <summary>
    /// The value of the immediate a line is written with, or null where it has none or none
    /// nt65 can work out. It determines which flags a <c>rep</c> or a <c>sep</c> changes.
    /// </summary>
    private static long? Immediate(SemanticModel model, StatementSyntax statement, LineLayout laid) =>
        laid.Mode == AddressingMode.Immediate
            && statement is InstructionStatementSyntax { Operand: ImmediateOperandSyntax immediate }
            ? model.ValueOf(immediate.Value).AsNumber()
            : null;

    /// <summary>
    /// The statement on the line <paramref name="position"/> is in, or null past the end of the
    /// file, where there is no line and so nothing to say anything about.
    /// </summary>
    private static StatementSyntax? Statement(SyntaxTree tree, int position) =>
        position < tree.Text.Length ? tree.GetLine(tree.GetLineIndex(position)).Statement : null;

    /// <summary>The block a statement is in, wherever in the file it was written.</summary>
    private static BasicBlock? Around(ControlFlow? flow, StatementSyntax statement) =>
        flow?.Regions
            .SelectMany(region => region.Blocks)
            .FirstOrDefault(block => block.Steps.Any(step =>
                step.Statement.Tree == statement.Tree && step.Statement.Position == statement.Position));

    /// <summary>
    /// What the routine has pushed, top of the stack first, one row per push. Two models of the
    /// stack are combined: the saved-register stack says which register's entry value a push
    /// holds, and the processor-state analysis's stack says what a <c>php</c> saved, what a
    /// constant push holds and where a <c>.frame</c> is. Each knows things the other cannot see.
    /// </summary>
    private static void Pushed(Card card, ProgramAnalysis analysis, RegisterState? registers, FlowState? state)
    {
        if (registers is null && state is null)
            return;

        // A missing group would read as an empty stack, and a stack the analysis lost track of
        // is not an empty one.
        if (registers?.Stack is null && state?.Stack is null)
        {
            card.Gap();
            card.Row("stack", "unknown");
            return;
        }
        var rows = Pushes(analysis, registers?.Stack, state?.Stack);
        if (rows.Count == 0)
            return;
        card.Gap();
        for (var i = 0; i < rows.Count && i < MostPushes; i++)
            card.Row(i == 0 ? "stack" : "", rows[i]);

        // A routine that has pushed a lot holds more than a reader can take in at a glance, and
        // the pushes it will pull back next are the ones on top, so only those are listed.
        if (rows.Count > MostPushes)
            card.Row("", $"and {rows.Count - MostPushes} more");
    }

    /// <summary>
    /// One row per push, top first, from whichever of the two stacks knows about it. The
    /// processor-state stack can name a push that the saved-register stack can only call new,
    /// so its name is used where it has one; a push neither stack knows about is shown as
    /// unknown rather than left out.
    /// </summary>
    private static IReadOnlyList<string> Pushes(ProgramAnalysis analysis, SavedStack? saved, AnalysisStack? bytes)
    {
        List<SavedPush> pushes = saved is null ? [] : [.. saved.Pushes.Reverse()];
        IReadOnlyList<StackEntry> entries = bytes?.Entries ?? [];
        var rows = new List<string>();
        var top = entries.Count - 1;
        var i = 0;
        while (i < pushes.Count || top >= 0)
        {
            var wide = i < pushes.Count ? Bytes(pushes[i]) : null;
            var group = top >= 0 ? Group(analysis, entries, top, wide) : (Bytes: 0, Name: (string?)null);
            top -= group.Bytes;

            // One group of bytes may span several pushes: a `.frame` names all the pushes it
            // covers, and the point of a frame is to read them as one thing.
            var first = i;
            for (var covered = 0; i < pushes.Count && (i == first || covered < group.Bytes); i++)
            {
                if (Bytes(pushes[i]) is not { } more)
                {
                    i++;
                    break;
                }
                covered += more;
            }

            // Use the saved-register stack's description only when this row covers exactly one
            // of its pushes.
            var push = i == first + 1 && first < pushes.Count ? pushes[first] : (SavedPush?)null;
            var text = group.Name ?? (push is { } held ? Held(held.Value, null) : "unknown");

            // Only the 65816 pushes a register whose width the caller cannot read off the CPU,
            // and only there does the width decide whether a pull gets the value back.
            if (analysis.Cpu == Cpu.Wdc65816 && push is { Size: PushSize.Accumulator or PushSize.Index, Width: var width }
                && width is Width.Eight or Width.Sixteen)
            {
                text += width == Width.Sixteen ? ", 16-bit" : ", 8-bit";
            }
            rows.Add(text);
        }
        return rows;
    }

    /// <summary>How many bytes a push took, or null where the width it goes by is not known.</summary>
    private static int? Bytes(SavedPush push) => push.Size switch
    {
        PushSize.OneByte => 1,
        PushSize.TwoBytes => 2,
        _ => push.Width switch
        {
            Width.Eight => 1,
            Width.Sixteen => 2,
            _ => (int?)null,
        },
    };

    /// <summary>
    /// The push whose top byte is <paramref name="top"/>: how many bytes it took, and what the
    /// processor-state analysis knows it holds, or a null name where it knows nothing.
    /// <paramref name="hint"/> is how many bytes the saved-register stack says the push took,
    /// which is used as the size of a push the processor-state stack knows nothing about.
    /// </summary>
    private static (int Bytes, string? Name) Group(
        ProgramAnalysis analysis, IReadOnlyList<StackEntry> entries, int top, int? hint)
    {
        if (Framed(analysis, entries, top) is { } frame)
            return frame;
        var entry = entries[top];
        if (entry.IsStatus)
            return (1, $"status {ProcessorState.Spell("a", entry.A)}, {ProcessorState.Spell("i", entry.Index)}");
        if (entry is { Size: > 0, Byte: 0, Held.IsKnown: true } && entry.Size <= top + 1)
            return (entry.Size, StateValue.Hex(entry.Held.Value, entry.Size * 2));
        return (hint is { } wide && wide <= top + 1 ? wide : 1, null);
    }

    /// <summary>
    /// The <c>.frame</c> the byte at <paramref name="top"/> belongs to, as one push however
    /// many bytes it covers; null when it belongs to none. A frame is marked on its lowest byte
    /// only; how far up it extends is the size of the type it was declared with.
    /// </summary>
    private static (int Bytes, string? Name)? Framed(
        ProgramAnalysis analysis, IReadOnlyList<StackEntry> entries, int top)
    {
        for (var i = top; i >= 0; i--)
        {
            if (entries[i].Frame is not { } frame)
                continue;
            return i + (Room(analysis, frame) ?? 1) > top ? (top - i + 1, $"frame {frame.DisplayName}") : null;
        }
        return null;
    }

    /// <summary>
    /// How many bytes a <c>.frame</c> covers: the size of the type it was declared with. The
    /// symbol may not carry its resolved type, since nothing else asks for it, so when it does
    /// not the size is looked up in the model of the file that declares the frame.
    /// </summary>
    private static long? Room(ProgramAnalysis analysis, Symbol frame) => frame.Type?.Size
        ?? (frame.TypeExpression is { } named
            ? analysis.ModelFor(frame.Tree.Path)?.SymbolOf(named)?.Size
            : null);

    /// <summary>
    /// What one register, or one push, may hold. It is a set rather than one answer: a place
    /// two paths reach may hold the entry value on one of them and something loaded on the
    /// other, and a reader told only that it is not known cannot see the save that is still
    /// good. The words are joined rather than collapsed, in that order:
    /// <code>
    /// A  X as entered
    /// X  as entered, or new
    /// Y  new
    /// C  new, or unknown
    /// </code>
    /// <para>
    /// A 6502 saves X through the accumulator, so the entry value is named by the register it
    /// came from rather than the one holding it: after <c>txa</c> the accumulator holds what X
    /// was entered with, and so does the byte a <c>pha</c> puts on the stack.
    /// </para>
    /// </summary>
    /// <param name="value">What may be held.</param>
    /// <param name="own">The register holding it, whose own entry value needs no naming; null for a push.</param>
    private static string Held(RegisterValue value, Registers? own)
    {
        var words = new List<string>();
        if (own is { } self && value.Entry == self)
            words.Add("as entered");
        else if (value.Entry != Registers.None)
        {
            words.Add(
                string.Join(" or ", RegisterEffects.Each(value.Entry).Select(RegisterEffects.Spell)) + " as entered");
        }
        if (value.IsWritten)
            words.Add("new");
        if (value.IsUnknown || words.Count == 0)
            words.Add("unknown");
        return string.Join(", or ", words);
    }

    /// <summary>
    /// The registers a routine or a block preserves, formatted as both the lens and the hover
    /// show them: <c>A, X, Y, C</c>, <c>X, Y</c>, <c>none</c>. It is a bare list rather than a
    /// sentence, because it is read at a glance. A lens writes <c>preserves</c> in front of it;
    /// a hover has a key beside it that says as much.
    /// <para>
    /// What nt65 works out is a lower bound, so where a call could not be followed the list ends with
    /// <c>?</c>: those registers and perhaps more, which is what <c>?</c> means everywhere else.
    /// </para>
    /// </summary>
    internal static string Spell(Registers kept, bool complete)
    {
        var names = RegisterEffects.Each(kept).Select(RegisterEffects.Spell).ToList();
        if (!complete)
            names.Add("?");
        return names.Count == 0 ? "none" : string.Join(", ", names);
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
        model.ReferenceAt(position) is not { } asked
            ? []
            : [.. program.ReferencesTo(asked.Symbol)
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
    /// one edit per line that changes. Layout is computed over the whole file, but only edits
    /// for these lines are returned, which is what a client formatting a selection wants.
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
        foreach (var byFile in Renamed(program, model, reference).GroupBy(found => found.File))
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
        // A mnemonic may be used as a name (it draws a warning, not an error), so the only
        // reserved words a rename refuses are register names.
        if (!symbol.IsCheapLocal && SyntaxFacts.IsRegister(name))
            return $"`{newName}` is a register name and cannot be used as a name";

        var taken = symbol.IsCheapLocal ? symbol.Scope.FindCheapLocal(name) : (alias ?? symbol.Scope).FindMember(name);
        return taken is null || taken == symbol ? null : $"`{newName}` is already declared in this scope";
    }

    /// <summary>Everywhere the symbol at <paramref name="position"/> is written in this file.</summary>
    private static IReadOnlyList<SymbolReference> Occurrences(SemanticModel model, int position) =>
        model.ReferenceAt(position) is { } reference ? model.ReferencesTo(reference.Symbol) : [];

    /// <summary>
    /// The names a rename of <paramref name="reference"/> writes over. An alias from
    /// <c>.use ... as</c> is written in place of the symbol's own name, so it is renamed
    /// separately: renaming the symbol leaves aliases alone, and renaming an alias renames only
    /// the uses in this file written with that same alias.
    /// </summary>
    private static IEnumerable<(SemanticModel File, SymbolReference Reference)> Renamed(
        ProgramModel program, SemanticModel model, SymbolReference reference)
    {
        // A model kept from before an edit elsewhere still refers to the symbols the edited file
        // declared then, so symbols are compared by their current versions.
        var symbol = program.Current(reference.Symbol);
        if (symbol.Bound?.Value.Member is { } member)
            symbol = member;
        if (reference.IsAlias)
        {
            var written = model.Tree.Text.Substring(reference.Span.Start, reference.Span.Length);
            return model.References
                .Where(other => other.IsAlias && program.Current(other.Symbol) == symbol
                    && model.Tree.Text.Substring(other.Span.Start, other.Span.Length) == written)
                .Select(other => (model, other));
        }

        // A family instance may be named after an enum member, so renaming either one renames
        // the member and every use of every instance named after it.
        var renamed = new HashSet<Symbol> { symbol };
        if (symbol.IsEnumMember)
        {
            foreach (var file in program.Files)
                renamed.UnionWith(file.Symbols.Where(instance => instance.Bound?.Value.Member == symbol));
        }
        return program.ReferencesTo(renamed).Where(found => !found.Reference.IsAlias);
    }

    /// <summary>
    /// A value as the hover grid shows it. A number is written in hexadecimal, which is how an
    /// address or a mask is read; since it may also be a count, the decimal is shown beside it
    /// from ten upward, below which the two are the same digit.
    /// </summary>
    private static string Spell(Value value) => value.AsNumber() is { } number && number >= 10
        ? $"{value} ({number.ToString(CultureInfo.InvariantCulture)})"
        : value.ToString();

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
        diagnostic.Id,
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

    /// <summary>
    /// A logical path converted back to a URI, as for a diagnostic that points into another
    /// file. A Windows path parses as an absolute URI, drive letter and all, wherever nt65 is
    /// running; a rooted Unix path does not parse as a URI on any host, so a file URI is built
    /// for it. A relative path is one the editor supplied and is returned unchanged.
    /// </summary>
    internal static string ToUri(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsoluteUri
            : path.StartsWith('/')
                ? new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = path }.Uri.AbsoluteUri
                : path;

    /// <summary>
    /// One hover's text, in the order it is read: the line under the caret in the language's
    /// own syntax, the comment its author left above it, the facts that kind of thing is asked
    /// about most, a rule, and everything else the analysis worked out under it. A zone with
    /// nothing in it is left out, and where nothing leads there is no rule either.
    /// <para>
    /// The caller decides which keys lead. Rows keep the order they were written in on both
    /// sides of the rule, so a fact appears in the same relative place from one hover to the
    /// next. Both grids pad their keys to the same width, so the values line up across the rule.
    /// </para>
    /// <para>
    /// A row whose fact is not known is left out rather than written as unknown, so the grid
    /// is only what is known.
    /// </para>
    /// </summary>
    /// <param name="headline">The line under the caret, as the language writes it.</param>
    /// <param name="asked">The keys of the rows that stand above the rule.</param>
    private sealed class Card(string headline, IReadOnlySet<string> asked)
    {
        /// <summary>How many spaces separate the longest key from the values.</summary>
        private const int Gutter = 2;

        /// <summary>
        /// The rows, a null standing for a blank line between two groups of them, each marked
        /// with whether it leads.
        /// </summary>
        private readonly List<((string Key, string Value)? Row, bool Leads)> rows = [];

        private string? prose;

        /// <summary>Whether the last row written leads; a following row with an empty key inherits this.</summary>
        private bool leading;

        /// <summary>
        /// One row of the grid, left out when there is nothing to say. A row with an empty key
        /// continues the row above it, which is how one fact spans several lines.
        /// </summary>
        public void Row(string key, string? value)
        {
            if (value is not { Length: > 0 })
                return;
            if (key.Length > 0)
                leading = asked.Contains(key);
            rows.Add(((key, value), leading));
        }

        /// <summary>A blank line between two groups of rows, which keeps them in the same columns.</summary>
        public void Gap() => rows.Add((null, leading));

        /// <summary>The comment written above the declaration, which is what its author had to say.</summary>
        public void Prose(string? written) => prose = written;

        /// <inheritdoc/>
        public override string ToString()
        {
            var column = (rows.Count == 0 ? 0 : rows.Max(row => row.Row?.Key.Length ?? 0)) + Gutter;

            // A gap before the first row of a group would open the grid with a blank line, and
            // one after the last would close it with one.
            var lead = Trimmed(rows.Where(row => row.Leads));
            var rest = Trimmed(rows.Where(row => !row.Leads));

            // The headline, the comment and the leading rows stay together; the single rule
            // separates them from the supporting detail below.
            var above = new List<string>();
            if (headline.Length > 0)
                above.Add($"```nt65\n{headline}\n```");
            if (prose is { Length: > 0 })
                above.Add(prose);
            if (lead.Count > 0)
                above.Add(Written(lead, column));
            var answer = string.Join("\n\n", above);
            return rest.Count == 0
                ? answer
                : answer.Length == 0 ? Written(rest, column) : $"{answer}\n---\n{Written(rest, column)}";
        }

        private static IReadOnlyList<(string Key, string Value)?> Trimmed(
            IEnumerable<((string Key, string Value)? Row, bool Leads)> group)
        {
            var rows = group.Select(row => row.Row).ToList();
            while (rows.Count > 0 && rows[0] is null)
                rows.RemoveAt(0);
            while (rows.Count > 0 && rows[^1] is null)
                rows.RemoveAt(rows.Count - 1);
            return rows;
        }

        private static string Written(IReadOnlyList<(string Key, string Value)?> rows, int column) =>
            $"```{Grid}\n"
                + string.Join("\n", rows.Select(row => row is { } has ? has.Key.PadRight(column) + has.Value : ""))
                + "\n```";
    }
}
