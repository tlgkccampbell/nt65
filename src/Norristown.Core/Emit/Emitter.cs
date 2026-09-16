using System.Globalization;
using System.Text;
using Norristown.Layout;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Emit;

/// <summary>
/// Writes one file's ca65 (§13). The output is readable, with the source's own spacing kept
/// and its comments dropped, and it is deterministic: the same source always gives the same
/// bytes.
/// <para>
/// Everything the output depends on is written into it. The header fixes the CPU and
/// switches off every ca65 option that changes syntax; every segment carries its address
/// size; every addressing mode that could be read two ways carries its prefix; and character
/// and string data is written as bytes, so no ca65 command line can change what it means.
/// </para>
/// </summary>
public sealed class Emitter
{
    /// <summary>Where a generated comment starts, so that a column of them lines up.</summary>
    private const int CommentColumn = 36;

    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly FlatNames names;
    private readonly List<Diagnostic> diagnostics;
    private readonly string source;
    private readonly StringBuilder output = new();
    private readonly List<int> lineBytes = [];
    private readonly List<string> segmentStack = [];
    private readonly HashSet<Symbol> exported = [];
    private string segment = SegmentTable.DefaultSegment;
    private string? written;
    private bool pendingBlank;
    private int depth;

    private Emitter(SemanticModel model, CodeLayout layout, FlatNames names, List<Diagnostic> diagnostics, string source)
    {
        this.model = model;
        this.layout = layout;
        this.names = names;
        this.diagnostics = diagnostics;
        this.source = source;
    }

    /// <summary>
    /// The ca65 for <paramref name="model"/>'s file. <paramref name="outRoot"/> is the
    /// project's output tree, or null to write beside the source (§5.3).
    /// </summary>
    public static OutputFile Emit(
        SemanticModel model, CodeLayout layout, FlatNames names, List<Diagnostic> diagnostics,
        string? outRoot = null)
    {
        var path = OutputPath(model.Tree.Path, outRoot);
        var emitter = new Emitter(model, layout, names, diagnostics, Relative(Directory(path), model.Tree.Path));
        emitter.Header();
        emitter.Exports();
        emitter.Imports();
        emitter.WalkContainer(model.Tree.Root);
        return new OutputFile(path, emitter.output.ToString(), emitter.lineBytes);
    }

    /// <summary>
    /// One <c>foo.nt65</c> produces one <c>foo.s</c> (§13), beside it or under the project's
    /// output tree, which mirrors the source tree (§5.3).
    /// </summary>
    public static string OutputPath(string source, string? outRoot = null)
    {
        var path = source.EndsWith(".nt65", StringComparison.OrdinalIgnoreCase)
            ? source[..^5] + ".s"
            : source + ".s";
        return string.IsNullOrEmpty(outRoot) ? path : $"{outRoot.TrimEnd('/')}/{path}";
    }

    /// <summary>The directory part of a logical path, without its trailing separator.</summary>
    private static string Directory(string path)
    {
        var at = path.LastIndexOf('/');
        return at < 0 ? "" : path[..at];
    }

    /// <summary>
    /// <paramref name="path"/> as it is reached from <paramref name="directory"/>. Debug
    /// information names the source relative to the output file (§13), so that a debugger
    /// finds the <c>.nt65</c> wherever the tree is checked out.
    /// </summary>
    private static string Relative(string directory, string path)
    {
        var from = directory.Length == 0 ? [] : directory.Split('/');
        var to = path.Split('/');
        var shared = 0;
        while (shared < from.Length && shared < to.Length - 1 && from[shared] == to[shared])
            shared++;
        return string.Join('/', Enumerable.Repeat("..", from.Length - shared).Concat(to[shared..]));
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

    /// <summary>The first and last tokens under a node, in source order.</summary>
    private static List<SyntaxToken> Tokens(SyntaxNode node)
    {
        var tokens = new List<SyntaxToken>();
        Collect(node, tokens);
        return tokens;

        static void Collect(SyntaxNode node, List<SyntaxToken> into)
        {
            var nodes = node.ChildNodes;
            var tokens = node.ChildTokens;
            int i = 0, j = 0;
            while (i < nodes.Length || j < tokens.Length)
            {
                if (j >= tokens.Length || (i < nodes.Length && nodes[i].Position < tokens[j].Position))
                    Collect(nodes[i++], into);
                else
                    into.Add(tokens[j++]);
            }
        }
    }

    /// <summary>
    /// The header of §13: the CPU, smart mode off, case-sensitive symbols, and every
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

        // The size lets ld65 check that the source has not changed under the debug file; the
        // timestamp is zero, so the output does not depend on when the file was written.
        Line($".dbg file, \"{source}\", {Encoding.UTF8.GetByteCount(model.Tree.Text)}, 0");
    }

    /// <summary>
    /// Every export, with the address size nt65 gives it (§12), hoisted to the top of the
    /// file as §13's example writes them.
    /// </summary>
    private void Exports()
    {
        var any = false;
        foreach (var node in model.Tree.Root.DescendantNodes())
        {
            if (node.Kind != SyntaxKind.ExportDirective)
                continue;
            foreach (var token in node.ChildTokens)
            {
                if (token.Kind != SyntaxKind.Identifier
                    || model.ReferenceAt(token.Span.Start) is not { } reference)
                {
                    continue;
                }
                if (!any)
                    Blank();
                any = true;
                exported.Add(reference.Symbol);

                // A type is not a symbol to the linker: what crosses is each of its members,
                // as the flat constant it becomes.
                if (reference.Symbol.Kind is SymbolKind.Enum or SymbolKind.Struct or SymbolKind.Union)
                {
                    foreach (var member in reference.Symbol.Body?.Symbols ?? [])
                    {
                        exported.Add(member);
                        Line(member.Value.AsNumber() is >= 0 and < 0x100
                            ? $".exportzp {names.Of(member)}"
                            : $".export {names.Of(member)}");
                    }
                    continue;
                }

                var name = names.Of(reference.Symbol);
                Line(reference.Symbol.AddressSize switch
                {
                    AddressSize.ZeroPage => $".exportzp {name}",
                    AddressSize.Far => $".export {name}: far",
                    _ => $".export {name}",
                });
            }
        }
        if (any)
            pendingBlank = true;
    }

    /// <summary>
    /// Everything the file gets from outside it (§12): what another nt65 file exports and
    /// this one names, and what an <c>.import</c> item declares. Each carries the address
    /// size nt65 gives it, so ca65 sizes an operand the way nt65 did.
    /// <para>
    /// A constant is not imported: ca65 cannot use an imported symbol where it needs a value,
    /// so the constant is written out here instead. A checked import is both — the value nt65
    /// uses, and an assertion that the definition it will be linked against agrees.
    /// </para>
    /// </summary>
    private void Imports()
    {
        var any = false;
        foreach (var symbol in model.Symbols
            .Where(symbol => symbol.Kind is SymbolKind.ImportedAddress or SymbolKind.ImportedConstant)
            .Concat(model.ExternalSymbols))
        {
            if (Import(symbol) is not { } line)
                continue;
            if (!any)
                Blank();
            any = true;
            Line(line);
            if (symbol.Kind == SymbolKind.ImportedConstant && symbol.Value.AsNumber() is { } checkedValue)
            {
                Line($".assert {names.Of(symbol)} = {Constant(checkedValue)}, lderror, "
                    + $"\"{names.Of(symbol)} is not {Constant(checkedValue)}, which is what "
                    + $"{source} was built against\"");
            }
        }
        if (any)
            pendingBlank = true;
    }

    /// <summary>The line that brings one symbol in, or null for one that needs no line at all.</summary>
    private string? Import(Symbol symbol)
    {
        var name = names.Of(symbol);
        if (symbol.IsAddress || symbol.Kind == SymbolKind.ImportedConstant)
        {
            return symbol.AddressSize switch
            {
                AddressSize.ZeroPage => $".importzp {name}",
                AddressSize.Far => $".import {name}: far",
                _ => $".import {name}",
            };
        }

        // A constant another file declares, written out by value (§12). One whose value nt65
        // does not know has already been reported, and a string is only ever used through
        // `.strlen` and `.strat`, which are numbers before anything is written.
        return symbol.Value.AsNumber() is { } value ? $"{name} = {Constant(value)}" : null;
    }

    private void WalkContainer(SyntaxNode container)
    {
        foreach (var child in container.ChildNodes)
        {
            if (child.Green is GreenBlock block)
                WalkBlock(child, block.BlockKind);
            else
                WalkLine(child);
        }
    }

    private void WalkBlock(SyntaxNode block, BlockKind kind)
    {
        var lines = block.ChildNodes;
        var opener = lines.Length > 0 ? lines[0].Statement : null;
        if (Constructs.IsDeferred(kind))
        {
            if (opener is not null)
                NotTranspiled(opener);
            return;
        }

        // A structure, a union, a list and a character mapping say what something means
        // without generating anything. An exported layout is the exception: its members
        // travel as flat constants, so the file that declares them has to define them.
        if (kind is BlockKind.List or BlockKind.Charmap)
            return;
        if (kind is BlockKind.Struct or BlockKind.Union)
        {
            Offsets(lines[0], opener);
            return;
        }

        // An enum writes its members out as the constants they are.
        if (kind == BlockKind.Enum)
        {
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Statement is { Kind: SyntaxKind.EnumMember } member)
                    EnumMember(lines[i], member);
            }
            pendingBlank = true;
            return;
        }

        if (kind == BlockKind.TagInitializer)
        {
            Initialized(lines[0], opener, [.. lines.Skip(1).Select(line => line.Statement).OfType<SyntaxNode>()]);
            return;
        }

        // A segment block anywhere but the file's own top level is a detour from the stream
        // around it, which is what `.pushseg` and `.popseg` say (§5.2, §13).
        var nested = depth > 0;
        var pushed = false;
        if (kind == BlockKind.Segment && opener is not null)
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
        else if (kind == BlockKind.Proc && opener is { Kind: SyntaxKind.ProcDeclaration })
        {
            ProcLabel(lines[0], opener);
        }
        else if (opener is not null && kind != BlockKind.Scope)
        {
            WalkLine(lines[0]);
        }

        depth++;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Green is GreenBlock inner)
                WalkBlock(lines[i], inner.BlockKind);
            else
                WalkLine(lines[i]);
        }
        depth--;

        if (pushed)
        {
            Line(".popseg");
            segment = segmentStack[^1];
            segmentStack.RemoveAt(segmentStack.Count - 1);
            written = segment;
            pendingBlank = true;
        }
        else if (kind == BlockKind.Segment)
        {
            segment = SegmentTable.DefaultSegment;
        }
    }

    private void WalkLine(SyntaxNode line)
    {
        if (line.Statement is not { } statement)
            return;

        switch (statement.Kind)
        {
            case SyntaxKind.BlankLine:
                pendingBlank = true;
                break;

            case SyntaxKind.LabeledLine:
                LabeledLine(line, statement);
                break;

            case SyntaxKind.ConstantDeclaration:
                Constant(line, statement);
                break;

            case SyntaxKind.DataDirective when Constructs.IsTag(statement):
                if (HasValues(statement))
                    Initialized(line, statement, []);
                else
                    Reserved(line, statement, statement);
                break;

            case SyntaxKind.DataDirective when layout.Of(statement) is null:
                NotTranspiled(statement);
                break;

            case SyntaxKind.InstructionStatement:
            case SyntaxKind.DataDirective:
                Source(line, statement, layout.Of(statement)?.Length ?? 0);
                break;

            case SyntaxKind.ExternProcDeclaration:
                ExternProc(line, statement);
                break;

            // The types, text and data of Stage 7 are read and bound, but nothing is written
            // for them yet, so a file that uses one is refused rather than written out short.
            // A function, and the openers of the blocks above, exist for the analysis: a
            // call is written as its body, and a type as the constants it names.
            case SyntaxKind.FuncDeclaration:
            case SyntaxKind.EnumMember:
            case SyntaxKind.CharmapEntry:
            case SyntaxKind.ListItems:
            case SyntaxKind.TagValue:
                break;

            case SyntaxKind.UnsupportedLine:
                NotTranspiled(statement);
                break;

            // A `.cpu` item, a segment declaration, an `.export` or `.import` (both already
            // written) and a closing brace all say something about the file without
            // generating anything.
            default:
                break;
        }
    }

    /// <summary>A <c>.proc</c> becomes its label; the signature says nothing to ca65 (§13).</summary>
    private void ProcLabel(SyntaxNode line, SyntaxNode opener)
    {
        foreach (var token in opener.ChildTokens)
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic
                && model.ReferenceAt(token.Span.Start) is { } reference)
            {
                Code(line, Indent(opener) + LabelText(names.Of(reference.Symbol)), 0);
                return;
            }
        }
    }

    private void LabeledLine(SyntaxNode line, SyntaxNode statement)
    {
        var label = statement.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.Label);
        var rest = statement.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.Label);
        if (rest is { Kind: SyntaxKind.DataDirective } && Constructs.IsTag(rest))
        {
            if (HasValues(rest))
            {
                Initialized(line, statement, []);
                return;
            }
            LabelOnly(line, statement, label);
            Reserved(line, statement, rest);
            return;
        }
        if (rest is { Kind: SyntaxKind.DataDirective } && layout.Of(rest) is null)
        {
            NotTranspiled(rest);
            return;
        }
        var bytes = rest is null ? 0 : layout.Of(rest)?.Length ?? 0;
        var edits = new Edits();
        if (rest is not null)
            Substitute(rest, edits, nested: false);

        if (label is not { ChildTokens.Length: > 0 }
            || model.ReferenceAt(label.ChildTokens[0].Span.Start) is not { } reference)
        {
            Code(line, Render(statement, edits), bytes);
            return;
        }

        // `z := *` and `f := *`: ca65 reads `z:` at the start of a line as an address-size
        // prefix, so such a label is written as an assignment instead (§13). An assignment
        // takes the whole line, so whatever followed the label goes on the next one.
        var text = LabelText(names.Of(reference.Symbol));
        if (rest is not null && !text.EndsWith(':'))
        {
            Code(line, Indent(statement) + text, 0);
            Code(line, Indent(statement) + Render(rest, edits).TrimStart(), bytes);
            return;
        }

        edits.Replace[label.ChildTokens[0].Position] = text;
        if (label.ChildTokens.Length > 1)
            edits.Replace[label.ChildTokens[1].Position] = "";
        Code(line, Render(statement, edits), bytes);
    }

    /// <summary>
    /// An instance, which is room for one or more of its type. The type itself writes
    /// nothing, so the output reserves the bytes and names the type in a comment.
    /// </summary>
    private void Reserved(SyntaxNode line, SyntaxNode statement, SyntaxNode directive)
    {
        if (model.RoomFor(directive) is not { } room)
        {
            NotTranspiled(directive);
            return;
        }
        var named = directive.ChildNodes.FirstOrDefault();
        var type = named is null ? null : model.ReferenceAt(named.Span.Start)?.Symbol.QualifiedName;
        var text = $"{Indent(statement)}    .res {room.Bytes}";
        Code(line, type is null ? text : text + new string(' ', Math.Max(CommentColumn - text.Length, 2)) + "; " + type,
            (int)room.Bytes);
    }

    /// <summary>The label of a line whose statement is written separately.</summary>
    private void LabelOnly(SyntaxNode line, SyntaxNode statement, SyntaxNode? label)
    {
        if (label is not { ChildTokens.Length: > 0 }
            || model.ReferenceAt(label.ChildTokens[0].Span.Start) is not { } reference)
        {
            return;
        }
        Code(line, Indent(statement) + LabelText(names.Of(reference.Symbol)), 0);
    }

    /// <summary>
    /// The members of an exported layout, each written out as the constant offset it is.
    /// A layout nothing exports says nothing to ca65 and is left out entirely.
    /// </summary>
    private void Offsets(SyntaxNode line, SyntaxNode? opener)
    {
        if (opener is null || opener.ChildTokens.Length == 0)
            return;
        var named = opener.ChildTokens.FirstOrDefault(token =>
            token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic);
        if (named.Parent is null || model.ReferenceAt(named.Span.Start) is not { } reference)
            return;

        foreach (var member in reference.Symbol.Body?.Symbols ?? [])
        {
            if (exported.Contains(member) && member.Value.AsNumber() is { } offset)
                Code(line, $"{Indent(opener)}{names.Of(member)} = {Constant(offset)}", 0);
        }
    }

    /// <summary>Whether a <c>.tag</c> carries the values of an initialized instance.</summary>
    private static bool HasValues(SyntaxNode statement) =>
        statement.ChildNodes.Any(child => child.Kind == SyntaxKind.TagValues);

    /// <summary>One enum member, which is a constant like any other.</summary>
    private void EnumMember(SyntaxNode line, SyntaxNode statement)
    {
        if (statement.ChildTokens.Length == 0
            || model.ReferenceAt(statement.ChildTokens[0].Span.Start) is not { } reference
            || reference.Symbol.Value.AsNumber() is not { } value)
        {
            return;
        }
        Code(line, $"{Indent(statement)}{names.Of(reference.Symbol)} = {Constant(value)}", 0);
    }

    /// <summary>
    /// An initialized instance: the type decides the layout, so the output writes one
    /// directive per member in the type's order, whatever order the values were written in,
    /// and a member no value names is zero.
    /// </summary>
    private void Initialized(SyntaxNode line, SyntaxNode? opener, IReadOnlyList<SyntaxNode> values)
    {
        if (opener is null)
            return;
        var directive = opener.Kind == SyntaxKind.LabeledLine
            ? opener.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.DataDirective)
            : opener;
        if (directive is null || model.RoomFor(directive) is not { } room)
        {
            NotTranspiled(opener);
            return;
        }

        // The label first, then the members, so the instance starts where the label does.
        if (opener.Kind == SyntaxKind.LabeledLine
            && opener.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.Label) is { ChildTokens.Length: > 0 } label
            && model.ReferenceAt(label.ChildTokens[0].Span.Start) is { } reference)
        {
            Code(line, Indent(opener) + LabelText(names.Of(reference.Symbol)), 0);
        }

        if (directive.ChildNodes.FirstOrDefault() is not { } named || model.ReferenceAt(named.Span.Start) is not { } type)
        {
            NotTranspiled(opener);
            return;
        }

        var written = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var value in values.Concat(
            directive.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.TagValues)?.ChildNodes ?? []))
        {
            if (value.Kind == SyntaxKind.TagValue && value.ChildTokens.Length > 0)
                written[value.ChildTokens[0].Text] = value;
        }

        var bytes = Fields(line, Indent(opener), type.Symbol, written, path: "");
        if (bytes != room.Bytes)
            NotTranspiled(opener);
    }

    /// <summary>
    /// The members of a type, one directive each, in the order the type declares them. A
    /// member that is itself a type is written out the same way, so a nested initializer
    /// reaches the fields inside it.
    /// </summary>
    private long Fields(
        SyntaxNode line, string indent, Symbol type, IReadOnlyDictionary<string, SyntaxNode> written, string path)
    {
        long bytes = 0;
        foreach (var member in type.Body?.Symbols ?? [])
        {
            if (member.Kind != SymbolKind.Member || member.Size is not { } size)
                continue;
            var given = written.GetValueOrDefault(member.Name);
            var named = path.Length == 0 ? member.Name : $"{path}::{member.Name}";

            // A nested type takes a braced list of its own members; anything else is a value.
            if (member.Type is { } inner && inner.IsLayout)
            {
                bytes += Fields(line, indent, inner, ValuesIn(given), named);
                continue;
            }

            var text = $"{indent}    {Member(member, given)}";
            Code(line, text + new string(' ', Math.Max(CommentColumn - text.Length, 2)) + "; " + named, (int)size);
            bytes += size;
        }
        return bytes;
    }

    /// <summary>The members a nested initializer names, by name; empty when none was given.</summary>
    private static IReadOnlyDictionary<string, SyntaxNode> ValuesIn(SyntaxNode? given)
    {
        var values = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var value in given?.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.TagValues)?.ChildNodes ?? [])
        {
            if (value.Kind == SyntaxKind.TagValue && value.ChildTokens.Length > 0)
                values[value.ChildTokens[0].Text] = value;
        }
        return values;
    }

    /// <summary>
    /// One member of an initialized instance, written with the directive the type gave it.
    /// A member no value names is zero, and a member reserved by <c>.res</c> takes text,
    /// padded with zeros to the room it has.
    /// </summary>
    private string Member(Symbol member, SyntaxNode? given)
    {
        var size = member.Size ?? 0;
        var directive = member.Data is { ChildTokens.Length: > 0 } data
            ? data.ChildTokens[0].Text.ToLowerInvariant()
            : ".res";
        var value = given?.ChildNodes.LastOrDefault();

        if (directive is ".res" or ".tag")
            return $".byte {string.Join(", ", Padded(value, size))}";
        return $"{directive} {(value is null ? Constant(0) : Rendered(value))}";
    }

    /// <summary>The bytes a reserved member takes, from the text it was given and zeros after it.</summary>
    private IEnumerable<string> Padded(SyntaxNode? value, long size)
    {
        var bytes = value is null ? [] : model.BytesOf(value)?.ToList() ?? [];
        for (var i = 0; i < size; i++)
            yield return Hex(i < bytes.Count ? bytes[i] & 0xff : 0, 2);
    }

    /// <summary>
    /// An expression written out rather than edited in place: a call becomes what it stands
    /// for, and every nested operation is parenthesized, so nothing depends on how ca65
    /// reads precedence.
    /// </summary>
    private string Rendered(SyntaxNode node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ParenthesizedExpression:
                return node.ChildNodes.Length > 0 ? "(" + Rendered(node.ChildNodes[0]) + ")" : "";
            case SyntaxKind.BinaryExpression when node.ChildNodes.Length == 2 && node.ChildTokens.Length > 0:
                return $"({Rendered(node.ChildNodes[0])} {node.ChildTokens[0].Text} {Rendered(node.ChildNodes[1])})";
            case SyntaxKind.UnaryExpression when node.ChildNodes.Length == 1 && node.ChildTokens.Length > 0:
                return $"({node.ChildTokens[0].Text}{Rendered(node.ChildNodes[0])})";
            default:
                break;
        }

        // Anything with a value nt65 knows is written as that value; anything else keeps its
        // own spelling, with names flattened.
        if (model.ValueOf(node).AsNumber() is { } value)
            return Constant(value);
        var edits = new Edits();
        Substitute(node, edits, nested: false);
        return Render(node, edits).Trim();
    }

    /// <summary>A label, or the assignment that stands in for one ca65 would misread (§13).</summary>
    private static string LabelText(string name) => name is "z" or "f" ? $"{name} := *" : $"{name}:";

    private void Constant(SyntaxNode line, SyntaxNode statement)
    {
        // A string has no ca65 spelling as a constant: it is used through `.strlen` and
        // `.strat`, which are numbers by the time anything is written.
        if (statement.ChildTokens.Length > 0
            && model.ReferenceAt(statement.ChildTokens[0].Span.Start) is { } reference)
        {
            if (reference.Symbol.Value.IsString)
                return;
            var edits = new Edits();
            edits.Replace[statement.ChildTokens[0].Position] = names.Of(reference.Symbol);
            foreach (var child in statement.ChildNodes)
                Substitute(child, edits, nested: false);
            Code(line, Render(statement, edits), 0);
        }
    }

    /// <summary>An extern proc is a routine at a constant address, which is a constant (§13).</summary>
    private void ExternProc(SyntaxNode line, SyntaxNode statement)
    {
        var name = statement.ChildTokens.FirstOrDefault(token =>
            token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic);
        var address = statement.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.ProcSignature);
        if (name.Parent is null || address is null
            || model.ReferenceAt(name.Span.Start) is not { } reference)
        {
            return;
        }

        var edits = new Edits();
        Substitute(address, edits, nested: false);
        Code(line, $"{Indent(statement)}{names.Of(reference.Symbol)} = {Render(address, edits).TrimStart()}", 0);
    }

    private void Source(SyntaxNode line, SyntaxNode statement, int bytes)
    {
        var edits = new Edits();
        Substitute(statement, edits, nested: false);
        Code(line, Render(statement, edits), bytes);
    }

    /// <summary>
    /// A construct whose stage has not arrived. Its line parses and is kept, but nothing can
    /// be written for it, so the file is not transpiled rather than transpiled wrongly.
    /// </summary>
    private void NotTranspiled(SyntaxNode statement)
    {
        var tokens = Tokens(statement);
        var first = tokens.FirstOrDefault(token => token.Kind != SyntaxKind.EndOfLine);
        if (first.Parent is null)
            return;
        diagnostics.Add(new Diagnostic(model.Tree.GetSpan(first.Span), Severity.Error,
            $"`{first.Text}` is not transpiled yet"));
    }

    /// <summary>
    /// Writes one line that came from the source, with the debug line that maps it back
    /// (§13). Only a line that generates bytes gets one: ld65 attaches a span of bytes to
    /// the line in effect while they were generated, so a directive before a label or a
    /// constant records a line covering nothing, which no debugger can step to or break on.
    /// </summary>
    private void Code(SyntaxNode line, string text, int bytes)
    {
        Segment();
        Flush();
        if (bytes != 0)
            Line($".dbg line, \"{source}\", {line.LineIndex + 1}");
        Line(text, bytes);
    }

    /// <summary>The <c>.segment</c> directive, written when what follows lands somewhere new.</summary>
    private void Segment()
    {
        if (written == segment)
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
        if (output.Length > 0)
            Line("");
    }

    private void Line(string text, int bytes = 0)
    {
        output.Append(text.TrimEnd()).Append('\n');
        lineBytes.Add(bytes);
    }

    /// <summary>The whitespace a line starts with, which the output keeps.</summary>
    private static string Indent(SyntaxNode statement)
    {
        var tokens = Tokens(statement);
        if (tokens.Count == 0)
            return "";
        var indent = new StringBuilder();
        foreach (var trivia in tokens[0].Green.LeadingTrivia)
        {
            if (trivia.Kind == SyntaxKind.WhitespaceTrivia)
                indent.Append(trivia.Text);
        }
        return indent.ToString();
    }

    /// <summary>
    /// The statement's own text with the edits applied: the source's spacing, its comments
    /// dropped, and names, prefixes and byte values written where the source had something
    /// else.
    /// </summary>
    private string Render(SyntaxNode statement, Edits edits)
    {
        var text = new StringBuilder();
        foreach (var token in Tokens(statement))
        {
            if (token.Kind == SyntaxKind.EndOfLine)
                continue;
            Whitespace(text, token.Green.LeadingTrivia);
            if (edits.Before.TryGetValue(token.Position, out var before))
                text.Append(before);
            text.Append(edits.Replace.TryGetValue(token.Position, out var replacement) ? replacement : token.Text);
            if (edits.After.TryGetValue(token.Position, out var after))
                text.Append(after);
            Whitespace(text, token.Green.TrailingTrivia);
        }

        var line = text.ToString().TrimEnd();
        if (edits.Comments.Count == 0)
            return line;
        var padding = Math.Max(CommentColumn - line.Length, 2);
        return line + new string(' ', padding) + "; " + string.Join(", ", edits.Comments);
    }

    private static void Whitespace(StringBuilder text, IEnumerable<GreenTrivia> trivia)
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
        switch (node.Kind)
        {
            case SyntaxKind.NameExpression:
                Name(node, edits);
                return;

            case SyntaxKind.StringExpression:
            case SyntaxKind.CharacterExpression:
                Text(node, edits);
                return;

            case SyntaxKind.CallExpression:
                Applied(node, edits);
                return;

            case SyntaxKind.AbsoluteOperand:
                Prefix(node, edits);
                break;

            case SyntaxKind.DataDirective:
                Terminated(node, edits);

                // An `.incbin` names a file rather than holding data, so its path is left a
                // path — pointed at the file from wherever the output lands.
                if (Included(node, edits))
                    return;
                break;

            case SyntaxKind.BinaryExpression:
            case SyntaxKind.UnaryExpression:
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
    /// Text reaches the output as bytes (§8, §13), so <c>.asciiz</c> becomes the bytes and
    /// the zero that ends them: ca65's own directive takes a string, and there is none left.
    /// </summary>
    private static void Terminated(SyntaxNode directive, Edits edits)
    {
        if (directive.ChildTokens.Length == 0
            || !directive.ChildTokens[0].Text.Equals(".asciiz", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        edits.Replace[directive.ChildTokens[0].Position] = ".byte";
        var last = directive.ChildNodes.LastOrDefault() is { } argument && Tokens(argument) is [.., var token]
            ? token.Position
            : directive.ChildTokens[0].Position;
        edits.After[last] = edits.After.GetValueOrDefault(last, "") + ", $00";
    }

    /// <summary>
    /// The path of an <c>.incbin</c>, rewritten so that ca65 finds the file from the output
    /// rather than from the source. Returns whether the directive was one.
    /// </summary>
    private bool Included(SyntaxNode directive, Edits edits)
    {
        if (directive.ChildTokens.Length == 0
            || !directive.ChildTokens[0].Text.Equals(".incbin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (directive.ChildNodes.FirstOrDefault() is { Kind: SyntaxKind.StringExpression } path
            && path.ChildTokens.Length > 0
            && model.ValueOf(path) is { Kind: ValueKind.String, Text: { } named })
        {
            var at = source.LastIndexOf('/');
            edits.Replace[path.ChildTokens[0].Position] =
                "\"" + (at < 0 ? named : source[..(at + 1)] + named) + "\"";
        }
        return true;
    }

    private void Name(SyntaxNode name, Edits edits)
    {
        var tokens = name.ChildTokens;
        if (tokens.Length == 0)
            return;

        // A path names one symbol; the whole of it becomes that symbol's flat name.
        var last = tokens.LastOrDefault(token => token.Kind is not SyntaxKind.ColonColon);
        if (last.Parent is null || model.ReferenceAt(last.Span.Start) is not { } reference)
            return;

        // A list stands for its own items wherever data takes them.
        if (model.ItemsOf(name) is { Count: > 0 } items)
        {
            edits.Replace[tokens[0].Position] = string.Join(", ", items.Select(Rendered));
            for (var i = 1; i < tokens.Length; i++)
                edits.Replace[tokens[i].Position] = "";
            edits.Comments.Add(name.GetText().Trim());
            return;
        }

        // A member is an offset: the offsets along the path added up, on the address the
        // path starts from when it starts at an instance rather than at a type.
        if (reference.Symbol.Kind == SymbolKind.Member)
        {
            MemberPath(name, tokens, edits);
            return;
        }

        // A define and a checked import are written as their value, never by name (§5.3,
        // §12): a `-D` given to ca65 then cannot collide with a define, and a checked import
        // is a value nt65 has already used in its own arithmetic.
        var symbol = reference.Symbol;
        var byValue = (symbol.IsDefine || symbol.Kind == SymbolKind.ImportedConstant)
            && symbol.Value.AsNumber() is not null;
        edits.Replace[tokens[0].Position] = byValue
            ? Constant(symbol.Value.Number)
            : names.Of(symbol);
        for (var i = 1; i < tokens.Length; i++)
            edits.Replace[tokens[i].Position] = "";
        if (byValue)
            edits.Comments.Add(symbol.QualifiedName);
    }

    /// <summary>
    /// A path through a type or an instance. Through a type it is a number; through an
    /// instance it is that instance plus the offset, which is what ca65 and ld65 resolve.
    /// Either way the path it came from is kept in a comment.
    /// </summary>
    private void MemberPath(SyntaxNode name, IReadOnlyList<SyntaxToken> tokens, Edits edits)
    {
        Symbol? start = null;
        long offset = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == SyntaxKind.ColonColon || model.ReferenceAt(token.Span.Start) is not { } part)
                continue;
            if (part.Symbol.Kind == SymbolKind.Member)
                offset += part.Symbol.Value.AsNumber() ?? 0;
            else if (part.Symbol.IsAddress)
                start ??= part.Symbol;
        }

        var text = start is null
            ? Constant(offset)
            : offset == 0 ? names.Of(start) : $"{names.Of(start)}+{offset}";
        edits.Replace[tokens[0].Position] = text;
        for (var i = 1; i < tokens.Count; i++)
            edits.Replace[tokens[i].Position] = "";
        edits.Comments.Add(name.GetText().Trim());
    }

    /// <summary>
    /// A call written out as what it stands for: a charmap applied to text becomes the bytes
    /// it maps them to, and a function call becomes its value. A call nt65 cannot work out
    /// is refused rather than passed to ca65, which knows neither.
    /// </summary>
    private void Applied(SyntaxNode call, Edits edits)
    {
        var tokens = Tokens(call);
        if (tokens.Count == 0)
            return;

        // A built-in the analysis answers keeps the ordinary path.
        if (call.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.NameExpression) is null)
        {
            if (model.ValueOf(call).AsNumber() is { } builtin)
            {
                edits.Replace[tokens[0].Position] = Constant(builtin);
                for (var i = 1; i < tokens.Count; i++)
                    edits.Replace[tokens[i].Position] = "";
                return;
            }
            foreach (var child in call.ChildNodes)
                Substitute(child, edits, nested: false);
            return;
        }

        string? text = null;
        if (model.BytesOf(call) is { Count: > 0 } bytes)
            text = string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2)));
        else if (model.ValueOf(call).AsNumber() is { } value)
            text = Constant(value);

        if (text is null)
        {
            NotTranspiled(call);
            return;
        }
        edits.Replace[tokens[0].Position] = text;
        for (var i = 1; i < tokens.Count; i++)
            edits.Replace[tokens[i].Position] = "";
        edits.Comments.Add(call.GetText().Trim());
    }

    /// <summary>Text becomes byte values, with the source spelling kept in a comment (§8, §13).</summary>
    private void Text(SyntaxNode literal, Edits edits)
    {
        if (DataLengths.Bytes(literal, model) is not { Count: > 0 } bytes || literal.ChildTokens.Length == 0)
            return;
        edits.Replace[literal.ChildTokens[0].Position] =
            string.Join(", ", bytes.Select(b => Hex(b & 0xff, 2)));
        edits.Comments.Add(literal.GetText());
    }

    /// <summary>The <c>z:</c> or <c>a:</c> that says which mode was chosen (§7.2, §13).</summary>
    private void Prefix(SyntaxNode operand, Edits edits)
    {
        var instruction = operand.Parent;
        if (instruction is null || layout.Of(instruction) is not { Prefix: { } prefix })
            return;
        var written = operand.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.AddressPrefix);
        var expression = operand.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.AddressPrefix);
        var tokens = expression is null ? [] : Tokens(expression);

        // ca65 reads a `(` straight after a prefix as an indirect operand, so an expression
        // that starts with one — `lda (hi + lo) * 2`, which §7.1 allows — gets a unary `+`
        // in front of it. It changes nothing and keeps the operand an expression.
        var text = tokens is [{ Kind: SyntaxKind.OpenParen }, ..] ? prefix + "+" : prefix;

        if (written is { ChildTokens.Length: > 0 })
        {
            // The source already said which; write the one that was chosen, in case an
            // expression made it wider.
            foreach (var token in written.ChildTokens)
                edits.Replace[token.Position] = "";
            edits.Replace[written.ChildTokens[0].Position] = text;
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
    }
}
