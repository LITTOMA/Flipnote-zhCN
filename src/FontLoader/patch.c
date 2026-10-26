#include "types.h"
#include "romfs.h"
#include "bcfnt.h"

#define FONT_FILE_MAX_SIZE 0x520000
// #define PREALLOCATED_FONT_BUFFER_ADDR 0x448C80
#define FUSER_HANDLE_ADDR 0x00028012
#define ORIG_SHARED_FONT_BUFFER_ADDR 0x3D4E30


typedef void* (*mallocFunc)(size_t, s32, u8); // size, align, filler

const mallocFunc                malloc                = *(mallocFunc)0x026CC60;

BOOL is_font_loaded = false;
u32 orignalSharedFontBufferAddr = 0;
u8 *fontHeaderBuffer = NULL;


BOOL initFont(void* fontBuffer, size_t fileSize) {
    BinaryFontHeader* header = (BinaryFontHeader*)fontBuffer;

    // check font magic, should be 'CFNT'
    if (header->magic[0] != 'C' || header->magic[1] != 'F' || header->magic[2] != 'N' || header->magic[3] != 'T') {
        // if magic is 'CFNU', then it's already initialized
        if (header->magic[0] == 'C' && header->magic[1] == 'F' && header->magic[2] == 'N' && header->magic[3] == 'U') {
            return true;
        }
        return false;
    }

    // Patch offsets in FINF section
    BinaryFontInfo* finf = (BinaryFontInfo*)(fontBuffer + sizeof(BinaryFontHeader));
    // Cache offsets before patching
    u32 tglp_offset = finf->tglp_offset;
    u32 cwdh_offset = finf->cwdh_offset;
    u32 cmap_offset = finf->cmap_offset;
    // Patch offsets
    finf->tglp_offset += (u32)fontBuffer;
    finf->cwdh_offset += (u32)fontBuffer;
    finf->cmap_offset += (u32)fontBuffer;

    // Patch offsets in TGLP section
    BinaryGlyphSheets* tglp = (BinaryGlyphSheets*)(fontBuffer + sizeof(BinaryFontHeader) + sizeof(BinaryFontInfo));
    tglp->sheet_data_offset += (u32)fontBuffer;

    // Patch offsets in CMAP section
    BinaryCharacterMap* cmap = (BinaryCharacterMap*)(fontBuffer + cmap_offset);
    while (cmap->next_cmap_offset != 0) {
        cmap->next_cmap_offset += (u32)fontBuffer;
        cmap = (BinaryCharacterMap*)(cmap->next_cmap_offset);
    }

    // Patch offsets in CWDH section
    BinaryCharacterWidth* cwdh = (BinaryCharacterWidth*)(fontBuffer + cwdh_offset);
    while (cwdh->next_cwdh_offset != 0) {
        cwdh->next_cwdh_offset += (u32)fontBuffer;
        cwdh = (BinaryCharacterWidth*)(cwdh->next_cwdh_offset);
    }

    // set magic to 'CFNU'
    *(u32*)fontBuffer = 0x554E4643;

    return true;
}

BOOL loadFont() {
    IFile *fontFileObj = NULL;
    Result res = UserFileSystem$$TryOpenFile(&fontFileObj, L"rom:/font_CN.bcfnt", FS_OPEN_READ);
    if (res != 0) {
        return false;
    }

    // Get file size
    s64 fileSize;
    CALL_VIRTUAL(fontFileObj, TryGetSize, &fileSize);
    // if (fileSize > FONT_FILE_MAX_SIZE) {
    //     // Close file
    //     CALL_VIRTUAL_NO_ARGS(fontFileObj, Close);
    //     return false;
    // }

    fontHeaderBuffer = (u8*)malloc(fileSize + 0x80, 0x80, 0);
    if (fontHeaderBuffer == NULL) {
        // Close file
        CALL_VIRTUAL_NO_ARGS(fontFileObj, Close);
        return false;
    }
    u8 *fontBuffer = fontHeaderBuffer + 0x80;

    // Try read file
    s32 bytesRead;
    // Result (*TryRead)(IFile* self, s32* pOut, s64 offset, void* buffer, size_t size);
    CALL_VIRTUAL(fontFileObj, TryRead, &bytesRead, 0, fontBuffer, fileSize);

    // Close file
    CALL_VIRTUAL_NO_ARGS(fontFileObj, Close);

    // Check if read was successful
    if (bytesRead != fileSize) {
        return false;
    }

    // Initialize font
    if (!initFont(fontBuffer, fileSize)) {
        return false;
    }

    SharedFontBufferHeader *sharedFontBufferHeader = (SharedFontBufferHeader*)(fontHeaderBuffer);
    sharedFontBufferHeader->SharedFontLoadState = 2;
    sharedFontBufferHeader->SharedFontType = 1;
    sharedFontBufferHeader->Size = fileSize;
    *(u32*)ORIG_SHARED_FONT_BUFFER_ADDR = (u32)(fontHeaderBuffer);

    is_font_loaded = true;
    return true;
}

uint32_t getSharedFontAddress() {
    uint32_t sharedFontAddress = orignalSharedFontBufferAddr;

    if (sharedFontAddress != NULL) {
        sharedFontAddress += 0x80;
    }

    return sharedFontAddress;
}

uint32_t hook_getSharedFontAddress() {
    orignalSharedFontBufferAddr = *(u32*)ORIG_SHARED_FONT_BUFFER_ADDR;
    if (!is_font_loaded) {
        if (!loadFont()) {
            return getSharedFontAddress();
        }
    }
    return fontHeaderBuffer + 0x80;
}
