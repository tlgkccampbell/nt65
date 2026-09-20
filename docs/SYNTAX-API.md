# The syntax API: what was built

nt65 exists to have a Roslyn-style analysis API and a fully-featured editor. This was the brief
for taking the syntax tree the rest of the way there — fixed-shape nodes, missing tokens, and a
public surface an analyzer or an editor feature is written against — and it is now the record of
it. The work is done; what is left is named at the end.

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
  and grepped like any other code: the green class, the red class, `CreateRed`, `Accept`, and the
  two visitors. A table it cannot read is diagnostic `NT1001` against `Syntax.xml`.
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
  `SyntaxVisitor`, `SyntaxVisitor<TResult>` and `SyntaxWalker`.
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
`Fidelity` (a line reads back as its text) and `IncrementalTests` (an edit reuses the lines it did
not touch, as the same objects). Each runs over every `.nt65` source in the repository and over
seven ways of cutting its lines short, which is what a file being typed looks like.

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
7. **Visitors.** Done.
8. **Diagnostics on the tree.** Done.
9. **One description of the grammar.** Done.

Deliberately **not** in scope, and the next work:

- **`SyntaxFactory`** — thin wrappers over the typed green constructors the generator already
  writes; the table knows every slot's type, so the factory methods generate from the same rows.
- **`With…` and `Update`** — one method per slot, each calling the typed constructor with one slot
  replaced; generated from the same rows as the properties.
- **`SyntaxRewriter`** — a `SyntaxVisitor<SyntaxNode>` whose default rewrites a node's children
  and calls `Update` when one changed. It needs `Update` and nothing else.
- **Annotations** — a field on `GreenNode` beside the diagnostics one, carried by `With…` the way
  diagnostics are carried by the constructors.

They are what refactorings want; the language server's refactorings work on text edits today. Add
them when a feature first needs them.

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
- **Two places inside the compiler still read the green lines**, and say so where they are: the
  formatter, which lays out a whole file from the lines the lexer made, and
  `UnusedSymbols.Written`, whose red walk measured about seven milliseconds a keystroke slower.
  Both are inside `Norristown.Core`, which is the boundary that matters. Everything else in the
  compiler, the language server and the CLI is on the red tree.
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
- `scripts/test.ps1` is the edit loop, about eleven seconds; `scripts/gate.ps1` once per unit of
  work. The sweeps run over 142 sources times eight variants; keep them parallel
  (`Repo.CollectFailures`).
- `CLAUDE.md` holds the C# rules. Generated files are one type per file like any other.
- `DESIGN.md` defines the language, not the API.
