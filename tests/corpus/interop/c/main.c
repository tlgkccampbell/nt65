/* The C side of the program, compiled against the header nt65 writes. A routine the header
   declares `void name(void)` is declared here with what it really takes and returns. */
#define NT65_OWN_memfill
#define NT65_OWN_memsum
#define NT65_OWN_read_key
#define NT65_OWN_set_left_margin
#include "nt65.h"
#include <stdio.h>

void __fastcall__ memfill(void* p, unsigned char v, unsigned n);
unsigned __fastcall__ memsum(const unsigned char* p, unsigned char n);
unsigned char read_key(void);
void __fastcall__ set_left_margin(unsigned char col);

int main(void)
{
    unsigned r;
    memfill((void*)0x2000, 0x55, 300);
    r = memsum((const unsigned char*)0x2000, 10);
    install_blob();
    call_blob();
    printf("%u %u %u %u\n", r, sprite_count, sprites[1].tile, buffer_size);
    ((void (*)(void))cmd_table[tables__Cmd__quit])();
    beep();
    return 0;
}
