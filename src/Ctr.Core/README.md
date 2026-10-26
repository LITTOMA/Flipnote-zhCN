# Ctr.Core

本项目的 Nintendo 3DS 容器与密码学格式代码，面向 .NET 10，无 NuGet 依赖。公开 API 位于 `Ctr.Core` 下，按 CIA、NCCH、ExeFS、RomFS、CodeSet、IO 和 Cryptography 分组。

格式代码来源和复用授权见根目录 `THIRD_PARTY.md`。游戏专用汉化逻辑位于 `Flipnote.Build`。写入器重建容器并更新哈希，不生成任天堂零售签名。
