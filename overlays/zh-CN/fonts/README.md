# BCFNT 字体源

汉字和英文字母使用思源黑体简体中文 Regular 24px；来源、固定版本和哈希见 `source-font.json`，许可证见 `SourceHanSans-LICENSE.txt`。

`font_CN_manifest.json`、76 张 PNG 与 `glyph-unification.json` 是构建输入。共 7,164 个字形，其中 6,763 个汉字和 52 个英文字母统一重绘，其余 349 个字形的像素、索引和字宽受保护。完整 GB2312 与可打印 ASCII 覆盖用于动态姓名和动画 ID。

编译输出为 2,540,684 字节，24×25 字形单元、A4 纹理，保持字体全局度量。主堆已增加 3 MiB，构建器同时检查字库、0x80 头部及 64 KiB 余量。更新方法见 `docs/CONTRIBUTING.md`，不能跳过逐字策略或只补静态译文中的字。
