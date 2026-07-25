# 无损音乐兼容助手

将常见音频转换为播放器兼容的 `FLAC / 16-bit / 44.1 kHz / 双声道`。
默认输出位置为桌面的“无损音乐兼容助手”文件夹。

音频策略：

- 完全符合目标规格的 FLAC：直接逐字节复制，不重新编码。
- 其他来源：FFmpeg 浮点处理、libsoxr 33-bit VHQ 重采样、TPDF 抖动、FLAC level 8。
- 不做音量归一化、动态压缩或 EQ。
- 转换时保留可复制的标签元数据。

构建：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

构建前需要把支持 `libsoxr` 的 Windows x64 `ffmpeg.exe` 放入
`Engine/ffmpeg.exe`。该文件不会提交到 Git。
