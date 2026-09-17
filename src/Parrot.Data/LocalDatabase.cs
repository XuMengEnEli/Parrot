using Microsoft.Data.Sqlite;
using IO = System.IO;

namespace Parrot.Data;

/// <summary>
/// 本地 SQLite：番茄记录、错题/生词（表结构随番茄钟与词库/学习记录功能细化，这里先立骨架与路径规范）。
/// 路径：Win %LOCALAPPDATA%\Parrot\parrot.db；mac ~/Library/Application Support/Parrot/。
/// 旧版目录名/库名（StudyEnglish / study-english.db）由 LegacyUserData 在取路径时搬移。
/// </summary>
public sealed class LocalDatabase
{
    public const string AppDataDirName = "Parrot";
    public const string DbFileName = "parrot.db";

    public LocalDatabase(string? path = null)
    {
        Path = path ?? DefaultPath();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
    }

    public string Path { get; }

    public string ConnectionString => new SqliteConnectionStringBuilder { DataSource = Path }.ToString();

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    public void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pomodoro_log (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                phase       TEXT    NOT NULL,           -- Focus / ShortBreak / LongBreak
                started_at  TEXT    NOT NULL,           -- ISO8601 本地时间
                seconds     INTEGER NOT NULL,           -- 实际进行秒数（含睡眠校正后）
                completed   INTEGER NOT NULL DEFAULT 0  -- 是否跑完整个阶段
            );
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS words (
                word        TEXT PRIMARY KEY,           -- 小写规范化
                phonetic    TEXT,
                transl      TEXT    NOT NULL DEFAULT '',-- 释义（多行 \n）
                tag         TEXT,                       -- 考纲标记 cet4/cet6/ky…
                source      TEXT    NOT NULL DEFAULT 'ecdict',
                added_at    TEXT    NOT NULL,
                reps        INTEGER NOT NULL DEFAULT 0, -- SM-2 状态
                ease        REAL    NOT NULL DEFAULT 2.5,
                interval    INTEGER NOT NULL DEFAULT 0,
                due         TEXT    NOT NULL,           -- yyyy-MM-dd
                wrong_count INTEGER NOT NULL DEFAULT 0  -- "忘记"次数 → 弹窗优先级
            );
            CREATE INDEX IF NOT EXISTS idx_words_due ON words(due);
            CREATE TABLE IF NOT EXISTS study_log (
                day        TEXT    NOT NULL,           -- yyyy-MM-dd（当日学习列表的"当日"）
                word       TEXT    NOT NULL,           -- 小写规范化
                meaning    TEXT    NOT NULL DEFAULT '',-- 记录瞬间从词库抓的释义快照（可空）
                note       TEXT    NOT NULL DEFAULT '',-- 记录时所在的句子/行（上下文，不上屏到弹窗）
                added_at   TEXT    NOT NULL,           -- yyyy-MM-dd HH:mm:ss
                PRIMARY KEY (day, word)                 -- 同一天重复点📌自动去重
            );
            CREATE INDEX IF NOT EXISTS idx_study_day ON study_log(day);
            """;
        cmd.ExecuteNonQuery();
    }

    public static string DefaultPath()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        Core.LegacyUserData.MigrateInto(root);
        return IO.Path.Combine(root, AppDataDirName, DbFileName);
    }
}
