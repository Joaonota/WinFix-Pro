using System.Text.Json;

namespace WinFixPro;

public sealed record DashboardIssue(
    string Title,
    string Severity,
    string Area,
    string Description,
    string Evidence,
    string PossibleCause,
    string Recommendation,
    string? RepairId);

public sealed record ComponentHealth(
    string Name,
    string Status,
    string Result,
    string LastChecked,
    string Details,
    string Recommendation);

public sealed record DashboardHealthSnapshot(
    string OverallStatus,
    string StatusMessage,
    int? Score,
    int CriticalCount,
    int ErrorCount,
    int WarningCount,
    int PendingRepairs,
    DateTimeOffset? LastDiagnostic,
    IReadOnlyList<string> ScoreFactors,
    IReadOnlyList<ComponentHealth> Components,
    IReadOnlyList<DashboardIssue> Issues);

public static class DashboardHealthAnalyzer
{
    private static readonly (string Name, string[] Checks, string Recommendation)[] ComponentDefinitions =
    [
        ("Windows Integrity", ["Full diagnostics"], "Run SFC/DISM diagnostics only when integrity concerns are suspected."),
        ("Hardware", ["Hardware", "Drivers", "Physical disks"], "Review the hardware inventory and device error codes."),
        ("Storage", ["Summary", "Physical disks"], "Review free space and any reported physical-disk health evidence."),
        ("Network", ["Network tests", "Network configuration"], "Review adapter, gateway, DNS and HTTPS test results."),
        ("Windows Update", ["Installed updates"], "Review update services and the installed update inventory; applicability is not assessed."),
        ("Security", ["Security"], "Review Defender, firewall and UAC evidence."),
        ("Drivers", ["Drivers", "Hardware"], "Inspect devices reporting a non-zero configuration error code."),
        ("Services", ["Services", "Full diagnostics"], "Review the essential service findings and service inventory."),
        ("Performance", ["Summary", "Processes", "Startup"], "Review CPU, memory, process and local-baseline evidence.")
    ];

    private static readonly IReadOnlyDictionary<string, int> SeverityWeights =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Low"] = 5,
            ["Medium"] = 12,
            ["High"] = 22,
            ["Critical"] = 35
        };

    public static DashboardHealthSnapshot Analyze(OperationResult? result, string? failure = null)
    {
        var issues = new List<DashboardIssue>();
        var scoredEvidence = new Dictionary<string, (string Severity, string Title)>(StringComparer.OrdinalIgnoreCase);
        var checkedAt = result?.FinishedAt;
        var isCompletedDiagnostic = result is not null &&
            result.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
            result.Operation is "Diagnostics" or "Universal";

        JsonDocument? document = null;
        JsonElement root = default;
        JsonElement context = default;
        JsonElement analysis = default;
        JsonElement summary = default;
        JsonElement findings = default;
        var hasRoot = false;
        if (isCompletedDiagnostic)
        {
            try
            {
                document = JsonDocument.Parse(result!.Json);
                root = document.RootElement;
                hasRoot = true;
                context = Get(root, "Context");
                analysis = Get(root, "Analysis");
                summary = Get(context.ValueKind == JsonValueKind.Object ? context : root, "Summary");
                findings = Get(context.ValueKind == JsonValueKind.Object ? context : root, "Findings");
            }
            catch (JsonException)
            {
                failure = "The diagnostic result could not be parsed; no component is reported as healthy.";
                isCompletedDiagnostic = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(failure))
        {
            issues.Add(new DashboardIssue("Diagnostic failed", "Unknown", result?.Operation ?? "Diagnostics",
                "The diagnostic operation did not complete successfully.", failure,
                "No cause can be inferred from a failed collection.", "Review the error and rerun the diagnostic.", null));
        }

        if (hasRoot)
        {
            AddFindingIssues(findings, Get(analysis, "Hypotheses"), issues, scoredEvidence);
            AddRuleIssues(Get(analysis, "RuleFindings"), issues, scoredEvidence);
            AddAnomalyIssues(Get(analysis, "Anomalies"), issues, scoredEvidence);
            AddCollectionErrors(Get(context, "CollectionErrors"), issues);
            AddInconclusiveHypotheses(Get(analysis, "Hypotheses"), issues);
        }

        var components = BuildComponents(
            hasRoot, root, context, analysis, summary, findings, checkedAt,
            isCompletedDiagnostic, failure);
        var critical = issues.Count(issue => issue.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
        var errors = issues.Count(issue => issue.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        var warnings = issues.Count(issue => issue.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        var pendingRepairs = issues.Count(issue => issue.RepairId is not null);
        var hasScoreEvidence = isCompletedDiagnostic && GetItems(findings).Any(item => !IsUnavailableFinding(item));
        int? score = !hasScoreEvidence
            ? null
            : Math.Clamp(100 - scoredEvidence.Values.Sum(item => SeverityWeights[item.Severity]), 0, 100);
        var scoreFactors = scoredEvidence.Values
            .OrderByDescending(item => SeverityWeights[item.Severity])
            .Select(item => $"{item.Title} ({item.Severity}, -{SeverityWeights[item.Severity]})")
            .ToArray();

        var overall = DetermineOverall(components, issues, isCompletedDiagnostic);
        var message = !isCompletedDiagnostic
            ? string.IsNullOrWhiteSpace(failure)
                ? "No completed diagnostic evidence is available. Run a quick scan to assess this computer."
                : "The latest diagnostic failed. Review the error and rerun a scan; unknown results are not healthy."
            : overall switch
            {
                "Healthy" => "All assessed components reported healthy evidence.",
                "Warning" => "Issues or incomplete coverage need technician review. Unavailable checks are not treated as healthy.",
                "Error" => "One or more assessed components reported a high-severity error.",
                "Critical" => "Critical evidence requires immediate technician attention.",
                _ => "The available evidence is insufficient to determine overall system health."
            };

        return new DashboardHealthSnapshot(
            overall, message, score, critical, errors, warnings, pendingRepairs, checkedAt,
            scoreFactors, components, issues.OrderByDescending(issue => StatusPriority(issue.Severity)).ToArray());
    }

    private static List<ComponentHealth> BuildComponents(
        bool hasRoot,
        JsonElement root,
        JsonElement context,
        JsonElement analysis,
        JsonElement summary,
        JsonElement findings,
        DateTimeOffset? checkedAt,
        bool completed,
        string? failure)
    {
        var result = new List<ComponentHealth>(ComponentDefinitions.Length);
        foreach (var component in ComponentDefinitions)
        {
            var status = "Not Tested";
            var resultText = "No diagnostic evidence collected.";
            var details = resultText;
            var recommendation = component.Recommendation;
            var relevantFindings = GetItems(findings)
                .Where(item => AreaMatches(component.Name, StringValue(Get(item, "Area"))))
                .ToArray();
            var matchedError = CollectionErrorFor(component.Name, Get(context, "CollectionErrors"));
            var tested = false;

            if (component.Name == "Storage" && Get(summary, "DiskFreeGB").ValueKind == JsonValueKind.Number)
            {
                tested = true;
                var free = Get(summary, "DiskFreeGB").GetDouble();
                var total = Get(summary, "DiskSizeGB").ValueKind == JsonValueKind.Number
                    ? $" of {Get(summary, "DiskSizeGB").GetDouble():0.0} GB total"
                    : "";
                resultText = $"{free:0.0} GB free{total}.";
                status = free < 2 ? "Critical" : free < 10 ? "Error" : free < 20 ? "Warning" : "Healthy";
                details = $"System drive {StringValue(Get(summary, "SystemDrive"))}: {resultText}";
                recommendation = status == "Healthy"
                    ? "No low-space condition was measured."
                    : "Review disk usage and free space only after identifying files that are safe to remove.";
            }
            else if (component.Name == "Performance" &&
                     Get(summary, "CpuLoadPercent").ValueKind == JsonValueKind.Number &&
                     Get(summary, "MemoryUsedPercent").ValueKind == JsonValueKind.Number)
            {
                tested = true;
                var cpu = Get(summary, "CpuLoadPercent").GetDouble();
                var memory = Get(summary, "MemoryUsedPercent").GetDouble();
                status = HighestStatus(relevantFindings.Select(item => SeverityStatus(StringValue(Get(item, "Severity")))));
                resultText = $"CPU {cpu:0.0}%; memory {memory:0.0}%.";
                details = $"Point-in-time readings: {resultText}";
                recommendation = status == "Healthy"
                    ? "No configured CPU or memory pressure threshold was exceeded at collection time."
                    : "Inspect process usage and repeat sampling while the reported symptom is occurring.";
            }
            else if (component.Name == "Hardware")
            {
                var hardware = Get(context, "Hardware");
                if (hardware.ValueKind == JsonValueKind.Object)
                {
                    tested = true;
                    var problems = Get(hardware, "ProblemDevices");
                    var count = GetItems(problems).Count;
                    status = count > 0 ? "Warning" : "Healthy";
                    resultText = count > 0
                        ? $"{count} device(s) report a configuration error."
                        : "Inventory completed; no problem device code was reported.";
                    details = resultText;
                    if (count > 0)
                        recommendation = "Review each device's reported configuration error before changing its driver.";
                }
            }
            else if (component.Name == "Drivers")
            {
                var hardware = Get(context, "Hardware");
                if (Get(hardware, "ProblemDevices").ValueKind == JsonValueKind.Array)
                {
                    tested = true;
                    var problems = GetItems(Get(hardware, "ProblemDevices"));
                    status = problems.Count > 0 ? "Warning" : "Healthy";
                    resultText = problems.Count > 0
                        ? $"{problems.Count} device(s) have a non-zero configuration error code."
                        : "No device configuration error codes were reported.";
                    details = resultText;
                }
            }
            else if (component.Name == "Network")
            {
                var tests = Get(context, "Network");
                if (tests.ValueKind == JsonValueKind.Array)
                {
                    tested = true;
                    var testItems = GetItems(tests);
                    var statuses = testItems.Select(item => SeverityStatus(StringValue(Get(item, "Severity")))).ToArray();
                    status = testItems.Count == 0 ? "Unknown" : HighestStatus(statuses);
                    resultText = testItems.Count == 0
                        ? "Network test returned no results."
                        : $"{testItems.Count} network check(s) returned results.";
                    details = string.Join(Environment.NewLine, testItems.Select(item =>
                        $"{StringValue(Get(item, "Test"))}: {StringValue(Get(item, "Result"))}"));
                    if (status is "Unknown" or "Not Tested")
                        recommendation = "The network test did not provide enough evidence to classify connectivity.";
                }
            }
            else if (component.Name == "Security")
            {
                var checks = Get(context, "Security");
                if (checks.ValueKind == JsonValueKind.Array)
                {
                    tested = true;
                    var items = GetItems(checks);
                    var knownStates = items.Select(item => Get(item, "State"))
                        .Where(value => value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        .ToArray();
                    var falseCount = knownStates.Count(value => value.ValueKind == JsonValueKind.False);
                    status = falseCount > 0 ? "Warning" : knownStates.Length == 0 ? "Unknown" :
                        knownStates.Length < items.Count ? "Unknown" : "Healthy";
                    resultText = falseCount > 0
                        ? $"{falseCount} security setting(s) were reported disabled."
                        : knownStates.Length == items.Count && items.Count > 0
                            ? "All collected security settings were enabled."
                            : "Security status is incomplete; at least one value was unavailable.";
                    details = string.Join(Environment.NewLine, items.Select(item =>
                        $"{StringValue(Get(item, "Check"))}: {StringValue(Get(item, "Evidence"))}"));
                }
            }
            else if (component.Name == "Services")
            {
                var serviceFindings = relevantFindings.Where(item =>
                    IsUnavailableFinding(item) ||
                    StringValue(Get(item, "Finding")).Contains("EventLog", StringComparison.OrdinalIgnoreCase) ||
                    StringValue(Get(item, "Finding")).Contains("Winmgmt", StringComparison.OrdinalIgnoreCase) ||
                    StringValue(Get(item, "Finding")).Contains("RpcSs", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (serviceFindings.Length > 0)
                {
                    tested = true;
                    status = HighestStatus(serviceFindings.Select(item => SeverityStatus(StringValue(Get(item, "Severity")))));
                    resultText = $"{serviceFindings.Length} essential service check(s) returned findings.";
                    details = string.Join(Environment.NewLine, serviceFindings.Select(item =>
                        $"{StringValue(Get(item, "Finding"))}: {StringValue(Get(item, "Evidence"))}"));
                }
            }
            else if (component.Name == "Windows Update")
            {
                if (Get(context, "InstalledUpdates").ValueKind == JsonValueKind.Array)
                {
                    tested = true;
                    status = "Unknown";
                    resultText = "Installed update inventory was collected; update compliance was not assessed.";
                    details = $"{GetItems(Get(context, "InstalledUpdates")).Count} installed update record(s) found. This does not establish that the system is up to date.";
                }
            }
            else if (component.Name == "Windows Integrity")
            {
                status = "Not Tested";
                resultText = "No SFC/DISM integrity verification was run.";
                details = "System information and events do not prove Windows component-store or protected-file integrity.";
            }

            if (!string.IsNullOrWhiteSpace(matchedError))
            {
                status = "Unknown";
                resultText = $"Collection failed: {matchedError}";
                details = resultText;
            }
            else if (tested && relevantFindings.Any(IsUnavailableFinding) &&
                     !relevantFindings.Any(item => !IsUnavailableFinding(item) &&
                         SeverityStatus(StringValue(Get(item, "Severity"))) is "Warning" or "Error" or "Critical"))
            {
                status = "Unknown";
                resultText = "A required check could not be queried.";
                details = string.Join(Environment.NewLine, relevantFindings.Where(IsUnavailableFinding)
                    .Select(item => StringValue(Get(item, "Evidence"))));
            }

            var lastChecked = tested || !string.IsNullOrWhiteSpace(matchedError)
                ? checkedAt?.ToLocalTime().ToString("g") ?? "Unknown"
                : "Never";
            result.Add(new ComponentHealth(component.Name, status, resultText, lastChecked, details, recommendation));
        }

        if (!completed && !string.IsNullOrWhiteSpace(failure))
            return result.Select(component => component with
            {
                Status = "Unknown",
                Result = "A diagnostic operation failed; this component cannot be classified.",
                Details = failure,
                LastChecked = checkedAt?.ToLocalTime().ToString("g") ?? "Unknown"
            }).ToList();
        return result;
    }

    private static void AddFindingIssues(
        JsonElement findings,
        JsonElement hypotheses,
        ICollection<DashboardIssue> issues,
        IDictionary<string, (string Severity, string Title)> scoredEvidence)
    {
        foreach (var finding in GetItems(findings))
        {
            var severity = StringValue(Get(finding, "Severity"));
            if (severity.Equals("Info", StringComparison.OrdinalIgnoreCase)) continue;
            var title = StringValue(Get(finding, "Finding"), "Diagnostic finding");
            var evidence = StringValue(Get(finding, "Evidence"));
            var area = StringValue(Get(finding, "Area"), "System");
            var unavailable = IsUnavailableFinding(finding);
            var mappedStatus = unavailable ? "Unknown" : SeverityStatus(severity);
            var recommendation = StringValue(Get(finding, "Recommendation"), "Review the detailed diagnostic evidence.");
            var repairId = MatchRepair(title, evidence, recommendation);
            var hypothesis = GetItems(hypotheses).FirstOrDefault(item =>
                StringValue(Get(item, "Title")).Equals(title, StringComparison.OrdinalIgnoreCase));
            var assessment = hypothesis.ValueKind == JsonValueKind.Object
                ? StringValue(Get(hypothesis, "Assessment"))
                : "No root cause is asserted without matching evidence.";
            var alternatives = hypothesis.ValueKind == JsonValueKind.Object
                ? string.Join("; ", GetItems(Get(hypothesis, "Alternatives")).Select(value => value.ToString()))
                : "";
            if (hypothesis.ValueKind == JsonValueKind.Object)
            {
                var recommendedTests = GetItems(Get(hypothesis, "RecommendedTests"));
                if (recommendedTests.Count > 0)
                    recommendation += $" Additional test: {string.Join("; ", recommendedTests.Select(value => value.ToString()))}";
            }
            issues.Add(new DashboardIssue(title, mappedStatus, area,
                unavailable ? "This check could not obtain enough data to classify the component." : title,
                evidence, string.IsNullOrWhiteSpace(alternatives) ? assessment : $"{assessment} Alternatives: {alternatives}",
                recommendation, repairId));
            if (!unavailable && SeverityWeights.ContainsKey(severity))
                AddScoreFactor(scoredEvidence, $"{area}|{title}|{evidence}", severity, title);
        }
    }

    private static void AddRuleIssues(JsonElement rules, ICollection<DashboardIssue> issues,
        IDictionary<string, (string Severity, string Title)> scoredEvidence)
    {
        foreach (var rule in GetItems(rules))
        {
            var title = StringValue(Get(rule, "Title"), "Matched diagnostic rule");
            var area = StringValue(Get(rule, "Area"), "Rules");
            if (issues.Any(issue => issue.Area.Equals(area, StringComparison.OrdinalIgnoreCase)))
                continue;
            var severity = StringValue(Get(rule, "Severity"), "Medium");
            var evidence = string.Join("; ", GetItems(Get(rule, "Evidence")).Select(value => value.ToString()));
            issues.Add(new DashboardIssue(title, SeverityStatus(severity), area,
                StringValue(Get(rule, "Description"), title), evidence,
                "A version-compatible rule matched the collected evidence; this is not proof of causality.",
                StringValue(Get(rule, "Recommendation"), "Review the matched rule and verify its evidence."),
                MatchRepair(title, evidence, StringValue(Get(rule, "Recommendation")))));
            if (SeverityWeights.ContainsKey(severity))
                AddScoreFactor(scoredEvidence, $"rule|{StringValue(Get(rule, "Id"))}", severity, title);
        }
    }

    private static void AddAnomalyIssues(JsonElement anomalies, ICollection<DashboardIssue> issues,
        IDictionary<string, (string Severity, string Title)> scoredEvidence)
    {
        foreach (var anomaly in GetItems(anomalies))
        {
            var title = $"Change in {StringValue(Get(anomaly, "Metric"), "metric")} relative to local baseline";
            if (issues.Any(issue => issue.Area.Equals("Performance", StringComparison.OrdinalIgnoreCase)))
                continue;
            var severity = StringValue(Get(anomaly, "Severity"), "Medium");
            var evidence = StringValue(Get(anomaly, "Evidence"));
            issues.Add(new DashboardIssue(title, SeverityStatus(severity), "Performance",
                "A measured value differs from this computer's local baseline; this is not a diagnosis.",
                evidence, "Workload or sampling conditions may differ from the baseline period.",
                "Repeat sampling during the reported symptom and compare under similar workload.", null));
            if (SeverityWeights.ContainsKey(severity))
                AddScoreFactor(scoredEvidence, $"anomaly|{StringValue(Get(anomaly, "Metric"))}", severity, title);
        }
    }

    private static void AddCollectionErrors(JsonElement errors, ICollection<DashboardIssue> issues)
    {
        foreach (var error in GetItems(errors))
        {
            var check = StringValue(Get(error, "Check"), "Unknown module");
            var message = StringValue(Get(error, "Error"), "No error details were returned.");
            issues.Add(new DashboardIssue($"{check} collection failed", "Unknown", check,
                "The diagnostic module failed to collect this area. It is not treated as healthy.",
                message, "Cause of the collection failure has not been determined.",
                "Review the module error, permissions and Windows feature availability, then rerun the check.", null));
        }
    }

    private static void AddInconclusiveHypotheses(JsonElement hypotheses, ICollection<DashboardIssue> issues)
    {
        foreach (var hypothesis in GetItems(hypotheses))
        {
            var state = StringValue(Get(hypothesis, "Status"));
            if (!state.Contains("Inconclusive", StringComparison.OrdinalIgnoreCase)) continue;
            var title = StringValue(Get(hypothesis, "Title"), "Root cause inconclusive");
            if (issues.Any(issue => issue.Title.Equals(title, StringComparison.OrdinalIgnoreCase))) continue;
            issues.Add(new DashboardIssue(title, "Unknown", "Root cause analysis",
                StringValue(Get(hypothesis, "Assessment"), "No root cause was established."),
                string.Join("; ", GetItems(Get(hypothesis, "SupportingEvidence")).Select(value => value.ToString())),
                string.Join("; ", GetItems(Get(hypothesis, "Alternatives")).Select(value => value.ToString())),
                string.Join("; ", GetItems(Get(hypothesis, "RecommendedTests")).Select(value => value.ToString())), null));
        }
    }

    private static string DetermineOverall(IReadOnlyCollection<ComponentHealth> components,
        IReadOnlyCollection<DashboardIssue> issues, bool completed)
    {
        if (issues.Any(issue => issue.Severity == "Critical")) return "Critical";
        if (issues.Any(issue => issue.Severity == "Error")) return "Error";
        if (!completed || components.All(component => component.Status is "Unknown" or "Not Tested"))
            return "Unknown";
        if (issues.Any(issue => issue.Severity == "Warning") ||
            components.Any(component => component.Status is "Unknown" or "Not Tested"))
            return "Warning";
        if (components.All(component => component.Status == "Healthy"))
            return "Healthy";
        return "Unknown";
    }

    private static string CollectionErrorFor(string component, JsonElement errors)
    {
        var checks = ComponentDefinitions.First(item => item.Name == component).Checks;
        var matching = GetItems(errors).FirstOrDefault(error =>
            checks.Contains(StringValue(Get(error, "Check")), StringComparer.OrdinalIgnoreCase));
        return matching.ValueKind == JsonValueKind.Object
            ? $"{StringValue(Get(matching, "Check"))}: {StringValue(Get(matching, "Error"))}"
            : "";
    }

    private static bool AreaMatches(string component, string area) => component switch
    {
        "Storage" => area.Equals("Storage", StringComparison.OrdinalIgnoreCase),
        "Performance" => area.Equals("Performance", StringComparison.OrdinalIgnoreCase),
        "Services" => area.Equals("Services", StringComparison.OrdinalIgnoreCase),
        "Windows Integrity" => area.Equals("System", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsUnavailableFinding(JsonElement finding)
    {
        var title = StringValue(Get(finding, "Finding"));
        return title.Contains("não foi possível", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("failed to", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
    }

    private static string? MatchRepair(string title, string evidence, string recommendation)
    {
        var text = $"{title} {evidence} {recommendation}";
        if (text.Contains("system file", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ficheiros protegidos", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("integrity corruption", StringComparison.OrdinalIgnoreCase))
            return "SfcScan";
        return null;
    }

    private static void AddScoreFactor(IDictionary<string, (string Severity, string Title)> factors,
        string key, string severity, string title)
    {
        factors.TryAdd(key, (severity, title));
    }

    private static string HighestStatus(IEnumerable<string> statuses) =>
        statuses.OrderByDescending(StatusPriority).FirstOrDefault() ?? "Not Tested";

    private static int StatusPriority(string status) => status switch
    {
        "Critical" => 6,
        "Error" => 5,
        "Warning" => 4,
        "Unknown" => 3,
        "Not Tested" => 2,
        "Healthy" => 1,
        _ => 0
    };

    private static string SeverityStatus(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => "Critical",
        "high" or "error" => "Error",
        "low" or "medium" or "warning" => "Warning",
        "info" or "healthy" => "Healthy",
        _ => "Unknown"
    };

    private static List<JsonElement> GetItems(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().ToList();
        return element.ValueKind == JsonValueKind.Object ? [element] : [];
    }

    private static JsonElement Get(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        foreach (var item in element.EnumerateObject())
            if (item.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
                return item.Value;
        return default;
    }

    private static string StringValue(JsonElement element, string fallback = "") =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? fallback,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => fallback
        };
}
