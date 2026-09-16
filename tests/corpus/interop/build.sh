#!/bin/sh
set -e
cd "$(dirname "$0")"
ROOT=../../..
NT=$ROOT/src/Norristown.Cli/bin/Debug/net10.0/nt65.exe
CA=$ROOT/.cache/cc65/bin/ca65.exe; LD=$ROOT/.cache/cc65/bin/ld65.exe
$NT build "$@"
for f in build/gen/nt65/*.s; do $CA --cpu 65c02 -g -o ${f%.s}.o $f; done
mkdir -p build/asm; for f in asm/*.s; do $CA --cpu 65c02 -g -I asm -o build/asm/$(basename ${f%.s}).o $f; done
$LD -C link.cfg -o build/app.bin -m build/app.map --dbgfile build/app.dbg build/asm/*.o build/gen/nt65/*.o
