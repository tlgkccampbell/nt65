using Norristown.LanguageServer.Protocol;
using Norristown.Syntax;
using static Norristown.Tests.LanguageServer.EditingWorkspace;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests what completion offers while code is being typed, and what an item inserts when it is
/// chosen. It also tests what the server announces it can do for the requests made while typing.
/// </summary>
public sealed class CompletionTests
{
    public static TheoryData<string, string, string[], string[]> Completions => new()
    {
        // After `::`, what the module or type the path names exports, and nothing it keeps private.
        { "body", "    jsr gfx::|", ["Sprite", "SCREEN", "clear"], ["helper", "main"] },
        { "body", "    lda gfx::Sprite::|", ["x", "y"], ["clear"] },
        { "body", "    lda vic::|", ["BORDER"], ["clear"] },

        // A `.use` path starts from the root of the module names, and in its braces offers what
        // the module exports.
        { "top", ".use |", ["gfx", "hw", "main"], ["clear"] },
        { "top", ".use hw::|", ["vic"], ["gfx"] },
        { "top", ".use gfx::{|", ["SCREEN", "Sprite", "clear"], ["hw", "helper"] },

        // An operand may name anything in scope, anything a `.use` brought in, or a module to
        // start a path from.
        { "body", "    lda |", ["@loop", "clear", "gfx", "main", "twice", "vic"], ["poke", "fast", "lda"] },
        { "macro", "    sta |", ["address", "value"], ["@loop"] },

        // A statement starts with an instruction or a macro call.
        { "body", "    |", ["lda", "poke"], ["clear"] },

        // After a routine's `:`, the signature items and the named signatures. A `.state` states the
        // processor at one point, so it takes no calling convention or named signature; a macro is
        // not called, so it takes no calling convention either.
        { "top", ".proc other: |", ["a16", "a?", "a*", "dp", "far", "fast", "keeps", "near"], ["clear", "lda"] },
        { "body", "    .state |", ["a16", "dbr", "e?", "keeps", "native"], ["near", "a*", "fast"] },
        { "top", ".macro m(): |", ["a8", "e*"], ["near", "far", "keeps"] },
        { "top", ".proc other: a8, dp = |", ["twice", "vic"], ["a16"] },

        // A macro call's argument may name its parameter.
        { "body", "    poke!(|", ["address", "value", "clear"], ["lda"] },
        { "body", "    poke!(1, |", ["value"], [] },

        // A name being declared is not completed, and nothing is offered between the name and the
        // `:` or `{` that follows it.
        { "top", ".proc |", [], ["clear"] },
        { "top", ".proc other |", [], ["clear", "lda", ".proc", "near"] },
        { "top", ".macro m(|", [], ["clear", "expr"] },
        { "top", ".macro m(a: |", ["expr", "const", "operand", "block", "one", "Pitch"], ["clear", "lda"] },

        // Inside a parameter kind: the addressing modes an `operand` may list, the kinds a `list`'s
        // items may be, and nothing inside a `one`, whose words the macro's author chooses.
        { "top", ".macro m(a: operand(|", ["imm", "zpx", "longy"], ["expr", "clear", "Pitch"] },
        { "top", ".macro m(a: operand(imm, |", ["zp", "abs"], ["expr", "clear"] },
        { "top", ".macro m(a: list(|", ["const", "one", "Pitch"], ["imm", "clear"] },
        { "top", ".macro m(a: one(|", [], ["expr", "imm", "clear"] },

        // An argument is offered the values its parameter accepts: an enum's members by their bare
        // names, and a `one`'s words, whether the argument is positional or named.
        { "body", "    tone!(|", ["low", "high", "p", "w"], ["up"] },
        { "body", "    tone!(low, |", ["up", "down"], ["low"] },
        { "body", "    tone!(low, up, |", ["low", "high"], ["up"] },
        { "body", "    tone!(w = |", ["up", "down"], ["low"] },

        // In a condition, `.mode(p)` is compared with the modes the parameter allows, and a `one`
        // with its words.
        { "pick", "    .if .mode(src) == |", ["imm", "abs"], ["zp", "absx", "clear", "src"] },
        { "pick", "    .if reg != |", ["x", "y"], ["imm", "clear"] },

        // After a complete expression only an operator may follow, never a name.
        { "body", "    lda clear |", [], ["clear", "x", "#"] },
        { "body", "    lda clear + |", ["clear", "twice"], ["x", "#"] },

        // Only the statements the place accepts are offered: a file's top level holds declarations,
        // and only code holds instructions and the directives that go with them.
        { "top", "|", [".proc", ".data", ".export", ".cpu"], ["lda", "rts", "poke", ".state"] },
        { "body", "|", [".data", ".scope", ".state", ".ensure", ".byte", "lda", "poke"], [".proc", ".macro", ".cpu", ".config", ".module"] },
        { "macro", "|", ["lda", ".state", ".if"], [".proc", ".macro", ".export", ".import", ".segment"] },
        { "struct", "|", [".struct", ".union"], ["lda", ".proc", ".byte", "clear"] },
        { "values", "|", ["clear", "twice", ".sizeof"], ["lda", ".proc", ".byte"] },
        { "scope", "|", [".proc", ".macro", ".data"], ["lda", "rts", ".state"] },
        { "body", ".|", [".data", ".state", ".byte"], [".proc", ".config", "clear"] },

        // After a `:`, the storage a member or data declaration takes, and the address size of an
        // imported name; after `.cpu`, the processors.
        { "struct", "    x: |", [".byte", ".word", ".res", ".type"], ["lda", "clear"] },
        { "top", ".data d: |", [".byte", ".addr", ".res", ".incbin"], ["lda", ".proc"] },
        { "top", ".import io: |", ["zp", "abs", "far", "proc"], ["clear"] },
        { "top", ".cpu |", ["6502", "65816", "65c02"], ["clear", "lda"] },

        // A `}` is followed by the next branch of a condition, and by nothing else.
        { "top", "} |", [".else", ".elseif"], [".proc", "lda", "clear"] },

        // An operand is offered the instruction's addressing forms, and names to build an address from.
        { "body", "    lda |", ["#", "z:", "a:", "(", "clear", "@loop"], ["x", "y", "a", "f:", "["] },
        { "body", "    asl |", ["a", "z:", "clear"], ["#", "("] },
        { "body", "    jsr |", ["clear", "main"], ["#", "z:", "a:", "("] },
        { "body", "    inx |", [], ["#", "a", "clear", "lda"] },

        // After the comma, the index registers that the operand's addressing form allows.
        { "body", "    lda table, |", ["x", "y"], ["s", "#", "clear"] },
        { "body", "    lda (table), |", ["y"], ["x", "s"] },
        { "body", "    lda (table, |", ["x"], ["y", "s"] },
        { "body", "    sta table, |", ["x", "y"], ["s"] },

        // Where a number may go, the prefixes for hex (`$`), binary (`%`) and a character (`'`)
        // are offered; decimal digits need no prefix.
        { "body", "    lda #|", ["$", "%", "'"], ["x", "z:"] },
        { "body", "    lda |", ["$", "%", "'"], ["x"] },
        { "values", "|", ["$", "%", "'"], ["lda"] },
        { "body", "    inx |", [], ["$", "%", "'"] },

        // An expression may call a built-in function, and three of them exist only in a macro body.
        { "body", "    lda #|", [".sizeof", ".lobyte", "clear"], [".mode", ".byteof", "x"] },
        { "macro", "    lda #|", [".sizeof", ".mode", ".byteof", ".empty"], ["x"] },

        // Nothing is offered inside a comment or a text literal.
        { "body", "    lda #1 ; load the |", [], ["lda", "clear", ".sizeof"] },
        { "top", ".error \"what went |", [], ["clear", ".proc"] },
    };

    [Fact]
    public async Task AnnouncesWhatTheEditingLayerCanDo()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(timeout);

        var capabilities = client.Initialized.Capabilities;
        Assert.Equal(
            [" ", ".", ":", "@", "!", "#", "(", "[", ","], capabilities.CompletionProvider?.TriggerCharacters);
        Assert.Equal(["(", ",", "="], capabilities.SignatureHelpProvider?.TriggerCharacters);
        Assert.NotNull(capabilities.CodeLensProvider);
        Assert.True(capabilities.WorkspaceSymbolProvider);
        Assert.Equal(
            ["quickfix", "refactor.rewrite", "refactor.extract"], capabilities.CodeActionProvider?.CodeActionKinds);
    }

    [Theory]
    [MemberData(nameof(Completions))]
    public async Task CompletionOffersWhatMayBeTypedThere(string where, string line, string[] offered, string[] notOffered)
    {
        var timeout = TestTimeout.Token();
        var (text, position) = WithLine(where, line);
        await using var client = await OpenAsync(text, timeout);

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        var labels = items.Select(item => item.Label).ToHashSet();
        Assert.All(offered, label => Assert.Contains(label, labels));
        Assert.All(notOffered, label => Assert.DoesNotContain(label, labels));
    }

    /// <summary>
    /// Where an expression may start, completion offers exactly the built-in functions in the
    /// table. Those only a macro body may call are offered only in one.
    /// </summary>
    [Theory]
    [InlineData("body", false)]
    [InlineData("macro", true)]
    public async Task CompletionOffersExactlyTheBuiltinTable(string where, bool inMacro)
    {
        var timeout = TestTimeout.Token();
        var (text, position) = WithLine(where, "    lda #|");
        await using var client = await OpenAsync(text, timeout);

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        Assert.Equal(
            SyntaxFacts.Builtins.Where(builtin => inMacro || !builtin.MacroOnly).Select(builtin => builtin.Name).Order(),
            items.Where(item => item.Detail == "built-in function").Select(item => item.Label).Order());
    }

    /// <summary>
    /// Completion replaces the part of a name already typed, and inserts a named argument with its
    /// <c>=</c>.
    /// </summary>
    [Fact]
    public async Task ACompletionReplacesWhatIsTypedAndInsertsWhatTheItemNeeds()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = WithLine("body", "    poke!(val|");
        await using var client = await OpenAsync(text, timeout);

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        var value = Assert.Single(items, item => item.Label == "value");
        Assert.Equal(CompletionItemKind.Property, value.Kind);
        Assert.Equal("value = ", value.TextEdit.NewText);
        Assert.Equal(new Position(position.Line, position.Character - 3), value.TextEdit.Range.Start);
        Assert.Equal(position, value.TextEdit.Range.End);
    }

    /// <summary>
    /// An instruction that takes an operand is inserted with a trailing space and a command that
    /// asks the client to complete the operand; one that takes no operand is inserted alone.
    /// </summary>
    [Fact]
    public async Task AnInstructionThatTakesAnOperandLeadsOnToIt()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = WithLine("body", "|");
        await using var client = await OpenAsync(text, timeout);

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        var lda = Assert.Single(items, item => item.Label == "lda");
        Assert.Equal("lda ", lda.TextEdit.NewText);
        Assert.Equal("editor.action.triggerSuggest", lda.Command?.Name);
        var inx = Assert.Single(items, item => item.Label == "inx");
        Assert.Equal("inx", inx.TextEdit.NewText);
        Assert.Null(inx.Command);
        var poke = Assert.Single(items, item => item.Label == "poke");
        Assert.Equal("poke!(", poke.TextEdit.NewText);
        Assert.Equal("editor.action.triggerSuggest", poke.Command?.Name);
    }

    /// <summary>An operand is offered the forms the CPU the program is built for actually has.</summary>
    [Theory]
    [InlineData("    lda |", new[] { "#", "(", "[", "z:", "a:", "f:", "d:" }, new[] { "x", "y", "s" })]
    [InlineData("    sta 3,|", new[] { "x", "y", "s" }, new[] { "#", "z:", "a:" })]
    [InlineData("    sta (3,|", new[] { "x", "s" }, new[] { "y" })]
    [InlineData("    sep |", new[] { "#" }, new[] { "main", "z:", "x" })]
    [InlineData("    mvn |", new[] { "#" }, new[] { "main", "z:", "a:" })]
    public async Task AnOperandOffersTheFormsTheCpuHas(string line, string[] offered, string[] notOffered)
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .cpu 65816
            .segment CODE
            .proc main {
            @line
            }
            """.Replace("@line", line, StringComparison.Ordinal));
        await using var client = await TestClient.OpenedAsync(timeout, (MainUri, text));

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        var labels = items.Select(item => item.Label).ToHashSet();
        Assert.All(offered, label => Assert.Contains(label, labels));
        Assert.All(notOffered, label => Assert.DoesNotContain(label, labels));
    }
}
