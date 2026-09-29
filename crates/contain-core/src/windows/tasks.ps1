$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$service = New-Object -ComObject 'Schedule.Service'
$service.Connect()
function Read-Folder($folder) {
    foreach ($task in $folder.GetTasks(1)) {
        $actions = @($task.Definition.Actions | ForEach-Object {
            if ($_.Type -eq 0) { [pscustomobject]@{ executable=[string]$_.Path; arguments=[string]$_.Arguments } }
            else { [pscustomobject]@{ executable=('NonExecAction:' + $_.Type); arguments='' } }
        })
        [xml]$xml = $task.Xml
        $triggers = @($xml.Task.Triggers.ChildNodes | Where-Object NodeType -eq Element | ForEach-Object { $_.OuterXml })
        [pscustomobject]@{ path=$task.Path; actions=$actions; triggers=$triggers; enabled=[bool]$task.Enabled }
    }
    foreach ($child in $folder.GetFolders(0)) { Read-Folder $child }
}
$rows = @(Read-Folder ($service.GetFolder('\')))
ConvertTo-Json -InputObject $rows -Compress -Depth 8
