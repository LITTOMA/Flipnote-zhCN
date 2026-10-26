OUTPUT_FORMAT(elf32-littlearm)
ENTRY(event_hook)
SECTIONS {
    . = 0x373300;
    .text : { *(.text.hooks) *(.text*) *(.rodata*) }
    ASSERT(. <= 0x374000, "Settings hook exceeds the existing text page")
    . = 0x448b1c;
    .bss (NOLOAD) : { *(.bss*) }
    ASSERT(SIZEOF(.bss) == 4, "Unexpected settings state size")
    /DISCARD/ : { *(.ARM.exidx*) *(.ARM.extab*) *(.comment*) *(.note*) }
}
