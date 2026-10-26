#include "types.h"

#ifndef ROMFS_H
#define ROMFS_H

#define BIT(n) (1U<<(n))

typedef enum
{
	FS_OPEN_READ   = BIT(0), ///< Open for reading.
	FS_OPEN_WRITE  = BIT(1), ///< Open for writing.
	FS_OPEN_CREATE = BIT(2), ///< Create file.
} OpenMode;

// 定义 IFile 虚函数表类型
typedef struct {
    Result (*TryRead)(IFile* self, s32* pOut, s64 offset, void* buffer, size_t size);
    Result (*TryWrite)(IFile* self, s32* pOut, s64 offset, const void* buffer, size_t size, bool flush);
    Result (*TryVerify)(IFile* self, s64* pOut, s64 offset, s64 size);
    Result (*TryGetSize)(IFile* self, s64* pOut);
    Result (*TrySetSize)(IFile* self, s64 size);
    Result (*TryFlush)(IFile* self);
    Result (*TrySetPriority)(IFile* self, s32 priority);
    Result (*TryGetPriority)(IFile* self, s32* pOut);
    Result (*DuplicateHandle)(IFile* self, Handle* pOut, s64 offset, s64 length);
    Result (*OpenLinkHandle)(IFile* self, Handle* pOut);
    Handle (*GetFileHandle)(IFile* self);
    void (*DetachFileHandle)(IFile* self);
    void (*Close)(IFile* self);
} IFileVTable;

// 定义 IFile 结构体
struct IFile {
    IFileVTable* vtable;
};

// 定义一个函数指针类型，用于指向 C++ 静态方法 UserFileSystem$$TryOpenFile
typedef Result (*UserFileSystemTryOpenFileFunc)(void**, const wchar_t*, unsigned int);

// 定义简化调用虚函数的宏
#define CALL_VIRTUAL_NO_ARGS(obj, method) ((obj)->vtable->method((obj)))
#define CALL_VIRTUAL(obj, method, ...) ((obj)->vtable->method((obj), __VA_ARGS__))

// 定义 UserFileSystem$$TryOpenFile 函数指针
const UserFileSystemTryOpenFileFunc UserFileSystem$$TryOpenFile = *(UserFileSystemTryOpenFileFunc) 0x026AB88;

#endif // ROMFS_H
