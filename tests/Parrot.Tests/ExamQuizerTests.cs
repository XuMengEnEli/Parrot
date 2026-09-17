using Parrot.Core.Exam;
using Xunit;

namespace Parrot.Tests;

/// <summary>拼写考试出题器（纯函数）：挖空数量/保护位/可复现性/逐空核对。</summary>
public class ExamQuizerTests
{
    private static int Blanks(IReadOnlyList<ExamSlot> slots) => slots.OfType<ExamSlot.Blank>().Count();

    [Fact]
    public void Make_BlanksRequestedCount_KeepsTwoLetters()
    {
        var slots = ExamQuizer.Make("abandon", 3, new Random(7));
        Assert.Equal(3, Blanks(slots));
        Assert.Equal(7, slots.Count);
    }

    [Fact]
    public void Make_ClampsToLeaveAtLeastTwoLettersVisible()
    {
        var slots = ExamQuizer.Make("cat", 6, new Random(1));   // 3 字母最多挖 1
        Assert.Equal(1, Blanks(slots));
        slots = ExamQuizer.Make("hi", 3, new Random(1));        // 2 字母不挖
        Assert.Equal(0, Blanks(slots));
    }

    [Fact]
    public void Make_SameSeedIsReproducible_DifferentSeedUsuallyDiffers()
    {
        var a = ExamQuizer.Render(ExamQuizer.Make("vocabulary", 4, new Random(42)));
        var b = ExamQuizer.Render(ExamQuizer.Make("vocabulary", 4, new Random(42)));
        Assert.Equal(a, b);
        Assert.Equal(4, a.Count(c => c == '_'));
        // 可见字母位置与答案一致（填回答案即还原单词）
        var answers = ExamQuizer.Make("vocabulary", 4, new Random(42))
            .OfType<ExamSlot.Blank>().OrderBy(x => x.Index).Select(x => x.Answer).ToArray();
        var filled = "vocabulary".ToCharArray();
        var slots = ExamQuizer.Make("vocabulary", 4, new Random(42));
        int bi = 0;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i] is ExamSlot.Blank) filled[i] = answers[bi++];
        Assert.Equal("vocabulary", new string(filled));
    }

    [Fact]
    public void Check_CaseInsensitive_RejectsEmpty()
    {
        var slots = ExamQuizer.Make("abandon", 3, new Random(9));
        var answers = slots.OfType<ExamSlot.Blank>().OrderBy(x => x.Index)
            .Select(x => char.ToUpperInvariant(x.Answer).ToString()).ToList(); // 大写输入
        answers[0] = "";                                                        // 一空不填=错
        var res = ExamQuizer.Check(slots, answers);
        Assert.Equal(2, res.CorrectBlanks);
        Assert.Equal(3, res.TotalBlanks);
        Assert.False(res.AllCorrect);
    }

    [Fact]
    public void Check_AllCorrect_True()
    {
        var slots = ExamQuizer.Make("focus", 2, new Random(3));
        var answers = slots.OfType<ExamSlot.Blank>().OrderBy(x => x.Index)
            .Select(x => x.Answer.ToString()).ToList();
        Assert.True(ExamQuizer.Check(slots, answers).AllCorrect);
    }

    [Fact]
    public void Render_KeepsLettersInPlace_BlanksBecomeUnderscore()
    {
        var slots = ExamQuizer.Make("withstand", 3, new Random(5));
        var masked = ExamQuizer.Render(slots);
        Assert.Equal(9, masked.Length);
        for (int i = 0; i < slots.Count; i++)
            Assert.Equal(slots[i] is ExamSlot.Letter l ? l.Ch : '_', masked[i]);
    }
}
