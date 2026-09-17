namespace Parrot.Core.Exam;

/// <summary>考卷的一个格子：字母（可见）或空位（要用户填，带题内空号）。</summary>
public abstract record ExamSlot
{
    public sealed record Letter(char Ch) : ExamSlot;
    public sealed record Blank(int Index, char Answer) : ExamSlot;
}

/// <summary>一轮填空检查的结果：逐空对错 + 汇总。</summary>
public sealed record ExamResult(IReadOnlyList<bool> BlankCorrect, int CorrectBlanks, int TotalBlanks)
{
    public bool AllCorrect => CorrectBlanks == TotalBlanks;
}

/// <summary>
/// 拼写考试出题器（纯函数，无 IO）：把单词随机"干掉" n 个字母让用户回填。
/// 规则：只挖字母格（音标/符号不挖）；至少保留 2 个可见字母，所以挖空数会被压到 len-2；
/// 传入同一 seed 的 Random 即完全可复现（测试友好）。
/// </summary>
public static class ExamQuizer
{
    /// <summary>出一次考卷。blanks&lt;=0 或词太短（&lt;3 字母）时不挖空，slots 全为可见字母。</summary>
    public static IReadOnlyList<ExamSlot> Make(string word, int blanks, Random rng)
    {
        word = (word ?? "").Trim();
        // 可挖位置：仅字母
        var letterPos = new List<int>();
        for (int i = 0; i < word.Length; i++)
            if (char.IsLetter(word[i])) letterPos.Add(i);

        // 至少留 2 个字母可见
        int cap = Math.Max(0, letterPos.Count - 2);
        int n = Math.Clamp(blanks, 0, cap);

        // Fisher–Yates 选 n 个下标（rng 决定 → 同 seed 可复现）
        for (int i = 0; i < n; i++)
        {
            int j = rng.Next(i, letterPos.Count);
            (letterPos[i], letterPos[j]) = (letterPos[j], letterPos[i]);
        }
        var chosen = new HashSet<int>(letterPos.Take(n));
        int idx = 0;

        var slots = new List<ExamSlot>(word.Length);
        for (int i = 0; i < word.Length; i++)
        {
            if (chosen.Contains(i)) slots.Add(new ExamSlot.Blank(idx++, char.ToLowerInvariant(word[i])));
            else slots.Add(new ExamSlot.Letter(word[i]));
        }
        return slots;
    }

    /// <summary>逐空核对（大小写不敏感；空输入算错）。</summary>
    public static ExamResult Check(IReadOnlyList<ExamSlot> slots, IReadOnlyList<string?> inputs)
    {
        var per = new List<bool>();
        foreach (var s in slots)
        {
            if (s is not ExamSlot.Blank b) continue;
            string? got = b.Index < inputs.Count ? inputs[b.Index]?.Trim() : null;
            per.Add(got?.Length == 1 && char.ToLowerInvariant(got[0]) == b.Answer);
        }
        int ok = per.Count(x => x);
        return new ExamResult(per, ok, per.Count);
    }

    /// <summary>把题面渲染成带 ___ 的字符串（--preview 自检与测试断言用）。</summary>
    public static string Render(IReadOnlyList<ExamSlot> slots)
    {
        var chars = new char[slots.Count];
        for (int i = 0; i < slots.Count; i++)
            chars[i] = slots[i] switch
            {
                ExamSlot.Letter l => l.Ch,
                ExamSlot.Blank => '_',
                _ => '?',
            };
        return new string(chars);
    }
}
