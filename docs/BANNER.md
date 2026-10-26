# HOME 菜单 banner 汉化

覆盖目标为 `content/0000/exefs/banner`，原资源 SHA-256 为 `F8D63D5CB021F9027E60E3137943D29DF7BC699FF6D2AB1FE8ABA6C4CCF4C00B`。`banner-textures` 直接从原 CIA 读取 CBMD，在内存中修改纹理、重新压缩 CGFX 并重建 CIA。仓库不保存完整 banner 或模型文件。

## 资源与显示

CBMD 包含公共 CGFX（槽 0）、日文纹理覆盖 CGFX（槽 9）及 85,984 字节的 CWAV。公共资源有四张纹理；日文覆盖提供前三张同名纹理。模型的 `TitleMat1`、`TitleMat2`、`TitleMat3` 分别引用 `COMMON1`、`COMMON2`、`COMMON3`，通过三个矩形平面组合显示。`COMMON4` 用于其它模型部件，原样保留。

| 纹理 | 尺寸 | 格式 |
|---|---|---|
| COMMON1 | 256×128 | LA4 |
| COMMON2 | 128×128 | LA4 |
| COMMON3 | 256×128 | LA4 |
| COMMON4（公共资源） | 128×128 | L4，只读保留 |

不要把纹理顺次摆成普通网格，也不要额外上下翻转。初次临时预览曾多做一次翻转，且错误地按网格摆放；这些预览已弃用。修正后的全部纹理与独立 `Cop.Ctr` / `Cop.FdFont` 解码结果逐像素一致；3dstool 解压结果也一致。模型第三标题平面相对第一平面约为 x=63.3、y=96.1 纹素。原公共字样的重叠区按 x=63、y=96 完全一致，日文字样对应 x=64、y=96，不能混用两套源图的边缘像素。

两套标题均改为“动画记事本 3D”。中文字样复用已选定的 `title-lettering-source.png`，本次没有调用图像生成。公共版本沿用两行布局，日文版本沿用单行布局。原 3D、TM、记事本图案、模型、动画、材质和声音保留。

## 编辑与构建

`overlays/zh-CN/images/banner.json` 声明五张修改纹理、原像素数据哈希及 `Banner-*.png` 源图。`allowedMask` 是按 PNG 行顺序、每字节低位优先的 1 位像素许可蒙版，经 zlib 和 Base64 编码。编译拒绝蒙版外改动、非灰度、不能精确表示为 4 位的色阶/透明度、尺寸变化及源哈希不符。

```powershell
python overlays/zh-CN/images/prepare-banner.py --input "C:\Games\original.cia"
```

作者工具需要 Python 与 Pillow，直接读取原 CIA，按原文字连通笔画擦除、生成中文描边，并处理重叠纹理中的文字。它只在自己的 `.work/` 临时目录提取一个 banner，结束立即清理。正常 `plan`、`build` 不需要 Python、图像生成服务或第三方模型转换器。

构建后运行 `python scripts/verify-banner.py --input "C:\Games\original.cia"`，独立解码最终 CIA 的 CBMD、LZ11、CGFX 和纹理。`--output` 可指定待检查的汉化 CIA。

## 验证范围

- 六张原始 LA4 纹理无损往返；无修改时保留整个原 banner 的压缩字节。
- 编译后回读并对照全部解压数据；模型、动画、材质、其它纹理及声音逐字节保持。
- 两块 CGFX 分别保持 135,040、82,560 字节，低于每块 512 KiB 的限制。重新计算区域/声音偏移，保持声音 32 字节对齐。
- 错误尺寸、颜色、蒙版外修改、越界引用均有失败测试；重复构建必须二进制一致。
- 本次使用 3dstool 和另一套纹理解码实现回读最终输出，检查全部 7 张纹理、180,224 个像素。报告为 `artifacts/banner-verification.json`、`artifacts/banner-review/parsing-verification.json`。标题对照是纹理审阅图，不是 HOME 菜单运行截图；尚未验证实机 HOME 菜单展示。

格式参考：[CBMD](https://3dbrew.org/wiki/CBMD)、[CGFX 格式记录](https://docs.mikage.app/CGFX/)、[Ohana3DS CGFX 解析](https://github.com/gdkchan/Ohana3DS-Rebirth/blob/master/Ohana3DS%20Rebirth/Ohana/Models/CGFX.cs)。生产构建器为本项目实现，不包含 SDK 或第三方转换程序。
