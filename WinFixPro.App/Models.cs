namespace WinFixPro;

public sealed record OperationDefinition(string Id, string Title, string Description, bool RequiresAdministrator = false, bool IsRepair = false);

public sealed record OperationResult(string Operation, string Status, string Json, string Summary, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

public sealed record HistoryRecord(long Id, DateTimeOffset CreatedAt, string Operation, string Status, string Summary, string Json);

public sealed class PowerShellPayload
{
    public string? RepairId { get; set; }
    public string[] Symptoms { get; set; } = [];
    public bool AllowPlugins { get; set; }
    public string? KnowledgeQuery { get; set; }
    public string? ExportFormat { get; set; }
    public string? CaseOutcome { get; set; }
    public string? CaseEvidence { get; set; }
    public string? CaseHypotheses { get; set; }
    public string? CaseTests { get; set; }
    public string[] CaseSources { get; set; } = [];
    public string? CaseStatus { get; set; }
    public string? KnowledgeBundlePath { get; set; }
    public string? KnowledgeEntryId { get; set; }
    public string? ReviewNotes { get; set; }
}

public static class OperationCatalog
{
    public static readonly IReadOnlyDictionary<string, OperationDefinition> Operations =
        new Dictionary<string, OperationDefinition>(StringComparer.Ordinal)
        {
            ["Dashboard"] = new("Dashboard", "Dashboard", "Estado atual do computador", false),
            ["Universal"] = new("Universal", "Universal Diagnostics", "Recolha ampla e análise adaptativa", false),
            ["Diagnostics"] = new("Diagnostics", "Windows Health", "Integridade, serviços essenciais e eventos", false),
            ["Hardware"] = new("Hardware", "Hardware Diagnostics", "Inventário de dispositivos, controladores e discos", false),
            ["Performance"] = new("Performance", "Performance Analyzer", "Amostragem de processos e uso de memória", false),
            ["Network"] = new("Network", "Network Diagnostics", "Adaptadores, DNS, gateway e HTTPS", false),
            ["WindowsUpdate"] = new("WindowsUpdate", "Windows Update", "Atualizações instaladas e estado associado", false),
            ["Security"] = new("Security", "Security", "Defender, firewall e UAC em modo de leitura", false),
            ["Services"] = new("Services", "Services", "Inventário de serviços e tarefas agendadas", false),
            ["Drivers"] = new("Drivers", "Drivers", "Inventário de controladores assinados e dispositivos problemáticos", false),
            ["CommonIssues"] = new("CommonIssues", "Common Issues", "Regras locais, compatibilidade e hipóteses", false),
            ["Knowledge"] = new("Knowledge", "Knowledge Center", "Pesquisar conhecimento local aprovado", false),
            ["Unknown"] = new("Unknown", "Unknown Problem Analysis", "Investigar sintomas sem assumir uma causa conhecida", false),
            ["SaveCase"] = new("SaveCase", "Record case", "Guardar caso local para revisão", false),
            ["ImportKnowledge"] = new("ImportKnowledge", "Import knowledge bundle", "Importar conhecimento local pendente de revisão", false),
            ["ReviewKnowledge"] = new("ReviewKnowledge", "Review knowledge", "Rever conhecimento local", false),
            ["History"] = new("History", "History", "Histórico local SQLite", false),
            ["Reports"] = new("Reports", "Reports", "Exportar relatório local", false),
            ["Settings"] = new("Settings", "Settings", "Configuração local e estado do motor", false),
            ["Repair:SfcScan"] = new("Repair:SfcScan", "SFC /scannow", "Verifica ficheiros protegidos do Windows.", true, true),
            ["Repair:DismRestore"] = new("Repair:DismRestore", "DISM RestoreHealth", "Repara o Component Store online.", true, true),
            ["Repair:FlushDns"] = new("Repair:FlushDns", "Flush DNS cache", "Limpa a cache DNS local.", true, true)
        };

    public static bool TryGet(string id, out OperationDefinition definition) => Operations.TryGetValue(id, out definition!);
}
