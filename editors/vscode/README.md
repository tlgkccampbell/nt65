# nt65 for VS Code

nt65 is an assembly language for the 6502, its CMOS variants and the 65816 that transpiles to
ca65. This extension gives its editor support: highlighting, the Norristown language server,
build tasks and a schema for the project file.

## What it gives you

- **Whole-program analysis as you type.** The server reads every file of the project, not only
  the ones you have open, so an error in a module you use shows up where you use it.
  Diagnostics, go to definition, find references, document highlights, rename, hover,
  completion, signature help, code actions, code lenses, document and workspace symbols, and
  folding.
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
and offers its build tasks.

The extension carries the language server and runs it on the installed .NET 10 runtime. The
`nt65` command is a separate .NET tool, which the build tasks run from the path; point
`nt65.cli.path` at it where it is somewhere else.

## Settings

| setting | |
|---|---|
| `nt65.configuration` | the named configuration the editor analyzes the program as; empty for the project's own settings |
| `nt65.server.path` | a language server to run in place of the one the extension carries |
| `nt65.cli.path` | the `nt65` command the build tasks run, in place of the one on the path |

## The language

The guide is a tour of nt65 for ca65 programmers, and how to migrate; the design document
defines the language, the output and the command. Both are in the repository this extension is
built from.
