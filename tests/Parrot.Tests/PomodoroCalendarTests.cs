using Parrot.Data;
using Parrot.UI.ViewModels;
using Xunit;

namespace Parrot.Tests;

/// <summary>
/// 专注月历：仓储的整月取数（真 SQLite 临时库）+ 格子表几何（纯函数）。
/// 两块分开测是因为月历错位、把 3 号标成 4 号这类 bug 都只出现在接缝上。
/// </summary>
public class PomodoroCalendarTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"se-cal-{Guid.NewGuid():N}.db");
    private readonly LocalDatabase _db;
    private readonly PomodoroRepository _repo;

    public PomodoroCalendarTests()
    {
        _db = new LocalDatabase(_dbPath);
        _db.EnsureSchema();
        _repo = new PomodoroRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* temp 自清 */ }
    }

    [Fact]
    public void MonthFocus_ReturnsEveryDayOfMonthZeroFilled()
    {
        var month = new DateOnly(2026, 3, 1);
        var days = _repo.MonthFocus(month);
        Assert.Equal(31, days.Count);
        Assert.Equal(month, days[0].Date);
        Assert.Equal(new DateOnly(2026, 3, 31), days[^1].Date);
        Assert.All(days, d => Assert.Equal(0, d.Sessions)); // 空库也要有整月格子
    }

    [Fact]
    public void MonthFocus_CountsCompletedOnlyButMinutesIncludeAbandoned()
    {
        var day = new DateOnly(2026, 3, 10);
        _repo.Log("Focus", StartAt(day), 25 * 60, completed: true);
        _repo.Log("Focus", StartAt(day), 25 * 60, completed: true);
        _repo.Log("Focus", StartAt(day), 600, completed: false); // 学 10 分钟放弃

        var cell = _repo.MonthFocus(new DateOnly(2026, 3, 1)).Single(d => d.Date == day);
        Assert.Equal(2, cell.Sessions);
        Assert.Equal(60, cell.TotalMinutes); // 50 + 10
    }

    [Fact]
    public void MonthFocus_ExcludesOtherMonthsAndNonFocusPhases()
    {
        _repo.Log("Focus", StartAt(new DateOnly(2026, 2, 28)), 25 * 60, completed: true);
        _repo.Log("Focus", StartAt(new DateOnly(2026, 4, 1)), 25 * 60, completed: true);
        _repo.Log("ShortBreak", StartAt(new DateOnly(2026, 3, 15)), 5 * 60, completed: true);

        var days = _repo.MonthFocus(new DateOnly(2026, 3, 1));
        Assert.All(days, d => Assert.Equal(0, d.Sessions));
        Assert.All(days, d => Assert.Equal(0, d.TotalMinutes));
    }

    [Fact]
    public void BuildMonth_PadsWeeksMondayFirst()
    {
        // 2026-03-01 是周日 → 周一为第一列时它落在最后一列（偏移 6）
        var cells = FocusDayCell.BuildMonth(new DateOnly(2026, 3, 1),
            _repo.MonthFocus(new DateOnly(2026, 3, 1)), Today);

        Assert.Equal(0, cells.Count % 7);
        Assert.Equal(42, cells.Count);                          // 6 + 31 天补到 6 整周
        Assert.Equal(6, cells.TakeWhile(c => c.IsEmpty).Count()); // 3 月 1 号是周日 → 前面空 6 格
        Assert.Equal(1, cells[6].Day!.Date.Day);
        Assert.Equal(31, cells.Count(c => !c.IsEmpty));
        Assert.All(cells.TakeLast(5), c => Assert.True(c.IsEmpty));
    }

    [Fact]
    public void BuildMonth_MarksTodayOnce()
    {
        // 用"本月内的一天"当今天，避免用例随系统日期跑到别的月份
        var first = new DateOnly(2026, 3, 1);
        var cells = FocusDayCell.BuildMonth(first, _repo.MonthFocus(first), new DateOnly(2026, 3, 20));
        Assert.Single(cells, c => c.IsToday);
        Assert.Equal(20, cells.Single(c => c.IsToday).Day!.Date.Day);
    }

    [Fact]
    public void CellText_And_HeatRamp_FollowSessionCount()
    {
        var day = new DateOnly(2026, 3, 12);
        _repo.Log("Focus", StartAt(day), 25 * 60, completed: true);
        var cells = FocusDayCell.BuildMonth(new DateOnly(2026, 3, 1), _repo.MonthFocus(new DateOnly(2026, 3, 1)), day);

        var one = cells.Single(c => c.Day?.Date == day);
        var none = cells.Single(c => c.Day?.Date == day.AddDays(1));
        var alsoNone = cells.Single(c => c.Day?.Date == day.AddDays(2));
        Assert.Equal("1", one.CountText);
        Assert.Equal("", none.CountText); // 0 不写数字：整月排满 0 像错误码
        Assert.NotEqual(one.Fill, none.Fill); // 有产出必须看出颜色
        Assert.Equal(none.Fill, alsoNone.Fill); // 都是 0 个 → 同一档
        Assert.NotEqual(none.Fill, cells.First(c => c.IsEmpty).Fill); // 空位（非本月）比 0 更淡，靠透明区分
    }

    [Fact]
    public void Cell_PicksDigitsThatContrast_WithTheirOwnHeatLevel()
    {
        var days = new List<FocusDaySummary>();
        for (int i = 1; i <= 6; i++) days.Add(new FocusDaySummary(new DateOnly(2026, 3, i), i, i * 25));
        var cells = FocusDayCell.BuildMonth(new DateOnly(2026, 3, 1), days, new DateOnly(2026, 3, 1));
        FocusDayCell At(int day) => cells.Single(c => c.Day?.Date.Day == day);

        // 数字颜色必须永远有值：绑成 null 会让浅色档的字糊掉（曾在真机上复现）
        Assert.All(cells, c => Assert.NotNull(c.CountBrush));
        Assert.NotEqual(At(1).CountBrush, At(2).CountBrush); // 最浅档深红，其余白
        Assert.Equal(At(2).CountBrush, At(6).CountBrush);

        Assert.Equal(At(3).Fill, At(4).Fill);   // 3–4 同一档
        Assert.Equal(At(5).Fill, At(6).Fill);   // 5 个以上同一档
        Assert.NotEqual(At(4).Fill, At(5).Fill);
    }

    /// <summary>仓储存本地时间串、按 substr(1,10) 归日，所以造数据必须带本机偏移，否则 UTC+8 下会落到前一天。</summary>
    private static DateTimeOffset StartAt(DateOnly day)
    {
        var local = day.ToDateTime(new TimeOnly(9, 0));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
