using System.Text.RegularExpressions;

namespace Parrot.Core.TextProcessing;

/// <summary>由 PDF 坐标重建出的一行（字体单位 pt；Top/Bottom 为 PDF 坐标系，Top 大 = 视觉靠上）。</summary>
public sealed record PdfLine(
    string Text, double Left, double Right, double Top, double Bottom,
    double FontSize, bool Bold);

public enum PdfBlockKind { Heading, Paragraph, ListItem, Entry, ScannedPage }

/// <summary>段内一块可渲染文本：Display 原样展示；Speak 为去掉中文/音标后的英文（null = 无英文，不挂发音按钮）。</summary>
public sealed record PdfFragment(string Display, string? Speak);

/// <summary>重排后的文档块（需求 1.1"按原有格式显示"+ 1.2"每句挂发音按钮"）。</summary>
public sealed record PdfBlock(PdfBlockKind Kind, int Page, IReadOnlyList<PdfFragment> Fragments);

/// <summary>
/// 行→块版式重建器：把重建好的行分类成 标题/正文段/列表项/词条行，
/// 正文行按"字号一致 + 行距正常"合并成段落（行尾连字符合词），再交给 SentenceSplitter 断句。
/// 词表页每行是独立词条（英文+音标+中文），不合并，整行挂一个发音按钮。
/// </summary>
public static class LayoutBuilder
{
    /// <summary>输入按视觉顺序（自上而下）排好的行，输出文档块。</summary>
    public static List<PdfBlock> BuildBlocks(int page, IReadOnlyList<PdfLine> lines)
    {
        var blocks = new List<PdfBlock>();
        if (lines.Count == 0) return blocks;

        double body = BodyFontSize(lines);
        var para = new List<PdfLine>(); // 正文合并缓冲
        (PdfLine First, List<PdfLine> Lines)? openHeading = null; // 同字号相邻标题行合并

        void FlushPara()
        {
            if (para.Count == 0) return;
            blocks.Add(new PdfBlock(PdfBlockKind.Paragraph, page,
                SentenceSplitter.SplitWithDisplay(JoinLines(para))));
            para.Clear();
        }

        void FlushHeading()
        {
            if (openHeading is null) return;
            var text = JoinLines(openHeading.Value.Lines);
            blocks.Add(new PdfBlock(PdfBlockKind.Heading, page,
                [new PdfFragment(text, SentenceSplitter.EnglishOnly(text))]));
            openHeading = null;
        }

        foreach (var ln in lines)
        {
            var kind = Classify(ln, body);
            switch (kind)
            {
                case PdfBlockKind.Paragraph:
                    FlushHeading();
                    if (para.Count > 0 && BreaksParagraph(para[^1], ln, body))
                        FlushPara();
                    para.Add(ln);
                    break;
                case PdfBlockKind.Heading:
                    FlushPara();
                    if (openHeading is { } oh && Math.Abs(ln.FontSize - oh.First.FontSize) < body * 0.1)
                    {
                        oh.Lines.Add(ln); // 同字号 → 续行并入（显式 List，避开集合表达式在旧二进制上的构造器问题）
                    }
                    else
                    {
                        FlushHeading();
                        openHeading = (ln, new List<PdfLine> { ln });
                    }
                    break;
                default: // Entry / ListItem：单行一块
                    FlushPara();
                    FlushHeading();
                    var speak = SentenceSplitter.EnglishOnly(ln.Text);
                    blocks.Add(new PdfBlock(kind, page, [new PdfFragment(ln.Text.Trim(), speak)]));
                    break;
            }
        }
        FlushPara();
        FlushHeading();
        return blocks;
    }

    internal static PdfBlockKind Classify(PdfLine ln, double body)
    {
        bool cjk = HasCjk(ln.Text);
        bool latin = HasEnglish(ln.Text);

        // 词条行判定：英中混排，且"英文开头（headword）"或短行——
        // 中文引语里夹 Unit 编号的长句不算词条
        if (cjk && latin && (FirstIsLatinish(ln.Text) || ln.Text.Length <= 40))
            return PdfBlockKind.Entry;

        bool big = ln.FontSize >= body * 1.15;
        bool shortish = ln.Text.Length <= 28 || (!cjk && CountWords(ln.Text) <= 10);
        bool shortBold = ln.Bold && shortish;
        if (big || (shortBold && !ln.Text.EndsWith('.') && !ln.Text.EndsWith('?')))
            return PdfBlockKind.Heading; // 中文大标题同样适用（speak 自然为 null）

        // 普通中文/符号行 → 当正文处理（断句后 speak=null，不挂按钮）
        if (!FirstIsLatinish(ln.Text) && !StartsBullet(ln.Text))
            return PdfBlockKind.Paragraph;

        if (StartsBullet(ln.Text))
            return PdfBlockKind.ListItem;

        return PdfBlockKind.Paragraph;
    }

    internal static bool FirstIsLatinish(string s)
    {
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            return char.IsAsciiLetter(c) || c is '/' or '(' or '\'';
        }
        return false;
    }

    internal static bool BreaksParagraph(PdfLine prev, PdfLine cur, double body)
    {
        double lineHeight = Math.Max(6, prev.Top - prev.Bottom);
        double gap = prev.Bottom - cur.Top; // 正常行间留白约 0.2–0.5 字高
        if (gap > lineHeight * 0.9) return true;
        if (Math.Abs(cur.FontSize - prev.FontSize) > body * 0.12) return true;
        if (DifferentColumns(prev, cur, body)) return true;
        return false;
    }

    /// <summary>
    /// 并排不同栏判定（词条表这类版式：左栏 x≈68–117、右栏 x≈143+，按 y 排序会两栏交替）。
    /// 正文续行必然与上一行横向重叠，所以"水平完全不相交 + 左缘明显错开"就是分栏信号；
    /// 首行缩进不算（缩进时下一行左缘在更左侧，不产生右侧空隙）。
    /// </summary>
    private static bool DifferentColumns(PdfLine prev, PdfLine cur, double body)
    {
        double tol = Math.Max(2, body * 0.5);
        bool disjoint = cur.Left - prev.Right > tol || prev.Left - cur.Right > tol;
        return disjoint && Math.Abs(cur.Left - prev.Left) > Math.Max(4, body);
    }

    /// <summary>多行合并为一段：行尾连字符 + 下行小写开头 → 去连字符续词，否则空格相接。</summary>
    internal static string JoinLines(List<PdfLine> lines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ln in lines)
        {
            var t = ln.Text.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0)
            {
                bool hyphenJoin = sb[^1] == '-' && char.IsLower(t[0]);
                if (hyphenJoin) sb.Length--; // 去掉断词连字符
                else sb.Append(' ');
            }
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>按字符数加权的中位字号（判标题/正文的基准）。</summary>
    internal static double BodyFontSize(IReadOnlyList<PdfLine> lines)
    {
        var sized = lines.Where(l => l.Text.Trim().Length > 3).ToList();
        if (sized.Count == 0) return 10;
        // 升序累计字符数，取过半数位置（降序扫描会恒返回最大值——那是错的）
        var bySize = sized.OrderBy(l => l.FontSize).ToList();
        int total = bySize.Sum(l => l.Text.Length);
        int run = 0;
        foreach (var l in bySize)
        {
            run += l.Text.Length;
            if (run >= (total + 1) / 2) return l.FontSize;
        }
        return bySize[^1].FontSize;
    }

    private static readonly Regex CjkChars = new(@"[一-鿿㐀-䶿]", RegexOptions.Compiled);
    private static readonly Regex EnglishRun = new(@"[A-Za-z]{2,}", RegexOptions.Compiled);

    internal static bool HasCjk(string s) => CjkChars.IsMatch(s);
    internal static bool HasEnglish(string s) => EnglishRun.IsMatch(s);

    private static int CountWords(string s) => Regex.Matches(s, @"[A-Za-z]").Count > 0
        ? s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length : 0;

    internal static bool StartsBullet(string t) =>
        t.Length > 1 && (t[0] is '•' or '·' or '●' or '▪' or '-' or '*') ||
        (Regex.IsMatch(t, @"^\d{1,2}[.、)）]") && HasEnglish(t));
}
