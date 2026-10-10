using System.Globalization;
using Norristown.Flow;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Builds what the editor shows at a position, which is the name under the caret or, where there
/// is none, the <c>.scope</c> block the line opens or the instruction on it.
/// <para>
/// Every hover reads from the top down, and each section is omitted when it is empty. The
/// sections are the line under the caret in nt65 syntax, the comment its author left above
/// it, the one or two facts most often wanted for that kind of thing, a horizontal rule, and
/// below it everything else the analysis worked out. Nothing is dropped for being far down.
/// The first screenful answers the question and the rest is supporting detail.
/// </para>
/// </summary>
internal static class Hovers
{
    /// <summary>
    /// The column on the <c>cycles</c> row where the enclosing block's count starts, after the
    /// line's own.
    /// </summary>
    private const int BlockColumn = 10;

    /// <summary>
    /// The width allowed for the block's count, so that the reason after it always starts in the
    /// same column.
    /// </summary>
    private const int ReasonColumn = 14;

    /// <summary>
    /// The keys of the rows that lead an instruction's hover, which give what a reader hovers an
    /// instruction to learn. These are its cycle count and the processor state on entry, which
    /// sized its operand. The flags it changes, the register contents and the stack are
    /// supporting detail and go below the rule.
    /// </summary>
    private static readonly IReadOnlySet<string> TimingAsked = Keys("cycles", "state");

    /// <summary>
    /// The keys of the rows that lead an <c>.ensure</c>'s hover, which a reader hovers to see
    /// what it became. The line states the requirement, not the instructions it emits.
    /// </summary>
    private static readonly IReadOnlySet<string> EnsureAsked = Keys("writes", "state");

    /// <summary>
    /// The keys of the rows that lead an inline <c>.scope</c>'s hover, which are the only rows it
    /// has.
    /// </summary>
    private static readonly IReadOnlySet<string> ScopeAsked = Keys("cost", "excluding", "preserves");

    /// <summary>
    /// Returns what to show at <paramref name="position"/>, or null where there is nothing to say.
    /// </summary>
    public static Protocol.Hover? At(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var flow = analysis.FlowFor(model.Tree.Path);
        return (model.ReferenceAt(position) ?? InstanceAt(model, position)) is { } reference
            ? ToName(analysis, model, reference)
            : ToPlaced(analysis, model, position) ?? ToAllowed(model, position) ?? ToComparedWord(model, position)
                ?? ToParameterKind(analysis, model, position) ?? ToScope(model, flow, position)
                ?? ToTiming(analysis, model, flow, position);
    }

    /// <summary>
    /// Describes what one register, or one push, may hold. The description is a set rather than
    /// one answer. A place two paths reach may hold the entry value on one of them and something
    /// loaded on the other, and a reader told only that it is not known cannot see the save that
    /// is still good. The words are joined rather than collapsed, in this order:
    /// <code>
    /// A  X as entered
    /// X  as entered, or new
    /// Y  new
    /// C  new, or unknown
    /// </code>
    /// <para>
    /// A 6502 saves X through the accumulator, so the entry value is named by the register it
    /// came from rather than the one holding it. After <c>txa</c> the accumulator holds what X
    /// was entered with, and so does the byte a <c>pha</c> puts on the stack.
    /// </para>
    /// </summary>
    /// <param name="value">What may be held.</param>
    /// <param name="own">
    /// The register holding it, whose own entry value needs no naming, or null for a push.
    /// </param>
    internal static string Held(RegisterValue value, Registers? own)
    {
        var words = new List<string>();
        if (own is { } self && value.Entry == self)
            words.Add("as entered");
        else if (value.Entry != Registers.None)
        {
            words.Add(
                string.Join(" or ", RegisterEffects.Each(value.Entry).Select(RegisterEffects.Format)) + " as entered");
        }
        if (value.IsWritten)
            words.Add("new");
        if (value.IsUnknown || words.Count == 0)
            words.Add("unknown");
        return string.Join(", or ", words);
    }

    /// <summary>
    /// Formats the registers a routine or a block preserves or reads as both the lens and the hover
    /// show them, such as <c>A, X, Y, C</c>, <c>X, Y</c> or <c>none</c>. It is a bare list rather
    /// than a sentence, because it is read at a glance. A lens puts <c>preserves</c> or
    /// <c>reads</c> in front of it, and a hover has a key beside it that says as much.
    /// <para>
    /// Where a call could not be followed, the list ends with <c>?</c>. That means those registers
    /// and perhaps more, which is what <c>?</c> means everywhere else.
    /// </para>
    /// </summary>
    internal static string Format(Registers kept, bool complete)
    {
        var names = RegisterEffects.Each(kept).Select(RegisterEffects.Format).ToList();
        if (!complete)
            names.Add("?");
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    /// <summary>
    /// Formats the registers a routine reads as both the lens and the hover show them, as
    /// <see cref="Format(Registers, bool)"/> does. Where the routine passes control to code nt65 cannot follow, and
    /// the answer is still complete because none of the registers holds an entry value there, the
    /// registers it does not read are named, such as <c>none of A, X, Y, C, Z, N, V</c> or
    /// <c>X · none of A, Y, C, Z, N, V</c>. That code may use what the caller left elsewhere, so a
    /// bare <c>none</c> would claim more than nt65 knows.
    /// </summary>
    /// <param name="reads">What the routine was found to read.</param>
    /// <returns>The registers, as a bare list.</returns>
    internal static string Format(RoutineReads reads)
    {
        var unread = Registers.All & ~reads.Read;
        if (!reads.Complete || !reads.Bounded || unread == Registers.None)
            return Format(reads.Read, reads.Complete);
        var none = $"none of {string.Join(", ", RegisterEffects.Each(unread).Select(RegisterEffects.Format))}";
        return reads.Read == Registers.None ? none : $"{Format(reads.Read, true)} · {none}";
    }

    /// <summary>
    /// Formats the registers a routine preserves where its signature promises some of them with
    /// <c>keeps</c>, such as <c>keeps X · also preserves Y, C, V (inferred)</c>. The promise comes
    /// first. The registers the analysis only found unchanged follow, marked as inferred, because
    /// a caller may not rely on them and an edit to the routine can change them.
    /// </summary>
    /// <param name="kept">The registers the analysis found the routine returns unchanged.</param>
    /// <param name="complete">A value indicating whether the analysis followed every call.</param>
    /// <param name="promised">The registers the routine's <c>keeps</c> promises.</param>
    /// <param name="also">The words in front of the inferred registers.</param>
    internal static string Promised(Registers kept, bool complete, Registers promised, string also)
    {
        var observed = kept & ~promised;
        var text = $"keeps {Format(promised, true)}";
        return observed == Registers.None && complete ? text : $"{text} · {also} {Format(observed, complete)} (inferred)";
    }

    /// <summary>Formats a cycle count as it is shown, such as <c>4 cycles</c> or <c>4-5 cycles</c>.</summary>
    internal static string Format(CycleCount cycles) =>
        cycles is { IsExact: true, Minimum: 1 } ? "1 cycle" : $"{cycles} cycles";

    /// <summary>
    /// Formats a value as the hover grid shows it. A number is shown in hexadecimal, which is how
    /// an address or a mask is read. Since it may also be a count, the decimal is shown beside it
    /// from ten upward, because below ten the two are the same digit.
    /// </summary>
    private static string Format(Value value) => value.AsNumber() is { } number && number >= 10
        ? $"{value} ({number.ToString(CultureInfo.InvariantCulture)})"
        : value.ToString();

    /// <summary>Formats an address size in the language's syntax.</summary>
    private static string Format(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "zp",
        AddressSize.Absolute => "abs",
        _ => "far",
    };

    /// <summary>
    /// Returns a reference that treats the name of a <see cref="Family"/>'s <c>.proc</c> inside an
    /// <c>.each</c> as the declaration of the family's binding, or null anywhere else. The name
    /// declares every instance, so its hover answers what the <c>.multiproc</c> form answers on
    /// its binding.
    /// </summary>
    private static SymbolReference? InstanceAt(SemanticModel model, int position)
    {
        foreach (var family in model.Families)
        {
            if (family.Instances.FirstOrDefault(instance => instance.Tree == model.Tree) is { } instance
                && position >= instance.NameSpan.Start && position <= instance.NameSpan.End)
            {
                return new SymbolReference(family.Binding, instance.NameSpan, IsDeclaration: true);
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the hover for the module a <c>.place</c> names, when the caret is on its path.
    /// The hover gives the module's file, what its declaration says about placing it, and which
    /// translation unit's output contains it.
    /// </summary>
    private static Protocol.Hover? ToPlaced(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        if (Lsp.PlacedAt(analysis, model, position) is not { } placed)
            return null;
        var placements = analysis.Placements;
        var card = new HoverCard($"module {placed.Path}", Keys("declared", "written in"));
        card.Row("declared", placements.DeclaredFor(placed.Tree) switch
        {
            ModulePlacement.Placed => "placed (its bytes are emitted where another module places it with `.place`)",
            ModulePlacement.Placeable => "placeable (at most one module may place it with `.place`; if none does, it gets its own output)",
            _ => "alone (it has its own output, and no module places it)",
        });
        if (placements.UnitOf(placed.Tree) is { IsPlaced: true, Root: var root }
            && analysis.ModelFor(root.Path)?.FileScope.Module is { } unit)
        {
            card.Row("written in", $"the output of `{unit}`");
        }
        card.Row("file", placed.Tree.Path);
        return Hover(card, model.Tree, placed.Span);
    }

    /// <summary>
    /// Returns the hover for the name in an <c>.allow</c>, which explains the warning it allows and
    /// gives the lines it covers and the reason it was written, or null anywhere else.
    /// </summary>
    private static Protocol.Hover? ToAllowed(SemanticModel model, int position)
    {
        var token = model.Tree.Root.FindToken(position);
        if (token.Parent is not AllowDirectiveSyntax allow || token.Span != allow.Name.Span
            || Literals.Text(token.Text) is not { } name || Catalogue.Find(name) is not { } descriptor)
        {
            return null;
        }
        var card = new HoverCard(allow.GetText().Trim(), Keys("covers", "reason"));
        card.Prose(descriptor.Explanation);
        if (model.Allowances.FirstOrDefault(allowance => allowance.Directive == allow) is { Covered: var covered })
        {
            card.Row("covers", covered.StartLine == covered.EndLine
                ? $"line {covered.StartLine + 1}"
                : $"lines {covered.StartLine + 1}-{covered.EndLine + 1}");
        }
        card.Row("reason", allow.Reason is { IsMissing: false } reason ? Literals.Text(reason.Text) : null);
        return Hover(card, model.Tree, token.Span);
    }

    /// <summary>
    /// Returns the hover for a bare word that a condition compares a parameter's argument with,
    /// such as <c>imm</c> in <c>.mode(src) == imm</c> or <c>x</c> in <c>reg == x</c>. The hover
    /// says what the word is, what it is compared with and what that parameter accepts. When the
    /// parameter can never take that value, it also says that the comparison never holds.
    /// </summary>
    private static Protocol.Hover? ToComparedWord(SemanticModel model, int position)
    {
        if (ComparedWords.At(model, position) is not { } compared)
            return null;
        var (word, isMode) = (compared.Word, compared.IsMode);
        var text = word.Text.ToLowerInvariant();
        var card = new HoverCard(isMode ? $"mode {text}" : $"word {word.Text}", Keys("mode", "never"));
        if (isMode)
            card.Row("mode", ParameterKinds.Mode(text) is { } meaning ? $"{text}: {meaning}" : null);
        card.Row("compared with", compared.Compared);
        card.Row("takes", ParameterKinds.Takes(compared.Accepts));
        if (!compared.CanHold)
        {
            card.Row("never", isMode && !ComparedWord.Modes.Contains(text)
                ? $"{text} is not a mode .mode gives: {string.Join(", ", ComparedWord.Modes)}"
                : $"{compared.Name} is never {word.Text}: it may be {string.Join(", ", compared.Choices)}");
        }
        return Hover(card, model.Tree, word.Span);
    }

    /// <summary>
    /// Returns the hover for the kind after a macro parameter's <c>:</c>, wherever in the kind
    /// the caret is. It is the parameter's own hover, which says what the kind accepts, plus, for
    /// a mode listed in <c>operand(...)</c>, how an operand in that mode is written. A kind is
    /// made of keywords rather than names, so there is no symbol reference there to answer from.
    /// The exception is the enum an enum kind names, which is a reference and gets its own hover.
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
            && ParameterKinds.Mode(token.Text) is { } meaning
                ? $"{token.Text.ToLowerInvariant()}: {meaning}"
                : null;
        var hover = ToName(analysis, model, declared, mode);
        return hover with { Range = Lsp.ToRange(model.Tree, token.Span) };
    }

    /// <summary>
    /// Returns what an editor shows about a name, which is the line that declares it, the facts
    /// the analysis worked out, and the comment above it. What a routine costs and which registers
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
        var card = new HoverCard(Headline(symbol, model.Tree), Asked(symbol));
        card.Prose(DocComments.Of(symbol));

        // For a name from another module, show that module's file: it holds the declaration and
        // the `.export` that makes the name visible here.
        if (symbol.Tree != model.Tree)
            card.Row("from", symbol.Tree.Path[(symbol.Tree.Path.LastIndexOf('/') + 1)..]);

        // A name that no qualified path can reach is shown unqualified, so the hover names the
        // routine or scope it is private to instead.
        if (!symbol.IsReachableByPath && symbol.Scope.NearestNamed()?.Name is { } owner)
            card.Row("private to", owner);

        // For a macro parameter, describe in words what arguments its kind accepts.
        card.Row("mode", mode);
        if (symbol.Parameter is { } parameter)
            card.Row("takes", ParameterKinds.Takes(parameter.Accepts));
        if (symbol.Kind == SymbolKind.Member)
            card.Row("offset", symbol.Value.ToString());
        else if (symbol.Value.IsKnown)
            card.Row("value", Format(symbol.Value));
        else if (symbol.Kind == SymbolKind.Func && Called(model, reference) is { IsKnown: true } called)
            card.Row("value", Format(called));
        Stage(card, analysis.Configuration, symbol);
        if (symbol.Type is { } type)
            card.Row("type", type.QualifiedName);

        // A routine's cost and preserved registers are what a caller wants to know, so they come
        // before where the routine lives. What a macro call expands to is the same question
        // asked of a macro, so its row goes here too.
        Routine(card, analysis, symbol);

        // A position inside an instruction runs the bytes there as other instructions, which the
        // source does not show, and neither does it show what they cost. Each instruction is
        // shown with its own cycles, and the run with its total.
        if (symbol.Kind == SymbolKind.Label
            && analysis.LayoutFor(symbol.Tree.Path)?.HiddenInstructionsOf(symbol) is { Count: > 0 } hidden)
        {
            card.Row("runs", string.Join("; ", hidden.Select(each => $"{each} ({each.Cycles?.ToString() ?? "no count"})")));
            CycleCount? total = new CycleCount(0);
            foreach (var each in hidden)
                total = total is { } sum && each.Cycles is { } cycles ? sum + cycles : null;
            card.Row("cycles", total is { } known ? $"{known} in all" : null);
        }
        var expansion = MacroCallHover.At(analysis, model, reference);
        card.Row("expands to", expansion?.Becomes());

        // Data found elsewhere may take its element type from its address, which its declaration
        // does not show.
        if (symbol is { Kind: SymbolKind.AddressAlias, ValueExpression.Parent: DataDeclarationSyntax { Directive: null } })
            card.Row("element", symbol.Data is DataDirectiveSyntax element ? ElementType(symbol, element) : "none, so it has no size");

        // Size and element count answer one question, so they share a row. A count of one is
        // what a declaration without a count means, so it is not shown.
        if (symbol.Size is { } room)
        {
            card.Row("size", $"{room} byte{(room == 1 ? "" : "s")}"
                + (symbol.Count is > 1 and { } count && symbol.Kind != SymbolKind.Member ? $" x {count}" : ""));
        }
        // The segment is where the bytes are laid out. A name given its address with `=`, such as
        // a routine in ROM, has no bytes here, so the segment its line sits in says nothing.
        if (symbol.AddressSize is { } size)
        {
            card.Row("address", $"{Format(size)} ({(int)size} byte{((int)size == 1 ? "" : "s")})"
                + (symbol is { IsAddress: true, ValueExpression: null, Segment: { } segment } ? $" in {segment}" : ""));
        }
        Declares(card, model, reference);
        OutputName(card, analysis, symbol);

        // A macro call is the only name whose hover says more than its declaration does: what
        // it expands to. The one expansion computed above gives the summary row, and
        // MacroCallHover appends its listing to the hover text here.
        return new Protocol.Hover(
            Protocol.MarkupContent.Markdown(MacroCallHover.Added(card.ToString(), expansion, model, reference)),
            Lsp.ToRange(model.Tree, reference.Span));
    }

    /// <summary>
    /// Returns the element type that data found elsewhere took from its address, such as
    /// <c>.byte</c> or <c>.type Pos</c>.
    /// </summary>
    private static string ElementType(Symbol symbol, DataDirectiveSyntax element) =>
        element.IsRecord && symbol.Type is { } type ? $".type {type.QualifiedName}" : element.Directive.Text.ToLowerInvariant();

    /// <summary>
    /// Returns the keys of the rows most wanted for this kind of name, which lead the hover. Every
    /// other row goes below the rule, in the order the hover adds them. A reader hovers a constant
    /// for its value, a member for its offset, a routine for what a call to it costs and which
    /// registers it preserves, and a name from another module to find out which module.
    /// </summary>
    private static IReadOnlySet<string> Asked(Symbol symbol)
    {
        var kind = symbol.Kind switch
        {
            SymbolKind.Member => new[] { "offset" },
            SymbolKind.Proc or SymbolKind.ExternProc => ["cost", "excluding", "reads", "preserves", "inferred"],

            // At a call, a reader hovers a function to see the value of the call.
            SymbolKind.Func => ["value"],
            // A reader hovers a macro to see what the call expands to, so that row leads. The key
            // is listed even where the row is absent, as at the declaration.
            SymbolKind.Macro => ["expands to"],
            // A family's binding is where its routines' costs are shown, since they get no lens.
            SymbolKind.Binding => ["declares", "cost", "excluding", "reads", "preserves"],
            SymbolKind.MacroParameter => ["mode", "takes"],
            SymbolKind.Label => ["runs", "address", "size"],
            SymbolKind.Data or SymbolKind.List or SymbolKind.Charmap or SymbolKind.Frame
                or SymbolKind.ImportedAddress => ["address", "size"],
            SymbolKind.AddressAlias => ["address", "element", "size"],
            SymbolKind.Struct or SymbolKind.Union or SymbolKind.Enum => ["size"],

            // Whether an `.if` may test a constant, and where a setting's value came from, go
            // with its value.
            SymbolKind.Constant => ["value", "known", "setting"],
            _ => ["value"],
        };
        return Keys(["from", "private to", .. kind]);
    }

    /// <summary>Returns the keys of the rows that lead a hover, compared as they are written.</summary>
    private static IReadOnlySet<string> Keys(params string[] keys) => new HashSet<string>(keys, StringComparer.Ordinal);

    /// <summary>Returns a hover that shows <paramref name="card"/> over <paramref name="span"/> of <paramref name="tree"/>.</summary>
    private static Protocol.Hover Hover(HoverCard card, SyntaxTree tree, TextSpan span) =>
        new(Protocol.MarkupContent.Markdown(card.ToString()), Lsp.ToRange(tree, span));

    /// <summary>
    /// Adds what the configuration makes of a constant at file level, which is whether the build
    /// set a setting or left it at its default, and whether an <c>.if</c> may test any other
    /// constant. The program's configuration is asked rather than a file's, because a file an
    /// edit did not reach keeps the configuration of the analysis before the edit.
    /// </summary>
    private static void Stage(HoverCard card, Configuration configuration, Symbol symbol)
    {
        if (symbol.Kind != SymbolKind.Constant || symbol.Scope.Kind != ScopeKind.File)
            return;
        if (symbol.IsSetting)
        {
            card.Row("setting", configuration.Gives(symbol.Tree, symbol.Name) ? "set by the build" : "its default, which the build may set");
            return;
        }
        if (configuration.DecidesValue(symbol.Tree, symbol.Name) is { } decided)
            card.Row("known", decided ? "decided by the configuration" : "once the declarations are read");
    }

    /// <summary>
    /// Returns the value of the call a function's name appears in, which may be text, or an unknown
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
    /// Returns the line a name is declared on, as it appears in the source, with the name replaced
    /// by the one a reader would write for it. Where the declaration is not a line of its own (a
    /// member of a layout, a macro parameter, the name a repetition binds and the instances it
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
    /// Returns the declaring line with the name on it replaced by <paramref name="named"/>, since
    /// outside the declaring scope or module a reader has to write a longer, qualified name than
    /// the one on the line.
    /// </summary>
    private static string Declaring(Symbol symbol, string named)
    {
        var tree = symbol.Tree;
        var index = tree.GetLineIndex(symbol.NameSpan.Start);
        var start = tree.LineStarts[index];

        // The line's code ends where its trailing trivia starts, so the comment is left out.
        var line = tree.Text[start..LineContext.CodeEnd(tree, index)];
        var at = symbol.NameSpan.Start - start;

        // A cheap local appears with an `@` that its symbol name lacks, so it is not
        // replaced; the line already shows it the way a reader would write it.
        if (!symbol.IsCheapLocal && at >= 0 && at + symbol.NameSpan.Length <= line.Length
            && line.AsSpan(at, symbol.NameSpan.Length).SequenceEqual(symbol.Name.AsSpan()))
        {
            line = line[..at] + named + line[(at + symbol.NameSpan.Length)..];
        }
        return Headline(line);
    }

    /// <summary>
    /// Returns a line's code as a headline shows it, without the indentation before it or the
    /// brace that opens the block it heads. Neither is what was asked about. The code holds no
    /// comment, since a comment is trivia that the code's span leaves out.
    /// </summary>
    private static string Headline(string code) => code.TrimEnd().TrimEnd('{').Trim();

    /// <summary>
    /// Adds the cost and preserved-register rows for a routine, wherever its name appears. A code
    /// lens shows the same above the declaration, but an editor can be told to hide lenses, and a
    /// lens is nowhere near a call anyway. A family's routines get no lens at all, so for the name
    /// a family binds these are the rows of each of its instances. The control flow used is the
    /// declaring file's, since a call from another file is not part of it.
    /// </summary>
    private static void Routine(HoverCard card, ProgramAnalysis analysis, Symbol symbol)
    {
        if (analysis.FlowFor(symbol.Tree.Path) is not { } flow)
            return;
        // Routines are matched by name, not by position. Every instance of a family is declared
        // at the one binding, so a position would match them all, whereas one instance named at
        // a call means that instance alone.
        var names = symbol.Kind == SymbolKind.Binding
            ? (analysis.ModelFor(symbol.Tree.Path)?.Families ?? [])
                .Where(family => family.Binding.Tree == symbol.Tree && family.Binding.NameSpan == symbol.NameSpan)
                .SelectMany(family => family.Instances)
                .Select(instance => instance.FlatName)
                .ToHashSet(StringComparer.Ordinal)
            : [symbol.FlatName];
        var found = flow.Regions
            .Where(region => region.Routine.Tree == symbol.Tree && names.Contains(region.Routine.FlatName))
            .Select(region => (
                region.Routine.Name,
                Cost: CodeLenses.Format(region.Cost, region.Total, "never returns", false),
                Excluded: region.Cost.IsKnown ? region.Total.Excluded ?? [] : [],
                Read: Format(region.Reads),
                Kept: region.Total.Ends ? Preserved(region, "also") : null))
            .ToList();
        Rows(card, "cost", found.Select(region => (region.Name, region.Cost)));

        // Each item the cost with calls leaves out is listed once, however many instances leave
        // it out, with the reason, which the lens has no room for.
        var excluded = found.SelectMany(region => region.Excluded).DistinctBy(exclusion => exclusion.What).ToList();
        for (var i = 0; i < excluded.Count; i++)
            card.Row(i == 0 ? "excluding" : "", $"{excluded[i].What}: {excluded[i].Why}");
        Rows(card, "reads", found.Select(region => (region.Name, (string?)region.Read)));
        Rows(card, "preserves", found.Select(region => (region.Name, region.Kept)));

        // The name a family binds is not a routine, and its instances are reached each on its
        // own, so it has no one context to show.
        if (symbol.Kind != SymbolKind.Binding)
        {
            var contexts = analysis.Contexts();
            card.Row("context", RunsFrom.Hover(contexts, symbol));
            card.Row("not followed", RunsFrom.Unfollowed(contexts, symbol));
        }

        // The parts of the state a routine leaves to be inferred are what it is entered with
        // and leaves without saying so, which the signature on its line does not show.
        if (analysis.Cpu == Cpu.Wdc65816 && symbol.Signature is { IsInterrupt: false } declared
            && flow.Signatures.Of(symbol) is { } inferred)
        {
            var home = symbol.Segment is { } segment ? analysis.ModelFor(symbol.Tree.Path)?.Segments.Find(segment)?.Bank : null;
            var (entry, exit) = InferredState.Items(declared, inferred, home);
            card.Row("inferred", InferredState.Format(entry, exit));
        }
    }

    /// <summary>
    /// Formats the registers a routine preserves as the hover's <c>preserves</c> row and the
    /// lens show them. The registers its <c>keeps</c> promises are set apart from the ones that
    /// are only inferred.
    /// </summary>
    /// <param name="region">The routine's region.</param>
    /// <param name="also">The words in front of the inferred registers where some are promised.</param>
    internal static string Preserved(FlowRegion region, string also) =>
        region.Routine.Signature?.Keeps is { } promised and not Registers.None
            ? Promised(region.Registers.Kept, region.Registers.Complete, promised, also)
            : Format(region.Registers.Kept, region.Registers.Complete);

    /// <summary>
    /// Returns each processor state that reaches a line, formatted by <paramref name="format"/>,
    /// with the number of expansions it reaches, where the expansions of a macro body or a
    /// repetition do not all agree. Returns an empty list where they agree, since the one state
    /// is then shown as it is.
    /// </summary>
    /// <param name="each">The state reaching each expansion of the line.</param>
    /// <param name="format">Formats one state.</param>
    internal static IReadOnlyList<(string State, int Count)> ByExpansion(
        IReadOnlyList<FlowState> each, Func<ProcessorState, string> format)
    {
        var counted = each
            .GroupBy(state => format(state.Processor), StringComparer.Ordinal)
            .Select(group => (State: group.Key, Count: group.Count()))
            .OrderBy(group => group.State, StringComparer.Ordinal)
            .ToList();
        return counted.Count > 1 ? counted : [];
    }

    /// <summary>
    /// Adds one row to the grid, or one row per instance where the instances of a family (the
    /// routines a repetition declares) differ. Every instance is declared on the family's single
    /// line, so where they all agree the row is shown once.
    /// </summary>
    private static void Rows(HoverCard card, string key, IEnumerable<(string Name, string? Text)> found)
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
    /// Adds, at the declaration of the name a repetition binds, a row listing the family
    /// instances it declares, which is what a reader wants to know about that line.
    /// </summary>
    private static void Declares(HoverCard card, SemanticModel model, SymbolReference reference)
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
    /// Adds an <c>in the output</c> row with the symbol's name in the ca65 output, where that
    /// differs from its name in the source. Only two kinds of name differ. ca65 reads a word of
    /// its own instruction table at the start of a line as an instruction, so the emitter
    /// prefixes such a name with its module. A module that another module places has every name
    /// it does not export prefixed the same way. Every other name is unchanged, so no row is
    /// added for it.
    /// </summary>
    private static void OutputName(HoverCard card, ProgramAnalysis analysis, Symbol symbol)
    {
        if (symbol is { IsReachableByPath: true, LinkerName: null }
            && Emit.FlatNames.Prefixed(symbol.FlatName, analysis.Cpu, symbol.Module) is { } prefixed)
        {
            card.Row("in the output", $"`{prefixed}`");
            return;
        }

        // A module placed by another module emits each name it does not export with the
        // module's name in front.
        if (symbol is { IsReachableByPath: true, LinkerName: null, Module: { } module }
            && analysis.Placements.PlacerOf(symbol.Tree) is not null)
        {
            card.Row("in the output", $"`{module.Replace("::", "__", StringComparison.Ordinal)}__{symbol.FlatName}`");
        }
    }

    /// <summary>
    /// Returns a hover giving what an inline <c>.scope</c> block costs and which registers it
    /// preserves, where the caret is on the line that opens it. The block has no name to hover, so
    /// apart from the lens this is the only place that shows it.
    /// </summary>
    private static Protocol.Hover? ToScope(SemanticModel model, ControlFlow? flow, int position)
    {
        foreach (var region in flow?.Regions ?? [])
        {
            foreach (var scope in region.ScopeRegisters)
            {
                if (position < scope.Opener.Start || position >= scope.Opener.End)
                    continue;
                var card = new HoverCard(
                    Headline(model.Tree.Text[scope.Opener.Start..scope.Opener.End]), ScopeAsked);
                var cost = region.Scopes.FirstOrDefault(costed => costed.Opener == scope.Opener).Cost;
                card.Row("cost", CodeLenses.Format(cost, null, null, false));
                card.Row("preserves", Format(scope.Kept, scope.Complete));
                return Hover(card, model.Tree, scope.Opener);
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the hover for the instruction at <paramref name="position"/>. It gives how long
    /// the instruction takes and how long the block around it takes. Where the analysis followed
    /// control to it, it also gives the processor state that reaches it, what each register
    /// holds and what the routine has pushed. The count is an interval wherever it depends on
    /// something the program does not say, such as whether an indexed read crosses a page. On
    /// the 65816 it also says that the counts are processor cycles, because the board decides
    /// how fast its memory is.
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

        // Show the instruction's full name as a trailing comment, since a reader who already
        // knows what `xba` stands for is not the one hovering it.
        var mnemonic = (statement as InstructionStatementSyntax)?.MnemonicKind;
        var line = Headline(model.Tree.Text[statement.Span.Start..statement.Span.End]);
        var card = new HoverCard(
            mnemonic is { } named && Mnemonics.Name(named) is { } called ? $"{line}  ; {called}" : line,
            laid.Ensured is null ? TimingAsked : EnsureAsked);

        // The line's cycles, the enclosing basic block's cycles and, where the line's count is
        // an interval, what causes the higher figure. These are three views of one question,
        // read across one row rather than down three. A line in a macro body counts once per
        // expansion, and the row spans them all.
        var counts = layout.EachOf(statement).Select(each => each.Cycles).OfType<CycleCount>().Distinct().ToList();
        var causes = counts.Count > 1 ? [.. laid.Causes ?? [], "the expansions differ"] : laid.Causes;
        card.Row("cycles", Cost(Spread(counts) ?? cycles, Spread(Around(flow, statement)), causes));

        // An `.ensure` emits whatever `rep` and `sep` the analysis found it needs, which is what
        // a reader hovers it to see.
        if (laid.Ensured is { } ensured)
        {
            var written = Edits.WidthInstructions(ensured);
            card.Row("writes", written.Count == 0 ? "nothing: the widths already hold" : string.Join(" and ", written));
        }

        // The processor state the analysis found on entry to the line, which determined the
        // size of its immediate. Where the expansions of a macro body differ, each state is
        // listed with how many expansions it reaches.
        var states = analysis.StatesFor(model.Tree.Path);
        var state = states?.AnyBefore(statement);
        var each = states is null ? [] : ByExpansion(states.EachBefore(statement), processor => processor.ToString());
        for (var i = 0; i < each.Count; i++)
            card.Row(i == 0 ? "state" : "", $"{each[i].State} ×{each[i].Count}");
        if (each.Count == 0 && state is not null)
            card.Row("state", state.Processor.ToString());
        if (mnemonic is { } flagged)
        {
            card.Row("flags", Mnemonics.Flags(
                analysis.Cpu, flagged, laid.Mode ?? AddressingMode.Implied, Immediate(model, statement, laid)));
        }

        // A 65816 board may stretch a cycle by the memory it reaches, as the SNES does, and nt65
        // does not know the board.
        if (layout.Cpu == Cpu.Wdc65816)
            card.Row("clock", "cycles are processor cycles; memory speed is the board's");

        // A column a reader can scan beats a sentence they have to take apart, so wherever
        // anything is known about the registers every register and flag is listed, set apart by
        // a gap from the rows about the line itself.
        var registers = flow?.Registers?.AnyBefore(statement);
        if (registers is { } held)
        {
            card.Gap();
            foreach (var register in RegisterEffects.Each(Registers.All))
                card.Row(RegisterEffects.Format(register), Held(held.Of(register), register));
        }
        StackRows.Add(card, analysis, registers, state);
        return Hover(card, model.Tree, statement.Span);
    }

    /// <summary>
    /// Builds the text of the <c>cycles</c> row, which is the line's own count, the count of the
    /// enclosing basic block, and the reason the line's own count is an interval. The reason
    /// belongs to the instruction, so a line whose own count is exact shows none even where its
    /// block's is not.
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
    /// Returns the value of a line's immediate operand, or null where it has none or nt65 cannot
    /// compute it. The value determines which flags a <c>rep</c> or a <c>sep</c> changes.
    /// </summary>
    private static long? Immediate(SemanticModel model, StatementSyntax statement, LineLayout laid) =>
        laid.Mode == AddressingMode.Immediate
            && statement is InstructionStatementSyntax { Operand: ImmediateOperandSyntax immediate }
            ? model.ValueOf(immediate.Value).AsNumber()
            : null;

    /// <summary>
    /// Returns the statement on the line <paramref name="position"/> is in, or null past the end of
    /// the file, where there is no line and so nothing to say anything about.
    /// </summary>
    private static StatementSyntax? Statement(SyntaxTree tree, int position) =>
        position < tree.Text.Length ? tree.GetLine(tree.GetLineIndex(position)).Statement : null;

    /// <summary>
    /// Returns the cycle counts of the basic blocks a statement is in, one for each expansion of it,
    /// wherever in the file the statement is.
    /// </summary>
    private static IEnumerable<CycleCount> Around(ControlFlow? flow, StatementSyntax statement) =>
        (flow?.Regions ?? [])
            .SelectMany(region => region.Blocks)
            .Where(block => block.Steps.Any(step =>
                step.Statement.Tree == statement.Tree && step.Statement.Position == statement.Position))
            .Select(block => block.Cycles)
            .OfType<CycleCount>();

    /// <summary>
    /// Returns the interval that covers every count in <paramref name="counts"/>, or null when
    /// there are none.
    /// </summary>
    private static CycleCount? Spread(IEnumerable<CycleCount> counts)
    {
        CycleCount? spread = null;
        foreach (var count in counts)
        {
            spread = spread is { } known
                ? new CycleCount(Math.Min(known.Minimum, count.Minimum), Math.Max(known.Maximum, count.Maximum))
                : count;
        }
        return spread;
    }
}
