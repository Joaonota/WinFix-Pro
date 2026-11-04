using System.Text;
using Microsoft.Data.Sqlite;
using WinFixPro;

var failures = 0;
void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
    if (!condition) failures++;
}

Check(OperationCatalog.TryGet("Universal", out _), "allow-list accepts a supported diagnostic");
Check(!OperationCatalog.TryGet("Invoke-Expression", out _), "allow-list rejects arbitrary operation names");
Check(OperationCatalog.TryGet("Repair:SfcScan", out var repair) && repair.IsRepair && repair.RequiresAdministrator,
    "repair operations require administrator authorization");
Check(!OperationCatalog.TryGet("Repair:Unknown", out _), "allow-list rejects an unknown repair");

var root = Path.Combine(Path.GetTempPath(), $"WinFixPro.App.Tests.{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var history = new HistoryStore(Path.Combine(root, "History", "test.db"));
    var now = DateTimeOffset.UtcNow;
    var operation = new OperationResult("Diagnostics", "Completed", "{\"message\":\"test\"}", "test summary", now, now);
    var id = await history.AddAsync(operation);
    var records = await history.GetRecentAsync(10);
    Check(id > 0 && records.Count == 1 && records[0].Json == operation.Json,
        "SQLite operation history persists and reloads a result");
    await history.SetSettingAsync("OperationTimeoutMinutes", "30");
    Check(await history.GetSettingAsync("OperationTimeoutMinutes") == "30",
        "SQLite settings persist and reload a preference");

    var unsafeResult = operation with
    {
        Summary = "<script>alert(\"x\")</script>",
        Json = "{\"value\":\"<script>\"}"
    };
    var html = ReportFormatter.Format(unsafeResult, "HTML");
    var csv = ReportFormatter.Format(unsafeResult, "CSV");
    Check(!html.Contains("<script>", StringComparison.Ordinal) && html.Contains("&lt;script&gt;", StringComparison.Ordinal),
        "HTML report encodes untrusted diagnostic text");
    Check(csv.Contains("alert(\"\"x\"\")</script>", StringComparison.Ordinal),
        "CSV report escapes embedded quotes");
    Check(ReportFormatter.Format(operation, "JSON") == operation.Json,
        "JSON report preserves the structured payload");

    var outputPath = Path.Combine(root, "report.json");
    await ReportFormatter.WriteAsync(outputPath, operation, "JSON");
    var bytes = await File.ReadAllBytesAsync(outputPath);
    Check(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()),
        "exported reports use explicit UTF-8 encoding");
    Check((await File.ReadAllTextAsync(outputPath, Encoding.UTF8)).Contains(operation.Json, StringComparison.Ordinal),
        "report export writes the expected report content");

    var engine = new PowerShellEngine(Path.Combine(root, "EngineData"));
    var engineResult = await engine.ExecuteAsync("Dashboard", timeout: TimeSpan.FromMinutes(2));
    using var engineJson = System.Text.Json.JsonDocument.Parse(engineResult.Json);
    Check(engineResult.Status == "Completed" && engineJson.RootElement.TryGetProperty("Summary", out _),
        "embedded PowerShell worker returns a structured live dashboard result");
    var universalResult = await engine.ExecuteAsync("Universal",
        new PowerShellPayload { Symptoms = ["intermittent slowdown"] },
        timeout: TimeSpan.FromMinutes(5));
    using var universalJson = System.Text.Json.JsonDocument.Parse(universalResult.Json);
    Check(universalResult.Status == "Completed" &&
        universalJson.RootElement.TryGetProperty("Analysis", out var analysis) &&
        analysis.TryGetProperty("Status", out _),
        "embedded worker completes evidence-based universal analysis");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

return failures == 0 ? 0 : 1;
