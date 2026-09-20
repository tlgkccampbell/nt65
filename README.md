# nt65

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
code --install-extension artifacts/nt65-1.0.0.vsix
```

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
```

`nt65 init` writes an `nt65.json` and a `src/main.nt65` that builds. `nt65 build` reads
`nt65.json` in the directory it runs in or the nearest one above it, and writes one ca65 source
per module; `--check` reports and writes nothing, `--watch` builds again whenever the program
changes, and `--json` writes one object per diagnostic for tools that are not editors.
`nt65 fmt` writes files in the one layout nt65 sources are written in, or with `--check` lists
the ones that are not in it and exits 1. `nt65 --help` lists every option; §5.3 of the design
describes the project file and the command line.

## Examples

[`examples/snes-hello`](examples/snes-hello) is a small SNES program: a backdrop colour mixed
with the joypad.

## Building from source

Needs the .NET SDK named in `global.json` and PowerShell 7. The pinned cc65 needs git, make and
a MinGW gcc on the path, and the extension needs Node.js. The scripts and the corpus builds are
written for Windows, with Git Bash for the corpus's shell scripts.

```text
dotnet build Norristown.slnx        # the command, the language server and the tests
pwsh scripts/build-cc65.ps1         # cc65, ca65 and ld65 at the pinned commit, into .cache/cc65
pwsh scripts/test.ps1               # the fast suite: units, fixtures and the server
pwsh scripts/test.ps1 -Ca65         # the output assembled with the pinned ca65
pwsh scripts/gate.ps1               # all of the above, and the extension's client
pwsh scripts/package.ps1            # the tool package and the extension, into artifacts
pwsh scripts/generate-syntax.ps1    # the syntax classes, after editing the node table
```

`scripts/test.ps1 -Fixture name` runs one fixture or corpus program, `-Update` accepts changed
fixture output, and `-Benchmark` measures what an edit costs in the language server.

The classes of the syntax tree come from `src/Norristown.Core/Syntax/Syntax.nodes`, a table with
a block per kind of node. Change the table, run `scripts/generate-syntax.ps1`, and commit what it
writes under the `Generated` folders; a test fails when the two disagree, and
`scripts/generate-syntax.ps1 -Check` says the same thing without writing. The generator is
`tools/Norristown.SyntaxGenerator`, which references nothing, so it runs whatever state the code
it writes is in: change the table, regenerate, then fix what the compiler points at.

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
