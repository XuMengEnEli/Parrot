namespace Parrot.Core.TextProcessing;

/// <summary>坐标化的最小文本单元（由 Pdf 层从 PdfPig Letter/Word 映射而来，Core 不依赖 PdfPig）。</summary>
public readonly record struct TextGlyph(char Char, double X, double Y, double Width, double FontSize);

/// <summary>
/// PDF 无空格文本层的坐标重建：
/// y 聚类成行 → 行内按 x 排序 → 间隙 &gt; 阈值补空格 → 行距断段 → 行尾连字符合并。
/// 输出"段落"列表（段内已含空格），可直接喂 <see cref="SentenceSplitter"/>。
/// </summary>
public static class LineRebuilder
{
    /// <summary>间隙超过字号的该比例视为空格（PDF 提取无空格的典型经验值）。</summary>
    private const double SpaceGapRatio = 0.25;

    public static IReadOnlyList<string> RebuildParagraphs(IReadOnlyList<TextGlyph> glyphs)
    {
        if (glyphs.Count == 0)
            return [];

        var lines = RebuildLines(glyphs);
        return SplitParagraphs(lines);
    }

    /// <summary>逐行文本（不含段合并），用于调试/缩进判断。</summary>
    public static IReadOnlyList<string> RebuildLines(IReadOnlyList<TextGlyph> glyphs)
    {
        // 1) y 聚类成行：按 y 降序（PDF 原点在左下，阅读顺序从上往下），容差=该字形字号一半
        var ordered = glyphs.OrderByDescending(g => g.Y).ToList();
        var rows = new List<(double Y, List<TextGlyph> Items)>();
        foreach (var g in ordered)
        {
            double tolerance = Math.Max(1.0, g.FontSize * 0.5);
            var bucket = rows.FirstOrDefault(r => Math.Abs(r.Y - g.Y) <= tolerance);
            if (bucket.Items is null)
                rows.Add((g.Y, [g]));
            else
                bucket.Items.Add(g);
        }

        // 2) 行内 x 排序 + 间隙插空格
        var result = new List<string>(rows.Count);
        foreach (var (_, items) in rows.OrderBy(r => r.Y))
        {
            items.Sort(static (a, b) => a.X.CompareTo(b.X));
            var sb = new System.Text.StringBuilder();
            double? prevEnd = null;
            double avgSize = items.Average(i => i.FontSize);
            foreach (var g in items)
            {
                if (prevEnd.HasValue && g.X - prevEnd.Value > Math.Max(0.5, avgSize * SpaceGapRatio) && sb.Length > 0 && !char.IsWhiteSpace(sb[^1]))
                    sb.Append(' ');
                sb.Append(g.Char);
                prevEnd = g.X + g.Width;
            }
            var line = sb.ToString().TrimEnd();
            if (line.Length > 0)
                result.Add(line);
        }
        return result;
    }

    /// <summary>3) 行距突变断段；4) 行尾 "xxx-" + 下行小写开头 → 合并连字符。</summary>
    private static List<string> SplitParagraphs(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return [];

        // 估算常规行高：相邻行 y 差需要原始坐标，这里退化用“空行式”判断 + 连字符逻辑
        var paragraphs = new List<string>();
        var current = new System.Text.StringBuilder(lines[0]);

        for (int i = 1; i < lines.Count; i++)
        {
            var prev = lines[i - 1];
            var next = lines[i];

            // 段边界启发：上一行以句末标点结束，或行首大写且上一行较短
            bool boundary = prev.EndsWith('.') || prev.EndsWith('!') || prev.EndsWith('?')
                || (prev.Length < 0.6 * MaxSoFar(paragraphs, current) && char.IsUpper(next.Length > 0 ? next[0] : ' '));

            if (boundary && current.Length > 0)
            {
                paragraphs.Append(MergeHyphenated(current.ToString()));
                current.Clear();
                current.Append(next);
            }
            else
            {
                current.Append(' ').Append(next);
            }
        }

        if (current.Length > 0)
            paragraphs.Append(MergeHyphenated(current.ToString()));

        return paragraphs;
    }

    private static double MaxSoFar(List<string> done, System.Text.StringBuilder cur)
        => Math.Max(done.Count > 0 ? done.Max(d => (double)d.Length) : 0, cur.Length);

    /// <summary>行尾连字符跨行合并："recon-\nstruct" → "reconstruct"。</summary>
    internal static string MergeHyphenated(string paragraph)
        => System.Text.RegularExpressions.Regex.Replace(paragraph, @"(\w)-\s+(\w)",
            m => char.IsLower(m.Groups[2].Value[0]) ? m.Groups[1].Value + m.Groups[2].Value : m.Value);
}
