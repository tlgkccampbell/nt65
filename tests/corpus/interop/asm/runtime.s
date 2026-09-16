; Just enough of the cc65 runtime for this test (the real one comes from <target>.lib).
.setcpu "65C02"
.exportzp sp, ptr1, ptr2, tmp1
.export popa, popax, pusha, pushax, __STARTUP__ : absolute = 1
.import _main, __STACK_START__, __STACK_SIZE__
.import __BSS_RUN__, __BSS_SIZE__
.import __CONSTRUCTOR_TABLE__, __CONSTRUCTOR_COUNT__

.segment "ZEROPAGE": zeropage
sp:     .res 2
ptr1:   .res 2
ptr2:   .res 2
tmp1:   .res 1

.segment "STARTUP"
start:
        ldx #$FF
        txs
        lda #<(__STACK_START__ + __STACK_SIZE__)
        sta sp
        lda #>(__STACK_START__ + __STACK_SIZE__)
        sta sp+1
        ; constructors (cc65's condes runner, simplified: at most 1)
        lda #<__CONSTRUCTOR_COUNT__
        beq @noctor
        jsr callctor
@noctor:
        jsr _main
        sta $FFF0               ; exit code -> test harness
        .byte $DB   ; stp
callctor:
        jmp (__CONSTRUCTOR_TABLE__)

.segment "CODE"
pusha:  pha
        lda sp
        bne :+
        dec sp+1
:       dec sp
        pla
        ldy #0
        sta (sp),y
        rts
pushax: pha
        txa
        jsr pusha
        pla
        jmp pusha
popa:   ldy #0
        lda (sp),y
        inc sp
        bne :+
        inc sp+1
:       rts
popax:  jsr popa
        pha
        jsr popa
        tax
        pla
        rts

.segment "VECTORS"
        .addr start, start, start
