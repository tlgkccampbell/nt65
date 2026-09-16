# nt65 — Plan to version 1

## Where this comes from

`PLAN.md` took nt65 from nothing to a working proof of concept (Stages 0–14). This plan takes
it to version 1. It comes from a design review that included three usability studies. In each,
a realistic program was written in nt65, transpiled, assembled with the pinned ca65 and linked
with ld65:

- a Commodore 64 game on the 6502, about 750 lines in 8 files;
- a LoROM SNES program on the 65816, about 900 lines in 7 files;
- a mixed cc65 project on the 65C02: nt65 modules called from C-style ca65, calling
  hand-written ca65, with checked imports, a constructor and `.incbin`.

All three built, but only with workarounds. The language held up. The 65816 width,
direct-page and data-bank checks caught every planted mistake, with messages that said
what to do. The problems are these:

- several bugs that emit wrong code silently;
- output that ca65 rejects;
- design features that were never finished;
- annotation and declaration overhead that real programs find tedious;
- a build workflow that doesn't fit a make-based project.

The working rules of `PLAN.md` still apply, with one difference: the design decisions for
Stages 16–22 were made with the user during the review and are recorded here. A stage builds
what its **Decided** list says. Anything that turns out not to work is raised again before it
changes. Each decision goes into `DESIGN.md` in the stage that builds it, and §16 of the design
records it with its reasoning. Stages continue `PLAN.md`'s numbering.

**Version 1 means:**

- every construct `DESIGN.md` describes works;
- nothing nt65 accepts produces wrong bytes or output ca65 rejects;
- the decisions below are built;
- nt65 fits into a make-based build.

After version 1, `DESIGN.md` stops being a draft, and the language, output and project file
become contracts that change deliberately.

## Order and priorities

| priority | stages | why |
|---|---|---|
| P0: correctness | 15, 16 | Wrong code with no error is the worst thing an assembler can do. Nothing else is worth building on it. |
| P1: finish the design | 17 | Features the design promises and the implementation defers. |
| P2: language changes | 18–21 | The awkward parts the studies hit. |
| P3: workflow and editor | 22, 23 | What a real project needs around the language. |
| Release | 24 | Freeze and document. |

Dependencies between the later stages:

- Stage 18 (data declarations) comes before Stage 19, because exporting a `.data` block exports
  its members, and both stages rewrite every fixture.
- Stage 19 (modules) comes before Stage 22, because output paths are named by module.
- Stage 21's `.config` and Stage 22's configurations also use qualified module names.
- Stages 20 and 21 are otherwise independent and can be reordered.
- Stage 23 can run alongside any of them, but its completion and code actions should follow
  Stage 19.

A mark on each item:

- **(V)** reproduced during the review.
- **(R)** reported by a study and not yet reproduced. Reproduce it first as a failing fixture; if
  it does not reproduce, drop it and say so.

---

## Stage 15: A corpus of real programs, and wrong code

**Why first.** The fixtures test each construct in the form its section of the design shows,
so they miss combinations real code uses: an indented `.if`, a macro inside a block argument,
fields of an instance in a zero-page segment. The fixes in Stages 15 and 16 need tests that
look like programs.

**Build.**

- **A corpus**, `tests/corpus/`: the three study programs, copied in as written (`c64` on the
  6502, `snes` on the 65816, `interop` for cc65 on the 65C02). Each has an `nt65.json`, a linker
  config, a `build.sh` and, for `interop`, hand-written ca65 and C. Today `snes` and `interop`
  build and link, and `c64` fails in ca65 on its exported charmap (Stage 16).
  - Wire every program into the oracle, which assembles it with no warnings, compares listing
    lengths and links it with ld65.
  - Remove the studies' workarounds: where a program exercises a bug below, write it the
    natural way, so the bug makes it fail.
  - Later stages rewrite the corpus to use each language change as it lands.
  - The corpus is part of the oracle suite and the gate, not the edit loop.
  - Keep each program to the size that exercises its target, so the oracle stays parallel
    and cached.
  - Record the gate time before and after.
- **Wrong code with no error**, each with a fixture first:
  - **(V)** An indented `.if` ignores its condition and emits every branch, inside a proc and at
    file level. At column 0 it works. Leading whitespace must not matter. Audit every
    line-kind decision for the same dependence on the first column.
  - **(V)** A block-macro call inside another macro's block argument is dropped from the
    expansion.
  - **(V)** Fields of a `.tag` instance are sized as far on the 65816 and absolute on the 6502,
    whatever their segment. `lda pos::py` on a zero-page instance must be `z:`. The data-bank
    check must see these operands.
  - **(V)** `.asciiz text`, with `text` a macro parameter bound to a string, loses its `$00`.
    (Stage 21 renames `.asciiz` to `.strz`.)
  - **(V)** A struct member with an operand (`colors: .word 16`) is accepted and the operand
    ignored. It is an error; Stage 18 adds array members as `.word[16]`.
  - **(R)** A string given to an `.addr` member of an initialized instance emits one address per
    character, overflowing the member.
  - **(R)** `.next tbl`, where `tbl` labels a line of its own followed by `.addr` lines, silently
    makes the processor state unknown. Report that `tbl` holds no code labels, until Stage 18
    makes such a table a declaration (`.data tbl: .addr[] { … }`) that `.next` can see into.
  - **(V)** `.addr` holds a far symbol or a constant above `$FFFF` with no error from nt65 or ca65,
    and silently keeps 16 bits: `.addr far_label` drops the bank. nt65 checks constant ranges for
    `.byte` and `.word` but not for `.addr` or `.faraddr`. Make `.addr` of a far symbol an error
    that suggests `.faraddr`, or `.loword(x)` when the 16 bits are meant, and range-check both
    address types (`.addr` 0..$FFFF, `.faraddr` 0..$FFFFFF).
  - **(R)** An extern-proc alias (`.proc r_long = r: ..., far`) is not checked against the routine
    it names, so near code can be reached with `jsl`. Check what the alias declares against the
    target's signature.

**Check.**

- Every item above is a fixture that failed before its fix.
- The three corpus programs build, assemble with no warnings, match listing lengths and link.
- The C64 program built with `DEBUG=0` contains no debug code.

## Stage 16: Output ca65 rejects, and diagnostics that mislead

`DESIGN.md` says a ca65 error or warning on nt65 output is an nt65 bug. Each of these is one.

**Build.**

- **Output ca65 rejects:**
  - **(V)** An `.assert` that ca65 has to evaluate is written with nt65 operators (`==`, `!=`,
    `&&`). Translate every operator for ca65, and parenthesize as for other expressions.
  - **(V)** `wai` and `stp` are accepted on the 65C02, and the header's `.setcpu "65C02"` lacks
    them. Stage 21 makes `65c02` the full WDC set and adds variants; here, emit
    `.setcpu "W65C02"` for `65c02` (checked on the pinned ca65: `wai`, `stp`, `bbr0`, `stz` and
    `(zp)` all assemble), and recheck the header.
  - **(V)** Re-exporting an import emits `.exportzp` and `.importzp` of one name. It is an nt65
    error until Stage 19 builds re-exports.
  - **(V)** `.byte -1` and `.word -2` reach ca65, which rejects them. It is an nt65 error until
    Stage 21 builds signed ranges.
  - **(V)** `MSG = "hello"` is accepted and emitted as `.byte MSG`, with `MSG` never defined. It
    is an error until Stage 21 builds string constants.
  - **(V)** `.word far_label` is accepted and reaches ca65, which rejects it ("Address size 3 does
    not match fragment size 2"). It is an nt65 error that suggests `.faraddr`, or `.loword(x)`
    for the low 16 bits. The same holds for any 1- or 2-byte slot given a wider symbol.
  - **(R)** `.export` of a charmap, `.func`, `.list` or define emits `.export` of a symbol that does
    not exist. They are used by value and emit no ca65 export.
  - **(V)** `.countof(Enum)`, and `.sizeof` of a label with no data on its line (above data lines,
    or on code in a proc), reach ca65 unevaluated, and ca65 rejects them.
    - Evaluate `.countof(Enum)` as its number of members.
    - `.sizeof`, `.countof`, `.endof` and `.spanof` of a label with no data on its line are an
      nt65 error.
    - A label with data on its line keeps its size here only so the corpus builds before
      `.data` exists. Stage 18 makes every label only a location, with no size at all.
  - **(R)** A named `.scope` used as an address becomes an undefined symbol. It is an nt65 error:
    Stage 18 makes a scope only a namespace.
  - **(R)** `pei (sym)` with `sym` in an absolute segment reaches ca65. It is an addressing-mode
    error.
  - **(R)** Constants alone open `.segment "CODE"`, so a linker config without `CODE` fails. A
    constant needs no segment. Emit constants before any segment, or in the segment of what is
    around them.
  - **(R)** Operands such as `sta buf,y`, `jmp (vec)` and `pei (zp)` have no size prefix, although
    the design says every choice is explicit. Make them explicit, or record why a mode is
    exempt.
- **Diagnostics that mislead:**
  - **(R)** A macro that takes no block, called inside a block argument, is reported as given a
    block, at the outer call's line and naming the inner macro.
  - **(R)** `jml` to a near routine says the routine "is jumped to with `jmp`". Say what to write.
  - **(R)** A 16-bit immediate with an unknown width also reports that it does not fit in one
    byte.
  - **(R)** The unknown-width hint always suggests `.state`. Where the cause is a construct (a call
    with no signature, an empty `.next`, an `a?` entry), point at it and suggest `.ensure` where
    that is the fix.
  - **(V)** Cascades. A reserved-word parameter also reports "takes 0 arguments" at the call and
    an error at every use. **(R)** One missing signature gives six errors at the `rts`. A
    non-exported `.func` used eight times gives eight identical errors. "Not transpiled yet"
    follows real errors. Report the cause once.
  - **(R)** `.assert`, `.import` and `.export` lines carry no `.dbg line`, so ca65 and ld65 blame
    the line before. Give every line that can fail in ca65 or ld65 a `.dbg line`, not only
    lines that emit bytes; recheck what ld65 records for them.
  - **(R)** An empty `jsr` says "does not take this operand" rather than "needs an operand".

**Check.**

- The corpus programs have no workarounds left for these items. The oracle assembles them
  with no warnings.
- A fixture for each diagnostic shows exactly the one message expected.

## Stage 17: Finish what the design describes

**Build.**

- **(V)** Declarations in `.repeat` and `.each` bodies. Cheap locals and labels declared there
  are an error ("arrives with macro expansion"). The design gives each iteration its own names,
  and a `.repeat` holding a loop with `@l:` is ordinary code. Use the per-expansion naming that
  macros already have.
- **(V)** `.each Cmd, c { .addr actions::c }`. A binding over an enum at the end of a path names
  the member with the same spelling. It is the design's replacement for `.ident`, and does not
  work. After Stage 18 the same loop is written in a body: `.data handlers: .addr[] { .each … }`.
- **(V)** Unused-symbol warnings, which the tooling section lists. Scope them so they help:
  unexported labels, constants, macros and types nothing names. Treat an exported symbol as used.
- **(V)** Members of a named enum may be register names (`.enum Reg { a x }`), as the design's
  reserved-word rules say. Structs already allow it. Register names stay reserved everywhere
  else (decided: no change).
- **(R)** `emu` in a signature fixes both widths at 8, as it does in `.state`, and does not carry
  into the exit's defaults so that `emu -> a8, i16` needs `native` repeated.
- Sweep `DESIGN.md` against the implementation for any other described-but-deferred behaviour.
  Search the code for messages that promise a later stage.

**Check.**

- The design's `.repeat`, `.each` and enum-table examples are fixtures that pass the oracle.
- No diagnostic refers to a stage.

---

## Stage 18: Declarations own their data

**The problem.**

- **(V)** A label's size depends on what happens to follow it. Only a label on the same line as a
  data directive gets `.sizeof` and `.countof`, and only for that line:
  `tbl: .byte 1, 2` followed by `.byte 3` has a `.sizeof` of 2. ca65 code usually puts a label
  on its own line over many data lines, and in nt65 that label has no size at all.
- A label may not precede `.repeat`.
- A table of initialized records has no count.
- A struct cannot have an array of words.
- A string member always pads with zeros.
- Code outside a proc gets no state analysis, and on the 65816 cannot use `rep`, `sep` or a
  width-dependent immediate.
- A named scope is not an address, although `.spanof` treats it as one.

The root cause is that a label is treated as a declaration, when it is only a position.
Assembly-like formats built for analysis (HLA, WebAssembly text, LLVM IR) have no free-standing
labels outside a routine body: every name for data is a declaration with an extent. nt65 adopts
that, and a label becomes only a location.

**Decided.**

1. **A label is only a location.** `name:` and `@name:` name an address and nothing else, wherever
   they appear and whatever follows them on the line. `tbl: .byte 1, 2` is a label followed by
   data: `tbl` has no size, count or end. `.sizeof`, `.countof`, `.endof` and `.spanof` of a label
   are errors.
2. **Outside a proc, there are no instructions and no labels.**
   - Code lives in `.proc`. An instruction or a label outside one is an error. This holds at file
     level and inside `.scope`, `.if`, `.repeat`, `.each` and segment blocks.
   - Every byte emitted outside a proc belongs to a `.data` declaration. The one exception is
     unnamed `.align` and `.res`, allowed between declarations as padding.
   - Inside a proc, labels are unchanged: positions in code, which covers inline data after a
     call, jump tables and interior entry points. A `.data` declaration may also appear there.
3. **Every data declaration starts with `.data`,** as every routine starts with `.proc`. The
   element types are `.byte`, `.word`, `.dword`, `.addr`, `.faraddr` (and Stage 21's `.long` and
   big-endian types), and `.type T` for a struct or union `T`, which replaces ca65's `.tag T`.

   | form | meaning |
   |---|---|
   | `.data player_x: .word` | one element, no value |
   | `.data buffer: .word[16]` | 16 elements, no values |
   | `.data gradient: .byte 40, $e0, 0` | values on one line; the count is the number of values |
   | `.data row_lo: .byte[] { … }` | values in a body; the count is the body's |
   | `.data handlers: .addr[16] { … }` | values in a body, which must hold exactly 16 |
   | `.data header: .type RomHeader { title = … }` | one record with an initializer |
   | `.data sprites: .type Sprite[] { {…} {…} }` | an array of records |
   | `.data tiles: .incbin "tiles.bin"`, `.data msg: .strz "hi"` | bytes |
   | `.data basic_stub { … }` | mixed contents (rule 5) |

   A line break never changes what a name means: a size can only come from `.data`.

   `.tag T, n` becomes `.type T[n]`. The brackets are what tell one initialized record,
   `.type T { … }`, from an array of them, `.type T[] { … }`. A value in a data directive can
   never start with `[`, so `.word[16]` (sixteen reserved words) and `.word 16` (one word holding
   16) cannot be confused. A built-in type and a declared one are both dotted: a bare type name
   would sit beside instructions in a proc and read like one.

   **`.res` is only padding.** Storage is declared with its type, `.data ptr: .word` or
   `.data buffer: .byte[64]`. `.res n [, fill]` stays for unnamed padding between declarations,
   inside a `.data` body, and for a struct's string member (rule 8). `.data ptr: .res 2` is an
   error that suggests `.byte[2]`.
4. **Bodies and counts.**
   - A body line is a list of values separated by commas, one element each. For `.byte`, a
     string is one element per character. For `.type T[]`, each element is a braced initializer.
   - A body may hold `.repeat`, `.each` and `.if` blocks whose lines are of the same kind.
   - A body may open and close on one line: `.data lut: .byte[4] { 1, 2, 4, 8 }`.
   - `[n]` with a body checks the count exactly. A body shorter than its count is an error, not
     zero-filled, because a short jump table is exactly the mistake a count should catch. Padding
     is written with a `.repeat` in the body.
   - `.data name: .byte 1, 2` and `.data name: .byte[] { 1, 2 }` are the same data. Both stay:
     the second is for a count to check or a body that spans lines.
5. **Mixed data: `.data name { … }`.** The name is required. The body may hold:
   - unnamed data directives of any kind;
   - nested `.data` declarations, which become members reached as `name::sub` and emitted as
     `name__sub`, nested to any depth;
   - `@label:` positions, private to the block, which may be referenced inside it but have no
     size and cannot be exported;
   - unnamed `.align` and `.res`;
   - `.repeat`, `.each` and `.if`.

   It may not hold instructions. For example:
   ```nt65
   .data vectors {
       .data native: .addr[8] {
           0, 0, cop_stub, brk_stub, brk_stub, nmi_stub, 0, irq_stub
       }
       .data emulation: .addr[8] {
           0, 0, brk_stub, 0, brk_stub, brk_stub, reset_stub, brk_stub
       }
   }
   ```
6. **Sizes.**

   | declared as | `.sizeof` | `.countof` |
   |---|---|---|
   | an element directive, any form | elements × element size | elements |
   | `.incbin`, `.strz` | bytes | bytes |
   | `.data` | bytes | error |
   | `.proc` | bytes in its body | error |
   | a label | error | error |
   | `.scope` | error | error |

   These four functions work only on a named extent: a data declaration, a `.data` member, a
   proc, a struct or union, and for `.countof` an enum. `.endof` and `.spanof` work on every one
   that has an address. `.sizeof(T)` is the stride of a record array.

   **`.next` reads targets from address-typed data:** an `.addr` or `.faraddr` declaration, or
   such a member of a `.data` block. Today only `.addr` counts, so a `jml` dispatch table written
   with `.faraddr` gives the flow analysis no targets. A `.next` to data of any other type is an
   error that suggests the address type.
7. **Struct members use the same forms:** `x: .byte`, `colors: .word[16]`, `pos: .type Point[4]`.
   - An initializer for an array member is always a braced list, `colors = { $7fff, $001f }`, in
     both the one-line and multi-line forms. A list given must match the count; a member left out
     of an initializer is zero, as today.
   - A struct member with values (`colors: .word 16`) stays an error.
8. **A string member declares its pad:** `title: .res 21, ' '`. An initializer's string is
   padded with it, an uninitialized instance is filled with it, and `.res n` alone pads with
   zero.
9. **A `.scope` is only a namespace.** It has no address: `.spanof`, `.endof` and use as an
   operand are errors. A named block of code is a proc, and a named block of data is `.data`.
   Inside a proc, an anonymous `.scope { }` still lets cheap locals be reused.
10. **The segment shortcuts are removed.** `.zeropage { }`, `.code { }`, `.bss { }`, `.data { }`
    and `.rodata { }` are written `.segment ZEROPAGE { }` and so on, which frees `.data` for
    declarations. The standard segment names stay predeclared.
11. **Segment names are identifiers:** `.segment RODATA { }`, `.segment ZP2: zp, dp = $2100`.
    ca65 quotes them because its segment names share a namespace with symbols. nt65 declares
    segments in a table of their own, so a segment and a symbol may share a name. `nt65.json` is
    unchanged, and the output still quotes them for ca65.
12. **Segment regions place top-level items without indenting them.** `.segment NAME` without
    braces, at file level, places every item from that line to the next region line or the end of
    the file. It is C#'s file-scoped namespace applied to segments.
    - A region is structure, not a mode. It is found from lines alone, like braces, and an edit to
      a region line affects only the lines up to the next one. A region line inside any block
      (`.scope`, `.if`, `.repeat`, `.each`, a proc) is an error, and there is no push or pop.
    - The braced form stays, anywhere an item may appear: a detour inside a proc, or a one-off
      within a region.
    - `.segment ZP2: zp` is still a declaration, not a region. A size after `:` tells them apart.
    - Bytes outside any region or block are an error. There is no implicit `CODE`. Items that emit
      nothing (constants, types, macros, imports) may sit anywhere, including before the first
      region.
    - The design's convention of not indenting top-level segment blocks goes away, and its
      decision for segment blocks over per-item attributes stands.

**Build.** The decisions above, and Appendix A of the design to match. Remove the Stage 15 and 16
interim errors they replace. Move every fixture and corpus program to the new forms: data
labels become `.data` declarations, every size query names one, `.tag` becomes `.type`,
declarations with `.res` take their type, and top-level segment blocks and shortcuts become
regions. Rewrite the corpus's tables,
record tables, vectors and headers the natural way, dropping the `.assert`s that a count now
expresses (`ship_shape`, `_cmd_table`, `sprites_end`).

**Check.**

- `.sizeof`, `.countof`, `.endof` and `.spanof` of each form, and of nested `.data` members, agree
  with the bytes ca65 emits, including a record array indexed with its stride.
- A body with too few or too many elements is an error that names both counts.
- A top-level instruction or label is an error that says to use a proc or a `.data` declaration.
- `.sizeof` of a label, including one with data on its line, is an error.
- Every data line in the corpus belongs to a declaration.

## Stage 19: Modules, exports and names

The largest language change in this plan: every file becomes a named module, and names from
other modules are no longer visible implicitly.

**The problem.**

- **(V)** A local declaration silently shadows another file's export, and all exports share one
  flat namespace.
- Every export is written twice.
- A scope's contents cannot be exported.
- Imports cannot be re-exported.
- An export's address size cannot be chosen.
- **(V)** Every mnemonic of every CPU is reserved in every program.

**Decided.**

1. **Modules.**
   - Every file declares `.module name`, once, before its other items. The name may be
     qualified (`.module gfx::sprite`).
   - A file without one is an error, including in a single-file build.
   - **Each module is one file:** two files declaring the same module is an error. A module is
     then exactly one ca65 translation unit, which buys two things no multi-file module can
     have:
     - *Privacy the linker respects.* ca65 can share a name between files only by exporting it
       to ld65, whose namespace is global. Within one `.s`, an unexported name is invisible to
       every other object.
     - *No hidden byte order.* Merging a module's files into one `.s` would make nt65 choose the
       order of their bytes in each segment, which moves with a file rename. Across objects, the
       order is the link order the build states.

     It also keeps the build one source, one `.s`, one `.o` (Stage 22). A large module is split
     into submodules (`hw::vic`, `hw::sid`), which share names by exporting them. Narrower export
     visibility, like Rust's `pub(super)`, can follow version 1 if splitting proves common.
   - **A qualified module name is only a name.** `gfx::sprite` needs no module `gfx`, and has no
     special visibility into `gfx` or its siblings. There are no relative paths (`super::`,
     `self::`).
2. **Using another module's names.**
   - Qualified: `hw::init`, `hw::vic::border`.
   - Or brought in with `.use`:
     - `.use hw::init` brings in one name;
     - `.use hw::{init, BORDER}` brings in several;
     - `.use hw::*` brings in everything the module exports;
     - `.use hw::init as hw_init` and `.use very::long::path as p` rename what they bring in,
       which is how a collision is resolved without writing the full path everywhere.
   - Precedence:
     - A local declaration beats a name from `.use hw::*`.
     - A name brought in by two `*` imports is an error only where it is used.
     - A name `.use` brings in explicitly that collides with a local declaration is an error.
     - Adding an export to one module can never silently change what a name in another module
       means.
   - Decide while building: how a qualified name's first part resolves when a local scope has
     the same name as a module. Proposal: local scopes first, and `::` alone reaches the module
     root, replacing its current meaning of file scope, which the file's own module name now
     covers.
3. **Linker names are qualified by module.** Modules stay apart at link time, as Rust, C# and
   Go keep them apart by mangling.
   - An export's ca65 symbol is its full path flattened with `__`: `init` in `.module gfx::sprite`
     is `gfx__sprite__init`, and an interior, scoped or `.data` member name continues the path
     (`hw__vectors__native`).
   - `.export init as "_memfill"` sets the linker name exactly, the way `#[no_mangle]` and
     `export_name` do in Rust. This is how a symbol meant for C or hand-written ca65 gets its name,
     and cc65's leading underscore is written out.
   - Unexported names are private to their `.s` and keep today's local spellings (`draw__loop`).
   - Two linker names that still collide, through an `as` or an identifier containing `__`, are an
     nt65 error.
   - The design's interoperability contract changes: an export is no longer spelled as its nt65
     name.
4. **Export at the declaration.** `.export` may prefix a declaration: `.export BORDER = $D020`,
   `.export .data ptr: .word`, `.export .proc init {`, `.export .data vectors {`, `.export .macro ...`,
   `.export .enum ...`. The list form stays, for interior labels, members, re-exports, `as` and
   sizes.
5. **Exporting a named scope exports what it declares,** through nested named scopes, reached as
   `gfx::clear` and emitted under the module's path (`gfx__clear` in module `gfx`). It stops at a
   proc's interior labels, which are exported one by one, and never exports cheap locals. A
   scope has no address of its own (Stage 18).
   - Exporting a `.data` block exports its address and its named members, through nested
     members, in the same way. Its `@` positions are never exported.
   - `as` renames a member like any other export: `.export vectors::native as "native_vectors"`.
6. **Re-exports.**
   - **Imports.** An `.import` may be exported. Other modules use it as if they had declared the
     import: same size, signature and checked value. In the output, each module that uses it
     writes its own `.import`. The re-exporting module writes no ca65 export, and writes the
     `lderror` assertion of a checked import once.
   - **Another module's names.** `.export .use hw::vic::border` makes `border` part of this
     module's interface, as Rust's `pub use` and TypeScript's `export { } from` do, so a facade
     module can present names its submodules define. Users reach it as `hw::border`, and it keeps
     the linker name of its definition: a re-export emits nothing. Explicit names and braced lists
     only: `.export .use hw::vic::*` is an error, because glob re-exports grow an interface
     silently.
   - **Macros.** An exported macro whose body names something its module does not export is an
     error at the macro's declaration ("`poke!` is exported but names `shadow`, which is not"),
     because the body resolves names where it is declared and the expansion in another module
     could not link. It is not exported implicitly.
7. **Export sizes.** `.export K: abs` in the list form, the same spelling as `.import`.
   Widening (zp to abs or far) is allowed, and narrowing below what nt65 knows is an error.
8. **Mnemonics are reserved by the program's CPU.**
   - Parsing is unchanged: a line starting with any known mnemonic is an instruction, and one
     the CPU lacks is a wrong-CPU error.
   - A name that is not a mnemonic of the program's CPU may be declared, and `ident :` is always
     a label.
   - The design states that changing a program's CPU can make an existing name reserved.
   - Register names stay reserved everywhere they are today.

Cheap locals in top-level segment blocks, reported by the studies, need no decision: after
Stage 18 there are no labels outside a proc, and `@` positions belong inside a `.data` block.

**Build.** The decisions above, and in the editor:

- resolution, definition, references and rename through qualified names, `.use`, `as` and
  re-exports;
- a rename across modules updates `.use` lists.

Move every fixture and corpus program to modules. Rewrite the corpus's hardware and cc65
runtime modules to export at declaration, export scopes, re-export imports and use a facade.
Give every symbol the interop program shares with C or hand-written ca65 an `as` name
(`_memfill`, `_sprites`), and update its ca65 sources for the names that are now qualified.
Remove Stage 16's interim re-export error.

**Check.**

- The interop program shares one runtime-import module across files and links with no
  address-size warnings.
- Two modules exporting `init` build and link, used qualified or through `.use … as`. Two `as`
  names that collide are an nt65 error.
- A facade module re-exports names from two submodules, and a user of the facade links.
- An exported macro that names a private symbol is an error at its declaration.
- A 6502 program declares `REP` and `per`.

## Stage 20: 65816 routines with less ceremony

**The problem.**

- 22 of 33 procs in the SNES study repeat `a8, i16, dp = 0, dbr = $80`.
- A routine that never returns must invent an exit state.
- Interrupt handlers spell out every unknown item and must pick `near` or `far`.
- **(R)** A segment has one bank, but low WRAM and FastROM code are mirrored.
- **(R)** `txs`, `phk`, `plb` loses B.
- **(R)** `mvn #^src, #^dst` does not set B.
- **(R)** `.frame` cannot reach arguments the caller pushed.

**Decided.**

1. **Named signature sets.**
   - `.signature std = a8, i16, dp = 0, dbr = $80` declares a set, which is exported and used
     across modules like a constant.
   - A set is used as an item: `.proc f: std`, `.proc g: std, a16 -> std`.
   - Items after a set override the set's items.
   - Built-in defaults are unchanged, and there are no per-file or per-segment defaults.
2. **An exit of `-> none`** marks a routine that never returns.
   - `rts` and `rtl` in it are errors.
   - A tail call from it checks only the target's entry, including near/far.
   - A call to it ends the path, so nothing after the call needs `.next ?`.
   - On every CPU, a proc ending in a call to a `none` routine does not fall off its end.
3. **An `interrupt` item:** `.proc nmi: interrupt { }`, or `interrupt, native` / `interrupt, emu`.
   - At entry, widths, D and B are unknown, and E is unknown unless `native` or `emu` is given.
   - It leaves by `rti`, and `rts`/`rtl` are errors.
   - It is neither near nor far, and tail calls from it check entry only.
   - `jsr`/`jsl` to it is an error; its address in data is fine.
   - It is accepted on the 6502 and 65C02 with the same `rti` and call checks.
4. **A segment has a home bank and mirrors:** `bank = $7e, mirrors = ["$00-$3f", "$80-$bf"]`,
   in files and in `nt65.json`.
   - An absolute data operand passes when B is the home bank or any mirror.
   - `phk`/`plb` and the cross-bank `jsr`/`jmp` checks use the home bank. The design states that
     code is taken to run in its home bank.
   - A long jump or call to a routine's address in a mirror bank is allowed and checked, which
     covers a FastROM reset stub in bank `$00` jumping to `$80`. Decide while building how the
     mirror address is written (`jml f:reset` with a mirror rule, or an explicit bank
     expression).
5. **`bank` and `dbr` keep their spellings.** `bank` is where a segment lives, and `dbr` is the
   register's value. The design states the distinction; this is not an inconsistency.
6. **The analysis stack is an unknown base with a known top.** After `txs` or `tcs`, pushes are
   tracked on top of an unknown base, a pull finds what they pushed, and pulling past the known
   top gives unknown. This is the model `.frame` already uses after `tcs`, and it covers `txs`,
   `phk`, `plb` and `tcs`, `pea`, `pld`.
7. **`mvn`/`mvp` with `#^sym`** as the destination sets B to the home bank of `sym`'s segment,
   when it declares one.
8. **A signature item `args n`:** the caller has pushed n bytes before the call.
   - Inside the proc, the entry stack is those n bytes, then the return address (2 bytes near,
     3 far), and a `.frame` may lay them out.
   - At a call, the caller's known stack must hold at least n bytes, or be unknown.
   - After the call, the stack is unchanged: the caller removes the arguments.
   - Callee-cleaned arguments are not in version 1.

**Build.** The decisions above. Rewrite the SNES corpus program without the workarounds (the
`tsc`/`tcs` stack erasure, the unchecked alias, the dropped bank, the invented exit of `reset`).

**Check.**

- The SNES program's signatures shrink to a set plus what differs from it.
- Every planted mistake of the study is still caught.
- A `jsr` to an interrupt handler and an `rts` in a `none` routine are errors.

## Stage 21: Smaller language decisions

**Decided.**

1. **CPU variants.**
   - `65c02` remains the full WDC set (`.setcpu "W65C02"`, from Stage 16).
   - `r65c02` is the Rockwell set: the bit instructions, without `wai` and `stp`.
   - `65sc02` has neither the bit instructions nor `wai`/`stp`.
   - Each checks exactly its own instructions and emits the matching `.setcpu`; check each
     against the pinned ca65.
   - The CPU names are reserved where a CPU is named, and are accepted by `.cpu`, `--cpu` and
     `nt65.json`.
2. **`.has(mnemonic)`** is true when the program's CPU has that instruction
   (`.if .has(phx) { ... }`). `.target(cpu)` stays exact.
3. **A proc that runs off its end warns on the 6502 and its variants.** `.next next_proc` or
   `.next ?` silences it, as on the 65816, where it stays an error.
4. **String constants.**
   - `MSG = "hello"` is usable wherever a string literal is: data, `.strz`, charmap
     application, `.strlen`/`.strat`, `.type` members and macro arguments.
   - A string constant crosses modules by value and never reaches ca65 as a symbol.
   - There is no arithmetic or concatenation on strings, and no string defines.
5. **Signed data.**
   - A byte slot (`.byte`, an 8-bit immediate, a fill) takes -128..255, a word slot (`.word`,
     `.beword`) -32768..65535, a 24-bit slot (`.long`, `.belong`) -8388608..16777215, and a 32-bit
     slot (`.dword`, `.bedword`) the 32-bit equivalent.
   - Address slots are unsigned: `.addr` takes 0..$FFFF and `.faraddr` 0..$FFFFFF, and a negative
     constant is an error.
   - A constant is emitted as its two's-complement value, with the source in a comment.
   - Out of range is an nt65 error.
   - A value that is not constant is emitted as written.
6. **`.select(c, a, b)`**, a built-in function.
   - Only the chosen operand is evaluated and has its names checked.
   - The condition must be constant.
   - It is usable in constants, conditions, `.func` bodies and address expressions.
   - There is no `?:` operator.
7. **In-file defines: `.config NAME = value`.**
   - A `.config` may appear only at file level, outside any `.if`, and its value may use only
     literals, built-ins, project defines and other `.config` values.
   - Conditions may test it.
   - It is private to its module and exportable like a constant, reached as `hw::NAME` or
     through `.use`.
   - `nt65.json` and `-D` may override an exported one by its qualified name
     (`-D hw::SOUND_CHANNELS=2`), which makes the in-file value a default. Overriding a
     `.config` that is not exported is an error.
   - The design's decision record changes: in-file defines were rejected, and now exist under a
     spelling that is not ca65's `.define`.
8. **Directives:** `.warning "text"`; `.bankbytes`, alongside `.lobytes` and `.hibytes`; and two
   element types:
   - `.long`, a 24-bit value. `.faraddr` stays for addresses, and the C header generator tells
     the two apart.
   - Big-endian partners for each width: `.beword` (16), `.belong` (24) and `.bedword` (32). ca65
     has only `.dbyt`, which sits confusingly beside `.dword`, a 32-bit little-endian value. A
     full set means no one wonders which widths have one.
   - `.constructor`, `.destructor` and `.interruptor` are not added: cc65 startup
     registration stays in a ca65 stub that calls the nt65 routine.
   - The interop corpus keeps that stub, and the design's interoperability section says so.
9. **`.asciiz` becomes `.strz`,** and checks what it promises.
   - The name was DEC's `.ASCIZ`, "ASCII, zero-terminated", by way of ca65. In nt65 the text is
     ASCII only without a charmap, so only the terminator is worth naming.
   - It takes exactly one text: a string literal, a string constant or a charmap application.
     Numbers and further arguments are errors.
   - A `$00` inside the text is an error that names the character, as with `screen("A@B")` when
     `screen` maps `@` to `$00`. The string would end early, and a routine declared
     `inline .strz` would return into the middle of it.
   - The signature item is `inline .strz`.
10. **`.assert cond, "message"`,** with no severity. ca65's `error`, `warning`, `lderror` and
    `ldwarning` choose when a check runs, which nt65 decides itself: at edit time when it can,
    and otherwise at link time. A failed assert is always an error. The output still writes the
    severity ca65 needs.

**Build.** The decisions above, each as a fixture through the oracle. Remove the Stage 16 interim
errors for strings and negative values. Move the fixtures and corpus to `.strz` and the new
`.assert`.

**Check.** A program builds for `65sc02`, `r65c02` and `65c02` with `.has` choosing the code,
and each output assembles on its matching `.setcpu`.

---

## Stage 22: The build workflow

**Decided.**

1. **Output is named by module.** `.module gfx::sprite` is written to `out/gfx/sprite.s`,
   wherever the source is. Sources may live outside the project root, so a library shared
   between projects works, and moving a source does not move its output. Debug information
   names the real source path. The mapping is one to one because a module is one file
   (Stage 19).
2. **Named configurations** in `nt65.json`:
   ```json
   "defines": { "DEBUG": 0 },
   "configurations": {
     "debug": { "defines": { "DEBUG": 1 }, "out": "build/debug" },
     "pal":   { "defines": { "hw::PAL": 1 }, "out": "build/pal" }
   }
   ```
   - `--config name` selects one: it overrides the top-level `defines` and `out`, and `-D`
     overrides on top.
   - With no `--config`, the top-level settings build.
   - The editor has a setting for the active configuration.
3. **`nt65 build --c-header <file>`** writes a C header from what the program exports:
   - structs and unions as byte-exact C structs in cc65's types, with array members as arrays;
   - enums;
   - constants as `#define`;
   - data declarations as `extern`, with arrays sized by their count;
   - routines as `extern void name(void);`, with a comment in the file that their parameters
     are the programmer's to declare.
   - Every C name is the symbol's linker name (Stage 19), so a name meant for C is exported
     with `as`. The generator warns for an exported routine or data declaration without one.
   - Each exported struct or union also exports its size to ca65, under its linker name plus
     `__sizeof`.

**Build.** The decisions above, and:

- **Command line:**
  - `--project <nt65.json>` and `--out <dir>`;
  - `nt65.json` found by looking upward from the current directory;
  - `--help` and `--version`.
- **Dependencies.** `--depfile` writes make-style dependencies: each output depends on its
  source, the modules whose interfaces it uses and its `.incbin` files.
- **Stale output.** An output whose module was removed is deleted. nt65 keeps a manifest of what
  it wrote under `out`, and deletes only files it wrote.
- **Single files.**
  - Naming a file on the command line drops the rest of the project, and an undeclared name then
    reads like a typo. Build the named files within the project, or say that the name belongs to
    a module not in this build.
  - Without a project or `--cpu`, the CPU defaults to 6502 silently. Require a CPU, or say which
    one was assumed.
- **(R)** **Debug paths.** `.dbg file` is relative to the output file, which ca65 records verbatim,
  so debug files and ca65 notes point outside the project. Decide the spelling, and check
  with `ld65 --dbgfile` from the directory a build normally runs in. Normalize `.incbin` paths
  too.
- **(R)** **Unused imports.**
  - A checked import nothing uses is still emitted and pulls its module out of an `ar65`
    library.
  - `jmp outer::inner` imports `outer` as well as `outer__inner`.
  - Emit only what is used.

**Check.**

- The interop corpus program builds with a plain Makefile that uses `--depfile`,
  `--config` and no marker files.
- Its C code uses the generated header, and a reordered struct member changes the header.
- A library outside the project root builds into `out`.

## Stage 23: The editor

**Build.**

- **Project discovery.** An `nt65.json` in a subfolder of the workspace, and more than one
  project in a workspace.
- **Watching files.** Changes to `nt65.json`, to files not open in the editor, to `.incbin`
  inputs, and files created or deleted, through `workspace/didChangeWatchedFiles`.
- **The active configuration** (Stage 22), chosen in the client, with inactive code dimmed for
  it.
- **Completion.**
  - After `::` and in `.use`, where it includes modules;
  - in operand position;
  - in signatures, `.state` and signature sets;
  - for macro parameters and named arguments.
- **Signature help** for macro calls, `.func` calls and `.select`.
- **Inlay hints** for cycle intervals per instruction and per block, and for the state reaching
  a label on the 65816. Planned in Stage 10 and delivered only as hover.
- **Workspace symbols.**
- **Code actions** for the diagnostics that name their fix:
  - add `.next ?`;
  - write `jsl` for `jsr`;
  - add the missing export or `.use`;
  - add a `.state`;
  - turn a label and the data lines under it into a `.data` declaration.

**Check.** Server tests cover each request. A keystroke's cost, measured as in Stage 14, does
not regress.

## Stage 24: Version 1

**Build.**

- `DESIGN.md` loses "draft, non-normative". Sweep it against the implementation once more:
  - every example is a fixture;
  - Appendix A matches the parser;
  - §16 records every decision made in Stages 15–23.
- State what version 1 promises, and to whom:
  - the language;
  - the output's interoperability contract;
  - the `nt65.json` format;
  - the command line;
  - the cc65 pin.
- Say what a breaking change is from here, and how the language version is named (the
  mnemonic sets of the CPUs are already part of it).
- A user-facing introduction, separate from the design: a tour for a ca65 programmer, and a
  migration table from ca65 idioms (Appendix B grown into a guide).
- Package the command and the VS Code extension.

**Check.**

- The gate passes.
- The corpus programs build from a clean checkout with the documented commands only.

## Considered and declined

Recorded so they are not raised again without new information:

- **Register names:** `x`, `y` and `s` stay reserved as names, although only `a` is
  syntactically ambiguous.
- **cc65 constructors:** `.constructor`, `.destructor` and `.interruptor` are not added.
- **A `?:` operator:** `.select` replaces it, because `:` already means an address prefix, a
  signature and an import or export size.
- **Per-file or per-segment default signatures, and changed built-in defaults:** named sets
  replace them.
- **`bank` everywhere or `dbr` everywhere:** the two words name different things.
- **A label owning the data that follows it:** a size that depends on what happens to come next
  is the problem, not a fix. Declarations with extents replace it.
- **Labels on `.repeat` and `.each`:** a body holds the loop instead.
- **A label with data on its line as a declaration (`tbl: .byte 1, 2`):** a line break would
  change what the name means. A label is only a location, and data declarations start with
  `.data`.
- **`name: .word, 16` for arrays:** it differs from `.word 16` by one comma. `.word[16]` replaces
  it.
- **`.tag T, n { ... }` for record tables:** `.type T[] { ... }` replaces it.
- **Bare type names (`byte`, `Player`):** a type would read like an instruction inside a proc.
  Types stay dotted, and `.tag` is spelled `.type`.
- **`.res` as a declaration:** storage is declared with its type.
- **Lowercase-only keywords:** mnemonics, registers and directives stay case-insensitive, as in
  ca65, so `X`, `S` and `AND` stay reserved.
- **Removing `.lobyte`, `.hibyte` or `.bankbyte`:** both they and `<`, `>`, `^` stay.
- **Removing unary `^`:** `#^sym` is how every ca65 65816 program writes a bank byte.
- **`.table` as the keyword for mixed data:** `.data`, freed by removing the segment shortcuts.
- **Zero-filling a short body or array initializer:** it hides a missing entry. Padding is
  written out.
- **Segment shortcuts (`.zeropage`, `.code`, `.bss`, `.data`, `.rodata`):** `.segment NAME` is
  explicit.
- **Quoted segment names:** segments have their own namespace.
- **A segment on each declaration (`.proc f in CODE0`), or a default segment per module:**
  regions place items without repeating the segment or indenting a file.
- **An implicit `CODE` segment:** bytes outside a region or block are an error.
- **A named `.scope` as an address:** a named block of data is `.data`, and of code is a proc.
- **A module name from the file name or path:** `.module` is required.
- **Implicit visibility of other modules' exports:** `.use` or qualification is required.
- **Bare export names in a flat linker namespace:** exports are qualified by module, and `as`
  gives an exact name.
- **Modules spanning several files:** a module is one ca65 translation unit, for privacy the
  linker respects and byte order the build states. Submodules split a large module.
- **Visibility between a module and its submodules, and relative paths (`super::`, `self::`):**
  after version 1, if splitting modules proves common.
- **Glob re-exports (`.export .use hw::*`):** they grow an interface silently.
- **Implicitly exporting what an exported macro names:** it is an error instead.
- **Callee-cleaned stack arguments:** after version 1, as `args n, pops`, if needed.

## Test turnaround

The corpus adds oracle and link runs. Before Stage 15 ends:

- measure `scripts/gate.ps1` with the corpus in it;
- keep corpus runs parallel and cached by output hash, as the oracle is;
- let `scripts/test.ps1 -Fixture` select a corpus program by name;
- keep the corpus out of the edit loop.

Stages 18 and 19 touch every fixture, so measure the edit loop before and after each. Revisit at each
stage that grows the corpus.
