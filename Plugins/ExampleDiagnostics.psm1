function Invoke-WinFixDiagnosticPlugin {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Context)
    if (-not $Context.Summary -or -not $Context.PSObject.Properties['Findings']) {
        throw 'Expected a version 1 universal diagnostic context.'
    }
    [pscustomobject]@{
        Area='Plugin contract'
        Finding="Received $(@($Context.Findings).Count) findings for Windows build $($Context.Summary.BuildNumber)."
        Severity='Info'
        Evidence='Read-only example plugin verified the diagnostic context contract.'
        Recommendation='Replace this example with an organization-reviewed, read-only module.'
    }
}

Export-ModuleMember -Function Invoke-WinFixDiagnosticPlugin
