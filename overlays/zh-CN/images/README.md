# 标题与画廊图片

构建直接读取四张固化 PNG，编译为匹配原版画布的 A4/LA4 BCLIM：`TitleLogo1`、`TitleLogo2`、`TheaterTitleLogo`、`TheaterTitleLogoJp`。游戏名为“动画记事本 3D”，服务名为“世界动画画廊”。

`title-lettering-source.png`、`gallery-lettering-source.png` 是可编辑中文源字样，提示词保存在 `generation-prompts.json`。`prepare.py --input <原版CIA>` 从输入获取保留部分，重新制作最终 PNG；源游戏参考图不分发。

画布尺寸、原版 3D/TM、记事本图标及阴影保持。标题中文在拼合画布 x=3..278 内，`TitleLogo2` 的 x≥30 区域逐像素保留。画廊两行居中，沿用原文字区域。正常构建不需要图像生成服务或 Python。

## HOME 菜单 banner

`banner.json` 和五张 `Banner-*.png` 同时替换公共/日文标题。`prepare-banner.py --input <原版CIA>` 复用现有中文字样制作源图和蒙版；正常构建不调用 Python。保护范围及验证见 [banner 说明](../../../docs/BANNER.md)。
