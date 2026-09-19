using System.Text;
using System.Text.RegularExpressions;

namespace Parrot.Core.TextProcessing;

/// <summary>
/// 英文分句器：从重建出的段落文本拆句子（供逐句发音按钮使用）。
/// 上游依赖 PdfPig 坐标重建（y 聚类成行、x 排序、行尾连字符合并已含在行重建里），
/// 本类只做：中文块剥离 + 缩写保护 + 句子边界识别。
/// </summary>
public static class SentenceSplitter
{
    // 句末标点后接空白 + 大写/引号 → 断句
    private static readonly Regex SentenceBoundary = new(
        @"(?<=[.!?][""')\]]?)\s+(?=[A-Z""'])",
        RegexOptions.Compiled);

    // 常见缩写（点号不视为句末）；够用起步，按讲义实际语料扩充
    private static readonly string[] Abbreviations =
    [
        "Mr.", "Mrs.", "Ms.", "Dr.", "Prof.", "St.", "vs.", "etc.",
        "e.g.", "i.e.", "U.S.", "U.K.", "D.C.", "Jr.", "Sr.", "No.", "Fig.",
    ];

    // CJK 统一表意文字块（如 【真题复现】、中文释义）——只留英文句子挂发音按钮
    private static readonly Regex CjkBlock = new(@"[一-鿿][^A-Za-z]*", RegexOptions.Compiled);

    // '․'(U+2024 one-dot leader) 作缩写的点号保护占位符
    private const char ProtectedDot = '․';

    /// <summary>输入已重建的段落文本（行间已用空格连接），输出英文句子列表。</summary>
    public static IReadOnlyList<string> Split(string paragraphText)
    {
        if (string.IsNullOrWhiteSpace(paragraphText))
            return [];

        // 1) 剥中文块、合并空白（PDF 提取常见粘连/多空格）
        var text = Regex.Replace(CjkBlock.Replace(paragraphText, " "), @"\s+", " ").Trim();
        if (text.Length == 0)
            return [];

        // 2) 保护缩写里的 '.'，断句，再还原
        var protectedText = ProtectDots(text);
        var sentences = SentenceBoundary.Split(protectedText);

        var result = new List<string>(sentences.Length);
        foreach (var raw in sentences)
        {
            var s = UnprotectDots(raw).Trim();
            if (s.Length > 1 && ContainsEnglish(s))
                result.Add(s);
        }

        return result;
    }

    // 混合断句：英文句末标点后接大写/引号，或中文句末标点（。！？；）后直接跟任何新句开头（可无空格）。
    private static readonly Regex MixedBoundary = new(
        @"(?:(?<=[.!?][""')\]]?)\s+|(?<=[。！？；])\s*)(?=[A-Z""'【(《「]|[一-鿿])",
        RegexOptions.Compiled);

    // 音标段（/…/ 、 […] 与全角 ［…］）与全角标点——发音前剔除，展示时保留
    // 全角方括号必须有：OCR（Vision/WinRT）对 IPA 注音普遍输出 ［…］，漏剔会把 "［'klammat］" 念进 TTS
    // /…/ 一支必须"前不接字母 + 内无空白"：否则斜杠并列词（enquiries/issues/complaints）会被当音标吃掉半截
    private static readonly Regex PhoneticSpan = new(@"(?<![A-Za-z])/[^/\s]{2,}/|\[[^\]]{2,}\]|［[^］]{2,}］", RegexOptions.Compiled);

    // OCR 常把一条注音撕成两个行框（"［ɪn'kri:s" + "ikri:s］"）——PhoneticSpan 只配对成对括号，
    // 剩下的 "ikri:s］" 残片必须剔掉：含方括号或 IPA 扩展区字符（U+0250-U+02FF，含重音/长音符）的 token 一律丢
    private static readonly Regex PhoneticResidue = new(@"[^\s]*[\[\]［］\u0250-\u02FF][^\s]*", RegexOptions.Compiled);

    // 撇号（' 与 ’）不剔：它是缩写的一部分，剔了会把 "It's" 念成 "It s"；句界引号交给 OverlayLayout.TrimSpeak
    private static readonly Regex FullWidthPunct = new(@"[。！？；，、：""（）【】《》…—－]", RegexOptions.Compiled);

    // 全角句末标点先换成半角再删其余：📌 靠句末标点认出"这一条是完整句子"，
    // 而 OCR（Vision）把英文句尾的 "?" "." 常识别成 "？" "．"（讲义第 3 页实测）
    // 只认字母后面那个：中文译文自带的 "。" 换成立在句尾的半角点是假句界
    private static readonly Regex FullWidthEnderAfterLetter = new(@"(?<=[A-Za-z])[。！？．]", RegexOptions.Compiled);

    // 词条行的词性标记（n. / adj. / vt. …）：OCR 常把它们和音标、频次混在一行，念出来是噪音
    private static readonly Regex PosToken = new(
        @"\b(adj|adv|afix|abbr|aux|conj|interj|prep|pron|v|vt|vi|num|n)\b\.?",
        RegexOptions.Compiled);

    /// <summary>
    /// 版式重排用分句：断句但 **不**剥中文——Display 保留原文（含中文注释），
    /// Speak 为该片段净化后的英文（无英文则 null → 该片段不挂发音按钮）。
    /// </summary>
    public static IReadOnlyList<PdfFragment> SplitWithDisplay(string paragraphText)
    {
        if (string.IsNullOrWhiteSpace(paragraphText))
            return [];

        var text = Regex.Replace(paragraphText, @"\s+", " ").Trim();
        var fragments = new List<PdfFragment>();
        foreach (var raw in MixedBoundary.Split(ProtectDots(text)))
        {
            var display = UnprotectDots(raw).Trim();
            if (display.Length == 0) continue;
            fragments.Add(new PdfFragment(display, EnglishOnly(display)));
        }
        return fragments;
    }

    /// <summary>留英文、去中文块/音标/全角标点/符号象形（☆★等）；剩余不足 2 个连续字母则返回 null。</summary>
    public static string? EnglishOnly(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var t = PhoneticSpan.Replace(text, " ");
        t = PhoneticResidue.Replace(t, " "); // 再扫一遍：残片（"ikri:s］"）不带配对括号，上面那条抓不到
        t = FullWidthEnderAfterLetter.Replace(t, m => m.Value switch
        {
            "。" or "．" => ".",
            "！" => "!",
            _ => "?",
        }); // 中文后面的 "。" 不在此列：那是译文句点，交给下面整块剔除
        t = FullWidthPunct.Replace(t, " ");
        t = Regex.Replace(t, @"[\p{Sm}\p{So}]+", " "); // ☆★◆※ 等装饰符号不进 TTS
        t = CjkBlock.Replace(t, " ");
        t = PosToken.Replace(t, " "); // 词性缩写最后剥：此时 "adj." 已独立成 token
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > 1 && ContainsEnglish(t) ? t : null;
    }

    private static string ProtectDots(string text)
    {
        var sb = new StringBuilder(text);
        foreach (var abbr in Abbreviations)
            sb.Replace(abbr, abbr.Replace('.', ProtectedDot));
        return sb.ToString();
    }

    private static string UnprotectDots(string text) => text.Replace(ProtectedDot, '.');

    private static bool ContainsEnglish(string s) => Regex.IsMatch(s, @"[A-Za-z]{2,}");
}
