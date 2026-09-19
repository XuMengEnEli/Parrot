using System.Text;
using System.Text.RegularExpressions;

namespace Parrot.Core.TextProcessing;

/// <summary>
/// 词典短语切分器：在一串英文词元里挑出"属于词典的最长短语"，长者优先、彼此不重叠。
/// 本类不碰数据库——词典判定由调用方给（Data 层把整句候选短语一次批量查成集合），
/// 这样每句只查库一次，也让贪心策略能在单测里直接验证。
/// </summary>
public static class PhraseMatcher
{
    /// <summary>
    /// 上限 6 词：全量 ECDICT 的 36.4 万条短语里 5 词 6707 条、6 词 747 条，4 词封顶会把
    /// "as a matter of fact" 这类五词搭配截断成 "a matter of"；再往上（7 词仅 292 条）不值得多查一倍候选。
    /// </summary>
    public const int MaxWords = 6;

    public const int MinWords = 2;

    // 与 ReaderPageViewModel.Headword 同一口径，否则切词结果和单词兜底会对不上
    private static readonly Regex WordToken = new(@"[A-Za-z][A-Za-z'’-]*", RegexOptions.Compiled);

    /// <summary>切小写词元（含单字母词：a/I 是 "take a walk" 这类短语的组成部分，不能丢）。</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;
        foreach (Match m in WordToken.Matches(text))
        {
            var t = m.Value.Trim('\'', '’', '-').ToLowerInvariant();
            if (t.Length > 0) tokens.Add(t);
        }
        return tokens;
    }

    /// <summary>整句可能的短语候选（所有 MinWords..MaxWords 连续窗口，去重），供一次批量查库。</summary>
    public static List<string> Candidates(IReadOnlyList<string> tokens)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + MinWords <= tokens.Count; i++)
        {
            var sb = new StringBuilder();
            for (int len = 1; i + len <= tokens.Count && len <= MaxWords; len++)
            {
                if (len > 1) sb.Append(' ');
                sb.Append(tokens[i + len - 1]);
                if (len >= MinWords) set.Add(sb.ToString());
            }
        }
        return [.. set];
    }

    /// <summary>贪心扫描：每个位置先试 MaxWords 词、再依次减到 MinWords，命中就整块吃掉并跳到块后，否则前进一个词。</summary>
    public static List<string> Find(IReadOnlyList<string> tokens, Func<string, bool> isInDictionary)
    {
        var hits = new List<string>();
        for (int i = 0; i + MinWords <= tokens.Count;)
        {
            int taken = 0;
            for (int len = Math.Min(MaxWords, tokens.Count - i); len >= MinWords; len--)
            {
                var candidate = Join(tokens, i, len);
                if (!IsEntry(candidate) || !isInDictionary(candidate)) continue;
                hits.Add(candidate);
                taken = len;
                break;
            }
            i += taken > 0 ? taken : 1;
        }
        return hits;
    }

    /// <summary>
    /// 整行本身就是搭配时返回它（小写、空格连接），否则 null。
    /// 讲义词表行（"social pressure 社会压力"、"prosocial behavior"）里的搭配不少是教材自造的，
    /// ECDICT 根本没收录，只按词典命中的话会塌回第一个单词 "social"——这类行整行才是要记的东西。
    /// 判据三条同时成立：2..MaxWords 个词、整行没有句末标点、首词不是句子开头词。
    /// 正文句子通常带谓语和句号，长度也常超上限，不会误判成词条。
    /// </summary>
    public static string? AsWholePhrase(string text)
    {
        if (text.IndexOfAny(['.', '。', '！', '!', '？', '?']) >= 0) return null;
        var tokens = Tokenize(text);
        if (tokens.Count < MinWords || tokens.Count > MaxWords) return null;
        return SentenceOpeners.Contains(tokens[0], StringComparer.Ordinal) ? null : string.Join(' ', tokens);
    }

    private static readonly char[] SentenceEnders = ['.', '!', '?', '。', '！', '？'];

    /// <summary>词性缩写：真句子不会以词性标记收尾。</summary>
    private static readonly HashSet<string> PosTags =
        new(StringComparer.Ordinal)
        {
            "n", "v", "vt", "vi", "a", "ad", "adj", "adv", "art", "prep", "pron", "conj", "int", "num", "abbr", "pl",
        };

    /// <summary>
    /// 是不是一个完整句子（该整句记入，而不是切成词典里碰到的碎片）。
    /// 大方向是 <see cref="AsWholePhrase"/> 的反面：带句末标点、又不像个词条。
    /// 词表行被 EnglishOnly 剥成 "abandon ə'bændən v." 后也是句点收尾，所以还要看词数与末尾词性。
    /// </summary>
    public static bool IsSentence(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0 || !SentenceEnders.Contains(trimmed[^1])) return false;
        var tokens = Tokenize(trimmed);
        return tokens.Count > MinWords && !PosTags.Contains(tokens[^1]);
    }

    /// <summary>句子开头词：代词/冠词/助动词/疑问词打头的短串多半是正文片段，不是词条（与 FunctionWords 用途不同，那份只管两词短语该不该收）。</summary>
    private static readonly string[] SentenceOpeners =
    [
        "he", "him", "his", "she", "her", "it", "its", "they", "them", "their", "we", "us", "our",
        "you", "your", "i", "me", "my", "mine", "this", "that", "these", "those", "there", "here",
        "what", "which", "who", "whom", "whose", "when", "where", "why", "how", "all", "both",
        "am", "is", "are", "was", "were", "be", "been", "being", "do", "does", "did", "have", "has",
        "had", "can", "could", "will", "would", "should", "may", "might", "must", "not",
    ];

    private static readonly string[] FunctionWords =
    [
        "the", "a", "an", "that", "this", "these", "those", "there", "it", "he", "she", "they", "we",
        "you", "i", "one", "some", "any", "every", "each", "either", "neither", "no", "all", "both",
        "many", "much", "more", "most", "other", "another",
    ];

    /// <summary>
    /// 是否像个值得记的条目：ECDICT 里"两词 + 虚词开头"（the law、a day、that many）是为覆盖度
    /// 凑数的自由组合，混进学习列表纯属噪音；三词以上的冠词/限定词搭配（a variety of、the number of）
    /// 是真固定说法，留。
    /// </summary>
    private static bool IsEntry(string phrase)
    {
        int firstSpace = phrase.IndexOf(' ');
        int secondSpace = phrase.IndexOf(' ', firstSpace + 1);
        return secondSpace >= 0 || !FunctionWords.Contains(phrase[..firstSpace], StringComparer.Ordinal);
    }

    private static string Join(IReadOnlyList<string> tokens, int start, int len)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < len; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(tokens[start + i]);
        }
        return sb.ToString();
    }
}
