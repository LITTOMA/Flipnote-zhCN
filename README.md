# 动画记事本 3D · 简体中文汉化

从原版 CIA 直接构建简体中文 CIA 的源码项目。译文、字体、标题图片、示例动画的文字位图配方和游戏代码补丁均在本项目中维护。构建工具在内存中读取、修改、压缩并重建游戏资源，不需要预先解包游戏。

[libkwz](https://github.com/LITTOMA/libkwz) 通过 `external/libkwz` Git submodule 引用，版本固定在主仓库的 gitlink 中。首次获取项目请使用 `git clone --recurse-submodules https://github.com/LITTOMA/Flipnote-zhCN.git`；已有克隆运行 `git submodule update --init --recursive`。请使用 Git 克隆，GitHub 的源码 ZIP 不含子模块及其版本记录。

## 构建

支持 **Windows、macOS、Linux**，提供 x64 和 ARM64 原生依赖。需要对应架构的 **.NET 10 SDK**、**Git** 和首次恢复依赖时的网络连接；Linux 还需要 fontconfig。Python、PowerShell、ARM 编译器和 GUI 均不是正常构建的前提。

Windows x64、Ubuntu 24.04 x64 已完成真实 CIA 构建、57 项回归与逐像素验证，输出二进制一致。三平台的 x64 / ARM64 均已通过原生 CI（不含原版 CIA 的构建工具检查）；见 [三平台支持与验收](docs/PLATFORMS.md)。

在项目根目录运行：

```powershell
.\build.cmd "C:\Games\Flipnote-v7.2.0.cia"
```

macOS / Linux（普通终端，无需 PowerShell）：

```sh
sh build.sh "/path/to/Flipnote-v7.2.0.cia"
sh build.sh "/path/to/Flipnote-v7.2.0.cia" --output artifacts/Flipnote-zhCN.cia
```

结果为 `artifacts/Flipnote-v7.2.0_zh-CN.cia`，同目录的 `.build.json` 记录输入身份、全部覆盖资源和输出哈希。也可以指定项目 `artifacts/` 内的输出名称：

```powershell
.\build.cmd -InputCia "C:\Games\Flipnote-v7.2.0.cia" -Output artifacts/Flipnote-zhCN.cia
```

构建会编译源码、获取固定版本依赖、检查原版身份和全部资源哈希、编译汉化覆盖层，再重建 CIA 并完整回读。所有验证通过后才替换输出文件；原版不会被修改。NuGet 下载缓存和中间文件结束即清理。编译好的工具保留供后续快速构建。

## 支持的原版

当前支持日版《うごくメモ帳 3D》，项目对应 v7.2.0 CIA（游戏显示 Ver. 1.3.3）：

| 项目 | 值 |
|---|---|
| Title ID | `0004000000056C00` |
| CIA SHA-256 | `337925D04CF9196E0D5F01A08E70133CA06C750AD4D15E796865C5E11977186C` |

版本与资源约束固定在 `project.json` 和覆盖清单中。不匹配时会停止构建，不能靠删除哈希检查来适配其它版本。用户自行提供原版；仓库不分发 CIA、整套 RomFS 或解包资源。输出适用于支持自制软件的 3DS 环境，容器重建不产生任天堂零售签名。

## 汉化内容

- 63 份消息文件、667 条消息，保留消息 ID、控制码和按钮图标。
- 思源黑体简体中文 Regular 24px：6,763 个汉字和 52 个英文字母统一字形，支持完整可打印 ASCII；日文假名、数字、标点和特殊图标按字形约束保留。
- 9 个含日文示例动画：在完整解码的索引位图上只修改文字笔画，保留原对齐和字号；使用缝合像素体，重新压缩并生成标准缩略图和签名。其余 3 个示例保持原文件。
- 中文标题“动画记事本 3D”和服务名“世界动画画廊”的 4 张纹理。
- 字库加载补丁与主堆/资源缓存预算检查：保留 64 MiB 应用模式，支持约 2.42 MiB 的完整中文字库，并修复画廊入口内存不足。见 [内存预算](docs/FONT_MEMORY.md)。
- HOME 菜单 ExeFS banner：公共/日文标题均改为“动画记事本 3D”，保留模型、动画、其它图形和声音；见 [banner 说明](docs/BANNER.md)。

## 项目结构

| 路径 | 内容 |
|---|---|
| `src/Flipnote.Bootstrap/` | 三平台共用的依赖恢复、编译缓存和命令启动器 |
| `src/Flipnote.Build/` | 命令行构建、资源格式编译、验证 |
| `src/Ctr.Core/` | CIA、NCCH、ExeFS、RomFS、压缩和加密格式代码 |
| `src/FontLoader/` | 字体加载器 C/汇编与接口定义，供补丁开发审阅 |
| `translations/` | 所有中文消息，唯一译文来源 |
| `overlays/zh-CN/manifest.json` | 覆盖目标、原资源哈希和源文件映射 |
| `overlays/zh-CN/fonts/` | BCFNT 字形表、字宽、统一字形约束及字体来源 |
| `overlays/zh-CN/animations/` | 示例动画译文布局、文字笔画掩码、像素字体 |
| `overlays/zh-CN/images/` | UI 汉化图片和可编辑字样来源 |
| `overlays/zh-CN/patches/` | 固定版本的代码及扩展头补丁配方 |
| `.gitmodules`、`external/libkwz` | 独立 libkwz 的仓库地址与固定提交（Git submodule） |
| `scripts/` | 依赖恢复、编译和独立验证脚本 |
| `docs/` | 术语、贡献流程、格式与验证说明 |

libkwz 负责 KWZ 编解码、缩略图和签名，作为独立子模块维护。构建入口会初始化缺失的子模块，并检查当前提交与 gitlink 一致、库工作区无修改；不会自动覆盖已有修改或切换一个已初始化的工作区。更新方法见 [构建与依赖](docs/BUILD.md)。

## 检查与编辑

```powershell
.\note.cmd plan --input "C:\Games\Flipnote-v7.2.0.cia"
.\note.cmd translation-check --input "C:\Games\Flipnote-v7.2.0.cia"
.\note.cmd selftest --input "C:\Games\Flipnote-v7.2.0.cia"
.\note.cmd verify --input "C:\Games\Flipnote-v7.2.0.cia"
```

macOS / Linux 将上述 `.\note.cmd` 换成 `sh note.sh`；Python 脚本可用 `python3` 运行。

`verify --output artifacts/name.cia` 可选择待验证产物。开发时也可将原版放在忽略的 `input/original.cia`，省略 `--input`。可选的字体、图片独立校验和位图作者工具需要 Python 3 与 Pillow；正常构建不需要它们。

阅读 [贡献流程](docs/CONTRIBUTING.md)、[构建说明](docs/BUILD.md)、[术语表](docs/GLOSSARY.md)、[验证说明](docs/VALIDATION.md) 和 [第三方说明](THIRD_PARTY.md)。构建回读与像素验证不等于实机或模拟器运行验证。

## 许可证

本项目自有代码、构建脚本、文档及中文译文采用 [MIT 许可证](LICENSE)。第三方库、字体以及游戏原有内容保留各自权利和许可，不因本项目采用 MIT 而改变；具体范围见 [来源与第三方许可](THIRD_PARTY.md)。
