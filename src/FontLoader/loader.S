#define FUNC(name) \
    .global name; \
    .type name, %function; \
name:

.arm
.section .text

//FUNC(call_hook_initSharedFont)
//    bl hook_initSharedFont
//    mov r0, #0
//    LDMFD SP!, {R3-R5,PC}

//FUNC(call_hook_ExpHeapInitilize)
//    STMFD SP!, {R4,LR}
//    MOV R4, R0
//    PUSH {R0-R4,LR}
//    BL hook_ExpHeapInitilize
//    POP {R0-R4,LR}
//    LDR PC, =0x26BE64

FUNC(call_hook_getSharedFontAddress)
    PUSH {R4,LR}
    BL hook_getSharedFontAddress
    POP {R4,LR}
    BX LR