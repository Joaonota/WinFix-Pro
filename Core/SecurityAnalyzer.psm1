function Get-WinFixSecurityStatus {
    [CmdletBinding()]
    param()
    $items = New-Object 'System.Collections.Generic.List[object]'
    try {
        $defender = Get-MpComputerStatus -ErrorAction Stop
        $items.Add([pscustomobject]@{Check='Antivirus';State=$defender.AntivirusEnabled;Evidence="Antivirus ativo: $($defender.AntivirusEnabled); última atualização: $($defender.AntivirusSignatureLastUpdated)"})
        $items.Add([pscustomobject]@{Check='Real-time protection';State=$defender.RealTimeProtectionEnabled;Evidence="Proteção em tempo real: $($defender.RealTimeProtectionEnabled)"})
    } catch {
        $items.Add([pscustomobject]@{Check='Microsoft Defender';State=$null;Evidence="Indisponível ou sem autorização: $($_.Exception.Message)"})
    }
    foreach ($profile in @(Get-NetFirewallProfile -ErrorAction Stop)) {
        $items.Add([pscustomobject]@{Check="Firewall $($profile.Name)";State=$profile.Enabled;Evidence="Perfil $($profile.Name): ativo=$($profile.Enabled)"})
    }
    try {
        $uac = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name EnableLUA -ErrorAction Stop
        $items.Add([pscustomobject]@{Check='UAC';State=($uac.EnableLUA -eq 1);Evidence="EnableLUA=$($uac.EnableLUA)"})
    } catch {
        $items.Add([pscustomobject]@{Check='UAC';State=$null;Evidence="Não foi possível consultar: $($_.Exception.Message)"})
    }
    return $items.ToArray()
}

Export-ModuleMember -Function Get-WinFixSecurityStatus
