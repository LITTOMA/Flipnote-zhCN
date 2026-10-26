#ifndef TYPES_H
#define TYPES_H

typedef int BOOL;
#define TRUE 1
#define FALSE 0

#define true 1
#define false 0

#define NULL ((void *)0)

typedef unsigned char u8;
typedef unsigned char uint8_t;

typedef unsigned short u16;
typedef unsigned short uint16_t;
typedef unsigned int u32;
typedef unsigned int uint32_t;
typedef unsigned int size_t;

typedef unsigned long long u64;
typedef long long __int64;

typedef signed char s8;
typedef signed char bool;

typedef signed short s16;
typedef signed int s32;
typedef signed long long s64;

typedef u16 wchar;
typedef u16 wchar_t;

typedef u32 Handle;
typedef s32 Result;

typedef unsigned char bit8;
typedef unsigned short bit16;
typedef unsigned int bit32;
typedef unsigned long long int bit64;

// 定义 IFile 结构体，用于模拟虚函数表
typedef struct IFile IFile;
#endif
