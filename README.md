# nt65

[![gate](https://github.com/tlgkccampbell/nt65/actions/workflows/gate.yml/badge.svg)](https://github.com/tlgkccampbell/nt65/actions/workflows/gate.yml)

nt65 is an assembly language for the 6502, its CMOS variants and the 65816 that transpiles to
ca65. The **Norristown Assembler** implements it: the `nt65` command, which turns `.nt65`
sources into ca65 sources, and a language server with a VS Code extension.

- [The guide](docs/GUIDE.md): a tour of nt65 for ca65 programmers, and how to migrate.
- [The design](DESIGN.md): the definition of version 1 of the language, of the output and of
  the command, with the reasons for each decision. §17 says what version 1 promises.

## Installing

nt65 needs the .NET 10 runtime. Build the packages (below), then:

```text
dotnet tool install --global nt65 --configfile artifacts/nuget.config
code --install-extension artifacts/nt65-<version>.vsix
```

A build from a checkout is `0.0.0-dev`; a release is what its `v*` tag says, and the release
workflow attaches both packages to it.

The extension runs the language server it carries on the installed .NET; `nt65.server.path`
points it at another. It also validates `nt65.json` against the schema it contributes, and
offers a build task per configuration under **Run Task → nt65**, whose output lands in the
Problems panel. Output assembles with ca65 and ld65 built from the cc65 commit in
`scripts/cc65.commit`.

## Using it

```text
nt65 init [dir] [--cpu 65816]
nt65 build [--config name] [-D NAME=value] [--check] [--watch] [--json]
nt65 fmt [--check] [file.nt65...]
nt65 lsp
```

`nt65 init` writes an `nt65.json` and a `src/main.nt65` that builds. `nt65 build` reads
`nt65.json` in the directory it runs in or the nearest one above it, and writes one ca65 source
per module; `--check` reports and writes nothing, `--watch` builds again whenever the program
changes, and `--json` writes one object per diagnostic for tools that are not editors.
`nt65 fmt` rewrites files in nt65's one standard layout, or with `--check` lists
the ones that are not in it and exits 1. `nt65 lsp` serves the language server on standard
input and output. `nt65 --help` lists every option; §5.3 of the design describes the project
file and the command line.

## Other editors

The language server is plain LSP over stdio, and `nt65 lsp` starts it from the installed tool,
so an editor needs nothing of nt65's but a few lines of configuration. `NT65_SERVER_LOG` names
a file the server writes its log to, which is where to look when an editor
starts it and shows nothing.

**Neovim** (0.11 or later) takes the file type and the server as configuration, in
`init.lua`:

```lua
vim.filetype.add({ extension = { nt65 = "nt65" } })
vim.lsp.config.nt65 = {
  cmd = { "nt65", "lsp" },
  filetypes = { "nt65" },
  root_markers = { "nt65.json", ".git" },
}
vim.lsp.enable("nt65")
```

**Helix** takes both in `~/.config/helix/languages.toml`. A language Helix does not know needs
a `scope` and an `indent` of its own; nt65 is four spaces and `;` comments:

```toml
[language-server.nt65]
command = "nt65"
args = ["lsp"]

[[language]]
name = "nt65"
scope = "source.nt65"
file-types = ["nt65"]
roots = ["nt65.json"]
comment-token = ";"
indent = { tab-width = 4, unit = "    " }
language-servers = ["nt65"]
```

**Zed** takes a language server only from an extension, so nt65 needs a small one of three
files, installed from its directory with **zed: install dev extension**. `extension.toml` and
`languages/nt65/config.toml` declare the language and the server:

```toml
# extension.toml
id = "nt65"
name = "nt65"
version = "0.1.0"
schema_version = 1
[language_servers.nt65]
name = "nt65"
languages = ["nt65"]

# languages/nt65/config.toml
name = "nt65"
path_suffixes = ["nt65"]
line_comments = ["; "]
```

and `src/lib.rs` answers with the command, from a `language_server_command` returning
`zed::Command { command: "nt65".into(), args: vec!["lsp".into()], env: vec![] }`. Once such an
extension is installed, `"lsp": { "nt65": { "binary": { "path": "..." } } }` in Zed's settings
points it at another build.

## Examples

[`examples/lorom-template`](examples/lorom-template) is Damian Yerrick's LoROM template for
the SNES ported to nt65: a character walking across a background, with a sound driver that
stays hand-written ca65 for the SPC700 and links in beside the nt65 output.

[`examples/msbasic`](examples/msbasic) is Michael Steil's msbasic ported to nt65: one source
that builds ten versions of Microsoft BASIC for the 6502, from Commodore's first PET BASIC to
Intellivision's Keyboard Component, each byte for byte the original ROM.

[`examples/monitor`](examples/monitor) is a machine-language monitor written for nt65 from the
start: a library that knows nothing of the machine it runs on, and a project for each platform
that supplies what it needs: the Commodore 64, the Apple IIGS with its 65816, and the Super NES,
typed on with the joypad. Its sessions run in VICE and MAME.

[`examples/cc65`](examples/cc65) is a C program, built with cc65, that calls a memory fill, a
checksum and a string routine written in nt65, through the header `nt65 build --c-header`
writes. It checks their answers in sim65, cc65's own simulator.

[`examples/c64-demo`](examples/c64-demo) is a one-screen C64 demo written for nt65: color bars
on a stable raster whose cycle counts the build checks, a sprite multiplexer with self-modifying
code, screens unpacked by an LZ4 decruncher that runs in the zero page, and a small SID music
driver. Its test runs it in VICE.

[`examples/atari-xex`](examples/atari-xex) is an Atari 800XL program written for nt65 whose
subject is the XEX's loading: a system check and a loading screen that run while the rest
loads, the program's own screen loaded over the loader, and color bars drawn by display list
interrupts and moved by a hook in the vertical blank. Its test runs it in Atari800.

[`examples/x16`](examples/x16) is a card for the Commander X16 written for nt65, for the 65C02
with the Rockwell bit instructions: code in one RAM bank draws a script kept in another, both
loaded by the KERNAL, through the VERA's two data ports. Its test runs it in x16emu.

[`examples/hirom-hdma`](examples/hirom-hdma) is a Super NES demo written for nt65 as a HiROM
cartridge at FastROM speed: a wall that waves on a backdrop shaded line by line, both by HDMA,
with code and data in two 64K banks, far calls between them and the data bank changed to reach
each. The configuration checks every bank it reaches. Its test runs it in MAME.

[`examples/mmc3`](examples/mmc3) is an NES cartridge on the MMC3 written for nt65: program banks
that nt65 knows from the linker configuration can never be mapped together, every bank chosen by
`.bankof`, a status bar split by the MMC3's scanline IRQ, character banks switched for the split
and for the water, a tune played from banks the NMI maps, and a drum the DMC plays from a sample.
The interrupts switch banks in the middle of the main loop's switches without breaking them. Its
test runs it in MAME.

## Building from source

Needs the .NET SDK named in `global.json` and PowerShell 7. The pinned cc65 needs git, make and
a C compiler on the path — on Windows a MinGW gcc — the extension needs Node.js, and the
example's asset conversion needs Python 3 with Pillow. The
scripts run on Windows and on Linux; the corpus's shell scripts want a `sh`, which on Windows
is Git Bash.

```text
dotnet build Norristown.slnx        # the command, the language server and the tests
pwsh scripts/build-cc65.ps1         # cc65 and its tools at the pinned commit, into .cache/cc65
pwsh scripts/test.ps1               # the fast suite: units, fixtures and the server
pwsh scripts/test.ps1 -Ca65         # the output assembled with the pinned ca65
pwsh scripts/gate.ps1               # all of the above, and the extension's client
pwsh scripts/package.ps1            # the tool package and the extension, into artifacts
pwsh scripts/install.ps1            # both packages, installed as the global tool and into VS Code
pwsh scripts/coverage.ps1           # what the fast suite reaches, by type; CI runs this, the gate does not
```

`scripts/test.ps1 -Fixture name` runs one fixture or corpus program, `-Update` accepts changed
fixture output, `-Thorough` also compiles every fixture with its files reversed and shuffled —
which the gate asks for, so that order independence is proven once per unit of work rather than
at every edit — and `-Benchmark` measures what an edit costs in the language server.

The classes of the syntax tree come from `src/Norristown.Core/Syntax/Syntax.xml`, a table with a
block per kind of node, after Roslyn's own `Syntax.xml`. The source generator in
`src/Norristown.SyntaxGenerator` reads it as `Norristown.Core` builds, so the way to work is:
change the table, build, then fix what the compiler points at. Nothing is checked in and nothing
can be stale. The classes it writes are on disk under `src/Norristown.Core/Generated`, one file
per type and git-ignored, to be read and grepped like any other code.

## The corpus programs

Three programs build end to end with the pinned tools, from the Debug build of `nt65`: a C64
game, a SNES program on the 65816, and a project that mixes nt65, hand-written ca65 and C
compiled with cc65 against the header nt65 writes. After `dotnet build` and
`scripts/build-cc65.ps1`:

```text
sh tests/corpus/c64/build.sh
sh tests/corpus/snes/build.sh
make -C tests/corpus/interop
```

`make -C tests/corpus/interop CONFIG=debug` builds the interop program's debug configuration.
