param([Parameter(Mandatory)]$Capture, [Parameter(Mandatory)][string]$Root)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$temporary=[IO.Path]::GetTempFileName()
try {
    $Capture | ConvertTo-Json -Depth 40 -Compress | Set-Content -LiteralPath $temporary -Encoding utf8
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $result=& python -X utf8 "$PSScriptRoot/score-fixture.py" --capture $temporary --truth-root $Root --sid $sid
    if ($LASTEXITCODE -ne 0) { throw 'Independent scorer failed' }
    return ($result | ConvertFrom-Json)
} finally { Remove-Item -LiteralPath $temporary -Force }
