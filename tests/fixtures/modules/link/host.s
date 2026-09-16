; A hand-written ca65 module, to show that the object files nt65 produces link against one
; written by hand: it calls into nt65 and nt65 calls into it (§12).
.setcpu "6502"
.export host_tick, host_print, HOST_VERSION
.import main, clear, clear__again

HOST_VERSION = $0102

.segment "ZEROPAGE": zeropage
host_tick:  .res 1

.segment "CODE": absolute
host_print:
    rts

start:
    jsr main
    jsr clear
    jsr clear__again
    rts
