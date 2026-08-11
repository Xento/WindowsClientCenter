param(
    [Parameter(Mandatory = $true)]
    [string]$ComputerName
)

$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ComputerName $ComputerName
$computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem -ComputerName $ComputerName
$bios = Get-CimInstance -ClassName Win32_BIOS -ComputerName $ComputerName

[pscustomobject]@{
    ComputerName     = $ComputerName
    Manufacturer     = $computerSystem.Manufacturer
    Model            = $computerSystem.Model
    UserName         = $computerSystem.UserName
    LastBootUpTime   = $operatingSystem.LastBootUpTime
    TotalMemoryGB    = [math]::Round($computerSystem.TotalPhysicalMemory / 1GB, 2)
    SerialNumber     = $bios.SerialNumber
    OperatingSystem  = $operatingSystem.Caption
    OperatingVersion = $operatingSystem.Version
} | Format-List
