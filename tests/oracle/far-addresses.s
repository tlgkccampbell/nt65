; The spellings §12 and §13 need for a far symbol, and what the linker assertion of a checked
; import looks like. ca65 accepts a far address size only on the 65816: `far` on a 6502 or a
; 65C02 is "Invalid address size specification for current CPU", which is why nt65 refuses a
; far segment or import for those processors rather than writing this out for them.
.setcpu "65816"
.case +

.segment "FARDATA": far
table:      .res 4          ;= 4

.export table: far

.segment "CODE": absolute
.importzp zp_scratch
.import near_thing
.import far_thing: far
.import VIC_BORDER
.assert VIC_BORDER = $d020, lderror, "VIC_BORDER is not $d020"

start:
    lda z:zp_scratch        ;= 2
    lda a:near_thing        ;= 3
    lda f:far_thing         ;= 4
    sta a:VIC_BORDER        ;= 3
    rts                     ;= 1
