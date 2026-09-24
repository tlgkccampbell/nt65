using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;
using static Norristown.Emit.Ca65Directives;
using static Norristown.Emit.Ca65Numbers;

namespace Norristown.Emit;

/// <summary>
/// Writes one file's ca65. The output is readable. It keeps the source's own spacing between
/// the tokens of a line, drops its comments, and uses two levels of indentation of its own
/// rather than the source's, because the output is flat and has none of the blocks the source's
/// indentation shows. The output is also deterministic, so the same source always gives the
/// same bytes.
/// <para>
/// A module that another module places has no file of its own. One emitter writes each module
/// of a translation unit, and the one at the root writes each placed module's lines at the
/// <c>.place</c> that places it, so that the unit is one file.
/// </para>
/// <para>
/// Everything the output depends on is written into it. The header fixes the CPU and switches
/// off every ca65 option that changes syntax. Every segment carries its address size, every
/// addressing mode that could be read two ways carries its prefix, and character and string
/// data is written as bytes, so no ca65 command line can change what the output means.
/// </para>
/// <para>
/// What the output does not hold is debug information. Where each line came from is recorded
/// in <see cref="OutputFile.LineSources"/>, which <see cref="LineMap"/> writes beside the
/// ca65 as its own file, so that the ca65 is only the program.
/// </para>
/// </summary>
public sealed class Emitter
{
    /// <summary>
    /// The indentation that starts a line that is not a label, a definition or a file-level
    /// directive.
    /// <para>
    /// The output is flat. It holds no ca65 <c>.proc</c>, <c>.scope</c>, <c>.enum</c> or
    /// <c>.struct</c>, so nothing in it opens a block that indentation could stand for. It has
    /// two levels, as hand-written ca65 does, with names at the margin and what they hold
    /// indented once. The source's own indentation would indent for constructs that no longer
    /// exist in the output.
    /// </para>
    /// </summary>
    internal const string Body = "    ";

    // The visitor that writing a line dispatches through, with one method per kind of statement.
    private readonly Statements statements;

    // What a record writer can reach of this emitter.
    private readonly RecordOutput recordOutput;

    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly FlatNames names;
    private readonly List<Diagnostic> diagnostics;
    private readonly string source;
    private readonly string output;

    // This module's index among the translation unit's sources, which every line it writes records.
    private readonly int file;

    // The lines that open and close the part of the output each placed module wrote. The placed
    // modules are the ones this module places and the ones they place in turn.
    private readonly List<(string Source, EmittedLine Opens, EmittedLine Closes)> parts = [];

    // The output, kept as structured lines until the whole file is written, because lining up
    // a run of named data lines and folding a run of equal bytes into a `.res` cannot be done
    // on lines that are already text.
    private readonly List<EmittedLine> lines = [];

    // The header's last line. A file that writes anything has more lines after it.
    private EmittedLine? headerEnd;
    private readonly HashSet<Symbol> exported = [];

    // The counter name each folded repetition uses in the output. A repetition's body may be
    // written out many times, inside another repetition or in every expansion of a macro, and
    // it uses the same counter name every time so that the iterations of an enclosing
    // repetition still match.
    private readonly Dictionary<Symbol, string> counters = [];

    // The symbols the file measures with `.endof` or `.spanof`, each of which needs a label just
    // past its last byte. A use may come before the thing it measures, so they are found up
    // front.
    private readonly HashSet<Symbol> ends = [];

    // The symbols of this file that other files measure. This file exports their end labels,
    // and those files import them.
    private readonly IReadOnlySet<Symbol> measuredElsewhere;

    // The width at which the previous 65816 immediate of each register was written, in output
    // order, which is exactly ca65's setting when the next one is reached.
    private readonly Dictionary<WidthRegister, int> widths = [];

    // The segment the output is in, which is the last one a `.segment` or `.popseg` switched to,
    // or null where no segment is known to be open.
    private string? writtenSegment;
    private bool pendingBlank;

    // Where the walk is, which every block, repetition and macro call it enters changes and
    // puts back when it leaves.
    private WalkContext context = new(null, null, null, null, 0);

    // The modules this one places, by the `.place` that places each, and the files of the
    // translation unit, whose names are defined in the same output and need no import.
    private IReadOnlyDictionary<PlaceDirectiveSyntax, Emitter> placing = new Dictionary<PlaceDirectiveSyntax, Emitter>();
    private IReadOnlySet<string> unit = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Initializes an emitter for one file of the program. <paramref name="file"/> is the file's
    /// index in the output's source list, which each line records for the map.
    /// </summary>
    private Emitter(
        SemanticModel model, CodeLayout layout, FlatNames names, List<Diagnostic> diagnostics,
        string source, string output, IReadOnlySet<Symbol> measuredElsewhere, int file = 0)
    {
        statements = new Statements(this);
        recordOutput = new RecordOutput(this);
        this.measuredElsewhere = measuredElsewhere;
        this.model = model;
        this.layout = layout;
        this.names = names;
        this.diagnostics = diagnostics;
        this.source = source;
        this.output = output;
        this.file = file;
    }

    /// <summary>
    /// Returns the ca65 for <paramref name="model"/>'s file. <paramref name="outRoot"/> is the
    /// project's output tree, or null for the project's root. <paramref name="measuredElsewhere"/>
    /// holds the symbols the program's other files measure with <c>.endof</c> and <c>.spanof</c>.
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
        emitter.Linkage(emitter.Exports());
        emitter.Linkage(emitter.Imports());
        emitter.WalkContainer(model.Tree.Root);
        LinePasses.AlignColumns(emitter.lines);
        LinePasses.FoldFills(emitter.lines);
        return new OutputFile(path, emitter.OutputText(), [.. emitter.lines.Select(line => line.Bytes)])
        {
            IsEmpty = emitter.WritesNothing(),
            Source = model.Tree.Path,
            SourceSize = SourceSize(model),
            LineSources = [.. emitter.lines.Select(line => line.Source)],
        };
    }

    /// <summary>
    /// Returns the ca65 for a translation unit of several modules, as one <c>.s</c> named after
    /// the first of <paramref name="members"/>, which is the module at the root. The other
    /// members are the modules placed in it, in the order they are written. Each placed module's
    /// items are written at the <c>.place</c> that places it, between a comment naming the module
    /// and its source and a comment closing it. What every module exports and imports is
    /// gathered at the top. Each import appears once, regardless of how many modules make it,
    /// and nothing that a module of the unit defines is imported.
    /// </summary>
    internal static OutputFile Emit(
        IReadOnlyList<(SemanticModel Model, CodeLayout Layout, FlatNames Names, IReadOnlySet<Symbol> MeasuredElsewhere)> members,
        Placements placements, List<Diagnostic> diagnostics, string? outRoot = null)
    {
        var root = members[0].Model;
        var path = OutputPath(root, outRoot);
        var unit = members.Select(member => member.Model.Tree.Path).ToHashSet(StringComparer.Ordinal);
        var emitters = members
            .Select((member, i) => new Emitter(member.Model, member.Layout, member.Names, diagnostics,
                member.Model.Tree.Path, path, member.MeasuredElsewhere, i))
            .ToList();
        var byPath = emitters.ToDictionary(emitter => emitter.source, StringComparer.Ordinal);
        foreach (var emitter in emitters)
        {
            emitter.unit = unit;
            emitter.placing = emitter.model.Tree.Root.DescendantNodes().OfType<PlaceDirectiveSyntax>()
                .Where(place => placements.Placed(place) is { } placed && byPath.ContainsKey(placed.Path))
                .ToDictionary(place => place, place => byPath[placements.Placed(place)!.Path]);
            emitter.Ends();
        }

        var first = emitters[0];
        first.Header();
        first.Linkage([.. emitters.SelectMany(emitter => emitter.Exports()).Distinct(StringComparer.Ordinal)]);
        first.Linkage([.. emitters.SelectMany(emitter => emitter.Imports()).Distinct(StringComparer.Ordinal)]);
        first.WalkContainer(root.Tree.Root);
        LinePasses.AlignColumns(first.lines);
        LinePasses.FoldFills(first.lines);

        // Each placed module's part is found by the line objects that open and close it. They
        // remain the same objects no matter how many lines the passes above removed before them.
        var lines = first.lines;
        var sources = new List<OutputSource> { new(root.Tree.Path, SourceSize(root), 0, lines.Count) };
        foreach (var member in members.Skip(1))
        {
            var part = first.parts.FirstOrDefault(part => part.Source == member.Model.Tree.Path);
            var opens = part.Opens is null ? -1 : lines.FindIndex(line => ReferenceEquals(line, part.Opens));
            var closes = part.Closes is null ? -1 : lines.FindIndex(line => ReferenceEquals(line, part.Closes));
            sources.Add(new OutputSource(
                member.Model.Tree.Path, SourceSize(member.Model), Math.Max(opens, 0), opens < 0 ? 0 : closes - opens + 1));
        }
        return new OutputFile(path, first.OutputText(), [.. lines.Select(line => line.Bytes)])
        {
            IsEmpty = first.WritesNothing(),
            Source = root.Tree.Path,
            SourceSize = SourceSize(root),
            LineSources = [.. lines.Select(line => line.Source)],
            LineFiles = [.. lines.Select(line => line.File)],
            Sources = sources,
        };
    }

    /// <summary>
    /// Returns where a file's output goes. For example, <c>.module gfx::sprite</c> is written to
    /// <c>gfx/sprite.s</c> under the project's output tree, regardless of where the source is. A
    /// source can therefore move, or live outside the project, without its output moving. A file
    /// that names no module, which is already an error, is named after its source.
    /// </summary>
    public static string OutputPath(SemanticModel model, string? outRoot = null)
    {
        var path = model.FileScope.Module is { } module
            ? module.Replace("::", "/", StringComparison.Ordinal) + ".s"
            : Paths.Normalized(model.Tree.Path).Split('/')[^1] is var name
                && name.EndsWith(".nt65", StringComparison.OrdinalIgnoreCase) ? name[..^5] + ".s" : name + ".s";
        return string.IsNullOrEmpty(outRoot) || outRoot == "." ? path : $"{outRoot.TrimEnd('/')}/{path}";
    }

    /// <summary>Returns the size in bytes of a module's source, which the line map records.</summary>
    private static int SourceSize(SemanticModel model) => Encoding.UTF8.GetByteCount(model.Tree.Text);

    /// <summary>
    /// Returns whether <paramref name="symbol"/> is a struct or union whose size this file exports.
    /// </summary>
    private static bool IsSized(Symbol symbol) => symbol is { IsExported: true, IsLayout: true, Size: not null };


    /// <summary>
    /// Formats an <c>.export</c> or <c>.import</c> of <paramref name="name"/> with the address
    /// size <paramref name="size"/>, as <c>.exportzp</c>, <c>.export name: far</c> or the plain
    /// form.
    /// </summary>
    private static string LinkageDirective(string directive, AddressSize? size, string name) => size switch
    {
        AddressSize.ZeroPage => $"{directive}zp {name}",
        AddressSize.Absolute => $"{directive} {name}: abs",
        AddressSize.Far => $"{directive} {name}: far",
        _ => $"{directive} {name}",
    };

    /// <summary>
    /// Returns the size an export needs to state, or null where ca65's default already gives it.
    /// ca65 takes an export of an address as absolute unless told otherwise. An import always
    /// states its size, because ld65 warns about an import whose size it had to guess under a
    /// far memory model.
    /// </summary>
    private static AddressSize? Implicit(AddressSize? size) => size == AddressSize.Absolute ? null : size;

    /// <summary>Returns where a call appears in the source, as the comment before its expansion names it.</summary>
    private static string Where(StatementSyntax call)
    {
        return $"{call.Tree.Path}:{call.LineIndex + 1}";
    }

    /// <summary>
    /// Returns the value of the one byte a line of <paramref name="directive"/>'s values writes,
    /// or null when the line writes anything else. A <c>.byte</c> with a single value can still
    /// write more or fewer bytes than one, as text does, so the length decides.
    /// </summary>
    /// <param name="directive">The directive whose type the line's values have.</param>
    /// <param name="single">The line's value as written, or null when it has more than one.</param>
    /// <param name="bytes">The number of bytes the line assembles to.</param>
    private static string? ByteValue(DataDirectiveSyntax directive, string? single, long bytes) =>
        directive.Directive.DirectiveKind == DirectiveKind.Byte && bytes == 1 ? single : null;

    /// <summary>
    /// Returns a label definition or, for a name ca65 would misread as an address-size prefix
    /// (<c>z</c> or <c>f</c>), the <c>:= *</c> assignment written in its place.
    /// </summary>
    private static string LabelText(string name) => name is "z" or "f" ? $"{name} := *" : $"{name}:";

    /// <summary>
    /// Formats text as its byte values. Empty text is no bytes, which is written as an empty
    /// string, as it is for an empty literal.
    /// </summary>
    private static string BytesText(IReadOnlyList<long> bytes) =>
        bytes.Count == 0 ? "\"\"" : string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2)));

    /// <summary>
    /// Returns the lines as the file holds them, each with its comment at the column where the
    /// comments line up.
    /// </summary>
    private string OutputText()
    {
        var text = new StringBuilder();
        foreach (var line in lines)
            text.Append(EmittedLine.Commented(line.Text, line.Comment).TrimEnd()).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Records the routines and data declarations this file measures, and claims their end
    /// labels and the size constants of the types it exports. The end of <c>f</c> is written
    /// <c>f__end</c>. That spelling is fixed, because other files and hand-written ca65 refer to
    /// it once it is exported, so a name that collides with it is an error rather than a reason
    /// to rename.
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
            var end = EndLabelOf(measured);
            if (names.Claimed(end) is { } other)
            {
                diagnostics.Add(new Diagnostic(measured.DeclarationSpan,
                    Catalogue.OutputNameCollision.Message(
    other.QualifiedName,
    $"the end of `{measured.QualifiedName}`",
    end), [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
            }
            names.Claim(end);
        }

        foreach (var type in model.Symbols.Where(symbol => symbol.Tree == model.Tree && IsSized(symbol)))
        {
            var size = SizeConstantOf(type);
            if (names.Claimed(size) is { } other)
            {
                diagnostics.Add(new Diagnostic(type.DeclarationSpan,
                    Catalogue.OutputNameCollision.Message(
    other.QualifiedName, $"the size of `{type.QualifiedName}`", size),
                    [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
            }
            names.Claim(size);
        }
    }

    /// <summary>Returns the label just past a symbol's last byte, which is what <c>.endof</c> stands for.</summary>
    private string EndLabelOf(Symbol symbol) => NameOf(symbol) + "__end";

    /// <summary>
    /// Returns the name of the constant that an exported struct or union's size is exported as,
    /// so that ca65 and C code can size what they allocate by it. Like an end label, its
    /// spelling is fixed.
    /// </summary>
    private string SizeConstantOf(Symbol type) => NameOf(type) + "__sizeof";

    /// <summary>
    /// Writes the end label of the symbol <paramref name="declaration"/> declares, if it has one.
    /// </summary>
    private void End(StatementSyntax declaration)
    {
        if (model.DeclaredBy(declaration, context.Expansion) is not { } symbol || !ends.Contains(symbol))
            return;
        Segment();
        Flush();
        Line(LabelText(EndLabelOf(symbol)), EmittedLineKind.Declaration);
    }

    /// <summary>
    /// Writes the header, which sets the CPU, turns smart mode off, makes symbols case-sensitive
    /// and switches off every <c>.feature</c> that changes syntax, so that no ca65 command line
    /// can change what the file means.
    /// </summary>
    private void Header()
    {
        Line($"; Generated by nt65 from {source}. Do not edit.");
        Line($".setcpu \"{CpuNames.FormatForCa65(layout.Cpu)}\"");
        Line(".smart -");
        Line(".case +");
        Line(".feature at_in_identifiers -, bracket_as_indirect -, c_comments -");
        Line(".feature dollar_in_identifiers -, dollar_is_pc -, force_range -, labels_without_colons -");
        Line(".feature leading_dot_in_identifiers -, line_continuations -, long_jsr_jmp_rts -");
        Line(".feature loose_char_term -, loose_string_term -, missing_char_term -, org_per_seg -");
        Line(".feature pc_assignment -, string_escapes -, ubiquitous_idents -, underline_in_numbers -");
        headerEnd = lines[^1];
    }

    /// <summary>
    /// Returns whether the file holds nothing but its header, with no export, import, byte, label
    /// or assertion. That is the case for a module that declares only what crosses modules by
    /// value.
    /// </summary>
    private bool WritesNothing() =>
        lines.SkipWhile(line => !ReferenceEquals(line, headerEnd)).Skip(1)
            .All(line => line is { Text: "", Label: null, Comment: null });

    /// <summary>
    /// Returns every export, under its linker name and with the address size nt65 gives it,
    /// hoisted to the top of the file in the order the file declares them.
    /// <para>
    /// A macro is not a symbol the linker sees. What crosses between modules is its expansion,
    /// written into each module that calls it. A charmap, a function and a list are used by
    /// value, so what crosses is their values, written where they are used. A scope and a type
    /// are only a way to reach their members, which are exported one by one, a type's as the
    /// flat constants they become. An import is defined elsewhere, and every module that uses
    /// one imports it itself.
    /// </para>
    /// </summary>
    private List<string> Exports()
    {
        var directives = new List<string>();
        foreach (var symbol in model.Symbols)
        {
            if (IsSized(symbol) && symbol.Tree == model.Tree)
                directives.Add(LinkageDirective(".export", Implicit(Value.Of(symbol.Size!.Value).ImpliedAddressSize()), SizeConstantOf(symbol)));
            if (!symbol.IsExported || !ProgramSymbols.IsLinked(symbol))
                continue;
            exported.Add(symbol);

            // A constant is as wide as its value, which is how ca65 sizes one it is given.
            var size = symbol.ExportSize
                ?? Implicit(symbol.IsAddress ? symbol.AddressSize : symbol.Value.ImpliedAddressSize());
            directives.Add(LinkageDirective(".export", size, NameOf(symbol)));

            // Another module that measures the declaration refers to its end label, so that is
            // exported alongside it.
            if (measuredElsewhere.Contains(symbol))
                directives.Add(LinkageDirective(".export", size, EndLabelOf(symbol)));
        }
        return directives;
    }

    /// <summary>Writes a run of exports or imports, set off from what is around it by a blank line.</summary>
    private void Linkage(IReadOnlyList<string> directives)
    {
        if (directives.Count == 0)
            return;
        Blank();
        foreach (var text in directives)
            Line(text);
        pendingBlank = true;
    }

    /// <summary>
    /// Returns everything the file gets from outside it. That is what another nt65 file exports
    /// and this one names, and what an <c>.import</c> item declares and the file uses. Each
    /// carries the address size nt65 gives it, so ca65 sizes an operand the way nt65 did. Nothing
    /// the file does not use is imported, because an import pulls the module that defines it out
    /// of a library.
    /// <para>
    /// A constant is not imported, because ca65 cannot use an imported symbol where it needs a
    /// value, so the constant is written out here instead. A checked import yields both the value
    /// nt65 uses and an assertion that the definition it will be linked against agrees.
    /// </para>
    /// </summary>
    private List<string> Imports()
    {
        var directives = new List<string>();
        var used = model.Used.ToHashSet();
        foreach (var symbol in model.Symbols
            .Where(symbol => symbol.Kind is SymbolKind.ImportedAddress or SymbolKind.ImportedConstant && used.Contains(symbol))
            .Concat(model.ExternalSymbols.Where(symbol => !DefinedInTheUnit(symbol))))
        {
            if (Import(symbol) is not { } line)
                continue;
            directives.Add(line);

            // A checked import is checked by every module that uses it, since each was built
            // against the value.
            if (symbol is { Kind: SymbolKind.ImportedConstant } && symbol.Value.AsNumber() is { } checkedValue)
            {
                directives.Add($".assert {NameOf(symbol)} = {Constant(checkedValue)}, lderror, "
                    + $"\"{NameOf(symbol)} is not {Constant(checkedValue)}, which is what "
                    + $"{source} was built against\"");
            }
        }

        // The symbols the linker defines for each segment this file, or a macro it calls, asks about.
        foreach (var (name, size) in SegmentImports())
            directives.Add(LinkageDirective(".import", size, name));

        // The end of what this file measures in another file comes from that file, which
        // exports it beside the declaration.
        foreach (var measured in Extents.MeasuredIn(model)
            .Where(symbol => symbol.Tree != model.Tree && !DefinedInTheUnit(symbol))
            .OrderBy(symbol => NameOf(symbol), StringComparer.Ordinal))
        {
            directives.Add(LinkageDirective(".import", measured.AddressSizeIn(model.Tree), EndLabelOf(measured)));
        }
        return directives;
    }

    /// <summary>
    /// Returns whether another module of the translation unit defines <paramref name="symbol"/>
    /// in the same output as this one. The symbol is defined there under the same name, so
    /// importing it, or writing a constant out by value beside its definition, would define it
    /// twice. What a module imports from outside the program is still imported, even when several
    /// modules of the unit use it.
    /// </summary>
    private bool DefinedInTheUnit(Symbol symbol) =>
        unit.Contains(symbol.Tree.Path) && symbol.Kind is not (SymbolKind.ImportedAddress or SymbolKind.ImportedConstant);

    /// <summary>
    /// Returns, with their sizes, the names ld65 defines for the segments that this file's
    /// <c>.loadof</c>, <c>.runof</c> and <c>.spanof</c> calls ask about, including the calls in
    /// every macro the file may expand.
    /// </summary>
    private IEnumerable<(string Name, AddressSize Size)> SegmentImports()
    {
        var calls = model.Tree.Root.DescendantNodes().OfType<MacroCallSyntax>()
            .Select(model.MacroAt).OfType<Symbol>();
        var bodies = Macros.Reachable(calls).Select(macro => macro.Definition).OfType<SyntaxNode>();
        return new[] { model.Tree.Root }.Concat(bodies)
            .SelectMany(node => node.DescendantNodes().OfType<CallExpressionSyntax>())
            .Select(call => SegmentFunctions.Of(call, model))
            .OfType<(BuiltinKind Function, Segment Segment)>()
            .Select(about => (SegmentFunctions.LinkerName(about.Function, about.Segment), SegmentFunctions.SizeOf()))
            .Distinct()
            .OrderBy(import => import.Item1, StringComparer.Ordinal);
    }

    /// <summary>Returns the line that brings one symbol in, or null for a symbol that needs no line at all.</summary>
    private string? Import(Symbol symbol)
    {
        var name = NameOf(symbol);
        if (symbol.IsAddress || symbol.Kind == SymbolKind.ImportedConstant)
            return LinkageDirective(".import", symbol.AddressSizeIn(model.Tree), name);

        // A constant another file declares is written out by value. A constant whose value nt65
        // does not know has already been reported, and a string is only ever used through
        // `.strlen` and `.strat`, which are numbers before anything is written.
        return symbol.Value.AsNumber() is { } value ? $"{name} = {Constant(value)}" : null;
    }

    /// <summary>Writes a whole file, from its first top-level line to its last.</summary>
    private void WalkContainer(FileSyntax file) => Walk(file.Members, from: 0);

    /// <summary>
    /// Writes a run of sibling lines and blocks. The <c>.if</c> chains among them are resolved
    /// here, because a chain spans consecutive siblings and only the code walking them sees them
    /// together.
    /// </summary>
    private void Walk(IReadOnlyList<SyntaxNode> children, int from)
    {
        foreach (var (child, included) in ConditionChain.Walk(model, children, from, context.Expansion))
        {
            if (child is LineSyntax line)
                WalkLine(line);
            else if (child is BlockSyntax block && included)
                WalkBlock(block, block.BlockKind);
        }
    }

    /// <summary>
    /// Writes one block as its kind requires. <paramref name="kind"/> is passed apart from the
    /// block, because a <c>.multiproc</c> writes its body once per member as a <c>.proc</c>.
    /// </summary>
    private void WalkBlock(BlockSyntax block, BlockKind kind)
    {
        var lines = block.Members;
        var opener = block.Opener.Statement;

        // A macro body is written at every call that expands it, and nothing is written where it
        // is declared.
        if (kind == BlockKind.Macro)
            return;

        // A block argument belongs to the call rather than being a block of its own. The line
        // that opens it is the call, which is written out here, and its lines are written where
        // the body splices them.
        if (kind == BlockKind.MacroBlock)
        {
            if (Macros.CallIn(opener) is not null)
                WalkLine(block.Opener);
            return;
        }

        // A branch the build takes is written out where it stands, with no trace of the `.if`
        // around it, and a branch the build leaves out is not written at all. The block adds no
        // nesting of its own, so a segment block inside it is nested only if the `.if` was.
        if (kind == BlockKind.If)
        {
            Walk(lines, from: 1);
            return;
        }

        // A repetition is unrolled here: its body is written once per iteration, with every
        // per-iteration decision made as it is written. Nothing is reported from here, because
        // layout walked the same iterations and has already reported what is wrong with the
        // count or the list. Whether the iterations came out alike enough to be written as one
        // ca65 `.repeat` is then decided from the lines, once they are all written.
        if (Constructs.Repeats(kind))
        {
            var iterations = Repetitions.Of(model, block, context.Expansion, null);
            var starts = new List<int>(iterations.Count);
            foreach (var iteration in iterations)
            {
                starts.Add(this.lines.Count);
                using (Enter(context with { Expansion = iteration }))
                {
                    Walk(lines, from: 1);
                }
            }
            if (kind == BlockKind.Repeat)
                FoldRepetition(block, Repetitions.BindingOf(model, opener), starts);
            return;
        }

        // `.multiproc` writes its body out once per member, as the `.each` around a `.proc`
        // that it is shorthand for would write it.
        if (kind == BlockKind.MultiProc)
        {
            if (model.FamilyAt(opener) is null)
                return;
            foreach (var iteration in Repetitions.Of(model, block, context.Expansion, null))
            {
                using (Enter(context with { Expansion = iteration }))
                {
                    WalkBlock(block, BlockKind.Proc);
                }
            }
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

        // An enum writes its members out as the constants they are. A member may sit inside
        // an `.if` in the body, so the branches this build takes are read as well.
        if (kind == BlockKind.Enum)
        {
            Members(lines, from: 1);
            pendingBlank = true;
            return;
        }

        // One record spread over several lines is written where its line is, as one directive
        // per member. The lines of values belong to the record and write nothing themselves.
        if (kind == BlockKind.RecordInitializer)
        {
            WalkLine(block.Opener);
            return;
        }

        // A segment block anywhere but the file's own top level temporarily switches away from
        // the enclosing segment, which is what `.pushseg` and `.popseg` express. A region is
        // always at file level.
        var placing = kind is BlockKind.Segment or BlockKind.Region;
        var proc = kind == BlockKind.Proc && opener is ProcDeclarationSyntax or MultiProcDeclarationSyntax;
        var pushed = placing && context.Depth > 0;
        if (pushed)
        {
            Blank();
            Line(".pushseg");
            writtenSegment = null;
        }
        if (proc)
        {
            Segment();
            Flush();
        }

        var inner = context with
        {
            Segment = placing ? Constructs.SegmentOf(opener) ?? context.Segment : context.Segment,
            Routine = proc ? ProcLabel(opener) : context.Routine,
        };
        using (Enter(inner))
        {
            if (proc)
            {
                // An instance of a family is written under a comment giving the family's line and
                // the instance's name. It is one routine in the output, named as the source names it.
                var routine = context.Routine;
                var instance = model.FamilyAt(opener) is not null && routine is not null ? $"  {routine.QualifiedName}" : "";
                Line($"; {opener.GetText().Trim().TrimEnd('{').TrimEnd()}  {Where(opener)}{instance}");
                Label(block.Opener, routine);
            }
            else if (!placing && kind != BlockKind.Scope)
            {
                WalkLine(block.Opener);
            }

            using (Enter(context with { Depth = context.Depth + 1 }))
            {
                Walk(lines, from: 1);
            }
            if (kind == BlockKind.DataBody
                && (opener as DataDirectiveSyntax ?? (opener as DataDeclarationSyntax)?.Directive) is { } declared)
            {
                Padding(block.Opener, declared);
            }
            if (kind is BlockKind.Proc or BlockKind.Data or BlockKind.DataBody)
                End(opener);
            if (kind == BlockKind.Proc && context.Routine is { } named)
                Line($"; end of {named.Name}");
        }

        if (pushed)
        {
            Line(".popseg");
            writtenSegment = context.Segment;
            pendingBlank = true;
        }
    }

    /// <summary>
    /// Makes <paramref name="next"/> the context being written in, and returns a scope that puts
    /// the current one back when it is disposed, however the walk inside it ends.
    /// </summary>
    private WalkScope Enter(WalkContext next)
    {
        var scope = new WalkScope(this, context);
        context = next;
        return scope;
    }

    /// <summary>
    /// Rewrites a counted repetition whose iterations all came out the same as a single ca65
    /// <c>.repeat</c>, as <see cref="LinePasses.FoldRepeat"/> describes. <paramref name="starts"/>
    /// is where each iteration's lines begin.
    /// </summary>
    private void FoldRepetition(BlockSyntax block, Symbol? binding, IReadOnlyList<int> starts) =>
        LinePasses.FoldRepeat(
            lines, starts, binding is null ? null : () => CounterFor(binding),
            At(block.Opener), At(block.Closer ?? block.Opener), file);

    /// <summary>
    /// Returns the name of a repetition's counter. The counter is an output name like any other,
    /// derived from the source and used by nothing else in the file. A repetition has one counter
    /// no matter how many times its body is written out; otherwise an enclosing repetition would
    /// see a different counter in each of its iterations.
    /// </summary>
    private string CounterFor(Symbol binding)
    {
        if (!counters.TryGetValue(binding, out var counter))
            counters[binding] = counter = names.Generated(binding.Name);
        return counter;
    }

    /// <summary>
    /// Writes the members of an enum, each as the constant it is. A member may sit inside
    /// an <c>.if</c> in the body, so a chain among the lines is resolved here as it is anywhere
    /// else, and members in the branches this build takes count like the body's own lines.
    /// </summary>
    private void Members(IReadOnlyList<SyntaxNode> lines, int from)
    {
        foreach (var (child, included) in ConditionChain.Walk(model, lines, from, context.Expansion))
        {
            if (child is LineSyntax { Statement: EnumMemberSyntax member })
                EnumMember(member);
            else if (child is BlockSyntax { BlockKind: BlockKind.If } block && included)
                Members(block.Members, 1);
        }
    }

    /// <summary>Writes what one line generates, through the visitor that picks a method by statement kind.</summary>
    private void WalkLine(LineSyntax line) => statements.Walk(line);

    /// <summary>
    /// Writes a macro call as the body it expands to, with a comment naming the call. nt65
    /// expands macros itself and emits flat code. ca65's own <c>.macro</c> is never used, so
    /// nt65's macro semantics never depend on ca65's.
    /// </summary>
    private void Expand(LineSyntax line, MacroCallSyntax call)
    {
        if (model.MacroAt(call) is not { Definition: BlockSyntax definition })
        {
            NotTranspiled(call);
            return;
        }

        // Going past the expansion limit is already an error, and writing the expansions out
        // would take as long as the limit spared layout.
        if (layout.ExpansionsExceeded)
            return;

        // A macro that reaches itself is an error already, and has nothing to write.
        if (Expansion.Expanding(context.Expansion, definition))
            return;

        Segment();
        Flush();

        // The `{` of a trailing block belongs to the block rather than to the call, and the
        // comment names the call. The comment that closes the expansion names the macro alone,
        // because the arguments are above it and repeating them would only make the block
        // harder to see.
        var callText = call.GetText().Trim().TrimEnd('{').TrimEnd();
        var called = callText.IndexOf('!') is var bang && bang > 0 ? callText[..(bang + 1)] : callText;
        Line($"{Body}; {callText}  {Where(call)}");

        // Every line of the expansion maps to the call, and a call inside a body maps to the
        // outermost call, which is the line of this file that asked for all of it.
        var expanded = context with
        {
            Expansion = Expansion.Of(context.Expansion, call, definition),
            CallLine = context.CallLine ?? line,
        };
        using (Enter(expanded))
        {
            Walk(definition.Members, from: 1);
        }

        // An expansion has no end of its own in the output. What follows it is the caller's own
        // code, on the same level and under no label, so only the comment marks the end.
        Flush();
        Line($"{Body}; end of {called}");
    }

    /// <summary>
    /// Writes a line naming a <c>block</c> parameter, which stands for the lines the call passed
    /// it. Those lines are the caller's own code, so where they are this file's own lines they
    /// map back to themselves rather than to the call that spliced them.
    /// </summary>
    private void Splice(BlockSpliceSyntax statement)
    {
        if (model.SymbolAt(statement.Name) is not { Parameter: { } parameter }
            || model.ArgumentFor(parameter.Symbol, context.Expansion) is not { Block: { } block })
        {
            return;
        }

        var spliced = context with
        {
            Expansion = Expansion.Spliced(context.Expansion, statement, block),
            CallLine = block.Tree == model.Tree ? null : context.CallLine,
        };
        using (Enter(spliced))
        {
            Walk(Macros.LinesOf(block), from: 0);
        }
    }

    /// <summary>
    /// Returns the routine that a <c>.proc</c>, or one iteration of a <c>.multiproc</c>, writes out
    /// here.
    /// </summary>
    private Symbol? ProcLabel(StatementSyntax opener) => model.DeclaredBy(opener, context.Expansion);

    /// <summary>Writes the label a routine's first byte carries, where the routine has a name.</summary>
    private void Label(LineSyntax line, Symbol? routine)
    {
        if (routine is not null)
            Declare(line, LabelText(NameOf(routine)));
    }

    /// <summary>
    /// Writes a long branch in the form nt65 chose for it. That is the plain short branch where
    /// the target is in reach, and otherwise the opposite branch over a <c>jmp</c> to a
    /// generated label. ca65's own long-branch macro package always has to use the long form
    /// for a forward target, because it chooses before it knows where the target lands.
    /// </summary>
    private void Branch(LineSyntax line, InstructionStatementSyntax statement, LineLayout laid)
    {
        var mnemonic = statement.Mnemonic;
        var (taken, skipped) = Instructions.FormsOf(statement.MnemonicKind);
        var rewriter = new TokenRewriter();
        Substitute(statement, rewriter, nested: false);

        if (!laid.Inverted)
        {
            rewriter.Replacements[mnemonic.Position] = SyntaxFacts.TextOf(taken);
            Code(line, rewriter.Render(statement, Body), laid.Length);
            return;
        }

        var over = names.Generated((context.Routine is null ? "" : NameOf(context.Routine) + "__") + "over");
        rewriter.Replacements[mnemonic.Position] = SyntaxFacts.TextOf(MnemonicKind.Jmp);
        var jump = rewriter.Render(statement, Body);
        Code(line, $"{Body}{SyntaxFacts.TextOf(skipped)} {over}", Instructions.Length(AddressingMode.Relative));
        Write(new EmittedLine(jump, Instructions.Length(AddressingMode.Absolute)));
        Line($"{over}:", EmittedLineKind.Declaration);
    }

    /// <summary>
    /// Writes a line that starts with a label. The label is written in whatever form ca65 reads
    /// as a label, and the statement after it is written as it would be on its own.
    /// </summary>
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
        if (rest is DataDirectiveSyntax && layout.Of(rest, context.Expansion) is null)
        {
            NotTranspiled(rest);
            return;
        }
        var bytes = rest is null ? 0 : layout.Of(rest, context.Expansion)?.Length ?? 0;
        if (rest is not null)
            WriteWidthDirective(rest);
        var rewriter = new TokenRewriter();
        if (rest is not null)
        {
            Substitute(rest, rewriter, nested: false);
            ReplaceFrameSlot(rest, rewriter);
            Direct(rest, rewriter);
        }

        if (model.SymbolAt(label.Name) is not { } reference)
        {
            Declare(line, rewriter.Render(statement), bytes);
            return;
        }

        // ca65 reads `z:` at the start of a line as an address-size prefix, so such a label is
        // written as an assignment instead, as in `z := *` and `f := *`. An assignment takes the
        // whole line, so the rest of the line goes on the next one.
        var text = LabelText(NameOf(reference));
        if (rest is not null && !text.EndsWith(':'))
        {
            Declare(line, text);
            Code(line, rewriter.Render(rest, Body), bytes);
            return;
        }

        rewriter.Replacements[label.Name.Position] = text;
        rewriter.Replacements[label.ColonToken.Position] = "";
        Declare(line, rewriter.Render(statement), bytes);
    }

    /// <summary>
    /// Writes a data declaration, with its name and what it holds where the line holds it. Mixed
    /// data, and an array whose values are in a body, write the name here and their bytes a line
    /// at a time below it.
    /// </summary>
    private void Declared(LineSyntax line, DataDeclarationSyntax declaration)
    {
        if (model.DeclaredBy(declaration, context.Expansion) is not { } symbol)
            return;
        if (declaration.Directive is not { } element)
        {
            Declare(line, LabelText(NameOf(symbol)));
            return;
        }
        if (DataSyntax.IsElementType(element))
        {
            Elements(line, element, symbol);
            return;
        }

        // Bytes such as an `.incbin` are written as the source has them.
        if (layout.Of(element, context.Expansion) is not { } laid)
        {
            NotTranspiled(element);
            return;
        }
        var rewriter = new TokenRewriter();
        Substitute(element, rewriter, nested: false);
        WithName(line, symbol, rewriter.Bare(element, out var comment), laid.Length, comment);
    }

    /// <summary>
    /// Writes an element type, named or not, as its values in the directive of its type, or as
    /// the room it takes filled with zeros. Values in a body are written a line at a time, below
    /// the name.
    /// </summary>
    private void Elements(LineSyntax line, DataDirectiveSyntax directive, Symbol? symbol)
    {
        if (DataSyntax.BodyOf(directive) is { BlockKind: BlockKind.DataBody })
        {
            if (symbol is not null)
                Declare(line, LabelText(NameOf(symbol)));
            return;
        }
        if (directive.Type is { } named)
        {
            if (model.SymbolOf(named) is { IsLayout: true } type)
                new RecordWriter(model, context.Expansion, recordOutput).Records(line, directive, symbol, type);
            else
                NotTranspiled(directive);
            return;
        }
        if (layout.Of(directive, context.Expansion) is not { } laid)
        {
            NotTranspiled(directive);
            return;
        }

        // A list is written without its braces, as the directive's own values are.
        var rewriter = new TokenRewriter();
        string text;
        string? comment = null;
        string? single;
        if (directive.Tail is BracedDataSyntax { Value: ValueListSyntax list })
        {
            var (width, bigEndian) = ElementFormat(directive);
            foreach (var value in list.Values)
                InPlace(value, width, bigEndian, rewriter);
            rewriter.Replacements[list.OpenBraceToken.Position] = "";
            if (!list.CloseBraceToken.IsMissing)
                rewriter.Replacements[list.CloseBraceToken.Position] = "";
            var values = rewriter.Bare(list, out comment);
            text = $"{ForCa65(directive.Directive.DirectiveKind, directive.Directive.Text)} {values}";
            single = list.Values.Count == 1 ? values : null;
        }
        else if (directive.Tail is InlineDataSyntax { Values: var inline })
        {
            Substitute(directive, rewriter, nested: false);
            text = rewriter.Bare(directive, out comment);
            single = inline is [var only] ? rewriter.Render(only).Trim() : null;
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
        var zeros = (int)(PaddedText.Padding(directive, model, context.Expansion)?.Zeros ?? 0);
        var bytes = laid.Length - zeros;
        WithName(line, symbol, text, bytes, comment, ByteValue(directive, single, bytes));
        Padding(line, directive);
    }

    /// <summary>Writes the zeros a padded text is filled out with, where a declaration has any.</summary>
    private void Padding(LineSyntax line, DataDirectiveSyntax directive)
    {
        if (PaddedText.Padding(directive, model, context.Expansion) is not var (zeros, count))
            return;
        foreach (var reserved in Reservations(zeros))
            Code(line, $"{Body}.res {reserved}, $00", (int)reserved, $"padded to {count}");
    }

    /// <summary>Writes one line of a body's values as the directive of the body's type.</summary>
    private void Values(LineSyntax line, DataValuesSyntax values)
    {
        if (DataSyntax.DirectiveOfValues(values) is not { } directive)
            return;
        if (directive.Type is { } named)
        {
            if (model.SymbolOf(named) is { IsLayout: true } type)
                new RecordWriter(model, context.Expansion, recordOutput).Values(line, type, values);
            return;
        }
        if (layout.Of(values, context.Expansion) is not { } laid)
        {
            NotTranspiled(values);
            return;
        }
        var rewriter = new TokenRewriter();
        Substitute(values, rewriter, nested: false);
        var text = rewriter.Bare(values, out var comment);
        Code(line, $"{Body}{ForCa65(directive.Directive.DirectiveKind, directive.Directive.Text)} {text}",
            laid.Length, comment, value: ByteValue(directive, values.Values.Count == 1 ? text : null, laid.Length));
    }

    /// <summary>
    /// Writes a line of data with its name in front, where it has one. The name goes on the same
    /// line, or on a line of its own where ca65 would read the name as a prefix. Where the name
    /// shares the line, the directive is lined up with those of the lines around it rather than
    /// where the source had it, because the name in front is rarely the one the source used.
    /// </summary>
    private void WithName(
        LineSyntax line, Symbol? symbol, string text, int bytes, string? comment = null, string? value = null)
    {
        if (symbol is null)
        {
            Code(line, Body + text, bytes, comment, value: value);
            return;
        }
        if (LabelText(NameOf(symbol)) is var label && !label.EndsWith(':'))
        {
            Declare(line, label);
            Code(line, Body + text, bytes, comment, value: value);
            return;
        }
        WriteNamedData(line, label, text, bytes, comment);
    }

    /// <summary>Writes the label of a line whose statement is written separately.</summary>
    private void LabelOnly(LineSyntax line, LabelSyntax label)
    {
        if (model.SymbolAt(label.Name) is { } reference)
            Declare(line, LabelText(NameOf(reference)));
    }

    /// <summary>
    /// Writes the members of an exported layout, each as the constant offset it is, along with
    /// the layout's size. A layout that nothing exports means nothing to ca65 and is left out
    /// entirely.
    /// </summary>
    private void Offsets(StatementSyntax opener)
    {
        if (opener is not TypeDeclarationSyntax { Name: { } named } || model.SymbolAt(named) is not { } reference)
            return;

        foreach (var member in reference.Body?.Symbols ?? [])
        {
            if (exported.Contains(member) && member.Value.AsNumber() is { } offset)
                Definition($"{NameOf(member)} = {Constant(offset)}");
        }
        if (IsSized(reference))
            Definition($"{SizeConstantOf(reference)} = {Constant(reference.Size!.Value)}");
    }

    /// <summary>Writes one enum member, which is a constant like any other.</summary>
    private void EnumMember(EnumMemberSyntax member)
    {
        if (model.SymbolAt(member.Name) is not { } reference
            || reference.Value.AsNumber() is not { } value)
        {
            return;
        }
        Definition($"{NameOf(reference)} = {Constant(value)}");
    }

    /// <summary>
    /// Returns an expression written out rather than edited in place. A call becomes what it
    /// stands for, and every nested operation is parenthesized, so nothing depends on how ca65
    /// reads precedence. Any comment the expression produces goes to <paramref name="comments"/>,
    /// the comment list of the line it is written into, when there is one. Otherwise, text after
    /// the expression on that line would end up inside its comment.
    /// </summary>
    private string Rendered(SyntaxNode node, List<string>? comments = null)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return "(" + Rendered(parenthesized.Expression, comments) + ")";
            case BinaryExpressionSyntax binary when Evaluator.IsIn(binary.OperatorToken)
                && model.ValueOf(binary, context.Expansion).AsNumber() is null:
                return Compared(binary, comments) ?? binary.GetText().Trim();
            case BinaryExpressionSyntax binary when !Evaluator.IsIn(binary.OperatorToken):
                return $"({Rendered(binary.Left, comments)} {TokenRewriter.Ca65Operator(binary.OperatorToken)} {Rendered(binary.Right, comments)})";
            case UnaryExpressionSyntax unary:
                return $"({unary.OperatorToken.Text}{Rendered(unary.Operand, comments)})";
            default:
                break;
        }

        // Anything with a value nt65 knows is written as that value. Anything else keeps its own
        // spelling, with names flattened.
        if (model.ValueOf(node, context.Expansion).AsNumber() is { } value)
            return Constant(value);
        var rewriter = new TokenRewriter();
        Substitute(node, rewriter, nested: false);
        return rewriter.Inline(node, comments);
    }

    /// <summary>
    /// Writes <c>value .in set</c> as its value, 1 or 0, where nt65 knows it. Where it does not,
    /// because the value is an address that only the linker places, it is written as ca65's
    /// comparisons, one for each item of the set.
    /// </summary>
    private void Membership(BinaryExpressionSyntax binary, TokenRewriter rewriter)
    {
        if (model.ValueOf(binary, context.Expansion).AsNumber() is { } value)
        {
            rewriter.Replace(binary, Constant(value));
            return;
        }
        if (Compared(binary, rewriter.Comments) is not { } compared)
        {
            NotTranspiled(binary);
            return;
        }

        // A parenthesis first in an operand would read as indirection, which a unary `+` prevents.
        rewriter.Replace(binary, "+" + compared);
    }

    /// <summary>
    /// Returns <c>value .in set</c> as ca65's comparisons, joined with <c>||</c>, or null when the
    /// set is a list with an item whose value nt65 does not know. A list's items are written as
    /// their values, since their names belong to the file that declares the list.
    /// </summary>
    private string? Compared(BinaryExpressionSyntax binary, List<string>? comments)
    {
        var value = Rendered(binary.Left, comments);
        var tests = new List<string>();
        switch (binary.Right)
        {
            case SetExpressionSyntax set:
                foreach (var range in set.Items)
                {
                    tests.Add(range.Last is { } last
                        ? $"(({value} >= {Rendered(range.First, comments)}) && ({value} <= {Rendered(last, comments)}))"
                        : $"({value} = {Rendered(range.First, comments)})");
                }
                break;
            case NameExpressionSyntax name when model.SymbolOf(name, context.Expansion) is { Kind: SymbolKind.List } list:
                foreach (var item in list.Items)
                {
                    if (model.ValueOf(item).AsNumber() is not { } known)
                        return null;
                    tests.Add($"({value} = {Constant(known)})");
                }
                break;
            default:
                return null;
        }
        return tests.Count == 0 ? "0" : "(" + string.Join(" || ", tests) + ")";
    }

    /// <summary>
    /// Returns the name a symbol has in the output, at the level being written. A name that a
    /// macro body declares is a different name in every expansion.
    /// </summary>
    private string NameOf(Symbol symbol) => names.Of(symbol, context.Expansion);

    /// <summary>
    /// Returns an argument written where the body named its parameter. It goes in as a
    /// parenthesized whole, so `value * 2` with the argument `1 + 2` is `(1 + 2) * 2`, which is
    /// six where ca65's textual substitution would give five. Likewise, `#&lt;value` with
    /// `label+1` is `#&lt;(label+1)`. The argument keeps its own spelling, because it is an
    /// expression, not a number, and a name in it is written the way the same name would be
    /// written anywhere else.
    /// </summary>
    private string Substituted(SyntaxNode argument, List<string>? comments = null)
    {
        var rewriter = new TokenRewriter();
        Substitute(argument, rewriter, nested: false);
        var text = rewriter.Inline(argument, comments);
        return argument is BinaryExpressionSyntax or UnaryExpressionSyntax
            ? "(" + text + ")"
            : text;
    }

    /// <summary>
    /// Writes a constant as a ca65 assignment under its flat name. A constant whose value uses
    /// the current address is written as a declaration where it stands, any other as a
    /// <see cref="Definition"/>, and a string constant not at all.
    /// </summary>
    private void WriteConstant(LineSyntax line, ConstantDeclarationSyntax statement)
    {
        // A string cannot be expressed as a ca65 constant. It is used through `.strlen` and
        // `.strat`, which are numbers by the time anything is written.
        if (model.SymbolAt(statement.Name) is { } reference)
        {
            if (reference.Value.IsString)
                return;
            var rewriter = new TokenRewriter();
            rewriter.Replacements[statement.Name.Position] = NameOf(reference);
            Substitute(statement.Value, rewriter, nested: false);
            var text = rewriter.Render(statement);
            if (statement.DescendantNodes().OfType<CurrentAddressExpressionSyntax>().Any())
                Declare(line, text);
            else
                Definition(text);
        }
    }

    /// <summary>Writes an extern proc, which is a routine at a constant address and so a constant.</summary>
    private void ExternProc(ExternProcDeclarationSyntax statement)
    {
        if (model.SymbolAt(statement.Name) is not { } reference)
            return;

        var address = statement.Address;
        var rewriter = new TokenRewriter();
        Substitute(address, rewriter, nested: false);
        Definition($"{NameOf(reference)} = {rewriter.Render(address)}");
    }

    /// <summary>
    /// Writes a statement as its own source, with names, immediates, frame slots and direct-page
    /// operands rewritten for ca65. It is how an instruction or a directive with no special
    /// handling is written.
    /// </summary>
    /// <param name="line">The line that holds the statement.</param>
    /// <param name="statement">The statement.</param>
    /// <param name="bytes">The number of bytes the statement assembles to.</param>
    /// <param name="located">Whether the line is mapped even though it holds no bytes.</param>
    private void Source(LineSyntax line, StatementSyntax statement, int bytes, bool located = false)
    {
        WriteWidthDirective(statement);
        var rewriter = new TokenRewriter();
        Substitute(statement, rewriter, nested: false);
        Immediate(statement, bytes, rewriter);
        ReplaceFrameSlot(statement, rewriter);
        Direct(statement, rewriter);
        Code(line, rewriter.Render(statement, Body), bytes, located: located);
    }

    /// <summary>
    /// Rewrites a negative constant in an immediate as its two's complement. An immediate is a
    /// byte or a word slot, as wide as the instruction makes it.
    /// </summary>
    private void Immediate(StatementSyntax statement, int bytes, TokenRewriter rewriter)
    {
        if (statement is InstructionStatementSyntax { Operand: ImmediateOperandSyntax { Value: var value, SecondValue: null } }
            && bytes is 2 or 3 && Datum(value, bytes - 1, bigEndian: false, rewriter.Comments) is { } text)
        {
            rewriter.Replace(value, text, around: false);
        }
    }

    /// <summary>
    /// Writes an assertion the linker checks, as ca65's <c>.assert</c> with the level that defers
    /// it to ld65.
    /// </summary>
    private void WriteLinkerAssertion(LineSyntax line, AssertDirectiveSyntax statement)
    {
        var rewriter = new TokenRewriter();
        Substitute(statement, rewriter, nested: false);

        // A missing condition has no token to put the level after, and the line has already
        // been reported.
        if (TokenRewriter.Tokens(statement.Condition) is [.., var end])
            rewriter.After[end.Position] = rewriter.After.GetValueOrDefault(end.Position, "") + ", lderror";
        Code(line, rewriter.Render(statement), 0, located: true);
    }

    /// <summary>
    /// Writes an <c>.ensure</c> as the <c>rep</c> and <c>sep</c> the analysis found it needs,
    /// which is nothing where the widths already hold.
    /// </summary>
    private void Ensure(LineSyntax line, EnsureDirectiveSyntax directive)
    {
        if (layout.Of(directive, context.Expansion)?.Ensured is not { } ensured)
            return;
        if (ensured.Reset != StatusFlags.None)
            Code(line, $"{Body}rep #{Hex((long)ensured.Reset, 2)}", 2);
        if (ensured.Set != StatusFlags.None)
            Code(line, $"{Body}sep #{Hex((long)ensured.Set, 2)}", 2);
    }

    /// <summary>
    /// Replaces a frame's member in a stack-relative operand with the offset from the stack
    /// pointer that the analysis counted for it here, because ca65 knows nothing of frames.
    /// </summary>
    private void ReplaceFrameSlot(StatementSyntax statement, TokenRewriter rewriter)
    {
        if (layout.Of(statement, context.Expansion)?.Slot is not { } slot
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
            rewriter.ReplaceName(name, slot.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Replaces a <c>d:</c> operand with its offset into the direct page, given the D the
    /// analysis found. For example, with D at <c>$2100</c>, <c>lda d:$2105</c> is
    /// <c>lda z:$05</c>. ca65 has no <c>d:</c>, and knows nothing of D.
    /// </summary>
    private void Direct(StatementSyntax statement, TokenRewriter rewriter)
    {
        if (layout.Of(statement, context.Expansion) is not { Direct: { } offset } laid
            || statement is not InstructionStatementSyntax { Operand: AbsoluteOperandSyntax { Prefix: { } sourcePrefix } operand })
        {
            return;
        }
        foreach (var token in sourcePrefix.ChildTokens)
            rewriter.Replacements[token.Position] = "";
        var tokens = TokenRewriter.Tokens(operand.Address);
        rewriter.Before.Remove(tokens[0].Position);
        rewriter.Replacements[tokens[0].Position] = (laid.Prefix ?? "") + Hex(offset, 2);
        for (var i = 1; i < tokens.Count; i++)
            rewriter.Replacements[tokens[i].Position] = "";
    }

    /// <summary>
    /// Writes the <c>.a8</c>, <c>.a16</c>, <c>.i8</c> or <c>.i16</c> that a 65816 immediate
    /// needs. ca65 sizes an immediate from the last such directive in the text, regardless of
    /// control flow. One therefore goes directly before each immediate whose width differs from
    /// the previous immediate's for the same register, and nowhere else. The width itself comes
    /// from the analysis, which follows control flow.
    /// </summary>
    private void WriteWidthDirective(StatementSyntax statement)
    {
        if (layout.Of(statement, context.Expansion) is not { Bits: { } bits }
            || statement is not InstructionStatementSyntax instruction
            || Instructions.SizedBy(instruction.MnemonicKind) is not { } register)
        {
            return;
        }
        if (widths.TryGetValue(register, out var previous) && previous == bits)
            return;
        widths[register] = bits;
        Segment();
        Flush();
        Line($"{Body}.{StateRegister.Of(register).WidthItem(bits)}");
    }

    /// <summary>
    /// Reports a line for which nothing can be written, which analysis should already have rejected.
    /// The error means the file produces no output rather than wrong output.
    /// </summary>
    private void NotTranspiled(SyntaxNode statement)
    {
        var tokens = TokenRewriter.Tokens(statement);
        var first = tokens.FirstOrDefault(token => token.Kind != SyntaxKind.EndOfLine);
        if (first.Parent is null)
            return;

        // In a file that already has errors, what cannot be written is almost always a
        // consequence of one of them, and reporting it again only buries the real mistake.
        if (diagnostics.Any(d => d.Severity == Severity.Error && d.Span.File == model.Tree.Path))
            return;
        diagnostics.Add(Expansion.Problem(
            model.Tree, first.Parent.Tree, first.Span, context.Expansion, null, Catalogue.CannotBeTranslated.Message(first.Text)));
    }

    /// <summary>
    /// Writes one line that came from the source, and records where it came from. A line that
    /// generates bytes is mapped, because ld65 attaches a span of bytes to the line in effect
    /// while they were generated. A line that is <paramref name="located"/> is mapped too. Such
    /// a line is an assertion ca65 evaluates, whose failure ca65 notes as generated from the line
    /// in effect. A label or a constant is not mapped, and neither are imports and exports. ld65
    /// reports problems with those against the output's own line regardless of the map, and
    /// mapping them would only add lines that cover no bytes.
    /// </summary>
    /// <param name="line">The line of the source the output line came from.</param>
    /// <param name="text">The output line's text.</param>
    /// <param name="bytes">The number of bytes the line assembles to.</param>
    /// <param name="comment">The comment nt65 adds about the line, if any.</param>
    /// <param name="located">Whether the line is mapped even though it holds no bytes.</param>
    /// <param name="kind">What the line is, for the passes that rewrite runs of lines.</param>
    /// <param name="value">
    /// The value of the one byte the line writes, which makes it a
    /// <see cref="EmittedLineKind.Byte"/> line, or null for any other line.
    /// </param>
    private void Code(
        LineSyntax line, string text, int bytes, string? comment = null, bool located = false,
        EmittedLineKind kind = EmittedLineKind.Other, string? value = null)
    {
        Segment();
        Flush();
        Write(new EmittedLine(
            text, bytes, Mapped(line, bytes, located), Comment: comment,
            Kind: value is null ? kind : EmittedLineKind.Byte, Value: value));
    }

    /// <summary>
    /// Writes one line that came from the source and declares a name, such as a label, and
    /// records where it came from as <see cref="Code"/> does.
    /// </summary>
    private void Declare(LineSyntax line, string text, int bytes = 0) =>
        Code(line, text, bytes, kind: EmittedLineKind.Declaration);

    /// <summary>
    /// Writes a named data line, which shares its line with the name at its margin. The two are kept
    /// apart until every line is written, because the column the directive goes in depends on
    /// the whole run of such lines, not on this one alone.
    /// </summary>
    private void WriteNamedData(LineSyntax line, string label, string text, int bytes, string? comment)
    {
        Segment();
        Flush();
        Write(new EmittedLine(text, bytes, Mapped(line, bytes, located: false), label, comment));
    }

    /// <summary>
    /// Returns the source line a generated line is mapped to, or 0 when the map should not point
    /// it at any source line.
    /// </summary>
    private int Mapped(LineSyntax line, int bytes, bool located) =>
        bytes != 0 || located ? At(line) : 0;

    /// <summary>
    /// Returns the line of this file a generated line came from, counting from one. The lines of an
    /// expansion came from the call, which is the line of this file that asked for all of them.
    /// </summary>
    private int At(LineSyntax line) => (context.CallLine ?? line).LineIndex + 1;

    /// <summary>
    /// Writes a definition that is in no segment, such as a constant or a name for an address
    /// given by other names. It is written where it stands, before any segment or in the segment
    /// that is open, so a program of constants needs no segment in its linker configuration.
    /// </summary>
    private void Definition(string text)
    {
        Flush();
        Line(text, EmittedLineKind.Declaration);
    }

    /// <summary>
    /// Writes the <c>.segment</c> directive when what follows goes in a different segment from
    /// the last one written.
    /// </summary>
    private void Segment()
    {
        var segment = context.Segment;
        if (writtenSegment == segment || segment is null)
            return;
        var size = model.Segments.Find(segment)?.Size ?? AddressSize.Absolute;
        if (writtenSegment is not null)
            pendingBlank = true;
        Flush();
        Line($".segment \"{segment}\": {size switch
        {
            AddressSize.ZeroPage => "zeropage",
            AddressSize.Far => "far",
            _ => "absolute",
        }}", EmittedLineKind.Segment);
        writtenSegment = segment;
    }

    /// <summary>
    /// Writes a blank line the source asked for, writing at most one no matter how many blank
    /// lines the source has.
    /// </summary>
    private void Blank()
    {
        pendingBlank = true;
        Flush();
    }

    /// <summary>
    /// Writes the blank line <see cref="Blank"/> asked for, if one is pending. A blank is never
    /// written as the first line of the output.
    /// </summary>
    private void Flush()
    {
        if (!pendingBlank)
            return;
        pendingBlank = false;
        if (lines.Count > 0)
            Line("");
    }

    /// <summary>
    /// Appends one line of output that the map should not point at any source line, such as a
    /// directive, a name, or a blank between them.
    /// </summary>
    private void Line(string text, EmittedLineKind kind = EmittedLineKind.Other) =>
        Write(new EmittedLine(text, Kind: kind));

    /// <summary>
    /// Appends a line to the output, without trailing spaces and tagged with this emitter's
    /// file.
    /// </summary>
    private void Write(EmittedLine line) => lines.Add(line with { Text = line.Text.TrimEnd(), File = file });

    /// <summary>
    /// Writes a <c>.place</c> as the module it places, in full, between a comment naming the
    /// module and the location of the <c>.place</c> and a comment closing it. Each of the placed
    /// module's lines belongs at this point in its own segment, which writing them here achieves.
    /// When the placed module leaves a different segment current, this file's segment is written
    /// again before its next line.
    /// </summary>
    private void Place(PlaceDirectiveSyntax directive)
    {
        if (!placing.TryGetValue(directive, out var placed) || Placements.PathOf(directive.Name) is not { } name)
            return;
        Blank();
        Line($"; .place {name}  {Where(directive)}");
        var opens = lines[^1];
        placed.writtenSegment = writtenSegment;
        placed.WalkContainer(placed.model.Tree.Root);
        LinePasses.AlignColumns(placed.lines);
        LinePasses.FoldFills(placed.lines);
        lines.AddRange(placed.lines);
        Line($"; end of {name}");
        parts.Add((placed.source, opens, lines[^1]));
        parts.AddRange(placed.parts);
        writtenSegment = placed.writtenSegment;
        pendingBlank = true;
    }

    /// <summary>
    /// Records the edits that make the output differ from the source. These are flat names, the
    /// address-size prefix the chosen mode needs, byte values for text, and the parentheses that
    /// keep the output from depending on ca65's precedence.
    /// </summary>
    private void Substitute(SyntaxNode node, TokenRewriter rewriter, bool nested)
    {
        switch (node)
        {
            case NameExpressionSyntax name:
                Name(name, rewriter);
                return;

            case LiteralExpressionSyntax literal when literal is StringExpressionSyntax or CharacterExpressionSyntax:
                Text(literal, rewriter);
                return;

            case CallExpressionSyntax call:
                Applied(call, rewriter);
                return;

            case BinaryExpressionSyntax binary when Evaluator.IsIn(binary.OperatorToken):
                Membership(binary, rewriter);
                return;

            // `wdm #n` is written as its bytes, which is what it is to every processor but the
            // emulator that hooks it.
            case InstructionStatementSyntax { Operand: ImmediateOperandSyntax hook } instruction
                when instruction.MnemonicKind == MnemonicKind.Wdm:
                rewriter.Replacements[instruction.Mnemonic.Position] = ".byte";
                rewriter.Replacements[hook.HashToken.Position] = "$42, ";
                break;

            case AbsoluteOperandSyntax operand:
                // In a macro body an `operand` parameter takes the place of a whole operand, so
                // the argument the call passed replaces the body's operand, including its prefix
                // and index. The prefix goes on last, outside any parentheses the expression was
                // given.
                if (Given(operand, rewriter))
                    return;
                foreach (var child in operand.ChildNodes)
                    Substitute(child, rewriter, nested: false);
                Prefix(operand, rewriter);
                return;

            case DataDirectiveSyntax directive:
                rewriter.TerminateStrz(directive);

                // An `.incbin` names a file rather than holding data, so its path is left a
                // path, pointed at the file from the output's location.
                if (Included(directive, rewriter))
                    return;

                // An element type's values, and a `.res` fill, are each written into a slot of a
                // fixed width.
                if (DataSyntax.IsElementType(directive) && directive.Tail is not BracedDataSyntax)
                {
                    rewriter.Replacements[directive.Directive.Position] = ForCa65(directive.Directive.DirectiveKind, directive.Directive.Text);
                    var (width, bigEndian) = ElementFormat(directive);
                    foreach (var value in DataLengths.ElementsOf(directive))
                        InPlace(value, width, bigEndian, rewriter);
                    return;
                }
                if (directive.Directive.DirectiveKind is DirectiveKind.Res or DirectiveKind.Align
                    && directive.Tail is InlineDataSyntax { Values: [var count, .. var fills] })
                {
                    ReplaceCount(count, rewriter);
                    foreach (var fill in fills)
                        InPlace(fill, 1, bigEndian: false, rewriter);
                    return;
                }
                break;

            case DataValuesSyntax values
                when DataSyntax.DirectiveOfValues(values) is { IsRecord: false } of:
                var (valueWidth, valuesBigEndian) = ElementFormat(of);
                foreach (var value in values.Values)
                    InPlace(value, valueWidth, valuesBigEndian, rewriter);
                return;

            // The distance between two places in one data declaration is a constant nt65 has
            // worked out, and a constant is written as its value, never as text for ca65 to
            // work out again.
            case BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Minus } difference
                when NamesAnAddress(difference) && Worth(difference).AsNumber() is { } distance:
                rewriter.Replace(difference, nested ? $"({Constant(distance)})" : Constant(distance));
                rewriter.Comments.Add(difference.GetText().Trim());
                return;

            case BinaryExpressionSyntax:
            case UnaryExpressionSyntax:
                foreach (var op in node.ChildTokens)
                {
                    if (TokenRewriter.Ca65Operator(op) is var spelled && spelled != op.Text)
                        rewriter.Replacements[op.Position] = spelled;
                }
                if (nested)
                {
                    var tokens = TokenRewriter.Tokens(node);
                    rewriter.Before[tokens[0].Position] = "(" + rewriter.Before.GetValueOrDefault(tokens[0].Position, "");
                    rewriter.After[tokens[^1].Position] = rewriter.After.GetValueOrDefault(tokens[^1].Position, "") + ")";
                }
                foreach (var child in node.ChildNodes)
                    Substitute(child, rewriter, nested: true);
                return;

            default:
                break;
        }

        foreach (var child in node.ChildNodes)
            Substitute(child, rewriter, nested: false);
    }

    /// <summary>
    /// Replaces a count that ca65 needs to know when it reaches the line, such as how much a
    /// <c>.res</c> reserves or what an <c>.align</c> aligns to. ca65 cannot know the count if a
    /// name in it is defined further down the output. A count that uses such a name is written
    /// as its value, with the source text in a comment beside it.
    /// </summary>
    private void ReplaceCount(SyntaxNode count, TokenRewriter rewriter)
    {
        var named = count.DescendantNodes().Prepend(count).OfType<NameExpressionSyntax>().Any(name =>
            name.Names is [.., var last] && model.SymbolAt(last) is { Kind: not (SymbolKind.Binding or SymbolKind.MacroParameter) });
        if (named && model.ValueOf(count, context.Expansion).AsNumber() is { } known)
        {
            rewriter.Comments.Add(count.GetText().Trim());
            rewriter.Replace(count, Constant(known));
            return;
        }
        Substitute(count, rewriter, nested: false);
    }

    /// <summary>
    /// Writes one value of a slot as <see cref="Datum"/> returns it when it returns anything, and
    /// as the source has it otherwise.
    /// </summary>
    private void InPlace(SyntaxNode value, int width, bool bigEndian, TokenRewriter rewriter)
    {
        if (Datum(value, width, bigEndian, rewriter.Comments) is { } text)
            rewriter.Replace(value, text);
        else
            Substitute(value, rewriter, nested: false);
    }

    /// <summary>
    /// Returns one value of a slot <paramref name="width"/> bytes wide where ca65 cannot take it
    /// as it stands. A negative constant is its two's complement, and a big-endian value wider
    /// than a word, which ca65 has no directive for, is its bytes, high first. Returns null for a
    /// value written as it stands. The source text of a rewritten value is added to
    /// <paramref name="comments"/>.
    /// </summary>
    private string? Datum(SyntaxNode value, int width, bool bigEndian, List<string> comments)
    {
        var known = model.ValueOf(value, context.Expansion).AsNumber();
        if (bigEndian && width > 2)
        {
            if (model.BytesOf(value, context.Expansion) is { Count: > 0 } text)
            {
                comments.Add(value.GetText().Trim());
                return string.Join(", ", text.SelectMany(b => HighFirst(b, width)));
            }
            if (known is { } number)
            {
                comments.Add(value.GetText().Trim());
                return string.Join(", ", HighFirst(number, width));
            }
            var rendered = Rendered(value, comments);
            var low = $".bankbyte({rendered}), .hibyte({rendered}), .lobyte({rendered})";
            return width == 3 ? low : $".lobyte(({rendered}) >> 24), {low}";
        }
        if (known is not (< 0 and var negative))
            return null;
        comments.Add(value.GetText().Trim());
        return Hex(negative & (long)(ulong.MaxValue >> (64 - (8 * width))), 2 * width);

        static IEnumerable<string> HighFirst(long number, int width) =>
            Enumerable.Range(0, width).Select(i => Hex((number >> (8 * (width - 1 - i))) & 0xff, 2));
    }

    /// <summary>
    /// Rewrites the path of an <c>.incbin</c> so that ca65 finds the file from the output rather
    /// than from the source. ca65 looks beside the file it is assembling, so the output assembles
    /// from any directory. Returns whether the directive was an <c>.incbin</c>.
    /// </summary>
    private bool Included(DataDirectiveSyntax directive, TokenRewriter rewriter)
    {
        if (directive.Directive.DirectiveKind != DirectiveKind.IncBin)
            return false;
        var values = directive.Tail is InlineDataSyntax inline ? inline.Values : default;
        if (values is [var path, ..]
            && model.ValueOf(path, context.Expansion) is { Kind: ValueKind.String, Text: { } named })
        {
            rewriter.Replace(path, "\"" + Paths.Relative(Paths.Directory(output), Paths.Beside(source, named)) + "\"");
        }
        foreach (var argument in values.Skip(1))
            Substitute(argument, rewriter, nested: false);
        return true;
    }

    /// <summary>
    /// Rewrites a name as ca65 needs it. Most names become the flat name of the symbol they
    /// refer to. A list becomes its items, a member path its offset, a macro parameter the
    /// argument given for it, a string its bytes, and a define or a checked import its value.
    /// </summary>
    private void Name(NameExpressionSyntax name, TokenRewriter rewriter)
    {
        // A path names one symbol, and the whole of it becomes that symbol's flat name. A body is
        // written out in every file that calls its macro, so the symbol a name refers to comes
        // from resolving the whole program rather than this one file.
        if (name.Names is not [.., var last] || model.SymbolAt(last) is not { } named)
            return;
        var reference = named;

        // A path that ends in a repetition's binding names a different member on every iteration.
        if (named.Kind == SymbolKind.Binding && name.SimpleName is null)
        {
            if (model.SymbolOf(name, context.Expansion) is not { } namesake)
                return;
            reference = namesake;
        }

        // A list's name is replaced by its items wherever data uses it.
        if (model.ItemsOf(name) is { Count: > 0 } items)
        {
            rewriter.ReplaceName(name, string.Join(", ", items.Select(item => Rendered(item, rewriter.Comments))));
            rewriter.Comments.Add(name.GetText().Trim());
            return;
        }

        // A member is an offset. The offsets along the path are added up, on the address the
        // path starts from when it starts at an instance rather than at a type. An index along
        // the path adds whole elements to the same sum.
        if (reference.Kind == SymbolKind.Member || (name.IsIndexed && reference.IsAddress))
        {
            MemberPath(name, rewriter);
            return;
        }

        // A macro parameter is replaced by the argument the call passed it, as a parenthesized
        // whole, so `value * 2` with the argument `1 + 2` is 6 rather than 5.
        var symbol = reference;
        if (symbol.Kind == SymbolKind.MacroParameter)
        {
            if (Parameter(symbol, rewriter.Comments) is not { } given)
                return;
            rewriter.ReplaceName(name, given);
            return;
        }

        // The name a repetition binds has a different value on every iteration, and the one
        // written is its value on the iteration being written.
        if (symbol.Kind == SymbolKind.Binding)
        {
            if (model.BindingsOf(context.Expansion)?.TryGetValue(symbol, out var bound) is not true)
                return;

            // A list item is written as it stands, with its own names substituted; a number
            // is written as its value on this iteration.
            var replacement = bound.Item is { } item ? Rendered(item, rewriter.Comments) : null;
            if (replacement is null && bound.Value.AsNumber() is { } number)
                replacement = Constant(number);
            if (replacement is null)
                return;

            rewriter.ReplaceName(name, replacement);

            // The binding's value is written into the line, so naming the binding in a comment
            // as well would only repeat it down every line an unrolled body writes.
            return;
        }

        // A string constant cannot be expressed in ca65, in this module or any other, so it is
        // written as the bytes of its text, as a literal is.
        if (symbol.Value.IsString)
        {
            if (model.BytesOf(name, context.Expansion) is { Count: > 0 } bytes)
            {
                rewriter.Replace(name, string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2))));
                rewriter.Comments.Add(name.GetText().Trim());
            }
            return;
        }

        // A define and a checked import are written as their value, never by name. That way a
        // `-D` given to ca65 cannot collide with a define, and a checked import is a value nt65
        // has already used in its own arithmetic.
        var byValue = (symbol.IsDefine || symbol.IsConfig || symbol.Kind == SymbolKind.ImportedConstant)
            && symbol.Value.AsNumber() is not null;
        rewriter.ReplaceName(name, byValue ? Constant(symbol.Value.Number) : NameOf(symbol));
        if (byValue)
            rewriter.Comments.Add(symbol.QualifiedName);
    }

    /// <summary>
    /// Returns the text a macro parameter is replaced by here, which is the operand the call
    /// passed, the expression it passed, or the word or number the parameter is bound to.
    /// </summary>
    private string? Parameter(Symbol parameter, List<string> comments)
    {
        if (model.BindingsOf(context.Expansion)?.TryGetValue(parameter, out var bound) is not true)
            return null;
        if (bound.Value.IsWord)
            return bound.Value.Text;
        if (bound.Argument is { Parameter.Kind: ParameterKind.Operand } given)
            return given.Operand is { } operand ? Substituted(operand, comments) : null;

        // A member of an enum is a constant, and constants are written as their values.
        if (bound is { Member: not null, Item: null } && bound.Value.AsNumber() is { } member)
            return Constant(member);
        return bound.Item is { } item ? Substituted(item, comments) : null;
    }

    /// <summary>
    /// Replaces a path through a type or an instance, including the elements any <c>[i]</c>
    /// along it steps over. Through a type it is a number, and through an instance it is that
    /// instance plus the offset, which ca65 and ld65 resolve. Either way the path it came from is kept
    /// in a comment.
    /// </summary>
    private void MemberPath(NameExpressionSyntax name, TokenRewriter rewriter)
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
                && model.ValueOf(index.Index, context.Expansion).AsNumber() is { } element)
            {
                offset += element * stride;
            }
        }

        rewriter.Replace(name, start is null
            ? Constant(offset)
            : offset == 0 ? NameOf(start) : $"{NameOf(start)}+{offset}");
        rewriter.Comments.Add(name.GetText().Trim());
    }

    /// <summary>
    /// Returns the value of <paramref name="node"/> in the expansion being written, with cycle
    /// counts from the layout.
    /// </summary>
    private Value Worth(SyntaxNode node) => model.ValueOf(node, context.Expansion, cycles: layout.CyclesOf);

    /// <summary>Returns whether an expression names an address anywhere along any of its paths.</summary>
    private bool NamesAnAddress(SyntaxNode node) =>
        node.DescendantNodes().OfType<NameExpressionSyntax>()
            .Any(name => name.Names.Any(part => model.SymbolAt(part) is { IsAddress: true }));

    /// <summary>
    /// Writes a call out as what it evaluates to. A charmap applied to text becomes the bytes it
    /// maps the text to, and a function call becomes its value. A call nt65 cannot evaluate is
    /// reported rather than passed to ca65, which knows neither charmaps nor functions.
    /// </summary>
    private void Applied(CallExpressionSyntax call, TokenRewriter rewriter)
    {
        var tokens = TokenRewriter.Tokens(call);
        if (tokens.Count == 0)
            return;

        // A segment function such as `.loadof` is written as the symbol ld65 defines for that segment.
        if (SegmentFunctions.Of(call, model) is { } about)
        {
            rewriter.Replace(call, SegmentFunctions.LinkerName(about.Function, about.Segment));
            return;
        }

        // `.endof(f)` and `.spanof(f)` describe layout rather than shape, so they are written
        // as the addresses they are and resolved by ca65 and ld65.
        if (Extents.Is(call, model, out var span) && Extents.MeasuredBy(call) is { } named
            && model.SymbolOf(named) is { } measured && (ends.Contains(measured) || measured.Tree != model.Tree))
        {
            rewriter.Replace(call, span ? $"({EndLabelOf(measured)} - {NameOf(measured)})" : EndLabelOf(measured));
            return;
        }

        // `.exprof(p)` is replaced by the expression inside the operand the call passed as `p`
        // (`5` for `{#5}`, `ptr` for `{(ptr),y}`), unless it is a constant, which the path
        // below writes as a number.
        if (Semantics.Operands.IsExprOf(call) && Worth(call).AsNumber() is null
            && model.ExprOf(call, context.Expansion) is { } inner)
        {
            rewriter.Replace(call, "(" + Substituted(inner, rewriter.Comments) + ")");
            return;
        }

        // A call to a built-in, which has no declared callee, is written from what the
        // analysis works out for it.
        if (call.Callee is null)
        {
            // Text a built-in builds or chooses is written as its bytes, as a literal is, because
            // ca65 never sees text.
            if (Worth(call).IsString)
            {
                if (model.BytesOf(call, context.Expansion) is { } built)
                {
                    rewriter.Replace(call, BytesText(built));
                    rewriter.Comments.Add(call.GetText().Trim());
                }
                else
                {
                    NotTranspiled(call);
                }
                return;
            }

            // An address `.select` or `.switch` chooses is written as the value it chose, which is
            // all ca65 sees.
            if (Worth(call).AsNumber() is null && model.ChosenBy(call, context.Expansion) is { } choice)
            {
                // A parenthesis first in an operand would read as indirection, which a unary `+` prevents.
                var chosen = Rendered(choice, rewriter.Comments);
                rewriter.Replace(call, chosen.StartsWith('(') ? "+" + chosen : chosen);
                return;
            }
            if (Worth(call).AsNumber() is { } builtin)
            {
                rewriter.Replace(call, Constant(builtin));
                return;
            }
            Substitute(call.Arguments, rewriter, nested: false);
            return;
        }

        string? text = null;
        if (model.BytesOf(call, context.Expansion) is { } bytes && (bytes.Count > 0 || Worth(call).IsString))
            text = BytesText(bytes);
        else if (model.ValueOf(call, context.Expansion).AsNumber() is { } value)
            text = Constant(value);

        if (text is null)
        {
            NotTranspiled(call);
            return;
        }
        rewriter.Replace(call, text);
        rewriter.Comments.Add(call.GetText().Trim());
    }

    /// <summary>Replaces text with its byte values, keeping the source spelling in a comment.</summary>
    private void Text(LiteralExpressionSyntax literal, TokenRewriter rewriter)
    {
        if (DataLengths.Bytes(literal, model) is not { Count: > 0 } bytes)
            return;
        rewriter.Replacements[literal.Token.Position] =
            string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2)));
        rewriter.Comments.Add(literal.GetText());
    }

    /// <summary>
    /// Writes an operand that names an <c>operand</c> parameter as the operand the call passed.
    /// Returns whether the operand named such a parameter.
    /// </summary>
    private bool Given(AbsoluteOperandSyntax operand, TokenRewriter rewriter)
    {
        if (Semantics.Operands.Substituted(model, operand, context.Expansion) is not { } given)
            return false;

        var text = Argument(given, rewriter.Comments);
        if (text is null)
            return false;

        // ca65 reads a `(` at the head of an operand as indirect addressing, so an expression
        // that starts with one gets a unary `+`, which changes nothing about what it is worth.
        var prefix = operand.Parent is { } instruction ? layout.Of(instruction, context.Expansion)?.Prefix ?? "" : "";
        if (text.StartsWith('(') && (prefix.Length > 0 || given.IsAddress))
            text = "+" + text;

        var tokens = TokenRewriter.Tokens(operand);
        rewriter.Replacements[tokens[0].Position] = prefix + text;
        for (var i = 1; i < tokens.Count; i++)
            rewriter.Replacements[tokens[i].Position] = "";
        return true;
    }

    /// <summary>
    /// Returns the text of the operand a call passed, in the form the body asked for. That is the
    /// operand as it stands, the byte after it, or one byte of an immediate value.
    /// </summary>
    private string? Argument(OperandSubstitution given, List<string> comments)
    {
        // `.byteof` on an immediate is a byte of the value. It is a number where nt65 knows the
        // value, and a shift and a mask where only the linker will.
        if (given.ByteOf && given.Operand is ImmediateOperandSyntax)
        {
            if (given.Expression is not { } value)
                return null;
            if (model.ValueOf(value, context.Expansion).AsNumber() is { } known)
                return "#" + Constant((known >> (int)(8 * given.Offset)) & 0xff);
            var shifted = Substituted(value, comments);
            return given.Offset == 0
                ? $"#({shifted} & $ff)"
                : $"#(({shifted} >> {8 * given.Offset}) & $ff)";
        }

        // Anything else is the argument's own operand, as it stands when the body named it
        // whole, and with `+ n` on its expression when the body asked for a later byte.
        var offset = given.Offset;
        if (offset == 0)
            return Substituted(given.Operand, comments);
        if (given.Expression is not { } addressed)
            return null;
        var index = given.Index is { } register ? "," + register.Text : "";
        var address = Substituted(addressed, comments);
        return offset > 0 ? $"{address}+{offset}{index}" : $"{address}{offset}{index}";
    }

    /// <summary>Writes the <c>z:</c> or <c>a:</c> that shows which mode was chosen.</summary>
    private void Prefix(AbsoluteOperandSyntax operand, TokenRewriter rewriter)
    {
        var instruction = operand.Parent;
        if (instruction is null)
            return;
        var chosen = layout.Of(instruction, context.Expansion)?.Prefix;
        var prefix = chosen ?? "";
        var sourcePrefix = operand.Prefix;
        if (chosen is null && sourcePrefix is not null)
            return;
        var tokens = TokenRewriter.Tokens(operand.Address);

        // ca65 reads a `(` at the head of an operand, or straight after a prefix, as indirect
        // addressing. An expression that starts with one therefore gets a unary `+` in front of
        // it. Examples are `lda (hi + lo) * 2`, which the language allows,
        // `jml (bank << 16) | .loword(f)`, and an expression written out with parentheses around
        // its first operation. The `+` changes nothing and keeps the operand an expression.
        var opens = tokens is [{ Kind: SyntaxKind.OpenParen }, ..]
            || (tokens.Count > 0 && rewriter.Before.GetValueOrDefault(tokens[0].Position, "").StartsWith('('));
        var text = opens ? prefix + "+" : prefix;

        if (sourcePrefix is not null)
        {
            // The source already has a prefix, so it is replaced with the one chosen, in case an
            // expression made the operand wider.
            foreach (var token in sourcePrefix.ChildTokens)
                rewriter.Replacements[token.Position] = "";
            rewriter.Replacements[sourcePrefix.Name.Position] = text;
            return;
        }
        if (tokens.Count > 0)
            rewriter.Before[tokens[0].Position] = text + rewriter.Before.GetValueOrDefault(tokens[0].Position, "");
    }

    /// <summary>
    /// Dispatches each line's statement to the emitter method for its kind. The emitter does
    /// the work; this only chooses which of its methods each kind of statement calls.
    /// <para>
    /// A kind with no method here writes nothing. A function, a signature set, an enum member,
    /// a charmap entry, a list's items and a member's value exist for the analysis. A call is
    /// written as its body, and a type as the constants it names. An <c>.error</c> the build
    /// reached has already been reported, and never reaches ca65. A <c>.cpu</c> item, a segment
    /// declaration, an <c>.export</c> or <c>.import</c> (both already written) and a closing
    /// brace all describe the file without generating anything.
    /// </para>
    /// </summary>
    /// <param name="emitter">The emitter whose file is being written.</param>
    private sealed class Statements(Emitter emitter) : SyntaxVisitor
    {
        // The line whose statement is being written. A macro call writes a body whose lines are
        // walked from inside its own method, so the line is put back when that returns.
        private LineSyntax? walked;

        /// <summary>Gets the line being written, which every method below is about.</summary>
        private LineSyntax Line => walked!;

        /// <summary>Writes what <paramref name="line"/> generates.</summary>
        /// <param name="line">The line.</param>
        public void Walk(LineSyntax line)
        {
            var outer = walked;
            walked = line;
            try
            {
                Visit(line.Statement);
            }
            finally
            {
                walked = outer;
            }
        }

        /// <inheritdoc/>
        public override void VisitBlankLine(BlankLineSyntax node) => emitter.pendingBlank = true;

        /// <inheritdoc/>
        public override void VisitLabeledLine(LabeledLineSyntax node) => emitter.LabeledLine(Line, node);

        /// <inheritdoc/>
        public override void VisitConstantDeclaration(ConstantDeclarationSyntax node) =>
            emitter.WriteConstant(Line, node);

        /// <summary>
        /// Writes a named data declaration. Its label is written at its first byte, and its end
        /// label just past its last byte when anything measures it.
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
            else if (emitter.layout.Of(node, emitter.context.Expansion) is null)
                emitter.NotTranspiled(node);
            else
                emitter.Source(Line, node, emitter.layout.Of(node, emitter.context.Expansion)?.Length ?? 0);
        }

        /// <inheritdoc/>
        public override void VisitDataValues(DataValuesSyntax node) => emitter.Values(Line, node);

        /// <inheritdoc/>
        public override void VisitInstructionStatement(InstructionStatementSyntax node)
        {
            if (SyntaxFacts.IsLongBranch(node.MnemonicKind)
                && emitter.layout.Of(node, emitter.context.Expansion) is { } laid)
            {
                emitter.Branch(Line, node, laid);
            }
            else
            {
                emitter.Source(Line, node, emitter.layout.Of(node, emitter.context.Expansion)?.Length ?? 0);
            }
        }

        /// <inheritdoc/>
        public override void VisitExternProcDeclaration(ExternProcDeclarationSyntax node) =>
            emitter.ExternProc(node);

        /// <summary>
        /// Writes an assertion that nt65 could not evaluate. An assertion nt65 could evaluate has
        /// already been checked and writes nothing. An assertion it could not evaluate depends on
        /// where things land, so it is written out for ld65 to check, with the level ca65 needs
        /// for that.
        /// </summary>
        /// <param name="node">The assertion.</param>
        public override void VisitAssertDirective(AssertDirectiveSyntax node)
        {
            if (emitter.model.ValueOf(
                node.Condition, emitter.context.Expansion, emitter.layout.SpanOf, emitter.layout.CyclesOf).AsNumber() is null)
                emitter.WriteLinkerAssertion(Line, node);
        }

        /// <inheritdoc/>
        public override void VisitMacroCall(MacroCallSyntax node) => emitter.Expand(Line, node);

        /// <inheritdoc/>
        public override void VisitEnsureDirective(EnsureDirectiveSyntax node) => emitter.Ensure(Line, node);

        /// <inheritdoc/>
        public override void VisitBlockSplice(BlockSpliceSyntax node) => emitter.Splice(node);

        /// <inheritdoc/>
        public override void VisitPlaceDirective(PlaceDirectiveSyntax node) => emitter.Place(node);
    }

    /// <summary>
    /// Gives a <see cref="RecordWriter"/> the few things it needs from an emitter, and nothing
    /// else of the emitter's state.
    /// </summary>
    /// <param name="emitter">The emitter whose file is being written.</param>
    private sealed class RecordOutput(Emitter emitter) : IRecordOutput
    {
        /// <inheritdoc/>
        public void Code(LineSyntax line, string text, int bytes, string? comment, string? value) =>
            emitter.Code(line, text, bytes, comment, value: value);

        /// <inheritdoc/>
        public void Label(LineSyntax line, Symbol symbol) =>
            emitter.Declare(line, LabelText(emitter.NameOf(symbol)));

        /// <inheritdoc/>
        public void Named(LineSyntax line, Symbol? symbol, string text, int bytes, string? comment) =>
            emitter.WithName(line, symbol, text, bytes, comment);

        /// <inheritdoc/>
        public void NotTranspiled(SyntaxNode node) => emitter.NotTranspiled(node);

        /// <summary>
        /// Returns one value of a slot as <see cref="Datum"/> returns it when it returns anything,
        /// and as <see cref="Rendered"/> writes it otherwise. Any comment either produces is
        /// dropped.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="width">The width of the slot, in bytes.</param>
        /// <param name="bigEndian">Whether the slot's bytes are written high first.</param>
        /// <returns>The value as the output writes it.</returns>
        public string ValueText(SyntaxNode value, int width, bool bigEndian) =>
            emitter.Datum(value, width, bigEndian, []) ?? emitter.Rendered(value);
    }

    /// <summary>
    /// Represents where the walk is in the source, which each block, repetition and macro call
    /// it enters changes for as long as it is inside.
    /// </summary>
    /// <param name="Segment">The segment being written, or null before any region or block names one.</param>
    /// <param name="Routine">The routine being written, which is what a generated label is named after.</param>
    /// <param name="Expansion">
    /// The expansion being written, which identifies the iteration of each enclosing repetition
    /// and the expansion of each enclosing macro. A body is written once per expansion or
    /// iteration, with every name bound at that level taking the value it has there.
    /// </param>
    /// <param name="CallLine">
    /// The line an expansion's output maps back to. The lines of an expansion map to the line of
    /// the call, the way a C debugger treats a preprocessor macro. Only a line of this file can be
    /// used, because the line map points into this module's source and a macro body may be
    /// defined in another file.
    /// </param>
    /// <param name="Depth">
    /// How many blocks enclose the line being written. A segment block inside another block
    /// switches segments with <c>.pushseg</c> and <c>.popseg</c>.
    /// </param>
    private readonly record struct WalkContext(
        string? Segment, Symbol? Routine, Expansion? Expansion, LineSyntax? CallLine, int Depth);

    /// <summary>Puts an emitter's earlier <see cref="WalkContext"/> back when it is disposed.</summary>
    /// <param name="emitter">The emitter whose context to put back.</param>
    /// <param name="outer">The context to put back.</param>
    private readonly struct WalkScope(Emitter emitter, WalkContext outer) : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose() => emitter.context = outer;
    }
}
