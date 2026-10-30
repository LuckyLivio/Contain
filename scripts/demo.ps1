param(
    [switch]$Release, [switch]$KeepArtifacts, [switch]$RequireEtw, [switch]$SnapshotOnly,
    [string]$OutputDirectory, [switch]$RegistryNoise,
    [string]$ContainExecutable, [string]$FixtureExecutable, [string]$BuildManifest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
if ($RequireEtw -and $SnapshotOnly) { throw 'Choose RequireEtw or SnapshotOnly, not both.' }
# A distributed package is self-identifying; source checkouts keep their Cargo entrypoint.
if (-not $ContainExecutable -and -not $FixtureExecutable -and -not $BuildManifest -and
    (Test-Path -LiteralPath (Join-Path $PSScriptRoot '../BUILD.json'))) {
    $BuildManifest=Join-Path $PSScriptRoot '../BUILD.json'
    $ContainExecutable=Join-Path $PSScriptRoot '../contain.exe'
    $FixtureExecutable=Join-Path $PSScriptRoot '../contain-test-installer.exe'
}
$prebuilt = [bool]($ContainExecutable -or $FixtureExecutable -or $BuildManifest)
if ($prebuilt -and (-not $ContainExecutable -or -not $FixtureExecutable -or -not $BuildManifest)) { throw 'Supply ContainExecutable, FixtureExecutable and BuildManifest together.' }
if ($prebuilt -and $RegistryNoise) { throw 'The bounded prebuilt demo does not include the optional registry stress fixture.' }
if ($prebuilt -and $Release) { throw 'The prebuilt package profile is fixed by its build manifest.' }
if ($prebuilt) {
    # A trial leaves its database/evidence available for inspection. The source
    # development entrypoint retains its existing explicit KeepArtifacts switch.
    $KeepArtifacts=$true
    if (-not $OutputDirectory) { $OutputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('contain-trial-'+[guid]::NewGuid().ToString('N')) }
}
Push-Location (Join-Path $PSScriptRoot '..')
. "$PSScriptRoot/demo-provenance.ps1"
. "$PSScriptRoot/demo-checks.ps1"
. "$PSScriptRoot/demo-save-capture.ps1"
$previousTruth=$env:CONTAIN_FIXTURE_TRUTH
$runIdentity=$null
$identityPath=$null
$prebuiltIdentity=$null
$clockRequested=[bool]$env:CONTAIN_ETW_RAW_CLOCK_PATH
$checks=[Collections.Generic.List[object]]::new()
$observation=@{ document=$null; manifest=$null; score=$null; clock=$null }
# A prerequisite failure leaves declared checks visibly not_run instead of silently absent.
foreach ($checkName in @('capture_execution','independent_fixture','detached_fixture','registry_noise_fixture',
    'inspect_json','capture_finished','source_loss_accounting','independent_score','view_consistency',
    'process_family_count','installer_certain','descendant_ancestry','independent_process_excluded',
    'file_modified','file_deleted','registry_modified','unrelated_state_unknown','providers_active','source_events',
    'file_high_source','independent_source_unknown','snapshot_state_unknown','failed_delete_preserved','rename_identity',
    'registry_noise_unknown','truth_count','independent_source_not_promoted','scorer_attribution','short_actor',
    'detached_actor','detached_after_exit','history_stability','explain_json','diff_json','history_json','doctor_json','dry_run_unchanged')) {
    $checks.Add([ordered]@{ name=$checkName; domain='product'; required=$true; status='not_run'; reason='not_reached' })
}
$checks.Add([ordered]@{ name='clock_diagnostic'; domain='diagnostic'; required=$clockRequested; status='not_run'; reason='not_reached' })
$plannedCheckNames=@($checks | ForEach-Object name)
function Assert-JsonContract([string]$text) {
    if (-not (Get-Command Test-Json -ErrorAction SilentlyContinue)) { throw 'PowerShell 7 Test-Json is required for the demo JSON checks.' }
    if (-not (Test-Json -Json $text -SchemaFile './docs/schema/cli-v3.schema.json')) { throw 'JSON schema validation failed' }
}
try {
    if ($OutputDirectory) {
        $OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
        if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) { throw 'OutputDirectory must be empty; historical evidence will not be overwritten' }
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
        $identityPath=Join-Path $OutputDirectory 'run-identity.json'
    }
    if ($clockRequested -and -not $OutputDirectory) { throw 'Clock diagnostics require OutputDirectory so failed evidence is retained.' }
    if ($prebuilt) {
        $prebuiltIdentity=Get-DemoPrebuiltExecutables -ContainExecutable $ContainExecutable -FixtureExecutable $FixtureExecutable -BuildManifest $BuildManifest
        $buildExecutables=$prebuiltIdentity.executables
        $buildSource=[ordered]@{ head=$prebuiltIdentity.manifest.source_sha; status=@(); runtime_tree_sha256=$prebuiltIdentity.manifest.runtime_tree_sha256; identity_origin='packaged_build_manifest' }
        if ($OutputDirectory) {
            $runIdentity=[ordered]@{
                schema_version=2; status='selected'; started_at_utc=[DateTime]::UtcNow.ToString('o')
                build_mode='prebuilt'; build_executed=$false; build_command=@(); build_exit_code=$null
                cargo_version=$null; rustc_version=$null; source_before_build=$buildSource; source_after_build=$buildSource
                source_stable_during_build=$null; source_stable_during_selection=$true
                prebuilt_manifest_path=$prebuiltIdentity.manifest_path; prebuilt_manifest_sha256=$prebuiltIdentity.manifest_sha256
                prebuilt_manifest_verified=$true; prebuilt_manifest_after_sha256=$null; prebuilt_manifest_unchanged=$null
                build_config=[ordered]@{ target=$prebuiltIdentity.manifest.target; profile=$prebuiltIdentity.manifest.profile }
                binary_before=@(); binary_after=$null; fixture_clock=$env:CONTAIN_FIXTURE_CLOCK; raw_clock_path=$env:CONTAIN_ETW_RAW_CLOCK_PATH
                elevated=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
                interpretation='Manifest-bound prebuilt selection and pre/post executable observations. No local compilation or Git lookup; no cryptographic loaded-image attestation.'
            }
        }
    } else {
        $cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
        if (-not (Test-Path -LiteralPath $cargo)) { $cargo = (Get-Command cargo -ErrorAction Stop).Source }
        $buildArgs=@('build','--workspace','--locked','--message-format=json-render-diagnostics');if($Release){$buildArgs+='--release'}
        if ($OutputDirectory) {
            $rustc=Join-Path (Split-Path $cargo) 'rustc.exe'
            if (-not (Test-Path -LiteralPath $rustc)) { $rustc='rustc' }
            $runIdentity=[ordered]@{
                schema_version=2; status='building'; started_at_utc=[DateTime]::UtcNow.ToString('o')
                build_mode='cargo'; build_executed=$true; source_before_build=(Get-DemoSourceIdentity); source_after_build=$null
                build_command=@($cargo)+$buildArgs; build_exit_code=$null
                cargo_version=((& $cargo --version) -join "`n"); rustc_version=((& $rustc --version --verbose) -join "`n")
                source_stable_during_build=$null; binary_before=@(); binary_after=$null
                fixture_clock=$env:CONTAIN_FIXTURE_CLOCK; raw_clock_path=$env:CONTAIN_ETW_RAW_CLOCK_PATH
                elevated=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
                interpretation='Contemporaneous build/source and pre/post executable observations, not a cryptographic loaded-image attestation.'
            }
        }
        $buildOutput=[Collections.Generic.List[string]]::new()
        $buildErrorPreference=$ErrorActionPreference
        try {
            $ErrorActionPreference='Continue'
            if ($OutputDirectory) {
                & $cargo @buildArgs 2>&1 | ForEach-Object -ErrorAction Stop { $line=[string]$_; $buildOutput.Add($line); $line } |
                    Tee-Object -FilePath (Join-Path $OutputDirectory 'build.log') -ErrorAction Stop
            } else {
                & $cargo @buildArgs 2>&1 | ForEach-Object -ErrorAction Stop { $line=[string]$_; $buildOutput.Add($line); $line }
            }
            $buildExit=$LASTEXITCODE
        } finally { $ErrorActionPreference=$buildErrorPreference }
        if ($runIdentity) { $runIdentity.build_exit_code=$buildExit }
        if ($buildExit -ne 0) { throw 'cargo build failed' }
        $buildExecutables=Get-DemoBuildExecutables -BuildOutput $buildOutput.ToArray() -RegistryNoise:$RegistryNoise
        if ($runIdentity) {
            $runIdentity.source_after_build=Get-DemoSourceIdentity
            $runIdentity.source_stable_during_build=$runIdentity.source_before_build.runtime_tree_sha256 -ceq $runIdentity.source_after_build.runtime_tree_sha256
            if (-not $runIdentity.source_stable_during_build) { throw 'Build inputs changed during build; refuse to launch fixture' }
        }
    }
    $name = 'contain-demo-' + [guid]::NewGuid().ToString('N')
    $root = Join-Path $env:TEMP $name
    $db = Join-Path $env:TEMP ($name + '.db')
    $truthRoot=Join-Path $env:TEMP ($name+'-truth')
    New-Item -ItemType Directory -Path $truthRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $truthRoot '.contain-demo-marker'),'Contain test fixture')
    $env:CONTAIN_FIXTURE_TRUTH=$truthRoot
    $key = 'Software\Contain\Demo\' + $name
    $registryPath = 'Registry::HKEY_CURRENT_USER\' + $key
    $contain = $buildExecutables['contain']
    $fixture = $buildExecutables['contain-test-installer']
    if ($runIdentity) {
        $runIdentity.binary_before=@(Get-DemoBinaryIdentity $contain; Get-DemoBinaryIdentity $fixture)
        if ($RegistryNoise) { $runIdentity.binary_before+=Get-DemoBinaryIdentity $buildExecutables['contain-registry-noise'] }
        $runIdentity.status='running'
        $runIdentity['fixture_root']=$root; $runIdentity['database']=$db; $runIdentity['truth_root']=$truthRoot
        $runIdentity | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $identityPath -Encoding utf8
    }
    New-Item -ItemType Directory -Path $root | Out-Null
    [IO.File]::WriteAllText((Join-Path $root '.contain-demo-marker'), 'Contain test fixture')
    foreach ($file in @('settings.txt','rename-me.txt','delete-me.txt')) { [IO.File]::WriteAllText((Join-Path $root $file), 'baseline') }
    New-Item -Path $registryPath -Force | Out-Null
    New-ItemProperty -LiteralPath $registryPath -Name Installed -Value 'baseline' -PropertyType String | Out-Null
    $unrelated = Start-Process -FilePath $fixture -ArgumentList @('--role','unrelated','--root',('"' + $root + '"')) -PassThru -WindowStyle Hidden
    $registryNoiseProcesses=@()
    try {
        if($RegistryNoise) {
            $noiseBin=$buildExecutables['contain-registry-noise']
            foreach($phase in @('pre','launcher')) {
                $registryNoiseProcesses += Start-Process -WindowStyle Hidden -FilePath $noiseBin -ArgumentList @('--root',('"'+$root+'"'),'--phase',$phase) -PassThru
            }
            $noiseDeadline=[DateTime]::UtcNow.AddSeconds(10)
            while(-not ((Test-Path -LiteralPath (Join-Path $root '.registry-pre-ready')) -and (Test-Path -LiteralPath (Join-Path $root '.registry-launcher-ready')))) {
                if([DateTime]::UtcNow -gt $noiseDeadline){throw 'registry noise readiness failed'}
                Start-Sleep -Milliseconds 10
            }
        }
        $arguments = @('--db',$db,'install',$fixture,'--name','TestFixture','--watch',$root,'--registry-key',$key)
        if ($SnapshotOnly) { $arguments += '--no-etw' }
        $arguments += @('--','--root',$root)
        if ($runIdentity) {
            $runIdentity['capture_command']=@($contain)+$arguments
            $runIdentity | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $identityPath -Encoding utf8
        }
        $captureClock=[Diagnostics.Stopwatch]::StartNew()
        if ($OutputDirectory) {
            $captureCommandPath=Join-Path $OutputDirectory 'capture-command.json'
            ConvertTo-Json -InputObject (@($contain)+$arguments) | Set-Content -LiteralPath $captureCommandPath -Encoding utf8
            python "$PSScriptRoot/demo-measure.py" --command $captureCommandPath --output $OutputDirectory --database $db
            $captureExit=$LASTEXITCODE
            Get-Content -LiteralPath (Join-Path $OutputDirectory 'capture.stdout.log')
            Get-Content -LiteralPath (Join-Path $OutputDirectory 'capture.stderr.log') | Write-Output
        } else {
            & $contain @arguments
            $captureExit=$LASTEXITCODE
        }
        $captureClock.Stop()
        Invoke-DemoCheck -Checks $checks -Name capture_execution -Action {
            if ($captureExit -ne 0) { throw 'capture failed; process measurements and logs were retained' }
        }
        Invoke-DemoCheck -Checks $checks -Name independent_fixture -Action {
            if (-not $unrelated.WaitForExit(10000)) { throw 'independent fixture timed out' }
            if ($unrelated.ExitCode -ne 0) { throw 'independent fixture failed' }
        }
        Invoke-DemoCheck -Checks $checks -Name detached_fixture -Ready ($captureExit -eq 0) -UnavailableReason 'capture_failed' -Action {
            $deadline=[DateTime]::UtcNow.AddSeconds(15)
            while (-not (Test-Path -LiteralPath (Join-Path $root '.detached-done'))) {
                if ([DateTime]::UtcNow -gt $deadline) { throw 'detached fixture did not finish' }
                Start-Sleep -Milliseconds 50
            }
        }
        Invoke-DemoCheck -Checks $checks -Name registry_noise_fixture -Required ([bool]$RegistryNoise) -Ready ([bool]$RegistryNoise) -UnavailableReason 'not_requested' -Action {
            foreach($noiseProcess in $registryNoiseProcesses) {
                if(-not $noiseProcess.WaitForExit(21000) -or $noiseProcess.ExitCode -ne 0){throw 'bounded registry noise failed'}
            }
        }
        Invoke-DemoCheck -Checks $checks -Name inspect_json -Action {
            $raw=(& $contain --db $db inspect TestFixture --json) -join "`n"
            if ($LASTEXITCODE -ne 0) { throw 'inspect failed' }
            Assert-JsonContract $raw
            $document=$raw | ConvertFrom-Json
            if ($document.schema_version -ne 3 -or $document.kind -ne 'inspect') { throw 'inspect JSON contract failed' }
            $observation.document=$document; $observation.manifest=$document.data
            if ($OutputDirectory) {
                Save-DemoCaptureJson -Json $raw -Path (Join-Path $OutputDirectory 'capture.json') -SchemaPath './docs/schema/cli-v3.schema.json'
            }
        }
        $manifest=$observation.manifest
        $manifestReady=$null -ne $manifest
        Invoke-DemoCheck -Checks $checks -Name capture_finished -Ready $manifestReady -UnavailableReason 'inspect_unavailable' -Action {
            if ($manifest.capture_state -ne 'finished') { throw 'Session did not finish; retained evidence is provisional' }
            # Finished is a recording lifecycle result, not a claim of Complete coverage.
            Write-Output ("BACKEND: " + ($manifest.backend | ConvertTo-Json -Compress -Depth 15))
        }
        Invoke-DemoCheck -Checks $checks -Name source_loss_accounting -Ready ($manifestReady -and $RequireEtw) -Required ([bool]$RequireEtw) -UnavailableReason 'inspect_unavailable_or_etw_not_required' -Action {
            foreach($field in @('dropped_events','context_losses','decode_errors','etw_events_lost','etw_buffers_lost')) {
                $value=$manifest.backend.$field
                if ($null -eq $value -or $value -is [bool] -or $value -ne 0) { throw "Missing or nonzero source counter: $field" }
            }
            if ($null -eq $manifest.stats.events_dropped -or $manifest.stats.events_dropped -ne 0 -or $manifest.stats.drain_timed_out -ne $false) { throw 'Loss or drain accounting unavailable/nonzero' }
            foreach ($pair in @(@('pipeline','queue_overflow'),@('pipeline','enqueue_disconnected'),@('pipeline','queue_pending'),
                @('pipeline','context_evictions'),@('pipeline','retention_dropped'),@('pipeline','failed_without_output'),
                @('handoff','overflow'),@('handoff','oversized'),@('handoff','disconnected'),@('handoff','serialize_failed'),@('handoff','pending'),
                @('stream','failed'),@('stream','quota_dropped'))) {
                $value=$manifest.backend.($pair[0]).($pair[1])
                if ($null -eq $value -or $value -is [bool] -or $value -ne 0) { throw "Missing or nonzero bounded evidence counter: $($pair -join '.')" }
            }
            if ($manifest.backend.handoff.receiver_finished -ne $true -or $manifest.backend.handoff.writer_finished -ne $true -or $null -ne $manifest.backend.stream.error) { throw 'Evidence pipeline did not finish cleanly' }
        }
        # Save the frozen score independently of all later diagnostic/product checks.
        Invoke-DemoCheck -Checks $checks -Name independent_score -Ready $manifestReady -UnavailableReason 'inspect_unavailable' -Action {
            $observation.score=& "$PSScriptRoot/score-fixture.ps1" -Capture $manifest -Root $truthRoot
            if ($OutputDirectory) {
                $observation.score | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'reliability.json') -Encoding utf8
                Copy-Item -LiteralPath (Join-Path $truthRoot 'ground-truth.json') -Destination $OutputDirectory
            }
        }
        $score=$observation.score
        $scoreReady=$null -ne $score
        Invoke-DemoCheck -Checks $checks -Name clock_diagnostic -Domain diagnostic -Required $clockRequested -Ready ($clockRequested -and $manifestReady -and $scoreReady) -UnavailableReason 'not_requested_or_capture_truth_unavailable' -Action {
            $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            $clockReport=Join-Path $OutputDirectory 'clock-diagnostic.json'
            python "$PSScriptRoot/v04-clock-protocol.py" --capture (Join-Path $OutputDirectory 'capture.json') --truth (Join-Path $OutputDirectory 'ground-truth.json') --marker-root $truthRoot --raw-clock $env:CONTAIN_ETW_RAW_CLOCK_PATH --sid $sid --output $clockReport --exit-contract diagnostic-v1
            $clockExit=$LASTEXITCODE
            if ($runIdentity) { $runIdentity['clock_diagnostic_exit_code']=$clockExit }
            if (Test-Path -LiteralPath $clockReport) { $observation.clock=Get-Content -Raw -LiteralPath $clockReport | ConvertFrom-Json }
            if ($clockExit -ne 0) { throw 'Clock diagnostic execution or supplied-view evidence failed; independent product checks continue' }
            if ($null -eq $observation.clock -or $observation.clock.result_contract.execution.status -ne 'completed' -or $observation.clock.result_contract.evidence.status -ne 'complete') { throw 'Clock diagnostic did not provide complete scoped results' }
        }
        # These checks depend on capture/readback, not on a successful clock diagnostic.
        Invoke-DemoCheck -Checks $checks -Name view_consistency -Ready $manifestReady -UnavailableReason 'inspect_unavailable' -Action {
            $viewReport=if($OutputDirectory){Join-Path $OutputDirectory 'view-consistency.json'}else{$db+'.views.json'}
            python "$PSScriptRoot/v04-verify-views.py" --contain $contain --database $db --app TestFixture --output $viewReport --page-size 2
            if($LASTEXITCODE -ne 0){throw 'Cross-CLI evidence consistency failed; see view-consistency report'}
        }
        Invoke-DemoCheck -Checks $checks -Name process_family_count -Ready $manifestReady -Action {
            if ($manifest.processes.Count -lt 3) { throw 'parent/child/grandchild were not all observed' }
        }
        Invoke-DemoCheck -Checks $checks -Name installer_certain -Ready $manifestReady -Action {
            if (@($manifest.processes | Where-Object confidence -eq 'Certain').Count -ne 1) { throw 'installer must be Certain' }
        }
        Invoke-DemoCheck -Checks $checks -Name descendant_ancestry -Ready $manifestReady -Action {
            if (@($manifest.processes | Where-Object confidence -eq 'High').Count -lt 2) { throw 'child and grandchild must have verified ancestry' }
        }
        Invoke-DemoCheck -Checks $checks -Name independent_process_excluded -Ready $manifestReady -Action {
            if (@($manifest.processes | Where-Object pid -eq $unrelated.Id).Count -ne 0) { throw 'independent process was attached to installer' }
        }
        Invoke-DemoCheck -Checks $checks -Name file_modified -Ready $manifestReady -Action {
            if (@($manifest.files | Where-Object operation -eq 'modified').Count -lt 1) { throw 'file modification not captured' }
        }
        Invoke-DemoCheck -Checks $checks -Name file_deleted -Ready $manifestReady -Action {
            if (@($manifest.files | Where-Object operation -eq 'deleted').Count -lt 1) { throw 'file deletion not captured' }
        }
        Invoke-DemoCheck -Checks $checks -Name registry_modified -Ready $manifestReady -Action {
            if (@($manifest.registry | Where-Object operation -eq 'modified').Count -lt 1) { throw 'registry update not captured' }
        }
        Invoke-DemoCheck -Checks $checks -Name unrelated_state_unknown -Ready $manifestReady -Action {
            $unrelatedChanges=@($manifest.files | Where-Object { $_.path -like '*\unrelated.txt' -or $_.path -like '*\shared.txt' })
            if ($unrelatedChanges.Count -lt 2 -or @($unrelatedChanges | Where-Object confidence -ne 'Unknown').Count -gt 0) { throw 'unrelated/mixed writer state was misattributed' }
        }
        Invoke-DemoCheck -Checks $checks -Name providers_active -Required ([bool]$RequireEtw) -Ready ($RequireEtw -and $manifestReady) -UnavailableReason 'inspect_unavailable_or_etw_not_required' -Action {
            if ($manifest.backend.etw_file -ne 'active' -or $manifest.backend.etw_registry -ne 'active' -or $manifest.backend.etw_process -ne 'active') { throw 'ETW file, registry and process providers were required but not all active' }
        }
        Invoke-DemoCheck -Checks $checks -Name source_events -Required ([bool]$RequireEtw) -Ready ($RequireEtw -and $manifestReady) -UnavailableReason 'inspect_unavailable_or_etw_not_required' -Action {
            if (@($manifest.events | Where-Object event_type -eq 'file').Count -eq 0 -or @($manifest.events | Where-Object event_type -eq 'registry').Count -eq 0) { throw 'ETW requires actual file and registry source events; provider activation alone is insufficient' }
        }
        $etwActive=$manifestReady -and $manifest.backend.etw_file -eq 'active'
        Invoke-DemoCheck -Checks $checks -Name file_high_source -Required ([bool]($RequireEtw -or $etwActive)) -Ready $etwActive -UnavailableReason 'file_provider_unavailable' -Action {
            if (@($manifest.events | Where-Object { $_.event_type -eq 'file' -and $_.confidence -eq 'High' }).Count -eq 0) { throw 'no real High-confidence file source event captured' }
        }
        Invoke-DemoCheck -Checks $checks -Name independent_source_unknown -Required ([bool]($RequireEtw -or $etwActive)) -Ready $etwActive -UnavailableReason 'file_provider_unavailable' -Action {
            if (@($manifest.events | Where-Object { $_.resource -like '*\unrelated.txt' -and $_.event_type -eq 'file' -and $_.confidence -eq 'Unknown' }).Count -eq 0) { throw 'independent ETW writer was not observed as Unknown' }
        }
        Invoke-DemoCheck -Checks $checks -Name snapshot_state_unknown -Required (-not $etwActive) -Ready ($manifestReady -and -not $etwActive) -UnavailableReason 'not_applicable_or_inspect_unavailable' -Action {
            if (@($manifest.files | Where-Object confidence -ne 'Unknown').Count -ne 0) { throw 'snapshot fallback overstated attribution' }
        }
        Invoke-DemoCheck -Checks $checks -Name failed_delete_preserved -Action {
            if (-not (Test-Path -LiteralPath (Join-Path $root 'locked.txt'))) { throw 'failed delete was treated as success' }
        }
        Invoke-DemoCheck -Checks $checks -Name rename_identity -Ready $manifestReady -Action {
            if (@($manifest.operations | Where-Object { $_.operation -eq 'Renamed' -and $_.resource -like '*\renamed.txt' }).Count -ne 1) { throw 'stable file identity rename was not recovered' }
        }
        Invoke-DemoCheck -Checks $checks -Name registry_noise_unknown -Ready $manifestReady -Action {
            $noiseRegistry=@($manifest.registry | Where-Object name -eq 'NoiseOnly')
            if ($noiseRegistry.Count -ne 1 -or $noiseRegistry[0].confidence -ne 'Unknown') { throw 'independent registry state was misattributed' }
        }
        Invoke-DemoCheck -Checks $checks -Name truth_count -Ready $scoreReady -UnavailableReason 'score_unavailable' -Action {
            $expected=if($RegistryNoise){2021}else{21}
            if ($score.expected -ne $expected) { throw "ground truth incomplete: expected $expected instrumented operations" }
        }
        Invoke-DemoCheck -Checks $checks -Name independent_source_not_promoted -Ready $manifestReady -Action {
            if (@($manifest.events | Where-Object { $_.evidence.pid -eq $unrelated.Id -and $_.confidence -in @('High','Certain') }).Count -gt 0) { throw 'independent source actor received application attribution' }
        }
        Invoke-DemoCheck -Checks $checks -Name scorer_attribution -Ready $scoreReady -UnavailableReason 'score_unavailable' -Action {
            if ($score.incorrect_attribution -ne 0 -or $score.false_positive_target_events -ne 0) { throw 'false attribution detected by unchanged scorer' }
        }
        foreach($role in @('short','detached')) {
            Invoke-DemoCheck -Checks $checks -Name ($role+'_actor') -Required ([bool]$RequireEtw) -Ready ($RequireEtw -and $scoreReady) -UnavailableReason 'score_unavailable_or_etw_not_required' -Action {
                if (@($score.rows | Where-Object { $_.role -eq $role -and $_.outcome -eq 'Correct' }).Count -eq 0) { throw "$role actor was not correctly attributed" }
            }
        }
        Invoke-DemoCheck -Checks $checks -Name detached_after_exit -Required ([bool]$RequireEtw) -Ready ($RequireEtw -and $manifestReady) -UnavailableReason 'inspect_unavailable_or_etw_not_required' -Action {
            $rootExit=@($manifest.events | Where-Object operation -eq 'installer_exited')[0]
            $detached=@($manifest.events | Where-Object { $_.event_type -eq 'file' -and $_.resource -like '*\detached.txt' -and $_.confidence -eq 'High' })
            if (@($detached | Where-Object { [uint64]$_.timestamp_ticks -gt [uint64]$rootExit.timestamp_ticks }).Count -eq 0) { throw 'detached write after root exit not captured' }
        }
        Invoke-DemoCheck -Checks $checks -Name history_stability -Ready $manifestReady -Action {
            $firstHistory=(& $contain --db $db history TestFixture --json) -join "`n"
            if ($LASTEXITCODE -ne 0) { throw 'first history read failed' }
            $secondHistory=(& $contain --db $db history TestFixture --json) -join "`n"
            if ($LASTEXITCODE -ne 0 -or $firstHistory -cne $secondHistory) { throw 'history ordering was not stable across readback' }
        }
        Invoke-DemoCheck -Checks $checks -Name explain_json -Ready $manifestReady -Action {
            $eventId=$manifest.events[0].id
            $explanation=(& $contain --db $db explain $eventId --json) -join "`n"
            if ($LASTEXITCODE -ne 0) { throw 'explain failed' }
            Assert-JsonContract $explanation
        }
        foreach ($command in @('diff','history')) {
            Invoke-DemoCheck -Checks $checks -Name ($command+'_json') -Ready $manifestReady -Action {
                $raw=(& $contain --db $db $command TestFixture --json) -join "`n"
                if ($LASTEXITCODE -ne 0) { throw "$command failed" }
                Assert-JsonContract $raw
                $json=$raw | ConvertFrom-Json
                if ($json.schema_version -ne 3 -or $json.kind -ne $command) { throw "$command JSON contract failed" }
            }
        }
        Invoke-DemoCheck -Checks $checks -Name doctor_json -Action {
            # The companion destination belongs only to the completed capture.
            $captureRawClock=$env:CONTAIN_ETW_RAW_CLOCK_PATH
            try {
                Remove-Item Env:CONTAIN_ETW_RAW_CLOCK_PATH -ErrorAction SilentlyContinue
                $raw=(& $contain doctor --json) -join "`n"
                if ($LASTEXITCODE -ne 0) { throw 'doctor probe failed' }
            } finally { $env:CONTAIN_ETW_RAW_CLOCK_PATH=$captureRawClock }
            Assert-JsonContract $raw
            $doctor=$raw | ConvertFrom-Json
            if ($doctor.kind -ne 'doctor' -or -not $doctor.data.process_observation) { throw 'doctor probe failed' }
        }
        Invoke-DemoCheck -Checks $checks -Name dry_run_unchanged -Ready $manifestReady -Action {
            $registryHandle=Get-Item -LiteralPath $registryPath
            try { $before=Get-DemoObservedState -Root $root -RegistryKey $registryHandle } finally { $registryHandle.Close() }
            & $contain --db $db remove TestFixture --dry-run
            if ($LASTEXITCODE -ne 0) { throw 'dry-run failed' }
            $registryHandle=Get-Item -LiteralPath $registryPath
            try { $after=Get-DemoObservedState -Root $root -RegistryKey $registryHandle } finally { $registryHandle.Close() }
            if ($before -cne $after) { throw 'dry-run changed fixture files, directories or registry values/types' }
        }
        $result=Get-DemoCheckSummary -Checks $checks -ClockRequested $clockRequested -ClockResult $observation.clock -PlannedCheckNames $plannedCheckNames
        if ($OutputDirectory) { $result | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'product-checks.json') -Encoding utf8 }
        Write-Output ("BOUNDED DEMO: " + ($result.product | ConvertTo-Json -Compress) + '; formal acceptance: requirements_unmet')
        if (-not $result.bounded_pass) {
            $failure=[InvalidOperationException]::new('Bounded demo failed or required checks were not executed; all available results were retained')
            $failure.Data['demo_results_recorded']=$true
            throw $failure
        }
        if ($runIdentity) { $runIdentity.status='completed' }
    } finally {
        foreach($noiseProcess in $registryNoiseProcesses) {
            # Bounded fixture exits itself. Do not terminate it to manufacture a passing test.
            if(-not $noiseProcess.HasExited){$null=$noiseProcess.WaitForExit(21000)}
            if(-not $noiseProcess.HasExited){throw 'noise did not honor deadline; retain fixture for investigation'}
        }
        if (-not $unrelated.HasExited) { $unrelated.Kill(); $unrelated.WaitForExit() }
        if (-not $KeepArtifacts) {
            & $fixture --root $root --cleanup
            if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $registryPath)) { throw 'fixture cleanup failed' }
            & $fixture --root $truthRoot --cleanup
            if ($LASTEXITCODE -ne 0) { throw 'truth cleanup failed' }
            foreach ($suffix in @('','-wal','-shm','.views.json')) { $target=$db+$suffix; if(Test-Path -LiteralPath $target){ Remove-Item -LiteralPath $target -Force } }
        } else { Write-Output "Fixture: $root`nDatabase: $db`nTruth: $truthRoot" }
    }
} catch {
    if (-not $_.Exception.Data.Contains('demo_results_recorded')) {
        $checks.Add([ordered]@{ name='demo_runtime'; domain='product'; required=$true; status='failed'; reason=$_.Exception.Message })
    }
    if ($runIdentity) { $runIdentity.status='failed'; $runIdentity['error']=$_.Exception.Message }
    throw
} finally {
    try {
        $identityFailed=$false
        if ($runIdentity) {
            $runIdentity['finished_at_utc']=[DateTime]::UtcNow.ToString('o')
            if ($runIdentity.binary_before.Count -gt 0) {
                $runIdentity.binary_after=Complete-DemoBinaryIdentity $runIdentity.binary_before
                if (-not $runIdentity.binary_after.unchanged) { $runIdentity.status='binary_changed' }
            }
            if ($prebuiltIdentity) {
                try {
                    $afterPackage=Get-DemoPrebuiltExecutables -ContainExecutable $ContainExecutable -FixtureExecutable $FixtureExecutable -BuildManifest $BuildManifest
                    $runIdentity.prebuilt_manifest_after_sha256=$afterPackage.manifest_sha256
                    $runIdentity.prebuilt_manifest_unchanged=$afterPackage.manifest_sha256 -ceq $prebuiltIdentity.manifest_sha256
                    if (-not $runIdentity.prebuilt_manifest_unchanged) { $runIdentity.status='binary_changed' }
                } catch {
                    $runIdentity.prebuilt_manifest_unchanged=$false
                    $runIdentity.status='binary_changed'
                    $runIdentity['package_identity_error']=$_.Exception.Message
                }
            }
            $runIdentity | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $identityPath -Encoding utf8
            $identityFailed=$runIdentity.status -eq 'binary_changed'
            if ($identityFailed) {
                $checks.Add([ordered]@{ name='package_identity'; domain='product'; required=$true; status='failed'; reason='Executable or packaged inputs changed during demo' })
            }
        }
        if ($OutputDirectory -and (Test-Path -LiteralPath $OutputDirectory)) {
            Get-DemoCheckSummary -Checks $checks -ClockRequested $clockRequested -ClockResult $observation.clock -PlannedCheckNames $plannedCheckNames |
                ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'product-checks.json') -Encoding utf8
        }
        if ($identityFailed) { throw 'Executable changed or disappeared during demo; retained evidence cannot pass identity checks' }
    } finally { $env:CONTAIN_FIXTURE_TRUTH=$previousTruth; Pop-Location }
}
