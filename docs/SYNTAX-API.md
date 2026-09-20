# The syntax API: a hand-off

nt65 exists to have a Roslyn-style analysis API and a fully-featured editor. This is the
brief for taking the syntax tree the rest of the way there: fixed-shape nodes, missing
tokens, and the public surface an analyzer or an editor feature is written against. It says
where the tree stands, what the target is, what the change breaks, and an order that keeps
the build green.

The direction is decided; the details are not. nt65 is a proof of concept, so nothing here
is a contract: where this brief and the code disagree, the code and whatever reads cleanly
win, and a register map, a port list or a public signature moves when the work calls for it.
Say what moved.

## Where the tree stands

The syntax layer is in `src/Norristown.Core/Syntax/`:

- `InternalSyntax/` is the green tree, the lexer and the parser (`GreenNode`, `GreenToken`,
  `GreenTrivia`, `GreenLine`, `GreenBlock`, `GreenFile`, `GreenSyntax`, `GreenCache`, `Lexer`,
  `Lines`, `Blocks`, `Parser`). A file is lexed a line at a time; `Blocks` builds the block
  structure over the lines; each line is then parsed on its own, in the kind of block around
  it, into one statement. That line-at-a-time design is what makes an edit cheap, and it
  stays.
- `GreenSyntax` is the only parsed green node: a kind and an `ImmutableArray<GreenNode>` of
  however many children the parser happened to add. There is no fixed slot count per kind.
- `SyntaxNode` is the abstract red node, and `Nodes/` holds one class per kind
  (`ProcDeclarationSyntax`, `BinaryExpressionSyntax`, …). `GreenNode.CreateRed` makes the red
  node; `GreenSyntax.CreateRed` is the switch from kind to class.
- A line's tokens live twice in the green tree: once in `GreenLine.Tokens`, and again, the
  same objects in the same order, inside the statement the line parses to
  (`SyntaxTree.Statement(i)`). `LineSyntax.Statement` is the red view of the second.
- The green types are `public`, and the red tree exposes them (`SyntaxNode.Green`,
  `SyntaxToken.Green`, `SyntaxTree.Lines`, `SyntaxTree.Green`). Trivia has no red type: a
  consumer that wants a comment reads `token.Green.TrailingTrivia`.

The parser's rule, stated in its own summary, is that it never drops a token and never
invents one. So a piece it expected and did not find is **absent**: `.proc {` parses to a
`ProcDeclaration` with two token children, not three. Everything follows from that:

- Accessors on the node classes find their piece rather than index it. They are built from
  the helpers at the bottom of `SyntaxNode.cs`: `TokenAt`, `NameAt`, `FirstToken`,
  `FirstWord`, `FirstNode<T>`, `NodeAfter`, `NodeBefore`, `TokenAfter`. A piece that may be
  absent is a nullable property.
- `tests/Norristown.Tests/Syntax/TypedNodeTests.cs` reads every property of every node over
  every `.nt65` source in the repository, whole and with each line cut short. It is the net
  under the accessors, and it stays the net under this work.
- A parse error is `Parser.Error(int Token, string Message, DiagnosticFix? Fix)`: the index of
  a token **in the line's token list**. `SyntaxTree.CollectDiagnostics` turns that into a
  span, and has a special case for an error on the end-of-line token, which means "something
  is missing here": it puts the caret just past the last real token.

## The target

What an analyzer author or an editor feature should be able to write, as they would against
Roslyn:

```text
var tree  = SyntaxTree.Parse(path, text);
var proc  = tree.Root.DescendantNodes().OfType<ProcDeclarationSyntax>().First();
proc.Name                       // SyntaxToken, never null; proc.Name.IsMissing when unwritten
proc.OpenBraceToken.Span        // where the `{` is, or where it belongs
proc.Signature?.Entry.Items     // SeparatedSyntaxList<StateItemSyntax>: items and their commas
proc.ChildNodesAndTokens()      // ChildSyntaxList of SyntaxNodeOrToken, in source order
tree.Root.FindToken(position)   // the token the caret is in
token.LeadingTrivia             // SyntaxTriviaList of SyntaxTrivia, red, with spans
node.ContainsDiagnostics        // without walking
new MyWalker().Visit(tree.Root) // SyntaxWalker with a VisitProcDeclaration to override
```

Concretely:

1. **Fixed slots, in typed green nodes.** Each node kind has a fixed number of children,
   each with a fixed meaning, and a green class of its own that holds them as fields:
   `InternalSyntax.ProcDeclarationSyntax(keyword, name, signature?, openBrace)`, beside the
   red `ProcDeclarationSyntax` of the same name, as in Roslyn. An optional piece that is not
   written is a null slot; a list is one slot holding a list node. `GreenSyntax`, the generic
   kind-and-children node, goes away. See "Typed green nodes" below.
2. **Missing tokens.** A *required* token that is not written is still in its slot, as a
   zero-width `GreenToken` with `IsMissing` set, positioned where it should have been
   written. The zero-child `ErrorExpression` the parser already makes is the same idea for a
   required node and can stay; give it an `IsMissing` too.
3. **Nullability means one thing**: optional and not written. `Signature`, an instruction's
   `Operand`, the `as` of a `.use`, a `.repeat`'s name stay nullable. Required pieces become
   non-null.
4. **Lists are lists**, with Roslyn's helper types, green and red. See "The list and helper
   types" below.
5. **The green tree goes internal.** Nothing outside `Norristown.Core` names a green type.
   That needs the red surface to be complete first: red `SyntaxTrivia`/`SyntaxTriviaList`,
   `SyntaxToken.LeadingTrivia`/`TrailingTrivia`/`IsMissing`/`ValueText`, and red answers for
   what `SyntaxTree.Lines` is used for today (line kind, a line's tokens, brace value).
   `InternalsVisibleTo` for the test project is fine, as Roslyn does; the language server
   should manage without, because it is the API's first real consumer.
6. **Navigation.** `FindToken(position)`, `FindNode(span)`, `DescendantTokens()`,
   `DescendantNodesAndTokens()`, `ChildNodesAndTokens()` (there is no in-order view of a
   node's children today; two test helpers merge `ChildNodes` and `ChildTokens` by position),
   `Ancestors()`, `GetFirstToken()`/`GetLastToken()`, `token.GetNextToken()`.
7. **Visitors.** `SyntaxVisitor`, `SyntaxVisitor<TResult>`, `SyntaxWalker`, with an `Accept`
   on every node. The binder's and the emitter's big `switch (statement)` blocks are where
   these pay for themselves.
8. **Diagnostics on the tree.** A green node carries its own diagnostics, with
   `ContainsDiagnostics` rolled up to the parent, so an error belongs to the node or token it
   is about and survives with the green node across an edit.
9. **One description of the grammar.** Roslyn generates both trees, the factory and the
   visitors from `Syntax.xml`. With a hundred kinds, two classes each, fixed slots and
   visitors, hand-writing stops being reasonable here too. The table says, per kind: its
   base, its summary, and per slot a name, a type (a token and the kinds it may be, a node
   type, `SyntaxList<T>`, `SeparatedSyntaxList<T>`), whether it is optional, and its summary.
   From it come the green class and its constructor, the red class and its properties, the
   `CreateRed` and `Accept` overrides, and the visitors. A table plus a generator that writes
   checked-in `.cs` files (run by hand, with a test that fails when the output is stale)
   avoids a build-time dependency; a source generator is the other way. Choose one early —
   see the order below. The summaries on today's hand-written classes in `Nodes/` are the
   table's first content; do not lose them.

Deliberately **not** in scope yet: `SyntaxFactory` and `With…`/`Update` mutation,
`SyntaxRewriter`, and annotations. They are what refactorings want, the language server's
refactorings work on text edits today, and they are cheap to add once the tree is generated.
Add them when a feature first needs them.

## Typed green nodes

Roslyn's green tree is typed for reasons that all apply once slots are fixed and the classes
are generated, which is why this is part of the work and not an extra:

- **The compiler checks the shape.** The parser stops filling
  `ImmutableArray<GreenNode>.Builder`s and calls a typed constructor. A required slot is a
  non-nullable parameter of the right type; an optional one is nullable. Most of what the
  shape test (step 6 below) would check becomes a type error instead.
- **Slots are fields.** A green node today is an object plus an array. A typed one is one
  allocation, and `GetSlot(i)` is a switch over its fields. nt65 makes a statement node and a
  few expression nodes per line, so this is a real if undramatic saving.
- **`CreateRed` and `Accept` are overrides**, not a hundred-arm switch on kind.
- **The parser reads what it built, typed.** `CheckRequiredParentheses` does
  `(GreenToken)binary.Children[1]`, `RightmostByteOperator` does `binary.Children[2]`, and
  `Finish` re-wraps `statement.Children`. They become `binary.OperatorToken`, `binary.Right`.
- **Flags live on the base.** `IsMissing`, `ContainsDiagnostics` and the diagnostics
  themselves are rolled up from children in the typed constructors, as Roslyn's node flags
  are.
- **Mutation comes cheap later.** `SyntaxFactory`, `With…` and `Update` are thin wrappers over
  typed green constructors.

What matters less here than in Roslyn, and can wait: a green node cache (nt65 already reuses
whole lines across edits and shares tokens through `GreenCache`), and green rewriters (only
useful once the tree is mutated).

`GreenLine`, `GreenBlock` and `GreenFile` are already typed and stay hand-written: they carry
line classification, brace values and block kinds, which a table cannot say. Consider
renaming them to match (`InternalSyntax.LineSyntax`, …) when the generic node goes.

The green and red classes share names. A file that imports both namespaces needs an alias or
a qualified name; today that is a handful of files, and after the green tree goes internal
it is only the syntax layer itself. `CLAUDE.md` wants one type per file, so the generated
green classes are about a hundred more files under `InternalSyntax/`; put them in a folder
of their own.

## The list and helper types

Wanted, because the plan already depends on them:

- **`SeparatedSyntaxList<T>`** — the most valuable one. Nearly every list in the grammar is
  comma-separated (arguments, macro parameters, state items, export and import items, data
  values, bank ranges, `.next` targets, kept registers), and an editor needs the separators:
  adding an argument, removing an item with its comma, signature help that counts commas up
  to the caret. `Count`, an indexer, `SeparatorCount`, `GetSeparator(i)`, `GetSeparators()`,
  `GetWithSeparators()`.
- **`SyntaxList<T>`** — a file's and a block's `Members`. The line grammar has almost no
  lists without separators, so it is small.
- **`SyntaxTokenList`** — the tokens of `SkippedTokens` and of an `ErrorLine`.
- **`SyntaxTrivia` and `SyntaxTriviaList`** — red trivia with spans. Doc comments, the
  formatter, folding and the emitter's comment carry-over all read trivia, and today they
  read it off the green token. Required before the green tree can go internal.
- **`SyntaxNodeOrToken` and `ChildSyntaxList`** — behind `ChildNodesAndTokens()`,
  `GetWithSeparators()` and `FindToken`.

Make them read-only structs over a parent and a slot, as Roslyn's are, so a list property
allocates nothing until it is enumerated (today each list accessor builds an
`ImmutableArray<T>` on first use). Red children are still created lazily and cached on the
parent.

**Items are nodes.** Roslyn's `SeparatedSyntaxList<T>` holds nodes only, and a few lists here
hold bare tokens: a `.func`'s parameter names, a `keeps` register list, the words of a
`one(...)`, the parts of a path. Make them nodes (`ParameterSyntax`, `IdentifierNameSyntax`,
`QualifiedNameSyntax`). It keeps one list type, and it is what an analyzer expects: the
symbol a parameter or a name refers to is looked up through its node.

Not wanted:

- **Roslyn's specialised green lists** (`WithTwoChildren`, `WithThreeChildren`,
  `WithManyChildren`, pooled builders). They save memory on files with hundreds of thousands
  of nodes. A list here lives inside one line and has a handful of items: one green list
  node over an array, and a plain builder.
- **Weakly-held red children** of very large lists. A red subtree here is a line.

For the consumers the change is mostly `.Length` to `.Count`. C# list patterns
(`is [var only]`) work with `Count` and an indexer; slice patterns (`[first, .. var rest]`)
need a range indexer or a `Slice` method, and `StateItem` and `MacroInvocation` use a few,
so give the list types one.

## What it breaks

Read these before starting; each is a place where the current design leans on the rule that
is being removed.

### The token-index correspondence

Today a line's tokens and its statement's tokens are the same list. Once the statement can
hold tokens the line does not, that stops being true, and three things rely on it:

- **`Parser.Error.Token`** is an index into the line's tokens. With diagnostics on the tree
  (target 8) it goes away: the parser reports on the node or token it has in hand, and
  "expected `{`" is reported on the missing `{`. The end-of-line special case in
  `SyntaxTree.CollectDiagnostics` goes with it.
- **`tests/Norristown.Tests/Syntax/TextMateGrammar.cs`** walks a statement's tokens and
  relies on their indexes matching the line's (`Tokens(LineSyntax)`); it must skip missing
  tokens.
- **Anything iterating `ChildTokens` for text** (the emitter's `Tokens()`/`Render`, the
  formatter, `Outline`, `FileInterface`, name walks over `NameExpressionSyntax.ChildTokens`).
  A missing token has empty text and zero width, so most loops are harmless, but any that
  count tokens, test `ChildTokens is [var only]`, or take `ChildTokens[^1]` must not see
  missing ones. Grep for `ChildTokens` outside `Syntax/Nodes/`; at the time of writing the
  list patterns are in `Evaluator`, `Binder`, `MacroArgument`, `MacroInvocation` and
  `DataSyntax`. Roslyn's `ChildTokens()` includes missing tokens. Follow it, and fix the
  handful of callers, rather than having the generic walk quietly differ from Roslyn's.

### The safety that nullable accessors gave

Today an unwritten `{` is a null, and the compiler makes every consumer deal with it. A
missing token is always there and sometimes empty, and nothing forces the `IsMissing` check.
The language server runs the binder, layout and flow over half-typed files all day, so this
is the change's real risk. Two defences, both cheap:

- **A broken-source sweep for the whole pipeline.** `TypedNodeTests` already builds cut-line
  variants of every source in the repository. Run the full analysis (bind, lay out, flow,
  and each language-server request that takes a position) over those variants and assert
  only that nothing throws. Add it *before* converting the parser, so it is a baseline.
- **Declarations ignore missing names in one place.** A symbol is never declared from a
  missing token: make that a property of `Binder.Declare` (or of whatever hands it the name)
  rather than of each call site.

### Fidelity

`tests/Norristown.Tests/Syntax/Fidelity.cs` checks that every statement reads back as its
line. Zero-width tokens keep that true for text. Make sure `GreenToken`'s width arithmetic,
`GreenCache` (a missing token must never be shared with a real one; one shared instance per
kind with no trivia is fine, as Roslyn does) and `SyntaxNode.Span`/`Measure` treat a missing
token as width 0 with no trivia.

### Incremental reuse

`GreenLine.Parse` caches the last `Parser.Result` per block kind, and `IncrementalTests`
asserts reused statements are the *same object*. Missing tokens and node diagnostics are
created inside the parse, so this keeps working as long as they are part of the cached
result and not made later. Diagnostics stored on green nodes must therefore be
position-free (an offset within the node, not a line and column).

### `SkippedTokens`, the line break and `ErrorLine`

`Parser.Finish` appends a `SkippedTokens` child and then the end-of-line token to every
top-level statement. With fixed slots these need a home, and the same statement kinds also
appear nested (an instruction after a label, a declaration after `.export`, a data directive
after `.data name:`), where the outer statement owns the line break.

Roslyn's answer to both is trivia: a line break is end-of-line trivia on the last token, and
skipped tokens are `SkippedTokensTrivia` on the next one. Here the line break is the
statement terminator, so the closer fit is to hang both on the *line*: `LineSyntax` becomes
`[Statement, SkippedTokens?, EndOfLineToken]`, and a statement is only its own tokens. That
removes the "nested statement has no `EndOfLineToken`" wrinkle, and lets `ExportedDeclaration`
be an ordinary wrapper (today `LineSyntax.Parsed` unwraps it so the declaration reads as the
line's statement; consider a `Modifiers`-style `ExportKeyword?` slot on each exportable
declaration instead, which is what Roslyn would do and removes the wrapper altogether).
`Fidelity` then checks that the *line* reads back as its text. Decide this first, because
every slot table depends on it.

### Irregular nodes

Most kinds map to slots directly. These do not, and need a design each:

- **`DataDirective`** — directive, optional type name, optional count, then *either* an
  opening brace, *or* one braced value, *or* a comma-separated list. Make the tail one slot
  holding one of three node kinds; `DataSyntax.ValuesOf`/`BracedOf` and
  `DataDirectiveSyntax.Values` collapse into it.
- **`StateItem`** — a set name, or `?`, or a word with a suffix, or a word with a value, or
  `inline .strz`, or `keeps` with a register list. Split it into several kinds under an
  abstract `StateItemSyntax`; `Semantics/StateItem.cs` already discriminates them.
- **`UseDirective`**, **`ModuleDirective`**, **`NameExpression`** — a path is a flat run of
  name and `::` tokens directly under the node. Give names Roslyn's shape
  (`QualifiedName`/`IdentifierName`, with `[i]` as an element-access node) or a separated
  list of parts; either way the "is this one bare token" patterns in the consumers become a
  type test.
- **Every comma-separated list** (`ParseCommaSeparated`): arguments, parameters, state items,
  export/import items, values, bank ranges, targets. Today commas are loose tokens among the
  item nodes. Each becomes a `SeparatedSyntaxList<T>` slot, and `ParseCommaSeparated` returns
  a green separated list; where the items are bare tokens today they become nodes.
- **`Segment*`** — the parser picks `SegmentDeclaration`, `SegmentBlock` or `SegmentRegion`
  by looking at the whole line. The three shapes stay three kinds.
- **`AssertDirective`** — ca65's level word is an error but is kept in the tree. Make it an
  optional slot.
- **`ImmediateOperand`** — the `#a, #b` of a block move; a second optional group, or a list.
- **`TryParseIndirect`** backtracks. It must discard any missing tokens and diagnostics made
  on the failed attempt along with the errors it already discards.

## An order that keeps the build green

Each step builds and passes `scripts/gate.ps1` on its own, and is a commit.

Done so far: steps 1, 2, 3, 4, 5 and 6. What they decided, where it differs from the text above:

- **The line is `[ExportKeyword?, Statement, SkippedTokens?, EndOfLineToken]`.** `.export`
  before a declaration went on the *line* (`LineSyntax.ExportKeyword`), not on each
  declaration: the `ExportedDeclaration` kind and wrapper are gone, and
  `StatementSyntax.IsExported`/`ExportToken` read the line. `ErrorLine` holds no line break
  either. `Parser.Result` carries the line's own pieces beside the statement. Moving the
  keyword into a slot of each exportable declaration is a change to the table once the
  table exists, if it is still wanted.
- **A list slot is one green node** (`GreenList`, `GreenSeparatedList`, items and separators
  alternating in one array), with one internal red `SyntaxListNode` over it that caches the
  red items; the list structs are views over that node, and an empty list is a null slot.
  `ChildSyntaxList` shows a list slot as one child; Roslyn flattens it, and step 5 decides.
- **A missing token** is `GreenToken.Missing(kind)`, one shared instance per kind, never in
  `GreenCache`. It sits after the previous token's trailing trivia, and `Measure` skips it,
  so a node's `Span` never stretches to one. Where the caret of "expected …" goes is step
  9's to settle.
- `BrokenSourceTests` is the broken-source sweep (about two seconds); `BrokenLines` holds
  the cut-line variants it shares with `TypedNodeTests`.
- **The description is a plain-text table**, `src/Norristown.Core/Syntax/Syntax.nodes`: a block
  per node in the order the files list, a line per key, nesting by indentation, and its own
  header for the format. A node says its base, its kinds, its summary and its markers
  (`abstract`, `internal`, `handwritten`, `partial`); a `slot` says a name, the property's type
  — whose trailing `?` is the whole of what optional means while nothing is invented — its
  summary, and how it is read. A property that is not a piece of the node is a `member`. Two
  people adding kinds touch two blocks. Generated files sit in a `Generated` folder beside the
  hand-written ones they belong with.
- **The generator is `tools/Norristown.SyntaxGenerator`, and references nothing**, because the
  order of work from step 5 on is: change the table, regenerate, fix what the compiler then
  points at. A generator that needed `Norristown.Core` to build could not be run in the state
  its own output had just left the tree in. `pwsh scripts/generate-syntax.ps1` writes the files
  and deletes the ones the table no longer describes; `-Check` compares instead and fails. The
  test project references the tool, so `GeneratedSyntaxTests` asks the same code for the same
  text in memory and never writes anything; it fails when what is checked in is stale, when a
  kind has no row, or when a walk misses a node.
- **The accessors ride along as they are.** A slot's `read` is today's search expression,
  verbatim; `nodes` and `cache` are the two shapes that need a field, and the field is named
  after the property. Step 5 adds what it needs per slot — the kinds a token may be, a list type
  — without touching a line it does not own, and step 7 deletes the reads and keeps the types.
  What no vocabulary covers stays in a hand-written half of a `partial`: `UseDirectiveSyntax`'s
  `ReadPath` and `NameExpressionSyntax`'s `IndexAfter`. `LineSyntax`, `BlockSyntax`,
  `FileSyntax` and `SyntaxListNode` sit over hand-written green types and stay hand-written; the
  table lists them so their `Accept` and their visitor methods come from it like everyone
  else's.
- **The visitors are `SyntaxVisitor`, `SyntaxVisitor<TResult>` and `SyntaxWalker`**, with an
  `Accept` pair on `SyntaxNode` that every concrete class overrides. The walker descends through
  `ChildNodes`; it visits no tokens, because a line's tokens are also its statement's and a
  token walk would see each of them twice until the slots are fixed. Nothing but the tests uses
  a visitor yet.
- **A slot may hold nothing**: `GreenNode.GetSlot` returns null for an optional piece not written
  and for a list with no items, and everything that walks slots allows for it. `GreenNode` carries
  `IsMissing`, which a token and an `ErrorExpression` set; `ContainsDiagnostics` waits for step 9.
- **`ChildSyntaxList` flattens a list slot**, as Roslyn does, and so do `ChildNodes`,
  `ChildTokens`, `DescendantNodes` and the walker: a list shows its items and its separators where
  the list itself would be, a slot holding nothing shows no child, and `SyntaxListNode` is never
  anyone's parent — the items hang from the node that holds the list, which is where an analyzer
  expects them. It keeps the red nodes of the items all the same.
- **`.export` stays on the line.** Moving it into a slot of each exportable declaration would add
  a slot to fourteen kinds and buy a consumer nothing: `StatementSyntax.IsExported` and
  `ExportToken` already answer the only question anyone asks, and `LineSyntax` and `Fidelity` are
  simpler for owning it.
- **The table says each kind's target layout, and one word converts a kind.** A slot's type is what
  its property returns once the kind is converted, and its trailing `?` means the piece belongs to
  a part of the line that may be absent altogether; a piece the line always has a place for is
  required and stands in its slot as a missing token when the source leaves it out. A piece inside
  an optional group stays optional — `a: ` writes the `:` and misses the size, and `a` writes
  neither. `today` gives the type a property still has, `read` the search it still makes, `legacy`
  a property that goes when the kind converts, and a slot with no `read` one the parser does not
  write yet. `kinds` names the kinds a token slot may hold and `layout` the slot order where a
  node's own slots come between the ones a class above it writes (only `ElseIfDirective`).
- **The generated green class is the target shape from the start**, and `converted` on a row is the
  whole of what a kind's conversion costs the generator: every property then reads its slot and its
  required pieces stop being nullable. Until then a property reads its slot when the green node is
  the typed one and searches when it is the generic node, so a typed node can be built by hand and
  read back before its production is converted. A slot declared by an abstract class is abstract
  there and overridden by every concrete class, which is why a kind converts with the family that
  writes the slots it inherits; the generator says so rather than writing what will not compile.
  A node slot takes a bare `GreenNode` until every kind it may hold builds its own green class, so
  no kind waits for what it holds.
- **The irregular kinds' designs are written down and generated, marked `unbuilt`**: a data
  directive's tail is one slot holding a `DataBody`, a `BracedData` or an `InlineData`; a state item
  splits into `StateFlagItem`, `StateValueItem`, `StateInlineItem`, `StateKeepsItem`, `StateSetItem`
  and `StateUnknownItem` under `StateItemSyntax`; a name is a `GlobalToken?` and a separated list of
  `IdentifierName` parts, each a name and the `[i]` after it, which is the separated-list option
  rather than a `QualifiedName` tree because it keeps `NameExpressionSyntax` one sealed class; a
  `.func`'s parameters become `Parameter` nodes, and a `keeps`'s registers and a `one(...)`'s words
  become `IdentifierName`s. `.use`'s path and `.module`'s name become one `NameExpression`.
- **The shape test is `ShapeTests`**, over every source and every cut-line variant, with the kinds
  the parser still builds the generic node for in `tests/Norristown.Tests/Syntax/UnconvertedKinds.txt`,
  one to a line, sorted. It is read strictly both ways, so a line cannot outlive its work. It runs
  in under half a second.

1. **The broken-source sweep** (above), as a baseline. Fix whatever it finds today.
2. **Move the line break and skipped tokens to the line.** Alone, with no slot work:
   `Parser.Finish`, `LineSyntax`, `StatementSyntax`, `Fidelity`, and the few consumers that
   read `EndOfLineToken`/`SkippedTokens`. Settle `.export` here too.
3. **Choose how nodes are described and generated**, and move the existing hand-written red
   classes onto it without changing their shape or their summaries. After this step the red
   classes, the `CreateRed` switch and (new) the visitors and `Accept` methods come from one
   table. The accessors are still the search helpers; the table carries them as they are.
4. **The helper types, unused**: `SyntaxNodeOrToken`, `ChildSyntaxList`, `SyntaxList<T>`,
   `SeparatedSyntaxList<T>`, `SyntaxTokenList`, `SyntaxTrivia`, `SyntaxTriviaList`, the green
   list and separated-list nodes and their builder, `GreenToken.IsMissing` and
   `SyntaxToken.IsMissing`. Test them on hand-built green nodes. `ChildNodesAndTokens()` can
   land for real here, since it needs nothing else.
5. **Typed green nodes, generated, beside `GreenSyntax`.** The generator emits the green
   class for every kind, with its typed constructor, `GetSlot`, `CreateRed` and `Accept`, and
   the red class's slot-based properties. Nothing constructs them yet: `GreenSyntax` still
   makes every node, so the build and every test are unchanged. A red class reads slots when
   its green node is the typed one and searches when it is a `GreenSyntax`, until step 7 has
   reached its kind. Done.
6. **A shape test.** For every node in every source and cut-line variant: the green node is
   its kind's typed class, not a `GreenSyntax`, and a required slot holds a node or a token
   (missing or not), never null. It fails for every kind at first; give it an allow-list of
   unconverted kinds that shrinks to empty. Done.
7. **Convert the parser one production at a time**, regular ones first. For each: replace
   `new GreenSyntax(kind, children)` with the typed constructor, replace "report and leave it
   out" with "report and add a missing token", delete the red class's search path, make the
   required properties non-nullable, and fix the consumers the compiler then points at
   (mostly `?.` and `is { } x` that can be deleted — and, each time, ask whether the consumer
   now needs an `IsMissing` test it did not need before). `Parser.ExpectOpenBrace` is the
   template: it already is the one place that says "expected `{`".
8. **Convert the irregular nodes**, one per change, each with its own small design, and the
   comma-separated lists with them. Delete `GreenSyntax` when the allow-list is empty.
9. **Diagnostics onto the tree.** Parser errors attach to green nodes and tokens;
   `ContainsDiagnostics`; `SyntaxTree.Diagnostics` collects them by walking only subtrees that
   contain any. Delete `Parser.Error`'s token index and the end-of-line special case. This is
   the step that changes user-visible output: caret positions of "expected …" diagnostics
   may move. Check them against the fixture snapshots and the language-server tests
   deliberately rather than updating wholesale.
10. **The red surface**: trivia, navigation (`FindToken` first — the language server has
   hand-rolled versions of it in `Lsp.cs`, `Edits.cs`, `ExtractProc.cs` and `LineContext.cs`
   that should become calls). Move each language-server feature onto it as it lands.
11. **Make the green tree internal.** The compiler lists what still reaches for it. Replace
    `SyntaxTree.Lines`/`Green`/`Statement(int)` with red equivalents.
12. **Spend it in the editor**: fixes and completion that insert the missing piece take its
    span from the token (`Fixes.cs`, `Completion.cs`, `LineContext.cs`).
13. **Delete the search helpers** from `SyntaxNode` that nothing uses any more, and rewrite
    the summaries of `SyntaxNode` and `Parser`, which both state the never-invents rule.

Steps 1–6 do not change the tree's shape and can be reviewed quickly. Step 7 is the bulk
and parallelises by production, provided steps 3 and 5 are done: what made the typed-node migration
parallelisable was that consumers were split by file with no shared edits, and the generated
table would otherwise be the one file everyone touches. Give each worker its kinds' rows.

### Step 7, kind by kind

**What a worker does for one kind**, start to finish:

1. **Edit its block in the table**: add `converted`, drop every `today` and every `read`, `nodes`
   and `cache` from its slots, and take "or null" out of the summary of a slot that is now
   required. Nothing else in the table is touched, so two workers never meet in it.
2. **`pwsh scripts/generate-syntax.ps1`.** The red class's properties now read slots and its
   required pieces are no longer nullable; the green class was already the target shape.
3. **Convert the production**: build the typed green node instead of filling an
   `ImmutableArray<GreenNode>.Builder`, and where it reported a piece and left it out, call
   `Expect(kind, message)` and put the missing token in its slot. A slot the source genuinely does
   not have stays null.
4. **Fix the consumers the compiler points at.** Mostly `?.` and `is { } x` that can go; each time,
   ask whether the consumer now wants an `IsMissing` test it did not want before — a symbol is
   never declared from a missing name.
5. **Delete the kind's line from `tests/Norristown.Tests/Syntax/UnconvertedKinds.txt`.**
6. **`pwsh scripts/test.ps1`**, and `pwsh scripts/gate.ps1` before the commit. Fixture output must
   not move by a byte.

`Parser.Expect` and `Parser.ExpectOpenBrace()` are there already, and every production that returns
a node returns a `GreenNode`, so no worker changes a signature anyone else reads. Whoever first
wants `ExpectName(message)` — a name, or the missing identifier that stands where one belongs —
adds it beside `Expect`.

**The groups.** Five, chosen so that they touch disjoint parser productions and, as far as they
can, disjoint consumer files:

- **A — expressions.** `BinaryExpression`, `UnaryExpression`, `ParenthesizedExpression`,
  `NumberExpression`, `CharacterExpression`, `StringExpression`, `CpuNameExpression`,
  `CurrentAddressExpression`, `ErrorExpression`, `CallExpression`, `ElementIndex`.
  `ParseBinary`/`ParseUnary`/`ParsePrimary`/`ParseParenthesized`/`ParseBuiltinCall`/`ParseElementIndex`,
  and the parser's own `OperatorOf` and `RightmostByteOperator`, which become `binary.OperatorToken`
  and `binary.Right`. Consumers: `Semantics/Evaluator.cs`, `Semantics/Annotations.cs`.
- **B — operands, instructions and the plain lines.** `InstructionStatement`, `AbsoluteOperand`,
  `AccumulatorOperand`, `AddressPrefix`, `ImmediateOperand`, `IndirectOperand`,
  `IndexedIndirectOperand`, `LongIndirectOperand`, `BracedOperand`, `Label`, `LabeledLine`,
  `BlockSplice`, `BlockCloseLine`, `BlockContinuation`, `BlankLine`.
  `ParseInstruction`, `ParseOperand` and everything under it, `ParseLabeledLine`, `ParseBlockClose`.
  `TryParseIndirect` backtracks, and must throw away the missing tokens of a failed attempt with
  the errors it already throws away. Consumers: `Layout/CodeLayout.cs`, `Flow/`, `Emit/Emitter.cs`'s
  operand rendering, `Semantics/MacroArgument.cs`.
- **C — routines, scopes and macros.** `ProcDeclaration`, `ExternProcDeclaration`,
  `MultiProcDeclaration`, `ScopeDeclaration`, `ProcSignature`, `MacroDeclaration`, `MacroCall`,
  `MacroParameter`, `EmptyBlock`, `NamedArgument`, `FuncDeclaration`, `SignatureDeclaration`,
  `FrameDirective`, `PatchDirective`. Consumers: `Semantics/Macros.cs`,
  `Semantics/MacroInvocation.cs`, `Semantics/ProgramModel.cs`, `Syntax/Outline.cs`,
  `Flow/RegisterKeeps.cs`.
- **D — types, data and constants.** `EnumDeclaration`, `StructDeclaration`, `UnionDeclaration`,
  `CharmapDeclaration`, `ListDeclaration`, `EnumMember`, `CharmapEntry`, `MemberValue`,
  `DataDeclaration`, `ElementCount`, `ConstantDeclaration`, `ConfigDeclaration`. Consumers:
  `Semantics/DataSyntax.cs`, `Semantics/Configuration.cs`, `Layout/`.
- **E — conditionals, repetition, state and the one-line directives.** `IfDirective`,
  `ElseIfDirective`, `ElseDirective`, `RepeatDirective`, `EachDirective`, `StateDirective`,
  `EnsureDirective`, `AssertDirective`, `ErrorDirective`, `CpuDirective`, `ImportItem`,
  `ImportSignature`, `ExportItem`, `UseItem`, `BankRange`. Consumers:
  `Semantics/Repetitions.cs`, `Semantics/SemanticModel.cs`, `Semantics/FileInterface.cs`,
  `Semantics/Binder.cs`.

**Set aside for step 8**, because each needs a list or a kind that does not exist yet: every
holder of a comma-separated list (`ArgumentList`, `ExportDirective`, `ImportDirective`,
`ListItems`, `MacroParameterList`, `NextDirective`, `ParameterList`, `ParameterKind`,
`RecordValues`, `StateList`, `ValueList`, `DataValues`), the token lists (`ErrorLine`,
`SkippedTokens`), `DataDirective` with its three tails, `StateItem` with its six, `NameExpression`
with `IdentifierName`, `ModuleDirective`, `UseDirective`, and the whole `.segment` family
(`SegmentDeclaration`, `SegmentBlock`, `SegmentRegion`, `SegmentAttribute`) — the declaration holds
two lists, and its two siblings share the abstract class that writes its first two slots.

**What ties the groups.** A kind converts with the family that writes the slots it inherits, and
the generator refuses anything else: `LiteralExpression` with its four (A), `TypeDeclaration` with
its five (D), and `ConditionalDirective`, `RepetitionDirective` and `StateListDirective` each with
their two (E). Nothing else is ordered: a node slot takes a bare green node until every kind it may
hold builds its own, so a group never waits for another. `ParseName` and `ParseCommaSeparated` are
step 8's and no one in step 7 touches them.

## Rules of the road

- Output for a well-formed program must not change by a byte. Fixture snapshots under
  `tests/fixtures/*/expected` are compared exactly; a diff on a valid fixture is a
  regression, never something to `-Update` away. Only diagnostics on malformed lines may
  move, and only in step 9.
- Incremental parsing must not get slower in kind: an edit still lexes the lines it touches
  and reparses only those (and lines whose block context changed). `scripts/test.ps1
  -Benchmark` prints what an edit costs; look at it after steps 2, 7 and 9.
- `scripts/test.ps1` is the edit loop (about six seconds); `scripts/gate.ps1` once per step.
  The broken-source sweep and the shape test run over 142 sources times eight variants; keep
  them parallel (`Repo.CollectFailures`) and watch that they stay under a second or two.
- `CLAUDE.md` holds the C# rules (one type per file, member order, warnings are errors).
  Generated files are one type per file like any other. Some `.cs` files are CRLF and some
  LF; edit in place rather than rewriting a file.
- `DESIGN.md` defines the language, not the API, and needs no change for this work. If the
  API is to be documented for analyzer authors, that is a new document written at the end.
