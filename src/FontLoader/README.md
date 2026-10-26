# BCFNT 加载器

本目录给出字体加载器的 C、ARM 汇编与二进制结构定义。加载器打开 `rom:/font_CN.bcfnt`，分配文件长度加 0x80 的缓冲区，读入并重定位 FINF/TGLP/CWDH/CMAP，再提供共享字体接口。

正常构建的精确输入是 `overlays/zh-CN/patches/font-loader-code.json` 和 `font-loader-exheader.json`，由 C# 构建器核对原始字节并应用。这些小型差分不包含完整游戏可执行文件，普通用户无需 ARM 工具链。

修改加载器源码时必须同时更新匹配的差分配方及输出哈希，核对 ARM 地址、扩展头的代码段/BSS、主堆预算，并执行启动测试。单改 C 文件不会改变已固定的机器码配方。
