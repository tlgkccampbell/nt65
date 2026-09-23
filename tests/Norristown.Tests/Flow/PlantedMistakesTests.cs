using Norristown.Tests.Oracle;

namespace Norristown.Tests.Flow;

/// <summary>
/// The SNES corpus program with mistakes planted in it: a call made with a register in the
/// wrong width, a data bank or direct page that does not reach the address an operand names,
/// a hardware register written while the data bank is one it is not mirrored in, a routine
/// called or returned from the wrong way, and a stack frame bigger than what was pushed. The
/// program no longer writes out signature sets and the other annotations it once needed, and
/// dropping them must not have hidden any of these mistakes. Each mistake is in a routine of
/// its own, so one compile catches them all.
/// </summary>
public sealed class PlantedMistakesTests
{
    private static readonly (string File, string Find, string Replace, string Expected)[] Mistakes =
    [
        ("src/main.nt65", "    sep #$20\n    lda #0\n    jsl bank1::level_speed", "    lda #0\n    jsl bank1::level_speed",
            "needs `a8`, and A is 16-bit here"),
        ("src/main.nt65", "    jsl bank1::spawn_player", "    jsr bank1::spawn_player",
            "is far, and is called with `jsl`"),
        ("src/main.nt65", "    mov16!(player_x, {#16})\n    sep #$20\n", "    mov16!(player_x, {#16})\n",
            "`rts`: `respawn` returns with `a8`, and A is 16-bit here"),
        ("src/main.nt65", "    pea 2                       ; divisor\n", "",
            "`div16` takes `args 4`, pushed before the call, and only 2 bytes are pushed here"),
        ("src/main.nt65", "        sta f:oam_shadow::tile", "        sta oam_shadow::tile",
            "is in \"WRAM\", which is in bank $7e, and B is $80 here"),
        ("src/reset.nt65", "    rep #$30\n    pha\n", "    pha\n",
            "`lda #` needs the width of A, and it is not known here, because `nmi` is an interrupt handler, entered from anywhere"),
        ("src/reset.nt65", "    jsr init_ppu", "    jsr nmi\n    jsr init_ppu",
            "`nmi` is an interrupt handler, which the processor enters and `rti` leaves: a call to it would not come back"),
        ("src/reset.nt65", "    cli\n    jmp main::main", "    cli\n    rts",
            "`reset` never returns, as its `noreturn` says, and `rts` returns"),
        ("src/bank1.nt65", "    phb\n    phk\n    plb\n    rep #$20", "    phb\n    rep #$20",
            "`spawn_points` is in \"BANK1\", which is in bank $81, and B is $80 here"),
        ("src/bank1.nt65", "    lda f:speeds,x\n    rtl", "    lda f:speeds,x\n    rts",
            "is far, and returns with `rtl`"),
        ("src/gfx.nt65", "    pea $4300\n    pld\n    stz d:$4310", "    stz d:$4310",
            "`d:$4310` is reached through the direct page at $0000, which reaches only $0000 to $00ff"),
        ("src/gfx.nt65", "    plb\n    ldx #.sizeof(oam_shadow)", "    plb\n    sta INIDISP\n    ldx #.sizeof(oam_shadow)",
            "$2100 is reached only from banks $00-$3f, $80-$bf, and B is $7e here"),
        ("src/math.nt65", "    pea 0                       ; prod_hi\n", "",
            "`f` is 10 bytes, and only 8 are pushed here"),
    ];

    [Fact]
    public void EveryPlantedMistakeIsCaught()
    {
        var program = CorpusProgram.Load(Repo.Path("tests", "corpus", "snes"));
        var sources = program.Sources.ToDictionary(source => source.Path, source => source.Text.ReplaceLineEndings("\n"));
        foreach (var (file, find, replace, _) in Mistakes)
        {
            Assert.True(sources[file].Split(find).Length == 2, $"{file} has no single \"{find}\"");
            sources[file] = sources[file].Replace(find, replace, StringComparison.Ordinal);
        }

        var planted = program with { Sources = [.. sources.Select(pair => new SourceFile(pair.Key, pair.Value))] };
        var reported = planted.Compile().Diagnostics;
        var missed = Mistakes
            .Where(mistake => !reported.Any(d => d.Span.File == mistake.File && d.Message.Contains(mistake.Expected, StringComparison.Ordinal)))
            .Select(mistake => $"{mistake.File}: nothing said \"{mistake.Expected}\"")
            .ToList();
        Assert.True(missed.Count == 0, string.Join("\n", missed.Append("reported:").Concat(reported.Select(d =>
            $"  {d.Span.File}:{d.Span.Line}: {d.Message}"))));
    }
}
