namespace Parrot.Core.Abstractions;

/// <summary>伪广告弹窗要展示的一张复习卡（生词或错题，需求 2.3）。</summary>
public sealed record ReviewCard(string Word, string Meaning, bool IsWrongAnswer);

/// <summary>
/// 弹窗卡片来源抽象：WordbookCardSource 两级取词——今日 📌 学习记录优先，当天没记录退回内嵌词典随机。
/// </summary>
public interface ICardSource
{
    /// <summary>取下一张卡；无可展示内容返回 null（弹窗不弹）。</summary>
    ReviewCard? NextCard();
}
