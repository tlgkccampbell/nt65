using Norristown.LanguageServer.Protocol;
using static Norristown.Tests.LanguageServer.EditingWorkspace;

namespace Norristown.Tests.LanguageServer;

/// <summary>Tests the signature help an editor shows while the arguments of a call are typed.</summary>
public sealed class SignatureHelpTests
{
    public static TheoryData<string, string, string, int> Calls => new()
    {
        { "body", "    poke!(|", "poke!(address: expr, value: const = 0)", 0 },
        { "body", "    poke!(SCREEN, |", "poke!(address: expr, value: const = 0)", 1 },
        { "body", "    poke!(value = |", "poke!(address: expr, value: const = 0)", 1 },
        { "body", "    poke!(twice(|", "twice(n)", 0 },
        { "body", "    poke!(twice(1), |", "poke!(address: expr, value: const = 0)", 1 },
        { "top", ".const X = .select(1, 2, |", ".select(condition, chosen, otherwise)", 2 },
        { "top", ".const X = .switch(1, |", ".switch(value, set, result, ...)", 1 },
        { "top", ".const X = .switch(1, [1, 2], |", ".switch(value, set, result, ...)", 2 },
        { "top", ".const X = .switch(1, [1], 2, [3..4], 5, |", ".switch(value, set, result, ...)", 1 },
        { "top", ".const X = .switch(1,\n    [1], |", ".switch(value, set, result, ...)", 2 },
        { "top", ".const X = .strsub(\"HELLO\", 1, |", ".strsub(text, start, count)", 2 },
        { "top", ".const X = .strcat(\"A\", 1, 2, |", ".strcat(part, ...)", 0 },
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task SignatureHelpShowsWhatTheCallTakes(string where, string line, string signature, int active)
    {
        var timeout = TestTimeout.Token();
        var (text, position) = WithLine(where, line);
        await using var client = await OpenAsync(text, timeout);

        var help = await client.RequestAsync<SignatureHelp?>("textDocument/signatureHelp",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        Assert.NotNull(help);
        var only = Assert.Single(help.Signatures);
        Assert.Equal(signature, only.Label);
        Assert.Equal(active, help.ActiveParameter);
    }

    [Fact]
    public async Task SignatureHelpIsNothingOutsideACall()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = WithLine("body", "    lda (vic::BORDER),y|");
        await using var client = await OpenAsync(text, timeout);

        Assert.Null(await client.RequestAsync<SignatureHelp?>("textDocument/signatureHelp",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout));
    }
}
