function Test-WinFixAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-WinFixSystemSummary {
    [CmdletBinding()]
    param()
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $processors = @(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop)
    $totalRam = [double]$os.TotalVisibleMemorySize
    $freeRam = [double]$os.FreePhysicalMemory
    $memoryPercent = if ($totalRam -gt 0) { [math]::Round((1 - ($freeRam / $totalRam)) * 100, 1) } else { $null }
    $cpuLoad = if ($processors.Count -gt 0) {
        [math]::Round((($processors | Measure-Object -Property LoadPercentage -Average).Average), 1)
    } else { $null }
    $systemDrive = [string]$os.SystemDrive
    $disk = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='$systemDrive'" -ErrorAction Stop
    $boot = [datetime]$os.LastBootUpTime
    $rebootPending = Test-WinFixPendingReboot

    [pscustomobject]@{
        ComputerName = $env:COMPUTERNAME
        Caption = [string]$os.Caption
        Version = [string]$os.Version
        BuildNumber = [string]$os.BuildNumber
        Architecture = [string]$os.OSArchitecture
        SystemDrive = $systemDrive
        CpuName = (($processors | ForEach-Object { $_.Name.Trim() }) -join '; ')
        CpuLoadPercent = $cpuLoad
        TotalMemoryGB = [math]::Round($totalRam / 1MB, 2)
        MemoryUsedPercent = $memoryPercent
        DiskSizeGB = if ($disk.Size) { [math]::Round($disk.Size / 1GB, 1) } else { $null }
        DiskFreeGB = if ($disk.FreeSpace) { [math]::Round($disk.FreeSpace / 1GB, 1) } else { $null }
        Uptime = ((Get-Date) - $boot).ToString('d\.hh\:mm')
        LastBootTime = $boot
        PendingReboot = $rebootPending
        IsAdministrator = Test-WinFixAdmin
        CollectedAt = (Get-Date).ToString('o')
    }
}

function Test-WinFixPendingReboot {
    $paths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
    )
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path) { return $true }
    }
    try {
        $sessionManager = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction Stop
        if ($sessionManager.PendingFileRenameOperations) { return $true }
    } catch [System.Management.Automation.ItemNotFoundException] {
    } catch [System.Management.Automation.PSArgumentException] {
    }
    return $false
}

function Get-WinFixHardwareInventory {
    [CmdletBinding()]
    param()
    [pscustomobject]@{
        ComputerSystem = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop |
            Select-Object Manufacturer, Model, TotalPhysicalMemory
        Bios = Get-CimInstance Win32_BIOS -ErrorAction Stop |
            Select-Object Manufacturer, SMBIOSBIOSVersion, ReleaseDate
        Processors = @(Get-CimInstance Win32_Processor -ErrorAction Stop |
            Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, LoadPercentage)
        Memory = @(Get-CimInstance Win32_PhysicalMemory -ErrorAction Stop |
            Select-Object DeviceLocator, Manufacturer, @{N='CapacityGB';E={[math]::Round($_.Capacity / 1GB, 1)}}, Speed)
        Disks = @(Get-CimInstance Win32_DiskDrive -ErrorAction Stop |
            Select-Object Model, InterfaceType, @{N='SizeGB';E={[math]::Round($_.Size / 1GB, 1)}}, Status)
        NetworkAdapters = @(Get-CimInstance Win32_NetworkAdapter -Filter 'PhysicalAdapter=True' -ErrorAction Stop |
            Select-Object Name, NetEnabled, MACAddress, Manufacturer)
        ProblemDevices = @(Get-CimInstance Win32_PnPEntity -Filter 'ConfigManagerErrorCode <> 0' -ErrorAction Stop |
            Select-Object Name, PNPDeviceID, ConfigManagerErrorCode)
    }
}

Export-ModuleMember -Function Test-WinFixAdmin,Get-WinFixSystemSummary,Test-WinFixPendingReboot,Get-WinFixHardwareInventory
