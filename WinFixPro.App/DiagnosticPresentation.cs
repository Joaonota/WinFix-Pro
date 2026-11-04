using System.Text.Json;

namespace WinFixPro;

public sealed record DiagnosticFindingPresentation(
    string Title,
    string Status,
    string StatusLabel,
    string Component,
    string Description,
    string Evidence,
    string PossibleCause,
    string Impact,
    string Recommendation,
    string NextAction);

public sealed record DiagnosticResultPresentation(
    string Title,
    string Status,
    string StatusLabel,
    string Summary,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<DiagnosticFindingPresentation> Findings,
    IReadOnlyList<DiagnosticMetricPresentation> Metrics,
    string TechnicalDetails,
    bool IsEmpty,
    bool IsFailure);

public sealed record DiagnosticMetricPresentation(string Name, string Value);

public static class DiagnosticStatusConverter
{
    public static string ToLabel(string? status) => Normalize(status) switch
    {
        "Healthy" => "Saudável",
        "Warning" => "Atenção",
        "Error" => "Erro",
        "Critical" => "Crítico",
        "Unknown" or "Not Tested" => "Desconhecido",
        "Completed" => "Concluído",
        "Cancelled" => "Cancelado",
        "Failed" => "Falhou",
        "Information" => "Informação",
        _ => "Desconhecido"
    };

    public static string Normalize(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "healthy" or "ok" or "success" or "passed" => "Healthy",
        "warning" or "medium" or "low" => "Warning",
        "error" or "high" => "Error",
        "critical" => "Critical",
        "unknown" or "not tested" or "inconclusive" or "unavailable" => "Unknown",
        "info" or "information" => "Information",
        "completed" => "Completed",
        "cancelled" or "canceled" => "Cancelled",
        "failed" or "failure" => "Failed",
        _ => "Unknown"
    };
}

public static class DiagnosticPresentationBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static DiagnosticResultPresentation Build(OperationResult? result, string? failure = null)
    {
        if (result is null)
            return Empty("Ainda não existem resultados para apresentar.", failure);

        var title = OperationTitle(result.Operation);
        var technicalDetails = BuildTechnicalDetails(result, failure);
        if (!string.IsNullOrWhiteSpace(failure) ||
            result.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
            result.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            var status = result.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ? "Unknown" : "Error";
            var description = status == "Error"
                ? "A operação não terminou corretamente. Consulte os detalhes técnicos e tente novamente."
                : "A operação foi cancelada e não produziu uma avaliação completa.";
            var finding = new DiagnosticFindingPresentation(
                title, status, DiagnosticStatusConverter.ToLabel(status), title, description,
                failure ?? result.Summary, "A causa não foi determinada.", "O estado do computador não foi confirmado por esta operação.",
                "Revise a mensagem técnica e execute novamente a análise.", "Tentar novamente");
            return new DiagnosticResultPresentation(title, status, DiagnosticStatusConverter.ToLabel(status),
                description, result.FinishedAt, [finding], [], technicalDetails, false, status == "Error");
        }

        DiagnosticResultDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DiagnosticResultDto>(result.Json, JsonOptions);
        }
        catch (JsonException ex)
        {
            var finding = new DiagnosticFindingPresentation(
                title, "Unknown", DiagnosticStatusConverter.ToLabel("Unknown"), title,
                "O resultado foi recebido, mas não pôde ser interpretado com segurança. São necessários mais dados.",
                "O conteúdo recebido não corresponde ao formato esperado.", "Não determinada.",
                "Não é possível avaliar o impacto sem dados legíveis.", "Consulte o resultado técnico ou execute a análise novamente.",
                "Executar novamente");
            return new DiagnosticResultPresentation(title, "Unknown", DiagnosticStatusConverter.ToLabel("Unknown"),
                finding.Description, result.FinishedAt, [finding], [],
                $"{technicalDetails}{Environment.NewLine}{Environment.NewLine}Erro de leitura: {ex.Message}",
                false, false);
        }

        if (result.Operation is "Diagnostics" or "Universal")
            return BuildHealthPresentation(result, payload, technicalDetails);

        return BuildGeneralPresentation(result, payload, technicalDetails);
    }

    private static DiagnosticResultPresentation BuildHealthPresentation(
        OperationResult result,
        DiagnosticResultDto? payload,
        string technicalDetails)
    {
        var snapshot = DashboardHealthAnalyzer.Analyze(result);
        var findings = new List<DiagnosticFindingPresentation>();
        findings.AddRange(snapshot.Components.Select(component => new DiagnosticFindingPresentation(
            component.Name,
            DiagnosticStatusConverter.Normalize(component.Status),
            DiagnosticStatusConverter.ToLabel(component.Status),
            component.Name,
            component.Result,
            component.Details,
            "A causa não é inferida para além da evidência recolhida.",
            component.Status is "Warning" or "Error" or "Critical"
                ? "Foi identificado um estado que requer revisão."
                : component.Status is "Unknown" or "Not Tested"
                    ? "O impacto não pode ser determinado sem mais dados."
                    : "Nenhum impacto adverso foi indicado na evidência recolhida.",
            component.Recommendation,
            component.Recommendation)));
        findings.AddRange(snapshot.Issues.Select(issue => new DiagnosticFindingPresentation(
            issue.Title,
            DiagnosticStatusConverter.Normalize(issue.Severity),
            DiagnosticStatusConverter.ToLabel(issue.Severity),
            issue.Area,
            issue.Description,
            issue.Evidence,
            issue.PossibleCause,
            "O impacto específico não foi quantificado pelos dados recolhidos.",
            issue.Recommendation,
            issue.Recommendation)));

        var title = OperationTitle(result.Operation);
        var status = DiagnosticStatusConverter.Normalize(snapshot.OverallStatus);
        var summary = ToSimpleSummary(status);
        var metrics = ReadMetrics(payload?.Context?.Summary ?? payload?.Summary);
        return new DiagnosticResultPresentation(title, status, DiagnosticStatusConverter.ToLabel(status), summary,
            result.FinishedAt, findings, metrics, technicalDetails, findings.Count == 0, false);
    }

    private static DiagnosticResultPresentation BuildGeneralPresentation(
        OperationResult result,
        DiagnosticResultDto? payload,
        string technicalDetails)
    {
        var context = payload?.Context;
        var rawFindings = payload?.Findings ?? context?.Findings ?? [];
        var findings = rawFindings.Select(item =>
        {
            var status = DiagnosticStatusConverter.Normalize(item.Status ?? item.Severity);
            return new DiagnosticFindingPresentation(
                First(item.Title, item.Finding, item.Name, item.Check, "Resultado da operação"),
                status,
                DiagnosticStatusConverter.ToLabel(status),
                First(item.Component, item.Area, item.Name, "Sistema"),
                First(item.Description, item.Assessment, item.Result, "O resultado não inclui uma descrição adicional."),
                First(item.Evidence, item.Value, "Não foi fornecida evidência adicional."),
                First(item.PossibleCause, "Não determinada pelos dados disponíveis."),
                First(item.Impact, "O impacto não foi especificado no resultado."),
                First(item.Recommendation, "Não foi fornecida uma recomendação específica."),
                First(item.NextAction, item.Recommendation, "Recolher mais dados antes de decidir uma ação."));
        }).ToArray();

        var metrics = ReadMetrics(context?.Summary ?? payload?.Summary);
        var explicitStatus = payload?.Status ?? result.Status;
        var status = DiagnosticStatusConverter.Normalize(explicitStatus);
        var highestFindingStatus = HighestFindingStatus(findings);
        if (StatusPriority(highestFindingStatus) > StatusPriority(status))
            status = highestFindingStatus;
        if (findings.Length == 0 && metrics.Count == 0)
            status = "Unknown";
        var summary = findings.Length > 0
            ? $"A operação terminou e apresentou {findings.Length} resultado(s). As conclusões limitam-se à evidência apresentada."
            : findings.Length == 0 && metrics.Count == 0
                ? "A operação terminou, mas o resultado está vazio ou incompleto. São necessários mais dados para avaliar o estado."
                : First(payload?.Message, result.Summary,
                    "A operação terminou, mas não forneceu informação suficiente para uma avaliação detalhada.");
        if (findings.Length == 0 && metrics.Count > 0)
            summary = "Foram recolhidos alguns valores, mas não há verificações suficientes para classificar o estado.";

        return new DiagnosticResultPresentation(OperationTitle(result.Operation), status,
            DiagnosticStatusConverter.ToLabel(status), summary, result.FinishedAt, findings, metrics,
            technicalDetails, findings.Length == 0 && metrics.Count == 0, false);
    }

    private static DiagnosticResultPresentation Empty(string message, string? failure) =>
        new("Resultado do diagnóstico", "Unknown", DiagnosticStatusConverter.ToLabel("Unknown"),
            message, null, [], [], failure ?? "Ainda não foi recebido um resultado.", true, !string.IsNullOrWhiteSpace(failure));

    private static IReadOnlyList<DiagnosticMetricPresentation> ReadMetrics(Dictionary<string, JsonElement>? values)
    {
        if (values is null) return [];
        return values
            .Where(item => item.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            .Select(item => new DiagnosticMetricPresentation(LocalizeField(item.Key), item.Value.ToString()))
            .ToArray();
    }

    private static string BuildTechnicalDetails(OperationResult result, string? failure)
    {
        var details = $"Operação interna: {result.Operation}{Environment.NewLine}" +
                      $"Estado interno: {result.Status}{Environment.NewLine}" +
                      $"Início: {result.StartedAt:O}{Environment.NewLine}" +
                      $"Conclusão: {result.FinishedAt:O}";
        if (!string.IsNullOrWhiteSpace(failure))
            details += $"{Environment.NewLine}Erro: {failure}";
        return $"{details}{Environment.NewLine}{Environment.NewLine}Resultado original (JSON):{Environment.NewLine}{result.Json}";
    }

    private static string ToSimpleSummary(string status) => status switch
    {
        "Healthy" => "As verificações realizadas não identificaram problemas nos componentes avaliados.",
        "Warning" => "Foram identificados pontos que precisam de atenção ou verificações incompletas.",
        "Error" => "Foi identificado um erro num dos componentes avaliados.",
        "Critical" => "Foi identificado um problema crítico que requer atenção prioritária.",
        _ => "Não há dados suficientes para confirmar o estado de todos os componentes."
    };

    private static string HighestFindingStatus(IEnumerable<DiagnosticFindingPresentation> findings)
    {
        var statuses = findings.Select(item => item.Status).ToHashSet(StringComparer.Ordinal);
        foreach (var status in new[] { "Critical", "Error", "Warning", "Healthy" })
            if (statuses.Contains(status))
                return status;
        if (statuses.Contains("Information"))
            return "Information";
        return "Unknown";
    }

    private static int StatusPriority(string status) => status switch
    {
        "Critical" => 5,
        "Error" => 4,
        "Warning" => 3,
        "Healthy" => 2,
        "Information" => 1,
        _ => 0
    };

    public static string OperationTitle(string operation) => operation switch
    {
        "Diagnostics" => "Integridade do Windows",
        "Universal" => "Diagnóstico completo",
        "Dashboard" => "Estado do computador",
        "Hardware" => "Diagnóstico de hardware",
        "Performance" => "Análise de desempenho",
        "Network" => "Diagnóstico de rede",
        "WindowsUpdate" => "Estado das atualizações",
        "Security" => "Verificação de segurança",
        "Services" => "Verificação de serviços",
        "Drivers" => "Verificação de controladores",
        "CommonIssues" => "Problemas comuns",
        "Unknown" => "Análise do problema",
        _ when operation.StartsWith("Repair:", StringComparison.Ordinal) &&
               OperationCatalog.TryGet(operation, out var definition) => definition.Title,
        _ => "Resultado do diagnóstico"
    };

    private static string LocalizeField(string field) => field switch
    {
        "SystemDrive" => "Unidade do sistema",
        "DiskFreeGB" => "Espaço livre (GB)",
        "DiskSizeGB" => "Capacidade (GB)",
        "CpuLoadPercent" => "Utilização do processador (%)",
        "MemoryUsedPercent" => "Memória utilizada (%)",
        _ => field
    };

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    public sealed class DiagnosticResultDto
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
        public DiagnosticContextDto? Context { get; set; }
        public Dictionary<string, JsonElement>? Summary { get; set; }
        public List<DiagnosticFindingDto>? Findings { get; set; }
    }

    public sealed class DiagnosticContextDto
    {
        public Dictionary<string, JsonElement>? Summary { get; set; }
        public List<DiagnosticFindingDto>? Findings { get; set; }
    }

    public sealed class DiagnosticFindingDto
    {
        public string? Title { get; set; }
        public string? Finding { get; set; }
        public string? Name { get; set; }
        public string? Check { get; set; }
        public string? Status { get; set; }
        public string? Severity { get; set; }
        public string? Component { get; set; }
        public string? Area { get; set; }
        public string? Description { get; set; }
        public string? Assessment { get; set; }
        public string? Result { get; set; }
        public string? Evidence { get; set; }
        public string? Value { get; set; }
        public string? PossibleCause { get; set; }
        public string? Impact { get; set; }
        public string? Recommendation { get; set; }
        public string? NextAction { get; set; }
    }
}