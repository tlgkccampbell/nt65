# The analysis API

nt65's syntax tree is written for the two things that read it: an analyzer that wants to know what
a file says, and an editor feature that wants to know what is at the caret. It is Roslyn's design,
smaller: a full-fidelity immutable tree, a class per kind of node, fixed slots, missing tokens, and
diagnostics on the nodes they are about. If you know `Microsoft.CodeAnalysis`, you know this.

Everything here is `Norristown.Syntax`, in `Norristown.Core`. Every sample below is a test in
`tests/Norristown.Tests/Syntax/AnalysisApiTests.cs`, so nothing on this page can drift from what
the API does.

## Parsing a tree

```csharp
var tree = SyntaxTree.Parse("main.nt65", ".proc main: a8, i8 {\n    lda #1\n    rts\n}\n");

tree.LineCount                             // 5: a text with n line breaks has n + 1 lines
tree.Root.ToFullString()                   // the file's text, exactly
tree.GetLineIndex(tree.GetPosition(1, 4))  // 1: line and character to offset, and back

// An edit gives a new tree, keeping the green nodes of every line it does not touch.
var edited = tree.WithChange(new TextChange(tree.GetPosition(1, 9), 1, "2"));
```

A tree is immutable and cheap to edit: a file is lexed a line at a time and each line is parsed on
its own, so `WithChange` re-lexes only the lines the change touches and re-parses only those, and
lines whose enclosing block changed. That is why an editor keeps a tree and edits it rather than
parsing again.

`tree.Path`, `tree.Text`, `tree.LineStarts`, `tree.GetPosition(line, character)`,
`tree.GetLineIndex(position)` and `tree.GetSpan(textSpan)` are how offsets, lines and the `Span`s a
diagnostic carries convert into one another. `tree.GetLine(i)` is a line as a node of the tree.

## Nodes, tokens and trivia

A tree is nodes; a node's children are nodes and tokens; a token's whitespace and comments are
trivia. The root is a `FileSyntax` of lines and blocks; a `LineSyntax` holds the `.export` that
exports what the line declares, the `StatementSyntax` its tokens parse to, whatever the statement
could not take, and the line break. A statement is only its own tokens, wherever it is written.

```csharp
var tree = SyntaxTree.Parse("main.nt65", ".proc main {   ; the entry point\n    rts\n}\n");
var proc = tree.Root.DescendantNodes().OfType<ProcDeclarationSyntax>().Single();

proc.Kind                   // SyntaxKind.ProcDeclaration
proc.Name.Text              // "main"
proc.OpenBraceToken.Text    // "{"
proc.GetText()              // ".proc main {"

// A comment belongs to the token before it, and the indentation of a line to its first.
var brace = proc.OpenBraceToken;
brace.TrailingTrivia.Select(trivia => trivia.Kind)      // WhitespaceTrivia, CommentTrivia
brace.TrailingTrivia[1].Text                            // "; the entry point"
tree.GetLine(1).Tokens[0].LeadingTrivia.Single().Text    // "    "

// Span is the text; FullSpan takes in the trivia around it.
brace.Span      // TextSpan(11, 1)
brace.FullSpan  // TextSpan(11, 21)
```

A `SyntaxNode` has `Kind`, `Parent`, `Tree`, `Position`, `Span`, `FullSpan`, `LineIndex`,
`ToFullString()` and `GetText()`. `Span` is the node's text without the trivia around it and
without the line break that ends it — what an editor selects or reveals. `SyntaxToken` and
`SyntaxTrivia` are read-only structs with the same vocabulary; they are values, so handing one
around allocates nothing, and a `default` one is a token of nothing.

Children come as `ChildNodes`, `ChildTokens` or `ChildNodesAndTokens()` — the last in source order,
as `SyntaxNodeOrToken`s, which is usually the one you want. `DescendantNodes()`,
`DescendantTokens()` and `DescendantNodesAndTokens()` are the same all the way down, a parent
before its children and siblings in source order.

## Fixed slots and missing tokens

Each kind has a slot for each piece it is written with, in source order, and a required piece stands
in its slot whether or not the source wrote it. So a required property is never null, and what you
ask about a piece the source left out is `IsMissing`.

```csharp
var tree = SyntaxTree.Parse("main.nt65", ".proc main\n    rts\n}\n");
var proc = tree.Root.DescendantNodes().OfType<ProcDeclarationSyntax>().Single();

// Required, so never null; missing, so no text and no width, placed where it belongs.
proc.OpenBraceToken.IsMissing   // true
proc.OpenBraceToken.Text        // ""
proc.OpenBraceToken.Span        // TextSpan(10, 0): where the `{` belongs

// The node's own span is its text, so a missing token never stretches it.
proc.Span             // TextSpan(0, 10)
proc.ToFullString()   // ".proc main"

// Nullable means the source wrote no signature at all, which is a different thing.
proc.Signature        // null
```

A nullable property means one thing: the piece belongs to a part of the line the source did not
write at all — a signature, an instruction's operand, the `as` of a `.use`. A piece the line always
has a place for is never null.

This matters, because in an editor a half-written file is the normal case. What follows from it:

- A symbol is never declared from a missing name. Ask `IsMissing` before treating a name token as a
  name.
- Text and widths are safe without asking: a missing token has empty text and zero width, so loops
  that concatenate or measure are unaffected. It is the loops that *count* tokens, or take
  `ChildTokens[^1]`, or match `is [var only]`, that have to allow for one — `ChildTokens` includes
  missing tokens, as Roslyn's does.
- `FindToken` never answers with a missing token: nothing is ever written in one.
- An `ErrorExpression` is the same idea where a node belongs: it holds nothing, and its `Kind` is
  how you see it.

## Lists and separators

A list slot reads as `SyntaxList<T>`, `SeparatedSyntaxList<T>` or `SyntaxTokenList`. They are
structs over the node the items hang from, so asking for one costs nothing, and a slot with no items
reads as the empty list rather than null.

```csharp
var tree = SyntaxTree.Parse("main.nt65", ".proc main: a8, i8, native {\n    rts\n}\n");
var proc = tree.Root.DescendantNodes().OfType<ProcDeclarationSyntax>().Single();

var items = proc.Signature!.Entry.Items;               // SeparatedSyntaxList<StateItemSyntax>
items.Count                                            // 3
items.SeparatorCount                                   // 2
items.Select(item => item.GetText())                   // "a8", "i8", "native"
items.GetSeparators().Select(comma => comma.Text)      // ",", ","
items.GetWithSeparators().Select(p => p.GetText())     // "a8", ",", "i8", ",", "native"
```

Nearly every list in the grammar is comma-separated, and an editor needs the commas as much as the
items: adding an argument, deleting an item with its comma, counting commas up to the caret.
`SeparatorCount` is one fewer than `Count`, or as many when the source wrote a trailing separator.
The lists give `Count`, an indexer, `Slice` (so C# slice patterns work) and enumeration.

`SyntaxTokenList` is also what a line's own tokens read as: `tree.GetLine(i).Tokens` is a view over
the line, and walking it makes each token as it is asked for without building an array or making the
line's statement. So a pass over every token of a file — which is what a lexical question about the
whole of it needs — costs no allocation, and its `foreach` allocates no enumerator either.

Items are always nodes, never bare tokens: a `.func`'s parameters are `ParameterSyntax`, and a
`keeps`'s registers and a `one(...)`'s words are `IdentifierNameSyntax`. That is what an analyzer
wants, because the symbol a name refers to is looked up through its node.

## Names

A name is a path. `NameExpressionSyntax` is an optional leading `::` and a separated list of
`IdentifierNameSyntax` parts, each a name token and the `[i]` after it when it has one.

```csharp
var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda hw::vic::border\n    lda count\n}\n");
var names = tree.Root.DescendantNodes().OfType<NameExpressionSyntax>().ToList();

names[0].Names.Select(name => name.Text)   // "hw", "vic", "border"
names[0].SimpleName                        // null: it is a path
names[0].LastPart!.Name.Text               // "border": what the path stands for

// One part and nothing before it is one name, which is the common case.
names[1].SimpleName!.Value.Text            // "count"
```

`Names`, `SimpleName` and `LastPart` leave out a part whose name token is missing, so a path being
typed never shows you a hole. `GlobalToken` is the leading `::` of a name written from the top
level, `IsIndexed` says whether any part has an `[i]`, and `Parts` is the list itself when you want
the indexes too.

## Navigation

```csharp
var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda #1\n    rts\n}\n");
var caret = tree.GetPosition(1, 9);

var token = tree.Root.FindToken(caret);      // the token the caret is written in
token.Kind                                   // SyntaxKind.NumberLiteral
token.Parent                                 // the NumberExpressionSyntax it is a piece of
token.GetPreviousToken()!.Value.Text         // "#"
token.GetNextToken()!.Value.Kind             // SyntaxKind.EndOfLine
tree.GetLine(1).GetFirstToken()!.Value.Text  // "lda"

tree.Root.FindNode(new TextSpan(0, 5))       // the innermost node holding the whole span
```

The boundary semantics are worth knowing, because they are exactly what an editor asks about:

- **`FindToken(position)`** gives the token whose *full* span holds the position. The whitespace and
  comment after a token belong to it, and the indentation before a line's first token belongs to
  that token, so a caret in either finds the token the trivia is written beside, as in Roslyn. At
  the very end of a node, where nothing is written, the answer is the node's last token.
- **`FindNode(span)`** gives the innermost node holding the whole span, or the node itself when no
  child of it does. An empty span is a caret rather than a selection: it belongs to what is written
  after it, not to what ends where it stands.
- **`FindTrivia(position)`** gives the whitespace or comment the position is in, or null when it is
  in a token's own text.
- **`GetFirstToken()`/`GetLastToken()`** skip the tokens that write nothing unless you pass
  `includeZeroWidth: true`, and answer null when there are none.
- **`GetNextToken()`/`GetPreviousToken()`** walk the same tokens as `DescendantTokens()`, missing
  ones included, across line boundaries, and answer null at either end of the file.
- **`Ancestors()`**, **`AncestorsAndSelf()`** and **`FirstAncestorOrSelf<T>()`** go the other way.

Everything that answers "there is none" answers null rather than a default token, so nothing has to
know that a token with no parent means nothing was found.

## Visitors and walkers

`SyntaxVisitor` and `SyntaxVisitor<TResult>` have a method per kind, and every node's `Accept`
dispatches to its own. `SyntaxWalker` is the visitor that goes on down the tree: a node whose method
you do not override has its children visited, so overriding one kind still sees everything below it,
and an override keeps that by calling `base.VisitXxx(node)`.

```csharp
private sealed class Mnemonics : SyntaxWalker
{
    public List<string> Found { get; } = [];

    public override void VisitInstructionStatement(InstructionStatementSyntax node)
    {
        Found.Add(node.Mnemonic.Text);
        base.VisitInstructionStatement(node);
    }
}

var counter = new Mnemonics();
counter.Visit(tree.Root);
counter.Found;   // "lda", "sta", "rts"
```

A walker is the right tool when a feature is about a few kinds among many. A plain
`SyntaxVisitor` is the right tool when a component is about all of them at once, one method per
kind: the binder, the emitter and the code layout each hand a statement to a nested private one,
and a kind with no method there is a kind that asks for nothing. A `switch` is still the right
tool for a handful of cases that answer with a value, and for a dispatch whose arms turn on
pattern guards and the order they are written in.

## Diagnostics on the tree

A node and a token carry the diagnostics they are about, and every node knows without walking
whether anything under it has one.

```csharp
var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda (1\n}\n");

tree.Root.ContainsDiagnostics   // true, answered from a flag
tree.Root.GetDiagnostics()      // the same list as tree.Diagnostics

var instruction = tree.Root.DescendantNodes().OfType<InstructionStatementSyntax>().Single();
instruction.ContainsDiagnostics               // true
var reported = instruction.GetDiagnostics().Single();
reported.Message                              // "expected `)`"
reported.Span                                 // main.nt65(2, 11-11): where the `)` belongs

// It belongs to the token standing where the `)` was not written.
var missing = instruction.DescendantTokens().Single(token => token.IsMissing);
missing.Kind                                  // SyntaxKind.CloseParen
missing.GetDiagnostics().Single()             // the same diagnostic
```

Where a diagnostic sits is one rule: over the token the parser was looking at, or, where the line
ran out, at the end of the last token the source does have, ahead of the whitespace and the comment
after it. That is where the missing piece belongs, and a caret there neither drifts right as
trailing spaces are typed nor lands past a trailing comment. So the place to write a piece an editor
is offering to insert is the *diagnostic's* span, not the missing token's own: the token sits after
the trivia, which is behind the comment.

A diagnostic may carry a `Fix`, which says what change its message names without holding the edit. A
missing bracket carries `FixKind.MissingPiece` with the bracket's text, which is what the language
server's "Write the `{`" code action applies.

`tree.Diagnostics` is the whole file's — lexical errors, parse errors, and the errors about the
braces over the lines — ordered by line and column. A line answers for everything written on it, and
a block and the root for every line under them, so the root's answer is the file's. Nothing is
walked where `ContainsDiagnostics` says there is nothing to find, which is what makes asking cheap
on every keystroke.

## Adding a node kind

Both trees come from one table, `src/Norristown.Core/Syntax/Syntax.xml`, after Roslyn's own. A kind
is one block in it:

```xml
<Node Name="FrameDirectiveSyntax" Base="StatementSyntax">
  <Kind Name="FrameDirective"/>
  <TypeComment>
    <summary><c>.frame locals: Locals</c>: a name, and the struct the stack is laid out as.</summary>
  </TypeComment>
  <Field Name="Keyword" Type="SyntaxToken">
    <PropertyComment>
      <summary>The <c>.frame</c> that starts the line.</summary>
    </PropertyComment>
    <Kind Name="Directive"/>
  </Field>
  <!-- one Field per piece, in source order -->
</Node>
```

The table's own header says what every element and attribute means. The short version: a `<Field>`
is a piece of the node, read from its slot, and `Optional="true"` means the piece belongs to a part
of the line that may be absent altogether — anything else is required and stands in its slot as a
missing token when the source leaves it out. A `<Member>` is a property that is not a piece of the
node, with a `<Read>` expression. A hand-written half of a `partial` class holds what the table
cannot say.

`src/Norristown.SyntaxGenerator` is an incremental source generator that `Norristown.Core`
references as an analyzer, with the table as an additional file. So the way to work is: **change the
table, build, fix what the compiler points at.** Nothing is run by hand and nothing is checked in,
so nothing can be stale. A table the generator cannot read is diagnostic `NT1001` against
`Syntax.xml`.

What it writes lands on disk under `src/Norristown.Core/Generated`, one file per type, git-ignored,
to be read and grepped like any other code: the red class with a property per slot, the green class
with a field per slot, `CreateRed`, `Accept`, and the two visitors. Then teach the parser to build
it, and `ShapeTests` holds the result to the row you wrote.

## Where the rest is

- [The design](../DESIGN.md) defines the language the tree is of.
- [The syntax API](SYNTAX-API.md) is the record of how the tree came to be this shape, and which
  choices were made on purpose.
- `src/Norristown.LanguageServer` is the API's first real consumer, and the best place to read a
  feature written against it end to end.
