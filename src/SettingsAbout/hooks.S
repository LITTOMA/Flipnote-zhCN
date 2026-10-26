.syntax unified
.arm
.section .text.hooks,"ax"
.global event_hook, poll_hook
event_hook:
    push {r0-r3,r12,lr}
    mov r0,r4
    mov r1,r5
    bl about_open
    cmp r0,#0
    pop {r0-r3,r12,lr}
    ldrne pc,=0x18672c
    ldr r0,[r5,#0x54]
    ldr pc,=0x186734
poll_hook:
    ldrb r12,[r0,#0x34]
    cmp r12,#9
    beq about_poll
    push {r4,lr}
    ldr pc,=0x186168
.ltorg
