using Microsoft.Data.Sqlite;
using Parrot.Core.TextProcessing;

namespace Parrot.Data;

/// <summary>词库条目（视图模型友好，due/wrong 参与弹窗优先级）。</summary>
public sealed record WordEntry(
    string Word, string Phonetic, string Translation, string? Tag,
    int Reps, double Ease, int Interval, DateOnly Due, int WrongCount)
{
    public bool IsDue(DateOnly today) => Due <= today;
}

/// <summary>
/// 内嵌词库仓储（原"生词本"页已按需求移除，本表退为纯词典数据源）：ECDICT 导入、
/// 词条查询/随机取词。words 表保留 SM-2 列以兼容历史数据，但不再有评分入口。
/// </summary>
public sealed class WordbookRepository(LocalDatabase db)
{
    public void EnsureWordSchema() => db.EnsureSchema();

    private (SqliteConnection conn, SqliteTransaction tx)? _bulk;

    /// <summary>批量导入事务作用域：期间所有 Import 复用同一连接+单个事务。
    /// 全量 ECDICT 有 77 万行，逐行独立事务会慢两个数量级；中途异常时已完成的行照常落库
    /// （导入是幂等 upsert，重跑补齐即可）。</summary>
    public IDisposable BeginBulk()
    {
        if (_bulk is not null) return NoopScope.Instance; // 重入时沿用外层
        var conn = db.Open();
        _bulk = (conn, conn.BeginTransaction());
        return new BulkScope(this);
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    private sealed class BulkScope(WordbookRepository repo) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            repo.EndBulk();
        }
    }

    private void EndBulk()
    {
        if (_bulk is not { } b) return;
        _bulk = null;
        b.tx.Commit();
        b.tx.Dispose();
        b.conn.Dispose();
    }

    /// <summary>导入一条 ECDICT 记录（存在则仅补空字段，不重置复习状态——复习进度是用户资产）。</summary>
    public bool Import(string word, string phonetic, string translation, string? tag, DateOnly today)
    {
        if (_bulk is { } b) return ImportCore(b.conn, b.tx, word, phonetic, translation, tag, today);
        using var conn = db.Open();
        return ImportCore(conn, null, word, phonetic, translation, tag, today);
    }

    private static bool ImportCore(
        SqliteConnection conn, SqliteTransaction? tx,
        string word, string phonetic, string translation, string? tag, DateOnly today)
    {
        word = word.Trim().ToLowerInvariant();
        if (word.Length == 0) return false;

        using var check = conn.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT 1 FROM words WHERE word=$w";
        check.Parameters.AddWithValue("$w", word);
        if (check.ExecuteScalar() is not null)
        {
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE words SET phonetic=COALESCE(NULLIF(phonetic,''),$p),
                                 transl=CASE WHEN transl='' THEN $t ELSE transl END,
                                 tag=COALESCE(tag,$g)
                WHERE word=$w
                """;
            upd.Parameters.AddWithValue("$p", phonetic);
            upd.Parameters.AddWithValue("$t", translation);
            upd.Parameters.AddWithValue("$g", (object?)tag ?? DBNull.Value);
            upd.Parameters.AddWithValue("$w", word);
            upd.ExecuteNonQuery();
            return false;
        }

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO words(word, phonetic, transl, tag, source, added_at, reps, ease, interval, due, wrong_count)
            VALUES ($w, $p, $t, $g, 'ecdict', $a, 0, 2.5, 0, $d, 0)
            """;
        ins.Parameters.AddWithValue("$w", word);
        ins.Parameters.AddWithValue("$p", phonetic);
        ins.Parameters.AddWithValue("$t", translation);
        ins.Parameters.AddWithValue("$g", (object?)tag ?? DBNull.Value);
        ins.Parameters.AddWithValue("$a", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        ins.Parameters.AddWithValue("$d", today.ToString("yyyy-MM-dd"));
        ins.ExecuteNonQuery();
        return true;
    }

    /// <summary>手动加词（词库没有也能记）。</summary>
    public void AddManual(string word, string translation, DateOnly today)
    {
        word = word.Trim().ToLowerInvariant();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO words(word, phonetic, transl, tag, source, added_at, reps, ease, interval, due, wrong_count)
            VALUES ($w, '', $t, NULL, 'manual', $a, 0, 2.5, 0, $d, 0)
            ON CONFLICT(word) DO UPDATE SET transl=CASE WHEN words.transl='' THEN excluded.transl ELSE words.transl END
            """;
        cmd.Parameters.AddWithValue("$w", word);
        cmd.Parameters.AddWithValue("$t", translation);
        cmd.Parameters.AddWithValue("$a", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$d", today.ToString("yyyy-MM-dd"));
        cmd.ExecuteNonQuery();
    }

    public void Remove(string word)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM words WHERE word=$w";
        cmd.Parameters.AddWithValue("$w", word.Trim().ToLowerInvariant());
        cmd.ExecuteNonQuery();
    }

    public List<WordEntry> AllWords(int limit = 5000)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT word, phonetic, transl, tag, reps, ease, interval, due, wrong_count FROM words ORDER BY word LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<WordEntry>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public WordEntry? Lookup(string word)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT word, phonetic, transl, tag, reps, ease, interval, due, wrong_count FROM words WHERE word=$w";
        cmd.Parameters.AddWithValue("$w", word.Trim().ToLowerInvariant());
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>
    /// 一句英文里命中的词典短语（最长优先、互不重叠，按出现顺序）。
    /// 整句候选一次批量查（ECDICT 有 36 万条多词条目，逐候选查库会把一句拖成几十次往返）。
    /// </summary>
    public List<string> MatchPhrases(string text)
    {
        var tokens = PhraseMatcher.Tokenize(text);
        if (tokens.Count < PhraseMatcher.MinWords) return [];
        var candidates = PhraseMatcher.Candidates(tokens);
        if (candidates.Count == 0) return [];

        var inDictionary = new HashSet<string>(StringComparer.Ordinal);
        using var conn = db.Open();
        foreach (var chunk in candidates.Chunk(200))
        {
            using var cmd = conn.CreateCommand();
            var names = chunk.Select((_, i) => $"$c{i}").ToArray();
            // 只认"有真释义"的条目：ECDICT 的 36 万条短语里约 2.9 万条是网页抓取凑数的专名与自由组合
            // （"colour in → [网络] 颜色"），命中这种碎片会把整句切成无意义的两三个词。
            cmd.CommandText = $"""
                SELECT word FROM words
                WHERE word IN ({string.Join(",", names)})
                  AND trim(transl) <> '' AND transl NOT LIKE '[网络]%'
                """;
            for (int i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) inDictionary.Add(r.GetString(0));
        }
        return inDictionary.Count == 0 ? [] : PhraseMatcher.Find(tokens, inDictionary.Contains);
    }

    public int Count()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM words";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>随机一张未到期卡（弹窗兜底内容源）。</summary>
    public WordEntry? RandomCard(DateOnly today)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT word, phonetic, transl, tag, reps, ease, interval, due, wrong_count FROM words ORDER BY RANDOM() LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    private static WordEntry Map(SqliteDataReader r) => new(
        r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetInt32(4), r.GetDouble(5), r.GetInt32(6), ParseDate(r.GetString(7)), r.GetInt32(8));

    private static DateOnly ParseDate(string s)
        => DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d) ? d : DateOnly.FromDateTime(DateTime.Now);
}
