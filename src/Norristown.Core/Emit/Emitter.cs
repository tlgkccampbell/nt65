using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Emit;

/// <summary>
/// Writes one file's ca65. The output is readable: the source's own spacing between the tokens
/// of a line, its comments dropped, and two levels of indentation of the output's own, because
/// the output is flat and nothing in it opens a block the source's would stand for. It is
/// deterministic: the same source always gives the same bytes.
/// <para>
/// Everything the output depends on is written into it. The header fixes the CPU and
/// switches off every ca65 option that changes syntax; every segment carries its address
/// size; every addressing mode that could be read two ways carries its prefix; and character
/// and string data is written as bytes, so no ca65 command line can change what it means.
/// </para>
/// <para>
/// What the output does not hold is debug information. Where each line came from is recorded
/// in <see cref="OutputFile.LineSources"/>, which <see cref="LineMap"/> writes beside the
/// ca65 as its own file, so that the ca65 is only the program.
/// </para>
/// </summary>
public sealed class Emitter
{
    /// <summary>Where a generated comment starts, so that a column of them lines up.</summary>
    private const int CommentColumn = 36;

    /// <summary>
    /// What a line that is not a label, a definition or a file-level directive starts with.
    /// <para>
    /// The output is flat: it holds no ca65 <c>.proc</c>, <c>.scope</c>, <c>.enum</c> or
    /// <c>.struct</c>, so nothing in it opens a block that indentation could stand for. It has
    /// two levels, as hand-written ca65 does — names at the margin and what they hold indented
    /// once — rather than the source's own, which would step in past constructs that are no
    /// longer there.
    /// </para>
    /// </summary>
    private const string Body = "    ";

    // What writing a line dispatches through: one method per kind of statement.
    private readonly Statements statements;
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly FlatNames names;
    private readonly List<Diagnostic> diagnostics;
    private readonly string source;
    private readonly string output;

    // The output, a line at a time and in its parts until the last of it is written: what a
    // run of named data lines lines up on, and what a run of equal bytes becomes, are
    // questions nothing can answer about a line that is already text.
    private readonly List<EmittedLine> lines = [];
    private readonly List<string?> segmentStack = [];
    private readonly HashSet<Symbol> exported = [];

    // What each folded repetition counts with in the output. A repetition's body may be
    // written out many times — inside another repetition, or in every expansion of a macro —
    // and the counter is the same name each time, so the repetition around it sees one thing.
    private readonly Dictionary<Symbol, string> counters = [];

    // What the file measures with `.endof` or `.spanof`, and so what needs a label just past
    // its last byte. A use may come before the thing it measures, so they are found up front.
    private readonly HashSet<Symbol> ends = [];

    // What other files measure, whose ends this file exports and those files import.
    private readonly IReadOnlySet<Symbol> measuredElsewhere;

    // The width the previous 65816 immediate of each register was written at, in output
    // order, which is exactly what ca65's setting is when the next one is reached.
    private readonly Dictionary<WidthRegister, int> widths = [];
    // The segment being written, or null before any region or block names one.
    private string? segment;

    // The routine being written, which is what a generated label is named after.
    private Symbol? routine;
    private string? written;
    private bool pendingBlank;
    private int depth;

    // Which turn of which repetitions, and which expansion of which macros, is being
    // written. A body is written once per writing, with every name the level binds standing
    // for what it is worth there.
    private Expansion? expansion;

    // The line an expansion's output maps back to. The lines of an expansion map to the line
    // of the call, the way a C debugger treats a preprocessor macro, and only a line of this
    // file can be named: the line map names one source, and a body may belong to another.
    private LineSyntax? callLine;

    private Emitter(
        SemanticModel model, CodeLayout layout, FlatNames names, List<Diagnostic> diagnostics,
        string source, string output, IReadOnlySet<Symbol> measuredElsewhere)
    {
        statements = new Statements(this);
        this.measuredElsewhere = measuredElsewhere;
        this.model = model;
        this.layout = layout;
        this.names = names;
        this.diagnostics = diagnostics;
        this.source = source;
        this.output = output;
    }

    /// <summary>
    /// The ca65 for <paramref name="model"/>'s file. <paramref name="outRoot"/> is the
    /// project's output tree, or null for the project's root. <paramref name="measuredElsewhere"/>
    /// is what the program's other files measure with <c>.endof</c> and <c>.spanof</c>.
    /// </summary>
    public static OutputFile Emit(
        SemanticModel model, CodeLayout layout, FlatNames names, List<Diagnostic> diagnostics,
        string? outRoot = null, IReadOnlySet<Symbol>? measuredElsewhere = null)
    {
        var path = OutputPath(model, outRoot);
        var emitter = new Emitter(model, layout, names, diagnostics,
            model.Tree.Path, path, measuredElsewhere ?? new HashSet<Symbol>());
        emitter.Ends();
        emitter.Header();
        emitter.Exports();
        emitter.Imports();
        emitter.WalkContainer(model.Tree.Root);
        emitter.Columns();
        emitter.Filled();
        return new OutputFile(path, emitter.Written(), [.. emitter.lines.Select(line => line.Bytes)])
        {
            Source = model.Tree.Path,
            SourceSize = Encoding.UTF8.GetByteCount(model.Tree.Text),
            LineSources = [.. emitter.lines.Select(line => line.Source)],
        };
    }

    /// <summary>The lines as the file holds them, each with its comment at the column those line up in.</summary>
    private string Written()
    {
        var text = new StringBuilder();
        foreach (var line in lines)
            text.Append(Commented(line.Text, line.Comment).TrimEnd()).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Where a file's output goes: <c>.module gfx::sprite</c> is written to <c>gfx/sprite.s</c>
    /// under the project's output tree, wherever the source is, so a source can move, or live
    /// outside the project, without its output moving. A file that says no module, which is
    /// already an error, is named after its source.
    /// </summary>
    public static string OutputPath(SemanticModel model, string? outRoot = null)
    {
        var path = model.FileScope.Module is { } module
            ? module.Replace("::", "/", StringComparison.Ordinal) + ".s"
            : Paths.Normalized(model.Tree.Path).Split('/')[^1] is var name
                && name.EndsWith(".nt65", StringComparison.OrdinalIgnoreCase) ? name[..^5] + ".s" : name + ".s";
        return string.IsNullOrEmpty(outRoot) || outRoot == "." ? path : $"{outRoot.TrimEnd('/')}/{path}";
    }

    /// <summary>A number as the output writes it: hexadecimal, at the width it is used at.</summary>
    private static string Hex(long value, int digits) =>
        "$" + value.ToString($"x{digits}", CultureInfo.InvariantCulture);

    /// <summary>
    /// A known value written in place of the name that stood for it, at the narrowest width
    /// that holds it. A negative number is written in decimal: hexadecimal for one would be
    /// sixteen digits of a width nt65 never meant.
    /// </summary>
    private static string Constant(long value) => value < 0
        ? value.ToString(CultureInfo.InvariantCulture)
        : Hex(value, value < 0x100 ? 2 : value < 0x10000 ? 4 : 8);

    /// <summary>
    /// The tokens under a node, in source order. A missing token is nowhere in the text, so
    /// there is nothing of it to write or to blank and it is left out.
    /// </summary>
    private static List<SyntaxToken> Tokens(SyntaxNode node) =>
        [.. node.DescendantTokens().Where(token => !token.IsMissing)];

    /// <summary>
    /// The routines and data declarations this file measures. The end of <c>f</c> is
    /// written <c>f__end</c>, and that spelling is fixed, because other files and
    /// hand-written ca65 refer to it once it is exported — so a name that collides with it
    /// is an error rather than a rename.
    /// </summary>
    private void Ends()
    {
        var own = Extents.MeasuredIn(model).Concat(measuredElsewhere)
            .Where(symbol => symbol.Tree == model.Tree)
            .Distinct()
            .OrderBy(s => s.NameSpan.Start);
        foreach (var measured in own)
        {
            ends.Add(measured);
            var end = EndOf(measured);
            if (names.Claimed(end) is { } other)
            {
                diagnostics.Add(new Diagnostic(measured.DeclarationSpan,
                    Catalogue.OutputNameCollision.Says(
    other.QualifiedName,
    $"the end of `{measured.QualifiedName}`",
    end), [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
            }
            names.Claim(end);
        }

        foreach (var type in model.Symbols.Where(symbol => symbol.Tree == model.Tree && IsSized(symbol)))
        {
            var size = SizeOf(type);
            if (names.Claimed(size) is { } other)
            {
                diagnostics.Add(new Diagnostic(type.DeclarationSpan,
                    Catalogue.OutputNameCollision.Says(
    other.QualifiedName, $"the size of `{type.QualifiedName}`", size),
                    [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
            }
            names.Claim(size);
        }
    }

    /// <summary>The label just past a symbol's last byte, which is what <c>.endof</c> stands for.</summary>
    private string EndOf(Symbol symbol) => Named(symbol) + "__end";

    /// <summary>
    /// The constant an exported struct or union's size is exported as, so that ca65 and C code
    /// can size what they allocate by it. Like an end, its spelling is fixed.
    /// </summary>
    private string SizeOf(Symbol type) => Named(type) + "__sizeof";

    /// <summary>Whether <paramref name="symbol"/> is a struct or union this file exports the size of.</summary>
    private static bool IsSized(Symbol symbol) => symbol is { IsExported: true, IsLayout: true, Size: not null };

    /// <summary>Writes the end label of whatever <paramref name="declaration"/> names, if it has one.</summary>
    private void End(StatementSyntax declaration)
    {
        if (model.DeclaredBy(declaration, expansion) is not { } symbol || !ends.Contains(symbol))
            return;
        Segment();
        Flush();
        Line(LabelText(EndOf(symbol)));
    }

    /// <summary>
    /// The header: the CPU, smart mode off, case-sensitive symbols, and every
    /// <c>.feature</c> that changes syntax switched off, so that no ca65 command line can
    /// change what the file means.
    /// </summary>
    private void Header()
    {
        Line($"; Generated by nt65 from {source}. Do not edit.");
        Line($".setcpu \"{CpuNames.SpellForCa65(layout.Cpu)}\"");
        Line(".smart -");
        Line(".case +");
        Line(".feature at_in_identifiers -, bracket_as_indirect -, c_comments -");
        Line(".feature dollar_in_identifiers -, dollar_is_pc -, force_range -, labels_without_colons -");
        Line(".feature leading_dot_in_identifiers -, line_continuations -, long_jsr_jmp_rts -");
        Line(".feature loose_char_term -, loose_string_term -, missing_char_term -, org_per_seg -");
        Line(".feature pc_assignment -, string_escapes -, ubiquitous_idents -, underline_in_numbers -");
    }

    /// <summary>
    /// Every export, under its linker name and with the address size nt65 gives it, hoisted
    /// to the top of the file in the order the file declares them.
    /// <para>
    /// A macro is no symbol to the linker: what crosses is its expansion, written into whichever
    /// module called it. A charmap, a function and a list are used by value, and what crosses
    /// is the values, written where they are used. A scope and a type are only the way to
    /// their members, which are exported one by one, a type's as the flat constants they
    /// become. An import is somebody else's, and every module that uses one imports it.
    /// </para>
    /// </summary>
    private void Exports()
    {
        var any = false;
        foreach (var symbol in model.Symbols)
        {
            if (IsSized(symbol) && symbol.Tree == model.Tree)
            {
                if (!any)
                    Blank();
                any = true;
                Line(Linked(".export", Implicit(Value.Of(symbol.Size!.Value).ImpliedAddressSize()), SizeOf(symbol)));
            }
            if (!symbol.IsExported || !ProgramSymbols.IsLinked(symbol))
                continue;
            if (!any)
                Blank();
            any = true;
            exported.Add(symbol);

            // A constant is as wide as its value, which is how ca65 sizes one it is given.
            var size = symbol.ExportSize
                ?? Implicit(symbol.IsAddress ? symbol.AddressSize : symbol.Value.ImpliedAddressSize());
            Line(Linked(".export", size, Named(symbol)));

            // Another module that measures the declaration names its end, which goes with it.
            if (measuredElsewhere.Contains(symbol))
                Line(Linked(".export", size, EndOf(symbol)));
        }
        if (any)
            pendingBlank = true;
    }

    /// <summary>
    /// Everything the file gets from outside it: what another nt65 file exports and
    /// this one names, and what an <c>.import</c> item declares and the file uses. Each carries
    /// the address size nt65 gives it, so ca65 sizes an operand the way nt65 did. Nothing the
    /// file does not use is imported, because an import pulls the module that defines it out
    /// of a library.
    /// <para>
    /// A constant is not imported: ca65 cannot use an imported symbol where it needs a value,
    /// so the constant is written out here instead. A checked import is both — the value nt65
    /// uses, and an assertion that the definition it will be linked against agrees.
    /// </para>
    /// </summary>
    private void Imports()
    {
        var any = false;
        var used = model.Used.ToHashSet();
        foreach (var symbol in model.Symbols
            .Where(symbol => symbol.Kind is SymbolKind.ImportedAddress or SymbolKind.ImportedConstant && used.Contains(symbol))
            .Concat(model.ExternalSymbols))
        {
            if (Import(symbol) is not { } line)
                continue;
            if (!any)
                Blank();
            any = true;
            Line(line);

            // A checked import is checked by every module that uses it, since each was built
            // against the value.
            if (symbol is { Kind: SymbolKind.ImportedConstant } && symbol.Value.AsNumber() is { } checkedValue)
            {
                Line($".assert {Named(symbol)} = {Constant(checkedValue)}, lderror, "
                    + $"\"{Named(symbol)} is not {Constant(checkedValue)}, which is what "
                    + $"{source} was built against\"");
            }
        }

        // The end of what this file measures in another file comes from that file, which
        // exports it beside the declaration.
        foreach (var measured in Extents.MeasuredIn(model).Where(symbol => symbol.Tree != model.Tree)
            .OrderBy(symbol => Named(symbol), StringComparer.Ordinal))
        {
            if (!any)
                Blank();
            any = true;
            Line(Linked(".import", measured.AddressSizeIn(model.Tree), EndOf(measured)));
        }
        if (any)
            pendingBlank = true;
    }

    /// <summary>
    /// An <c>.export</c> or <c>.import</c> of <paramref name="name"/>, with the address size
    /// <paramref name="size"/>: <c>.exportzp</c>, <c>.export name: far</c> or plain.
    /// </summary>
    private static string Linked(string directive, AddressSize? size, string name) => size switch
    {
        AddressSize.ZeroPage => $"{directive}zp {name}",
        AddressSize.Absolute => $"{directive} {name}: abs",
        AddressSize.Far => $"{directive} {name}: far",
        _ => $"{directive} {name}",
    };

    /// <summary>
    /// A size an export states by standing there: ca65 takes an export of an address as
    /// absolute unless told otherwise. An import says its size outright, because ld65 warns
    /// about one whose size it had to guess under a far memory model.
    /// </summary>
    private static AddressSize? Implicit(AddressSize? size) => size == AddressSize.Absolute ? null : size;

    /// <summary>The line that brings one symbol in, or null for one that needs no line at all.</summary>
    private string? Import(Symbol symbol)
    {
        var name = Named(symbol);
        if (symbol.IsAddress || symbol.Kind == SymbolKind.ImportedConstant)
            return Linked(".import", symbol.AddressSizeIn(model.Tree), name);

        // A constant another file declares, written out by value. One whose value nt65
        // does not know has already been reported, and a string is only ever used through
        // `.strlen` and `.strat`, which are numbers before anything is written.
        return symbol.Value.AsNumber() is { } value ? $"{name} = {Constant(value)}" : null;
    }

    private void WalkContainer(FileSyntax file) => Walk(file.Members, from: 0);

    /// <summary>
    /// A run of sibling lines and blocks. The <c>.if</c> chains among them are resolved here,
    /// because a chain is a run of siblings and only whoever walks them can see it.
    /// </summary>
    private void Walk(IReadOnlyList<SyntaxNode> children, int from)
    {
        var chain = new ConditionChain();
        for (var i = from; i < children.Count; i++)
        {
            if (children[i] is not BlockSyntax block)
            {
                chain.Break();
                if (children[i] is LineSyntax line)
                    WalkLine(line);
                continue;
            }
            if (chain.Includes(model, block, expansion))
                WalkBlock(block, block.BlockKind);
        }
    }

    private void WalkBlock(BlockSyntax block, BlockKind kind)
    {
        var lines = block.Members;
        var opener = block.Opener.Statement;

        // A macro body is written at every call that expands it, and nothing at all where it
        // stands.
        if (kind == BlockKind.Macro)
            return;

        // A block argument is the call's, not a block of its own: the line that opens it is
        // the call, which is written out here, and its lines are written wherever the body
        // splices them.
        if (kind == BlockKind.MacroBlock)
        {
            if (Macros.CallIn(opener) is not null)
                WalkLine(block.Opener);
            return;
        }

        // A branch the build takes is written out where it stands, with nothing said about
        // the `.if` around it; one it leaves out is not written at all. The block adds no
        // nesting of its own, so a segment block inside it is nested only if the `.if` was.
        if (kind == BlockKind.If)
        {
            Walk(lines, from: 1);
            return;
        }

        // A repetition is unrolled here: its body is written once per turn, every per-turn
        // decision made as it is written. Nothing is reported from here, because layout walked
        // the same turns and has already said what is wrong with the count or the list.
        // Whether the turns came out alike enough for ca65 to say them once is then a question
        // about the lines, and is asked of them once they are all written.
        if (Constructs.Repeats(kind))
        {
            var outerTurn = expansion;
            var turns = Repetitions.Of(model, block, outerTurn, null);
            var starts = new List<int>(turns.Count);
            foreach (var turn in turns)
            {
                starts.Add(this.lines.Count);
                expansion = turn;
                Walk(lines, from: 1);
            }
            expansion = outerTurn;
            if (kind == BlockKind.Repeat)
                Folded(block, Repetitions.BindingOf(model, opener), starts);
            return;
        }

        // `.multiproc` writes its body out once per member, as the `.each` around a `.proc`
        // that it stands for would write it.
        if (kind == BlockKind.MultiProc)
        {
            if (model.FamilyAt(opener) is null)
                return;
            var outerFamily = expansion;
            foreach (var turn in Repetitions.Of(model, block, outerFamily, null))
            {
                expansion = turn;
                WalkBlock(block, BlockKind.Proc);
            }
            expansion = outerFamily;
            return;
        }

        // A structure, a union, a list and a character mapping say what something means
        // without generating anything. An exported layout is the exception: its members
        // travel as flat constants, so the file that declares them has to define them.
        if (kind is BlockKind.List or BlockKind.Charmap)
            return;
        if (kind is BlockKind.Struct or BlockKind.Union)
        {
            Offsets(opener);
            return;
        }

        // An enum writes its members out as the constants they are. A member may stand under
        // an `.if` in the body, so the branches this build takes are read as well.
        if (kind == BlockKind.Enum)
        {
            Members(lines, from: 1);
            pendingBlank = true;
            return;
        }

        // One record written over several lines is written where its line is, as one
        // directive per member; the lines of values are the record's, and write nothing.
        if (kind == BlockKind.RecordInitializer)
        {
            WalkLine(block.Opener);
            return;
        }

        // A segment block anywhere but the file's own top level is a detour from the stream
        // around it, which is what `.pushseg` and `.popseg` say. A region is at file level.
        var nested = depth > 0;
        var pushed = false;
        var outerSegment = segment;
        var placing = kind is BlockKind.Segment or BlockKind.Region;
        if (placing)
        {
            var name = Constructs.SegmentOf(opener) ?? segment;
            if (nested)
            {
                Blank();
                Line(".pushseg");
                segmentStack.Add(segment);
                pushed = true;
                written = null;
            }
            segment = name;
        }
        else if (kind == BlockKind.Proc && opener is ProcDeclarationSyntax or MultiProcDeclarationSyntax)
        {
            Segment();
            Flush();
            routine = ProcLabel(opener);

            // An instance of a family is written under the family's line and the member it is:
            // one routine in the output, named as the source names it.
            var instance = model.FamilyAt(opener) is not null && routine is not null ? $"  {routine.QualifiedName}" : "";
            Line($"; {opener.GetText().Trim().TrimEnd('{').TrimEnd()}  {Where(opener)}{instance}");
            Label(block.Opener, routine);
        }
        else if (kind != BlockKind.Scope)
        {
            WalkLine(block.Opener);
        }

        var outerRoutine = routine;
        depth++;
        Walk(lines, from: 1);
        depth--;
        if (kind == BlockKind.DataBody
            && (opener as DataDirectiveSyntax ?? (opener as DataDeclarationSyntax)?.Directive) is { } declared)
        {
            Padding(block.Opener, declared);
        }
        if (kind is BlockKind.Proc or BlockKind.Data or BlockKind.DataBody)
            End(opener);
        if (kind == BlockKind.Proc && routine is { } named)
            Line($"; end of {named.Name}");
        routine = outerRoutine;

        if (pushed)
        {
            Line(".popseg");
            segment = segmentStack[^1];
            segmentStack.RemoveAt(segmentStack.Count - 1);
            written = segment;
            pendingBlank = true;
        }
        else if (placing)
        {
            segment = outerSegment;
        }
    }

    /// <summary>
    /// A counted repetition whose turns all came out the same, written back out as the one ca65
    /// <c>.repeat</c> that says what the turns said. nt65 makes every per-turn decision itself —
    /// the address size an operand is reached at, the width an immediate is written at, the form
    /// a branch takes, the name a turn declares — so the turns are written first and compared
    /// after, and ca65 is handed a <c>.repeat</c> only where there was nothing left for it to
    /// decide. <paramref name="starts"/> is where each turn's lines begin.
    /// <para>
    /// Where the turns differ only in what the binding was worth, the body is written once with
    /// a ca65 repeat counter where the number stood, and only after the counter has been put
    /// back for every turn and the lines have come out as the turns did.
    /// </para>
    /// <para>
    /// What the whole block assembles to goes on the <c>.repeat</c> line, because that is the
    /// line ca65 counts every turn's bytes against; the body's own lines make no bytes of their
    /// own any more, and keep only where they came from.
    /// </para>
    /// </summary>
    private void Folded(BlockSyntax block, Symbol? binding, IReadOnlyList<int> starts)
    {
        if (Turns(starts, out var at) is not { } turns)
            return;

        var body = turns[^1];
        var counter = "";
        if (!turns.TrueForAll(turn => Written(turn, body)))
        {
            if (binding is null || Counted(turns, binding) is not { } counted)
                return;
            (counter, body) = ($", {counted.Counter}", counted.Body);
        }
        else if (Repeated(body[0]) is not null && body.TrueForAll(line => Same(line, body[0])))
        {
            // A row of one repeated byte is a fill however it was written, and `.res` says a
            // fill more plainly than a `.repeat` around one `.byte` does.
            return;
        }

        // A line nt65 makes no claim about makes the whole block one, because what the turns
        // come to is as unpredictable as the line is.
        long bytes = 0;
        foreach (var line in body)
            bytes = line.Bytes < 0 || bytes < 0 ? DataLengths.Unpredictable : bytes + line.Bytes;
        bytes = bytes <= 0 ? bytes : bytes * starts.Count;
        var length = bytes is > 0 and <= int.MaxValue ? (int)bytes : bytes < 0 ? DataLengths.Unpredictable : 0;

        var opened = body.Select(line => line.Text).FirstOrDefault(text => text.Length > 0) ?? Body;
        var indent = opened[..(opened.Length - opened.TrimStart().Length)];
        lines.RemoveRange(at, lines.Count - at);
        Write(new EmittedLine(
            $"{indent}.repeat {starts.Count}{counter}", length, Mapped(block.Opener, length, located: false)));
        foreach (var line in body)
            Write(line with { Text = line.Text.Length == 0 ? "" : Body + line.Text, Bytes = 0 });

        // ca65 counts the block's bytes against the `.repeat`, and ld65 records a span for the
        // whole of it against the `.endrepeat`, so the closing line is named as well as the
        // opening one: the map has to answer for every line the debug information reaches.
        Write(new EmittedLine($"{indent}.endrepeat", Source: At(block.Closer ?? block.Opener)));
    }

    /// <summary>
    /// The lines each turn came out as, or null where the block is one no <c>.repeat</c> could
    /// stand for however alike the turns are. <paramref name="at"/> is where the
    /// <c>.repeat</c> goes: the first turn may be led by lines that belong before the repetition
    /// rather than inside it — a blank the source asked for, the <c>.segment</c> whatever
    /// follows lands in — which the turns after it found written already, and those stay where
    /// they are.
    /// </summary>
    private List<List<EmittedLine>>? Turns(IReadOnlyList<int> starts, out int at)
    {
        at = lines.Count;

        // Two turns read no better as a `.repeat` than as themselves, and one reads worse.
        if (starts.Count < 3)
            return null;

        var length = lines.Count - starts[^1];
        if (length == 0)
            return null;
        for (var turn = 1; turn < starts.Count; turn++)
        {
            var end = turn + 1 < starts.Count ? starts[turn + 1] : lines.Count;
            if (end - starts[turn] != length)
                return null;
        }

        var opening = starts[1] - starts[0] - length;
        if (opening < 0)
            return null;
        for (var i = 0; i < opening; i++)
        {
            var text = lines[starts[0] + i].Text.TrimStart();
            if (text.Length != 0 && !text.StartsWith(".segment ", StringComparison.Ordinal))
                return null;
        }

        var turns = new List<List<EmittedLine>>(starts.Count) { lines.GetRange(starts[0] + opening, length) };
        for (var turn = 1; turn < starts.Count; turn++)
            turns.Add(lines.GetRange(starts[turn], length));

        // A name inside a ca65 `.repeat` is declared once per turn, which is an error on the
        // second: such a body is written out in full however alike the turns look.
        if (turns[0].Any(Declares))
            return null;

        at = starts[0] + opening;
        return turns;
    }

    /// <summary>Whether two turns came out as all the same lines.</summary>
    private static bool Written(List<EmittedLine> one, List<EmittedLine> other)
    {
        for (var i = 0; i < one.Count; i++)
        {
            if (one[i] != other[i])
                return false;
        }
        return true;
    }

    /// <summary>
    /// The one body the turns of a counted repetition are, written in terms of a ca65 repeat
    /// counter, with the name to count with — or null where no such body says what the turns
    /// said. ca65 puts the turn's number in wherever the counter's name stands, so the body is
    /// the first turn with the counter where the number it was worth stood, and it is written
    /// only once every turn has been put back and has come out as the turn did. ca65 then
    /// evaluates the expression the source wrote, in the arithmetic nt65 already agrees with it
    /// on, since it is the line nt65 wrote for that turn with one number in it.
    /// </summary>
    private (string Counter, List<EmittedLine> Body)? Counted(List<List<EmittedLine>> turns, Symbol binding)
    {
        // Something no output holds, so that the places the counter goes can be marked before
        // there is a name to put there.
        const string mark = "\u0001";

        var body = new List<EmittedLine>(turns[0].Count);
        for (var i = 0; i < turns[0].Count; i++)
        {
            var line = turns[0][i];
            if (!turns.TrueForAll(turn => Alongside(turn[i], line))
                || Templated(line.Text, turns[1][i].Text, mark) is not { } text)
            {
                return null;
            }
            body.Add(line with { Text = text });
        }
        for (var turn = 0; turn < turns.Count; turn++)
        {
            for (var i = 0; i < body.Count; i++)
            {
                if (Instantiated(body[i].Text, mark, Constant(turn)) != turns[turn][i].Text)
                    return null;
            }
        }

        // The counter is a name of the output's like any other: derived from the source, and
        // taken by nothing else in the file. One repetition has one, however many times its
        // body is written out, or the repetition around it would see two different counters.
        if (!counters.TryGetValue(binding, out var counter))
            counters[binding] = counter = names.Generated(binding.Name);
        return (counter, [.. body.Select(line =>
            line with { Text = line.Text.Replace(mark, counter, StringComparison.Ordinal) })]);
    }

    /// <summary>
    /// What the first two turns wrote, with <paramref name="mark"/> wherever the one wrote the
    /// number the binding was worth on its turn and the other wrote its own; null where they
    /// differ in anything else, which is a decision that came out differently and no counter
    /// can stand for.
    /// </summary>
    private static string? Templated(string zeroth, string first, string mark)
    {
        if (zeroth.Length != first.Length)
            return null;
        var zero = Constant(0);
        var one = Constant(1);
        var text = new StringBuilder(zeroth.Length);
        for (var at = 0; at < zeroth.Length;)
        {
            if (at + zero.Length <= zeroth.Length
                && string.CompareOrdinal(zeroth, at, zero, 0, zero.Length) == 0
                && string.CompareOrdinal(first, at, one, 0, one.Length) == 0
                && (at == 0 || !InAName(zeroth[at - 1]))
                && (at + zero.Length == zeroth.Length || !InAName(zeroth[at + zero.Length])))
            {
                text.Append(mark);
                at += zero.Length;
                continue;
            }
            if (zeroth[at] != first[at])
                return null;
            text.Append(zeroth[at]);
            at++;
        }
        return text.ToString();
    }

    /// <summary>
    /// A body with <paramref name="value"/> where <paramref name="mark"/> stands, as ca65 puts
    /// the turn's number where the counter's name stands: at a whole word of it and nowhere
    /// inside one.
    /// </summary>
    private static string Instantiated(string body, string mark, string value)
    {
        var text = new StringBuilder(body.Length);
        for (var at = 0; at < body.Length;)
        {
            var found = body.IndexOf(mark, at, StringComparison.Ordinal);
            if (found < 0)
            {
                text.Append(body, at, body.Length - at);
                break;
            }
            text.Append(body, at, found - at);
            var after = found + mark.Length;
            text.Append((found == 0 || !InAName(body[found - 1]))
                && (after == body.Length || !InAName(body[after])) ? value : mark);
            at = after;
        }
        return text.ToString();
    }

    /// <summary>Whether a character is one a ca65 name is spelled with, or the dot that opens a word of ca65's own.</summary>
    private static bool InAName(char letter) => char.IsLetterOrDigit(letter) || letter == '_' || letter == '.';

    /// <summary>Whether two lines agree about everything but what they say.</summary>
    private static bool Alongside(EmittedLine one, EmittedLine other) =>
        one.Bytes == other.Bytes && one.Source == other.Source
            && one.Label == other.Label && one.Comment == other.Comment;

    /// <summary>
    /// Whether a line gives something a name. The name a repetition's body declares is its own
    /// on every turn, and a ca65 <c>.repeat</c> has no way to say that.
    /// </summary>
    private static bool Declares(EmittedLine line)
    {
        if (line.Label is not null)
            return true;
        var text = line.Text.TrimStart();
        var name = 0;
        while (name < text.Length && (char.IsLetterOrDigit(text[name]) || text[name] == '_'))
            name++;
        var rest = text[name..].TrimStart();
        return name > 0 && (rest.StartsWith(':') || rest.StartsWith('='));
    }

    /// <summary>
    /// The members of an enum, each written out as the constant it is. A member may stand under
    /// an <c>.if</c> in the body, so a chain among the lines is resolved here as it is anywhere
    /// else and the branches this build takes hold members like the body's own lines.
    /// </summary>
    private void Members(IReadOnlyList<SyntaxNode> lines, int from)
    {
        var chain = new ConditionChain();
        for (var i = from; i < lines.Count; i++)
        {
            if (lines[i] is not BlockSyntax block)
            {
                chain.Break();
                if (lines[i] is LineSyntax { Statement: EnumMemberSyntax member })
                    EnumMember(member);
                continue;
            }
            if (chain.Includes(model, block, expansion) && block.BlockKind == BlockKind.If)
                Members(block.Members, 1);
        }
    }

    private void WalkLine(LineSyntax line) => statements.Walk(line);

    /// <summary>
    /// A call, written out as the body it expands to, with a comment naming it. nt65 expands
    /// macros itself and emits flat code: ca65's own <c>.macro</c> is never used, so nt65's
    /// macro semantics never depend on ca65's.
    /// </summary>
    private void Expand(LineSyntax line, MacroCallSyntax call)
    {
        if (model.MacroAt(call) is not { Definition: BlockSyntax definition })
        {
            NotTranspiled(call);
            return;
        }

        // Expansions that went past the bound are an error already, and writing them out
        // would take as long as the laying out was spared.
        if (layout.ExpansionsExceeded)
            return;

        // A macro that reaches itself is an error already, and has nothing to write.
        if (Expansion.Expanding(expansion, definition))
            return;

        Segment();
        Flush();

        // The `{` of a trailing block belongs to the block rather than to the call, and the
        // comment names the call. The one that closes it names the macro alone, because the
        // arguments are above it and saying them twice would only make the block harder to see.
        var written = call.GetText().Trim().TrimEnd('{').TrimEnd();
        var called = written.IndexOf('!') is var bang && bang > 0 ? written[..(bang + 1)] : written;
        Line($"{Body}; {written}  {Where(call)}");

        var outerCall = callLine;
        var outer = expansion;

        // Every line of the expansion maps to the call, and a call inside a body maps to the
        // outermost call, which is the line of this file that asked for all of it.
        callLine ??= line;
        expansion = Expansion.Of(outer, call, definition);
        Walk(definition.Members, from: 1);
        expansion = outer;
        callLine = outerCall;

        // An expansion has no end of its own in the output: what follows it is the caller's
        // own code, on the same level and under no label, so the comment is what says so.
        Flush();
        Line($"{Body}; end of {called}");
    }

    /// <summary>
    /// A line naming a <c>block</c> parameter, which writes out the lines the call gave it.
    /// Those are the caller's own code, so where they are this file's own lines they map back
    /// to themselves rather than to the call that spliced them.
    /// </summary>
    private void Splice(BlockSpliceSyntax statement)
    {
        if (model.SymbolAt(statement.Name) is not { Parameter: { } parameter }
            || model.ArgumentFor(parameter.Symbol, expansion) is not { Block: { } block })
        {
            return;
        }

        var outerCall = callLine;
        var outer = expansion;
        if (block.Tree == model.Tree)
            callLine = null;
        expansion = Expansion.Spliced(outer, statement, block);
        Walk(Macros.LinesOf(block), from: 0);
        expansion = outer;
        callLine = outerCall;
    }

    /// <summary>Where a call was written, as the comment before its expansion names it.</summary>
    private static string Where(StatementSyntax call)
    {
        return $"{call.Tree.Path}:{call.LineIndex + 1}";
    }

    /// <summary>The routine a <c>.proc</c> or a turn of a <c>.multiproc</c> writes out here.</summary>
    private Symbol? ProcLabel(StatementSyntax opener) => model.DeclaredBy(opener, expansion);

    /// <summary>The label a routine's first byte carries, where the routine has a name.</summary>
    private void Label(LineSyntax line, Symbol? routine)
    {
        if (routine is not null)
            Code(line, LabelText(Named(routine)), 0);
    }

    /// <summary>
    /// A long branch, written as the form nt65 chose for it: the plain short branch where
    /// the target is in reach, and otherwise the opposite branch over a <c>jmp</c> to a
    /// generated label. ca65's own package can only ever write the long form forwards,
    /// because it chooses without knowing where the target lands.
    /// </summary>
    private void Branch(LineSyntax line, InstructionStatementSyntax statement, LineLayout laid)
    {
        var mnemonic = statement.Mnemonic;
        var (taken, skipped) = Instructions.FormsOf(mnemonic.Text);
        var edits = new Edits();
        Substitute(statement, edits, nested: false);

        if (!laid.Inverted)
        {
            edits.Replace[mnemonic.Position] = taken;
            Code(line, Render(statement, edits, Body), laid.Length);
            return;
        }

        var over = names.Generated((routine is null ? "" : Named(routine) + "__") + "over");
        edits.Replace[mnemonic.Position] = "jmp";
        var jump = Render(statement, edits, Body);
        Code(line, $"{Body}{skipped} {over}", Instructions.Length(AddressingMode.Relative));
        Write(new EmittedLine(jump, Instructions.Length(AddressingMode.Absolute)));
        Line($"{over}:");
    }

    private void LabeledLine(LineSyntax line, LabeledLineSyntax statement)
    {
        var label = statement.Label;
        var rest = statement.Statement;

        // An element type after a label is written as it is anywhere, with the label in front.
        if (rest is DataDirectiveSyntax directive && DataSyntax.IsElementType(directive))
        {
            Elements(line, directive, model.SymbolAt(label.Name));
            return;
        }

        // A label on a call names what the expansion emits, so the label comes first and the
        // expansion follows it.
        if (rest is MacroCallSyntax call)
        {
            LabelOnly(line, label);
            Expand(line, call);
            return;
        }
        if (rest is DataDirectiveSyntax && layout.Of(rest, expansion) is null)
        {
            NotTranspiled(rest);
            return;
        }
        var bytes = rest is null ? 0 : layout.Of(rest, expansion)?.Length ?? 0;
        if (rest is not null)
            Width(rest);
        var edits = new Edits();
        if (rest is not null)
        {
            Substitute(rest, edits, nested: false);
            Slot(rest, edits);
            Direct(rest, edits);
        }

        if (model.SymbolAt(label.Name) is not { } reference)
        {
            Code(line, Render(statement, edits), bytes);
            return;
        }

        // `z := *` and `f := *`: ca65 reads `z:` at the start of a line as an address-size
        // prefix, so such a label is written as an assignment instead. An assignment
        // takes the whole line, so whatever followed the label goes on the next one.
        var text = LabelText(Named(reference));
        if (rest is not null && !text.EndsWith(':'))
        {
            Code(line, text, 0);
            Code(line, Render(rest, edits, Body), bytes);
            return;
        }

        edits.Replace[label.Name.Position] = text;
        edits.Replace[label.ColonToken.Position] = "";
        Code(line, Render(statement, edits), bytes);
    }

    /// <summary>
    /// A data declaration: its name, and what it holds where the line holds it. Mixed data,
    /// and an array whose values are in a body, write the name here and their bytes a line at
    /// a time below it.
    /// </summary>
    private void Declared(LineSyntax line, DataDeclarationSyntax declaration)
    {
        if (model.DeclaredBy(declaration, expansion) is not { } symbol)
            return;
        if (declaration.Directive is not { } element)
        {
            Code(line, LabelText(Named(symbol)), 0);
            return;
        }
        if (DataSyntax.IsElementType(element))
        {
            Elements(line, element, symbol);
            return;
        }

        // Bytes such as an `.incbin`, written as the source wrote them.
        if (layout.Of(element, expansion) is not { } laid)
        {
            NotTranspiled(element);
            return;
        }
        var edits = new Edits();
        Substitute(element, edits, nested: false);
        WithName(line, symbol, Bare(element, edits, out var comment), laid.Length, comment);
    }

    /// <summary>
    /// An element type, named or not: its values as the directive of its type, or the room it
    /// takes as zeros. Values in a body are written a line at a time, below the name.
    /// </summary>
    private void Elements(LineSyntax line, DataDirectiveSyntax directive, Symbol? symbol)
    {
        if (DataSyntax.BodyOf(directive) is { BlockKind: BlockKind.DataBody })
        {
            if (symbol is not null)
                Code(line, LabelText(Named(symbol)), 0);
            return;
        }
        if (directive.Type is { } named)
        {
            if (model.SymbolOf(named) is { IsLayout: true } type)
                Records(line, directive, symbol, type);
            else
                NotTranspiled(directive);
            return;
        }
        if (layout.Of(directive, expansion) is not { } laid)
        {
            NotTranspiled(directive);
            return;
        }

        // A list is written without its braces, as the directive's own values are.
        var edits = new Edits();
        string text;
        string? comment = null;
        if (directive.Tail is BracedDataSyntax { Value: ValueListSyntax list })
        {
            var (width, bigEndian) = Slot(directive);
            foreach (var value in list.Values)
                InPlace(value, width, bigEndian, edits);
            edits.Replace[list.OpenBraceToken.Position] = "";
            if (!list.CloseBraceToken.IsMissing)
                edits.Replace[list.CloseBraceToken.Position] = "";
            text = $"{ForCa65(directive.Directive.Text)} {Bare(list, edits, out comment)}";
        }
        else if (directive.Tail is InlineDataSyntax)
        {
            Substitute(directive, edits, nested: false);
            text = Bare(directive, edits, out comment);
        }
        else
        {
            var reserved = Reservations(laid.Length).ToList();
            WithName(line, symbol, $".res {reserved[0]}", (int)reserved[0], comment);
            foreach (var rest in reserved.Skip(1))
                Code(line, $"{Body}.res {rest}", (int)rest, comment);
            return;
        }

        // One text in a counted `.byte` array is padded with zero to the count, so the line
        // holds the text and the line below it the zeros that fill the array out.
        var zeros = (int)(PaddedText.Padding(directive, model, expansion)?.Zeros ?? 0);
        WithName(line, symbol, text, laid.Length - zeros, comment);
        Padding(line, directive);
    }

    /// <summary>
    /// How a run of reserved bytes is split across <c>.res</c> directives. ca65 reserves at
    /// most <c>$ffff</c> bytes in one of them, and which declarations a program may write is
    /// not the assembler's to decide, so a bigger one is written as several.
    /// </summary>
    private static IEnumerable<long> Reservations(long bytes)
    {
        if (bytes <= 0xffff)
        {
            yield return bytes;
            yield break;
        }
        for (var left = bytes; left > 0; left -= 0xffff)
            yield return Math.Min(left, 0xffff);
    }

    /// <summary>The zeros a padded text is filled out with, where a declaration writes any.</summary>
    private void Padding(LineSyntax line, DataDirectiveSyntax directive)
    {
        if (PaddedText.Padding(directive, model, expansion) is not var (zeros, count))
            return;
        foreach (var reserved in Reservations(zeros))
            Code(line, $"{Body}.res {reserved}, $00", (int)reserved, $"padded to {count}");
    }

    /// <summary>
    /// <paramref name="node"/> rendered without the comment its edits collected, which is given
    /// back instead, to go after whatever the line puts in front of the text.
    /// </summary>
    private static string Bare(SyntaxNode node, Edits edits, out string? comment)
    {
        comment = edits.Comments.Count == 0 ? null : string.Join(", ", edits.Comments);
        edits.Comments.Clear();
        return Render(node, edits).Trim();
    }

    /// <summary>One line of a body's values, written as the directive of the type the body is of.</summary>
    private void Values(LineSyntax line, DataValuesSyntax values)
    {
        if (DataSyntax.DirectiveOfValues(values) is not { } directive)
            return;
        if (directive.Type is { } named)
        {
            if (model.SymbolOf(named) is not { IsLayout: true } type)
                return;
            foreach (var record in values.Values)
                Fields(line, type, ValuesIn(record), path: "");
            return;
        }
        if (layout.Of(values, expansion) is not { } laid)
        {
            NotTranspiled(values);
            return;
        }
        var edits = new Edits();
        Substitute(values, edits, nested: false);
        Code(line, $"{Body}{ForCa65(directive.Directive.Text)} {Bare(values, edits, out var comment)}",
            laid.Length, comment);
    }

    /// <summary>
    /// A line of data with its name in front, where it has one: on the same line, or on a line
    /// of its own where ca65 would read the name as a prefix. Where it shares the line, the
    /// directive is lined up with those of the lines around it rather than where the source
    /// wrote it, because the name in front is rarely the one the source used.
    /// </summary>
    private void WithName(LineSyntax line, Symbol? symbol, string text, int bytes, string? comment = null)
    {
        if (symbol is null)
        {
            Code(line, Body + text, bytes, comment);
            return;
        }
        if (LabelText(Named(symbol)) is var label && !label.EndsWith(':'))
        {
            Code(line, label, 0);
            Code(line, Body + text, bytes, comment);
            return;
        }
        Named(line, label, text, bytes, comment);
    }

    /// <summary>The line with its generated comment at the column those line up in.</summary>
    private static string Commented(string text, string? comment) =>
        comment is null ? text : text + new string(' ', Math.Max(CommentColumn - text.Length, 2)) + "; " + comment;

    /// <summary>
    /// Lines up the directives of each run of named data lines, so that a run reads as a column
    /// the way hand-written ca65 does. The names are nt65's, not the source's, so what the
    /// source lined up no longer lines up, and only the whole run says where the column goes.
    /// </summary>
    private void Columns()
    {
        foreach (var run in Runs())
        {
            var column = run.Max(at => lines[at].Label!.Length) + 1;
            foreach (var at in run)
            {
                var line = lines[at];
                var label = line.Label!;
                lines[at] = line with
                {
                    Text = label + new string(' ', column - label.Length) + line.Text,
                    Label = null,
                };
            }
        }
    }

    /// <summary>
    /// Runs of one repeated byte, written as the one <c>.res</c> that says the same thing. A
    /// repetition of a single value unrolls to a row of equal lines, which is a fill however it
    /// was written, and ca65 spells a fill <c>.res n, value</c>.
    /// <para>
    /// This is the last thing done, because it takes lines away: what a line assembles to and
    /// where it came from go with it, the count and the source of the first of the run.
    /// </para>
    /// </summary>
    private void Filled()
    {
        var kept = new List<EmittedLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            // A run longer than one `.res` reserves is left as it is: gathering it would need
            // several directives, and a file with that many equal lines in it is a repetition
            // nobody would have written by hand either.
            var run = 1;
            while (i + run < lines.Count && run < 0xffff && Same(lines[i + run], lines[i]) && Repeated(lines[i]) is not null)
                run++;
            if (run >= 3 && Repeated(lines[i]) is { } value)
            {
                var indent = lines[i].Text[..(lines[i].Text.Length - lines[i].Text.TrimStart().Length)];
                kept.Add(lines[i] with { Text = $"{indent}.res {run}, {value}", Bytes = run * lines[i].Bytes });
                i += run - 1;
                continue;
            }
            kept.Add(lines[i]);
        }
        lines.Clear();
        lines.AddRange(kept);
    }

    /// <summary>
    /// Whether two lines say the same thing. What each assembles to and where it came from
    /// are no part of that: a row of equal bytes is a fill however many source lines wrote
    /// it, and the fill is mapped to the first of them.
    /// </summary>
    private static bool Same(EmittedLine a, EmittedLine b) =>
        a.Text == b.Text && a.Label == b.Label && a.Comment == b.Comment;

    /// <summary>
    /// The one byte a line is, or null for a line that is anything more: only such a line
    /// stands for one byte of a fill. The comment goes with it, because what it says about
    /// the byte is as true of the fill the run becomes.
    /// </summary>
    private static string? Repeated(EmittedLine line)
    {
        if (line.Label is not null)
            return null;
        var text = line.Text.TrimStart();
        if (!text.StartsWith(".byte ", StringComparison.Ordinal))
            return null;
        var value = text[".byte ".Length..].Trim();
        return value.Length == 0 || value.Contains(',') || value.Contains(';') ? null : value;
    }

    /// <summary>The runs of lines to line up: named data lines with nothing in between.</summary>
    private List<List<int>> Runs()
    {
        var runs = new List<List<int>>();
        for (var at = 0; at < lines.Count; at++)
        {
            if (lines[at].Label is null)
                continue;
            if (runs.Count > 0 && runs[^1][^1] == at - 1)
                runs[^1].Add(at);
            else
                runs.Add([at]);
        }
        return runs;
    }

    /// <summary>
    /// Records: one with values, on its line or in the block its line opens, or an array of
    /// them in braces, each written as one directive per member in the type's order, whatever
    /// order the values were written in. Room with no values is zeros, which one `.res` says,
    /// unless a member pads with something else.
    /// </summary>
    private void Records(LineSyntax line, DataDirectiveSyntax directive, Symbol? symbol, Symbol type)
    {
        if (model.RoomFor(directive, expansion) is not { } room)
        {
            NotTranspiled(directive);
            return;
        }
        IReadOnlyList<IReadOnlyDictionary<string, MemberValueSyntax>> records;
        if (directive.Tail is BracedDataSyntax { Value: { } braced })
        {
            records = braced is ValueListSyntax list ? [.. list.Values.Select(ValuesIn)] : [ValuesIn(braced)];
        }
        else if (DataSyntax.BodyOf(directive) is { } block)
        {
            records = [ValuesIn(block.Members.Skip(1).OfType<LineSyntax>().Select(member => member.Statement))];
        }
        else if (!Pads(type))
        {
            var reserved = Reservations(room.Bytes).ToList();
            WithName(line, symbol, $".res {reserved[0]}", (int)reserved[0], type.QualifiedName);
            foreach (var rest in reserved.Skip(1))
                Code(line, $"{Body}.res {rest}", (int)rest, type.QualifiedName);
            return;
        }
        else
        {
            records = [.. Enumerable.Repeat(ValuesIn([]), (int)room.Elements)];
        }

        if (symbol is not null)
            Code(line, LabelText(Named(symbol)), 0);
        long bytes = 0;
        foreach (var record in records)
            bytes += Fields(line, type, record, path: "");
        if (bytes != room.Bytes)
            NotTranspiled(directive);
    }

    /// <summary>
    /// Whether a type, or a record inside it, has a member that pads with something other
    /// than zero. A type that holds itself has no layout at all, which the analysis has
    /// already said, so there is nothing here to walk into.
    /// </summary>
    private bool Pads(Symbol type) => !type.IsCyclic && (type.Body?.Symbols ?? []).Any(member =>
        member.Kind == SymbolKind.Member
        && (member.Type is { IsLayout: true } inner ? Pads(inner) : Fill(member) != 0));

    /// <summary>The byte a <c>.res n, fill</c> member pads with, which is zero when it names none.</summary>
    private long Fill(Symbol member) =>
        member.Data is DataDirectiveSyntax data && DataSyntax.NameOf(data) == ".res"
        && data.Tail is InlineDataSyntax { Values: [_, var padding, ..] }
        && model.ValueOf(padding, expansion).AsNumber() is { } fill
            ? fill & 0xff
            : 0;

    /// <summary>The label of a line whose statement is written separately.</summary>
    private void LabelOnly(LineSyntax line, LabelSyntax label)
    {
        if (model.SymbolAt(label.Name) is { } reference)
            Code(line, LabelText(Named(reference)), 0);
    }

    /// <summary>
    /// The members of an exported layout, each written out as the constant offset it is, and its size.
    /// A layout nothing exports says nothing to ca65 and is left out entirely.
    /// </summary>
    private void Offsets(StatementSyntax opener)
    {
        if (opener is not TypeDeclarationSyntax { Name: { } named } || model.SymbolAt(named) is not { } reference)
            return;

        foreach (var member in reference.Body?.Symbols ?? [])
        {
            if (exported.Contains(member) && member.Value.AsNumber() is { } offset)
                Definition($"{Named(member)} = {Constant(offset)}");
        }
        if (IsSized(reference))
            Definition($"{SizeOf(reference)} = {Constant(reference.Size!.Value)}");
    }

    /// <summary>One enum member, which is a constant like any other.</summary>
    private void EnumMember(EnumMemberSyntax member)
    {
        if (model.SymbolAt(member.Name) is not { } reference
            || reference.Value.AsNumber() is not { } value)
        {
            return;
        }
        Definition($"{Named(reference)} = {Constant(value)}");
    }

    /// <summary>
    /// The members of a type, one directive each, in the order the type declares them. A
    /// member that is itself a record, or an array of them, is written out the same way, so a
    /// nested value reaches the fields inside it.
    /// </summary>
    private long Fields(
        LineSyntax line, Symbol type, IReadOnlyDictionary<string, MemberValueSyntax> written, string path)
    {
        // A type that holds itself has no layout to write out, and the analysis has said so.
        if (type.IsCyclic)
            return 0;
        long bytes = 0;
        var members = (type.Body?.Symbols ?? []).Where(member => member.Kind == SymbolKind.Member).ToList();

        // A union is written as the one member it is given, or its first, and zeros to its size.
        if (type.Kind == SymbolKind.Union && members.Count > 0)
            members = [members.FirstOrDefault(member => written.ContainsKey(member.Name)) ?? members[0]];

        foreach (var member in members)
        {
            if (member.Size is not { } size)
                continue;
            var given = written.GetValueOrDefault(member.Name)?.Value;
            var named = path.Length == 0 ? member.Name : $"{path}::{member.Name}";
            var element = member.Data as DataDirectiveSyntax;

            // An array member takes a braced list, and one no value names is zeros.
            if (element is { Count: not null })
            {
                SeparatedSyntaxList<SyntaxNode> items = given is ValueListSyntax list ? list.Values : default;
                if (member.Type is { IsLayout: true } records)
                {
                    for (var i = 0; i < member.Count; i++)
                        bytes += Fields(line, records, ValuesIn(i < items.Count ? items[i] : null), $"{named}[{i}]");
                    continue;
                }
                if (items.Count == 0)
                {
                    foreach (var reserved in Reservations(size))
                        Field(line, $".res {reserved}", named, reserved);
                    bytes += size;
                    continue;
                }
                var (width, bigEndian) = Slot(element);
                Field(line,
                    $"{ForCa65(DataSyntax.NameOf(element))} {string.Join(", ", items.Select(item => Datum(item, width, bigEndian, []) ?? Rendered(item)))}",
                    named, size);
                bytes += size;
                continue;
            }

            // A nested record takes a braced list of its own members; anything else is a value.
            if (member.Type is { IsLayout: true } inner)
            {
                bytes += Fields(line, inner, ValuesIn(given), named);
                continue;
            }
            foreach (var (text, part) in Member(member, element, given))
                Field(line, text, named, part);
            bytes += size;
        }

        if (type.Kind == SymbolKind.Union && type.Size is { } whole && whole > bytes)
        {
            foreach (var reserved in Reservations(whole - bytes))
                Field(line, $".res {reserved}, $00", path.Length == 0 ? type.Name : path, reserved);
            bytes = whole;
        }
        return bytes;
    }

    /// <summary>One member's directive, with the path it fills in a comment.</summary>
    private void Field(LineSyntax line, string directive, string path, long size) =>
        Code(line, Body + directive, (int)size, path);

    /// <summary>The <c>member = value</c>s of a record, by member name: a braced record, or the lines of one.</summary>
    private static IReadOnlyDictionary<string, MemberValueSyntax> ValuesIn(SyntaxNode? record) =>
        ValuesIn(record is RecordValuesSyntax values ? values.Members : []);

    private static IReadOnlyDictionary<string, MemberValueSyntax> ValuesIn(IEnumerable<StatementSyntax> values)
    {
        var named = new Dictionary<string, MemberValueSyntax>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is MemberValueSyntax member)
                named[member.Name.Text] = member;
        }
        return named;
    }

    /// <summary>
    /// One member of a record, written with the directive its type gave it. A member no value
    /// names is zero, and a member reserved by <c>.res</c> takes text, padded to the room it
    /// has with the byte it pads with.
    /// </summary>
    private IEnumerable<(string Text, long Size)> Member(Symbol member, DataDirectiveSyntax? element, SyntaxNode? given)
    {
        var size = member.Size ?? 0;
        var directive = element is null ? ".res" : DataSyntax.NameOf(element);

        // Room with a fill is what `.res n, fill` says in both languages, so the room a value
        // does not reach is written as the one directive rather than as a row of equal bytes.
        if (directive == ".res")
        {
            var values = given is null ? [] : model.BytesOf(given, expansion)?.ToList() ?? [];
            var used = Math.Min(values.Count, size);
            if (used > 0)
                yield return ($".byte {string.Join(", ", values.Take((int)used).Select(b => Hex(b & 0xff, 2)))}", used);
            if (size > used)
            {
                foreach (var reserved in Reservations(size - used))
                    yield return ($".res {reserved}, {Hex(Fill(member), 2)}", reserved);
            }
            yield break;
        }
        var (width, bigEndian) = Slot(element!);
        var value = given is null ? Constant(0) : Datum(given, width, bigEndian, []) ?? Rendered(given);
        yield return ($"{ForCa65(directive)} {(given is null && bigEndian && width > 2 ? string.Join(", ", Enumerable.Repeat(Hex(0, 2), width)) : value)}", size);
    }

    /// <summary>
    /// An expression written out rather than edited in place: a call becomes what it stands
    /// for, and every nested operation is parenthesized, so nothing depends on how ca65
    /// reads precedence. What it would say in a comment goes to <paramref name="comments"/>,
    /// the comments of the line it is written into, when there is one: text after it on that
    /// line would otherwise land in its comment.
    /// </summary>
    private string Rendered(SyntaxNode node, List<string>? comments = null)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return "(" + Rendered(parenthesized.Expression, comments) + ")";
            case BinaryExpressionSyntax binary:
                return $"({Rendered(binary.Left, comments)} {Operator(binary.OperatorToken)} {Rendered(binary.Right, comments)})";
            case UnaryExpressionSyntax unary:
                return $"({unary.OperatorToken.Text}{Rendered(unary.Operand, comments)})";
            default:
                break;
        }

        // Anything with a value nt65 knows is written as that value; anything else keeps its
        // own spelling, with names flattened.
        if (model.ValueOf(node, expansion).AsNumber() is { } value)
            return Constant(value);
        var edits = new Edits();
        Substitute(node, edits, nested: false);
        return Inline(node, edits, comments);
    }

    /// <summary>
    /// <paramref name="node"/> rendered to go inside another line: its comments are that
    /// line's, given to <paramref name="comments"/>, or kept on the text when there is none.
    /// </summary>
    private static string Inline(SyntaxNode node, Edits edits, List<string>? comments)
    {
        if (comments is null)
            return Render(node, edits).Trim();
        comments.AddRange(edits.Comments);
        edits.Comments.Clear();
        return Render(node, edits).Trim();
    }

    /// <summary>
    /// An operator as ca65 spells it. ca65 writes equality `=` and inequality `&lt;&gt;`; its
    /// `&amp;&amp;`, `||`, `^^` and `!` are nt65's.
    /// </summary>
    private static string Operator(SyntaxToken op) => op.Kind switch
    {
        SyntaxKind.EqualsEquals => "=",
        SyntaxKind.BangEquals => "<>",

        // ca65 has no `^^`: it reads `a ^^ b` as `a ^ ^b`, an xor with the bank byte.
        SyntaxKind.CaretCaret => ".xor",
        _ => op.Text,
    };

    /// <summary>
    /// What a symbol is called in the output, at the level being written. A name a macro body
    /// declares is a different name at every expansion.
    /// </summary>
    private string Named(Symbol symbol) => names.Of(symbol, expansion);

    /// <summary>
    /// An argument written where the body named its parameter. It goes in as a parenthesized
    /// whole, so `value * 2` with the argument `1 + 2` is `(1 + 2) * 2` — six, where ca65's
    /// textual substitution would give five — and `#&lt;value` with `label+1` is
    /// `#&lt;(label+1)`. It keeps its own spelling: an argument is an expression, not a number,
    /// and a name in it is written the way the same name would be written anywhere else.
    /// </summary>
    private string Substituted(SyntaxNode argument, List<string>? comments = null)
    {
        var edits = new Edits();
        Substitute(argument, edits, nested: false);
        var text = Inline(argument, edits, comments);
        return argument is BinaryExpressionSyntax or UnaryExpressionSyntax
            ? "(" + text + ")"
            : text;
    }

    /// <summary>A label, or the assignment that stands in for one ca65 would misread.</summary>
    private static string LabelText(string name) => name is "z" or "f" ? $"{name} := *" : $"{name}:";

    private void Constant(LineSyntax line, ConstantDeclarationSyntax statement)
    {
        // A string has no ca65 spelling as a constant: it is used through `.strlen` and
        // `.strat`, which are numbers by the time anything is written.
        if (model.SymbolAt(statement.Name) is { } reference)
        {
            if (reference.Value.IsString)
                return;
            var edits = new Edits();
            edits.Replace[statement.Name.Position] = Named(reference);
            Substitute(statement.Value, edits, nested: false);
            var text = Render(statement, edits);
            if (statement.DescendantNodes().OfType<CurrentAddressExpressionSyntax>().Any())
                Code(line, text, 0);
            else
                Definition(text);
        }
    }

    /// <summary>An extern proc is a routine at a constant address, which is a constant.</summary>
    private void ExternProc(ExternProcDeclarationSyntax statement)
    {
        if (model.SymbolAt(statement.Name) is not { } reference)
            return;

        var address = statement.Address;
        var edits = new Edits();
        Substitute(address, edits, nested: false);
        Definition($"{Named(reference)} = {Render(address, edits)}");
    }

    private void Source(LineSyntax line, StatementSyntax statement, int bytes, bool located = false)
    {
        Width(statement);
        var edits = new Edits();
        Substitute(statement, edits, nested: false);
        Immediate(statement, bytes, edits);
        Slot(statement, edits);
        Direct(statement, edits);
        Code(line, Render(statement, edits, Body), bytes, located: located);
    }

    /// <summary>
    /// An immediate is a byte or a word slot, as wide as the instruction makes it, so a negative
    /// constant in one is written as its two's complement.
    /// </summary>
    private void Immediate(StatementSyntax statement, int bytes, Edits edits)
    {
        if (statement is InstructionStatementSyntax { Operand: ImmediateOperandSyntax { Value: var value, SecondValue: null } }
            && bytes is 2 or 3 && Datum(value, bytes - 1, bigEndian: false, edits.Comments) is { } text)
        {
            Replace(value, text, edits, around: false);
        }
    }

    /// <summary>An assertion the linker checks: ca65's <c>.assert</c> with the level that defers it to ld65.</summary>
    private void Linked(LineSyntax line, AssertDirectiveSyntax statement)
    {
        var edits = new Edits();
        Substitute(statement, edits, nested: false);

        // A condition nobody wrote has no token to put the level after, and the line has been
        // reported on already.
        if (Tokens(statement.Condition) is [.., var end])
            edits.After[end.Position] = edits.After.GetValueOrDefault(end.Position, "") + ", lderror";
        Code(line, Render(statement, edits), 0, located: true);
    }

    /// <summary>
    /// An <c>.ensure</c>, written as the <c>rep</c> and <c>sep</c> the analysis found it needs,
    /// which is nothing where the widths already hold.
    /// </summary>
    private void Ensure(LineSyntax line, EnsureDirectiveSyntax directive)
    {
        if (layout.Of(directive, expansion)?.Ensured is not { } ensured)
            return;
        if (ensured.Reset != 0)
            Code(line, $"{Body}rep #{Hex(ensured.Reset, 2)}", 2);
        if (ensured.Set != 0)
            Code(line, $"{Body}sep #{Hex(ensured.Set, 2)}", 2);
    }

    /// <summary>
    /// A frame's member in a stack-relative operand, written as the offset from the stack
    /// pointer the analysis counted for it here: ca65 knows nothing of frames.
    /// </summary>
    private void Slot(StatementSyntax statement, Edits edits)
    {
        if (layout.Of(statement, expansion)?.Slot is not { } slot
            || statement is not InstructionStatementSyntax { Operand: { } operand })
        {
            return;
        }
        foreach (var name in operand.DescendantNodes().OfType<NameExpressionSyntax>())
        {
            if (name.GlobalToken is not null || name.Names is not [var first, ..]
                || model.SymbolAt(first) is not { Kind: SymbolKind.Frame })
            {
                continue;
            }
            ReplaceName(name, slot.ToString(CultureInfo.InvariantCulture), edits);
        }
    }

    /// <summary>
    /// A <c>d:</c> operand, written as the offset into the direct page the analysis found D
    /// makes it: with D at <c>$2100</c>, <c>lda d:$2105</c> is <c>lda z:$05</c>. ca65 has no
    /// <c>d:</c>, and knows nothing of D.
    /// </summary>
    private void Direct(StatementSyntax statement, Edits edits)
    {
        if (layout.Of(statement, expansion) is not { Direct: { } offset } laid
            || statement is not InstructionStatementSyntax { Operand: AbsoluteOperandSyntax { Prefix: { } written } operand })
        {
            return;
        }
        foreach (var token in written.ChildTokens)
            edits.Replace[token.Position] = "";
        var tokens = Tokens(operand.Address);
        edits.Before.Remove(tokens[0].Position);
        edits.Replace[tokens[0].Position] = (laid.Prefix ?? "") + Hex(offset, 2);
        for (var i = 1; i < tokens.Count; i++)
            edits.Replace[tokens[i].Position] = "";
    }

    /// <summary>
    /// The <c>.a8</c>, <c>.a16</c>, <c>.i8</c> or <c>.i16</c> a 65816 immediate needs. ca65
    /// sizes an immediate from the last such directive in the text, whatever the control flow,
    /// so one goes directly before each immediate whose width differs from the previous
    /// immediate's for the same register, and nowhere else. The width itself is the
    /// analysis's, which follows control flow.
    /// </summary>
    private void Width(StatementSyntax statement)
    {
        if (layout.Of(statement, expansion) is not { Bits: { } bits }
            || statement is not InstructionStatementSyntax instruction
            || Instructions.SizedBy(instruction.Mnemonic.Text) is not { } register)
        {
            return;
        }
        if (widths.TryGetValue(register, out var previous) && previous == bits)
            return;
        widths[register] = bits;
        Segment();
        Flush();
        Line($"{Body}.{(register == WidthRegister.A ? "a" : "i")}{bits}");
    }

    /// <summary>
    /// A line nothing can be written for, which analysis should already have refused. The file
    /// is not transpiled rather than transpiled wrongly.
    /// </summary>
    private void NotTranspiled(SyntaxNode statement)
    {
        var tokens = Tokens(statement);
        var first = tokens.FirstOrDefault(token => token.Kind != SyntaxKind.EndOfLine);
        if (first.Parent is null)
            return;

        // In a file that is already wrong, what cannot be written is almost always what the
        // mistake left behind, and saying so again only buries the mistake.
        if (diagnostics.Any(d => d.Severity == Severity.Error && d.Span.File == model.Tree.Path))
            return;
        diagnostics.Add(Expansion.Problem(
            model.Tree, first.Parent.Tree, first.Span, expansion, null, Catalogue.CannotBeWritten.Says(first.Text)));
    }

    /// <summary>
    /// Writes one line that came from the source, and records where it came from. A line that
    /// generates bytes is mapped, because ld65 attaches a span of bytes to the line in effect
    /// while they were generated. So is one that is <paramref name="located"/>: an assertion ca65
    /// evaluates, whose failure ca65 notes as generated from the line in effect. A label or a
    /// constant is not, and neither are imports and exports: ld65 names the output's own line for
    /// what goes wrong with those, whatever the map says, and each would only map a line covering
    /// nothing.
    /// </summary>
    private void Code(LineSyntax line, string text, int bytes, string? comment = null, bool located = false)
    {
        Segment();
        Flush();
        Write(new EmittedLine(text, bytes, Mapped(line, bytes, located), Comment: comment));
    }

    /// <summary>
    /// A named data line, which shares its line with the name at its margin. The two are kept
    /// apart until every line is written, because where the directive goes is what the whole
    /// run of them says rather than what this one does.
    /// </summary>
    private void Named(LineSyntax line, string label, string text, int bytes, string? comment)
    {
        Segment();
        Flush();
        Write(new EmittedLine(text, bytes, Mapped(line, bytes, located: false), label, comment));
    }

    /// <summary>The source line a generated line is mapped to, or 0 for one the map should not name.</summary>
    private int Mapped(LineSyntax line, int bytes, bool located) =>
        bytes != 0 || located ? At(line) : 0;

    /// <summary>
    /// The line of this file a generated line came from, counting from one. The lines of an
    /// expansion came from the call, which is the line of this file that asked for all of them.
    /// </summary>
    private int At(LineSyntax line) => (callLine ?? line).LineIndex + 1;

    /// <summary>
    /// Writes a definition that is in no segment: a constant, or a name for an address given
    /// by other names. It is written where it stands, before any segment or in whichever one is
    /// open, so a program of constants needs no segment in its linker configuration.
    /// </summary>
    private void Definition(string text)
    {
        Flush();
        Line(text);
    }

    /// <summary>The <c>.segment</c> directive, written when what follows lands somewhere new.</summary>
    private void Segment()
    {
        if (written == segment || segment is null)
            return;
        var size = model.Segments.Find(segment)?.Size ?? AddressSize.Absolute;
        if (written is not null)
            pendingBlank = true;
        Flush();
        Line($".segment \"{segment}\": {size switch
        {
            AddressSize.ZeroPage => "zeropage",
            AddressSize.Far => "far",
            _ => "absolute",
        }}");
        written = segment;
    }

    /// <summary>A blank line the source asked for, at most one however many it wrote.</summary>
    private void Blank()
    {
        pendingBlank = true;
        Flush();
    }

    private void Flush()
    {
        if (!pendingBlank)
            return;
        pendingBlank = false;
        if (lines.Count > 0)
            Line("");
    }

    /// <summary>
    /// Appends one line of output that came from nowhere the map should name: a directive, a
    /// name, or a blank between them.
    /// </summary>
    private void Line(string text) => Write(new EmittedLine(text));

    private void Write(EmittedLine line) => lines.Add(line with { Text = line.Text.TrimEnd() });

    /// <summary>
    /// The statement's own text with the edits applied: the source's spacing between its
    /// tokens, its comments dropped, and names, prefixes and byte values written where the
    /// source had something else. What the source indented it by is not kept; where the line
    /// goes is the caller's to say (<see cref="Body"/>).
    /// </summary>
    private static string Render(SyntaxNode statement, Edits edits, string indent = "")
    {
        var text = new StringBuilder();
        var tokens = Tokens(statement);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == SyntaxKind.EndOfLine)
                continue;
            if (edits.Joined.Contains(token.Position))
            {
                if (!edits.Joined.Contains(tokens[i - 1].Position))
                    text.Length -= Width(tokens[i - 1].TrailingTrivia);
                text.Append(edits.Before.GetValueOrDefault(token.Position, ""))
                    .Append(edits.Replace.GetValueOrDefault(token.Position, Spelt(token)))
                    .Append(edits.After.GetValueOrDefault(token.Position, ""));
                if (i + 1 == tokens.Count || !edits.Joined.Contains(tokens[i + 1].Position))
                    Whitespace(text, token.TrailingTrivia);
                continue;
            }
            Whitespace(text, token.LeadingTrivia);
            if (edits.Before.TryGetValue(token.Position, out var before))
                text.Append(before);
            text.Append(edits.Replace.TryGetValue(token.Position, out var replacement) ? replacement : Spelt(token));
            if (edits.After.TryGetValue(token.Position, out var after))
                text.Append(after);
            Whitespace(text, token.TrailingTrivia);
        }

        var line = indent + text.ToString().Trim();
        return edits.Comments.Count == 0 ? line : Commented(line, string.Join(", ", edits.Comments));
    }

    /// <summary>
    /// A token as the output spells it, which for every token but a number is as the source
    /// spelt it. Everything nt65 works out for itself is written in lower case
    /// (<see cref="Hex"/>), so a number the source wrote in upper case is brought down to it:
    /// one file with <c>$FFD2</c> in one line and <c>$d020</c> in the next reads as two hands.
    /// The <c>_</c> that separates a number's digits is nt65's own, and the header switches
    /// ca65's <c>underline_in_numbers</c> off, so it is dropped on the way out.
    /// </summary>
    private static string Spelt(SyntaxToken token)
    {
        if (token.Kind != SyntaxKind.NumberLiteral)
            return token.Text;
        var written = token.Text.Replace("_", "", StringComparison.Ordinal);
        return written.StartsWith('$') ? written.ToLowerInvariant() : written;
    }

    private static int Width(SyntaxTriviaList trivia) =>
        trivia.Where(piece => piece.Kind == SyntaxKind.WhitespaceTrivia).Sum(piece => piece.Text.Length);

    private static void Whitespace(StringBuilder text, SyntaxTriviaList trivia)
    {
        foreach (var piece in trivia)
        {
            if (piece.Kind == SyntaxKind.WhitespaceTrivia)
                text.Append(piece.Text);
        }
    }

    /// <summary>
    /// What the output writes instead of what the source did: flat names, the address-size
    /// prefix the chosen mode needs, byte values for text, and the parentheses that keep the
    /// output from depending on ca65's precedence.
    /// </summary>
    private void Substitute(SyntaxNode node, Edits edits, bool nested)
    {
        switch (node)
        {
            case NameExpressionSyntax name:
                Name(name, edits);
                return;

            case LiteralExpressionSyntax literal when literal is StringExpressionSyntax or CharacterExpressionSyntax:
                Text(literal, edits);
                return;

            case CallExpressionSyntax call:
                Applied(call, edits);
                return;

            // `wdm #n` is written as its bytes, which is what it is to every processor but the
            // emulator that hooks it.
            case InstructionStatementSyntax { Operand: ImmediateOperandSyntax hook } instruction
                when instruction.Mnemonic.Text.Equals("wdm", StringComparison.OrdinalIgnoreCase):
                edits.Replace[instruction.Mnemonic.Position] = ".byte";
                edits.Replace[hook.HashToken.Position] = "$42, ";
                break;

            case AbsoluteOperandSyntax operand:
                // In a macro body an `operand` parameter stands as a whole operand, so what
                // the call gave replaces what the body wrote, prefix, index and all. The
                // prefix goes on last, outside whatever parentheses the expression was given.
                if (Given(operand, edits))
                    return;
                foreach (var child in operand.ChildNodes)
                    Substitute(child, edits, nested: false);
                Prefix(operand, edits);
                return;

            case DataDirectiveSyntax directive:
                Terminated(directive, edits);

                // An `.incbin` names a file rather than holding data, so its path is left a
                // path — pointed at the file from wherever the output lands.
                if (Included(directive, edits))
                    return;

                // An element type's values, and a `.res` fill, are slots of a width.
                if (DataSyntax.IsElementType(directive) && directive.Tail is not BracedDataSyntax)
                {
                    edits.Replace[directive.Directive.Position] = ForCa65(directive.Directive.Text);
                    var (width, bigEndian) = Slot(directive);
                    foreach (var value in DataLengths.ElementsOf(directive))
                        InPlace(value, width, bigEndian, edits);
                    return;
                }
                if (DataSyntax.NameOf(directive) == ".res"
                    && directive.Tail is InlineDataSyntax { Values: [var count, var fill] })
                {
                    Substitute(count, edits, nested: false);
                    InPlace(fill, 1, bigEndian: false, edits);
                    return;
                }
                break;

            case DataValuesSyntax values
                when DataSyntax.DirectiveOfValues(values) is { IsRecord: false } of:
                var (valueWidth, valuesBigEndian) = Slot(of);
                foreach (var value in values.Values)
                    InPlace(value, valueWidth, valuesBigEndian, edits);
                return;

            case BinaryExpressionSyntax:
            case UnaryExpressionSyntax:
                foreach (var op in node.ChildTokens)
                {
                    if (Operator(op) is var spelled && spelled != op.Text)
                        edits.Replace[op.Position] = spelled;
                }
                if (nested)
                {
                    var tokens = Tokens(node);
                    edits.Before[tokens[0].Position] = "(" + edits.Before.GetValueOrDefault(tokens[0].Position, "");
                    edits.After[tokens[^1].Position] = edits.After.GetValueOrDefault(tokens[^1].Position, "") + ")";
                }
                foreach (var child in node.ChildNodes)
                    Substitute(child, edits, nested: true);
                return;

            default:
                break;
        }

        foreach (var child in node.ChildNodes)
            Substitute(child, edits, nested: false);
    }

    /// <summary>
    /// The directive ca65 writes for one of nt65's element types. ca65's 24-bit directive is
    /// <c>.faraddr</c> and its one big-endian directive <c>.dbyt</c>; a wider big-endian value
    /// is written as its bytes.
    /// </summary>
    private static string ForCa65(string directive) => directive.ToLowerInvariant() switch
    {
        ".long" => ".faraddr",
        ".beword" => ".dbyt",
        ".belong" or ".bedword" => ".byte",
        _ => directive,
    };

    /// <summary>How wide one element of an element type is, and whether its bytes are written high first.</summary>
    private static (int Width, bool BigEndian) Slot(DataDirectiveSyntax directive)
    {
        var name = DataSyntax.NameOf(directive);
        return (SyntaxFacts.ElementSize(name) ?? 1, name is ".beword" or ".belong" or ".bedword");
    }

    /// <summary>One value of a slot, written as <see cref="Datum"/> says where it says anything, and as it stands otherwise.</summary>
    private void InPlace(SyntaxNode value, int width, bool bigEndian, Edits edits)
    {
        if (Datum(value, width, bigEndian, edits.Comments) is { } text)
            Replace(value, text, edits);
        else
            Substitute(value, edits, nested: false);
    }

    /// <summary>
    /// Writes <paramref name="text"/> in place of all of <paramref name="node"/>. What was written
    /// around it stays, and what was already made of the inside goes; with
    /// <paramref name="around"/> false, what was made of its ends goes too.
    /// </summary>
    private static void Replace(SyntaxNode node, string text, Edits edits, bool around = true)
    {
        var tokens = Tokens(node);

        // A node nobody wrote, the value after a last comma, has no place to write anything.
        if (tokens.Count == 0)
            return;
        for (var i = 0; i < tokens.Count; i++)
        {
            edits.Replace[tokens[i].Position] = "";
            if (i > 0 || !around)
                edits.Before.Remove(tokens[i].Position);
            if (i < tokens.Count - 1 || !around)
                edits.After.Remove(tokens[i].Position);
        }
        edits.Replace[tokens[0].Position] = text;

        // The spaces between the tokens it replaces go with them, and the ones around it stay.
        for (var i = 1; i < tokens.Count; i++)
            edits.Joined.Add(tokens[i].Position);
    }

    /// <summary>
    /// One value of a slot <paramref name="width"/> bytes wide where ca65 cannot take it as
    /// written: a negative constant is its two's complement, and a big-endian value wider than
    /// a word, which ca65 has no directive for, is its bytes, high first. Null for a value
    /// written as it stands. What the source said goes to <paramref name="comments"/>.
    /// </summary>
    private string? Datum(SyntaxNode value, int width, bool bigEndian, List<string> comments)
    {
        var known = model.ValueOf(value, expansion).AsNumber();
        if (bigEndian && width > 2)
        {
            if (model.BytesOf(value, expansion) is { Count: > 0 } text)
            {
                comments.Add(value.GetText().Trim());
                return string.Join(", ", text.SelectMany(b => HighFirst(b, width)));
            }
            if (known is { } number)
            {
                comments.Add(value.GetText().Trim());
                return string.Join(", ", HighFirst(number, width));
            }
            var written = Rendered(value, comments);
            var low = $".bankbyte({written}), .hibyte({written}), .lobyte({written})";
            return width == 3 ? low : $".lobyte(({written}) >> 24), {low}";
        }
        if (known is not (< 0 and var negative))
            return null;
        comments.Add(value.GetText().Trim());
        return Hex(negative & (long)(ulong.MaxValue >> (64 - (8 * width))), 2 * width);

        static IEnumerable<string> HighFirst(long number, int width) =>
            Enumerable.Range(0, width).Select(i => Hex((number >> (8 * (width - 1 - i))) & 0xff, 2));
    }

    /// <summary>
    /// Text reaches the output as bytes, so <c>.strz</c> becomes the bytes and
    /// the zero that ends them: ca65's own directive takes a string, and there is none left.
    /// </summary>
    private static void Terminated(DataDirectiveSyntax directive, Edits edits)
    {
        if (!directive.Directive.Text.Equals(".strz", StringComparison.OrdinalIgnoreCase))
            return;

        edits.Replace[directive.Directive.Position] = ".byte";
        var last = directive.Tail is InlineDataSyntax { Values: [.., var argument] } && Tokens(argument) is [.., var token]
            ? token.Position
            : directive.Directive.Position;
        edits.After[last] = edits.After.GetValueOrDefault(last, "") + ", $00";
    }

    /// <summary>
    /// The path of an <c>.incbin</c>, rewritten so that ca65 finds the file from the output
    /// rather than from the source: ca65 looks beside the file it is assembling, so the output
    /// assembles from any directory. Returns whether the directive was one.
    /// </summary>
    private bool Included(DataDirectiveSyntax directive, Edits edits)
    {
        if (!directive.Directive.Text.Equals(".incbin", StringComparison.OrdinalIgnoreCase))
            return false;
        var values = directive.Tail is InlineDataSyntax inline ? inline.Values : default;
        if (values is [var path, ..]
            && model.ValueOf(path, expansion) is { Kind: ValueKind.String, Text: { } named })
        {
            Replace(path, "\"" + Paths.Relative(Paths.Directory(output), Paths.Beside(source, named)) + "\"", edits);
        }
        foreach (var argument in values.Skip(1))
            Substitute(argument, edits, nested: false);
        return true;
    }

    private void Name(NameExpressionSyntax name, Edits edits)
    {
        // A path names one symbol; the whole of it becomes that symbol's flat name. A body is
        // written out in every file that calls its macro, so what a name means is the
        // program's answer rather than this one file's.
        if (name.Names is not [.., var last] || model.SymbolAt(last) is not { } named)
            return;
        var reference = named;

        // A path that ends in a repetition's name names a different member on every turn.
        if (named.Kind == SymbolKind.Binding && name.SimpleName is null)
        {
            if (model.SymbolOf(name, expansion) is not { } namesake)
                return;
            reference = namesake;
        }

        // A list stands for its own items wherever data takes them.
        if (model.ItemsOf(name) is { Count: > 0 } items)
        {
            ReplaceName(name, string.Join(", ", items.Select(item => Rendered(item, edits.Comments))), edits);
            edits.Comments.Add(name.GetText().Trim());
            return;
        }

        // A member is an offset: the offsets along the path added up, on the address the
        // path starts from when it starts at an instance rather than at a type. An index along
        // the path is whole elements of the same sum.
        if (reference.Kind == SymbolKind.Member || (name.IsIndexed && reference.IsAddress))
        {
            MemberPath(name, edits);
            return;
        }

        // A macro parameter stands for the argument the call gave it, as a parenthesized
        // whole, so `value * 2` with the argument `1 + 2` is 6 rather than 5.
        var symbol = reference;
        if (symbol.Kind == SymbolKind.MacroParameter)
        {
            if (Parameter(symbol, edits.Comments) is not { } given)
                return;
            ReplaceName(name, given, edits);
            return;
        }

        // The name a repetition binds is worth something different on every turn, and this is
        // the turn being written.
        if (symbol.Kind == SymbolKind.Binding)
        {
            if (model.BindingsOf(expansion)?.TryGetValue(symbol, out var bound) is not true)
                return;

            // A list item is written as it stands, with its own names substituted; a number
            // is written as the number it is on this turn.
            var written = bound.Item is { } item ? Rendered(item, edits.Comments) : null;
            if (written is null && bound.Value.AsNumber() is { } turn)
                written = Constant(turn);
            if (written is null)
                return;

            ReplaceName(name, written, edits);

            // What the turn is worth is written into the line, so naming the binding as well
            // would only repeat it down every line an unrolled body writes.
            return;
        }

        // A string constant has no ca65 spelling, in this module or any other: it is written
        // as the bytes of its text, as a literal is.
        if (symbol.Value.IsString)
        {
            if (model.BytesOf(name, expansion) is { Count: > 0 } bytes)
            {
                Replace(name, string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2))), edits);
                edits.Comments.Add(name.GetText().Trim());
            }
            return;
        }

        // A define and a checked import are written as their value, never by name: a `-D` given to ca65 then cannot collide with a define, and a checked import
        // is a value nt65 has already used in its own arithmetic.
        var byValue = (symbol.IsDefine || symbol.IsConfig || symbol.Kind == SymbolKind.ImportedConstant)
            && symbol.Value.AsNumber() is not null;
        ReplaceName(name, byValue ? Constant(symbol.Value.Number) : Named(symbol), edits);
        if (byValue)
            edits.Comments.Add(symbol.QualifiedName);
    }

    /// <summary>
    /// Writes <paramref name="text"/> where the whole of <paramref name="name"/> stood: at the
    /// first token the name itself is written with, with the rest of them blanked. An <c>[i]</c>
    /// along the path is left as it stands, being a place in what the name names rather than part
    /// of the name.
    /// </summary>
    private static void ReplaceName(NameExpressionSyntax name, string text, Edits edits)
    {
        var names = name.Names;
        var first = name.GlobalToken;
        if (first is null)
        {
            if (names.IsEmpty)
                return;
            first = names[0];
        }
        foreach (var token in names)
            edits.Replace[token.Position] = "";

        // What is left directly under the name are the `::` between its parts.
        foreach (var token in name.ChildTokens)
            edits.Replace[token.Position] = "";
        edits.Replace[first.Value.Position] = text;
    }

    /// <summary>
    /// What a macro parameter stands for here: the operand a call gave, the expression it
    /// gave, or the word or number it stands for.
    /// </summary>
    private string? Parameter(Symbol parameter, List<string> comments)
    {
        if (model.BindingsOf(expansion)?.TryGetValue(parameter, out var bound) is not true)
            return null;
        if (bound.Value.IsWord)
            return bound.Value.Text;
        if (bound.Argument is { Parameter.Kind: ParameterKind.Operand } given)
            return given.Operand is { } operand ? Substituted(operand, comments) : null;

        // A member of an enum is a constant, and constants are written as what they are worth.
        if (bound is { Member: not null, Item: null } && bound.Value.AsNumber() is { } member)
            return Constant(member);
        return bound.Item is { } item ? Substituted(item, comments) : null;
    }

    /// <summary>
    /// A path through a type or an instance, with the elements any <c>[i]</c> along it steps
    /// over. Through a type it is a number; through an instance it is that instance plus the
    /// offset, which is what ca65 and ld65 resolve. Either way the path it came from is kept
    /// in a comment.
    /// </summary>
    private void MemberPath(NameExpressionSyntax name, Edits edits)
    {
        Symbol? start = null;
        long offset = 0;
        foreach (var token in name.Names)
        {
            if (model.SymbolAt(token) is not { } part)
                continue;
            if (part.Kind == SymbolKind.Member)
                offset += part.Value.AsNumber() ?? 0;
            else if (part.IsAddress)
                start ??= part;
        }
        foreach (var (part, index) in ElementIndexes.Of(name))
        {
            if (model.SymbolAt(part) is { } indexed && ElementIndexes.Stride(indexed) is { } stride
                && model.ValueOf(index.Index, expansion).AsNumber() is { } element)
            {
                offset += element * stride;
            }
        }

        Replace(name, start is null
            ? Constant(offset)
            : offset == 0 ? Named(start) : $"{Named(start)}+{offset}", edits);
        edits.Comments.Add(name.GetText().Trim());
    }

    /// <summary>
    /// A call written out as what it stands for: a charmap applied to text becomes the bytes
    /// it maps them to, and a function call becomes its value. A call nt65 cannot work out
    /// is refused rather than passed to ca65, which knows neither.
    /// </summary>
    private Value Worth(SyntaxNode node) => model.ValueOf(node, expansion, cycles: layout.CyclesOf);

    private void Applied(CallExpressionSyntax call, Edits edits)
    {
        var tokens = Tokens(call);
        if (tokens.Count == 0)
            return;

        // `.endof(f)` and `.spanof(f)` describe layout rather than shape, so they are written
        // as the addresses they are and resolved by ca65 and ld65.
        if (Extents.Is(call, model, out var span) && Extents.MeasuredBy(call) is { } named
            && model.SymbolOf(named) is { } measured && (ends.Contains(measured) || measured.Tree != model.Tree))
        {
            Replace(call, span ? $"({EndOf(measured)} - {Named(measured)})" : EndOf(measured), edits);
            return;
        }

        // `.exprof(p)` is replaced by the expression inside the operand the call passed as `p`
        // (`5` for `{#5}`, `ptr` for `{(ptr),y}`), unless it is a constant, which the path
        // below writes as a number.
        if (Semantics.Operands.IsExprOf(call) && Worth(call).AsNumber() is null
            && model.ExprOf(call, expansion) is { } inner)
        {
            Replace(call, "(" + Substituted(inner, edits.Comments) + ")", edits);
            return;
        }

        // A built-in the analysis answers keeps the ordinary path.
        if (call.Callee is null)
        {
            // An address `.select` chooses is written as the value it chose, which is all ca65 sees.
            if (Worth(call).AsNumber() is null
                && Evaluator.SelectArguments(call) is [var condition, var ifHolds, var otherwise]
                && model.ValueOf(condition, expansion).AsNumber() is { } holds)
            {
                // A parenthesis first in an operand would read as indirection, which a unary `+` prevents.
                var chosen = Rendered(holds != 0 ? ifHolds : otherwise, edits.Comments);
                Replace(call, chosen.StartsWith('(') ? "+" + chosen : chosen, edits);
                return;
            }
            if (Worth(call).AsNumber() is { } builtin)
            {
                Replace(call, Constant(builtin), edits);
                return;
            }
            Substitute(call.Arguments, edits, nested: false);
            return;
        }

        string? text = null;
        if (model.BytesOf(call, expansion) is { Count: > 0 } bytes)
            text = string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2)));
        else if (model.ValueOf(call, expansion).AsNumber() is { } value)
            text = Constant(value);

        if (text is null)
        {
            NotTranspiled(call);
            return;
        }
        Replace(call, text, edits);
        edits.Comments.Add(call.GetText().Trim());
    }

    /// <summary>Text becomes byte values, with the source spelling kept in a comment.</summary>
    private void Text(LiteralExpressionSyntax literal, Edits edits)
    {
        if (DataLengths.Bytes(literal, model) is not { Count: > 0 } bytes)
            return;
        edits.Replace[literal.Token.Position] =
            string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2)));
        edits.Comments.Add(literal.GetText());
    }

    /// <summary>
    /// An operand that names an <c>operand</c> parameter, written out as the one the call
    /// gave. Returns whether it was one.
    /// </summary>
    private bool Given(AbsoluteOperandSyntax operand, Edits edits)
    {
        if (Semantics.Operands.Substituted(model, operand, expansion) is not { } given)
            return false;

        var text = Argument(given, edits.Comments);
        if (text is null)
            return false;

        // ca65 reads a `(` at the head of an operand as indirect addressing, so an expression
        // that starts with one gets a unary `+`, which changes nothing about what it is worth.
        var prefix = operand.Parent is { } instruction ? layout.Of(instruction, expansion)?.Prefix ?? "" : "";
        if (text.StartsWith('(') && (prefix.Length > 0 || given.IsAddress))
            text = "+" + text;

        var tokens = Tokens(operand);
        edits.Replace[tokens[0].Position] = prefix + text;
        for (var i = 1; i < tokens.Count; i++)
            edits.Replace[tokens[i].Position] = "";
        return true;
    }

    /// <summary>
    /// The text of the operand a call gave, with whatever the body asked of it: the operand
    /// as it stands, the byte after it, or one byte of an immediate value.
    /// </summary>
    private string? Argument(OperandSubstitution given, List<string> comments)
    {
        // `.byteof` on an immediate is a byte of the value, which is a number wherever nt65
        // knows it and a shift and a mask wherever only the linker will.
        if (given.ByteOf && given.Operand is ImmediateOperandSyntax)
        {
            if (given.Expression is not { } value)
                return null;
            if (model.ValueOf(value, expansion).AsNumber() is { } known)
                return "#" + Constant((known >> (int)(8 * given.Offset)) & 0xff);
            var shifted = Substituted(value, comments);
            return given.Offset == 0
                ? $"#({shifted} & $ff)"
                : $"#(({shifted} >> {8 * given.Offset}) & $ff)";
        }

        // Anything else is the argument's own operand: as it stands when the body named it
        // whole, and with `+ n` on its expression when the body asked for a later byte.
        var offset = given.Offset;
        if (offset == 0)
            return Substituted(given.Operand, comments);
        if (given.Expression is not { } addressed)
            return null;
        var index = given.Index is { } register ? "," + register.Text : "";
        var written = Substituted(addressed, comments);
        return offset > 0 ? $"{written}+{offset}{index}" : $"{written}{offset}{index}";
    }

    /// <summary>The <c>z:</c> or <c>a:</c> that says which mode was chosen.</summary>
    private void Prefix(AbsoluteOperandSyntax operand, Edits edits)
    {
        var instruction = operand.Parent;
        if (instruction is null)
            return;
        var chosen = layout.Of(instruction, expansion)?.Prefix;
        var prefix = chosen ?? "";
        var written = operand.Prefix;
        if (chosen is null && written is not null)
            return;
        var tokens = Tokens(operand.Address);

        // ca65 reads a `(` at the head of an operand, or straight after a prefix, as indirect
        // addressing, so an expression that starts with one — `lda (hi + lo) * 2`, which the
        // language allows, `jml (bank << 16) | .loword(f)`, or one written out with parentheses
        // around its first operation — gets a unary `+` in front of it. It changes nothing and
        // keeps the operand an expression.
        var opens = tokens is [{ Kind: SyntaxKind.OpenParen }, ..]
            || (tokens.Count > 0 && edits.Before.GetValueOrDefault(tokens[0].Position, "").StartsWith('('));
        var text = opens ? prefix + "+" : prefix;

        if (written is not null)
        {
            // The source already said which; write the one that was chosen, in case an
            // expression made it wider.
            foreach (var token in written.ChildTokens)
                edits.Replace[token.Position] = "";
            edits.Replace[written.Name.Position] = text;
            return;
        }
        if (tokens.Count > 0)
            edits.Before[tokens[0].Position] = text + edits.Before.GetValueOrDefault(tokens[0].Position, "");
    }

    /// <summary>What the output writes in place of the source's own tokens.</summary>
    private sealed class Edits
    {
        /// <summary>Text to write before a token, by the token's position.</summary>
        public Dictionary<int, string> Before { get; } = [];

        /// <summary>Text to write after a token, by the token's position.</summary>
        public Dictionary<int, string> After { get; } = [];

        /// <summary>Text to write instead of a token, by the token's position; empty omits it.</summary>
        public Dictionary<int, string> Replace { get; } = [];

        /// <summary>Source spellings to keep in a comment at the end of the line.</summary>
        public List<string> Comments { get; } = [];

        /// <summary>Tokens written as part of the one before them, with no whitespace between the two.</summary>
        public HashSet<int> Joined { get; } = [];
    }

    /// <summary>
    /// What writing one line's statement does, a method per kind. The work is the emitter's
    /// own; this says which of it each kind asks for.
    /// <para>
    /// A kind with no method here writes nothing. A function, a signature set, an enum member,
    /// a charmap entry, a list's items and a member's value exist for the analysis: a call is
    /// written as its body, and a type as the constants it names. An <c>.error</c> the build
    /// reached has already been reported, and never reaches ca65. A <c>.cpu</c> item, a segment
    /// declaration, an <c>.export</c> or <c>.import</c> — both already written — and a closing
    /// brace all say something about the file without generating anything.
    /// </para>
    /// </summary>
    /// <param name="emitter">The emitter whose file is being written.</param>
    private sealed class Statements(Emitter emitter) : SyntaxVisitor
    {
        // The line whose statement is being written. A macro call writes a body whose lines are
        // walked from inside its own method, so the line is put back when that returns.
        private LineSyntax? walked;

        /// <summary>The line being written, which every method below is about.</summary>
        private LineSyntax Line => walked!;

        /// <summary>Writes what <paramref name="line"/> generates.</summary>
        /// <param name="line">The line.</param>
        public void Walk(LineSyntax line)
        {
            var outer = walked;
            walked = line;
            Visit(line.Statement);
            walked = outer;
        }

        /// <inheritdoc/>
        public override void VisitBlankLine(BlankLineSyntax node) => emitter.pendingBlank = true;

        /// <inheritdoc/>
        public override void VisitLabeledLine(LabeledLineSyntax node) => emitter.LabeledLine(Line, node);

        /// <inheritdoc/>
        public override void VisitConstantDeclaration(ConstantDeclarationSyntax node) =>
            emitter.Constant(Line, node);

        /// <summary>
        /// Data that names itself stands where its first byte does, and ends where its last byte
        /// does when anything measures it.
        /// </summary>
        /// <param name="node">The declaration.</param>
        public override void VisitDataDeclaration(DataDeclarationSyntax node)
        {
            emitter.Declared(Line, node);
            if (Line.OpensBlockKind is not (BlockKind.Data or BlockKind.DataBody))
                emitter.End(node);
        }

        /// <inheritdoc/>
        public override void VisitDataDirective(DataDirectiveSyntax node)
        {
            if (DataSyntax.IsElementType(node))
                emitter.Elements(Line, node, symbol: null);
            else if (emitter.layout.Of(node, emitter.expansion) is null)
                emitter.NotTranspiled(node);
            else
                emitter.Source(Line, node, emitter.layout.Of(node, emitter.expansion)?.Length ?? 0);
        }

        /// <inheritdoc/>
        public override void VisitDataValues(DataValuesSyntax node) => emitter.Values(Line, node);

        /// <inheritdoc/>
        public override void VisitInstructionStatement(InstructionStatementSyntax node)
        {
            if (SyntaxFacts.LongBranches.Contains(node.Mnemonic.Text)
                && emitter.layout.Of(node, emitter.expansion) is { } laid)
            {
                emitter.Branch(Line, node, laid);
            }
            else
            {
                emitter.Source(Line, node, emitter.layout.Of(node, emitter.expansion)?.Length ?? 0);
            }
        }

        /// <inheritdoc/>
        public override void VisitExternProcDeclaration(ExternProcDeclarationSyntax node) =>
            emitter.ExternProc(node);

        /// <summary>
        /// An assertion nt65 answered has been answered; one it could not depends on where things
        /// land, so it is written out for ld65 to check, with the level ca65 needs for that.
        /// </summary>
        /// <param name="node">The assertion.</param>
        public override void VisitAssertDirective(AssertDirectiveSyntax node)
        {
            if (emitter.model.ValueOf(
                node.Condition, emitter.expansion, emitter.layout.SpanOf, emitter.layout.CyclesOf).AsNumber() is null)
                emitter.Linked(Line, node);
        }

        /// <inheritdoc/>
        public override void VisitMacroCall(MacroCallSyntax node) => emitter.Expand(Line, node);

        /// <inheritdoc/>
        public override void VisitEnsureDirective(EnsureDirectiveSyntax node) => emitter.Ensure(Line, node);

        /// <inheritdoc/>
        public override void VisitBlockSplice(BlockSpliceSyntax node) => emitter.Splice(node);
    }
}
