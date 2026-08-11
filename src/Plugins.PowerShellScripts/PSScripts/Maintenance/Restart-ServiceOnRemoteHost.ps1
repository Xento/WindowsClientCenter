param(
    [Parameter(Mandatory = $true)]
    [string]$ComputerName,

    [Parameter(Mandatory = $true)]
    [string]$ServiceName,

    [switch]$PassThru
)

$service = Get-Service -ComputerName $ComputerName -Name $ServiceName -ErrorAction Stop

if ($service.Status -eq 'Running')
{
    Write-Host "Stopping service '$ServiceName' on '$ComputerName'..."
    $service.Stop()
    $service.WaitForStatus('Stopped', '00:00:30')
}

Write-Host "Starting service '$ServiceName' on '$ComputerName'..."
$service.Start()
$service.WaitForStatus('Running', '00:00:30')

$updatedService = Get-Service -ComputerName $ComputerName -Name $ServiceName -ErrorAction Stop

if ($PassThru)
{
    $updatedService
}
else
{
    [pscustomobject]@{
        ComputerName = $ComputerName
        ServiceName  = $updatedService.Name
        DisplayName  = $updatedService.DisplayName
        Status       = $updatedService.Status
    } | Format-List
}
