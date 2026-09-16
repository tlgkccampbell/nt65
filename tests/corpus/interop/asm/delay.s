; Hand-written ca65 routine that nt65 calls: delay A iterations (clobbers A, X).
.setcpu "65C02"
.export wait_short
.segment "CODE"
wait_short:
        tax
@l:     dex
        bne @l
        rts
