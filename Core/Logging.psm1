function Write-WinFixLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('INFO','WARNING','ERROR','AUDIT')][string]$Level = 'INFO',
        [string]$LogDirectory = (Join-Path $PSScriptRoot '..\Logs')
    )

    if (-not (Test-Path -LiteralPath $LogDirectory)) {
        New-Item -ItemType Directory -Path $LogDirectory -Force -ErrorAction Stop | Out-Null
    }
    $entry = [pscustomobject]@{
        Time = (Get-Date).ToString('o')
        Level = $Level
        Message = $Message
    }
    Add-Content -LiteralPath (Join-Path $LogDirectory 'WinFixPro.log') -Value ($entry | ConvertTo-Json -Compress) -Encoding UTF8 -ErrorAction Stop
}

Export-ModuleMember -Function Write-WinFixLog
