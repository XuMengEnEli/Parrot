namespace Parrot.Core.Abstractions;

/// <summary>伪广告弹窗要展示的一张复习卡（生词或错题，需求 2.3）。</summary>
public sealed record ReviewCard(string Word, string Meaning, bool IsWrongAnswer);

/// <summary>
/// 弹窗卡片来源抽象：WordbookCardSource 三级取词——记忆曲线今日到期词优先，
/// 其次今日 📌 学习记录，都没有才退回内嵌词典随机。
/// </summary>
public interface ICardSource
{
    /// <summary>取下一张卡；无可展示内容返回 null（弹窗不弹）。</summary>
    ReviewCard? NextCard();
}
