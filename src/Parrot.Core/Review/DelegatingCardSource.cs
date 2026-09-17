using Parrot.Core.Abstractions;

namespace Parrot.Core.Review;

/// <summary>委托式卡片源（组合根拼装用；M4 有词库后被 WordbookCardSource 取代）。</summary>
public sealed class DelegatingCardSource(Func<ReviewCard?> next) : ICardSource
{
    public ReviewCard? NextCard() => next();
}
