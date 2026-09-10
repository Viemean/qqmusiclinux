.global _start
_start:
    ldr x0, [sp]        // argc
    add x1, sp, #8      // argv
    bl real_main
    mov x0, #0
    mov x8, #93         // sys_exit
    svc #0
