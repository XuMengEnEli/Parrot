using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Parrot.Core.Abstractions;

namespace Parrot.Ocr.Windows;

/// <summary>
/// Windows 内置 WinRT OCR（Windows.Media.Ocr）：系统离线引擎，中英文均可，无需任何下载。
/// 语言偏好：用户配置文件语言 → zh-CN → en-US；语言包缺失时 TryCreate 返回 null → IsAvailable=false。
/// 限长：WinRT 引擎有最大边长（MaxImageDimension，常见 2600±），调用方据此反推渲染 DPI。
/// </summary>
public sealed class WinRtOcrService : IOcrService
{
    private static readonly Lazy<OcrEngine?> Engine = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsAvailable => Engine.Value is not null;

    // MaxImageDimension 是引擎类的静态属性（当前系统 OCR 组件的值，常见 2600±）
    public int MaxImageDimensionPx
    {
        get
        {
            if (Engine.Value is null) return 2604;
            try { return (int)OcrEngine.MaxImageDimension; } catch { return 2604; }
        }
    }

    private static OcrEngine? Create()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? SafeFromLanguage("zh-CN")
                ?? SafeFromLanguage("en-US");
        }
        catch
        {
            return null; // Server SKU / 精简系统可能无该组件
        }
    }

    private static OcrEngine? SafeFromLanguage(string tag)
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language(tag));
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<OcrTextLine>?> RecognizePngAsync(byte[] png, CancellationToken ct)
    {
        var engine = Engine.Value;
        if (engine is null) return null;
        try
        {
            using var ms = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(ms))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync().AsTask(ct);
                await writer.FlushAsync().AsTask(ct);
                writer.DetachStream(); // 否则 DataWriter.Dispose 会连带关闭 ms，后面 Seek 抛 ObjectDisposed
            }
            ms.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ms).AsTask(ct);
            // 超引擎限长的图识别必抛，提前拦（调用方按 null 处理=保留占位卡）
            int maxDim = MaxImageDimensionPx;
            if (decoder.PixelWidth > maxDim || decoder.PixelHeight > maxDim)
                return null;

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct);
            var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
            if (result is null || result.Lines.Count == 0) return null;

            // OcrLine 无整行包围盒 → 取行内各词 BoundingRect 的并集
            var lines = new List<OcrTextLine>(result.Lines.Count);
            foreach (var l in result.Lines)
            {
                if (string.IsNullOrWhiteSpace(l.Text) || l.Words.Count == 0) continue;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
                foreach (var w in l.Words)
                {
                    var b = w.BoundingRect;
                    if (b.X < minX) minX = b.X;
                    if (b.Y < minY) minY = b.Y;
                    if (b.X + b.Width > maxX) maxX = b.X + b.Width;
                    if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
                }
                lines.Add(new OcrTextLine(l.Text, minX, minY, maxX - minX, maxY - minY));
            }
            return lines;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ocr] WinRT 识别失败：{ex}");
            return null; // 识别失败 → UI 保留占位卡，不打断整本流程
        }
    }
}
