$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$rows = @(Get-CimInstance Win32_Service -OperationTimeoutSec 10 | ForEach-Object {
    [pscustomobject]@{ name=$_.Name; display_name=$_.DisplayName; binary_path=[string]$_.PathName; startup_type=$_.StartMode; account=$_.StartName }
})
ConvertTo-Json -InputObject $rows -Compress -Depth 5
