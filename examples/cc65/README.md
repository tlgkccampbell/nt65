# C calling nt65

A short C program, compiled with cc65, that calls three routines written in nt65: a fast memory
fill, a CRC-16 checksum and a string routine. It checks each one against C's own answer and
exits with the number of checks that failed. It runs in sim65, cc65's own 6502 simulator, so
`build.ps1` builds it and runs it, and passes when sim65 exits with 0:

```text
cc65 and nt65: all checks passed
```

## Building

With `nt65`, `cl65` and `sim65` on the path, in PowerShell:

```text
./build.ps1
./build.ps1 -NoRun
```

From a build of this repository, with the pinned cc65 in `.cache/cc65`, run from the
repository's root:

```text
examples/cc65/build.ps1 -Nt65 src/Norristown.Cli/bin/Debug/net10.0/nt65.exe -Cl65 .cache/cc65/bin/cl65.exe -Sim65 .cache/cc65/bin/sim65.exe
```

`scripts/build-cc65.ps1` builds cl65 and sim65 along with the other tools, and the `sim6502`
target's runtime library and linker configuration beside them, where cl65 looks for them.

The build has four steps:

1. `nt65 build --c-header build/gen/nt65.h` writes a ca65 file for each module, and the header
   that the C includes.
2. `cl65 -t sim6502 -c` turns nt65's output, the hand-written ca65 in `asm/` and the C in `c/`
   into object files.
3. `cl65 -t sim6502` links them with cc65's runtime library into `build/cc65.sim`, and writes
   a debug file that `nt65 remap-dbg` then points at the `.nt65` sources.
4. `sim65 build/cc65.sim` runs it.

The program has no linker configuration of its own: it uses the one cl65 has for the target. So
`nt65.json` declares the one segment it uses that nt65 does not already know, `ONCE`.

## Organization of the program

- `src/runtime.nt65` imports what the routines use of cc65's runtime library: the C stack
  pointer `c_sp`, scratch bytes in the zero page, and `popa` and `popax`.
- `src/mem.nt65` is `memfill(p, v, n)`, which fills a page at a time, four bytes to a turn of
  its loop.
- `src/crc.nt65` is `crc16(p, n)`, the CRC-16 that is also called CRC-16/CCITT-FALSE, worked
  out a byte at a time from two tables, and `crc::init`, which builds the tables.
- `src/str.nt65` is `str_upper(s)`, which changes a string's lowercase letters to capitals in
  place and returns its length.
- `asm/constructor.s` registers `crc::init` as a constructor, so that cc65's start-up code
  builds the tables before `main` runs.
- `asm/upcase.s` is ca65 of the program's own, a routine that `str_upper` calls for each
  character.
- `c/main.c` is the program.

## Calling nt65 from C

cc65 passes a function's last argument in A, or in A and X when it is two bytes, and pushes the
others on its own stack, which grows down from `c_sp`. A function returns its result in A and
X. This is `__fastcall__`, cc65's default. The routines follow it by hand, since nt65 describes
what a routine does to the processor, not which C types it takes:

- `crc16` and `str_upper` take their last argument from A and X, and `crc16` removes the one
  before it from the C stack with `popax`.
- `memfill` reads the two arguments on the C stack where they lie and then moves `c_sp` past
  all three bytes, which is quicker than calling `popa` and `popax`. The C program checks that
  the stack is as it was by reading back a local variable, which cc65 finds from `c_sp`.

Each routine is exported under the name cc65 gives a C function, with a `_` in front:
`.export memfill as "_memfill"`. The header declares each one with that `_` taken off, as
`void memfill(void)`, because nt65 does not know what C passes it. C does not allow a second
prototype that differs, so `main.c` defines `NT65_OWN_memfill` before it includes the header
and declares the real one itself:

```c
#define NT65_OWN_memfill
#include "nt65.h"

void __fastcall__ memfill(void* p, unsigned char v, unsigned n);
```

The header also declares the data and constants the program exports, so the C program uses
nt65's `CRC_POLY` and `CRC_INIT` for its own CRC, and reads the tables the constructor built:

```c
#define CRC_POLY 0x1021
#define CRC_INIT 0xFFFF

extern unsigned char crc_table_lo[256];
extern unsigned char crc_table_hi[256];
```

## Constructors

nt65 has no `.constructor`. A routine that cc65's start-up code runs is registered by a ca65
stub that calls it, which is all `asm/constructor.s` is. `crc::init` is exported under the name
nt65 gives it, `crc__init`, which C cannot use, so the header would warn that it leaves it out.
Nothing in C calls it, so `nt65.json` turns that warning off with
`"c-header-name-left-out": "off"`.
