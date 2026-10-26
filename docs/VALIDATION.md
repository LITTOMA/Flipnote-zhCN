# 验证范围

`build` 每次执行覆盖层原始哈希检查、字体策略与内存预算、动画逐帧保护检查、压缩和容器重建，再回读输出的全部资源。`artifacts/*.build.json` 保存实际输入和输出哈希。

`selftest --input <原版CIA>` 覆盖消息无损往返、控制码、LZ11、嵌套 DARC、完整资源重建、重复构建一致、字体预算、错误输入、失败保存保护，以及 KWZ 掩码、对象和后继差分帧。默认不依赖外部工具；若设置 `FLIPNOTE_3DSTOOL` 为自行安装的 3dstool 路径，会额外独立解码压缩结果，并在报告中标明是否使用。

```powershell
.\note.cmd selftest --input "C:\Games\original.cia"
.\note.cmd translation-check --input "C:\Games\original.cia"
.\note.cmd animation-check --input "C:\Games\original.cia"
python scripts/verify-font.py
python scripts/verify-ui-images.py --input "C:\Games\original.cia"
```

最后两项需要 Pillow。`verify-font.py --output <汉化CIA>` 逐像素独立解码包内 A4 字库，并核对所有 7,164 个字形映射和字宽。`verify-ui-images.py --output <汉化CIA>` 独立解码四张 BCLIM，检查实际像素、原文件尾、3D/TM 和未改图标。

检查产物放在忽略的 `artifacts/`，可以按需删除。CI 在三平台的 x64 / ARM64 原生 runner 上编译并运行不需要游戏的 `platform-check`，验证原生 Skia、字体加载、GB2312、KWZ 压缩/缩略图/签名、文件保存、命令入口及错误退出码；不上传或要求提供游戏。持有原版的开发者可用 `python scripts/check-platform.py --input <原版CIA>` 运行完整的两次构建、回读、57 项回归和三项独立像素验证。CI 递归检出固定版本的 libkwz 子模块；依赖缺失、版本不符或被修改均会失败。游戏内显示、字距、模拟器与实机测试需另行记录，不能由格式检查结果推断。

子模块集成的复验见 [submodule-validation.json](submodule-validation.json)：从公开远端检出固定提交，13 项依赖恢复/保护检查、77 项库测试和 220 项 Studio 检查通过；Windows 下两次 CIA 构建、57 项回归及三项独立像素验证通过，产物哈希保持不变。可用 `python scripts/check-submodule.py` 重跑依赖检查（无需原版 CIA）；该脚本只在自己的 `.work/` 子目录构造测试仓库并在结束时清理。

公开仓库的六个平台/架构原生 CI 已通过，具体源码提交、运行链接和各平台报告见 [ci-validation.json](ci-validation.json)。CI 验证不使用原版 CIA，不代表新增了这些平台上的完整汉化构建或游戏运行验收。
