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

var notRun = DashboardHealthAnalyzer.Analyze(null);
Check(notRun.OverallStatus == "Unknown" && notRun.Score is null,
    "dashboard reports unknown and no score before evidence exists");
Check(notRun.Components.Any(component => component.Name == "Windows Integrity" && component.Status == "Not Tested"),
    "an unrun integrity check is never classified as healthy");

var diagnosticJson = """
    {
      "CreatedAt":"2026-11-04T10:00:00+00:00",
      "Summary":{"SystemDrive":"C:","DiskFreeGB":1.2,"DiskSizeGB":512,"CpuLoadPercent":12.5,"MemoryUsedPercent":43.0},
      "Findings":[
        {"Area":"System","Severity":"Info","Finding":"Windows detected","Evidence":"Windows 11"},
        {"Area":"Storage","Severity":"Critical","Finding":"Low free space","Evidence":"1.2 GB free","Recommendation":"Review disk usage."},
        {"Area":"Services","Severity":"Info","Finding":"Service EventLog","Evidence":"Running"},
        {"Area":"Services","Severity":"Info","Finding":"Service Winmgmt","Evidence":"Running"},
        {"Area":"Services","Severity":"Info","Finding":"Service RpcSs","Evidence":"Running"}
      ],
      "Health":{"Score":65}
    }
    """;
var diagnostic = new OperationResult("Diagnostics", "Completed", diagnosticJson, "test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
var snapshot = DashboardHealthAnalyzer.Analyze(diagnostic);
Check(snapshot.OverallStatus == "Critical" && snapshot.CriticalCount == 1,
    "critical evidence determines overall dashboard status and issue count");
Check(snapshot.Score == 65 && snapshot.ScoreFactors.Count == 1,
    "score is calculated from measured findings and explains the affecting issue");
Check(snapshot.Components.Any(component => component.Name == "Storage" && component.Status == "Critical"),
    "storage health is derived from actual free-space evidence");
Check(snapshot.Components.Any(component => component.Name == "Network" && component.Status == "Not Tested"),
    "uncollected network evidence remains not tested");

var incompleteJson = """
    {
      "Summary":{"CpuLoadPercent":null,"MemoryUsedPercent":null},
      "Findings":[{"Area":"Services","Severity":"Low","Finding":"Não foi possível consultar o serviço X","Evidence":"Access denied"}]
    }
    """;
var incomplete = DashboardHealthAnalyzer.Analyze(
    new OperationResult("Diagnostics", "Completed", incompleteJson, "test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
Check(incomplete.Score is null && incomplete.ErrorCount == 0 &&
      incomplete.Issues.Any(issue => issue.Severity == "Unknown"),
    "unavailable measurements are visible but do not penalize the evidence-based score");
Check(incomplete.Components.Any(component => component.Name == "Services" && component.Status == "Unknown"),
    "a failed service query is unknown, not healthy or a confirmed warning");
var failed = DashboardHealthAnalyzer.Analyze(
    new OperationResult("Universal", "Failed", "", "worker unavailable", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
    "worker unavailable");
Check(failed.OverallStatus == "Unknown" && failed.Components.All(component => component.Status == "Unknown"),
    "a failed diagnostic is surfaced and cannot leave stale components classified healthy");

var allProblemsState = MainWindow.GetAllProblemsPageState(null, null, "", "Todos", "Todos");
Check(!allProblemsState.HasDiagnostic && allProblemsState.Message.Contains("Ainda não existem diagnósticos", StringComparison.OrdinalIgnoreCase),
    "all-problems page distinguishes no diagnostic from no issue");

var allProblemsJson = """
    {
      "Context": {
        "Summary": { "SystemDrive": "C:" },
        "Findings": [
          {"Area":"Storage","Severity":"Critical","Finding":"Low free space","Evidence":"1.2 GB free","Recommendation":"Review disk usage."}
        ]
      },
      "Analysis": {
        "Status": "Completed",
        "Hypotheses": [],
        "RuleFindings": [],
        "Anomalies": []
      }
    }
    """;
var actualProblems = MainWindow.GetAllProblemsPageState(
    new OperationResult("Diagnostics", "Completed", allProblemsJson, "test summary", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
    null, "storage", "Todos", "Todos");
Check(actualProblems.HasProblems && actualProblems.Problems.Count == 1 && actualProblems.Problems[0].Severity == "Critical",
    "all-problems page surfaces real findings and search filters by component");

var failedLoad = MainWindow.GetAllProblemsPageState(null, "worker unavailable", "", "Todos", "Todos");
Check(failedLoad.HasError && failedLoad.Message.Contains("Erro ao carregar", StringComparison.OrdinalIgnoreCase),
    "all-problems page shows a clear loading error state and retry guidance");

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
    var liveDashboard = DashboardHealthAnalyzer.Analyze(universalResult);
    Check(liveDashboard.Components.Count == 9 && liveDashboard.LastDiagnostic is not null,
        "live universal result populates all component health cards with a real timestamp");
    Check(liveDashboard.Components.Any(component => component.Name == "Windows Integrity" && component.Status == "Not Tested"),
        "universal evidence does not claim integrity testing that was not performed");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

return failures == 0 ? 0 : 1;
