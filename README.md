# WinFix Pro 0.2

WinFix Pro 0.2 includes a standalone WPF desktop host and a local PowerShell diagnostic engine. It is independent of `WinFix-Pro-MVP-0.1`; it neither imports nor modifies that release.

## Standalone desktop application

The primary user experience is the Windows desktop application. After publishing, launch `dist\WinFixPro\WinFixPro.exe` or use the portable ZIP. No terminal, developer environment, or separately installed .NET runtime is required on a Windows x64 target. The .NET 10 WPF host is published self-contained. Windows PowerShell 5.1 is the fallback engine; an installed PowerShell 7 under Program Files is preferred.

The application requests UAC elevation at startup. If elevation is declined, read-only diagnostics remain available and repair actions stay disabled. Diagnostic and repair operations are allow-listed, run asynchronously, and exchange JSON payloads with the bundled worker. The worker and PowerShell modules are extracted under `%LOCALAPPDATA%\WinFixPro\0.2\Engine` and checked against embedded resource hashes at startup. This integrity check is not a substitute for code signing.

The GUI provides Dashboard, Universal Diagnostics, hardware, performance, network, Windows health/update, security, services, drivers, common issues, knowledge, unknown-problem analysis, repair, reports, history, and settings views. Repair confirmation requires a completed diagnostic in the current session, describes the proposed operation and risk, and runs only an explicitly selected allow-listed action. A completed repair is not presented as proof of resolution; review its output and run a follow-up diagnostic.

The Dashboard runs a real Quick Scan at startup and presents the latest collected evidence, health status, issue counts, component results, recommendations, and diagnostic time. `Unknown` and `Not Tested` remain distinct from `Healthy`; unavailable or failed measurements are surfaced and do not reduce the evidence-based score. Use Quick Scan or Full System Scan to refresh the dashboard, open issue details for evidence and recommendations, and export a report. Light/dark theme preference is available in Settings.

Build a release executable from this directory with the .NET 10 SDK:

```powershell
dotnet publish .\WinFixPro.App\WinFixPro.App.csproj -c Release -r win-x64 --self-contained true -o .\dist\WinFixPro
Compress-Archive -Path .\dist\WinFixPro\* -DestinationPath .\dist\WinFixPro-0.2.0-win-x64.zip -Force
```

The release output includes version metadata and the application icon. It is a portable distribution, not a signed installer. The app stores local history, configuration and logs under `%LOCALAPPDATA%\WinFixPro\0.2`. The timeout preference applies to diagnostics; confirmed maintenance repairs have their own bounded timeout. PowerShell execution policies managed by the computer administrator are not bypassed.

## Legacy PowerShell UI

The original PowerShell/WPF entry point remains available for compatibility. Open Windows PowerShell 5.1 in this folder and run:

```powershell
.\WinFix-Pro.ps1
```

For general inventory and diagnostics, standard-user mode is supported. SFC and DISM repair operations require an elevated PowerShell session. In this legacy entry point, repairs start in simulation mode by default; uncheck simulation and accept the detailed confirmation dialog before a real repair.

## Included diagnostic capabilities

- Live OS, CPU, memory, system-drive and pending-reboot information.
- Diagnostic findings with evidence, recommendations and a calculated 0–100 health score.
- Hardware inventory, process sampling, network checks, service inventory, Windows event analysis and read-only Defender/firewall/UAC checks.
- Universal Diagnostics performs read-only collection of installed apps, startup entries, signed drivers, user profiles, scheduled tasks, installed updates and physical disks where supported; collection errors are reported per area.
- A versioned declarative rule catalog, Windows-build compatibility filtering, event-signature correlation, evidence-backed hypotheses, explicit contradictory-evidence handling and an inconclusive outcome when evidence does not support a cause.
- Baseline-relative CPU, memory and system-drive anomaly checks using at least three local samples. An anomaly is a deviation, not an asserted root cause.
- Configurable default diagnostic profile, local PowerShell plugin contract (disabled unless explicitly selected), case recording with basic local identifier redaction, and reviewed/expiring local knowledge candidates.
- A local JSON knowledge-bundle import flow. Imported entries remain pending review; activation requires explicit review, HTTPS provenance, verification steps, compatibility checks and a future expiry date.
- Explicitly confirmed SFC, DISM RestoreHealth and local DNS-cache repair operations.
- HTML, JSON, CSV and TXT exports; local SQLite operation history and application audit/error logs.

Diagnostic evidence is processed locally and is not transmitted to external services. External material must be obtained and imported by the technician as a JSON knowledge bundle; imported material is never trusted or activated automatically. Diagnostic plugins in `Plugins` are opt-in executable PowerShell code and should only be enabled when reviewed and trusted by the organization.

## Safety and current limitations

- Repairs use fixed, allow-listed operations; user text is never interpreted as a command.
- Legacy UI repairs run in simulation by default. The standalone app performs a real repair only after an affirmative confirmation and UAC elevation.
- No personal files are deleted. Network settings, Windows Update policies, firewall, Defender, UAC, essential services and event logs are not modified.
- SFC/DISM output is captured and the exit code reported; the application does not claim that Windows integrity is repaired solely because the process exited successfully. Consult the corresponding CBS/DISM logs.
- DNS cache validation performs a live public-name lookup, which may be blocked by enterprise DNS policy.
- Universal collection is a bounded local inventory, not exhaustive introspection of every Windows component. Process wait chains, disk latency/SMART, reliable temperatures, GPU/USB-specific health, BitLocker/Secure Boot status, DLL/runtime dependency analysis, deep registry/policy auditing, boot-time analysis, DHCP lease validation and VPN-specific tests are not currently implemented.
- Baseline anomaly checks are intentionally limited to CPU, memory and free system-drive space; they require at least three local measurements and do not adjust for workload. New hardware, app or OS behavior may remain unknown until a reviewed rule/plugin is added.
- Root-cause hypotheses are ranked observations/correlations, not proof. Missing data is reported as a collection error, not treated as a negative result. Unknown symptoms without supporting evidence are explicitly marked inconclusive.
- Knowledge matching is local text matching with compatibility and expiry checks, not a live Microsoft Q&A/forum search. The application does not download diagnostic data or automatically scrape sources. Knowledge JSON contains suggestions only; it cannot encode or run commands.
- Profiles currently use the bundled `Profiles\default.json`; custom profile selection is not exposed in the UI.
- Service inventory is read-only. Service changes, driver installation/rollback, Update policy changes, destructive disk operations, repair backups/rollback, persistent graphs and localization are not implemented.
- Health scoring uses only measured diagnostics in this release; unavailable checks do not receive inferred scores.

## Tests

From this directory:

```powershell
.\Tests\Run-Tests.ps1
dotnet run --project .\Tests\WinFixPro.App.Tests\WinFixPro.App.Tests.csproj -c Release
.\Tests\WinFixPro.App.Tests\Run-UiSmoke.ps1
```

The PowerShell test script checks live OS/hardware discovery, rules and version constraints, anomaly baselines, repeated-event correlation, contradictory hypotheses, unknown/inconclusive symptoms, adaptive test de-duplication, plugin compatibility/contract, case redaction, knowledge review/import/expiry, repair confirmation/simulation guards, and legacy report formats. The C# host tests the operation allow-list, SQLite history/settings, HTML/CSV/JSON/UTF-8 report behavior, embedded PowerShell worker, and live universal analysis. Neither test suite executes real system repairs.

The UI smoke test requires an interactive Windows desktop. It exercises native window controls, movement/resizing, every sidebar destination, Quick Scan and Full System Scan, issue details, report export, and clean close.

## Local data

- `%LOCALAPPDATA%\WinFixPro\0.2\History\`: SQLite operation history and settings.
- `%LOCALAPPDATA%\WinFixPro\0.2\Knowledge\`: local cases and reviewed knowledge.
- `%LOCALAPPDATA%\WinFixPro\0.2\Logs\`: local audit and error logs.
- `%LOCALAPPDATA%\WinFixPro\0.2\Engine\`: extracted, hash-checked PowerShell resources.

Case descriptions and reports can contain system details; review and redact them before sharing, and share them only with authorized support personnel.
