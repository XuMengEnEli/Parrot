using System.Text;
using Parrot.Core.Abstractions;

namespace Parrot.Core.TextProcessing;

/// <summary>
/// OCR 行框 → PdfLine 映射：像素坐标（y 向下）转成 PDF 坐标系（Top 大 = 视觉靠上，
/// 与 PdfPig 词框一致），行高换算字号（÷1.3 行距系数），供 LayoutBuilder 照常分类。
/// 传 pageHeightPx（渲染位图的像素高）得到真实页内坐标（原图热点定位要用）；
/// 只作文本重排时可省（坐标整体平移不影响行序/行距/字号等相对几何）。
/// </summary>
public static class OcrLayout
{
    /// <param name="scale">pt/px = 72/dpi。</param>
    /// <param name="pageHeightPx">该页渲染位图的像素高（0 = 不平移页高，坐标为负但几何正确）。</param>
    public static List<PdfLine> ToPdfLines(IReadOnlyList<OcrTextLine> lines, double scale, double pageHeightPx = 0)
    {
        var result = new List<PdfLine>(lines.Count);
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.Text)) continue;
            double hPt = Math.Max(6, l.Height * scale);
            result.Add(new PdfLine(
                Normalize(l.Text),
                l.X * scale,
                (l.X + l.Width) * scale,
                (pageHeightPx - l.Y) * scale, // 翻 y 轴 +（可选）页高补偿 → PDF y-up 真坐标
                (pageHeightPx - (l.Y + l.Height)) * scale,
                hPt / 1.3,    // 行框高 ≈ 1.3 倍字号
                Bold: false)); // OCR 无字体信息，加粗不判（标题走"大字号"通道）
        }
        // LayoutBuilder 要求输入视觉自上而下（Top 降序），引擎顺序不做假设
        result.Sort((a, b) => b.Top.CompareTo(a.Top));
        return result;
    }

    /// <summary>
    /// Windows OCR 引擎会在 CJK 字/全角标点之间插空格（"上 策 词"）。
    /// 两侧都是 CJK/全角的空白串删掉；其余空白串折叠为一个半角空格
    /// （CJK-拉丁之间的空格是中英边界，发音剥离要用，必须保留）。
    /// </summary>
    public static string Normalize(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (IsSpace(c))
            {
                int j = i;
                while (j < s.Length && IsSpace(s[j])) j++;
                char prev = sb.Length > 0 ? sb[^1] : '\0';
                char next = j < s.Length ? s[j] : '\0';
                if (!(IsCjkOrFullWidth(prev) && IsCjkOrFullWidth(next)))
                    sb.Append(' '); // 非 CJK 边界：折叠保留一个空格
                i = j;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '　'; // 　 = U+3000

    private static bool IsCjkOrFullWidth(char c) =>
        c is >= '　' and <= '〿'   // CJK 标点/全角空格 U+3000-303F
        or >= '一' and <= '鿿' // CJK 统一表意文字
        or >= '＀' and <= '￯';   // 半角/全角形式 U+FF00-FFEF

    /// <summary>
    /// 采用门槛：图形页（思维导图等）OCR 只会产出散落字块，混进文本视图不如保留占位卡。
    /// 要求总量、最长行、平均行长都达到"确有正文"的量级。
    /// </summary>
    public static bool WorthKeeping(IReadOnlyList<PdfLine> lines)
    {
        if (lines.Count == 0) return false;
        int total = 0, maxLen = 0;
        foreach (var l in lines)
        {
            total += l.Text.Length;
            if (l.Text.Length > maxLen) maxLen = l.Text.Length;
        }
        return total >= 30 && maxLen >= 8 && (double)total / lines.Count >= 5;
    }
}
