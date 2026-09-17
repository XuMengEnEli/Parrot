namespace Parrot.Core.Review;

/// <summary>
/// 词库就位前的占位卡片（需求 2.3 演示链路用）。M4 ECDICT 导入后由 WordbookCardSource 供数，
/// 本表仅作最终兜底，保证弹窗永不"空弹"。
/// </summary>
public static class SampleCards
{
    public static readonly (string Word, string Meaning)[] Words =
    [
        ("abandon", "v. 放弃；抛弃 n. 放纵"),
        ("utilize", "v. 利用，运用"),
        ("significant", "adj. 有意义的；重要的"),
        ("consequence", "n. 后果，结果；重要性"),
        ("maintain", "v. 维持；主张；保养"),
        ("available", "adj. 可获得的；有空的"),
        ("eliminate", "v. 消除，排除；淘汰"),
        ("explicit", "adj. 明确的，清楚的"),
        ("incentive", "n. 刺激；动机，诱因"),
        ("undermine", "v. 逐渐削弱；暗中破坏"),
    ];

    private static readonly Random _rng = new();

    public static Abstractions.ReviewCard RandomCard()
    {
        var (w, m) = Words[_rng.Next(Words.Length)];
        return new Abstractions.ReviewCard(w, m, IsWrongAnswer: false);
    }
}
