namespace Parrot.Data;

/// <summary>
/// 学习记录/复习进度里词条的键：单个单词统一小写（与内嵌词典的 word 主键对齐，"Abandon" 和 "abandon" 是同一个词）；
/// 词组和整句例句保留原样大小写——📌 整句记入时小写会把句首字母抹掉，列表里读着像坏了。
/// 所有读写这张表的入口都必须过它，否则钉住能记进、✅ 撤销删不掉。
/// </summary>
public static class TermKey
{
    public static string Of(string word)
    {
        word = word.Trim();
        return word.Contains(' ') ? word : word.ToLowerInvariant();
    }
}
