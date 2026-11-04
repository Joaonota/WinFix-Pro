# Changelog

## 0.2

- Added a standalone .NET 10 WPF x64 host with programmatic UAC elevation, embedded PowerShell modules, allow-listed operations, asynchronous progress/cancellation, SQLite history, reports and portable self-contained publishing.
- Added GUI workflows for all bundled diagnostic areas, case capture, knowledge import/review, repair confirmation and configurable diagnostic timeouts.
- Hardened repair dispatch and PowerShell discovery, separated case outcome/tests, and prevented successful diagnostics from being reported as failed when local history persistence fails.
- Updated the Microsoft.Data.Sqlite dependency to 10.0.14 and documented the executable build/distribution workflow.
- Created an independent modular PowerShell 5.1/WPF release; MVP 0.1 remains unchanged.
- Added real system, hardware, process, network, service, event and security data collection.
- Added health scoring, local history, structured logs and HTML/JSON/CSV/TXT reports.
- Added a confirmed repair catalog with a safe-by-default simulation mode and bounded process execution.
- Added automated offline tests and documented feature limitations.
- Added local universal evidence collection, versioned declarative rules, compatibility-aware knowledge, correlation and behavioral-baseline analysis.
- Added inconclusive/contradictory root-cause hypotheses, a deduplicated adaptive test plan, and opt-in version-constrained diagnostic plugins.
- Added local case recording, identity redaction, pending-review knowledge promotion/import and expiring reviewed entries.
