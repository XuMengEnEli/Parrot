using Microsoft.Data.Sqlite;

namespace Parrot.Data;

/// <summary>一天专注汇总（统计页柱状图）。</summary>
public sealed record FocusDaySummary(DateOnly Date, int Sessions, int TotalMinutes);

/// <summary>番茄记录读写（pomodoro_log）。阶段结束或提前停止时落一条。</summary>
public sealed class PomodoroRepository(LocalDatabase db)
{
    public void Log(string phase, DateTimeOffset startedAt, int seconds, bool completed)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO pomodoro_log(phase, started_at, seconds, completed) VALUES ($p, $t, $s, $c)";
        cmd.Parameters.AddWithValue("$p", phase);
        cmd.Parameters.AddWithValue("$t", startedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$s", seconds);
        cmd.Parameters.AddWithValue("$c", completed ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 近 n 天（含今天）：番茄数=完整专注数，分钟=**实际专注秒数**（含放弃/中途退出的片段）——
    /// 旧口径只算 completed=1，学 20 分钟放弃记 0，用户反馈"统计不准"的根源之一。缺失日补零，日期升序。
    /// </summary>
    public List<FocusDaySummary> DailyFocus(int days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var result = new List<FocusDaySummary>(days);

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT substr(started_at, 1, 10) AS d, SUM(completed) AS n, SUM(seconds) AS sec
            FROM pomodoro_log
            WHERE phase = 'Focus' AND substr(started_at, 1, 10) >= $from
            GROUP BY d ORDER BY d
            """;
        cmd.Parameters.AddWithValue("$from", today.AddDays(-(days - 1)).ToString("yyyy-MM-dd"));

        var byDay = new Dictionary<string, (int n, long sec)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                byDay[reader.GetString(0)] = ((int)reader.GetInt64(1), reader.GetInt64(2));
        }

        for (var i = days - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            byDay.TryGetValue(day.ToString("yyyy-MM-dd"), out var v);
            result.Add(new FocusDaySummary(day, v.n, (int)(v.sec / 60)));
        }
        return result;
    }

    /// <summary>今日：番茄数只算完整专注；分钟算全部实际专注秒数（含未完成片段）。</summary>
    public (int Sessions, int Minutes) TodayFocus()
    {
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(completed), 0), COALESCE(SUM(seconds), 0) FROM pomodoro_log WHERE phase='Focus' AND substr(started_at,1,10)=$d";
        cmd.Parameters.AddWithValue("$d", today);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return ((int)reader.GetInt64(0), (int)(reader.GetInt64(1) / 60));
    }
}
