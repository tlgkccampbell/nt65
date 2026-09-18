# nt65 — Plan after version 1

## Where this comes from

Version 1 is built: `DESIGN.md` defines the language, the output, the project file and the
command, and everything it describes works. A review of the language and the tools on
2026-09-17 found no contradiction inside the design and no diagnostic that failed to say what
to write. What it found is at the edges: two documents that had gone stale, a handful of
conveniences the language lacks, and the build and editor integrations a programmer coming
from a modern toolchain looks for first. This plan builds them.

The rules are those of version 1 (§17): every stage adds and never breaks. A program that
builds today builds after every stage here, to the same bytes; each new construct was an error
before it was a construct. A stage that changes the language changes `DESIGN.md` in the same
commit, with its reasoning in §16, and nothing here needs a decision record beyond that.
`docs/GUIDE.md` is **not** updated stage by stage: the guide is rewritten once, at the end,
from the list in the last section, and the one thing it gets wrong today (signature widths
default to `*`, not `a8, i8`) waits for that pass too.

The order below puts the language work first, because it is what the rest is documented
against, and the release work last, because it is what the rest is packaged into. Stages
continue the numbering of the plans this one replaces; the last of those was Stage 24. Each
stage ends with the gate (`scripts/gate.ps1`), and each stage boundary is where the test loop
is looked at: whether a fixture run has grown, and whether the edit loop still runs only what
can catch the change.

## Stage 25: Small things

Everything here is a few lines each, and together they are most of what a first hour with
nt65 trips over.

**Build.**

- **Digit separators.** `_` between the digits of a number in any base: `$7f_ff`,
  `%1010_1010`, `1_000`. Not first, not last, not doubled. nt65 writes every number itself
  and the output header already switches ca65's `underline_in_numbers` off, so nothing
  downstream sees one. §4.
- **`\0`.** Added to the fixed escapes; it is `\x00`. §4.
- **`$schema` in `nt65.json`** is accepted and ignored, so a project file can name the schema
  Stage 28 ships. Every other unknown key stays an error. §5.3.
- **`.if` in an enum body.** A member may stand under an `.if` in the enum, so that
  `qux` under `.if TEST != 0` is one enum rather than two whole ones under exclusive
  conditions. The condition tests the configuration, as every `.if` outside a turn does, and
  a member without a value is still the previous member taken plus one. This is what makes the
  set of instances of a family (Stage 26) vary by configuration in the place that lists them.
  §6.3, §10, Appendix A (`enum` takes `if-block` among its members).
- **Messages name the near miss.** The binder already finds the declared name a misspelling
  is a letter or two from, for the editor's fix; the message now says it, in the voice the
  messages use: "`COUNTR` is not declared; `COUNTER` is". The same for a `nt65.json` key:
  "`define` is not a `nt65.json` key; `defines` is". A CLI or CI user then gets what the editor
  gets.
- **Unknown commands and options.** `nt65 check` says "`check` is not a command" and
  `nt65 build --watch` says what it says today, and each is followed by one line, "see
  `nt65 --help`", rather than the whole usage text. `nt65` alone and `--help` keep the text.
- **One cause, one report, on pasted ca65.** An unnamed label, `:` at line start or `:+` in
  an operand, is one error saying unnamed labels are `@name`, and nothing else on that line.
  A run of instructions outside a proc is reported once, on its first line, naming how many
  lines it runs to. `.data name { .byte 1 }` on one line says the block opens on its own line.
  The migration table already names all three; the diagnostics catch up with it.

**Check.** Fixtures for each: numbers with separators through the oracle, `\0` in a `.strz`
(an error, as any zero inside it is), `$schema` accepted, an enum with a member under `.if`
built under both configurations, the near-miss messages, the three cascades reduced to one
line each. A CLI test for the command-line messages.

## Stage 26: Routine families

### The problem

A program needs several routines that differ only in a constant: one per sound channel, per
sprite slot, per bank. In ca65 a macro writes the `.proc` and `.ident` builds its name, or a
`.repeat` does. In nt65 a `.proc` may not stand in a `.repeat`, an `.each` or a macro body
(§10, §11.3), and every declaration in such a body is private to its turn, so the routines
are written out by hand, or as one hand-written `.proc` per instance around a macro that
holds the body. The second is three lines per routine and is the right answer for two or
three of them. For eight channels it is not, and the dispatch table beside them is written
out too.

The constraints are the ones that make nt65 analyzable, and each rules out one obvious
answer:

- **Names are never computed** (§2, §15): no `.ident`, no `.concat`, so `play_0` from a
  `.repeat` index is out.
- **Which declarations exist follows from the configuration, before anything is evaluated**
  (§3.1, §10). A routine template instantiated by its calls, `jsr play(2)`, would make the
  routines that exist depend on every use in the program, and on other modules' bodies.
- **A macro cannot declare a name in its caller** (§11.1), and a file's interface is derived
  from headers alone (§14). ca65's `proc_macro name` idiom, a macro whose body is a `.proc`
  named by an `ident` parameter, would put a routine's kind and signature in a macro body,
  possibly in another module, and the instances it makes are tied to nothing that builds the
  table they need.
- **Labels in a turn are private to it** (§10), so one proc with a `.repeat` of entry points
  cannot export them: the same naming problem in a different place.

What nt65 already has is the enum as the source of a fixed set of names. It is what
replaced `.ident` for tables: `.each Cmd, c { actions::c }` reads `actions::move`,
`actions::fire`, because "a binding at the end of a path names the member of that scope with
the same spelling" (§10). That rule *finds* a declaration by a member's name. A family is the
same rule *making* one.

### Decided: a family is an `.each` over an enum that declares by its binding, and `.multiproc` writes the common one

```nt65
.enum Channel { pulse1, pulse2, triangle, noise }

.scope level {
    .each Channel, ch {                     ; the form .multiproc stands for, composed further
        .data ch: .byte                     ; level::pulse1, level::pulse2, ...
    }
}

.scope play {
    .multiproc Channel, ch: a8, i8 {        ; play::pulse1, play::pulse2, ...
        lda level::ch
        .if ch == Channel::noise {          ; the enum's members may be named in a condition here
            inc a
        }
        sta level::ch
        rts
    }
}

.data dispatch: .addr[] {
    .each Channel, c {
        play::c                             ; play::pulse1, play::pulse2, ...
    }
}

    jsr play::triangle
```

In an `.each` over a named enum at item level, a `.proc` or a `.data` whose name is the
binding is declared **once per member, under the member's name, in the scope around the
`.each`**. That is the whole construct, and `.each`, `.scope`, `.data` and `.proc` compose in
it as they compose everywhere else.

`.multiproc E, b: signature { body }` is `.each E, b { .proc b: signature { body } }` with
the two blocks folded into one keyword line, for the case that is nearly every family: one
routine per member and nothing else. It is worth a directive of its own because `.proc b`
with `b` a binding reads oddly until one knows the rule, and because a keyword line says what
the block is from its opener: the parser knows the body is a routine's and the outline shows
one block. It stands where `.proc` stands, and everything said below of a family holds of it.
The name is not `.procs`, which is `.proc` with one letter more at a glance.

**Why it keeps every requirement.**

- *Names are declared, not computed.* Which names a family declares comes from two headers:
  the `.each` or `.multiproc` line and the enum's member list, which is read from the enum's
  lines without evaluating anything. Nothing is concatenated; the members' names are the
  names. It is the rule that lets `.each Cmd, c { actions::c }` *find* a member's
  declaration, run the other way to *make* one.
- *Declarations remain a set fixed before evaluation.* The set now also depends on an enum's
  member names, which is a syntactic fact, not a value. The dependency runs one way: member
  names are identifiers on the enum's lines, and an enum declared inside a turn or a macro is
  private to it, so no family can feed the enum that names it. Whether an instance exists
  never depends on a value: a binding-named declaration stands directly in the body, not
  under an `.if` in it, because a condition in a turn tests the member's *value*, and a value
  is evaluated. The set of instances varies by configuration where the enum does, under `.if`
  in its body (Stage 25) or around it, which is the place that lists them. Where the enum
  comes from another module, its member names are already part of that module's interface
  (an enum crosses by value, §12), so an edit there is news to exactly the modules the
  incremental rule of §14 already names.
- *The interface is derived from headers.* An instance's name, kind and signature come from
  the `.each` or `.multiproc` line, the enum and the `.proc` line. A signature may name the
  binding, `dbr = Bank::b`, and each instance's is that expression with its member's value:
  still a header, nothing from a body.
- *One flow analysis per instance.* The binder already reads a repetition body once, with the
  binding in a scope of its own, and the analysis walks it once per turn, as it walks a macro
  expansion once per call. A problem in one turn is reported at the body line naming the
  instance, `in play::noise`, and once when every turn has it.
- *Tooling without expansion.* Go to definition on `play::triangle` lands on the
  `.multiproc` line, or on the `.proc ch` line of the long form; rename of `triangle`
  renames the enum member and every use of the instance, and the family's line does not
  change. Completion after `play::` lists the members; hover on the family's line lists the
  instances. All of it from what the binder knows of the family, with nothing expanded.
- *Flat, deterministic output.* An instance is an ordinary routine in the output,
  `play__triangle:`, under a comment naming the family and the member, and its lines are
  mapped to the body's lines as a `.repeat` turn's are. An exported instance's linker name is
  ordinary, `snd__play__triangle`.
- *Syntax stays context-free.* `.proc ch: a8, i8 {` in an item-level `.each` parses as any
  proc parses, and the binder decides what it is: a name that is the binding is a family; any
  other name is a declaration private to the turn, which is the rule for every declaration in
  a body today, so an instance may have a helper proc of its own. `.multiproc` is a directive
  line that opens a block whose body is a routine's. The block layer is untouched, and both
  forms were errors before, so neither is a breaking change (§17). Macro bodies still hold
  no `.proc` and no `.multiproc`, so a macro still declares nothing in its caller.

**Rules.**

- A binding-named declaration stands directly in the body of an `.each` over a named enum, at
  item level: file level, a `.scope`, a segment region or block, or an `.if` around the
  `.each`. Anywhere else the binding as a declaration's name is an error that says why: inside
  a proc it would nest; over a list, a `list` parameter or a `.repeat` there are no names.
  `.multiproc` stands where `.proc` may, at file level or in a `.scope` outside any routine,
  and is elsewhere the error `.proc` is there.
- It is one declaration per member in the enclosing scope, so it collides with a hand-written
  declaration of a member's name there, and two families over the same enum in one scope
  collide too, as any duplicate does. Two roles for one member are two scopes, `note::pulse1`
  and `stop::pulse1`, or one binding-named scope, `pulse1::note` and `pulse1::stop`.
- A family declares routines and data: `.proc`, `.multiproc` and `.data name: element`. What a
  binding-named `.scope` or `.data` block held would be reached through it, `pulse1::stop`,
  which is one declaration per member of everything inside — one identity per member for a
  body the binder reads once, which `SymbolMap`, `Step.Routine` and `FlatNames` have no room
  for. Two roles for one member are two families instead, `note::pulse1` and `stop::pulse1`,
  which says the same thing with the names the program already has. A fixed-name declaration
  directly in the body stays private to the turn.
- `.export` before a binding-named declaration, or before `.multiproc`, exports every
  instance; the list form, `.export play::pulse1`, exports one. It is the one `.export`
  allowed in a repetition body, since the names it exports are the enum's;
  `.export .scope play { }` around a family exports its instances today without any new rule.
- A condition in the body may name the enum's members, `.if ch == Channel::noise`. The
  members' values are known when a turn's conditions are evaluated, because the binding's own
  value is one of them, so this adds no ordering.
- An unused warning names a family only when nothing uses any instance, as an enum's members
  are one of a set.
- The lens above the family's `.proc` or `.multiproc` line says what a pass costs; where
  instances differ it says so per instance.

**Where the design changes.** §6.1 gains rows for the family and for `.multiproc`; §7.3
counts an instance among the routines that carry a signature; §10 gains the exception for
binding-named declarations, the `.export`, and members in conditions; §13 the output form;
§14 the family's instances in the interface; §16 the decision, with the alternatives above
and why each was refused, and why the sugar has the name it has. Appendix A adds
`multiproc := '.multiproc' path ',' ident (':' state ('->' state)?)? '{' NL body '}'` to
`item`; the `.each` form needs no grammar change.

**Build.** `Parser` reads the `.multiproc` line and opens a routine body, and accepts
`.proc` in an item-level `.each`; `Binder` declares the instances into the enclosing scope
from the header and the enum, with the binding in a scope of its own over the body, and
reads `.multiproc` as the `.each` it stands for; the flow analysis and the emitter run per
instance as they run per turn today. The server: definition, rename, references,
completion, hover, outline, lens and unused warnings, for both forms.

**Check.** Fixtures: a `.multiproc`; a family of procs and of data in the long form; an
exported family; one whose signature names the binding; one over an enum from another module;
one over an enum with a member under `.if` built under both configurations; the `.if` in the
body naming a member; every wrong place and every collision. All through the oracle, and a
test that writes one family both ways and checks the output is byte-identical. The C64 corpus
program gains a family where it repeats itself. Editor tests for definition, rename and
completion through an instance of each form.

**Done.** Both forms, over an enum in the file or in another module, with the exports, the
per-instance signatures, the per-instance flow analysis and the editor. Two things the writing
of it found: an enum member under an `.if` (Stage 25) was declared but never written out, which
the oracle caught on the first exported one; and a path that ends in a binding names a member
of a **scope**, never of a module, so `snd::b` is not the namesake of `b` and `snd::play::b`
is. The second is how the rule has always been and is left as it is.

## Stage 27: Data conveniences

**Build.**

- **Element indexing.** `name[i]` for data declared with a count, an element type with `[n]`
  or `.type T[n]`, with `i` a constant: the address `name + i * element size`, sized as `name`
  is, and a member path may follow, `actors[1]::hp`. An index at or past the count is an
  error. The count after `.type T` in a declaration stays a count; an index is an expression
  and stands where expressions do. The output writes the sum with a comment naming the path,
  as `player::hp` is written today. §6.3, §8, §9, Appendix A (`path` gains the index).
- **Padded text.** A counted `.byte` array whose only value is one string shorter than its
  count is padded with zero to the count, as `char title[21] = "..."` is in C. A short *list*
  is still the error it is today: the count exists to catch a short table, and a string is
  not a table. A pad other than zero is what the struct's `.res n, pad` member is for, and the
  guide will say so; the SNES header is already written that way. §8.

**Check.** Fixtures for both through the oracle; the C header for an indexed export is
unchanged, since indexing is a use, not a declaration.

**Done.** Both, with an index allowed after any component of a path, so a member that is
itself an array takes one too, `player::colors[2]`. Two things the writing of it found: a
name that stands for what a call or a repetition gave it — a macro parameter, an `.each`
item — has to refuse an index rather than quietly drop it, since what it stands for is not
the declaration the index reaches into; and eight messages named a symbol's kind with `a` in
front, which said `a enumeration` for the kinds that start with a vowel, so the article is
decided once beside the kind now.

## Stage 28: The project file in the editor, and the build in VS Code

**Build.**

- **A JSON schema for `nt65.json`**, `editors/vscode/nt65.schema.json`, contributed through
  `jsonValidation`, so the project file gets completion, hover and validation in every editor
  that reads schemas. A test keeps it in step with `ProjectFile`: every key the reader knows
  is in the schema, and nothing else is.
- **A problem matcher**, `$nt65`, for `file:line:column: severity: message`, and a task
  provider that offers `nt65 build`, once for the project's own settings and once per named
  configuration, with the matcher attached. A build run from the terminal then lands in the
  Problems panel with clickable positions.
- **`wordPattern`** in `language-configuration.json`, so that a double-click takes `@loop`,
  `gfx::init` and `.proc` whole.
- **A README for the extension**, which is its Marketplace page, and the `.nt65` file icon.

**Check.** The schema test; a client test that the matcher parses the CLI's output; the
extension packages without `--allow-missing-repository` once the repository is named.

**Done.** The schema, the matcher, the task provider, the word pattern, the extension's README
and the file icon. `--allow-missing-repository` stays: this repository has no remote to name,
so there is no URL to write down, and the flag comes out with the first push. Two things the
writing of it found: the reader's key lists had to become `ProjectFile.Keys`,
`ConfigurationKeys` and `SegmentKeys` for a test to hold the schema to them, which also made
the segment reader check its keys in one place instead of three; and a task has to run in the
workspace folder rather than beside the project file, because the command prints paths
relative to where it ran and the matcher resolves them against the folder.

## Stage 29: What the server still owes

**Build.**

- **Diagnostics for every file of every project**, not only open ones. `Server.cs` publishes
  for open documents today; the analysis is whole-program, so the change is to publish for
  each file a project names, under its URI, and to clear a file's when it leaves the program.
  An export broken in one file then shows in every module that used it, before any of them is
  opened.
- **Doc comments.** The `;` lines directly above a declaration, with no blank line between,
  and the comment on its own line, shown on hover and as completion documentation, with the
  `;` and one space stripped. A family's instances show the family's comment. Every routine in
  the corpus already has one.
- **Call hierarchy.** `prepareCallHierarchy`, `incomingCalls` and `outgoingCalls` for
  routines, across modules, from the edges the flow analysis already has for the cost lenses:
  calls, tail jumps, `.next` to routines and relative calls. Callers are what one searches for
  most in assembly.
- **Document links** for `.incbin` paths.

**Check.** Server tests for each; the keystroke benchmark before and after publishing for
closed files, since that is the one that could cost.

**Done.** All four, and the project file is published for as well, so a wrong key in
`nt65.json` is a squiggle where it is written. Publishing starts when the editor connects,
before anything is open. The benchmark measures the whole of what a keystroke costs now, the
diagnostics of every file included: on the 300-file program a keystroke went from 5.8 ms to
6.7 ms, a new line from 7.1 to 7.7, an exported constant from 7.9 to 8.2, and the edit that
re-analyzes all 300 files from 150.0 to 149.2. Three things the writing of it found: a file
nobody has open must be sent again only when what is wrong with it changed, or a keystroke
costs one message per file of the program; the client's own spelling of a URI has to be kept
once it gives one, because VS Code escapes a drive's colon and nt65 does not, and two
spellings would leave one file in the problem list twice; and hover has to read the
declaration as the program has it now — an edit that leaves a file's interface alone keeps
the other files' models, and with them symbols whose file is the one from before the edit,
which nothing noticed until a doc comment was read out of it.

## Stage 30: A formatter

Leading whitespace means nothing (§4), so a formatter that touches only leading and trailing
whitespace can never change what a line means, and there is exactly one layout to pick: the
one the output already uses. Names at the margin, what a block holds indented once, `@labels`
at the margin of their routine, instructions indented, a run of named data lines lined up on
their directives. Nothing inside a line is touched.

**Build.** `nt65 fmt [--check] [files]`, which rewrites files in place or, with `--check`,
lists the ones that would change and exits 1; and `textDocument/formatting` and
`rangeFormatting` in the server, from the same code in `Norristown.Core`.

**Check.** Every fixture and corpus source is already formatted, or is reformatted in the
same commit; formatting is idempotent; formatting then transpiling gives byte-identical
output for every fixture, which is the test that it changed no meaning.

## Stage 31: The command line

**Build.**

- **`nt65 init [dir] [--cpu <cpu>]`** writes an `nt65.json` and a `src/main.nt65` that
  builds, and refuses to overwrite either.
- **`nt65 build --check`** analyzes and reports, writes nothing, and exits as `build` would.
- **`nt65 build --watch`** rebuilds when a source, the project file or an `.incbin` file
  changes, which is the set the dependency file already names.
- **`--json`** writes one JSON object per diagnostic to stdout, with the file, position,
  severity, message and related spans, for tools that are not editors.
- **Colour** on `error:` and `warning:` when stderr is a terminal and `NO_COLOR` is unset.

**Check.** CLI tests for each; `--watch` by a test that edits a file and waits for the
rebuild.

## Stage 32: Release

**Build.**

- A **LICENSE**, which is the user's to choose, and the packaging step run without
  `--skip-license`.
- A **CHANGELOG**, starting at 1.0.0.
- **CI**: the gate on push, on Linux as well as Windows. `build-cc65.ps1` uses whatever
  `gcc` and `make` are present rather than assuming MinGW, and the corpus scripts add `.exe`
  only on Windows.
- **Publishing**: the tool to NuGet and the extension to the Marketplace, from a tagged
  commit, so that `dotnet tool install -g nt65` and the extension's page are how nt65 is
  installed. The accounts are the user's.
- **A quick reference**, `docs/REFERENCE.md`: the directives, the built-in functions, the
  signature items, the project keys and the command line, each in a line or two, with
  Appendix A as its seed. The design stays the definition; the reference is the page one
  keeps open.

**Check.** The gate passes in CI on both platforms; a clean machine installs both packages
from their registries and builds the SNES example.

## Deferred: the guide

`docs/GUIDE.md` is rewritten once, after Stage 32, in one pass:

- signature widths default to `*`, not `a8, i8`;
- the family idiom, in "Tricks the analysis needs told about" or a section of its own, and
  the macro-per-instance idiom for two or three routines;
- element indexing and zero-padded text in "Data and types", and the struct's pad for
  anything else;
- digit separators and `\0` in "What stays the same";
- the editor section: the Problems panel from a terminal build, the formatter, call
  hierarchy, doc comments on hover;
- `nt65 init`, `nt65 fmt`, `--check` and `--watch` in "Getting started";
- the migration tables, checked against the Stage 25 diagnostics.
