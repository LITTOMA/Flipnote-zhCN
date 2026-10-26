# Windows、macOS、Linux

正常构建、资源访问、翻译检查、字体更新、动画/图片作者脚本和验证工具共用相同格式实现。没有 Windows 专属 GUI 或 PowerShell 依赖。

| 平台 | 架构 | 入口 | 原生运行验收 |
|---|---|---|---|
| Windows | x64 / ARM64 | `build.cmd` / `note.cmd` | Windows 11 x64 完整通过；ARM64 待执行 |
| macOS | Intel x64 / Apple Silicon ARM64 | `sh build.sh` / `sh note.sh` | 已配置两种架构的原生 CI，尚未执行 |
| Linux（glibc 桌面发行版） | x64 / ARM64 | `sh build.sh` / `sh note.sh` | Ubuntu 24.04 x64 完整通过；ARM64 待执行 |

安装 Git，以及与机器架构一致的 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，并选择 [.NET 10 支持的操作系统版本](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。本项目的原生 CI 使用 Windows、Ubuntu 24.04、macOS 15。Apple Silicon 使用 ARM64 SDK，无需 Rosetta。Linux 范围为 glibc 发行版，未将 Alpine/musl 或 32 位系统纳入验收范围。

Linux 需要 fontconfig 及系统基础字体（用于通用 KWZ 工程的字体发现）。Ubuntu/Debian：

```sh
sudo apt-get install libfontconfig1 fonts-dejavu-core
```

其它发行版安装对应的 fontconfig 与基础字体包。.NET 自身的 ICU/OpenSSL 等系统依赖按官方安装说明处理。无需 X11、Wayland、显示器或图形会话。思源黑体字形表和动画像素字体已在覆盖层中，正常构建无需将中文字体安装到操作系统。

## 使用

Windows：

```powershell
.\build.cmd "C:\Games\original.cia"
.\note.cmd platform-check
.\note.cmd selftest --input "C:\Games\original.cia"
```

macOS / Linux：

```sh
sh build.sh "/path/to/original.cia"
sh note.sh platform-check
sh note.sh selftest --input "/path/to/original.cia"
```

支持从其它目录调用绝对路径入口，输入相对于调用者目录，CIA 输出相对于项目根目录且必须位于 `artifacts/`。路径含空格或中文时加引号。使用 `sh` 不依赖 ZIP 是否保留执行权限；`.gitattributes` 固定 shell 脚本为 LF，避免 Windows 检出后的 CRLF 破坏脚本。

Python 作者/独立验证工具是可选开发工具，三平台均通过 `dotnet` 调用同一个 DLL。推荐 Python 3.10+ 与 Pillow，可在项目 `.work/` 内建立虚拟环境。Windows 可用 `python`，macOS/Linux 通常用 `python3`。执行前先用对应平台入口完成工具编译。

```sh
python3 scripts/check-platform.py
python3 scripts/check-platform.py --input "/path/to/original.cia"
```

第一条不需要 CIA 或 Pillow，检查原生图形和字体、KWZ 压缩/缩略图/签名、Unicode 路径、错误退出码以及原子保存失败的清理。第二条需要 Pillow，另执行 plan、两次构建、回读、完整 selftest，以及字库、UI 纹理和 banner 独立像素验证。结果写入 `artifacts/platform-validation.json`。

## 平台差异处理

- `.cmd`、POSIX shell 和可选 PowerShell 包装均委托 `src/Flipnote.Bootstrap`。库调用固定为 `dotnet ...dll`，不查找 `.exe`。
- 同时恢复固定版本的 Win32、macOS 和 Linux Skia 本地组件，由 .NET 选择当前系统与架构。macOS 的动态库为 Intel/ARM64 通用二进制。
- 编译缓存包含平台、架构和运行时版本；移动工作目录到另一平台会重编译。始终核对 libkwz 子模块提交及工作区状态，依赖变化不会静默复用缓存。
- Linux 文件路径采用大小写敏感比较；Windows/macOS 对保护目录采用保守的不区分大小写比较。CIA 内虚拟资源名一直采用精确比较。
- Unix 允许替换已打开的文件，因此保存失败测试使用三平台都能触发的目标目录冲突，检查原内容完好且临时文件清理。
- 正常构建读取审阅过的固定像素，不依赖各系统的字体栅格化差异。字体重绘和通用 KWZ 文字对象仍须安装/提供精确字体并检查输出；找不到字体会报错。

## 验收与尚待执行的部分

Windows x64 与 Ubuntu 24.04 x64（WSL2）已实际构建同一原版：输出 7,083,008 字节、2,093 个资源，SHA-256 为 `E6CCBAAA8969F9C63586DC353C3CAF192D22BA014826AC836C4C892EFC797E3D`。两边均通过 57 项格式回归和独立像素核对。Linux 还执行了 UI/banner 作者脚本，复现相同像素；教程动画检查 141 帧并复现全部 18 组掩码；`font-unify` 和 `font-extend` 均通过，保持 7,164 个字形及 349 个受保护字形。具体记录见 [platform-validation.json](platform-validation.json)。

`.github/workflows/build.yml` 配置六个原生系统/架构组合，不跨架构模拟。CI 不需要或上传游戏。CI 使用递归子模块检出，固定 libkwz 提交由主仓库 gitlink 提供。macOS/ARM64 的实际运行尚未验证，不能以依赖包存在或 CI 配置完成替代运行记录。游戏内显示与运行验证也与构建平台验收分开记录。
