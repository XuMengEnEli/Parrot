namespace Parrot.Core.Abstractions;

/// <summary>OCR 识别出的一行文本框。**像素坐标、左上原点、y 向下**，分辨率 = 送识别图像的 dpi。</summary>
public sealed record OcrTextLine(string Text, double X, double Y, double Width, double Height);

/// <summary>
/// 扫描件 OCR（需求 1.1 的最后一公里）：把无文本层的页识别成带坐标的文字行，
/// 再交给 LayoutBuilder 重建版式。Windows 走系统内置 WinRT 引擎（离线、免装）；
/// 平台无实现/语言包缺失时 IsAvailable=false，UI 保留"扫描件占位卡 + 原图兜底"。
/// </summary>
public interface IOcrService
{
    bool IsAvailable { get; }

    /// <summary>引擎允许的最大边长（像素）；0=未知/不限。用于反推安全渲染 DPI。</summary>
    int MaxImageDimensionPx { get; }

    /// <summary>识别 PNG 图像中的文字行；不可用/失败返回 null（调用方保留占位）。</summary>
    Task<IReadOnlyList<OcrTextLine>?> RecognizePngAsync(byte[] png, CancellationToken ct);
}
