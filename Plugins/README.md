# Diagnostic plugin contract

Plugins are local PowerShell modules (`*.psm1`) accompanied by a same-named JSON manifest. A manifest has `schemaVersion: 1`, a `name`, `version`, `entryPoint` set to `Invoke-WinFixDiagnosticPlugin`, and `minimumWindowsBuild`/`maximumWindowsBuild` (zero means unspecified). The exported entry point accepts `-Context` and returns objects with `Area`, `Finding`, `Severity`, `Evidence`, and `Recommendation`.

The host never downloads or automatically runs plugins. A technician must explicitly opt in to running local plugins. Only install modules reviewed and trusted by the organization: a PowerShell plugin executes with the user's permissions. Plugin failures and incompatible manifests are surfaced as collection errors.
