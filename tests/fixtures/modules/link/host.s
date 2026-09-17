; A hand-written ca65 module, to show that the object files nt65 produces link against one
; written by hand: it calls into nt65 and nt65 calls into it. What an nt65 module exports
; is named with the module's path in front.
.setcpu "6502"
.export host_tick, host_print, HOST_VERSION
.import main__main, gfx__clear, gfx__clear__again, hw__vic__set_border
.import rt__ticks: abs
.import gfx__tables, gfx_high_bytes

HOST_VERSION = $0102

.segment "ZEROPAGE": zeropage
host_tick:  .res 1

.segment "CODE": absolute
host_print:
    rts

start:
    jsr main__main
    jsr gfx__clear
    jsr gfx__clear__again
    jsr hw__vic__set_border
    lda rt__ticks
    lda gfx__tables
    lda gfx_high_bytes
    rts
