# Flipnote-zhCN 工作约定

先读 README.md、docs/BUILD.md、docs/CONTRIBUTING.md 和 docs/GLOSSARY.md。

- 本项目以用户输入的原版 CIA 和仓库内覆盖层直接构建完整汉化 CIA。保持原版只读，不展开整套 RomFS；所有临时工作放 `.work/` 的任务专属子目录并及时清理。
- `translations/` 是中文消息唯一来源。对照 `note.cmd messages` 读取的日文审校，保留 ID、控制码、按钮图标及空消息。SD 卡容量称“空间”，不要称“内存”。
- 游戏名统一为“动画记事本 3D”，服务名为“世界动画画廊”。覆盖已有资源必须固定原始 SHA-256；`!/` 表示归档内路径。
- BCFNT 汉字和英文字母统一使用思源黑体简体中文 Regular 24px。必须覆盖完整 GB2312 汉字和可打印 ASCII。保护假名、数字、标点和特殊图标，严格遵守 `glyph-unification.json`。不可通过仅补静态文本所需字符绕过动态姓名/ID 的字形需求。
- 字体预算包括文件、0x80 字节头部及至少 64 KiB 余量；主堆扩展 3 MiB。超预算要调整并验证内存划分，不可仅放宽大小检查。
- KWZ 示例只能修改完整解码后的索引位图。禁止图像生成 AI 重画动画；保留掩码外所有像素、帧数、时序、颜色、音轨和元数据。中文使用缝合像素体原生 12px 或整数 2 倍 24px，匹配原对齐和字号。
- 示例缩略图调用外部 libkwz 的 `UpdateThumbnail`，从选定完整帧整图生成标准 JPEG，然后签名。不要修改 JPEG DCT 块。
- libkwz 是独立项目，以 `external/libkwz` Git submodule 引用；主仓库仅保存 `.gitmodules` 与 gitlink，不复制库源码。更新时先在独立仓库提交并推送，再更新和暂存主仓库的 gitlink；构建必须使用该固定提交且子模块工作区干净。
- 自有代码、脚本、文档和中文译文采用 MIT。保留第三方库、字体及游戏内容的原有许可与权利说明，不将它们重新标记为 MIT。
- 只提交源代码、译文、覆盖层、说明和许可证。CIA、输入游戏、缓存、编译结果、临时截图与测试产物均被忽略。
- 维护 Windows、macOS、Linux 的共用 .NET 启动器；不得重新引入只调用 `.exe` 的脚本。Linux/macOS 使用 `sh note.sh`，Windows 使用 `note.cmd`。变更启动器/本地依赖时运行 `scripts/check-platform.py`，有原版时加 `--input`，记录实测平台，不把 CI 配置或交叉编译当成原生运行验证。
- 修改后运行 `plan` 和 `build`；修改格式/编译器时运行 `selftest`，确认重复构建一致。字体/图片/动画修改需额外核对像素及保留范围。
- 报告验证范围，明确区分格式回读、视觉审阅和游戏运行。不要把构建成功当作实机验证。
