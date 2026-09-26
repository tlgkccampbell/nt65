# Microsoft BASIC for 6502

Michael Steil's [msbasic](https://github.com/mist64/msbasic), one source tree that builds the
first ten known versions of Microsoft BASIC for the 6502, ported to nt65. Each target's image is
byte for byte the original ROM, which `build.ps1` checks against the SHA-256 of each in
`original.sha256`.

| Target | Configuration | Microsoft version | Forked from |
|---|---|---|---|
| Commodore BASIC 1 (PET, 1977) | `cbmbasic1` | | |
| OSI BASIC (1977) | `osi` | 1.0 rev 3.2 | `CONFIG_10A` |
| AppleSoft I (1977) | `applesoft` | 1.1 | `CONFIG_11` |
| KIM-1 BASIC (1977) | `kb9` | 1.1 | `CONFIG_11A` |
| AIM-65 BASIC (1978) | `aim65` | 1.1? | `CONFIG_2A` |
| SYM-1 BASIC (1978) | `sym1` | 1.1? | `CONFIG_2A` |
| Commodore BASIC 2 (1979) | `cbmbasic2` | | `CONFIG_2A` |
| Microtan 65 BASIC (1980) | `microtan` | | `CONFIG_2C` |
| Intellivision Keyboard Component BASIC (1982) | `kbdbasic` | | `CONFIG_2B` |
| WDC W65C816SXB board (2014) | `w65c816sxb` | | `CONFIG_2C` |

The W65C816SXB was never an original ROM; its image is checked against upstream's own build.

## Building

With `nt65`, `ca65` and `ld65` on the path, in PowerShell:

```text
./build.ps1
./build.ps1 -Target cbmbasic2
```

Each target is an `nt65.json` configuration that links the target's linker configuration from
`cfg/`, which declares its segments. So `nt65 build --config cbmbasic2` builds one by hand, and the editor's active configuration chooses the one it analyzes. `default` makes `applesoft` the target when none is chosen. From a build of this
repository, with the pinned cc65 in `.cache/cc65`, run from the repository's root:

```text
examples/msbasic/build.ps1 -Nt65 src/Norristown.Cli/bin/Debug/net10.0/nt65.exe -Ca65 .cache/cc65/bin/ca65.exe -Ld65 .cache/cc65/bin/ld65.exe
```

`build/<target>/<target>.bin` is the ROM, with a debug file beside it that points at the
`.nt65` sources.

## Organization of the program

Upstream is one ca65 translation unit: `msbasic.s` includes every file in order, and some
files include a machine's code in the middle of another. Here every file is a module, and
`src/msbasic.nt65` places the others in upstream's order, so the program is still one object
and each module's bytes land where upstream's did. A machine's code that upstream included in
the middle of a shared file is a module of its own, placed there:

- `iscntc` places each machine's check for control-C, which runs into `STOP` in `flow1`.
- `program` places `kbd::loadsave` and `inline`, between its own routines.
- `loadsave` and `extra` place each machine's LOAD, SAVE and extras, and give the names
  other modules use, whichever machine's they are.

The rest:

- `src/defines.nt65`: the targets, which of Microsoft's versions each was forked from and the
  optional features, as `.config` settings a configuration sets, and each machine's constants
  and ROM entry points.
- `src/zeropage.nt65`: BASIC's zero-page variables in four blocks, `ZP1` to `ZP4`, which each
  machine's linker configuration in `cfg/` places, since each original put them somewhere
  else.
- `src/chrget.nt65`: CHRGET, which runs in zero page after the variables and is copied there
  from the ROM. Its segment loads in the ROM and runs in `ZP4`, so its labels, and `TXTPTR`
  in its own `lda`, are zero-page addresses.
- `src/token.nt65`: the keywords, as an enum of their tokens and the tables of names and
  routines, which asserts that the two agree.
- `src/error.nt65`: the error messages; an error's number is its message's offset in the
  table, which nt65 works out.
- `src/text.nt65`: `htasc`, the text with bit 7 set on its last byte that BASIC's tables use,
  as a function.

## What the port changed

The images are the originals, byte for byte. What reads differently:

- Every `.ifdef` on a target or a feature is an `.if` on a `.config` setting, and the version
  chain (2C, 2B, 2A, 2, 1.1A, 1.1, 1.0A) is a chain of settings.
- Zero page is four segments placed by the linker configuration, where upstream set `.org`.
- CHRGET's segment runs in zero page, where upstream worked out each of its zero-page names
  from the distance between two ROM labels.
- A label that something calls is a routine of its own, and a routine that runs into the next
  says so with `.fallthrough`. A label only jumped to stays inside its routine, and one reached
  from another module is exported.
- The keyword table is an enum and two tables, where upstream counted tokens in a segment that
  was never linked.
- Text that upstream's disassembly read as instructions is text: Microtan's `ora $5845` is
  the start of `"\rEXAM?"`, and KBD's `jsr L6874 / .byte $72 / adc $00,x` after a call that
  prints what follows it is `.strz " thru"`.
- The end of KBD's cold start, which upstream kept with its extras, is in `init`, at the end
  of the INIT segment: every configuration puts EXTRA straight after INIT, and the cold start
  ran on into it there.
- Where upstream wrote a long branch (`jeq` and the rest) to a label later in the file, the
  port writes it out as a branch over a `jmp`. ca65's long branch is long whenever its target
  is further on, and nt65's only when the target is out of reach, so `jeq` would have been
  three bytes shorter.
- Tables that the disassembly left as data after a `jmp`, and padding, are `.data` and
  `.res`; SWEET16's inline code on the Apple, and the text KBD's monitor prints after a
  call, are declared as what the routine they follow reads.
- The W65C816SXB's `xce` is a `.byte $FB`: the program is built for the 6502, and upstream
  switched CPU for that one instruction.

## Credits

- Main work by Michael Steil.
- AIM-65 and SYM-1 by Martin Hoffmann-Vetter.
- Function names and all uppercase comments taken from Bob Sander-Cederlof's AppleSoft II
  disassembly.
- Applesoft lite by Tom Greene helped a lot, too.
- Thanks to Joe Zbicak for help with Intellivision Keyboard BASIC.

## License

2-clause BSD, by Michael Steil, as upstream states it; this port keeps it. It is an altered
version of the original, which is at the address above. The original ROM images are not part
of this port; `original.sha256` holds their SHA-256 hashes.
