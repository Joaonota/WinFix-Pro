function Get-WinFixRepairCatalog {
    @(
        [pscustomobject]@{Id='SfcScan';Name='System File Checker';Category='System Repair';Description='Executa SFC /scannow para verificar e reparar ficheiros protegidos do Windows.';Risk='Low';RequiresAdmin=$true;MayRestart=$false;Backup='Não aplicável; a ferramenta usa o mecanismo de manutenção do Windows.';Validation='O código de saída confirma a execução do processo, não a reparação; reveja o registo CBS e execute novo diagnóstico.'}
        [pscustomobject]@{Id='DismRestore';Name='DISM RestoreHealth';Category='System Repair';Description='Repara o Component Store online através do DISM e fontes configuradas do Windows.';Risk='Medium';RequiresAdmin=$true;MayRestart=$false;Backup='Não aplicável; o DISM usa o mecanismo de manutenção do Windows.';Validation='O código de saída confirma a execução do processo, não a reparação; reveja o registo DISM e execute novo diagnóstico.'}
        [pscustomobject]@{Id='FlushDns';Name='Limpar cache DNS';Category='Network Repair';Description='Limpa apenas a cache DNS local; não altera servidores nem políticas de rede.';Risk='Low';RequiresAdmin=$true;MayRestart=$false;Backup='Não aplicável; a cache é recriada automaticamente.';Validation='Verificar a resolução DNS após a limpeza.'}
    )
}

function Invoke-WinFixRepair {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('SfcScan','DismRestore','FlushDns')][string]$RepairId,
        [Parameter(Mandatory)][switch]$Confirmed,
        [switch]$Simulation,
        [ValidateRange(10,14400)][int]$TimeoutSeconds = 14400
    )
    $repair = Get-WinFixRepairCatalog | Where-Object {$_.Id -eq $RepairId}
    if (-not $repair) { throw "Reparação desconhecida: $RepairId" }
    if (-not $Confirmed) { throw 'Operação cancelada: é necessária autorização explícita.' }
    if ($Simulation) {
        return [pscustomobject]@{RepairId=$RepairId;Status='Simulated';StartedAt=(Get-Date).ToString('o');FinishedAt=(Get-Date).ToString('o');ExitCode=$null;Output='Modo de simulação: não foram efetuadas alterações no sistema.';Validated=$false}
    }
    if ($repair.RequiresAdmin -and -not (Test-WinFixAdmin)) { throw "A reparação '$($repair.Name)' requer privilégios de administrador." }

    $started = Get-Date
    $exitCode = $null
    $output = ''
    switch ($RepairId) {
        'FlushDns' {
            Clear-DnsClientCache -ErrorAction Stop
            $output = 'Clear-DnsClientCache concluído. A resolução DNS será consultada na validação.'
            $exitCode = 0
        }
        {$_ -in 'SfcScan','DismRestore'} {
            $exe = if ($RepairId -eq 'SfcScan') {Join-Path $env:SystemRoot 'System32\sfc.exe'} else {Join-Path $env:SystemRoot 'System32\dism.exe'}
            $arguments = if ($RepairId -eq 'SfcScan') {@('/scannow')} else {@('/Online','/Cleanup-Image','/RestoreHealth')}
            $tempBase = Join-Path ([IO.Path]::GetTempPath()) ("WinFix-{0}-{1}" -f $RepairId,[guid]::NewGuid().ToString('N'))
            $tempOutput = "$tempBase.out.txt"
            $tempError = "$tempBase.err.txt"
            try {
                $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $tempOutput -RedirectStandardError $tempError -ErrorAction Stop
                if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                    $process.Kill()
                    throw "A operação excedeu o limite de $TimeoutSeconds segundos e foi terminada."
                }
                $process.Refresh()
                $exitCode = $process.ExitCode
                if (Test-Path -LiteralPath $tempOutput) {$output = Get-Content -LiteralPath $tempOutput -Raw -ErrorAction Stop}
                if (Test-Path -LiteralPath $tempError) {$output += "`n" + (Get-Content -LiteralPath $tempError -Raw -ErrorAction Stop)}
                if ($exitCode -ne 0) {throw "$($repair.Name) terminou com código de saída $exitCode. $output"}
            } finally {
                if (Test-Path -LiteralPath $tempOutput) {Remove-Item -LiteralPath $tempOutput -Force -ErrorAction SilentlyContinue}
                if (Test-Path -LiteralPath $tempError) {Remove-Item -LiteralPath $tempError -Force -ErrorAction SilentlyContinue}
            }
        }
    }
    $validated = $false
    if ($RepairId -eq 'FlushDns') {
        try { Resolve-DnsName -Name 'www.microsoft.com' -ErrorAction Stop | Out-Null; $validated = $true }
        catch {$output += "`nValidação DNS falhou: $($_.Exception.Message)"}
    } elseif ($null -ne $exitCode) {
        $validated = $false
        $output += "`nCódigo de saída: $exitCode. Isto não confirma por si só a integridade reparada; reveja os registos do Windows e execute um novo diagnóstico."
    }
    [pscustomobject]@{RepairId=$RepairId;Status=$(if ($validated) {'Completed'} else {'CompletedWithWarnings'});StartedAt=$started.ToString('o');FinishedAt=(Get-Date).ToString('o');ExitCode=$exitCode;Output=$output;Validated=$validated}
}

Export-ModuleMember -Function Get-WinFixRepairCatalog,Invoke-WinFixRepair
