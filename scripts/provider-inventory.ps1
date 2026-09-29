param([string]$OutputDirectory='./target/provider-inventory')
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$selection=@{'Microsoft-Windows-Kernel-File'=@(12,14,16,24,26,27,30);'Microsoft-Windows-Kernel-Process'=@(1,2,3,4);'Microsoft-Windows-Kernel-Registry'=@(1,2,3,5,6,13)}
$inventory=@(foreach($name in $selection.Keys){
    $p=Get-WinEvent -ListProvider $name
    [ordered]@{name=$name;guid=$p.Id;keywords=@($p.Keywords|Select-Object Name,Value);selected_events=@($p.Events|Where-Object Id -in $selection[$name]|Select-Object Id,Version,Level,Task,Keywords,Template)}
})
[ordered]@{os=[Environment]::OSVersion.VersionString;providers=$inventory;note='Read-only manifest metadata; actual filter efficacy is measured with filter-on/off captures. File Close only has FILEIO keyword, so clearing that keyword would discard required context.'}|ConvertTo-Json -Depth 12|Set-Content (Join-Path $OutputDirectory 'providers.json') -Encoding utf8
