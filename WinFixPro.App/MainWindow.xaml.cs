using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace WinFixPro;

public partial class MainWindow : Window
{
    private const string AllProblemsSection = "All Problems";

    private static readonly string[] Sections =
    [
        "Dashboard", "All Problems", "Universal Diagnostics", "Hardware Diagnostics", "Performance Analyzer",
        "Network Diagnostics", "Windows Health", "Windows Update", "Security", "Services",
        "Drivers", "Common Issues", "Knowledge Center", "Unknown Problem Analysis",
        "Repair Center", "Reports", "History", "Settings"
    ];

    private static readonly IReadOnlyDictionary<string, string> SectionDisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Dashboard"] = "Dashboard",
            ["All Problems"] = "Todos os problemas",
            ["Universal Diagnostics"] = "Diagnóstico",
            ["Hardware Diagnostics"] = "Hardware",
            ["Performance Analyzer"] = "Desempenho",
            ["Network Diagnostics"] = "Rede",
            ["Windows Health"] = "Windows",
            ["Windows Update"] = "Atualizações",
            ["Security"] = "Segurança",
            ["Services"] = "Serviços",
            ["Drivers"] = "Drivers",
            ["Common Issues"] = "Problemas comuns",
            ["Knowledge Center"] = "Centro de conhecimento",
            ["Unknown Problem Analysis"] = "Problema desconhecido",
            ["Repair Center"] = "Centro de reparação",
            ["Reports"] = "Relatórios",
            ["History"] = "Histórico",
            ["Settings"] = "Definições"
        };

    private readonly bool _isAdministrator;
    private readonly string _dataRoot;
    private readonly PowerShellEngine _engine;
    private readonly HistoryStore _history;
    private CancellationTokenSource? _operationCancellation;
    private OperationResult? _lastResult;
    private OperationResult? _lastDiagnosticResult;
    private OperationResult? _dashboardResult;
    private string? _dashboardFailure;
    private string _currentSection = "Dashboard";
    private bool _operationRunning;
    private bool _isLightTheme;

    public MainWindow(bool isAdministrator, bool elevationCancelled)
    {
        InitializeComponent();
        _isAdministrator = isAdministrator;
        _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinFixPro", "0.2");
        Directory.CreateDirectory(_dataRoot);
        _engine = new PowerShellEngine(_dataRoot);
        _history = new HistoryStore(Path.Combine(_dataRoot, "History", "WinFixPro.db"));

        AllProblemsSeverityFilter.ItemsSource = new[] { "Todos", "Critical", "Error", "Warning", "Healthy", "Unknown" };
        AllProblemsSeverityFilter.SelectedIndex = 0;
        AllProblemsResolutionFilter.ItemsSource = new[] { "Todos", "Pendente", "Reparo disponível", "Sem ação" };
        AllProblemsResolutionFilter.SelectedIndex = 0;

        ComputerNameText.Text = Environment.MachineName;
        PrivilegeText.Text = isAdministrator ? "Administrator" : "Standard user - limited repairs";
        PrivilegeText.Foreground = isAdministrator ? StatusBrush("Healthy") : StatusBrush("Warning");
        EngineText.Text = $"Internal engine: {_engine.EngineVersion}\nData: %LOCALAPPDATA%\\WinFixPro\\0.2";
        FooterText.Text = elevationCancelled
            ? "UAC declined: diagnostics remain available; administrative repairs are disabled."
            : "Local diagnostics | No telemetry";

        BuildNavigation();
        Navigate("Dashboard");
        CancelButton.Click += (_, _) => CancelCurrentOperation();
        FullScanButton.Click += async (_, _) => await RunDashboardScanAsync("Universal");
        QuickScanButton.Click += async (_, _) => await RunDashboardScanAsync("Diagnostics");
        AdvancedScanButton.Click += async (_, _) => await RunDashboardScanAsync("Universal", NewDashboardSymptomsPayload());
        AllProblemsButton.Click += (_, _) => Navigate(AllProblemsSection);
        ViewProblemsButton.Click += (_, _) => Navigate(AllProblemsSection);
        DashboardReportButton.Click += async (_, _) => await ExportReportAsync("HTML");
        Loaded += async (_, _) => await InitializeDashboardAsync();
        Closing += (_, _) => _operationCancellation?.Cancel();
    }

    private void BuildNavigation()
    {
        foreach (var section in Sections)
        {
            var displayName = SectionDisplayNames.TryGetValue(section, out var label) ? label : section;
            var button = new Button
            {
                Content = displayName,
                Tag = section,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 8, 8),
                Margin = new Thickness(0, 2, 0, 2),
                Background = ThemeBrush("PanelBrush"),
                ToolTip = section,
                BorderBrush = ThemeBrush("BorderBrush"),
                BorderThickness = new Thickness(1)
            };
            AutomationProperties.SetName(button, displayName);
            button.Click += (_, _) => Navigate((string)button.Tag);
            NavigationItems.Children.Add(button);
        }
    }

    private void Navigate(string section)
    {
        if (_operationRunning) return;
        _currentSection = section;
        foreach (var item in NavigationItems.Children.OfType<Button>())
        {
            var selected = string.Equals(item.Tag as string, section, StringComparison.Ordinal);
            item.Background = ThemeBrush(selected ? "AccentBrush" : "PanelBrush");
            item.Foreground = selected ? Brushes.White : ThemeBrush("PrimaryTextBrush");
            item.BorderBrush = selected ? ThemeBrush("AccentBrush") : ThemeBrush("BorderBrush");
        }
        PageTitle.Text = SectionDisplayNames.TryGetValue(section, out var title) ? title : section;
        PageDescription.Text = section switch
        {
            "All Problems" => "Lista consolidada dos problemas reais encontrados pelo diagnóstico local.",
            "Universal Diagnostics" => "Recolha local e análise baseada em evidências; áreas desconhecidas permanecem inconclusivas.",
            "Unknown Problem Analysis" => "Investiga sintomas com testes não destrutivos e sem assumir uma causa conhecida.",
            "Knowledge Center" => "Consulta conhecimento local aprovado e compatível com esta versão do Windows.",
            "Repair Center" => "Reparações confirmadas são executadas só depois da validação do técnico.",
            "History" => "Consulta o histórico local das últimas análises e operações realizadas.",
            "Settings" => "Configurações, registos locais e detalhes do motor de diagnóstico.",
            _ => "Diagnóstico e reparação do Windows em linguagem simples."
        };
        ActionPanel.Children.Clear();
        DashboardScroll.Visibility = section == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        DetailScroll.Visibility = section == "Dashboard" ? Visibility.Collapsed : Visibility.Visible;
        GenericDetailContent.Visibility = section == AllProblemsSection ? Visibility.Collapsed : Visibility.Visible;
        ActionSection.Visibility = section == AllProblemsSection ? Visibility.Collapsed : Visibility.Visible;
        AllProblemsPage.Visibility = section == AllProblemsSection ? Visibility.Visible : Visibility.Collapsed;
        if (section != "Dashboard" && section != AllProblemsSection)
            AddActions(section);
        RenderDashboard();
        if (section == AllProblemsSection)
            RenderAllProblemsPage();
    }

    private async Task InitializeDashboardAsync()
    {
        try
        {
            var records = await _history.GetRecentAsync(200);
            var latest = records.FirstOrDefault(record =>
                record.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
                record.Operation is "Diagnostics" or "Universal");
            if (latest is not null)
            {
                _lastDiagnosticResult = new OperationResult(
                    latest.Operation, latest.Status, latest.Json, latest.Summary, latest.CreatedAt, latest.CreatedAt);
                _dashboardResult = _lastDiagnosticResult;
                RenderDashboard();
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _dashboardFailure = $"Local diagnostic history could not be loaded: {ex.Message}";
            RenderDashboard();
        }

        try
        {
            var savedTheme = await _history.GetSettingAsync("Theme");
            if (savedTheme is "Light" or "Dark")
            {
                _isLightTheme = savedTheme == "Light";
                ApplyTheme();
                Navigate(_currentSection);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            FooterText.Text = $"Theme preference could not be loaded: {ex.Message}";
        }

        await RunDashboardScanAsync("Diagnostics");
    }

    private PowerShellPayload NewDashboardSymptomsPayload() => new()
    {
        Symptoms = DashboardSymptomsText.Text.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20).ToArray()
    };

    private async Task RunDashboardScanAsync(string operation, PowerShellPayload? payload = null)
    {
        _dashboardFailure = null;
        StatusText.Text = $"Running {OperationCatalog.Operations[operation].Title}...";
        await RunOperationAsync(operation, payload ?? new PowerShellPayload());
    }

    public sealed record AllProblemsEntry(string Title, string Severity, string Area, string Description, DateTimeOffset? DetectedAt, string ResolutionState, string? RepairId);

    public sealed record AllProblemsPageState(string Message, bool HasDiagnostic, bool HasProblems, bool HasError, IReadOnlyList<AllProblemsEntry> Problems, IReadOnlyList<int> SeverityCounts);

    public static AllProblemsPageState GetAllProblemsPageState(OperationResult? result, string? failure, string searchText, string severityFilter, string resolutionFilter)
    {
        if (!string.IsNullOrWhiteSpace(failure))
            return new AllProblemsPageState($"Erro ao carregar os problemas: {failure}", false, false, true, [], [0, 0, 0, 0, 0]);

        if (result is null || result.Status != "Completed")
            return new AllProblemsPageState("Ainda não existem diagnósticos. Execute uma análise do computador para identificar possíveis problemas.", false, false, false, [], [0, 0, 0, 0, 0]);

        var snapshot = DashboardHealthAnalyzer.Analyze(result, failure);
        var entries = snapshot.Issues
            .Select(issue => new AllProblemsEntry(
                issue.Title,
                issue.Severity,
                issue.Area,
                issue.Description,
                result.FinishedAt,
                issue.RepairId is not null ? "Reparo disponível" : (issue.Severity.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ? "Sem ação" : "Pendente"),
                issue.RepairId))
            .ToList();

        var filtered = entries
            .Where(entry =>
            {
                var matchesSearch = string.IsNullOrWhiteSpace(searchText) ||
                    entry.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    entry.Area.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    entry.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                var matchesSeverity = severityFilter == "Todos" || string.Equals(entry.Severity, severityFilter, StringComparison.OrdinalIgnoreCase);
                var matchesResolution = resolutionFilter == "Todos" || string.Equals(entry.ResolutionState, resolutionFilter, StringComparison.OrdinalIgnoreCase);
                return matchesSearch && matchesSeverity && matchesResolution;
            })
            .OrderByDescending(entry => SeveritySortPriority(entry.Severity))
            .ToList();

        var severityCounts = new[]
        {
            filtered.Count(item => string.Equals(item.Severity, "Critical", StringComparison.OrdinalIgnoreCase)),
            filtered.Count(item => string.Equals(item.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
            filtered.Count(item => string.Equals(item.Severity, "Warning", StringComparison.OrdinalIgnoreCase)),
            filtered.Count(item => string.Equals(item.Severity, "Healthy", StringComparison.OrdinalIgnoreCase)),
            filtered.Count(item => string.Equals(item.Severity, "Unknown", StringComparison.OrdinalIgnoreCase))
        };

        if (filtered.Count == 0)
        {
            var message = entries.Count == 0
                ? "Nenhum problema foi identificado nas análises realizadas."
                : "Nenhum problema corresponde aos filtros atuais. Ajuste a pesquisa ou os filtros para ver resultados.";
            return new AllProblemsPageState(message, true, false, false, filtered, severityCounts);
        }

        return new AllProblemsPageState("Resultados carregados.", true, true, false, filtered, severityCounts);
    }

    private void AllProblemsSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderAllProblemsPage();

    private void AllProblemsSeverityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderAllProblemsPage();

    private void AllProblemsResolutionFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderAllProblemsPage();

    private async void AllProblemsRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunDashboardScanAsync("Universal");
    }

    private void RenderAllProblemsPage()
    {
        if (AllProblemsList is null || AllProblemsSearchBox is null || AllProblemsSeverityFilter is null || AllProblemsResolutionFilter is null)
            return;

        var severityFilter = (AllProblemsSeverityFilter.SelectedItem as string) ?? "Todos";
        var resolutionFilter = (AllProblemsResolutionFilter.SelectedItem as string) ?? "Todos";
        var state = GetAllProblemsPageState(_dashboardResult, _dashboardFailure, AllProblemsSearchBox.Text.Trim(), severityFilter, resolutionFilter);

        AllProblemsTotalText.Text = state.Problems.Count == 0 ? "0 problemas" : $"{state.Problems.Count} problemas";
        AllProblemsSummary.Children.Clear();

        foreach (var severity in new[] { "Critical", "Error", "Warning", "Healthy", "Unknown" })
        {
            var count = state.SeverityCounts[(GetSeverityIndex(severity))];
            var border = new Border
            {
                Background = ThemeBrush("CardBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 8, 0),
                BorderBrush = StatusBrush(severity),
                BorderThickness = new Thickness(1)
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = severity, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = StatusBrush(severity) });
            panel.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 20, FontWeight = FontWeights.Bold, Foreground = ThemeBrush("PrimaryTextBrush") });
            border.Child = panel;
            AllProblemsSummary.Children.Add(border);
        }

        if (!state.HasDiagnostic || state.HasError)
        {
            AllProblemsList.Items.Clear();
            AllProblemsEmptyState.Visibility = Visibility.Visible;
            AllProblemsEmptyText.Text = state.Message;
            AllProblemsList.Visibility = Visibility.Collapsed;
            return;
        }

        if (!state.HasProblems)
        {
            AllProblemsList.Items.Clear();
            AllProblemsEmptyState.Visibility = Visibility.Visible;
            AllProblemsEmptyText.Text = state.Message;
            AllProblemsList.Visibility = Visibility.Collapsed;
            return;
        }

        AllProblemsEmptyState.Visibility = Visibility.Collapsed;
        AllProblemsList.Visibility = Visibility.Visible;
        AllProblemsList.Items.Clear();
        foreach (var entry in state.Problems)
        {
            var item = new Border
            {
                Background = ThemeBrush("CardBrush"),
                BorderBrush = StatusBrush(entry.Severity),
                BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
                Tag = entry
            };

            var stack = new StackPanel();
            var header = new DockPanel();
            var severityText = new TextBlock
            {
                Text = entry.Severity.ToUpperInvariant(),
                Foreground = StatusBrush(entry.Severity),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(severityText, Dock.Right);
            header.Children.Add(severityText);
            header.Children.Add(new TextBlock
            {
                Text = entry.Title,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0)
            });
            stack.Children.Add(header);
            stack.Children.Add(new TextBlock
            {
                Text = $"Componente: {entry.Area}",
                Foreground = ThemeBrush("SecondaryTextBrush"),
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"Última deteção: {entry.DetectedAt:dd/MM/yyyy HH:mm}",
                Foreground = ThemeBrush("SecondaryTextBrush"),
                FontSize = 9,
                Margin = new Thickness(0, 6, 0, 0)
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"Estado: {entry.ResolutionState}",
                Foreground = ThemeBrush("SecondaryTextBrush"),
                FontSize = 9,
                Margin = new Thickness(0, 2, 0, 0)
            });

            var button = new Button
            {
                Content = "Ver detalhes",
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            button.Click += (_, _) => ShowDetails(entry.Title,
                $"Gravidade: {entry.Severity}{Environment.NewLine}Componente: {entry.Area}{Environment.NewLine}Descrição: {entry.Description}{Environment.NewLine}{Environment.NewLine}" +
                $"Última deteção: {entry.DetectedAt:dd/MM/yyyy HH:mm}{Environment.NewLine}{Environment.NewLine}" +
                $"Estado: {entry.ResolutionState}{Environment.NewLine}{Environment.NewLine}" +
                $"Possível causa: {ResolvePossibleCause(entry)}{Environment.NewLine}{Environment.NewLine}Recomendação: {ResolveRecommendation(entry)}");
            stack.Children.Add(button);

            item.Child = stack;
            AllProblemsList.Items.Add(item);
        }
    }

    private static string ResolvePossibleCause(AllProblemsEntry entry)
    {
        return entry.Severity switch
        {
            "Critical" => "Existe evidência forte de falha funcional ou de risco operacional relevante.",
            "Error" => "O componente está a reportar erro ou configuração não válida.",
            "Warning" => "O componente está funcional, mas com sinais de atenção ou degradação.",
            "Healthy" => "Nenhuma ação necessária com base na evidência atual.",
            _ => "A causa exata ainda não foi confirmada com dados suficientes."
        };
    }

    private static string ResolveRecommendation(AllProblemsEntry entry)
    {
        return entry.Severity switch
        {
            "Critical" => "Agende a investigação imediata e confirme as reparações recomendadas antes de continuar.",
            "Error" => "Revise o componente afetado e execute a correção recomendada no diagnóstico.",
            "Warning" => "Acompanhe o comportamento e confirme se a correção recomendada resolve a evolução do problema.",
            "Healthy" => "Manter a monitorização e confirmar que o estado continua estável.",
            _ => "Recolha mais evidência ou execute um diagnóstico complementar para reduzir a incerteza."
        };
    }

    private static int SeveritySortPriority(string severity) => severity switch
    {
        "Critical" => 5,
        "Error" => 4,
        "Warning" => 3,
        "Healthy" => 2,
        "Unknown" => 1,
        _ => 0
    };

    private static int GetSeverityIndex(string severity) => severity switch
    {
        "Critical" => 0,
        "Error" => 1,
        "Warning" => 2,
        "Healthy" => 3,
        "Unknown" => 4,
        _ => 4
    };

    private void RenderDashboard()
    {
        if (!IsLoaded && DashboardStatusText is null) return;
        var snapshot = DashboardHealthAnalyzer.Analyze(_dashboardResult, _dashboardFailure);
        DashboardStatusText.Text = snapshot.OverallStatus;
        DashboardStatusText.Foreground = StatusBrush(snapshot.OverallStatus);
        DashboardStatusMessage.Text = snapshot.StatusMessage;
        DashboardLastDiagnosticText.Text = snapshot.LastDiagnostic is { } date
            ? $"Last diagnostic: {date.ToLocalTime():g}"
            : "Last diagnostic: Never";
        DashboardScoreText.Text = snapshot.Score?.ToString() ?? "--";
        DashboardScoreText.Foreground = snapshot.Score is null
            ? StatusBrush("Unknown")
            : snapshot.Score < 50 ? StatusBrush("Critical") : snapshot.Score < 75 ? StatusBrush("Warning") : StatusBrush("Healthy");
        DashboardScoreLabel.Text = snapshot.Score switch
        {
            null => "Not available",
            >= 90 => "Excellent",
            >= 75 => "Good",
            >= 50 => "Needs attention",
            _ => "Critical"
        };
        CriticalCountText.Text = snapshot.CriticalCount.ToString();
        ErrorCountText.Text = snapshot.ErrorCount.ToString();
        WarningCountText.Text = $"{snapshot.WarningCount} warnings";
        PendingRepairsText.Text = $"{snapshot.PendingRepairs} pending repairs";
        DashboardFailureBanner.Visibility = string.IsNullOrWhiteSpace(_dashboardFailure)
            ? Visibility.Collapsed
            : Visibility.Visible;
        DashboardFailureText.Text = _dashboardFailure ?? "";

        ComponentPanel.Children.Clear();
        foreach (var component in snapshot.Components)
            ComponentPanel.Children.Add(BuildComponentCard(component));

        ProblemsPanel.Children.Clear();
        foreach (var issue in snapshot.Issues)
            ProblemsPanel.Children.Add(BuildIssueCard(issue));
        ProblemCountLabel.Text = $"{snapshot.Issues.Count} item(s)";
        NoProblemsCard.Visibility = snapshot.Issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoProblemsText.Text = _dashboardResult is null || _dashboardResult.Status != "Completed"
            ? "Run a scan to assess system health. Untested and unavailable components remain Unknown."
            : snapshot.Components.Any(component => component.Status is "Unknown" or "Not Tested")
                ? "No actionable problems were detected in the checks that ran. Untested or unavailable components are not classified as healthy."
                : "No problems were detected in the completed checks.";

        ScoreFactorsText.Text = snapshot.ScoreFactors.Count == 0
            ? "No measured issue affected the score. Unavailable and untested areas are excluded; the score is not a claim that every component is healthy."
            : $"Score uses observed findings only (Low −5, Medium −12, High −22, Critical −35; minimum 0). " +
              $"Unavailable checks are excluded.{Environment.NewLine}{string.Join(Environment.NewLine, snapshot.ScoreFactors)}";
    }

    private Border BuildComponentCard(ComponentHealth component)
    {
        var border = new Border
        {
            Width = 280,
            MaxWidth = 380,
            Background = ThemeBrush("CardBrush"),
            BorderBrush = StatusBrush(component.Status),
            BorderThickness = new Thickness(1, 1, 1, 3),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 8, 8)
        };
        var panel = new StackPanel();
        var titleLine = new DockPanel();
        var status = new TextBlock
        {
            Text = component.Status,
            Foreground = StatusBrush(component.Status),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(status, Dock.Right);
        titleLine.Children.Add(status);
        titleLine.Children.Add(new TextBlock
        {
            Text = component.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        });
        panel.Children.Add(titleLine);
        panel.Children.Add(new TextBlock
        {
            Text = component.Result,
            Foreground = ThemeBrush("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Margin = new Thickness(0, 5, 0, 3)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Last checked: {component.LastChecked}",
            Foreground = ThemeBrush("SecondaryTextBrush"),
            FontSize = 9
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Recommended: {component.Recommendation}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 9,
            Margin = new Thickness(0, 5, 0, 4)
        });
        var details = new Button { Content = "Ver detalhes", Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left };
        details.Click += (_, _) => ShowDetails(component.Name, component.Details + Environment.NewLine + Environment.NewLine +
            $"Recomendação: {component.Recommendation}");
        panel.Children.Add(details);
        border.Child = panel;
        return border;
    }

    private Border BuildIssueCard(DashboardIssue issue)
    {
        var border = new Border
        {
            Background = ThemeBrush("PanelBrush"),
            BorderBrush = StatusBrush(issue.Severity),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var content = new StackPanel();
        var heading = new DockPanel();
        var severity = new TextBlock
        {
            Text = issue.Severity.ToUpperInvariant(),
            Foreground = StatusBrush(issue.Severity),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(severity, Dock.Right);
        heading.Children.Add(severity);
        heading.Children.Add(new TextBlock { Text = issue.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(heading);
        content.Children.Add(new TextBlock
        {
            Text = $"{issue.Area}: {issue.Description}",
            Foreground = ThemeBrush("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 2)
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Evidence: {issue.Evidence}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Possible cause: {issue.PossibleCause}",
            Foreground = ThemeBrush("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Recommended: {issue.Recommendation}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 4)
        });
        var actions = new WrapPanel();
        var details = new Button { Content = "Ver detalhes", Padding = new Thickness(8, 4, 8, 4) };
        details.Click += (_, _) => ShowDetails(issue.Title,
            $"Gravidade: {issue.Severity}{Environment.NewLine}Componente: {issue.Area}{Environment.NewLine}" +
            $"Descrição: {issue.Description}{Environment.NewLine}{Environment.NewLine}" +
            $"Evidência: {issue.Evidence}{Environment.NewLine}{Environment.NewLine}" +
            $"Possível causa: {issue.PossibleCause}{Environment.NewLine}{Environment.NewLine}" +
            $"Recomendação: {issue.Recommendation}");
        actions.Children.Add(details);
        var investigate = new Button { Content = "Investigar", Padding = new Thickness(8, 4, 8, 4) };
        investigate.Click += (_, _) => NavigateForArea(issue.Area);
        actions.Children.Add(investigate);
        if (issue.RepairId is not null && _isAdministrator)
        {
            var repair = new Button { Content = $"Reparar: {OperationCatalog.Operations[$"Repair:{issue.RepairId}"].Title}", Padding = new Thickness(8, 4, 8, 4) };
            repair.Click += async (_, _) => await ConfirmAndRunRepairAsync(issue.RepairId);
            actions.Children.Add(repair);
        }
        content.Children.Add(actions);
        border.Child = content;
        return border;
    }

    private void NavigateForArea(string area)
    {
        var section = area.ToLowerInvariant() switch
        {
            "storage" or "system" or "events" or "operatingsystem" => "Windows Health",
            "hardware" or "drivers" => "Hardware Diagnostics",
            "performance" or "behavioral anomaly" => "Performance Analyzer",
            "network" => "Network Diagnostics",
            "security" => "Security",
            "services" => "Services",
            "windows update" => "Windows Update",
            _ => "Universal Diagnostics"
        };
        Navigate(section);
    }

    private void ShowDetails(string title, string details)
    {
        var workArea = SystemParameters.WorkArea;
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            Width = Math.Min(760, workArea.Width - 40),
            Height = Math.Min(580, workArea.Height - 40),
            MinWidth = Math.Min(420, workArea.Width - 40),
            MinHeight = Math.Min(320, workArea.Height - 40),
            Background = ThemeBrush("BackgroundBrush"),
            Foreground = ThemeBrush("PrimaryTextBrush")
        };
        var text = new TextBlock
        {
            Text = details,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14),
            Foreground = ThemeBrush("PrimaryTextBrush")
        };
        dialog.Content = new ScrollViewer
        {
            Content = text,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        dialog.ShowDialog();
    }

    private async Task ToggleThemeAsync()
    {
        _isLightTheme = !_isLightTheme;
        ApplyTheme();
        try
        {
            await _history.SetSettingAsync("Theme", _isLightTheme ? "Light" : "Dark");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"The theme changed, but the preference could not be saved.\n\n{ex.Message}",
                "Theme preference", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Navigate(_currentSection);
    }

    private void ApplyTheme()
    {
        var colors = _isLightTheme
            ? new Dictionary<string, string>
            {
                ["BackgroundBrush"] = "#F1F5F9", ["PanelBrush"] = "#FFFFFF", ["CardBrush"] = "#E2E8F0",
                ["PrimaryTextBrush"] = "#0F172A", ["SecondaryTextBrush"] = "#475569",
                ["InputBackgroundBrush"] = "#FFFFFF", ["BorderBrush"] = "#CBD5E1", ["ActionButtonBrush"] = "#CBD5E1"
            }
            : new Dictionary<string, string>
            {
                ["BackgroundBrush"] = "#0B1120", ["PanelBrush"] = "#111827", ["CardBrush"] = "#172235",
                ["PrimaryTextBrush"] = "#F8FAFC", ["SecondaryTextBrush"] = "#94A3B8",
                ["InputBackgroundBrush"] = "#0B1120", ["BorderBrush"] = "#263449", ["ActionButtonBrush"] = "#263449"
            };
        foreach (var (key, value) in colors)
            ((SolidColorBrush)Application.Current.Resources[key]).Color = (Color)ColorConverter.ConvertFromString(value);
    }

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    private Brush StatusBrush(string status)
    {
        var color = (_isLightTheme, status) switch
        {
            (true, "Healthy") => "#15803D",
            (true, "Warning") => "#A16207",
            (true, "Error") => "#C2410C",
            (true, "Critical") => "#B91C1C",
            (true, "Unknown" or "Not Tested") => "#475569",
            (false, "Healthy") => "#22C55E",
            (false, "Warning") => "#F59E0B",
            (false, "Error") => "#F97316",
            (false, "Critical") => "#EF4444",
            (false, "Unknown" or "Not Tested") => "#94A3B8",
            _ => _isLightTheme ? "#1D4ED8" : "#60A5FA"
        };
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
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
                HorizontalAlignment = HorizontalAlignment.Stretch,
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
                Foreground = ThemeBrush("SecondaryTextBrush"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 12, 0, 5)
            });
        }

        switch (section)
        {
            case "Dashboard":
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
                ActionPanel.Children.Add(new TextBlock { Text = "Recent local operations", Foreground = ThemeBrush("SecondaryTextBrush"), Margin = new Thickness(2, 5, 0, 8) });
                AddLocalAction("Refresh history", LoadHistoryAsync);
                break;
            case "Settings":
                ActionPanel.Children.Add(new TextBlock
                {
                    Text = $"Version: 0.2.0\nRuntime: .NET 10 WPF, self-contained x64\nPowerShell: {_engine.EngineVersion}\nElevated: {_isAdministrator}\nDatabase: %LOCALAPPDATA%\\WinFixPro\\0.2\\History\\WinFixPro.db\nModules: embedded in application and integrity-checked at launch\nTelemetry: disabled",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("PrimaryTextBrush"),
                    Margin = new Thickness(2, 4, 2, 12)
                });
                AddLocalAction(_isLightTheme ? "Switch to dark theme" : "Switch to light theme", ToggleThemeAsync);
                ActionPanel.Children.Add(new TextBlock
                {
                    Text = "Diagnostic timeout (minutes)",
                    Foreground = ThemeBrush("SecondaryTextBrush"),
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
            Background = new SolidColorBrush(Color.FromRgb(146, 64, 14)),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
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
                Foreground = ThemeBrush("SecondaryTextBrush"),
                FontSize = 10,
                Margin = new Thickness(0, 6, 0, 0)
            });
            var input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = minHeight,
                Margin = new Thickness(0, 3, 0, 5),
                Background = ThemeBrush("InputBackgroundBrush"),
                Foreground = ThemeBrush("PrimaryTextBrush"),
                BorderBrush = ThemeBrush("BorderBrush"),
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
            Background = ThemeBrush("InputBackgroundBrush"),
            Foreground = ThemeBrush("PrimaryTextBrush"),
            BorderBrush = ThemeBrush("BorderBrush")
        };
        ActionPanel.Children.Add(entryId);
        var reviewNotes = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 42,
            ToolTip = "Record the validation performed before approval.",
            Margin = new Thickness(0, 3, 0, 5),
            Background = ThemeBrush("InputBackgroundBrush"),
            Foreground = ThemeBrush("PrimaryTextBrush"),
            BorderBrush = ThemeBrush("BorderBrush")
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
        var button = new Button { Content = caption, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
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
            if (operation is "Diagnostics" or "Universal")
            {
                _lastDiagnosticResult = result;
                _dashboardResult = result;
                _dashboardFailure = null;
                RenderDashboard();
            }
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
            if (operation is "Diagnostics" or "Universal")
            {
                _dashboardResult = new OperationResult(operation, "Cancelled", "", "Diagnostic cancelled.", DateTimeOffset.Now, DateTimeOffset.Now);
                _dashboardFailure = "The diagnostic was cancelled before it returned complete evidence.";
                RenderDashboard();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{definition.Title} failed";
            ResultText.AppendText($"\nERROR: {ex.Message}");
            if (!await WriteErrorLogAsync(operation, ex))
                ResultText.AppendText("\nWARNING: The error log could not be written.");
            if (operation is "Diagnostics" or "Universal")
            {
                _dashboardResult = new OperationResult(operation, "Failed", "", ex.Message, DateTimeOffset.Now, DateTimeOffset.Now);
                _dashboardFailure = ex.Message;
                RenderDashboard();
            }
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
        var result = _lastDiagnosticResult ?? _lastResult;
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
