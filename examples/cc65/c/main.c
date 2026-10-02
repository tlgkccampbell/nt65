/* Calls the nt65 routines and checks what they do against C's own answers. Each check that
   fails prints a line, and the program's exit code is the number that failed, so sim65 exits
   with 0 only when every check passes.

   nt65.h declares each routine `void name(void)`, because nt65 knows what a routine does to
   the processor and not which C types it takes. The program defines NT65_OWN_name for each
   routine it calls and declares the real prototype itself. */
#define NT65_OWN_memfill
#define NT65_OWN_crc16
#define NT65_OWN_str_upper
#include "nt65.h"

#include <stdio.h>
#include <string.h>

void __fastcall__ memfill(void* p, unsigned char v, unsigned n);
unsigned __fastcall__ crc16(const void* p, unsigned n);
unsigned char __fastcall__ str_upper(char* s);

static unsigned char failures;

/* Large enough for a fill that spans whole pages and a part of one, with guard bytes. */
static unsigned char buffer[700];

static void check(int passed, const char* what)
{
    if (!passed) {
        printf("FAILED: %s\n", what);
        ++failures;
    }
}

/* Returns whether every one of n bytes from p is v. */
static int all(const unsigned char* p, unsigned char v, unsigned n)
{
    while (n--) {
        if (*p++ != v) {
            return 0;
        }
    }
    return 1;
}

/* Returns the CRC of n bytes from p a bit at a time, from the constants nt65 exports. */
static unsigned crc16_bitwise(const unsigned char* p, unsigned n)
{
    unsigned crc = CRC_INIT;
    unsigned char bit;
    while (n--) {
        crc ^= (unsigned)*p++ << 8;
        for (bit = 0; bit < 8; ++bit) {
            crc = (crc & 0x8000) ? (crc << 1) ^ CRC_POLY : crc << 1;
        }
    }
    return crc;
}

static void check_memfill(void)
{
    /* A local lives on the C stack and is found from c_sp, so it reads back wrong if memfill
       drops more or fewer bytes of its arguments than C pushed. */
    unsigned marker = (unsigned)buffer;

    memset(buffer, 0, sizeof buffer);
    memfill(buffer + 1, 0xA5, 600);
    check(marker == (unsigned)buffer, "memfill leaves the C stack as it found it");
    check(all(buffer + 1, 0xA5, 600), "memfill fills two pages and part of a third");
    check(buffer[0] == 0 && buffer[601] == 0, "memfill stays inside what it was given");

    memfill(buffer + 1, 0x3C, 256);
    check(all(buffer + 1, 0x3C, 256) && buffer[257] == 0xA5, "memfill fills exactly one page");

    memfill(buffer + 1, 0x77, 5);
    check(all(buffer + 1, 0x77, 5) && buffer[6] == 0x3C, "memfill fills part of a page");

    memfill(buffer + 1, 0xFF, 0);
    check(buffer[1] == 0x77, "memfill of no bytes changes nothing");
}

static void check_crc16(void)
{
    static const char standard[] = "123456789";
    unsigned i;

    /* The constructor in asm/constructor.s built the tables before main ran. */
    check(crc_table_lo[1] == 0x21 && crc_table_hi[1] == 0x10, "the CRC tables are built");

    check(crc16(standard, 9) == 0x29B1, "crc16 gives the standard check value");
    check(crc16(standard, 0) == CRC_INIT, "crc16 of no bytes is the initial value");

    for (i = 0; i < sizeof buffer; ++i) {
        buffer[i] = (unsigned char)(i * 7 + (i >> 8));
    }
    check(crc16(buffer, sizeof buffer) == crc16_bitwise(buffer, sizeof buffer),
          "crc16 agrees with the bitwise CRC across pages");
}

static void check_str_upper(void)
{
    /* The backquote and the braces sit just outside the lowercase letters. */
    char text[] = "Hello, nt65 `{a-z}` cc65!";
    char empty[] = "";

    check(str_upper(text) == strlen(text), "str_upper returns the length");
    check(strcmp(text, "HELLO, NT65 `{A-Z}` CC65!") == 0, "str_upper changes only the letters");
    check(str_upper(empty) == 0, "str_upper of an empty string returns 0");
}

int main(void)
{
    check_memfill();
    check_crc16();
    check_str_upper();
    if (failures == 0) {
        printf("cc65 and nt65: all checks passed\n");
    }
    return failures;
}
