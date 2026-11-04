param([string]$InputPath, [string]$OutputPath, [string]$SchemaPath)

# Shared by the production demo and the offline Windows report preflight.
# Preserve validated JSON field expressions, including date strings and integers.
# Encoding is UTF-8 without BOM. Physical CRLF becomes LF; a final LF is appended
# only when absent. Existing trailing whitespace and escaped string text remain.
function Save-DemoCaptureJson {
    param(
        [Parameter(Mandatory)][string]$Json,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$SchemaPath
    )
    if (-not (Get-Command Test-Json -ErrorAction SilentlyContinue)) {
        throw 'PowerShell 7 Test-Json is required for capture saving.'
    }
    if (-not (Test-Json -Json $Json -SchemaFile $SchemaPath -ErrorAction Stop)) {
        throw 'Capture JSON schema validation failed'
    }
    $text=$Json.Replace("`r`n", "`n")
    if (-not $text.EndsWith("`n", [StringComparison]::Ordinal)) { $text+="`n" }
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference='Stop'
    if (-not $InputPath -or -not $OutputPath -or -not $SchemaPath) {
        throw 'Supply InputPath, OutputPath and SchemaPath together.'
    }
    $json=[IO.File]::ReadAllText($InputPath, [Text.UTF8Encoding]::new($false, $true))
    Save-DemoCaptureJson -Json $json -Path $OutputPath -SchemaPath $SchemaPath
}
