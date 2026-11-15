using System.Globalization;
using System.Text;

namespace CivicOps.Web.Services;

public static class CsvExport
{
    public static readonly string[] Header =
        ["Number", "Title", "Category", "District", "Priority", "Status", "Assignee", "Created (UTC)", "Due (UTC)", "Resolved (UTC)", "Overdue"];

    public static string Build(IEnumerable<RequestRow> rows, DateTime now)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // BOM so Excel opens it as UTF-8
        sb.Append(string.Join(',', Header)).Append("\r\n");
        foreach (var r in rows)
        {
            sb.Append(string.Join(',', new[]
            {
                Escape(r.Number), Escape(r.Title), Escape(r.Category), Escape(r.District), Escape(r.Priority.ToString()),
                Escape(StatusRules.Label(r.Status)), Escape(r.Assignee ?? ""), Fmt(r.CreatedAt), Fmt(r.DueAt),
                r.ResolvedAt is { } at ? Fmt(at) : "", r.IsOverdue(now) ? "Yes" : "No"
            })).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Fmt(DateTime d) => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>RFC 4180 quoting plus neutralization of spreadsheet formulas (CSV injection).</summary>
    public static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
