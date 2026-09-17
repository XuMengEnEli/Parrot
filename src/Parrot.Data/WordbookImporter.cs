using System.Globalization;

namespace Parrot.Data;

/// <summary>
/// ECDICT 导入器：skywind3000/ECDICT csv（列序 word,phonetic,definition,translation,pos,collins,oxford,tag,…）。
/// 全量文件 200MB+ 不适合入仓：完整导入走 scripts/fetch-ecdict（下载后调用 ImportCsvFile）；
/// 首次启动内嵌 seed.csv（约 50 高频考研词）保证功能开箱即用。
/// </summary>
public static class WordbookImporter
{
    /// <summary>导入 csv（含表头），返回新增条数。</summary>
    public static int ImportCsvFile(WordbookRepository repo, string csvPath, DateOnly today)
    {
        using var reader = new StreamReader(csvPath);
        return Import(repo, reader, today);
    }

    public static int Import(WordbookRepository repo, TextReader reader, DateOnly today)
    {
        int added = 0, lineNo = 0;
        // 流式解析（CsvReader 状态机自身处理引号内换行），整批一个事务（全量 77 万行）
        using var bulk = repo.BeginBulk();
        foreach (var f in CsvReader.ReadRecords(reader))
        {
            lineNo++;
            if (f.Length < 4) continue;
            if (lineNo == 1 && string.Equals(f[0].Trim(), "word", StringComparison.OrdinalIgnoreCase))
                continue; // 表头

            var word = f[0].Trim();
            if (word.Length == 0 || word.Length > 64) continue;
            if (!word.All(c => char.IsAsciiLetter(c) || c is ' ' or '-' or '\'' or '.')) continue;

            var phonetic = f[1].Trim();
            // translation 里 \n 是字面两字符（ECDICT 约定），还原为换行提升可读性
            var translation = f[3].Replace("\\n", "\n", StringComparison.Ordinal).Trim();
            string? tag = f.Length > 7 && f[7].Trim().Length > 0 ? f[7].Trim() : null;

            if (repo.Import(word, phonetic, translation, tag, today))
                added++;
        }
        return added;
    }

    /// <summary>词库为空时导入内嵌 seed（首次启动兜底）。返回新增数。</summary>
    public static int SeedIfEmpty(WordbookRepository repo, DateOnly today)
    {
        if (repo.Count() > 0) return 0;
        var stream = typeof(WordbookImporter).Assembly
            .GetManifestResourceStream("Parrot.Data.Resources.seed-ecdict.csv")
            ?? throw new InvalidOperationException("内嵌 seed 词表缺失（检查 AvaloniaResource/EmbeddedResource 配置）");
        using var reader = new StreamReader(stream);
        return Import(repo, reader, today);
    }
}
