using Norristown.Semantics;

namespace Norristown.Tests.Semantics;

/// <summary>
/// Routine families: a <c>.proc</c> inside an <c>.each</c> over an enum, named with the name the
/// <c>.each</c> binds, which declares one routine per member of the enum. <c>.multiproc</c> folds
/// those two blocks into one line, so the two forms are one construct, and the test of that is
/// that they write the same bytes.
/// </summary>
public sealed class FamilyTests
{
    private const string Channels = """
        .module main
        .enum Channel {
            pulse1
            pulse2
            triangle
        }

        .segment CODE

        """;

    private const string Body = """
                ldx #Channel::ch
                lda $4000,x
            @again:
                dex
                bne @again
                rts
        """;

    /// <summary>
    /// <c>.multiproc E, b: signature { }</c> is <c>.each E, b { .proc b: signature { } }</c>,
    /// so the two write the same output byte for byte: the folded form is only another spelling.
    /// </summary>
    [Fact]
    public void TheFoldedFormWritesWhatTheLongFormDoes()
    {
        var folded = Output($"{Channels}.scope play {{\n.multiproc Channel, ch: a8, i8 {{\n{Body}\n}}\n}}\n");
        var long_ = Output($"{Channels}.scope play {{\n.each Channel, ch {{\n.proc ch: a8, i8 {{\n{Body}\n}}\n}}\n}}\n");

        // The comment above each routine names the directive it was written as and the line
        // it came from, which are the two things that differ; every byte is the same.
        Assert.Contains("play__triangle:", folded, StringComparison.Ordinal);
        Assert.Equal(Code(long_), Code(folded));
    }

    /// <summary>The instances are named after the enum's members, in the order it lists them.</summary>
    [Fact]
    public void AFamilyDeclaresOneRoutinePerMember()
    {
        var program = Analysis.Program(("main.nt65", $"{Channels}.multiproc Channel, ch {{\n    rts\n}}\n"));
        var model = program.File("main.nt65");

        var family = Assert.Single(model.Families);
        Assert.Equal([".multiproc", "Channel", "ch"], [family.Directive, family.Enumeration.Name, family.Binding.Name]);
        Assert.Equal(["pulse1", "pulse2", "triangle"], family.Instances.Select(instance => instance.Name));
        Assert.All(family.Instances, instance => Assert.Equal(SymbolKind.Proc, instance.Kind));

        // Each instance is declared where the family is written, which is what go to definition
        // on one of them lands on.
        Assert.All(family.Instances, instance => Assert.Equal(family.Binding.NameSpan, instance.NameSpan));
    }

    /// <summary>
    /// A name a family's body declares is that instance's, so the output names it after the
    /// instance rather than once for all of them.
    /// </summary>
    [Fact]
    public void WhatTheBodyDeclaresIsNamedAfterTheInstance()
    {
        var main = Output($"{Channels}.multiproc Channel, ch {{\n@again:\n    bne @again\n    rts\n}}\n");

        Assert.Contains("pulse1__again:", main, StringComparison.Ordinal);
        Assert.Contains("triangle__again:", main, StringComparison.Ordinal);
    }

    /// <summary>
    /// Declaring a family does not cost the module its re-exports. Declaring the instances
    /// reads the module's <c>.use</c> directives once against an unfinished program and then
    /// discards what that read found, and the re-exports must survive the discard.
    /// </summary>
    [Fact]
    public void AModuleThatDeclaresAFamilyStillReexports()
    {
        var program = Analysis.Program(
            ("vic.nt65", ".module vic\n.export BORDER\nBORDER = $d020\n"),
            ("hw.nt65", ".module hw\n.export .use vic::BORDER\n.enum Channel {\n    a\n    b\n}\n.segment CODE\n.export .multiproc Channel, ch {\n    rts\n}\n"),
            ("main.nt65", ".module main\n.use hw::BORDER\n.segment CODE\n.export .proc main {\n    sta BORDER\n    rts\n}\n"));

        Assert.Empty(program.Problems());
    }

    private static string Output(string text) => Analysis.Outputs(("main.nt65", text))["main.s"];

    /// <summary>The output without the comments, which name where each line came from.</summary>
    private static string Code(string output) =>
        string.Join('\n', output.Split('\n').Where(line => !line.TrimStart().StartsWith(';')));
}
