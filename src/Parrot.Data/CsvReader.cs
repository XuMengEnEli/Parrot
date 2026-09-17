namespace Parrot.Data;

/// <summary>
/// 最小 RFC4180 CSV 读取器：双引号包裹、"" 转义、字段内允许逗号/换行。
/// ECDICT 的 translation 字段实际带字面 "\n"（两字符）与真逗号，必须按引号状态解析。
/// </summary>
public static class CsvReader
{
    public static IEnumerable<string[]> ReadRecords(TextReader reader)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        int c;

        while ((c = reader.Read()) >= 0)
        {
            char ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    int next = reader.Peek();
                    if (next == '"')
                    {
                        sb.Append('"');
                        reader.Read(); // 吃掉转义的第二个引号
                    }
                    else
                        inQuotes = false;
                }
                else
                    sb.Append(ch);
            }
            else if (ch == '"')
                inQuotes = true;
            else if (ch == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                fields.Add(sb.ToString());
                sb.Clear();
                yield return fields.ToArray();
                fields.Clear();
            }
            else
                sb.Append(ch);
        }

        if (sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(sb.ToString());
            yield return fields.ToArray();
        }
    }
}
