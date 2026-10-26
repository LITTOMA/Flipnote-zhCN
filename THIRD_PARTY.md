# 来源与第三方许可

本项目自有代码、构建脚本、文档及中文译文采用根目录的 [MIT 许可证](LICENSE)。下列第三方内容和游戏原有内容不由该 MIT 许可重新授权；其原有版权声明和许可证继续适用。

- **libkwz**：独立的 [LITTOMA/libkwz](https://github.com/LITTOMA/libkwz)，以 `external/libkwz` Git submodule 引用，版本由主仓库 gitlink 固定。库和 Studio 的自有代码采用 MIT，见子模块根目录 `LICENSE`。本仓库仅记录引用；库源码、编辑器及样本仍由独立仓库维护。KWZ 签名资源由该独立库维护。
- **CTR 格式代码**：项目所有者开发并授权复用的 Ctr.Core，复用来源为 ACC_Swkbd_Remake 的 Acc.Ctr（`02c6d2913d0fea066138a94a5abe4cbc94060347`），其通用格式代码来自同一所有者的 Psq.Core（`676614cf749104ebf1f4bf14de9981ab5ebc6e24`）。这里独立维护 CIA/NCCH/ExeFS/RomFS 与压缩实现，不依赖这些工程目录。
- **思源黑体**：Adobe Source Han Sans CN Regular 2.005，SIL Open Font License 1.1。来源与哈希见 `overlays/zh-CN/fonts/source-font.json`，完整许可见同目录 `SourceHanSans-LICENSE.txt`。字体按字形位图用于 BCFNT。
- **缝合像素体**：TakWolf/fusion-pixel-font 2026.10.25，简体中文比例 12px 版本，SIL Open Font License 1.1。源字体及组件许可在 `overlays/zh-CN/animations/fonts/fusion-pixel-12px/`。
- **SkiaSharp 2.88.9**：MIT，Windows、macOS、Linux 本地 Skia 组件附带各自第三方条款，由 NuGet 恢复。
- **SixLabors.ImageSharp 3.1.12**：由 libkwz 引用，适用 Six Labors Split License，不能以本项目的许可替代。当前保持库指定版本，NuGet 安全公告不会被屏蔽。
- **System.IO.Hashing 8.0.0**：由 libkwz 引用，MIT。

游戏内容、商标、原字形和保留图案的权利属于各自权利人；项目许可不授予这些内容的额外权利。输入游戏和构建成品不随源码分发。UI 中文字样的生成提示词保存在图片目录；示例动画不使用图像生成。
