using System.Text;

namespace WinFixPro;

public static class ReportFormatter
{
    public static string Format(OperationResult result, string format) => format switch
    {
        "JSON" => result.Json,
        "TXT" => $"{result.Summary}\r\nOperation: {result.Operation}\r\nStatus: {result.Status}\r\nCreated: {result.FinishedAt:O}\r\n\r\n{result.Json}",
        "CSV" => $"Operation,Status,Summary,CreatedAt,Json\r\n{Csv(result.Operation)},{Csv(result.Status)},{Csv(result.Summary)},{Csv(result.FinishedAt.ToString("O"))},{Csv(result.Json)}",
        "HTML" => $"<!doctype html><html><head><meta charset=\"utf-8\"><title>WinFix Pro report</title><style>body{{font-family:Segoe UI,Arial;margin:32px;background:#0b1120;color:#f8fafc}}pre{{white-space:pre-wrap;background:#172235;padding:16px;border-radius:8px}}</style></head><body><h1>WinFix Pro report</h1><p>{Html(result.Summary)}</p><p>{Html(result.Operation)} | {Html(result.Status)} | {Html(result.FinishedAt.ToString("O"))}</p><pre>{Html(result.Json)}</pre></body></html>",
        _ => throw new ArgumentOutOfRangeException(nameof(format), "Unsupported report format.")
    };

    public static Task WriteAsync(string path, OperationResult result, string format, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(path, Format(result, format), new UTF8Encoding(true), cancellationToken);

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string Html(string value) => System.Net.WebUtility.HtmlEncode(value);
}
