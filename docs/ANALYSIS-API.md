# The analysis API

nt65's syntax tree is written for the two things that read it: an analyzer that wants to know what
a file says, and an editor feature that wants to know what is at the caret. It is Roslyn's design,
smaller: a full-fidelity immutable tree, a class per kind of node, fixed slots, missing tokens,
diagnostics on the nodes they are about, and a factory and a rewriter for changing one. If you know
`Microsoft.CodeAnalysis`, you know this.

Above the tree is the semantic layer, `Norristown.Semantics`: what every name in a file refers to,
what its expressions are worth, and how the files of a program see one another. It is Roslyn's
design there too — a model per file, built once and then read-only, with `LookupSymbols`,
`GetSymbolInfo` and a symbol for every declaration.

Below both is `Norristown.Processor`: which processor a program is built for — `Cpu`, `CpuNames`
for how one is written, and `ProgramCpu` for reading it off a program's `.cpu` items — and what a
mnemonic is on each: which addressing modes it has, whether it calls or returns, what it moves on
and off the stack, which registers it leaves changed. With them are `AddressSize`, how wide an
address anything here reaches, and `Define`, one constant the build gives every file. The tables
are datasheet fact and ca65's own, and the layer names nothing above itself, so the layers above
may all read it.

Everything here is in `Norristown.Core`. Every sample on the syntax half is a test in
`tests/Norristown.Tests/Syntax/AnalysisApiTests.cs` and every sample on the semantic half one in
`tests/Norristown.Tests/Semantics/AnalysisApiTests.cs`, so nothing on this page can drift from
what the API does.

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

## Changing a tree

A tree is immutable, so changing one gives another. Every node has an `Update` over its slots and a
`With<Slot>` per slot; `SyntaxFactory` builds a node from nothing; `SyntaxRewriter` is a visitor
whose methods rewrite a node's slots and call `Update`; and `ReplaceNode`, `ReplaceNodes`,
`ReplaceToken`, `ReplaceTokens` and `RemoveNode` on a node are that rewriter with one override.

```csharp
var tree = SyntaxTree.Parse("main.nt65", ".proc main {\n    lda #16 ; the mask\n    sta mask\n}\n");

// A fix: every immediate written in decimal is written in hex instead.
class Hexadecimal : SyntaxRewriter
{
    public override SyntaxNode? VisitNumberExpression(NumberExpressionSyntax node) =>
        node.Parent is ImmediateOperandSyntax && int.TryParse(node.Token.Text, out var value)
            ? node.WithToken(node.Token.WithText($"${value:x2}"))
            : node;
}

var hex = new Hexadecimal().Visit(tree.Root)!;   // .proc main {\n    lda #$10 ; the mask\n …

// A rename: one name said another way, wherever it is written.
var named = hex.DescendantTokens().Where(token => token is { Kind: SyntaxKind.Identifier, Text: "mask" });
var renamed = hex.ReplaceTokens(named, (old, _) => SyntaxFactory.Identifier("flags").WithTriviaFrom(old));

renamed.Tree        // a new tree; `tree` is still the file as it was
```

Two rules hold the rest up.

**A rewrite that changes nothing gives back what it was given** — the same objects, not an equal
copy. `Update` compares each slot against the green node already in it, so a node whose pieces all
came back unchanged is itself; that rolls up, and a rewriter with no override returns the root it
was handed. Which is why a fix can be run over a file and asked afterwards whether it did anything.

**A rewrite of one piece leaves every other character of the file alone.** A statement and
everything under it is rebuilt where it stands, out of the green nodes its pieces already hold. A
line, a block and the file are not: a line holds the tokens the lexer read rather than what they
parse to, so what moved on a line is written back as one text change and the file is parsed again
from there. So a rewrite that reaches a line or the root gives back the matching node of a **new**
tree, which `node.Tree` answers for, and the lines the change did not touch keep the nodes they had.

**Whitespace is the caller's**, because nothing else can know it. A token built by the factory
carries only the trivia it was given, so an `Identifier("flags")` on its own prints as `flags` with
nothing around it. Two ways to give it some:

- `token.WithTriviaFrom(other)` keeps what stood around the token being written over — the
  indentation of the line, the comment at the end of it — which is what a fix that swaps one token
  for another wants. `WithLeadingTrivia`, `WithTrailingTrivia` and `SyntaxFactory.Space`,
  `Whitespace` and `Comment` are there for the rest.
- `node.NormalizeWhitespace()` throws away the trivia a node carries and writes one space where two
  tokens would otherwise read as one and none where none is wanted, which is what a node built out
  of bare tokens needs: without it a built `lda #0` prints as `lda#0`.

`SyntaxFactory.SeparatedList(items)` is the one place the factory writes a space of its own: its
commas are written `, `, because a list has nowhere else to say it. `RemoveNode` takes a line with
the break that ends it and a list item with the separator written after it, and hands on whatever
stood after the last item so that what is left is not written up against the rest of the line.

What none of this does is lay a line out. `Formatter.Format(newRoot.Tree)` is the one layout an
nt65 file is written in — indentation, trailing space, the column a run of data lines shares — and
running it over the tree a rewrite gives back is what puts a built line where it belongs.

**A tag finds a piece again afterwards.** A fix that puts something into a file usually wants to
know where that something ended up — to put the caret on it, to start a rename on it, to say what
it changed. Counting characters will not answer that, because the tree that comes back is parsed
from the line as a whole. A `SyntaxAnnotation` is the answer: put one on the piece before the
rewrite, and find it again in the result.

```csharp
var tree = SyntaxTree.Parse("main.nt65", ".proc main: a8 {\n    rts\n}\n");
var widened = new SyntaxAnnotation("widened");

// A fix: a routine that says only what the accumulator is has the index registers said too.
class Widen(SyntaxAnnotation tag) : SyntaxRewriter
{
    public override SyntaxNode? VisitStateList(StateListSyntax node) =>
        node.Items is [StateFlagItemSyntax { Name.Text: "a8" } a8]
            ? node.WithItems(SyntaxFactory.SeparatedList(
                [a8, (StateItemSyntax)SyntaxFactory.StateFlagItem(SyntaxFactory.Identifier("i8"))
                    .WithAdditionalAnnotations(tag)]))
            : node;
}

var root = new Widen(widened).Visit(tree.Root)!;   // .proc main: a8, i8 {\n    rts\n}\n
root.GetAnnotatedNodes(widened).Single().Span      // where `i8` ended up: the caret goes here
```

`WithAdditionalAnnotations` and `WithoutAnnotations` are on a node and on a token, as are
`HasAnnotation`, `HasAnnotations(kind)` and `GetAnnotations(kind)`; a node also has
`GetAnnotatedNodes`, `GetAnnotatedTokens` and `GetAnnotatedNodesAndTokens`, by the annotation
itself or by a kind, and `ContainsAnnotations`, which answers without walking. An annotation is
found by *being itself* — two with the same kind and the same data are two annotations — and it is
no part of what a piece says: a tagged node writes the same text, has the same width and reports
the same diagnostics. What it is, is a different node, and that is what makes it findable.

Two rules say how long a tag lasts.

**It crosses the reparse, or it is dropped and nothing is said.** A rewrite that reaches a line
writes text and parses the file again, so the rewrite remembers every annotated piece of what it
wrote — what kind it is and where it stands in the new text — and gives the annotations back to
the piece of the same kind at the same place. Where the parser read that text another way there is
no such piece, and the annotations go. A tag on a line, on a block or on the file itself is the
same case: those are not pieces a reparse makes.

**It lasts as long as the line it is on.** An edit elsewhere in the file leaves an annotated line
alone, green node and all, so what it carries is still there; a line the edit reaches is read again
and its annotations go with the parse they were on. Roslyn does the same.

## The model of a file

`Compiler.Analyze` reads a set of files as one program and gives a `ProgramAnalysis`; the
`SemanticModel` in it for one file is what every question about that file is asked of.

```csharp
var analysis = Compiler.Analyze([new SourceFile("main.nt65", text), …], ProjectSettings.None);
var model = analysis.ModelFor("main.nt65")!;

model.Tree                 // the file, as a syntax tree
model.Diagnostics          // what is wrong with its names and constants, ordered
model.Symbols              // every name it declares, in source order
model.References           // every place a name is written, declarations included
model.FileScope            // its top level
model.Segments             // the program's segments, which address sizes come from
model.Configuration        // which `.if` branches this build takes

// What its output brings in from elsewhere, and what of its own it uses.
model.ExternalSymbols      // the symbols it names and another file declares
model.Used                 // everything its code and data name, macro bodies included
model.Brought              // what its `.use` items bring in, under the names it writes
model.Globs                // the modules a `.use module::*` brought everything of
model.Families             // a declaration written once and made once per enum member
model.FamilyAt(statement)  // the family that statement stands for, or null

// One file on its own sees no other, which is what a scratch buffer is analyzed as.
var alone = SemanticModel.Create(tree, SegmentTable.Standard);
```

A model is built once and is then read-only. Nothing it answers works anything out for the first
time — every type is laid out and every value settled while the program is evaluated — so an
editor may hold one and ask it anything from whatever thread a request arrives on.

## What a name means

Two questions, and they are different ones. *What does this name, already written, refer to?* is
answered from what binding recorded. *What could a name written here mean?* is answered by running
the binder's own lookup at a position, which is what completion asks while a line is being typed.

```csharp
// A name already written.
var reference = model.ReferenceAt(caret)!;    // the name the position is in, or null
reference.Symbol                              // what it refers to
model.SymbolAt(token)                         // the same, from a token
model.GetSymbolInfo(name)                     // the same, from a NameExpressionSyntax
model.ReferencesTo(reference.Symbol)          // every place this file writes it
model.DeclaredBy(header, on)                  // what a declaration line declares

// A name being typed, which is text at a position rather than a node.
model.ScopeAt(caret)                          // the innermost scope there
model.GetSymbolInfo(caret, ["cells"])         // SymbolInfo: a symbol, a module, or neither
model.GetSymbolInfo(caret, ["Point", "y"])    // a path, part by part
model.GetSymbolInfo(caret, ["hw"], fromRoot: true)   // a path from the modules' root
model.LookupNames(caret)                      // every name writable there, and what each means
model.LookupSymbols(caret)                    // the symbols among them
model.LookupSymbols(caret, "EDGE")            // the candidates for one spelling, the winner first
```

`SymbolInfo` is a symbol or a module path, because a module is no declaration: `hw::vic` is the way
to a name rather than a name. `SymbolInfo.None` is neither, and `IsNone` says so.

The order `LookupNames` answers in is the order the binder tries: the scopes from the caret out to
the file, the nearest first, then what `.use` brought in, then the defines, then what a
`.use module::*` brings in. Where two share a name the first is what the name means — so
`LookupSymbols(position, name)` lists the candidates with the one that binds at the front. A name a
`.use … as` renamed answers to the name written here, and a cheap local to its `@` name. This is
the same code the binder resolves the file with, so what an editor offers and what the line will
mean cannot disagree.

## What a declaration is

```csharp
point.Kind                  // SymbolKind.Struct
point.KindText              // "structure"; KindPhrase is "a structure"
point.Name                  // "Point", without the `@` of a cheap local; DisplayName has it
point.QualifiedName         // the scopes around it, joined with `::`
point.PathName              // the same with the module in front, as another module writes it
point.FlatName              // the name in the output, joined with `__`
point.OutputName            // the linker's name for it: its `as` name, or the flat one
point.Module                // the module it is declared in
point.Tree, point.NameSpan  // the file, and where the name is written
point.DeclarationSpan       // the same as a diagnostic names it
point.Size, point.Count     // how many bytes it stands for, and how many elements they are
point.Value                 // what it is worth; for a member of a layout, its offset
point.Body                  // the scope it opens, for a routine, a scope or a type
```

The rest of a symbol says what kind of thing it is and how it is reached: `IsExported`,
`LinkerName`, `ExportSize`, `ExportSpan`, `IsCheapLocal`, `IsDefine`, `IsConfig`, `IsEnumMember`,
`FollowsPrevious`, `IsCyclic`, `IsAddress`, `IsLayout`, `IsReachableByPath`, `Segment`,
`AddressSize`, `AddressSizeIn(tree)`, `Routine`, `Scope`, `Type`, `TypeExpression`,
`ValueExpression`, `Data`, `Definition`, `Items`, `Entries`, `PreviousMember`, `Signature`,
`MacroSignature`, `StateDeclaration`, `Parameters`, `ParameterSymbols`, `Parameter`, `Bound`,
`IsSiblingOf(other)`, and `Calls` and `Uses` for a macro — read-only views of what its body calls
and names.

A `Scope` is one level of naming, and lookup runs from the inside out:

```csharp
scope.Kind, scope.Name, scope.Parent, scope.Owner, scope.Module
scope.Symbols                    // everything declared here, in source order
scope.Lookup("Point")            // from here outward to the file
scope.FindMember("Point")        // here only
scope.LookupCheapLocal("loop")   // cheap locals live in a namespace of their own
scope.FindCheapLocal("loop")
scope.Enclosing(ScopeKind.Proc)  // the routine this is written in, or null
scope.NearestNamed()             // the nearest scope with a name
scope.IsReachableByPath          // whether `::` can reach what is declared here
```

## What an expression is worth

```csharp
model.ValueOf(expression)          // a Value: a number, a string, or unknown
model.SymbolOf(expression)         // the symbol a written name stands for
model.RoomFor(directive)           // a DataSize: the bytes a data directive takes, and elements
model.ElementsOf(directive)        // how many its count declares, and how many its values come to
model.ElementsOf(directive).Given
model.AddressSizeOf(expression)    // zp, abs or far
model.BytesOf(operand)             // the bytes it becomes, for a literal or mapped text
model.ItemsOf(operand)             // a list's items, or null
model.Check(expression, problems)  // evaluate and report, for an operand nobody else evaluates
```

An address is the linker's to say, so a label has no value; how many bytes a routine takes is
layout's, and `ValueOf(expression, on, spans)` takes a `spans` function from a caller that has laid
the file out. The model stays read-only either way: what only layout knows is handed in rather than
kept.

## Macros and repetitions

A macro body is read once and written out at every call, and a `.repeat` or `.each` body once per
turn. An `Expansion` is which writing of those lines is being asked about, and every question that
could be answered differently on different turns takes one.

```csharp
model.MacroAt(call)                 // the macro a call names
model.InvocationAt(call)            // what that call gives each parameter
var on = Expansion.Of(null, call, (BlockSyntax)plot.Definition!);

model.ArgumentFor(parameter, on)    // what the parameter was given at this expansion
model.GivenAt(parameter, on)        // the same, with the level it was written at
model.EnumOf(parameter.Accepts)     // the enum a parameter's kind names, when it names one
model.MemberFor(argument, caller)   // the member of it an argument names, bare or by its path
model.MemberOf(kind, written, caller) // the same, for one expression written for such a kind
model.BindingsOf(on)                // every name bound here and at the levels around it
model.BindingsOf(null)              // null: outside an expansion nothing is bound
```

## The program the files are part of

```csharp
var program = analysis.Program;

program.Files                       // one model per file
program.Segments                    // the program's segments
program.Diagnostics                 // everything wrong with its names and constants
program.Symbols                     // what each file may name in the others
program.Current(symbol)             // what a symbol stands for now, after an edit elsewhere
program.ReferencesTo(symbol)        // every place it is written, in every file
program.ReferencesTo([a, b])        // the same for several, which one name written two ways needs

program.Symbols.Modules             // every module
program.Symbols.ModuleNamed("hw::vic")
program.Symbols.IsModulePath("hw")  // whether a path starts a module's name
program.Symbols.Member(module, "BORDER")
program.Symbols.ModulesExporting("BORDER")
program.Symbols.Defines             // what the build gives every file; Define(name) is one

ProgramModel.Create(trees, SegmentTable.Build(trees, []));   // names and constants, laid out by nobody
```

`ReferencesTo` answers in file and source order, and compares what each reference stands for *now*:
a file kept from before an edit elsewhere names what the edited file declared then, and
`Current` is what that declaration is today. Find-references, rename and the language server's
call hierarchy are all this one method.

Analysis is incremental. `Compiler.Analyze(files, project, previous)` reads only the files that
changed and the files a change reaches; `ProgramAnalysis.Reanalyzed` says how many were read and
`WholeProgram` why all of them were, when all of them were. What it answers is exactly what
analyzing from scratch answers — that is a test, replayed over hundreds of edits.

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
