# A card for the Commander X16

A small program for the Commander X16, written for nt65 from the start. It draws a card on the
text screen: a box, a heading in reverse, and a few lines of text in three colors. It is there
to try nt65 on the 65C02 and on banked memory, and it is small: under a kilobyte, in three
files.

The code that draws is in RAM bank 1 and the card's script is in RAM bank 2, both at `$A000`.
The program loads each bank's file with the KERNAL's LOAD, then runs the script from main
memory: each operation reads what it takes while the script's bank is mapped, and maps the
drawing code's bank only to call it. The drawing code writes video memory through the VERA's two
data ports. Once the card is drawn, the program goes back to BASIC with both bank registers as
BASIC left them.

```text
A card for the Commander X16

Loading the banks... done.

          +----------------------------------------------------------+
          |                                                          |
          |    Commander X16                                         |
          |                                                          |
          |   This card was drawn by code in RAM bank 1, from a      |
          |   script in RAM bank 2. The KERNAL loaded each bank      |
          |   from a file of its own, beside the program.            |
          ...
```

## Building

With `nt65`, `ca65` and `ld65` on the path, in PowerShell:

```text
./build.ps1
```

`build/card.prg` is the program, and `build/card.prg.01` and `build/card.prg.02` are the files
it loads into RAM banks 1 and 2. A debug file beside the program points at the `.nt65` sources.
From a build of this repository, with the pinned cc65 in `.cache/cc65`, run from the
repository's root:

```text
examples/x16/build.ps1 -Nt65 src/Norristown.Cli/bin/Debug/net10.0/nt65.exe -Ca65 .cache/cc65/bin/ca65.exe -Ld65 .cache/cc65/bin/ld65.exe
```

## Running it

The program and its banks' files must be on the same disk. In x16emu, the host's file system
is the disk, so from the `build` folder:

```text
x16emu -prg card.prg -run
```

## Testing it

`./test.ps1` runs the card in x16emu and checks what it left. It takes under half a second.
The emulator runs in its test bench mode, without a window: it loads the program from the
`build` folder and runs it, and the program goes back to BASIC. When BASIC waits for a line,
the test bench takes commands from the script, which send the emulator to `$FFFF`. There it
saves main memory, the RAM banks and video memory to `build/test/dump.bin`, and stops. A run
that never gets there is stopped after 30 seconds, and fails. RAM starts as zeros, so every run
is the same run. The checks are:

- **screen**: the text on the screen is `tests/screen.txt`, with each line of a box drawn in
  ASCII.
- **colors**: each cell's colors are as `tests/colors.txt` has them. `.` is white on blue,
  which the KERNAL starts with, and a hex digit is another color on blue.
- **reverse**: the cells shown in reverse are the ones `tests/reverse.txt` lists.
- **banks**: RAM banks 1 and 2 hold `build/card.prg.01` and `build/card.prg.02`, which tests
  the loads.

`./test.ps1 -Update` writes the three files in `tests` from the run instead of checking them,
which are then read and checked by hand. `-X16emu` names `x16emu`, or the folder it is in, when
it is not on the path.

## Organization of the program

- `src/main.nt65`: the start, which loads the banks' files and runs the card's script, and the
  routine that writes text through the KERNAL.
- `src/script.nt65`: the operations a script is made of, and the routine that runs one. Each
  operation is a routine, and a table built with `.each` over the `Op` enum dispatches to them.
- `src/draw.nt65`: the drawing code, in RAM bank 1. It draws a box and writes a line of text.
- `src/card.nt65`: the card's script, in RAM bank 2, written with a macro for each operation.
- `src/x16.nt65`: the bank registers, the VERA's registers and its text screen, the colors, and
  the KERNAL routines the program calls.
- `src/stub.nt65`: the load address and the BASIC line `10 SYS2061`.
- `x16.cfg`: the linker configuration. The program loads where BASIC programs do, its variables
  take the zero page from `$22` to `$7F`, and each RAM bank is a memory area of its own at
  `$A000`, written to a file of its own with a load address in front, as a PRG has.

## What it shows of nt65

- **The 65C02.** The project's CPU is `r65c02`: the X16's W65C02S has the Rockwell bit
  instructions, and the program uses `bbr0`, `rmb0` and `smb0` on a flag in the zero page. It
  also uses `stz`, `bra`, `phx` and `plx`, `pha` then `plx` to move A into X, `lda (zp)`
  without an index, `bit #`, `dec a`, `trb` and `tsb`, and `jmp (table,x)` to dispatch.
- **Banks that cannot see each other.** nt65 reads `x16.cfg`, so it knows that the two banks'
  memory areas cover the same addresses and only one is ever mapped. Code in bank 1 that named
  the script in bank 2 would be an error, `segment-not-visible`. The script's operations, in
  main memory, read the script and call the drawing code, which is why they are there.
- **Which bank to map, by name.** `x16.cfg` gives each bank's memory area a `bank`, and
  `.bankof(draw::box)` is that number, which ld65 works out. Neither the program nor its table
  of files writes a bank's number down.
- **Video memory past 64 KB.** The text screen's map is at `$1B000` in the VERA's memory, a
  17-bit address, which `x16::text::MAP` holds and `.bankbyte` and `.hibyte` take apart.
- **Text in the machine's own characters.** The program switches to the lowercase and uppercase
  characters, then writes text for the KERNAL with `nt65::cbm::petscii_lower` and text for
  video memory with `nt65::cbm::screen_lower`.
- **A script as data.** The card is written with macros, `at!`, `paint!`, `box!`, `text!` and
  `reverse!`, in a `.data` block. A macro's parameter of the `Color` enum's kind takes only a
  color, and one of kind `const(0..79)` only a column.

The X16 can have a 65816 in place of its 65C02, and the 65816 has no Rockwell instructions, so
a program meant for both would leave them out. This one is for the 65C02. It also never waits
for the next frame with `wai`: in its test bench mode x16emu draws no frames, so the interrupt
that would end the wait never comes.
