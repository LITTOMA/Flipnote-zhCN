# 构建与依赖

Windows 使用 `build.cmd <原版CIA路径>`，macOS / Linux 使用 `sh build.sh <原版CIA路径>`。两者调用同一套纯 .NET 启动器，再以 `dotnet Flipnote.Build.dll` 执行命令行程序。PowerShell 入口仅作可选兼容方式。路径含空格时加引号，可以从任意工作目录调用入口；`--input` 相对于调用者当前目录解析，输出路径相对于项目根目录解析。

固定流水线：CIA 身份验证 → NCCH/ExeFS/RomFS 虚拟资源树 → 按清单编译 MSBT、BCFNT、BCLIM、KWZ 与代码补丁 → 保留或重建 DARC 和压缩容器 → 重建各层哈希 → 完整 CIA 回读 → 原子保存。

未改资源保留原始字节。消息与动画逐项检查原始哈希，字体同时检查字形、缺字和内存预算。字库、动画、标题图片均由项目内源资产构建，不需要额外下载汉化成品。

## 平台范围

支持 Windows、macOS、Linux 的 x64 / ARM64。正常构建、资源查看、字体/位图作者工具和验证脚本均使用平台无关入口；版本要求、Linux 系统依赖、已验证范围和原生 CI 矩阵见 [PLATFORMS.md](PLATFORMS.md)。

## 外部依赖

- .NET 10 SDK：`global.json` 固定主版本，允许同主版本的更新功能带。
- Git：克隆项目、初始化和核对子模块时使用。
- libkwz：`external/libkwz` 子模块，地址见 `.gitmodules`，版本只由主仓库 gitlink 固定。`ProjectReference` 直接引用该子模块的库项目。
- NuGet：SkiaSharp 2.88.9、Windows/macOS/Linux 本地运行库，以及 libkwz 指定的 ImageSharp/System.IO.Hashing。许可证分别适用，见 `THIRD_PARTY.md`。

### 初始化与更新子模块

首次克隆请加 `--recurse-submodules`。已有 Git 克隆可在项目根目录运行（三平台相同）：

```sh
git submodule update --init --recursive
```

构建入口也会自动初始化尚未检出的空子模块。每次启动都会核对主仓库索引中的 gitlink、子模块 HEAD 和工作区状态，发现版本不符、未提交修改、未跟踪文件或冲突便停止，不会重置或删除开发者的修改。先保存自己的工作，再执行上面的命令同步。构建不使用 `--remote`，不会跟随远端分支升级。源码 ZIP 缺少 gitlink，需改用 Git 克隆。

更新依赖时，先在独立 libkwz 仓库完成提交与推送，再在本项目运行：

```sh
git -C external/libkwz fetch origin
git -C external/libkwz checkout --detach <已公开的提交SHA>
git add external/libkwz
```

完成构建与验证后，将 gitlink 变更随主项目提交。不要把子模块源码复制进主仓库，也不要另行维护重复的版本锁文件。

`src/Flipnote.Bootstrap` 按源码、子模块提交、运行时及平台/架构指纹判断是否重编译；即便工具缓存有效，也先检查子模块状态。首次恢复需要网络，完成后删除本次 NuGet 下载缓存，保留子模块和已编译工具。构建日志输出到 stderr，命令 JSON 输出到 stdout，失败返回非零退出码。

三平台均可单独检查/初始化依赖或强制重新编译：

```sh
dotnet run --project src/Flipnote.Bootstrap -c Release -- dependencies
dotnet run --project src/Flipnote.Bootstrap -c Release -- tool --force
```

`scripts/build-tool.ps1` 与 `scripts/restore-dependencies.ps1` 委托同一启动器。`.work/`、`**/bin`、`**/obj` 不是发布源文件，临时下载缓存结束即清理。

## 资源访问

```powershell
.\note.cmd inventory --input "C:\Games\original.cia" --filter .kwz
.\note.cmd messages "content/0000/romfs/messageData/JP_Japanese/LayoutMessage.blz!/MemoryBar.msbt" --input "C:\Games\original.cia"
.\note.cmd texture "content/0000/romfs/TexturePack/JP/JPTexturePack.blz!/timg/TitleLogo1.bclim" --input "C:\Games\original.cia" --output .work/title.png
```

`read`、`texture`、`messages` 和 `inventory` 的 `--input` 指向要读取的任意 CIA；`build`、`plan`、`selftest`、`translation-check`、`animation-check` 的 `--input` 指向锁定的原版。`verify --input` 也指定原版，`--output` 指定需比对的汉化包。

## 字体加载补丁

`overlays/zh-CN/patches/` 是固定版本的精确二进制补丁源：包含原字节、替换字节、区间、输入/输出哈希。构建器直接校验并应用，不需要完整修改后的游戏代码。`src/FontLoader/` 提供加载器 C/ARM 汇编及结构定义供开发审阅。调整加载器后必须重新制作和验证补丁配方，而非只修改 C 文件。

加载器读取 `rom:/font_CN.bcfnt`，按文件大小加 0x80 申请对齐缓冲区，重定位字体内部指针。主堆从 0xDF1000 增至 0x10F1000，增加 3 MiB；保持原 64 MiB 应用内存配置，同时将工作堆内两处可回收资源缓存合计减少 3 MiB，补偿主堆扩展，修复画廊入口分配失败。字体当前 2,540,684 字节，构建检查头部、分配器余量及缓存容量配对。完整依据与验证范围见 [内存预算](FONT_MEMORY.md)。

## 动画覆盖检查

构建在编译资源前扫描所有示例帧中的已审阅文字模板，拒绝未覆盖的字幕出现。`animation-check` 报告列出出现帧、漏掉的布局范围及擦除掩码，完整说明见 [逐帧复核](ANIMATION_REVIEW.md)。

## HOME 菜单 banner

`banner-textures` 覆盖类型直接编译原 CBMD 中的 LA4 纹理，保留模型、动画及声音，重建两个 CGFX 压缩流和全部容器哈希。正常构建无新增外部工具依赖；作者及独立验证工具见 [banner 说明](BANNER.md)。

## 设置页署名

汉化信息使用原生菜单扩展和 `resource-patch` 资源配方，正常构建无需 ARM 编译器。新增文字参与缺字检查，源码和生成补丁之间有一致性校验；编辑与验证方法见 [设置页汉化信息](SETTINGS_ABOUT.md)。
