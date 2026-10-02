; Registers crc::init in src/crc.nt65 as a constructor, which cc65's start-up code runs before
; main. nt65 has no .constructor, and ca65 registers only a routine defined in the same file,
; so this stub is the constructor and jumps to the nt65 routine.
.setcpu "6502"
.import crc__init
.constructor build_crc_tables

; cc65 keeps code that runs only once, at start-up, in ONCE.
.segment "ONCE"
build_crc_tables:
        jmp crc__init
