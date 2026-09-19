using Microsoft.Data.Sqlite;
using Parrot.Core.Review;

namespace Parrot.Data;

/// <summary>复习队列的一行：词 + 释义 + 钉住时的出处句 + 当前档位与下次到期。</summary>
public sealed record ReviewRow(string Word, string Meaning, string Note, int Visits, DateOnly Due, int Lapses)
{
    public string PhaseText => EbbinghausCycle.PhaseLabel(Visits);
}

/// <summary>复习日历的一格：某个到期日有多少词在等。</summary>
public sealed record ReviewAgenda(DateOnly Due, int Count);

/// <summary>
/// 艾宾浩斯复习进度仓储（每词一行，与词典表解耦）：
/// 📌 钉住即建档（首访 1 天后），当日复习队列被展示即整体推进一档，拼写考试答错退回一档并次日再见。
/// 排期规则本体在 Core 的 EbbinghausCycle 里（纯函数），这里只负责落库与取数。
/// </summary>
public sealed class ReviewRepository(LocalDatabase db)
{
    /// <summary>钉住一个词时建档；已有进度则原样保留（复习进度是用户资产，重复钉住不重置曲线）。</summary>
    public void ScheduleFirst(string word, DateOnly today)
    {
        word = TermKey.Of(word);
        if (word.Length == 0) return;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO review_state(word, first_day, visits, due, last_seen, lapses)
            VALUES ($w, $d, 0, $due, '', 0)
            ON CONFLICT(word) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$w", word);
        cmd.Parameters.AddWithValue("$d", Fmt(today));
        cmd.Parameters.AddWithValue("$due", Fmt(EbbinghausCycle.FirstDue(today)));
        cmd.ExecuteNonQuery();
    }

    /// <summary>今日到期的词（含逾期），逾期久的与答错多的排在前面。</summary>
    public List<ReviewRow> DueQueue(DateOnly today, int limit = 500)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectReview} WHERE rs.due <= $d ORDER BY rs.due, rs.lapses DESC, rs.word LIMIT $n";
        cmd.Parameters.AddWithValue("$d", Fmt(today));
        cmd.Parameters.AddWithValue("$n", Math.Max(1, limit));
        return Read(conn, cmd);
    }

    public int CountDue(DateOnly today)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM review_state WHERE due <= $d";
        cmd.Parameters.AddWithValue("$d", Fmt(today));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>今日之后的排期（复习日历）：按到期日聚合成"哪天有多少词要见"。</summary>
    public List<ReviewAgenda> Agenda(DateOnly today)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT due, COUNT(*) FROM review_state WHERE due > $d GROUP BY due ORDER BY due
            """;
        cmd.Parameters.AddWithValue("$d", Fmt(today));
        using var r = cmd.ExecuteReader();
        var list = new List<ReviewAgenda>();
        while (r.Read()) list.Add(new ReviewAgenda(ParseDate(r.GetString(0)), r.GetInt32(1)));
        return list;
    }

    /// <summary>
    /// 打开今日复习列表：先把到期（含逾期）的词整体推进一档，再返回"今天该见的词"。
    /// 判据是 <c>due &lt;= 今天 或 last_seen = 今天</c>——后半句保证同一天再打开一次列表不会空掉，
    /// 而且列出的是推进后的状态（"下次"即下一档日期）。
    /// 推进本身同日幂等（last_seen 判据），所以反复刷新不会把曲线拉快。
    /// </summary>
    public List<ReviewRow> OpenTodayQueue(DateOnly today)
    {
        var due = DueQueue(today);
        if (due.Count > 0)
        {
            string seen = Fmt(today);
            using var conn = db.Open();
            using var tx = conn.BeginTransaction();
            foreach (var row in due)
            {
                var (visits, nextDue) = EbbinghausCycle.AfterReview(today, row.Visits);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE review_state
                    SET visits=$v, due=$due, last_seen=$seen
                    WHERE word=$w AND (last_seen = '' OR last_seen <> $seen)
                    """;
                cmd.Parameters.AddWithValue("$v", visits);
                cmd.Parameters.AddWithValue("$due", Fmt(nextDue));
                cmd.Parameters.AddWithValue("$seen", seen);
                cmd.Parameters.AddWithValue("$w", row.Word);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        // 展示顺序沿用推进前的紧急度（逾期最久、答错最多的先见）；推进后的新到期日只作每行的"下次"
        var urgency = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < due.Count; i++) urgency[due[i].Word] = i;
        var rows = TodayRows(today);
        return rows.Count > 1 && urgency.Count > 0
            ? [.. rows.OrderBy(r => urgency.TryGetValue(r.Word, out int i) ? i : int.MaxValue)]
            : rows;
    }

    /// <summary>今天该复习的词：仍到期（含逾期）的 + 今天已经推进过一次的。</summary>
    public List<ReviewRow> TodayRows(DateOnly today)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        string d = Fmt(today);
        cmd.CommandText = $"{SelectReview} WHERE rs.due <= $d OR rs.last_seen = $s ORDER BY rs.due, rs.lapses DESC, rs.word";
        cmd.Parameters.AddWithValue("$d", d);
        cmd.Parameters.AddWithValue("$s", d);
        return Read(conn, cmd);
    }

    /// <summary>答错回退：退回上一档、次日再见，并记下今日已处理（避免同日又被队列推进一次）。</summary>
    public void MarkLapse(string word, DateOnly today)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE review_state
            SET visits=$v, due=$due, lapses=lapses+1, last_seen=$seen
            WHERE word=$w
            """;
        var (visits, due) = EbbinghausCycle.AfterLapse(today, LookupVisits(conn, word));
        cmd.Parameters.AddWithValue("$v", visits);
        cmd.Parameters.AddWithValue("$due", Fmt(due));
        cmd.Parameters.AddWithValue("$seen", Fmt(today));
        cmd.Parameters.AddWithValue("$w", TermKey.Of(word));
        cmd.ExecuteNonQuery();
    }

    /// <summary>SQLite 的 INTEGER 取回来是 Int64，别按 int 模式匹配（匹配不上会静默变 0）。</summary>
    private static int LookupVisits(SqliteConnection conn, string word)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT visits FROM review_state WHERE word=$w";
        cmd.Parameters.AddWithValue("$w", TermKey.Of(word));
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt32(v);
    }

    /// <summary>
    /// 老记录回填（升级路径）：曲线上线之前钉住的词没有排期行，按其最早钉住日 +1 天建档；
    /// 该日期已过（多半如此）就直接落到今天，旧词一升上来就进当日复习队列。幂等，只补缺失词。
    /// </summary>
    public int BackfillFromStudyLog(DateOnly today)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO review_state(word, first_day, visits, due, last_seen, lapses)
            SELECT s.word, MIN(s.day), 0,
                   CASE WHEN date(MIN(s.day), '+1 day') <= $d THEN $d ELSE date(MIN(s.day), '+1 day') END,
                   '', 0
            FROM study_log s
            WHERE s.word NOT IN (SELECT word FROM review_state)
            GROUP BY s.word
            """;
        cmd.Parameters.AddWithValue("$d", Fmt(today));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>已没有任何 📌 记录挂靠的进度行清掉（移除单词/清空某天记录后调用）。</summary>
    public int PruneUnpinned()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM review_state WHERE word NOT IN (SELECT DISTINCT word FROM study_log)";
        return cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM review_state";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>释义口径与学习记录一致：先取最近一次钉住时的快照，空则回落到词库现取。</summary>
    private const string SelectReview = """
        SELECT rs.word,
               COALESCE(NULLIF(sl.meaning, ''), w.transl, ''),
               COALESCE(sl.note, ''),
               rs.visits, rs.due, rs.lapses
        FROM review_state rs
        LEFT JOIN words w ON w.word = rs.word
        LEFT JOIN study_log sl ON sl.rowid = (
            SELECT s.rowid FROM study_log s WHERE s.word = rs.word ORDER BY s.day DESC LIMIT 1)
        """;

    private static List<ReviewRow> Read(SqliteConnection conn, SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<ReviewRow>();
        while (r.Read())
            list.Add(new ReviewRow(r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetInt32(3), ParseDate(r.GetString(4)), r.GetInt32(5)));
        return list;
    }

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd");

    private static DateOnly ParseDate(string s)
        => DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d) ? d : DateOnly.FromDateTime(DateTime.Now);
}
