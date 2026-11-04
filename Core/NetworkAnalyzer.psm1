function Get-WinFixNetworkDiagnostic {
    [CmdletBinding()]
    param()
    $results = New-Object 'System.Collections.Generic.List[object]'
    $adapters = @(Get-NetIPConfiguration -ErrorAction Stop | Where-Object {$_.NetAdapter.Status -eq 'Up'})
    if ($adapters.Count -eq 0) {
        $results.Add([pscustomobject]@{Severity='High'; Test='Adapter'; Result='Nenhum adaptador de rede ativo foi encontrado.'})
        return $results.ToArray()
    }
    foreach ($adapter in $adapters) {
        $ip = if ($adapter.IPv4Address) {$adapter.IPv4Address[0].IPAddress} else {'Sem IPv4'}
        $results.Add([pscustomobject]@{Severity='Info';Test='Adapter';Result="$($adapter.InterfaceAlias) | $ip"})
        if ($adapter.IPv4DefaultGateway) {
            $gateway = $adapter.IPv4DefaultGateway.NextHop
            $reachable = Test-Connection -ComputerName $gateway -Count 1 -Quiet -ErrorAction SilentlyContinue
            $results.Add([pscustomobject]@{Severity=$(if ($reachable) {'Info'} else {'Medium'});Test='Gateway';Result="$gateway | $(if ($reachable) {'ICMP respondeu'} else {'sem resposta ICMP; pode estar filtrado'})"})
        } else {
            $results.Add([pscustomobject]@{Severity='Medium';Test='Gateway';Result='Não existe gateway IPv4 configurado neste adaptador.'})
        }
        $results.Add([pscustomobject]@{Severity='Info';Test='DNS servers';Result=(@($adapter.DnsServer | ForEach-Object {$_.ServerAddresses}) -join ', ')})
        try {
            $resolved = Resolve-DnsName -Name 'www.microsoft.com' -ErrorAction Stop
            $results.Add([pscustomobject]@{Severity='Info';Test='DNS lookup';Result="www.microsoft.com resolveu para $((@($resolved | Where-Object {$_.IPAddress} | Select-Object -First 2 -ExpandProperty IPAddress)) -join ', ')"})
        } catch {
            $results.Add([pscustomobject]@{Severity='High';Test='DNS lookup';Result=$_.Exception.Message})
        }
    }
    try {
        $response = Invoke-WebRequest -Uri 'https://www.microsoft.com' -Method Head -TimeoutSec 8 -UseBasicParsing -ErrorAction Stop
        $results.Add([pscustomobject]@{Severity='Info';Test='HTTPS';Result="Ligação HTTPS estabelecida (HTTP $([int]$response.StatusCode))."})
    } catch {
        $results.Add([pscustomobject]@{Severity='Medium';Test='HTTPS';Result="Falha no teste HTTPS: $($_.Exception.Message)"})
    }
    return $results.ToArray()
}

Export-ModuleMember -Function Get-WinFixNetworkDiagnostic
