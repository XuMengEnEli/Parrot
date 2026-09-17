using Microsoft.Data.Sqlite;

namespace Parrot.Data;

/// <summary>某天的学习记录汇总（列表页一行）。</summary>
public sealed record StudyDay(string Day, int Count);

/// <summary>学习记录里的一条生词（含释义快照与出处句）。</summary>
public sealed record StudyWordRow(string Word, string Meaning, string Note, string AddedAt);

/// <summary>
/// 学习记录仓储（需求：🔊 旁"记入当日学习"按钮）：按天记词、同日自动去重；
/// 弹窗复习与拼写考试的取词都优先从这里抽（见 WordbookCardSource / ExamPageViewModel）。
/// </summary>
public sealed class StudyLogRepository(LocalDatabase db)
{
    /// <summary>记入某天的学习列表；同日同词已存在则只刷新出处/释义为空时补齐，返回是否新增。</summary>
    public bool Add(string word, string meaning, string note, DateOnly day)
    {
        word = word.Trim().ToLowerInvariant();
        if (word.Length == 0) return false;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO study_log(day, word, meaning, note, added_at)
            VALUES ($d, $w, $m, $n, $a)
            ON CONFLICT(day, word) DO UPDATE SET
                meaning   = CASE WHEN study_log.meaning = '' THEN excluded.meaning ELSE study_log.meaning END,
                note      = CASE WHEN excluded.note <> '' THEN excluded.note ELSE study_log.note END
            """;
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$w", word);
        cmd.Parameters.AddWithValue("$m", meaning);
        cmd.Parameters.AddWithValue("$n", note);
        cmd.Parameters.AddWithValue("$a", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        // ON CONFLICT DO UPDATE 不影响总变更行数语义：Changes=1 两种情况都有，用存在性判断新增
        using (var pre = conn.CreateCommand())
        {
            pre.CommandText = "SELECT 1 FROM study_log WHERE day=$d AND word=$w";
            pre.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
            pre.Parameters.AddWithValue("$w", word);
            if (pre.ExecuteScalar() is not null)
            {
                cmd.ExecuteNonQuery();
                return false;
            }
        }
        cmd.ExecuteNonQuery();
        return true;
    }

    /// <summary>从某天的学习中移除一个词；返回是否确有其词被删。</summary>
    public bool Remove(string word, DateOnly day)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM study_log WHERE day=$d AND word=$w";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$w", word.Trim().ToLowerInvariant());
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>整天的记录清空（列表页"移除"按钮），返回删掉的条数。</summary>
    public int RemoveDay(DateOnly day)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM study_log WHERE day=$d";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>按日期分组倒序（列表页数据源）。</summary>
    public List<StudyDay> Days()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT day, COUNT(*) FROM study_log GROUP BY day ORDER BY day DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<StudyDay>();
        while (r.Read()) list.Add(new StudyDay(r.GetString(0), r.GetInt32(1)));
        return list;
    }

    /// <summary>
    /// 取词口径：释义快照为空时回落到词库现取。全量 ECDICT 是事后才导入的（首次启动只有内嵌 seed），
    /// 只读快照会让早先记的词永远显示"暂无释义"。
    /// </summary>
    private const string SelectWithMeaning = """
        SELECT sl.word, COALESCE(NULLIF(sl.meaning, ''), w.transl, ''), sl.note, sl.added_at
        FROM study_log sl LEFT JOIN words w ON w.word = sl.word
        """;

    public List<StudyWordRow> WordsOfDay(DateOnly day)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectWithMeaning} WHERE sl.day=$d ORDER BY sl.added_at, sl.word";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        var list = new List<StudyWordRow>();
        while (r.Read()) list.Add(MapRow(r));
        return list;
    }

    /// <summary>当天随机抽 n 个词（考试取词）；不足 n 返回全部。</summary>
    public List<StudyWordRow> RandomOfDay(DateOnly day, int n)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectWithMeaning} WHERE sl.day=$d ORDER BY RANDOM() LIMIT $n";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$n", Math.Max(1, n));
        using var r = cmd.ExecuteReader();
        var list = new List<StudyWordRow>();
        while (r.Read()) list.Add(MapRow(r));
        return list;
    }

    /// <summary>当天随机一个词（弹窗优先取词）；当天无记录返回 null。</summary>
    public StudyWordRow? RandomOneOfDay(DateOnly day)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectWithMeaning} WHERE sl.day=$d ORDER BY RANDOM() LIMIT 1";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapRow(r) : null;
    }

    private static StudyWordRow MapRow(SqliteDataReader r)
        => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));

    /// <summary>某词今天是否已在列表中（📌 按钮初始状态用）。</summary>
    public bool HasWord(string word, DateOnly day)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM study_log WHERE day=$d AND word=$w";
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$w", word.Trim().ToLowerInvariant());
        return cmd.ExecuteScalar() is not null;
    }
}
