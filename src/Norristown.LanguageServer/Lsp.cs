using System.Globalization;
using Norristown.Flow;
using Norristown.Layout;
using Norristown.Project;
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
    /// The language the grid of a hover is fenced as. Markdown cannot reach inside a fenced
    /// block, so the only way to tell one row from another is to give the grid a grammar; an
    /// editor that has none renders it as the plain monospace it was before.
    /// </summary>
    private const string Grid = "nt65-hover";

    /// <summary>Where the block's own count stands on the row the line's count starts.</summary>
    private const int BlockColumn = 10;

    /// <summary>How wide the block's own count stands, so the reason after it starts in one place.</summary>
    private const int ReasonColumn = 14;

    /// <summary>
    /// How many pushes a hover lists before it says how many more there are. A reader takes
    /// in the top of the stack, which is what the routine is about to pull back.
    /// </summary>
    private const int MostPushes = 6;

    /// <summary>
    /// What an instruction is pointed at for: how long it takes, and the state that reaches it
    /// and sized its operand. The flags it writes, what the registers hold and what is on the
    /// stack are the working, and stand under the rule.
    /// </summary>
    private static readonly IReadOnlySet<string> TimingAsked =
        new HashSet<string>(["cycles", "state"], StringComparer.Ordinal);

    /// <summary>
    /// The same for an <c>.ensure</c>, which is pointed at to find out what it turned into:
    /// the line says what it is for and not what it writes.
    /// </summary>
    private static readonly IReadOnlySet<string> EnsureAsked =
        new HashSet<string>(["writes", "state"], StringComparer.Ordinal);

    /// <summary>What an inline <c>.scope</c> is pointed at for, which is all a block hover has.</summary>
    private static readonly IReadOnlySet<string> ScopeAsked =
        new HashSet<string>(["cost", "preserves"], StringComparer.Ordinal);

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
    /// What the output calls the symbol, where that is not what the source calls it. Only one
    /// kind of name is written any differently: ca65 reads a word of its own instruction table
    /// at the start of a line as an instruction, so the emitter writes such a name with its
    /// module in front. Every other name keeps its spelling, and saying so would say nothing.
    /// </summary>
    private static void Written(Card card, ProgramAnalysis analysis, Symbol symbol)
    {
        if (symbol is { IsReachableByPath: true, LinkerName: null }
            && Emit.FlatNames.Prefixed(symbol.FlatName, analysis.Cpu, symbol.Module) is { } prefixed)
        {
            card.Row("in the output", $"`{prefixed}`");
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
    /// Every hover is read from the top down, and each zone is left out when it is empty: the
    /// line under the caret as the language writes it, the comment its author left above it,
    /// the one or two facts that kind of thing is asked about most, a rule, and everything else
    /// the analysis worked out beneath it. Nothing is left out for being far down; the first
    /// screenful is the answer and the rest is the working.
    /// </para>
    /// </summary>
    public static Protocol.Hover? ToHover(ProgramAnalysis analysis, SemanticModel model, int position)
    {
        var flow = analysis.FlowFor(model.Tree.Path);
        return model.ReferenceAt(position) is { } reference
            ? ToName(analysis, model, reference)
            : ToScope(model, flow, position) ?? ToTiming(analysis, model, flow, position);
    }

    /// <summary>
    /// What an editor shows about a name: the line that declares it, the facts the analysis
    /// worked out, and the comment written above it. What a routine costs and what it hands
    /// back are shown wherever it is named and not only where it is declared, because what a
    /// call costs is the question asked at the call.
    /// </summary>
    private static Protocol.Hover ToName(ProgramAnalysis analysis, SemanticModel model, SymbolReference reference)
    {
        // The declaration as the program has it now: an edit that leaves what other files
        // see of a file alone keeps their models, and with them the symbols they resolved
        // to, whose file is the one from before the edit.
        var symbol = analysis.Program.Current(reference.Symbol);
        var card = new Card(Headline(symbol, model.Tree), Asked(symbol));
        card.Prose(DocComments.Of(symbol));

        // A name from another module is worth naming that module for: it is the file the
        // declaration is in, and the file whose `.export` makes it nameable here.
        if (symbol.Tree != model.Tree)
            card.Row("from", symbol.Tree.Path[(symbol.Tree.Path.LastIndexOf('/') + 1)..]);

        // A name no path can reach is shown as it is written, so the routine or scope it is
        // private to is worth saying instead.
        if (!symbol.IsReachableByPath && symbol.Scope.NearestNamed()?.Name is { } owner)
            card.Row("private to", owner);
        if (symbol.Kind == SymbolKind.Member)
            card.Row("offset", symbol.Value.ToString());
        else if (symbol.Value.IsKnown)
            card.Row("value", Spell(symbol.Value));
        if (symbol.Type is { } type)
            card.Row("type", type.QualifiedName);

        // What a routine costs and hands back is what a caller came to ask, so it is read before
        // where the routine lives. What a macro call becomes is the same question asked of a
        // macro, and its rows belong here beside these.
        Routine(card, analysis, symbol);
        card.Row("expands to", MacroCallHover.Becomes(analysis, model, reference));

        // How much room it takes and how many of them there are answer one question, so they
        // are read together rather than a line apart. One of something is what a declaration
        // with no count means, and saying so says nothing.
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

        // A macro call is the one name whose hover has more to say than its declaration: what
        // it becomes. The line that says so is worked out where the expansion is, and the
        // listing under it is added to what the card writes.
        return new Protocol.Hover(
            Protocol.MarkupContent.Markdown(MacroCallHover.Added(card.ToString(), analysis, model, reference)),
            ToRange(model.Tree, reference.Span));
    }

    /// <summary>
    /// The keys of the facts this kind of name is asked about most, which are what a hover
    /// leads with; every other fact it knows stands under the rule, in the order it is written
    /// above. A constant is pointed at to read its value, a member to read its offset, a
    /// routine to find out what a call to it costs and what it hands back, and a name from
    /// another module to find out which one.
    /// </summary>
    private static IReadOnlySet<string> Asked(Symbol symbol)
    {
        var kind = symbol.Kind switch
        {
            SymbolKind.Member => new[] { "offset" },
            SymbolKind.Proc or SymbolKind.ExternProc or SymbolKind.Func => ["cost", "preserves"],
            // What a macro call becomes is what is asked about a macro, and the row that says
            // it leads whether or not anything writes it yet.
            SymbolKind.Macro => ["expands to"],
            SymbolKind.Binding => ["declares"],
            SymbolKind.Data or SymbolKind.List or SymbolKind.Charmap or SymbolKind.Frame
                or SymbolKind.Label or SymbolKind.ImportedAddress or SymbolKind.AddressAlias => ["address", "size"],
            SymbolKind.Struct or SymbolKind.Union or SymbolKind.Enum => ["size"],
            _ => ["value"],
        };
        return new HashSet<string>(["from", "private to", .. kind], StringComparer.Ordinal);
    }

    /// <summary>
    /// The line a name is declared on, as the language writes it, under the name a reader
    /// would write for it. Where the declaration is not a line of its own — a member of a
    /// layout, a macro's parameter, the name a repetition binds and the instances it stands
    /// for — there is no line to show, so the kind and the name stand in for one.
    /// </summary>
    private static string Headline(Symbol symbol, SyntaxTree asked)
    {
        var named = symbol.Tree != asked ? symbol.PathName : symbol.QualifiedName;
        return symbol.Kind is SymbolKind.Member or SymbolKind.MacroParameter or SymbolKind.Binding
            || symbol.Bound is not null
            || Declaring(symbol, named) is not { Length: > 0 } line
                ? $"{symbol.KindText} {named}"
                : line;
    }

    /// <summary>
    /// The declaring line as it is written, with the name on it replaced by
    /// <paramref name="named"/>, since a scope or a module makes the name a reader writes
    /// longer than the one the line carries.
    /// </summary>
    private static string Declaring(Symbol symbol, string named)
    {
        var tree = symbol.Tree;
        var index = tree.GetLineIndex(symbol.NameSpan.Start);
        var start = tree.LineStarts[index];
        var end = index + 1 < tree.LineStarts.Length ? tree.LineStarts[index + 1] : tree.Text.Length;
        var line = tree.Text[start..end].TrimEnd('\n', '\r');
        var at = symbol.NameSpan.Start - start;

        // A cheap local is written with an `@` that its name does not carry, so the line
        // already says it the way a reader would.
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
    /// What a routine costs and what it hands back, wherever its name is written. A lens says
    /// the same above the declaration, and a lens is something an editor can be told not to
    /// show and is nowhere near the call anyway. The flow is the declaring file's, which a
    /// call from another file is not in.
    /// </summary>
    private static void Routine(Card card, ProgramAnalysis analysis, Symbol symbol)
    {
        if (analysis.FlowFor(symbol.Tree.Path) is not { } flow)
            return;
        var found = flow.Regions
            .Where(region => region.Routine.Tree == symbol.Tree && region.Routine.NameSpan == symbol.NameSpan)
            .Select(region => (
                region.Routine.Name,
                Cost: CodeLenses.Spell(region.Cost, region.Total, "never returns"),
                Kept: region.Total.Ends ? Spell(region.Registers.Kept, region.Registers.Complete) : null))
            .ToList();
        Rows(card, "cost", found.Select(region => (region.Name, region.Cost)));
        Rows(card, "preserves", found.Select(region => (region.Name, region.Kept)));
    }

    /// <summary>
    /// One row of the grid, or one row per instance where the instances of a family answer
    /// differently. Every instance is declared on the family's one line, so where they all
    /// answer the same the line says it once.
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
    /// What a family declares, for the name its repetition binds: the instances it stands for,
    /// which is what the line the caret is on is worth knowing.
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
                var card = new Card(
                    Written(model.Tree.Text[scope.Opener.Start..scope.Opener.End]), ScopeAsked);
                var cost = region.Scopes.FirstOrDefault(costed => costed.Opener == scope.Opener).Cost;
                card.Row("cost", CodeLenses.Spell(cost, null, null));
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

        // An instruction is read under the name its datasheet gives it, written as a comment
        // is written here, since a reader who knows what `xba` stands for is not the one asking.
        var mnemonic = (statement as InstructionStatementSyntax)?.Mnemonic.Text.ToLowerInvariant();
        var line = Written(model.Tree.Text[statement.Span.Start..statement.Span.End]);
        var card = new Card(
            mnemonic is { } named && Mnemonics.Name(named) is { } called ? $"{line}  ; {called}" : line,
            laid.Ensured is null ? TimingAsked : EnsureAsked);

        // What the line takes, what the block around it takes and, where the count is an
        // interval, what its top would be paid for: three scales of the one question, read
        // across a row rather than down three.
        card.Row("cycles", Cost(cycles, Around(flow, statement)?.Cycles, laid.Causes));

        // An `.ensure` writes what the analysis found it needs, which is worth seeing.
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

        // What the analysis found reaching the line, which is what sized its immediate.
        var state = analysis.StatesFor(model.Tree.Path)?.AnyBefore(statement);
        if (state is not null)
            card.Row("state", state.Processor.ToString());
        if (mnemonic is { } flagged)
        {
            card.Row("flags", Mnemonics.Flags(
                analysis.Cpu, flagged, laid.Mode ?? AddressingMode.Implied, Immediate(model, statement, laid)));
        }

        // A column a reader's eye can run down beats a sentence they have to take apart, so
        // the registers are always all four wherever anything is known of them, and stand apart
        // from what the line itself is.
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
    /// What the line costs, as the row reads it: its own count, the count of the block around
    /// it, and the reason its own count is an interval. The reason belongs to the instruction,
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
    /// nt65 can work out. It is what says which flags a <c>rep</c> or a <c>sep</c> writes.
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
    /// What the routine has pushed, top of the stack first, one row to a push. Two models of
    /// the stack are lined up against one another: the saved-register stack says whose entry
    /// value a push holds, and the 65816's says what a <c>php</c> saved, what a constant push
    /// holds and where a <c>.frame</c> is, neither of which the other can see.
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

        // A routine deep in a save holds more than a reader can take in at a glance, and the
        // pushes it is about to pull back are the ones on top.
        if (rows.Count > MostPushes)
            card.Row("", $"and {rows.Count - MostPushes} more");
    }

    /// <summary>
    /// One row per push, top first, from whichever of the two stacks knows about it. The
    /// 65816's names a push the other can only call new, so where it has a name that name is
    /// what the row says; a push neither of them reaches is unknown rather than missing.
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

            // One group of bytes may be more than one push: a `.frame` names all of the pushes
            // it covers, and reading them as the one thing it made of them is what it is for.
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

            // The saved-register stack is about this row only where it is about one push of it.
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
    /// 65816's analysis knows it holds, or null where it knows nothing about it.
    /// <paramref name="hint"/> is how many bytes the saved-register stack says the push took,
    /// which is what says how far a push of bytes nothing is known about reaches.
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
    /// many bytes it covers; null when it belongs to none. A frame is named on its lowest byte
    /// only, and how far up it reaches is the size of the layout it was declared as.
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
    /// How many bytes a <c>.frame</c> names: the size of the layout it was declared as. Nothing
    /// asks a frame what it is laid out as until something like this does, so the answer is
    /// looked up in the model of the file that declares it rather than read off the symbol.
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
    /// The registers a routine or a block hands back, as a lens and a hover both say it:
    /// <c>A, X, Y, C</c>, <c>X, Y</c>, <c>none</c>. It is a list and not a sentence about one,
    /// because it is read at a glance. A lens writes <c>preserves</c> in front of it; a hover
    /// has a key beside it that says as much.
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
        // A mnemonic is a name like any other, warned about and not refused, so only a
        // register is a word a rename cannot reach.
        if (!symbol.IsCheapLocal && SyntaxFacts.IsRegister(name))
            return $"`{newName}` is a register name";

        var taken = symbol.IsCheapLocal ? symbol.Scope.FindCheapLocal(name) : (alias ?? symbol.Scope).FindMember(name);
        return taken is null || taken == symbol ? null : $"`{newName}` is already declared in this scope";
    }

    /// <summary>Everywhere the symbol at <paramref name="position"/> is written in this file.</summary>
    private static IReadOnlyList<SymbolReference> Occurrences(SemanticModel model, int position) =>
        model.ReferenceAt(position) is { } reference ? model.ReferencesTo(reference.Symbol) : [];

    /// <summary>
    /// The names a rename of <paramref name="reference"/> writes over. A name a
    /// <c>.use ... as</c> gives is written instead of the symbol's own, so it is kept apart:
    /// renaming the symbol leaves those alone, and renaming one renames only the names that
    /// module wrote the same way.
    /// </summary>
    private static IEnumerable<(SemanticModel File, SymbolReference Reference)> Renamed(
        ProgramModel program, SemanticModel model, SymbolReference reference)
    {
        // A file kept from before an edit elsewhere names what the edited file declared then,
        // so the symbols are compared as what they stand for now.
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

        // An instance of a family is named after an enum's member, so renaming either is
        // renaming the member and every use of every instance named after it.
        var renamed = new HashSet<Symbol> { symbol };
        if (symbol.IsEnumMember)
        {
            foreach (var file in program.Files)
                renamed.UnionWith(file.Symbols.Where(instance => instance.Bound?.Value.Member == symbol));
        }
        return program.ReferencesTo(renamed).Where(found => !found.Reference.IsAlias);
    }

    /// <summary>
    /// A value as the grid shows it. nt65 writes a number in hexadecimal, which is what an
    /// address or a mask is read as; a number that is also a count is worth the decimal beside
    /// it, and below ten the two are the same digit.
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
    /// A logical path back as a URI, for a diagnostic that points into another file. A Windows
    /// path reads as an absolute URI, drive letter and all, wherever nt65 is running; a rooted
    /// Unix path reads as no URI at all, on any host, so it is written out as one. A relative
    /// path is one the editor gave and is handed back as it came.
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
    /// Which keys lead is the caller's to say, and the rows are written in one order whichever
    /// side of the rule they land on, so that what a reader has learned about where a fact
    /// stands holds from one hover to the next. Both grids are padded to one column, so that
    /// the rule does not move the values under it.
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
        /// <summary>How far the values stand off the longest key.</summary>
        private const int Gutter = 2;

        /// <summary>
        /// The rows, a null standing for a blank line between two groups of them, each marked
        /// with whether it leads.
        /// </summary>
        private readonly List<((string Key, string Value)? Row, bool Leads)> rows = [];

        private string? prose;

        /// <summary>Whether the row last written leads, which an empty key carries on.</summary>
        private bool leading;

        /// <summary>
        /// One row of the grid, left out when there is nothing to say. An empty key carries the
        /// row above it on, which is how one fact takes more than one line.
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

            // What is asked stands together, with nothing between the line, the comment and the
            // answer; the one rule is where the answer ends and the working begins.
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
