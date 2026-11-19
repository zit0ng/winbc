using System.Text;

namespace WinBC.Util;

/// <summary>Windows 命令行拆分/引号处理。</summary>
public static class CommandLineUtil
{
    /// <summary>按 Windows 规则拆分命令行（支持双引号与反斜杠转义）。</summary>
    public static List<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;

        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int backslashes = 1;
                while (i + 1 < commandLine.Length && commandLine[i + 1] == '\\') { backslashes++; i++; }
                if (i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    sb.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1) { sb.Append('"'); i++; }
                }
                else sb.Append('\\', backslashes);
                continue;
            }
            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    sb.Append('"'); i++;
                }
                else inQuotes = !inQuotes;
                continue;
            }
            if ((c == ' ' || c == '\t') && !inQuotes)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    /// <summary>去掉命令行开头的可执行文件，返回参数部分原文。</summary>
    public static string? StripExecutable(string? commandLine, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var tokens = Split(commandLine);
        if (tokens.Count <= 1) return null;

        int start = 0;
        if (exePath is not null)
        {
            var exeName = Path.GetFileName(exePath);
            if (string.Equals(tokens[0], exePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(tokens[0]), exeName, StringComparison.OrdinalIgnoreCase))
                start = 1;
        }
        if (start == 0 &&
            (tokens[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
             tokens[0].EndsWith(".com", StringComparison.OrdinalIgnoreCase)))
            start = 1;

        if (start >= tokens.Count) return null;
        return string.Join(' ', tokens.Skip(start).Select(QuoteIfNeeded));
    }

    public static string QuoteIfNeeded(string token)
    {
        if (string.IsNullOrEmpty(token)) return "\"\"";
        return token.Contains(' ') || token.Contains('\t') || token.Contains('"')
            ? "\"" + token.Replace("\"", "\\\"") + "\""
            : token;
    }

    public static string Quote(string value) => "\"" + value + "\"";
}
