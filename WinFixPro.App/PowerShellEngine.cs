using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace WinFixPro;

public sealed class PowerShellEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _dataRoot;
    private readonly string _moduleRoot;
    private readonly string _workerPath;
    private readonly string _powerShellPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PowerShellEngine(string dataRoot)
    {
        _dataRoot = dataRoot;
        _moduleRoot = Path.Combine(dataRoot, "Engine");
        _workerPath = Path.Combine(_moduleRoot, "Worker.ps1");
        _powerShellPath = LocatePowerShell();
        MaterializeTrustedResources();
    }

    public string EngineVersion => $"{(Path.GetFileName(_powerShellPath).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ? "PowerShell 7" : "Windows PowerShell 5.1")} {FileVersionInfo.GetVersionInfo(_powerShellPath).FileVersion}";
    public string EnginePath => _powerShellPath;

    public async Task<OperationResult> ExecuteAsync(
        string operation,
        PowerShellPayload? payload = null,
        IProgress<(int Percent, string Message)>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (!OperationCatalog.TryGet(operation, out var definition))
            throw new ArgumentException("Operação não suportada.", nameof(operation));
        if (definition.IsRepair && string.IsNullOrWhiteSpace(payload?.RepairId))
            throw new ArgumentException("A identificação da reparação é obrigatória.", nameof(payload));

        payload ??= new PowerShellPayload();
        if (payload.Symptoms.Length > 20 || payload.Symptoms.Any(s => s is null || s.Length > 1000))
            throw new ArgumentException("Foram fornecidos demasiados sintomas ou texto acima do limite.", nameof(payload));
        if (payload.KnowledgeQuery?.Length > 500)
            throw new ArgumentException("A pesquisa excede 500 caracteres.", nameof(payload));
        if (payload.CaseSources.Length > 20 || payload.CaseSources.Any(source => source is null || source.Length > 2000))
            throw new ArgumentException("Foram fornecidas demasiadas fontes ou URLs acima do limite.", nameof(payload));
        if (new[] { payload.CaseOutcome, payload.CaseEvidence, payload.CaseHypotheses, payload.CaseTests, payload.ReviewNotes }
            .Any(value => value?.Length > 5000))
            throw new ArgumentException("Os detalhes do caso excedem o limite de 5000 caracteres.", nameof(payload));
        if (payload.RepairId is not null && payload.RepairId is not ("SfcScan" or "DismRestore" or "FlushDns"))
            throw new ArgumentException("A reparação solicitada não está permitida.", nameof(payload));

        await _gate.WaitAsync(cancellationToken);
        var started = DateTimeOffset.Now;
        string? payloadPath = null;
        try
        {
            var operationDirectory = Path.Combine(_dataRoot, "Operations");
            Directory.CreateDirectory(operationDirectory);
            payloadPath = Path.Combine(operationDirectory, $"{Guid.NewGuid():N}.json");
            var safePayload = JsonSerializer.Serialize(payload, JsonOptions);
            await File.WriteAllTextAsync(payloadPath, safePayload, new UTF8Encoding(false), cancellationToken);
            var startInfo = new ProcessStartInfo
            {
                FileName = _powerShellPath,
                WorkingDirectory = _moduleRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(_workerPath);
            startInfo.ArgumentList.Add("-Operation");
            startInfo.ArgumentList.Add(definition.IsRepair ? "Repair" : operation);
            startInfo.ArgumentList.Add("-PayloadPath");
            startInfo.ArgumentList.Add(payloadPath);

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null) return;
                if (TryReadProgress(args.Data, out var current))
                    progress?.Report(current);
                else
                    stdout.AppendLine(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null) stderr.AppendLine(args.Data);
            };

            if (!process.Start()) throw new InvalidOperationException("Não foi possível iniciar o motor PowerShell interno.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout ?? TimeSpan.FromMinutes(15));
            try
            {
                await process.WaitForExitAsync(linked.Token);
                process.WaitForExit();
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                if (cancellationToken.IsCancellationRequested)
                    throw;
                throw new TimeoutException($"A operação excedeu o limite de {(timeout ?? TimeSpan.FromMinutes(15)).TotalMinutes:0} minuto(s).");
            }

            var errorText = stderr.ToString().Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
                    ? $"O motor PowerShell terminou com código {process.ExitCode}."
                    : errorText);

            var resultLine = stdout.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));
            if (resultLine is null) throw new InvalidDataException("O motor PowerShell não devolveu um resultado estruturado.");
            using var json = JsonDocument.Parse(resultLine);
            if (!json.RootElement.TryGetProperty("type", out var type) || type.GetString() != "result" ||
                !json.RootElement.TryGetProperty("data", out var data))
                throw new InvalidDataException("O motor PowerShell devolveu um envelope de resultado inválido.");
            var normalizedJson = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            var summary = BuildSummary(operation, data);
            if (!string.IsNullOrWhiteSpace(errorText))
                summary += $" Warning(s): {errorText}";
            return new OperationResult(operation, "Completed", normalizedJson, summary, started, DateTimeOffset.Now);
        }
        finally
        {
            if (payloadPath is not null)
            {
                try { File.Delete(payloadPath); } catch (IOException) { }
            }
            _gate.Release();
        }
    }

    private static bool TryReadProgress(string line, out (int Percent, string Message) progress)
    {
        progress = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("type", out var type) || type.GetString() != "progress") return false;
            var percent = document.RootElement.TryGetProperty("percent", out var p) ? p.GetInt32() : 0;
            var message = document.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "A executar" : "A executar";
            progress = (Math.Clamp(percent, 0, 100), message);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildSummary(string operation, JsonElement data)
    {
        if (operation == "Universal" && data.TryGetProperty("Analysis", out var analysis))
        {
            var status = analysis.TryGetProperty("Status", out var s) ? s.GetString() : "unknown";
            var count = analysis.TryGetProperty("Hypotheses", out var h) && h.ValueKind == JsonValueKind.Array ? h.GetArrayLength() : 0;
            return $"Universal analysis: {status}; {count} hypothesis/hypotheses.";
        }
        if (operation.StartsWith("Repair:", StringComparison.Ordinal))
            return $"{OperationCatalog.Operations[operation].Title} completed; review the reported output and run a follow-up diagnostic.";
        if (operation == "Dashboard" && data.TryGetProperty("Summary", out var summary) &&
            summary.TryGetProperty("Caption", out var caption))
            return caption.GetString() ?? "Dashboard completed.";
        return $"{OperationCatalog.Operations[operation].Title} completed.";
    }

    private void MaterializeTrustedResources()
    {
        Directory.CreateDirectory(_moduleRoot);
        var assembly = Assembly.GetExecutingAssembly();
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WinFixPro.PowerShell.Worker.ps1"] = _workerPath,
            ["WinFixPro.PowerShell.Rules.Issues.json"] = Path.Combine(_moduleRoot, "Rules", "Issues.json"),
            ["WinFixPro.PowerShell.Profiles.default.json"] = Path.Combine(_moduleRoot, "Profiles", "default.json"),
            ["WinFixPro.PowerShell.Knowledge.knowledge.json"] = Path.Combine(_moduleRoot, "Knowledge", "knowledge.json"),
            ["WinFixPro.PowerShell.Plugins.ExampleDiagnostics.psm1"] = Path.Combine(_moduleRoot, "Plugins", "ExampleDiagnostics.psm1"),
            ["WinFixPro.PowerShell.Plugins.ExampleDiagnostics.json"] = Path.Combine(_moduleRoot, "Plugins", "ExampleDiagnostics.json")
        };
        foreach (var name in new[]
        {
            "DiagnosticsEngine","EventAnalyzer","HealthScore","KnowledgeEngine","Logging","NetworkAnalyzer",
            "PerformanceAnalyzer","Reports","SecurityAnalyzer","ServiceManager","SystemInfo",
            "UniversalCollector","UniversalDiagnostics"
        })
            map[$"WinFixPro.PowerShell.Core.{name}.psm1"] = Path.Combine(_moduleRoot, "Core", $"{name}.psm1");
        map["WinFixPro.PowerShell.Repairs.RepairEngine.psm1"] = Path.Combine(_moduleRoot, "Repairs", "RepairEngine.psm1");

        foreach (var (resourceName, destination) in map)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Required internal module resource '{resourceName}' is missing.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var existing = File.Exists(destination) ? File.ReadAllBytes(destination) : [];
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(existing)))
                File.WriteAllBytes(destination, bytes);
            var written = File.ReadAllBytes(destination);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(written)))
                throw new InvalidDataException($"Internal module integrity verification failed for '{Path.GetFileName(destination)}'.");
        }
    }

    private static string LocatePowerShell()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        candidates.Add(Path.Combine(programFilesX86, "PowerShell", "7", "pwsh.exe"));
        candidates.Add(Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
        var selected = candidates.FirstOrDefault(File.Exists);
        if (selected is null)
            throw new FileNotFoundException("Windows PowerShell 5.1 was not found. This Windows component is required.");
        return selected;
    }
}
