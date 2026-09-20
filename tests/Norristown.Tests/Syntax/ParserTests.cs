using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class ParserTests
{
    [Theory]
    // C's order, so a tighter operator ends up deeper.
    [InlineData("1 + 2 * 3", "(1 + (2 * 3))")]
    [InlineData("1 * 2 + 3", "((1 * 2) + 3)")]
    [InlineData("1 - 2 - 3", "((1 - 2) - 3)")]
    [InlineData("7 .mod 2 + 1", "((7 .mod 2) + 1)")]
    [InlineData("p == q || r != t", "((p == q) || (r != t))")]
    [InlineData("p < q && r > t", "((p < q) && (r > t))")]
    // Parentheses in the source stay visible, and are what makes the traps below legal.
    [InlineData("(flags & $0f) == 0", "([(flags & $0f)] == 0)")]
    [InlineData("1 << (i + 1)", "(1 << [(i + 1)])")]
    [InlineData("(<label) + 1", "([(<label)] + 1)")]
    [InlineData("<(label + 1)", "(<[(label + 1)])")]
    // Unary operators, names and calls.
    [InlineData("-n", "(-n)")]
    [InlineData("~ ~n", "(~(~n))")]
    [InlineData("^far_label", "(^far_label)")]
    [InlineData("*", "*")]
    [InlineData("* + 2", "(* + 2)")]
    [InlineData("::top_level", "::top_level")]
    [InlineData("Player::pos::y", "Player::pos::y")]
    [InlineData(".sizeof(Point)", ".sizeof(Point)")]
    [InlineData(".min(1, 2) + 1", "(.min(1,2) + 1)")]
    [InlineData("rgb15(31, 0, 0)", "rgb15(31,0,0)")]
    [InlineData("screen(\"HELLO\")", "screen(\"HELLO\")")]
    [InlineData(".target(65c02)", ".target(65c02)")]
    public void ExpressionsBindAsCDoes(string expression, string shape) =>
        Assert.Equal(shape, SyntaxDump.Infix(Expression(expression)));

    [Theory]
    // An operand of a shift or a bitwise operator may not be a different binary operator.
    [InlineData("1 << i + 1", "`<<` and `+` need parentheses to show which applies first")]
    [InlineData("flags & $0f == 0", "`&` and `==` need parentheses to show which applies first")]
    [InlineData("p ^ q + 1", "`^` and `+` need parentheses to show which applies first")]
    [InlineData("p | q & r", "`|` and `&` need parentheses to show which applies first")]
    // The logical operators may not be mixed.
    [InlineData("p || q && r", "`||` and `&&` need parentheses to show which applies first")]
    [InlineData("p && q ^^ r", "`^^` and `&&` need parentheses to show which applies first")]
    // A byte operator that reads as if it applied to the whole expression.
    [InlineData("<label + 1", "unary `<` before `+` needs parentheses to show what `<` applies to")]
    [InlineData(">label * 2", "unary `>` before `*` needs parentheses to show what `>` applies to")]
    [InlineData("1 + <label + 2", "unary `<` before `+` needs parentheses to show what `<` applies to")]
    public void ParenthesesAreRequiredWhereTheOrderIsEasyToMisread(string expression, string message) =>
        Assert.Equal([message], Errors(".word " + expression));

    [Theory]
    // The same operator repeated needs nothing, and neither does a tighter operator that is
    // not one of the two sets the language names.
    [InlineData("p | q | r")]
    [InlineData("1 << 2 << 3")]
    [InlineData("p && q | r")]
    [InlineData("p && q && r")]
    [InlineData("(flags & $0f) == 0")]
    [InlineData("1 << (i + 1)")]
    [InlineData("(<label) + 1")]
    [InlineData("<(label + 1)")]
    [InlineData("1 + 2 * 3 - 4 / 5")]
    public void ClearExpressionsNeedNoParentheses(string expression) => Assert.Empty(Errors(".word " + expression));

    [Theory]
    [InlineData("inx", "InstructionStatement(inx)")]
    [InlineData("asl a", "InstructionStatement(asl AccumulatorOperand(a))")]
    [InlineData("lda #$10", "InstructionStatement(lda ImmediateOperand(# NumberExpression($10)))")]
    [InlineData("lda ptr", "InstructionStatement(lda AbsoluteOperand(NameExpression(IdentifierName(ptr))))")]
    [InlineData("lda d:$2105",
        "InstructionStatement(lda AbsoluteOperand(AddressPrefix(d :) NumberExpression($2105)))")]
    [InlineData("lda z:ptr+1", "InstructionStatement(lda AbsoluteOperand(AddressPrefix(z :) "
        + "BinaryExpression(NameExpression(IdentifierName(ptr)) + NumberExpression(1))))")]
    [InlineData("lda buf,x", "InstructionStatement(lda AbsoluteOperand(NameExpression(IdentifierName(buf)) , x))")]
    [InlineData("lda (ptr),y", "InstructionStatement(lda IndirectOperand(( NameExpression(IdentifierName(ptr)) ) , y))")]
    [InlineData("lda (ptr,x)",
        "InstructionStatement(lda IndexedIndirectOperand(( NameExpression(IdentifierName(ptr)) , x )))")]
    [InlineData("lda (3,s),y", "InstructionStatement(lda IndexedIndirectOperand(( NumberExpression(3) , s ) , y))")]
    [InlineData("jmp (vector)", "InstructionStatement(jmp IndirectOperand(( NameExpression(IdentifierName(vector)) )))")]
    [InlineData("lda [dp]", "InstructionStatement(lda LongIndirectOperand([ NameExpression(IdentifierName(dp)) ]))")]
    [InlineData("lda [dp],y", "InstructionStatement(lda LongIndirectOperand([ NameExpression(IdentifierName(dp)) ] , y))")]
    [InlineData("bne @loop", "InstructionStatement(bne AbsoluteOperand(NameExpression(IdentifierName(@loop))))")]
    [InlineData("mvn #1, #2", "InstructionStatement(mvn ImmediateOperand(# NumberExpression(1) , # NumberExpression(2)))")]
    [InlineData("bbr0 $12, skip",
        "InstructionStatement(bbr0 AbsoluteOperand(NumberExpression($12) , NameExpression(IdentifierName(skip))))")]
    // Parentheses around a whole operand are indirect; anything else is an expression.
    [InlineData("lda (hi + lo) * 2", "InstructionStatement(lda AbsoluteOperand(BinaryExpression("
        + "ParenthesizedExpression(( BinaryExpression(NameExpression(IdentifierName(hi)) + "
        + "NameExpression(IdentifierName(lo))) )) * NumberExpression(2))))")]
    public void EveryOperandFormParses(string line, string shape)
    {
        Assert.Equal(shape, SyntaxDump.Shape(Statement(line)));
        Assert.Empty(Errors(line));
    }

    [Theory]
    [InlineData("fill_page:", SyntaxKind.LabeledLine)]
    [InlineData("@loop:   sta (ptr),y", SyntaxKind.LabeledLine)]
    [InlineData("ptr:     .res 2", SyntaxKind.LabeledLine)]
    [InlineData(".data ptr: .word", SyntaxKind.DataDeclaration)]
    [InlineData(".data buffer: .byte[64]", SyntaxKind.DataDeclaration)]
    [InlineData(".data gradient: .byte 40, $e0, 0", SyntaxKind.DataDeclaration)]
    [InlineData(".data row_lo: .byte[] {", SyntaxKind.DataDeclaration)]
    [InlineData(".data handlers: .addr[16] {", SyntaxKind.DataDeclaration)]
    [InlineData(".data lut: .byte[4] { 1, 2, 4, 8 }", SyntaxKind.DataDeclaration)]
    [InlineData(".data header: .type RomHeader { title = \"NT65\" }", SyntaxKind.DataDeclaration)]
    [InlineData(".data sprites: .type Sprite[] { { x = 1 }, { x = 2 } }", SyntaxKind.DataDeclaration)]
    [InlineData(".data tiles: .incbin \"tiles.bin\"", SyntaxKind.DataDeclaration)]
    [InlineData(".data vectors {", SyntaxKind.DataDeclaration)]
    [InlineData("SCREEN = $0400", SyntaxKind.ConstantDeclaration)]
    [InlineData("@n = 1", SyntaxKind.ConstantDeclaration)]
    [InlineData(".byte 1, 2, $ff, 'A', \"text\"", SyntaxKind.DataDirective)]
    [InlineData(".strz \"hello\"", SyntaxKind.DataDirective)]
    [InlineData(".cpu 65816", SyntaxKind.CpuDirective)]
    [InlineData(".cpu 65c02", SyntaxKind.CpuDirective)]
    [InlineData(".segment ZP2: zp", SyntaxKind.SegmentDeclaration)]
    [InlineData(".segment ZP2: zp, dp = $2100", SyntaxKind.SegmentDeclaration)]
    [InlineData(".segment WRAM: abs, bank = $7e", SyntaxKind.SegmentDeclaration)]
    [InlineData(".segment CODE {", SyntaxKind.SegmentBlock)]
    [InlineData(".segment RODATA", SyntaxKind.SegmentRegion)]
    [InlineData(".proc fill_page {", SyntaxKind.ProcDeclaration)]
    [InlineData(".proc render: a16, i8 -> a8, i8 {", SyntaxKind.ProcDeclaration)]
    [InlineData(".proc nmi: a?, i? {", SyntaxKind.ProcDeclaration)]
    [InlineData(".proc hud: a8, i16, dp = $2100, dbr = $7e {", SyntaxKind.ProcDeclaration)]
    [InlineData(".proc show: a*, i*, e*, near, inline .strz {", SyntaxKind.ProcDeclaration)]
    [InlineData(".proc skip: far, inline 2 {", SyntaxKind.ProcDeclaration)]
    [InlineData(".proc CHROUT = $FFD2: a8, i8", SyntaxKind.ExternProcDeclaration)]
    [InlineData(".proc alias = other", SyntaxKind.ExternProcDeclaration)]
    [InlineData(".scope {", SyntaxKind.ScopeDeclaration)]
    [InlineData(".scope gfx {", SyntaxKind.ScopeDeclaration)]
    [InlineData(".export fill_page, SCREEN, Player", SyntaxKind.ExportDirective)]
    [InlineData(".import _printf: proc(a8, i16)", SyntaxKind.ImportDirective)]
    [InlineData(".import tick: proc(-> a8)", SyntaxKind.ImportDirective)]
    [InlineData(".import reset: proc()", SyntaxKind.ImportDirective)]
    [InlineData(".import zp_scratch: zp, far_table: far", SyntaxKind.ImportDirective)]
    [InlineData(".import VIC_BORDER = $D020", SyntaxKind.ImportDirective)]
    [InlineData(".import raw", SyntaxKind.ImportDirective)]
    [InlineData("   ; just a comment", SyntaxKind.BlankLine)]
    [InlineData(".macro set16(dest: operand, value) {", SyntaxKind.MacroDeclaration)]
    [InlineData(".macro note(pitch: const, frames: const = 1) {", SyntaxKind.MacroDeclaration)]
    [InlineData(".macro push(regs: list(one(a, x, y))) {", SyntaxKind.MacroDeclaration)]
    [InlineData(".macro wrap(body: block, otherwise: block = {}) {", SyntaxKind.MacroDeclaration)]
    [InlineData(".macro add16(dest: operand, value: operand): a8 {", SyntaxKind.MacroDeclaration)]
    [InlineData("set16!(ptr, SCREEN)", SyntaxKind.MacroCall)]
    [InlineData("set16!({buf,x}, $1234)", SyntaxKind.MacroCall)]
    [InlineData("mov16!(ptr, {(src),y})", SyntaxKind.MacroCall)]
    [InlineData("note!(C4, frames = 8)", SyntaxKind.MacroCall)]
    [InlineData("push!(a, x, y)", SyntaxKind.MacroCall)]
    [InlineData("times_x!(8) {", SyntaxKind.MacroCall)]
    [InlineData("body", SyntaxKind.BlockSplice)]
    [InlineData("tune: note!(C4)", SyntaxKind.LabeledLine)]
    // Conditions, repetitions, annotations and the declarative constructs.
    [InlineData(".if .defined(DEBUG) {", SyntaxKind.IfDirective)]
    [InlineData(".repeat 8, i {", SyntaxKind.RepeatDirective)]
    [InlineData(".each handlers, h {", SyntaxKind.EachDirective)]
    [InlineData("    .assert .sizeof(table) == 32, \"table must be 32 bytes\"", SyntaxKind.AssertDirective)]
    [InlineData("    .error \"unsupported configuration\"", SyntaxKind.ErrorDirective)]
    [InlineData(".next @move, @fire", SyntaxKind.NextDirective)]
    [InlineData("    .next gfx::init, ::top", SyntaxKind.NextDirective)]
    [InlineData("    .next ?", SyntaxKind.NextDirective)]
    [InlineData("    .patch @op", SyntaxKind.PatchDirective)]
    [InlineData("boss: .type Actor { x = 100 }", SyntaxKind.LabeledLine)]
    [InlineData(".enum Color {", SyntaxKind.EnumDeclaration)]
    [InlineData(".struct Point {", SyntaxKind.StructDeclaration)]
    [InlineData(".union Value {", SyntaxKind.UnionDeclaration)]
    [InlineData(".charmap screen {", SyntaxKind.CharmapDeclaration)]
    [InlineData(".list handlers {", SyntaxKind.ListDeclaration)]
    [InlineData(".func rgb15(r, g, b) = r | (g << 5) | (b << 10)", SyntaxKind.FuncDeclaration)]
    [InlineData("pos: .type Point[4]", SyntaxKind.LabeledLine)]
    [InlineData("colors: .word[16]", SyntaxKind.LabeledLine)]
    [InlineData("    .align 256", SyntaxKind.DataDirective)]
    [InlineData("    .incbin \"sprites.bin\", 64, 32", SyntaxKind.DataDirective)]
    [InlineData("    .lobytes first, second", SyntaxKind.DataDirective)]
    [InlineData("    .ensure a16, i8", SyntaxKind.EnsureDirective)]
    [InlineData("    .frame locals: Locals", SyntaxKind.FrameDirective)]
    public void EveryCoreItemParses(string line, SyntaxKind kind)
    {
        Assert.Equal(kind, Statement(line).Kind);
        Assert.Empty(Errors(line));
    }

    [Theory]
    [InlineData(".proc {", "expected a routine name")]
    [InlineData(".proc p", "expected `{`, or `= address` for a routine with no body")]
    [InlineData(".proc p: 5 {", "expected a processor-state item")]
    [InlineData(".proc p: a9 {", "`a9` is not a processor-state item")]
    [InlineData(".proc p: near = 1 {", "`near` is not a processor-state item")]
    [InlineData(".scope gfx", "expected `{`")]
    [InlineData(".segment \"X\"", "a segment name is written without quotes: `.segment X`")]
    [InlineData(".segment X: word", "expected `zp`, `abs` or `far`")]
    [InlineData(".segment {", "expected a segment name")]
    [InlineData(".segment X: zp, page = 1", "expected `dp`, `bank` or `mirrors`")]
    [InlineData(".rodata {", "`.rodata` is written `.segment RODATA`")]
    [InlineData(".tag Point", "`.tag T` is written `.type T`, and `.tag T, n` is `.type T[n]`")]
    [InlineData(".data {", "`.data` declares data, and needs a name: the segment is written `.segment DATA`")]
    [InlineData(".data x .byte", "expected `:` and what the data is, or `{` for mixed data")]
    [InlineData(".data x: lda", "expected what the data is: a number such as `.byte` or `.word`, an address such as `.addr`, `.type T`, or bytes such as `.incbin`")]
    [InlineData(".data x: .byte[4 {", "expected `]`")]
    [InlineData(".data x: .byte[4] 1, 2, 3, 4", "the values of an array go in braces: `.byte[n] { 1, 2 }`")]
    [InlineData(".data x: .type Point 1", "a record's values go in braces: `.type T { member = value }`")]
    [InlineData(".data x: .byte {", "values in a body need a count: `.byte[] {` counts them")]
    [InlineData(".cpu 6510", "expected `6502`, `65sc02`, `r65c02`, `65c02` or `65816`")]
    [InlineData(".frame", "expected a name for the frame")]
    [InlineData(".frame locals Locals", "expected `:` and the struct the frame is laid out as")]
    [InlineData(".frobnicate 1", "unknown directive `.frobnicate`")]
    [InlineData(".word .frobnicate(1)", "`.frobnicate` is not a function")]
    [InlineData(".word .sizeof", "expected `(` after `.sizeof`")]
    [InlineData(".word (1 + 2", "expected `)`")]
    [InlineData(".import x: quad", "expected `zp`, `abs`, `far` or `proc(...)`")]
    [InlineData(".export", "expected a name to export")]
    [InlineData("lda #1 junk", "unexpected `junk`")]
    [InlineData("label: .proc p {", "`.proc` may not follow a label")]
    [InlineData("label: rubbish", "expected an instruction, a data directive or a macro call after a label")]
    [InlineData("gfx::init", "expected a label, a constant, an instruction or a directive")]
    [InlineData(".macro {", "expected a macro name")]
    [InlineData(".macro m {", "expected `(` and the parameters")]
    [InlineData(".macro m(p: word) {", "expected `expr`, `const`, `ident`, `operand`, `one(...)`, `list(...)` or `block`")]
    [InlineData(".macro m(p: one) {", "expected `(`")]
    [InlineData(".macro m(p: one(a, 3)) {", "expected a word")]
    [InlineData(".macro m(1) {", "expected a parameter name")]
    [InlineData("m!", "expected `(` and the arguments")]
    [InlineData("m!({buf,x", "expected `}`")]
    [InlineData("m!(a", "expected `)`")]
    [InlineData(".if {", "expected an expression")]
    [InlineData(".repeat 8, {", "expected the name to bind")]
    [InlineData(".each handlers, h", "expected `{`")]
    [InlineData(".assert 1 == 1, 3", "expected the message, in quotes")]
    [InlineData(".error nope", "expected the message, in quotes")]
    [InlineData(".else {", "`.else` continues an `.if`, and belongs after its `}`")]
    public void UnreadableLinesAreReportedOnce(string line, string message) => Assert.Equal([message], Errors(line));

    /// <summary>A <c>}</c> alone, and the lines that continue a construct after it.</summary>
    [Theory]
    [InlineData("}", SyntaxKind.BlockCloseLine)]
    [InlineData("} .else {", SyntaxKind.ElseDirective)]
    [InlineData("} .elseif LEVEL > 2 {", SyntaxKind.ElseIfDirective)]
    [InlineData("} handlers {", SyntaxKind.BlockContinuation)]
    public void ALineThatClosesABlockParses(string line, SyntaxKind kind)
    {
        // A continuation line opens a block of its own, which needs closing in turn.
        var tree = SyntaxTree.Parse("test.nt65", ".scope {\n" + line + (line.EndsWith('{') ? "\n}" : ""));
        Assert.Equal(kind, Statement(tree, 1).Kind);
        Assert.Empty(tree.Diagnostics);
    }

    /// <summary>
    /// A statement is only its own tokens. The line break that ends it, the tokens it could not
    /// take and the <c>.export</c> that exports what it declares are the line's, so a statement
    /// written inside another reads the same as one written on a line of its own.
    /// </summary>
    [Fact]
    public void TheLineHoldsWhatIsNoPartOfTheStatement()
    {
        var left = Line("lda #1 junk");
        Assert.Equal("lda #1", left.Statement.GetText());
        Assert.Equal("junk", left.SkippedTokens?.GetText());
        Assert.Equal(["junk"], left.SkippedTokens?.Tokens.Select(token => token.Text));
        Assert.Equal(SyntaxKind.EndOfLine, left.EndOfLineToken.Kind);
        Assert.Null(left.ExportKeyword);

        var exported = Line(".export .proc main {");
        Assert.Equal(".export", exported.ExportKeyword?.Text);
        Assert.Equal(".proc main {", exported.Statement.GetText());
        Assert.Null(exported.SkippedTokens);
    }

    /// <summary>A bad line is one line's problem: the next one parses as if nothing happened.</summary>
    [Fact]
    public void ABadLineDoesNotDisturbTheNextOne()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".frobnicate\nlda #1\n");
        Assert.Single(tree.Diagnostics);
        var bad = Assert.IsType<ErrorLineSyntax>(Statement(tree, 0));
        Assert.Equal([".frobnicate"], bad.Tokens.Select(token => token.Text));
        Assert.Equal("InstructionStatement(lda ImmediateOperand(# NumberExpression(1)))", SyntaxDump.Shape(Statement(tree, 1)));
    }

    /// <summary>
    /// A line's syntax depends on the kind of block around it: the same `name = expr` is a
    /// member inside an `.enum` and a constant declaration outside one.
    /// </summary>
    [Fact]
    public void TheEnclosingBlockDecidesHowALineReads()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".enum Color {\ngreen = 5\n}\ngreen = 5\n");
        Assert.Equal(SyntaxKind.EnumMember, Statement(tree, 1).Kind);
        Assert.Equal(SyntaxKind.ConstantDeclaration, Statement(tree, 3).Kind);
        Assert.Empty(tree.Diagnostics);
    }

    /// <summary>
    /// A diagnostic about something the line does not have stands where that something
    /// would go, just past the last real token. Trailing whitespace and a trailing comment
    /// are no part of the line's meaning, so neither moves the caret: it stays put as
    /// spaces are typed, rather than sliding right or landing past a comment.
    /// </summary>
    [Theory]
    [InlineData("lda #")]
    [InlineData("lda #    ")]
    [InlineData("lda #\t")]
    [InlineData("lda #   ; note")]
    public void AMissingPieceIsReportedWhereItWouldHaveBeenWritten(string line)
    {
        var diagnostic = Assert.Single(SyntaxTree.Parse("test.nt65", line + "\n").Diagnostics);
        Assert.Equal("expected an expression", diagnostic.Message);

        // Column 6 is just past the `#`, whatever follows it.
        Assert.Equal(6, diagnostic.Span.StartColumn);
        Assert.Equal(6, diagnostic.Span.EndColumn);
    }

    /// <summary>Whitespace between tokens is trivia, so an operand written apart is still read.</summary>
    [Fact]
    public void SpaceBetweenTokensDoesNotChangeWhatALineMeans()
    {
        Assert.Empty(Parse("lda #      1").Diagnostics);
        Assert.Equal(SyntaxDump.Shape(Statement("lda #1")), SyntaxDump.Shape(Statement("lda #      1")));
    }

    /// <summary>
    /// One line's tree. A line that opens a block is given the <c>}</c> it wants, so what
    /// comes back is the parser's alone and not the block layer's.
    /// </summary>
    private static SyntaxTree Parse(string line) =>
        SyntaxTree.Parse("test.nt65", line.TrimEnd().EndsWith('{') ? line + "\n}" : line);

    private static StatementSyntax Statement(string line) => Statement(Parse(line), 0);

    /// <summary>What line <paramref name="line"/> of a tree parses to, 0-based.</summary>
    private static StatementSyntax Statement(SyntaxTree tree, int line) => Line(tree, line).Statement;

    private static LineSyntax Line(string line) => Line(Parse(line), 0);

    /// <summary>Line <paramref name="line"/> of a tree, 0-based.</summary>
    private static LineSyntax Line(SyntaxTree tree, int line) =>
        tree.Root.DescendantNodes().OfType<LineSyntax>().ElementAt(line);

    /// <summary>The operand of a <c>.word</c>, which is the shortest line an expression fits on.</summary>
    private static SyntaxNode Expression(string expression) =>
        Assert.Single(Assert.IsType<DataDirectiveSyntax>(Statement(".word " + expression)).Values);

    private static string[] Errors(string line) => [.. Parse(line).Diagnostics.Select(d => d.Message)];
}
