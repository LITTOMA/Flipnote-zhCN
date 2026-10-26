OUTPUT_FORMAT("elf32-littlearm", "elf32-littlearm", "elf32-littlearm")
OUTPUT_ARCH(arm)

ENTRY(_start)

SECTIONS
{
  . = 0x00373050;
  .text       : { *(.text) }
  . = 0x003BBCA8;
  .rodata     : { *(.rodata) }
  . = 0x003EFC10;
  .data       : { *(.data) }
  . = 0x00448B08;
  .bss        : { *(.bss) }
}