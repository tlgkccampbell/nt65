; The C caller (c/main.c), written as cc65 would compile it, since cc65 is not available.
.setcpu "65C02"
.export _main
.import pushax, pusha
.import _memfill, _memsum, _memfill__fill, _install_blob, _call_blob, _beep
.import _read_key, _set_left_margin
.import _sprites, _sprite_count, _cmd_table, _message, _glyphs, _glyphs_size
.import _buffer
.importzp _buffer_size   ; cc65 would emit .import here, and ld65 warns about the size
.importzp Sprite__tile, Cmd__quit
.importzp ptr1, ptr2, tmp1

RESULT = $0200          ; results area the harness dumps

.segment "CODE"
_main:
        ; memfill((void*)0x2000, 0x55, 300);
        lda #<$2000
        ldx #>$2000
        jsr pushax
        lda #$55
        jsr pusha
        lda #<300
        ldx #>300
        jsr _memfill
        ; r = memsum((void*)0x2000, 10);   -> $0352
        lda #<$2000
        ldx #>$2000
        jsr pushax
        lda #10
        jsr _memsum
        sta RESULT
        stx RESULT+1
        ; interior entry: ptr1=$2200, tmp1=$77, ptr2=3
        lda #<$2200
        sta ptr1
        lda #>$2200
        sta ptr1+1
        lda #$77
        sta tmp1
        lda #3
        sta ptr2
        lda #0
        sta ptr2+1
        jsr _memfill__fill
        ; blob
        jsr _install_blob
        jsr _call_blob
        ; sprite_count, sprites[1].tile, cmd_table[quit], message[0], glyphs_size, buffer[0]
        lda _sprite_count
        sta RESULT+2
        ldx #6 + Sprite__tile
        lda _sprites,x
        sta RESULT+3
        lda _sprites+1,x
        sta RESULT+4
        lda #<_buffer_size
        sta RESULT+5
        lda _buffer
        sta RESULT+6
        lda _message
        sta RESULT+7
        lda _glyphs_size
        sta RESULT+8
        lda _glyphs+1
        sta RESULT+9
        lda #Cmd__quit
        asl a
        tax
        lda _cmd_table,x
        sta RESULT+10
        lda _cmd_table+1,x
        sta RESULT+11
        jsr _read_key
        sta RESULT+12
        lda #5
        jsr _set_left_margin
        jsr _beep
        lda #0
        tax
        rts
