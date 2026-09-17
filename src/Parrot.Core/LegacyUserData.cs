namespace Parrot.Core;

/// <summary>
/// 品牌改名（StudyEnglish → Parrot）后的本地数据搬迁。
/// 各取数据目录的入口在拼路径前调用一次：旧目录存在且新目录不存在时整体搬移，
/// 老用户的词库、学习记录、最近文档、TTS 缓存都不会丢；已搬过则是空操作。
/// </summary>
public static class LegacyUserData
{
    public const string OldDirName = "StudyEnglish";
    public const string NewDirName = "Parrot";

    private const string LegacyDbFileName = "study-english.db";

    /// <summary><paramref name="parentDir"/> 为数据根的父目录（如 %LOCALAPPDATA% 或 ~/Library/Application Support）。</summary>
    public static void MigrateInto(string parentDir)
    {
        if (string.IsNullOrEmpty(parentDir)) return;
        var oldDir = Path.Combine(parentDir, OldDirName);
        var newDir = Path.Combine(parentDir, NewDirName);
        if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;
        try
        {
            Directory.Move(oldDir, newDir);
            var legacyDb = Path.Combine(newDir, LegacyDbFileName);
            if (File.Exists(legacyDb))
                File.Move(legacyDb, Path.Combine(newDir, "parrot.db"));
        }
        catch
        {
            // 只读卷/权限不足时退回空库，绝不影响启动
        }
    }
}
