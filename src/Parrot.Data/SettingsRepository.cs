namespace Parrot.Data;

/// <summary>设置持久化：meta 表 KV（热键开关、托盘行为、弹窗间隔、音色等）。全部主线程读写，量小无需异步。</summary>
public sealed class SettingsRepository(LocalDatabase db)
{
    public string? Get(string key)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public bool GetBool(string key, bool fallback)
        => bool.TryParse(Get(key), out var v) ? v : fallback;

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), out var v) ? v : fallback;

    // —— 已知键的强类型门面（默认值即产品默认）——
    public const string KeyGlobalHotkey = "bosskey.global";   // SharpHook 全局热键
    public const string KeyCloseToTray = "main.closeToTray";  // 关窗最小化到托盘
    public const string KeyPopupEnabled = "popup.enabled";    // 右下角伪广告弹窗
    public const string KeyPopupIntervalMin = "popup.interval"; // 弹窗间隔（分钟）
    public const string KeyVoice = "tts.voice";               // Edge 音色
    public const string KeyExamBlanks = "exam.blanks";        // 拼写考试每词挖空字母数
    public const string KeyUpdateAuto = "update.auto";         // 在线升级：启动时静默检查（仓库写死在 GitHubUpdateService.ParrotRepo）

    public bool BossKeyGlobal
    {
        get => GetBool(KeyGlobalHotkey, true);
        set => Set(KeyGlobalHotkey, value.ToString());
    }

    public bool CloseToTray
    {
        get => GetBool(KeyCloseToTray, true);
        set => Set(KeyCloseToTray, value.ToString());
    }

    public bool PopupEnabled
    {
        get => GetBool(KeyPopupEnabled, true);
        set => Set(KeyPopupEnabled, value.ToString());
    }

    public int PopupIntervalMinutes
    {
        get => GetInt(KeyPopupIntervalMin, 10);
        set => Set(KeyPopupIntervalMin, value.ToString());
    }

    public string Voice
    {
        get => Get(KeyVoice) ?? "en-US-AriaNeural";
        set => Set(KeyVoice, value);
    }

    /// <summary>拼写考试每词随机挖掉的字母数（1–6，默认 3；短词自动少挖，至少露 2 个字母）。</summary>
    public int ExamBlanks
    {
        get => Math.Clamp(GetInt(KeyExamBlanks, 3), 1, 6);
        set => Set(KeyExamBlanks, Math.Clamp(value, 1, 6).ToString());
    }

    public bool UpdateAutoCheck
    {
        get => GetBool(KeyUpdateAuto, true);
        set => Set(KeyUpdateAuto, value.ToString());
    }
}
