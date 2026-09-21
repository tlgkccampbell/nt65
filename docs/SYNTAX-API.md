# The syntax API: what was built

nt65 exists to have a Roslyn-style analysis API and a fully-featured editor. This was the brief
for taking the syntax tree the rest of the way there — fixed-shape nodes, missing tokens, a public
surface an analyzer or an editor feature is written against, and a way to change a tree rather than
only read one — and it is now the record of it. The work is done; annotations are the one thing
held back, and are named where they belong.

For writing against the tree, read [the analysis API](ANALYSIS-API.md), which is the guide. This
document says how the tree came to be the shape it is, so that the next person to change it knows
which choices were made on purpose.

nt65 is a proof of concept, so nothing here is a contract: where this and the code disagree, the
code wins, and a public signature moves when the work calls for it. Say what moved.

## Where the tree stands

The syntax layer is `src/Norristown.Core/Syntax/`:

- **One description of the grammar.** `Syntax.xml` is the node table, after Roslyn's own: a block
  per node saying its base, its kinds, its summary and, per slot, a name, a type, whether it is
  optional and its summary. A hundred and five concrete kinds and twelve abstract classes. Its own
  header says what every element and attribute means, and adding a kind is one block.
- **A source generator writes both trees.** `src/Norristown.SyntaxGenerator` is an incremental
  source generator that `Norristown.Core` references as an analyzer with the table as an
  additional file, so a build regenerates and the way to work is: change the table, build, fix
  what the compiler points at. Nothing is run by hand and nothing is checked in. What it writes
  lands on disk under `src/Norristown.Core/Generated`, one file per type, git-ignored, to be read
  and grepped like any other code: the green class, the red class with `CreateRed`, `Accept`,
  `Update` and a `With` per slot, the two visitors, `SyntaxFactory` and `SyntaxRewriter`. A table
  it cannot read is diagnostic `NT1001` against `Syntax.xml`.
- **`InternalSyntax/` is the green tree**, the lexer and the parser, and all of it is `internal`.
  A file is lexed a line at a time; `Blocks` builds the block structure over the lines; each line
  is then parsed on its own, in the kind of block around it, into one statement. That
  line-at-a-time design is what makes an edit cheap, and it stays. `GreenLine`, `GreenBlock` and
  `GreenFile` are hand-written, because they carry line classification, brace values and block
  kinds that a table cannot say; every other green class is generated and typed, one field per
  slot.
- **The red tree is the API.** `SyntaxNode` with a class per kind, `SyntaxToken`, `SyntaxTrivia`,
  and the list types `SyntaxList<T>`, `SeparatedSyntaxList<T>`, `SyntaxTokenList`,
  `SyntaxTriviaList`, `SyntaxNodeOrToken` and `ChildSyntaxList`, all read-only structs over a
  parent and a slot. Red children are made on first use and kept. Navigation is `FindToken`,
  `FindTrivia`, `FindNode`, `DescendantNodes`, `DescendantTokens`, `DescendantNodesAndTokens`,
  `ChildNodesAndTokens`, `Ancestors`, `GetFirstToken`/`GetLastToken`,
  `SyntaxToken.GetNextToken`/`GetPreviousToken` and `SyntaxTree.GetLine`/`LineCount`. Dispatch is
  `SyntaxVisitor`, `SyntaxVisitor<TResult>` and `SyntaxWalker`. Changing a tree is
  `SyntaxFactory`, `Update`, `With<Slot>`, `SyntaxRewriter` and the replacements on a node.
- **A node's shape is fixed.** Each kind has a slot for each piece it is written with, in source
  order. A required piece the source did not write stands in its slot as a zero-width missing
  token, or as an `ErrorExpression` where a node belongs, so a required property is never null;
  `IsMissing` is what a reader asks. A nullable property means one thing: the piece belongs to a
  part of the line the source did not write at all.
- **Diagnostics live on the tree.** A green node and a green token carry their own
  `GreenDiagnostic`s, placed within themselves — an offset and a width, never a line and a column
  — with `ContainsDiagnostics` rolled up, so a line keeps them across an edit anywhere else in
  the file and collecting a file's diagnostics walks only the subtrees that hold one.
  `SyntaxNode.GetDiagnostics()` and `SyntaxToken.GetDiagnostics()` are the red answer.

The nets under all of this are `ShapeTests` (every node has the shape its row describes),
`TypedNodeTests` (every property of every node is read), `BrokenSourceTests` (the whole pipeline
and every editor request over half-written files), `NavigationTests`, `TreeDiagnosticsTests`,
`Fidelity` (a line reads back as its text), `IncrementalTests` (an edit reuses the lines it did
not touch, as the same objects) and `RewriteTests` (a rewrite of nothing gives back the same root,
rebuilding every name writes the file back byte for byte, replacing one number moves nothing else,
and a normalized file reads back as the tokens it was written with). All but the last run over
every `.nt65` source in the repository and over seven ways of cutting its lines short, which is
what a file being typed looks like; `RewriteTests` runs over the sources as they are written.

## The target

All nine items are met.

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

1. **Fixed slots, in typed green nodes.** Done; `GreenSyntax`, the generic kind-and-children node,
   is gone.
2. **Missing tokens.** Done, for tokens and for `ErrorExpression`.
3. **Nullability means one thing**: optional and not written.
4. **Lists are lists**, with Roslyn's helper types, green and red.
5. **The green tree is internal.** `InternalsVisibleTo` is for `Norristown.Tests` only, as Roslyn
   does. `ApiSurfaceTests` reflects over the assembly and fails if a signature a consumer can see
   names a green type, or anything else non-public.
6. **Navigation.** Done.
7. **Visitors.** Done, and what the binder, the emitter and the code layout dispatch a statement
   through.
8. **Diagnostics on the tree.** Done.
9. **One description of the grammar.** Done.

## A tree that can be rewritten

An analyzer could be written against the tree above; a fix could not, which is why every
refactoring in the language server is still a text edit. That is what this adds, from the same
rows: `SyntaxFactory`, `Update` and one `With<Slot>` per slot, `SyntaxRewriter`, and the
replacements on a node. [The analysis API](ANALYSIS-API.md) is how to use them; this is why they
are the shape they are. **Annotations** are still deliberately out: they are a field on `GreenNode`
beside the diagnostics one, and they go in when a refactoring first needs one.

- **The generator writes three more things.** `Update` over every slot and a `With<Slot>` per slot
  on the red class; a `SyntaxFactory` method per kind over the typed green constructor;
  and a `SyntaxRewriter` method per kind that rewrites the node's slots and calls `Update`. The
  hand-written halves are `SyntaxFactory.cs` — tokens, trivia and lists — and `SyntaxRewriter.cs`,
  which holds `VisitToken`, the three `VisitList`s and the file, the block and the line.
- **`Update` compares green nodes, so nothing changed means the same node.** Every slot given back
  the green node already in it makes `Update` return `this`, which rolls up: a rewriter with no
  override hands back the root it was given, the same object. That is the identity a fix run over a
  file is asked about afterwards, and it is what makes the rest cheap.
- **A statement and below is rebuilt; a line and above is written back as text.** A green line
  holds the tokens the lexer read and *not* the statement they parse to — the statement is the
  parse's, kept per line — so there is no green edge from a line to what is on it to rebuild
  through. So a rewrite collects what moved on each line, writes those spans back into the file's
  text as **one** change from the first to the last, and parses again from there. Fidelity makes
  that exact: a piece's text *is* its span in the file, so replacing the span replaces the piece
  and touches nothing else, and `WithChange` keeps the green nodes of every line outside the range.
  A rewrite that reaches a line or the root therefore gives back the matching node of a new tree.
  The alternative — threading a rebuilt statement back into the line — would mean a green line that
  holds both its tokens and its statement, which is the design the whole layer is written against.
- **A built node has a tree of its own.** A red node needs a tree and a position, and Roslyn's
  answer is that a detached node is the root of its own tree; `SyntaxTree` has a second, private
  constructor that takes one green node, so a factory-built node's spans, text and diagnostics read
  the way a node of a file's do. Such a tree has no lines and no `Root`; asking for one says so.
  That is one tree per factory call, which is a string and a line table, and a rewrite is what
  takes the node out of it.
- **Whitespace is the caller's, and the formatter cannot supply it.** `nt65 fmt` moves what stands
  before a line's first token, drops what follows its last and sets the gap in a data line's
  column; it never puts a separator *between* two tokens, so it cannot rescue a built `lda#0`.
  So the factory invents nothing and there are two ways to say it: `WithTriviaFrom` keeps what
  stood around the token being written over, which is what a fix wants, and `NormalizeWhitespace`
  writes one space where two tokens would otherwise read as one and none where none is wanted,
  which is what a node built out of bare tokens wants. The one exception is a separated list, whose
  commas are written `, `, because a list has nowhere else to say it. Running `Formatter.Format`
  over the tree a rewrite gives back is still what lays the lines out.
- **`NormalizeWhitespace` is a pair rule with one thing the pair cannot say.** What binds tight
  binds tight on whichever side it is written (`::`, `#`, `(`, `[`, `,` and `)` before or after),
  a word needs telling from the word after it, and a binary operator is written clear of both
  operands. The part two kinds cannot answer is a prefix operator, an operand's own comma and an
  address prefix's `:`, which are written tight: that is read off the node holding the token, not
  off the token. `RewriteTests` normalizes every source in the repository and checks it reads back
  as the tokens it was written with, which is the guarantee — valid nt65, not the file's own
  layout.
- **`ReplaceNode` and the rest are the rewriter with one override.** `ReplaceNodes` is a rewriter
  whose `Visit` answers for the nodes it was given, `ReplaceTokens` one whose `VisitToken` does,
  and `NormalizeWhitespace` one that hands back tokens in the order they are met. A node being
  written over is not walked into, so what replaces it is worked out from the node as it stands.
  `RemoveNode` is a replacement with nothing: a line goes with the break that ends it, a list item
  with the separator after it, and whatever stood after a list's last item is handed to the item
  now at the end, so that what is left is not written up against the rest of the line. A piece the
  table says must be there cannot go, and says so.
- **The generator says no to a table that would break it.** A second row of the same `Name` used to
  write one file twice and keep whichever came last; a `Base` that leads back into a circle used to
  be walked up forever. Both are `NT1001` against `Syntax.xml` now, with the line to look at.

## What was decided, and why

Where the code does not already say it.

- **`.export` stays on the line.** `LineSyntax` is
  `[ExportKeyword?, Statement, SkippedTokens?, EndOfLineToken]`, and `.export` before a
  declaration belongs to the line rather than to each exportable declaration: a `Modifiers`-style
  slot would add one to fourteen kinds and buy a consumer nothing, since
  `StatementSyntax.IsExported` and `ExportToken` read the line and answer the only question anyone
  asks. The line break is the line's for the same reason, which is why a statement is only its own
  tokens wherever it is written — nested inside a `LabeledLine`, after an `.export`, or on its own
  — and why `Fidelity` checks that the *line* reads back as its text.
- **The required/optional rule.** Optional means the piece belongs to a part of the line that may
  be absent altogether: a signature, an operand, an `as`. A piece the line always has a place for
  is required and stands in its slot as a missing token when the source leaves it out. A piece
  inside an optional group stays optional, and is *missing* rather than absent once the group is
  there: `.use a::{b` holds a missing `}`, because the `{` is written, while `.use a::b` has no
  place for one at all. That is the rule the table's header states, and the one to apply to a new
  kind.
- **A list slot is one green node.** `GreenList` and `GreenSeparatedList` hold items and
  separators alternating in one array, with one internal red `SyntaxListNode` over them that
  caches the red items; the list structs are views over that node, and an empty list is a slot
  holding nothing. `ChildSyntaxList` flattens a list slot, as Roslyn does, and so do `ChildNodes`,
  `ChildTokens`, `DescendantNodes` and the walker: the items hang from the node that holds the
  list, and `SyntaxListNode` is never anyone's parent. Items are nodes, never bare tokens, so a
  `.func`'s parameters are `ParameterSyntax` and a `keeps`'s registers are `IdentifierNameSyntax`
  — which is what an analyzer expects, because the symbol a name refers to is looked up through
  its node.
- **A name is a separated list of parts.** `NameExpressionSyntax` is a `GlobalToken?` and a
  separated list of `IdentifierName`s, each a name and the `[i]` after it, rather than a
  `QualifiedName` tree: it keeps the class sealed and one kind, and `[i]` is a place in what the
  name stands for rather than part of the name. A part the source did not write is an
  `IdentifierName` whose name token is missing, and `Names`, `SimpleName` and `LastPart` leave
  those out, so a consumer asking what a path names never sees a hole.
- **The caret rule is one rule**, written on `Parser` and implemented by `Parser.Caret`: a
  diagnostic goes over the token the parser is looking at, or, where the line has run out, at the
  end of the last token the source does have, ahead of the whitespace and the comment after it.
  That is where the piece belongs, and a caret there neither drifts right as trailing spaces are
  typed nor lands past a trailing comment. A missing token sits *after* that trivia, so its
  diagnostic reaches back over it with a negative offset — which is why a `GreenDiagnostic`'s
  offset may be negative. Moving the diagnostics onto the tree moved no caret.
- **Diagnostics are stored position-free, in a field on `GreenNode`.** A field is one reference
  and a flag, which fits the padding a kind and a width already leave, against a
  `ConditionalWeakTable`'s lookup on every node and its write on every node the parser reports
  about. Being position-free is what lets a line keep its diagnostics across an edit elsewhere,
  and being part of the parse is what lets `IncrementalTests` still find a reused statement to be
  the same object. A token that reports something is never shared: the lexer's cache refuses one
  with an error, and the missing token of a kind is shared only while it says nothing.
- **A line, a block and a file answer over their lines.** A green line holds the tokens the lexer
  read, not the pieces they parse to, so no flag on it could answer for the line. The tree works
  out as it is built which lines have anything to say — their tokens, what the parser said about
  what they parse to, and the braces over them — and a line, a block and the root read those
  flags. So `tree.Root.ContainsDiagnostics` is true exactly when `tree.Diagnostics` has something
  in it, and `tree.Root.GetDiagnostics()` is that same list.
- **Navigation answers `SyntaxToken?`, not a default token.** A `SyntaxToken` is a value, so
  `default` is a token of nothing; `GetNextToken`, `GetPreviousToken`, `GetFirstToken` and
  `GetLastToken` return null instead, and nothing has to know that a token with a null parent
  means "there is none". `FindToken` never answers with a missing token, because a token of no
  width holds no position and nothing is ever written in one; at the very end of a node, where
  nothing is written at all, the answer is its last token.
- **Nothing outside the syntax layer reads the green lines.** The formatter and
  `UnusedSymbols.Written` were the two that did, and both now read `SyntaxTree.GetLine`,
  `LineSyntax.Tokens` and the blocks over them. What made the red walk cost about seven
  milliseconds a keystroke was `LineSyntax.Tokens` building and keeping an
  `ImmutableArray<SyntaxToken>` for every line; it is a `SyntaxTokenList` over the line's own
  slots now, which makes a token as it is asked for and never makes a statement, so walking a
  file's tokens allocates nothing. `SyntaxTree.Green` is a private field, `Lines` is the syntax
  layer's own, and `ApiSurfaceTests` reads the source of everything in `Norristown.Core` outside
  `Syntax/` and fails if one of them names a green type or reaches for `tree.Lines`.
- **`UnusedSymbols.Written` is lexical because binding cannot answer it.** "Was this `.use`
  item's name written anywhere" has to count a name in a branch this build leaves out, and
  binding returns at such a branch without reading a line of it; a name written where a bare word
  may stand is never looked up; a `.defined` asks about a name without naming it; and a reference
  records the symbol it reached rather than the spelling it was written as, so it cannot tell `b`
  written as `a::b` from the `c` that `.use a::b as c` brought in. The tokens are what all of
  those have in common, which is why the scan reads them.
- **The big statement switches are visitors.** `Binder.BindStatement`, `Emitter.WalkLine` and
  `CodeLayout.Statement` each dispatch through a nested private `SyntaxVisitor` with a method per
  kind — twenty-four, twelve and sixteen of them — which is what the visitors are for. A kind with
  no method is what the `default:` arm was, and each class says so. The smaller switches are
  still switches: an expression switch answering from a handful of cases, a dispatch that carries
  arguments besides the node and falls out of the switch into a shared tail, and a switch whose
  arms `continue` the loop around it all read worse as a visitor, and say so where they are.
- **Completion reads the line lexed as far as the caret**, not the tree, and that is deliberate:
  the caret cuts the line in the middle of what is being typed, so the file's `$10` is the
  typist's `$1` and `.byt` is not yet `.byte`, and it is what has been typed that a completion is
  about. `LineContext` says so, and the tree answers what surrounds the line.

## Rules of the road

Still true for anyone changing the tree.

- Output for a well-formed program must not change by a byte. Fixture snapshots under
  `tests/fixtures/*/expected` are compared exactly; a diff on a valid fixture is a regression,
  never something to `-Update` away.
- No diagnostic moves without meaning to. The way to know is a probe: a console program against
  the tree before and after, over every source in eight cut variants and a few hundred malformed
  lines in every block context, printing each diagnostic's position, severity, message, fix and
  related spans, and comparing the two outputs byte for byte.
- Incremental parsing must not get slower in kind: an edit still lexes the lines it touches and
  reparses only those, and lines whose block context changed. `scripts/test.ps1 -Benchmark` prints
  what an edit costs.
- `scripts/test.ps1` is the edit loop, about three and a half seconds of which one is the build;
  `scripts/gate.ps1` once per unit of work, about four and a half. It was ten and eleven, and what
  it spent them on was not the work: the test project asks for the server collector, because the
  workstation one suspended every test at once to gather what the sweeps allocate and cost over
  five of the nine seconds; the runner is given `-parallelMode all`, because a class's tests queued
  behind each other and the longest of them are three seeds of one theory and one replay among
  twelve other tests; and the random-edit replay makes its edits a batch at a time and compares the
  batch's trees beside each other, since making an edit is a twentieth of what checking it costs.
- Five sweeps run over 142 sources times eight variants — the broken-source sweep, the shape test,
  the typed-node test, navigation and the tree's diagnostics — and each parses those 1,136 trees
  for itself. That reads like waste and measures like nothing: building the variants and parsing
  them is a quarter of a second of the eighteen seconds of processor time the five spend, and the
  five together are four tenths of a second of the loop. Keep them parallel
  (`Repo.CollectFailures`); a tree shared between them would also be a tree two sweeps race to
  build a red root over, which `SyntaxTree.Root` is not written for.
- Where the next second is: the two analysis replays. Each of their steps analyzes the program
  from scratch to compare against, which is most of what they cost and is about that step alone,
  so they can be batched the way the parse replay now is.
- `CLAUDE.md` holds the C# rules. Generated files are one type per file like any other.
- `DESIGN.md` defines the language, not the API.
