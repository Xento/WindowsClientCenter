$processes = Get-Process |
    Sort-Object -Property CPU -Descending |
    Select-Object -First 10 Name, Id, CPU, WorkingSet64, StartTime

$processes | Format-Table -AutoSize
