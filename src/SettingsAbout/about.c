// Native Settings extension for Japanese v7.2.0. All addresses are verified
// against the original CodeSet hash; no persistent setting is written.
typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned int u32;
#define PTR(p,o) (*(void **)((u8 *)(p)+(o)))
#define BYTE(p,o) (*(u8 *)((u8 *)(p)+(o)))
#define FN(a,t) ((t)(a))
extern const u16 title[], projectTab[], thirdPartyTab[], projectInfo[], thirdPartyInfo[], closeLabel[];
static u32 lastPage;
const char aboutName[] = "About";
const char * const targets[] = {
    (char*)0x3b61d0, (char*)0x3b5723, (char*)0x3b572b,
    (char*)0x3b570f, (char*)0x3b4c2f, (char*)0x3b5718,
    (char*)0x3b4cc9, (char*)0x3b5734, (char*)0x3b571e, aboutName
};
static void text(void *form, const char *pane, const u16 *value) {
    FN(0x21e8cc, void(*)(void*,const char*,const u16*))(form,pane,value);
}
void about_labels(void *scene) {
    FN(0x22a9b8, void(*)(void*))(scene);
    text(PTR(scene,0x1c), "TxtAbout", title);
}
static void update(void *scene) {
    u32 page=BYTE(PTR(scene,0x14),0x27); // A = 1, B = 0 in the native dialog
    if(page==lastPage) return;
    text(PTR(scene,0x18),"TxtSetContents",page ? projectInfo : thirdPartyInfo);
    lastPage=page;
}
int about_open(void *scene, void *event) {
    void *item=PTR(event,0x54);
    const char *name=(*(const char * (**)(void*))PTR(item,0))(item);
    if(!FN(0x265584,int(*)(const char*,const char*))(name,aboutName)) return 0;
    // Use the stock two-choice layout and its appearance animations.
    FN(0x22a748,void(*)(void*,int,int,int))(scene,5,1,0);
    BYTE(scene,0x34)=9; // unused scene state, distinct from delete-confirm (8)
    void *lower=PTR(scene,0x14);
    text(lower,"TxtSetTitle",title);
    text(lower,"TxtSubjectA",projectTab);
    text(lower,"TxtSubjectB",thirdPartyTab);
    text(lower,"TxtOK",closeLabel);
    text(PTR(scene,0x18),"TxtSetTitle",title);
    lastPage=0xffffffff;
    update(scene);
    return 1;
}
int about_poll(void *scene) {
    void *lower=PTR(scene,0x14);
    int ok=FN(0x230e68,int(*)(void*))(lower);
    int cancel=FN(0x230e34,int(*)(void*))(lower);
    if(ok || cancel) {
        FN(0x22a86c,void(*)(void*,int))(scene,9);
        FN(0x236ecc,void(*)(const char*))(ok ? "SE_SY_OK" : "SE_SY_CANCEL");
        return 1;
    }
    update(scene);
    return 0;
}
