# nt65 for VS Code

nt65 is an assembly language for the 6502, its CMOS variants and the 65816 that transpiles to
ca65. This extension gives its editor support: highlighting, the Norristown language server,
build tasks and a schema for the project file.

## What it gives you

- **Whole-program analysis as you type.** The server reads every file of the project, not only
  the ones you have open, and reports on every one of them: a broken export is a problem in
  each module that named it, and none of them has to be open. Go to definition, find
  references, call hierarchy, document highlights, rename, hover, completion, signature help,
  code actions, code lenses, document and workspace symbols, links to what an `.incbin`
  names, and folding.
- **What each routine costs and preserves**, on the line that opens it, and on each inline
  `.scope` block of one: the shortest and longest path through it, what it costs with
  everything it calls, and which of A, X, Y and the carry it hands back as it was entered with
  them — `preserves X, Y, C`, or `preserves X, ?` where a call could not be followed and it may
  preserve more. It is on hover too, because lenses can be turned off; and hovering an
  instruction shows what each register holds at that line. A hover's grid of facts is
  coloured from a grammar this extension contributes, so the keys, what the analysis could
  not work out and what is about the block rather than the line each read as themselves.
- **Where an instruction's inputs come from.** Rest the caret on an instruction and each line
  that set a value it reads is tinted in that value's colour, with a bar at its left edge, a
  tag naming the value and a mark in the scrollbar. On a `jsr`, the values are the ones the
  routine called reads, so you see what is passed to it. A line the value only passed through,
  such as the `pla` that restored it or a call that keeps it, gets a dotted bar. The caret line
  gets a short chip per value: `A↑12` for a source 12 lines up, `A ×2` for two of them, `A↰`
  for a value the routine's caller set, `A?` where the analysis lost track. Hover the line for
  each source and its code, and for why the analysis lost track. Values in memory are followed
  too, as a best guess drawn with dashes: a location the routine reads before writing it has
  the last store to it as its source, and a line that might also have changed it, such as a
  store through a pointer, gets a thin dashed bar and a faded tag ending in `?`. **Shift+Alt+PageDown** and
  **Shift+Alt+PageUp** step through the sources, and **nt65: Peek Input Sources** lists them.
- **Loops and where control goes.** Set `nt65.margin` to `loops` and each loop of every
  routine has a bracket in front of its lines, nested where loops nest, and its trip count
  after its last line: `×16` for a loop that counts a register down from 16, `×?` where the
  program does not say. The bracket of the innermost loop around the caret is brighter. Set it
  to `flow` and the branches and jumps of the caret's routine are drawn too, as arrows from
  each one to the label it goes to.
  Brackets take the outer columns and arrows the inner ones, with shorter arrows nearer the
  code, and a bracket that starts at its loop's label stands for the branches back to it. The
  arrow on the caret's line is drawn thicker and in its own color, one a `.next` declares has
  another color, and a branch the flags prove always or never taken is faded. Calls, lines that
  run on into the next, and transfers to other routines are not drawn. Brackets and arrows take
  at most four columns, and the hover names any arrow that had no room.
- **Widths beside the line numbers.** On the 65816 each line gets an icon with two stripes,
  the width of A on the left and of X and Y on the right, bright for 16 bits and dim for 8.
  Both stripes are purple in emulation mode, and a width nt65 does not know has no stripe. A
  line shows the widths its own instruction runs with, so a `rep` or `sep` shows the widths
  before it, and the inlay hint at its end says what they become. The status bar, beside the
  caret's position, shows the caret line's stripes with the register each stands for, such as
  `A 8  XY 16`, and its tooltip is the key to the colors.
- **Where the direct page goes.** The nt65 view in the activity bar lists every direct page the
  program reaches memory through: each `D` with its segments, the locations in it and the
  routines that use each one, down the calls that reach them. Colour says how a location is
  shared, a glyph says what each routine does with it (`↓` in, `↑` out, `↕` both, `◦` temp),
  `⧉` marks pages that overlap and `⚠` a hazard, which the toolbar hides and shows. Selecting
  a row marks the lines it stands for, with a solid bar where they write and a dashed one where
  they only read. Code that runs with `D` not known is grouped under `D = ?`. **Show Grid**
  opens a page as 16 rows of 16 bytes beside the source, with each location's bytes outlined
  and brighter the more they are used, what is free, and what the page shares with others,
  with a legend under it. With the caret in a routine, both views mark the locations it uses,
  and the one under the caret more strongly.
- **What this became, beside the source.** **nt65: Show Output Beside** opens the ca65 for the
  file you are in, as the program stands with whatever you have not saved, and the caret is the
  link: move in the source and the lines it became are highlighted and scrolled to; move in the
  output and the line that wrote them is highlighted. It follows your edits, and a file with
  errors shows what could be written under a first line saying so. `nt65 build --stdout` writes
  the same text.
- **What a macro call becomes.** Hovering a call says it in one line — `expands to 14 lines ·
  31 bytes · 58–64 cycles` — and shows the first eight lines of the expansion, in nt65 and not
  in ca65: the body with your arguments in place. **Show expansion** opens the rest beside the
  file, with the calls inside it left as calls and a lens on each to open that one too, one
  level at a time. **Inline `name!`**, under the light bulb, writes the expansion where the
  call was; where that would change what the line means it is offered greyed, with the reason.
- **What a macro parameter takes.** Hovering a parameter, or anything in the kind after its
  `:`, says it in words: `a constant from 0 to 15`, `an operand in imm or zp mode`, and for a
  mode, how an operand in it is written. Completion offers the kinds and the enums in scope
  after the `:` and the modes inside `operand(...)`, and at a call, the members of the enum a
  parameter takes or the words its `one(...)` lists. A member passed by its bare name is the
  member, for colour, hover, going to its definition and renaming it. In a condition,
  `.mode(src) == imm` and `reg == x` complete, colour and hover their words the same way, and
  a hover says when the parameter can never be the word compared.
- **Hints in the line, for what the line does not say.** A width, the mode, D or B changing on
  a line that does not spell it (`rep #$30` ` a16 i16`, `jsr widen` ` → a16`); `long` on a
  branch that became the five-byte form; a value nobody wrote (an enum member, a member's
  offset, a constant that is not a literal); parameter names at a macro call. Cycle counts are
  off until **nt65: Toggle Cycle Counts** or the status bar switches them on. A hint stands
  beside the code it is about, so on a line with a comment it moves the comment over; if you
  keep your comments in a column and would rather see hints only when you ask, set
  `"[nt65]": { "editor.inlayHints.enabled": "offUnlessPressed" }` and hold Ctrl+Alt.
- **The comment above a declaration** on hover and beside its completion. There is no
  doc-comment syntax to learn: the `;` lines directly above it, each on a line of its own, are
  what you had to say about it.
- **One layout, on Format Document or Format Selection.** nt65 has one way of laying a file
  out and no setting for it: leading whitespace means nothing to the language, so there is
  nothing to disagree about. It is the same layout `nt65 fmt` writes, so turning on **Format
  on Save** and running `nt65 fmt --check` in CI agree by construction.
- **Highlighting** from a TextMate grammar for a file the server has not read yet, and semantic
  highlighting from the server for one it has.
- **Build tasks**, under **Run Task → nt65**: one for the project's own settings and one for
  each named configuration in `nt65.json`. What the build reports lands in the Problems panel,
  with the position clickable.
- **`nt65.json` completion and validation**, from the schema the extension contributes: every
  key, the processors, the segment sizes and the shape of a define, a bank and a range.
- **The configuration the editor analyzes as**, in the status bar. Click it to build the
  program as `debug`, as `pal`, or as the project's own settings say.

## Getting started

Open a folder holding an `nt65.json`. The extension starts there, analyzes the whole program,
and offers its build tasks. Opening a single `.nt65` file with no project anywhere works too:
the file is its own program, and everything but the project's settings is the same.
**nt65: Restart Server** starts the server again where it has stopped answering.

The extension carries the language server and runs it on the installed .NET 10 runtime. The
`nt65` command is a separate .NET tool, which the build tasks run from the path; point
`nt65.cli.path` at it where it is somewhere else.

## Settings

| setting | |
|---|---|
| `nt65.configuration` | the named configuration the editor analyzes the program as; empty for the project's own settings |
| `nt65.server.path` | a language server to run in place of the one the extension carries |
| `nt65.cli.path` | the `nt65` command the build tasks run, in place of the one on the path |
| `nt65.inlayHints.stateChanges`, `.longBranches`, `.impliedValues`, `.parameterNames` | each kind of hint, on unless switched off |
| `nt65.sources.chipLength` | the most characters the chips after the caret line may take, 60 unless set; a `+N` box counts the rest, and 0 means no limit |
| `nt65.sources.enabled` | where the values an instruction reads were set, shown when the caret rests on it; on unless switched off, and **nt65: Toggle Input Sources** switches it |
| `nt65.widths.enabled` | the width stripes beside the line numbers on the 65816; on unless switched off, and **nt65: Toggle Width Stripes** switches it |
| `nt65.margin` | what is drawn in front of a routine's lines: `off`, `loops` for a bracket and trip count on each loop of every routine, or `flow` for the loops and the arrows of the routine at the caret; `off` unless set, and **nt65: Choose What the Margin Shows** picks one |
| `nt65.inlayHints.cycles` | cycle counts at the end of every instruction; off unless switched on |
| `nt65.lineLength` | the longest a line may be before the editor suggests laying out a call's arguments, a set or a list of parameters one item to a line, or writing a list directive's items as a block, 100 unless set; 0 for no suggestion |
| `nt65.diagnostics.onlyWhileOpen` | globs, in the form of `files` in nt65.json, naming files whose problems show only while they are open, such as test inputs that are wrong on purpose; none unless set |

## The language

The guide is a tour of nt65 for ca65 programmers, and how to migrate; the design document
defines the language, the output and the command. Both are in the repository this extension is
built from.
