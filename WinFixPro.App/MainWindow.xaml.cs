using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace WinFixPro;

public partial class MainWindow : Window
{
    private static readonly string[] Sections =
    [
        "Dashboard", "Universal Diagnostics", "Hardware Diagnostics", "Performance Analyzer",
        "Network Diagnostics", "Windows Health", "Windows Update", "Security", "Services",
        "Drivers", "Common Issues", "Knowledge Center", "Unknown Problem Analysis",
        "Repair Center", "Reports", "History", "Settings"
    ];

    private readonly bool _isAdministrator;
    private readonly string _dataRoot;
    private readonly PowerShellEngine _engine;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCancellation;
    private OperationResult? _lastResult;
    private string _currentSection = "Dashboard";
    private bool _operationRunning;

    public MainWindow(bool isAdministrator, bool elevationCancelled)
    {
        InitializeComponent();
        _isAdministrator = isAdministrator;
        _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinFixPro", "0.2");
        Directory.CreateDirectory(_dataRoot);
        _engine = new PowerShellEngine(_dataRoot);
        _history = new HistoryStore(Path.Combine(_dataRoot, "History", "WinFixPro.db"));

        ComputerNameText.Text = Environment.MachineName;
        PrivilegeText.Text = isAdministrator ? "Administrator" : "Standard user - limited repairs";
        PrivilegeText.Foreground = isAdministrator
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
        EngineText.Text = $"Internal engine: {_engine.EngineVersion}\nData: %LOCALAPPDATA%\\WinFixPro\\0.2";
        FooterText.Text = elevationCancelled
            ? "UAC declined: diagnostics remain available; administrative repairs are disabled."
            : "Local diagnostics | No telemetry";

        BuildNavigation();
        Navigate("Dashboard");
        CancelButton.Click += (_, _) => CancelCurrentOperation();
        Closing += (_, _) => _operationCancellation?.Cancel();
    }

    private void BuildNavigation()
    {
        foreach (var section in Sections)
        {
            var button = new Button
            {
                Content = section,
                Tag = section,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 8, 8),
                Margin = new Thickness(0, 1, 0, 1),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 24, 39))
            };
            button.Click += (_, _) => Navigate((string)button.Tag);
            NavigationItems.Items.Add(button);
        }
    }

    private void Navigate(string section)
    {
        if (_operationRunning) return;
        _currentSection = section;
        PageTitle.Text = section;
        PageDescription.Text = section switch
        {
            "Universal Diagnostics" => "Recolha local e análise baseada em evidências; desconhecidos permanecem inconclusivos.",
            "Unknown Problem Analysis" => "Investigue sintomas com testes não destrutivos e sem pressupor uma solução conhecida.",
            "Knowledge Center" => "Consulte conhecimento local aprovado e compatível com esta versão do Windows.",
            "Repair Center" => "Reparações fixas, confirmadas individualmente e executadas em segundo plano.",
            "History" => "Resultados e operações administrativas guardados na base SQLite local.",
            "Settings" => "Motor interno, privilégios, armazenamento local e preferências da aplicação.",
            _ => "Windows Diagnostic & Repair Toolkit"
        };
        ActionPanel.Children.Clear();
        AddActions(section);
    }

    private void AddActions(string section)
    {
        void Add(string operation, string? caption = null, PowerShellPayload? payload = null)
        {
            if (!OperationCatalog.TryGet(operation, out var definition)) return;
            var button = new Button
            {
                Content = caption ?? definition.Title,
                Tag = operation,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                ToolTip = definition.Description
            };
            button.Click += async (_, _) => await RunOperationAsync(operation, payload);
            ActionPanel.Children.Add(button);
        }

        void AddLabel(string text)
        {
            ActionPanel.Children.Add(new TextBlock
            {
                Text = text.ToUpperInvariant(),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 12, 0, 5)
            });
        }

        switch (section)
        {
            case "Dashboard":
                Add("Dashboard", "Refresh live system summary");
                Add("Diagnostics", "Run basic Windows health scan");
                Add("Universal", "Run full system analysis", new PowerShellPayload());
                break;
            case "Universal Diagnostics":
                Add("Universal", "Full System Analysis", NewSymptomsPayload());
                Add("Universal", "Advanced Diagnostics", NewSymptomsPayload());
                Add("CommonIssues", "Evaluate compatible rules");
                Add("Unknown", "Investigate unknown symptom", NewSymptomsPayload());
                Add("Knowledge", "Search approved local knowledge", NewKnowledgePayload());
                AddLabel("Case Intelligence");
                AddCaseActions();
                break;
            case "Hardware Diagnostics":
                Add("Hardware");
                Add("Drivers", "Inspect problem devices");
                break;
            case "Performance Analyzer":
                Add("Performance");
                Add("Universal", "Compare against local baseline", NewSymptomsPayload());
                break;
            case "Network Diagnostics":
                Add("Network");
                break;
            case "Windows Health":
                Add("Diagnostics");
                AddLabel("Confirmed Repairs");
                AddRepair("SfcScan");
                AddRepair("DismRestore");
                break;
            case "Windows Update":
                Add("WindowsUpdate");
                AddLabel("No policy changes are offered by this action.");
                break;
            case "Security":
                Add("Security");
                break;
            case "Services":
                Add("Services");
                break;
            case "Drivers":
                Add("Drivers");
                Add("Hardware", "Hardware inventory");
                break;
            case "Common Issues":
                Add("CommonIssues");
                Add("Knowledge", "Search approved local knowledge", NewKnowledgePayload());
                break;
            case "Knowledge Center":
                Add("Knowledge", "Search approved local knowledge", NewKnowledgePayload());
                Add("Knowledge", "List compatible entries");
                AddKnowledgeImport();
                AddKnowledgeReview();
                AddCaseActions();
                break;
            case "Unknown Problem Analysis":
                Add("Unknown", "Run unknown-problem analysis", NewSymptomsPayload());
                AddCaseActions();
                break;
            case "Repair Center":
                AddRepair("SfcScan");
                AddRepair("DismRestore");
                AddRepair("FlushDns");
                break;
            case "Reports":
                AddReportButtons();
                break;
            case "History":
                ActionPanel.Children.Add(new TextBlock { Text = "Recent local operations", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)), Margin = new Thickness(2, 5, 0, 8) });
                AddLocalAction("Refresh history", LoadHistoryAsync);
                break;
            case "Settings":
                ActionPanel.Children.Add(new TextBlock
                {
                    Text = $"Version: 0.2.0\nRuntime: .NET 10 WPF, self-contained x64\nPowerShell: {_engine.EngineVersion}\nElevated: {_isAdministrator}\nDatabase: %LOCALAPPDATA%\\WinFixPro\\0.2\\History\\WinFixPro.db\nModules: embedded in application and integrity-checked at launch\nTelemetry: disabled",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)),
                    Margin = new Thickness(2, 4, 2, 12)
                });
                ActionPanel.Children.Add(new TextBlock
                {
                    Text = "Diagnostic timeout (minutes)",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                    FontSize = 10,
                    Margin = new Thickness(2, 5, 0, 4)
                });
                var timeout = new ComboBox { SelectedIndex = 1, Margin = new Thickness(0, 0, 0, 8) };
                foreach (var value in new[] { "5", "20", "30", "60", "120" }) timeout.Items.Add(value);
                timeout.SelectionChanged += async (_, _) =>
                {
                    if (timeout.SelectedItem is string selected)
                    {
                        try
                        {
                            await _history.SetSettingAsync("OperationTimeoutMinutes", selected);
                            StatusText.Text = $"Diagnostic timeout set to {selected} minutes";
                        }
                        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                        {
                            StatusText.Text = "Could not save diagnostic timeout";
                            MessageBox.Show(ex.Message, "Settings could not be saved", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                };
                timeout.Loaded += async (_, _) =>
                {
                    try
                    {
                        var saved = await _history.GetSettingAsync("OperationTimeoutMinutes");
                        if (saved is not null && timeout.Items.Contains(saved))
                            timeout.SelectedItem = saved;
                    }
                    catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                    {
                        StatusText.Text = "Could not load diagnostic timeout setting";
                        MessageBox.Show(ex.Message, "Settings could not be loaded", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };
                ActionPanel.Children.Add(timeout);
                AddLocalAction("Open data folder", () =>
                {
                    OpenDataFolder();
                    return Task.CompletedTask;
                });
                break;
        }
    }

    private PowerShellPayload NewSymptomsPayload() => new()
    {
        Symptoms = InputText.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(20).ToArray()
    };

    private PowerShellPayload NewKnowledgePayload() => new() { KnowledgeQuery = InputText.Text.Trim() };

    private void AddRepair(string repairId)
    {
        if (!OperationCatalog.TryGet($"Repair:{repairId}", out var definition)) return;
        var button = new Button
        {
            Content = definition.Title,
            Tag = repairId,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(146, 64, 14)),
            ToolTip = definition.Description
        };
        button.Click += async (_, _) => await ConfirmAndRunRepairAsync(repairId);
        ActionPanel.Children.Add(button);
    }

    private void AddCaseActions()
    {
        TextBox AddCaseField(string label, int minHeight)
        {
            ActionPanel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                FontSize = 10,
                Margin = new Thickness(0, 6, 0, 0)
            });
            var input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = minHeight,
                Margin = new Thickness(0, 3, 0, 5),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 17, 32)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 52, 73)),
                ToolTip = "Do not enter passwords, tokens, personal identifiers or confidential data."
            };
            ActionPanel.Children.Add(input);
            return input;
        }

        var outcome = AddCaseField("Case outcome / current status", 38);
        var evidence = AddCaseField("Evidence collected (optional)", 38);
        var hypotheses = AddCaseField("Hypotheses considered (optional)", 38);
        var tests = AddCaseField("Tests and verification results (required for resolved)", 42);
        var sources = AddCaseField("Sources (optional, one HTTPS URL per line)", 38);
        var status = new ComboBox { Tag = "CaseStatus", Margin = new Thickness(0, 0, 0, 5), SelectedIndex = 0 };
        foreach (var item in new[] { "Open", "Investigating", "Resolved", "Inconclusive" }) status.Items.Add(item);
        ActionPanel.Children.Add(status);
        AddLocalAction("Record local case", async () =>
        {
            if (!OperationCatalog.TryGet("SaveCase", out _))
            {
                ResultText.Text = "Case recording is not available.";
                return;
            }
            var payload = new PowerShellPayload
            {
                Symptoms = NewSymptomsPayload().Symptoms,
                CaseOutcome = outcome.Text,
                CaseEvidence = evidence.Text,
                CaseHypotheses = hypotheses.Text,
                CaseTests = tests.Text,
                CaseSources = sources.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                CaseStatus = status.SelectedItem?.ToString() ?? "Open"
            };
            await RunOperationAsync("SaveCase", payload);
        });
    }

    private void AddKnowledgeImport()
    {
        AddLocalAction("Import knowledge bundle for review", async () =>
        {
            var dialog = new OpenFileDialog
            {
                Filter = "WinFix knowledge bundle (*.json)|*.json",
                Multiselect = false,
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            await RunOperationAsync("ImportKnowledge", new PowerShellPayload { KnowledgeBundlePath = dialog.FileName });
        });
    }

    private void AddKnowledgeReview()
    {
        var entryId = new TextBox
        {
            ToolTip = "Entry ID from the imported bundle.",
            Margin = new Thickness(0, 3, 0, 5),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 17, 32)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 52, 73))
        };
        ActionPanel.Children.Add(entryId);
        var reviewNotes = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 42,
            ToolTip = "Record the validation performed before approval.",
            Margin = new Thickness(0, 3, 0, 5),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 17, 32)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 52, 73))
        };
        ActionPanel.Children.Add(reviewNotes);
        AddLocalAction("Approve entry after review", async () =>
        {
            if (string.IsNullOrWhiteSpace(entryId.Text) || string.IsNullOrWhiteSpace(reviewNotes.Text))
            {
                MessageBox.Show("Enter the knowledge entry ID and review notes.", "Knowledge review",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await RunOperationAsync("ReviewKnowledge", new PowerShellPayload
            {
                KnowledgeEntryId = entryId.Text.Trim(),
                ReviewNotes = reviewNotes.Text.Trim()
            });
        });
    }

    private void AddReportButtons()
    {
        foreach (var format in new[] { "HTML", "JSON", "CSV", "TXT" })
        {
            var button = new Button { Content = $"Export {format}", Tag = format };
            button.Click += async (_, _) => await ExportReportAsync(format);
            ActionPanel.Children.Add(button);
        }
        AddLocalAction("Load latest history result", LoadLatestAsync);
    }

    private void AddLocalAction(string caption, Func<Task> action)
    {
        var button = new Button { Content = caption, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => await action();
        ActionPanel.Children.Add(button);
    }

    private async Task ConfirmAndRunRepairAsync(string repairId)
    {
        var definition = OperationCatalog.Operations[$"Repair:{repairId}"];
        var diagnosticOperations = new HashSet<string>(StringComparer.Ordinal)
        {
            "Diagnostics", "Universal", "CommonIssues", "Unknown", "Hardware", "Performance",
            "Network", "WindowsUpdate", "Security", "Services", "Drivers"
        };
        if (_lastResult is null || _lastResult.Status != "Completed" || !diagnosticOperations.Contains(_lastResult.Operation))
        {
            MessageBox.Show("Run and review a diagnostic before selecting a repair. WinFix Pro will not apply a repair based on an unverified assumption.",
                "Diagnostic evidence required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var risk = repairId switch { "DismRestore" => "Moderate", _ => "Low" };
        var validation = repairId switch
        {
            "SfcScan" => "O código de saída não prova que todos os ficheiros foram reparados; reveja CBS.log e execute novo diagnóstico.",
            "DismRestore" => "O código de saída não prova a reparação; reveja DISM.log e execute novo diagnóstico.",
            _ => "O teste DNS posterior pode ser afetado por políticas de rede."
        };
        var confirm = MessageBox.Show(
            $"Problem: {(InputText.Text.Trim() is { Length: > 0 } symptom ? symptom : definition.Title)}\n\n" +
            $"Proposed solution: {definition.Description}\nChanges: {definition.Description}\nRisk: {risk}\n" +
            $"Administrator required: Yes\nBackup: Not applicable; operation uses the Windows maintenance/cache mechanism.\n" +
            $"Reboot requirement: No automatic reboot.\nValidation: {validation}\n\n" +
            "This repair will execute for real. Continue?",
            $"Confirm {definition.Title}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        if (!_isAdministrator)
        {
            MessageBox.Show("The application is running without administrator privileges. Restart WinFix Pro and accept UAC to run repairs.",
                "Administrator required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await RunOperationAsync($"Repair:{repairId}", new PowerShellPayload { RepairId = repairId });
    }

    private async Task RunOperationAsync(string operation, PowerShellPayload? payload = null)
    {
        if (_operationRunning) return;
        if (!OperationCatalog.TryGet(operation, out var definition))
        {
            MessageBox.Show("The requested operation is not allow-listed.", "WinFix Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (definition.IsRepair && !_isAdministrator)
        {
            MessageBox.Show("This repair requires administrator privileges.", "Administrator required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _operationRunning = true;
        _operationCancellation = new CancellationTokenSource();
        OperationProgress.Visibility = Visibility.Visible;
        OperationProgress.Value = 0;
        CancelButton.Visibility = definition.IsRepair ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = $"Starting {definition.Title}...";
        ResultText.Clear();
        var progress = new Progress<(int Percent, string Message)>(value =>
        {
            OperationProgress.Value = value.Percent;
            StatusText.Text = $"{definition.Title}: {value.Message}";
            ResultText.AppendText($"[{value.Percent,3}%] {value.Message}\n");
            ResultText.ScrollToEnd();
        });
        try
        {
            var configuredTimeout = await _history.GetSettingAsync("OperationTimeoutMinutes");
            var timeoutMinutes = int.TryParse(configuredTimeout, out var savedTimeout) ? Math.Clamp(savedTimeout, 5, 120) : 20;
            var timeout = definition.IsRepair ? TimeSpan.FromHours(4) : TimeSpan.FromMinutes(timeoutMinutes);
            var result = await _engine.ExecuteAsync(operation, payload, progress, _operationCancellation.Token, timeout);
            _lastResult = result;
            ResultText.Text = FormatResult(result);
            StatusText.Text = $"{definition.Title} completed";
            UpdateDashboardMetrics(result.Json);
            try
            {
                var id = await _history.AddAsync(result);
                await WriteAuditLogAsync(result, id);
            }
            catch (Exception persistenceError) when (persistenceError is SqliteException or IOException or UnauthorizedAccessException)
            {
                StatusText.Text = $"{definition.Title} completed; local history could not be fully saved";
                ResultText.AppendText($"\nWARNING: The operation completed, but local history/audit storage failed: {persistenceError.Message}");
                MessageBox.Show($"The operation completed successfully, but its local history could not be fully saved.\n\n{persistenceError.Message}",
                    "History storage warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation cancelled";
            ResultText.AppendText("\nOperation was cancelled. No success is reported.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{definition.Title} failed";
            ResultText.AppendText($"\nERROR: {ex.Message}");
            if (!await WriteErrorLogAsync(operation, ex))
                ResultText.AppendText("\nWARNING: The error log could not be written.");
            MessageBox.Show(ex.Message, $"{definition.Title} failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationRunning = false;
            _operationCancellation.Dispose();
            _operationCancellation = null;
            OperationProgress.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateDashboardMetrics(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var summary = root.TryGetProperty("Summary", out var direct) ? direct :
                root.TryGetProperty("Context", out var context) && context.TryGetProperty("Summary", out var nested) ? nested :
                root.TryGetProperty("PostRepairSummary", out var post) ? post : default;
            if (summary.ValueKind != JsonValueKind.Object) return;
            if (summary.TryGetProperty("CpuLoadPercent", out var cpu)) CpuText.Text = $"{cpu}%";
            if (summary.TryGetProperty("MemoryUsedPercent", out var memory)) MemoryText.Text = $"{memory}%";
            if (summary.TryGetProperty("DiskFreeGB", out var disk)) DiskText.Text = $"{disk} GB";
            if (root.TryGetProperty("Health", out var health) && health.TryGetProperty("Score", out var score))
                HealthText.Text = score.ToString();
        }
        catch (JsonException ex)
        {
            StatusText.Text = "Operation completed, but dashboard metrics could not be parsed.";
            ResultText.AppendText($"\nWARNING: Dashboard metrics were unavailable: {ex.Message}");
        }
    }

    private static string FormatResult(OperationResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.Summary);
        builder.AppendLine($"Operation: {result.Operation}");
        builder.AppendLine($"Status: {result.Status}");
        builder.AppendLine($"Started: {result.StartedAt:O}");
        builder.AppendLine($"Finished: {result.FinishedAt:O}");
        builder.AppendLine(new string('-', 72));
        builder.Append(result.Json);
        return builder.ToString();
    }

    private async Task ExportReportAsync(string format)
    {
        var result = _lastResult;
        if (result is null)
        {
            var records = await _history.GetRecentAsync(1);
            if (records.Count == 0)
            {
                MessageBox.Show("Run a diagnostic first or load an operation from History.", "Reports", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var item = records[0];
            result = new OperationResult(item.Operation, item.Status, item.Json, item.Summary, item.CreatedAt, item.CreatedAt);
        }
        var extension = format.ToLowerInvariant();
        var dialog = new SaveFileDialog
        {
            FileName = $"WinFix-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
            Filter = $"{format} report (*.{extension})|*.{extension}",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await ReportFormatter.WriteAsync(dialog.FileName, result, format);
        StatusText.Text = $"Report saved: {dialog.FileName}";
        ResultText.AppendText($"\nReport exported: {dialog.FileName}");
    }

    private async Task LoadHistoryAsync()
    {
        var records = await _history.GetRecentAsync();
        ResultText.Text = records.Count == 0
            ? "No local operation history is recorded."
            : string.Join(Environment.NewLine, records.Select(r => $"{r.Id} | {r.CreatedAt:O} | {r.Operation} | {r.Status} | {r.Summary}"));
        StatusText.Text = $"{records.Count} history entries";
    }

    private async Task LoadLatestAsync()
    {
        var records = await _history.GetRecentAsync(1);
        if (records.Count == 0)
        {
            ResultText.Text = "No local operation history is recorded.";
            return;
        }
        var item = records[0];
        _lastResult = new OperationResult(item.Operation, item.Status, item.Json, item.Summary, item.CreatedAt, item.CreatedAt);
        ResultText.Text = FormatResult(_lastResult);
        StatusText.Text = "Loaded latest operation";
    }

    private void CancelCurrentOperation()
    {
        if (_operationCancellation is null) return;
        StatusText.Text = "Cancelling operation...";
        _operationCancellation.Cancel();
    }

    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _dataRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open data folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task WriteAuditLogAsync(OperationResult result, long id)
    {
        var path = Path.Combine(_dataRoot, "Logs", "WinFixPro.audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(new
        {
            id,
            result.Operation,
            result.Status,
            result.StartedAt,
            result.FinishedAt
        });
        await File.AppendAllTextAsync(path, line + Environment.NewLine, new UTF8Encoding(false));
    }

    private async Task<bool> WriteErrorLogAsync(string operation, Exception exception)
    {
        try
        {
            var path = Path.Combine(_dataRoot, "Logs", "WinFixPro.errors.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var line = JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Operation = operation, Error = exception.Message });
            await File.AppendAllTextAsync(path, line + Environment.NewLine, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
