using System.Text.RegularExpressions;

namespace Parrot.Core.TextProcessing;

/// <summary>可发音热点：句尾锚点（PDF pt 坐标，y-up），供原图视图叠加 🔊。</summary>
public sealed record SpeakSpot(double RightPt, double MidYPt, string Speak, string? Gloss = null);

/// <summary>
/// 原图叠加层用"句子热点"提取（用户方案：不重排版，直接在 PDF 渲染层对应句尾挂 🔊）：
/// 段落分组规则与 LayoutBuilder 一致，但输出 (句尾坐标, 发音文本) 而非重排块——
/// 版式由位图原样呈现，OCR 文本只进 TTS，不上屏。
/// </summary>
public static class OverlayLayout
{
    public static List<SpeakSpot> BuildSpots(IReadOnlyList<PdfLine> lines)
    {
        var spots = new List<SpeakSpot>();
        if (lines.Count == 0) return spots;

        double body = LayoutBuilder.BodyFontSize(lines);
        var para = new List<PdfLine>();

        void FlushPara()
        {
            if (para.Count == 0) return;
            AddParagraph(para, spots);
            para.Clear();
        }

        foreach (var ln in lines)
        {
            // 中文译文行/符号行不产热点：EnglishOnly 会从这类行里剥出零星拉丁 token（"2010 iPad"），念出来是垃圾
            if (!LayoutBuilder.FirstIsLatinish(ln.Text) && !LayoutBuilder.StartsBullet(ln.Text))
            {
                FlushPara();
                // 但它是上一句的释义：📌 整句记入时要用（扫描件里没有"块"可查，只能就地挂上）
                if (spots.Count > 0 && IsGlossLine(ln.Text))
                    spots[^1] = spots[^1] with { Gloss = JoinGloss(spots[^1].Gloss, ln.Text) };
                continue;
            }
            var kind = LayoutBuilder.Classify(ln, body);
            if (kind != PdfBlockKind.Paragraph)
            {
                // 标题/词条/列表行：单行即一个发音单元，直接锚在行尾
                FlushPara();
                var sp = SentenceSplitter.EnglishOnly(ln.Text);
                if (sp is not null)
                    spots.Add(new SpeakSpot(ln.Right, (ln.Top + ln.Bottom) / 2, TrimSpeak(sp)));
                continue;
            }
            if (para.Count > 0 && LayoutBuilder.BreaksParagraph(para[^1], ln, body))
                FlushPara();
            para.Add(ln);
        }
        FlushPara();
        return spots;
    }

    /// <summary>
    /// 是不是上一句的中文译文行。以【/〖开头的行是栏目标签或下一个词条的释义（【真题复现】、【频次 61】、
    /// 【释义】n.公司），拼进上一句的释义只会污染记录。
    /// </summary>
    private static bool IsGlossLine(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t[0] is '【' or '〖') return false;
        return LayoutBuilder.HasCjk(t);
    }

    private static string JoinGloss(string? existing, string addition)
        => string.IsNullOrEmpty(existing) ? addition.Trim() : $"{existing} {addition.Trim()}";

    /// <summary>段落行合并 → 断句 → 用"片段在拼接串中的偏移"回找所属行，锚定最后一条行右缘。</summary>
    private static void AddParagraph(List<PdfLine> lines, List<SpeakSpot> spots)
    {
        // 与 SentenceSplitter.SplitWithDisplay 的 \s+→" " 归一保持一致，偏移才可定位
        var sb = new System.Text.StringBuilder();
        var spans = new List<(int Line, int Start, int Len)>(lines.Count);
        for (int li = 0; li < lines.Count; li++)
        {
            var t = SpaceRun.Replace(lines[li].Text.Trim(), " ");
            if (t.Length == 0) continue;
            if (sb.Length > 0)
            {
                if (sb[^1] == '-' && char.IsLower(t[0])) sb.Length--; // 断词连字符合并
                else sb.Append(' ');
            }
            spans.Add((li, sb.Length, t.Length)); // Line=原始行下标（跳空行也不错位）
            sb.Append(t);
        }
        if (spans.Count == 0) return;
        var joined = sb.ToString();

        int cursor = 0;
        foreach (var f in SentenceSplitter.SplitWithDisplay(joined))
        {
            int idx = joined.IndexOf(f.Display, cursor, StringComparison.Ordinal);
            if (idx < 0) idx = joined.IndexOf(f.Display, StringComparison.Ordinal);
            int end = idx + f.Display.Length;
            cursor = Math.Max(cursor, end);
            if (f.Speak is null || idx < 0) continue;

            int lineAt = -1;
            for (int i = 0; i < spans.Count; i++)
                if (end - 1 >= spans[i].Start && end - 1 < spans[i].Start + spans[i].Len) { lineAt = i; break; }
            if (lineAt < 0) lineAt = spans[^1].Line; // 尾随空白等边界：归到最后一行
            var anchor = lines[spans[lineAt].Line];
            spots.Add(new SpeakSpot(anchor.Right, (anchor.Top + anchor.Bottom) / 2, TrimSpeak(f.Speak)));
        }
    }

    private static readonly Regex SpaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 发音文本去首尾非字非数残留（OCR 常把 " ·" "〖" 挂在句界）。
    /// 真正的句末标点要留下：📌 靠它判断"这一条是完整句子"，文本视图的发音文本本来就带标点。
    /// </summary>
    private static string TrimSpeak(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && !char.IsLetterOrDigit(s[a])) a++;
        int tail = b;
        while (b > a && !char.IsLetterOrDigit(s[b - 1])) b--;
        var trimmed = s[a..b];
        if (trimmed.Length == 0) return trimmed;
        // 被剥掉的尾部里找回句末标点（全角的那几个已由 EnglishOnly 归一成半角）
        for (int i = tail - 1; i >= b; i--)
            if ("!.?".Contains(s[i])) return trimmed + s[i];
        return trimmed;
    }
}
