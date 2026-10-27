using System.Globalization;
using System.Text;

namespace IISManager.Console;

public static class OperationExport
{
    public static byte[] Csv(IEnumerable<ConsoleOperationSummary> rows)
    {
        static string Cell(string? value)
        {
            var text = value ?? "";
            if (text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }
        var output = new StringBuilder();
        output.AppendLine(string.Join(',', new[] { "操作编号", "服务器编号", "动作", "目标", "执行状态", "操作人", "平台保存", "提交时间", "最近核对", "结果代码" }.Select(Cell)));
        foreach (var row in rows)
            output.AppendLine(string.Join(',', new[] { row.OperationId.ToString("D"), row.ServerId, row.Type, row.Target, row.State,
                row.OperatorName, row.PersistenceState, row.AcceptedAt.ToString("O", CultureInfo.InvariantCulture),
                row.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), row.ErrorCode }.Select(Cell)));
        return new UTF8Encoding(false).GetBytes(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
