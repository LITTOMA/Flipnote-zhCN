# 修改汉化

下列示例采用 Windows 命令。macOS / Linux 使用 `sh note.sh` / `sh build.sh`，Python 使用 `python3`；参数及资源路径保持一致。作者与验证脚本以 `dotnet` 调用 DLL，不依赖 Windows 可执行文件。Linux 须安装 fontconfig，见 [平台说明](https://github.com/LITTOMA/Flipnote-zhCN/blob/main/docs/PLATFORMS.md)。正常构建直接使用固化字形和位图，重新绘制字体时仍需审阅像素及保护报告。

## 消息

编辑 `translations/LayoutMessage/` 与 `translations/SystemMessage/` 的 JSON。用 `note.cmd messages` 从原版 CIA 读取原文，按术语表审校。消息 ID、按钮图标和控制码必须保持；四位十六进制字形标签可以写成等价的可打印字符。

编辑后运行 `translation-check`、`plan`、`build`。短标签注意布局宽度；完整 GB2312 字库用于动态作者名，不要因当前译文没有某字而裁减字库。

## 字体

字体来源为 Adobe Source Han Sans CN Regular 2.005，固定 URL 与哈希见 `overlays/zh-CN/fonts/source-font.json`。正常构建只需要字形 PNG、manifest 和策略 JSON。需要更换/新增字形时将源字体临时下载到 `.work/`，先核对 SHA-256，再运行：

```powershell
.\note.cmd font-unify --input .work/fonts/SourceHanSansCN-Regular.otf
.\build.cmd "C:\Games\original.cia"
python scripts/verify-font.py
```

`font-unify` 重绘全部汉字和英文；`font-extend` 在统一策略存在时维护相同集合，并拒绝混入不同字体。完成后清理临时 OTF。原假名/符号等的像素、字宽和索引受到逐字哈希保护。

## 示例动画

编辑 `overlays/zh-CN/animations/translation-layout.json`，保留原文字笔画范围、对齐锚点和每行位置。使用 Python 3 与 Pillow 重新生成实际掩码：

```powershell
python overlays/zh-CN/animations/author-masks.py --input "C:\Games\original.cia" 4 8
.\note.cmd animation-check --input "C:\Games\original.cia"
```

掩码操作完整解码后的三层索引像素；作者脚本一次只保存一帧，退出时删除临时目录。只改 JSON 的译文注释不会改变画面。缩略图由 libkwz 从原选定帧生成，随后重新签名。游戏会复制示例到 SD 卡，测试时应区别 CIA 内资源和已有 SD 卡副本；正常构建不操作模拟器存档。

`plan`、`build` 会检查全部帧中的已登记原文笔画，不只检查声明的修改区间。新增或变化的文字仍需逐帧目视审阅；详见 [动画逐帧复核](https://github.com/LITTOMA/Flipnote-zhCN/blob/main/docs/ANIMATION_REVIEW.md)。

## UI 图片

四张构建 PNG 位于 `overlays/zh-CN/images/`，保持原画布大小。需要修改字样时编辑同目录的字样源图，再运行 `prepare.py --input <原版CIA>`。作者工具从用户原版提取保留部分，并生成可审阅图片；构建本身不需要图像生成服务。

```powershell
python overlays/zh-CN/images/prepare.py --input "C:\Games\original.cia"
.\build.cmd "C:\Games\original.cia"
python scripts/verify-ui-images.py --input "C:\Games\original.cia"
```

## HOME 菜单 banner

`overlays/zh-CN/images/banner.json` 和五张 `Banner-*.png` 是标题覆盖源。更新后运行 `plan`、`build`、`selftest` 及 `python scripts/verify-banner.py --input <原版CIA>`，不能只检查 PNG 预览。模型、动画、声音及蒙版外像素必须保持，详见 [banner 说明](https://github.com/LITTOMA/Flipnote-zhCN/blob/main/docs/BANNER.md)。

## 提交范围

提交实际修改的源资产和相应说明，不提交 ROM、完整动画、运行存档、编译产物或缓存。变更 libkwz 依赖须先在独立仓库提交并推送，再切换子模块到该提交，执行 `git add external/libkwz` 更新 gitlink；不要使用跟随分支最新版本的构建方式。涉及编译器/格式的变更须通过 `selftest`，并记录确切的验证范围。

## 设置页汉化信息

新增署名维护在 `overlays/zh-CN/settings/texts.json`，不要直接编辑补丁中的十六进制文字。修改后用 `src/SettingsAbout/author.py` 重新生成，再运行 `plan`、`selftest`、`build`；见 [维护说明](https://github.com/LITTOMA/Flipnote-zhCN/blob/main/docs/SETTINGS_ABOUT.md)。
