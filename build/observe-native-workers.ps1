<#
.SYNOPSIS
Records retained identities and liveness of one Windows application's app-local MKVToolNix children.
.DESCRIPTION
Read-only process observation: no UI input, process control or media changes. WorkerAlive records are
status-read intervals, never operator-action timestamps. Correlate native input separately; an idle
or successfully stopped observer does not establish cancellation acceptance. Requires PowerShell 7.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int] $GuiProcessId,
    [Parameter(Mandatory)][string] $ExpectedApplicationPath,
    [Parameter(Mandatory)][string] $ExpectedProductVersion,
    [Parameter(Mandatory)][string] $ToolDirectory,
    [Parameter(Mandatory)][string] $EvidencePath,
    [string] $StopMarkerPath,
    [ValidateRange(1, 3600)][int] $MaximumSeconds = 300,
    [ValidateRange(20, 1000)][int] $PollMilliseconds = 25,
    [ValidateRange(50, 5000)][int] $LivenessMilliseconds = 100
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$maximumEvidenceBytes = 2MB
$maximumWorkers = 128
$identityToleranceMilliseconds = 1
$workers = @{}
$guiProcess = $null
$evidenceWriter = $null
$evidenceStream = $null

if (-not $IsWindows) {
    throw 'Native worker observation requires Windows and PowerShell 7.'
}

$applicationPath = (Resolve-Path -LiteralPath $ExpectedApplicationPath).ProviderPath
$toolsPath = (Resolve-Path -LiteralPath $ToolDirectory).ProviderPath
$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
$stopFullPath = if ([string]::IsNullOrWhiteSpace($StopMarkerPath)) { $null } else { [IO.Path]::GetFullPath($StopMarkerPath) }
$expectedTools = @{
    'mkvmerge.exe' = Join-Path $toolsPath 'mkvmerge.exe'
    'mkvextract.exe' = Join-Path $toolsPath 'mkvextract.exe'
}

if ($stopFullPath -and (Test-Path -LiteralPath $stopFullPath)) {
    throw 'Use a fresh stop-marker path; existing markers are never removed.'
}

if ([Diagnostics.FileVersionInfo]::GetVersionInfo($applicationPath).ProductVersion -ne $ExpectedProductVersion) {
    throw 'The application ProductVersion does not match the explicit expected version.'
}

foreach ($toolPath in $expectedTools.Values) {

    if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
        throw "Expected app-local tool is missing: $toolPath"
    }
}

## Appends bounded JSONL evidence and flushes it for an independent operator/reader.
function Write-Evidence {
    param([hashtable] $Record)
    $Record.ObservedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $line = $Record | ConvertTo-Json -Depth 5 -Compress

    if ($evidenceStream.Position + [Text.Encoding]::UTF8.GetByteCount($line) + 1 -gt $maximumEvidenceBytes) {
        throw 'The 2 MiB evidence limit was reached; observation is incomplete.'
    }

    $evidenceWriter.WriteLine($line)
    $evidenceWriter.Flush()
}

## Distinguishes the production private-candidate remux from probes and extraction.
function Get-InvocationKind {
    param([object] $Child)
    $commandLine = [string] $Child.CommandLine

    if ($Child.Name -ieq 'mkvmerge.exe') {
        $isProbe = $commandLine -match '(?i)(?:^|\s)(?:--version|-V|-J|--identify|-i|--identification-format)(?:\s|$)'

        if ($isProbe) {
            return 'IdentificationOrVersionProbe'
        }

        $hasGuiMode = $commandLine -match '(?i)(?:^|\s)--gui-mode(?:\s|$)'
        $hasPrivateOutput = $commandLine -match '(?i)(?:^|\s)--output\s+(?:"[^"]*\.tracktrim-[0-9a-f]{32}[\\/]candidate\.mkv"|[^\s"]*\.tracktrim-[0-9a-f]{32}[\\/]candidate\.mkv)(?:\s|$)'

        if ($hasGuiMode -and $hasPrivateOutput) {
            return 'RemuxCandidate'
        }

        return 'OtherMkvmerge'
    }

    $mode = [regex]::Match($commandLine, '(?i)(?:^|\s)(tracks|timestamps_v2|chapters|tags|attachments|cues)(?:\s|$)').Groups[1].Value

    if ($mode) {
        return "Extract:$mode"
    }

    return 'OtherMkvextract'
}

## Reads exit evidence from the retained process instance rather than a later PID lookup.
function Record-WorkerExit {
    param([object] $Worker)

    if ($Worker.ExitRecorded -or -not $Worker.Process.HasExited) {
        return
    }

    Write-Evidence @{ Event = 'WorkerExited'; WorkerKey = $Worker.Key; ProcessId = $Worker.Process.Id;
        InvocationKind = $Worker.Kind; ExitUtc = $Worker.Process.ExitTime.ToUniversalTime().ToString('o'); ExitCode = $Worker.Process.ExitCode }
    $Worker.ExitRecorded = $true
}

try {
    $guiMetadata = Get-CimInstance Win32_Process -Filter "ProcessId = $GuiProcessId"

    if ($null -eq $guiMetadata) {
        throw 'The requested application PID is unavailable.'
    }

    $guiProcess = [Diagnostics.Process]::GetProcessById($GuiProcessId)
    $null = $guiProcess.SafeHandle
    $guiStartUtc = $guiProcess.StartTime.ToUniversalTime()
    $guiImage = if ($guiMetadata.ExecutablePath) { $guiMetadata.ExecutablePath } else { $guiProcess.MainModule.FileName }
    $matchesPath = [string]::Equals($guiImage, $applicationPath, [StringComparison]::OrdinalIgnoreCase)
    $matchesStart = [Math]::Abs(($guiStartUtc - $guiMetadata.CreationDate.ToUniversalTime()).TotalMilliseconds) -le $identityToleranceMilliseconds
    $invalidGuiIdentity = -not $matchesPath -or -not $matchesStart -or $guiProcess.HasExited

    if ($invalidGuiIdentity) {
        throw 'The application path/start identity changed before handle retention.'
    }

    $evidenceStream = [IO.FileStream]::new($evidenceFullPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $evidenceWriter = [IO.StreamWriter]::new($evidenceStream, [Text.UTF8Encoding]::new($false))
    $evidenceWriter.NewLine = "`n"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $lastLivenessMilliseconds = -$LivenessMilliseconds
    $guiExitRecorded = $false
    $discoveryFailed = $false
    $reason = 'DurationLimit'
    Write-Evidence @{ Event = 'ObserverStarted'; GuiProcessId = $GuiProcessId; GuiStartUtc = $guiStartUtc.ToString('o');
        ApplicationPath = $applicationPath; ProductVersion = $ExpectedProductVersion; ToolDirectory = $toolsPath;
        PollMilliseconds = $PollMilliseconds; LivenessMilliseconds = $LivenessMilliseconds; MaximumSeconds = $MaximumSeconds;
        OperatorInputTimestampsRecorded = $false; Scope = 'Read-only process evidence; no native action or cancellation pass is implied.' }

    while ($timer.Elapsed.TotalSeconds -lt $MaximumSeconds) {

        if ($stopFullPath -and (Test-Path -LiteralPath $stopFullPath)) {
            $reason = 'OwnerStopMarker'
            break
        }

        foreach ($worker in @($workers.Values)) {
            Record-WorkerExit $worker
        }

        if ($guiProcess.HasExited -and -not $guiExitRecorded) {
            Write-Evidence @{ Event = 'GuiExited'; ExitUtc = $guiProcess.ExitTime.ToUniversalTime().ToString('o'); ExitCode = $guiProcess.ExitCode }
            $guiExitRecorded = $true
        }

        $children = @()

        if (-not $guiProcess.HasExited) {

            try {
                $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $GuiProcessId AND (Name = 'mkvmerge.exe' OR Name = 'mkvextract.exe')")
                $discoveryFailed = $false
            }
            catch {

                if (-not $discoveryFailed) {
                    Write-Evidence @{ Event = 'DiscoveryFailed'; Detail = $_.Exception.Message }
                    $discoveryFailed = $true
                }
            }
        }

        foreach ($child in $children) {
            $metadataKey = '{0}:{1}' -f $child.ProcessId, $child.CreationDate.ToUniversalTime().Ticks

            if ($workers.ContainsKey($metadataKey)) {
                continue
            }

            if ($workers.Count -ge $maximumWorkers) {
                throw 'The 128-worker evidence bound was reached; observation is incomplete.'
            }

            $workerProcess = $null

            try {
                $workerProcess = [Diagnostics.Process]::GetProcessById([int] $child.ProcessId)
                $null = $workerProcess.SafeHandle
                $startUtc = $workerProcess.StartTime.ToUniversalTime()
                $image = if ($child.ExecutablePath) { $child.ExecutablePath } else { $workerProcess.MainModule.FileName }
                $matchesTool = [string]::Equals($image, $expectedTools[[string] $child.Name], [StringComparison]::OrdinalIgnoreCase)
                $matchesStart = [Math]::Abs(($startUtc - $child.CreationDate.ToUniversalTime()).TotalMilliseconds) -le $identityToleranceMilliseconds

                if (-not $matchesTool -or -not $matchesStart) {
                    throw 'Worker path/start identity does not match the discovered app-local child.'
                }

                $worker = [pscustomobject]@{ Key = ('{0}:{1}' -f $child.ProcessId, $startUtc.Ticks); Process = $workerProcess;
                    Kind = Get-InvocationKind $child; ExitRecorded = $false }
            }
            catch {

                if ($null -ne $workerProcess) {
                    $workerProcess.Dispose()
                }

                Write-Evidence @{ Event = 'DiscoveryRaceOrReadFailure'; ProcessId = $child.ProcessId; Detail = $_.Exception.Message }
                continue
            }

            $workers[$metadataKey] = $worker
            Write-Evidence @{ Event = 'WorkerFirstSeen'; WorkerKey = $worker.Key; ProcessId = $workerProcess.Id;
                ParentProcessId = $child.ParentProcessId; ExecutablePath = $image; CommandLine = [string] $child.CommandLine;
                StartUtc = $startUtc.ToString('o'); HasExited = $workerProcess.HasExited; InvocationKind = $worker.Kind }
            Record-WorkerExit $worker
        }

        if ($timer.Elapsed.TotalMilliseconds - $lastLivenessMilliseconds -ge $LivenessMilliseconds) {
            $lastLivenessMilliseconds = $timer.Elapsed.TotalMilliseconds

            foreach ($worker in @($workers.Values | Where-Object { $_.Kind -eq 'RemuxCandidate' -and -not $_.ExitRecorded })) {
                $readStartedUtc = [DateTimeOffset]::UtcNow.ToString('o')
                $hasExited = $worker.Process.HasExited
                $readCompletedUtc = [DateTimeOffset]::UtcNow.ToString('o')

                if (-not $hasExited) {
                    Write-Evidence @{ Event = 'WorkerAlive'; WorkerKey = $worker.Key; ProcessId = $worker.Process.Id;
                        InvocationKind = $worker.Kind; HasExited = $false; ReadStartedUtc = $readStartedUtc; ReadCompletedUtc = $readCompletedUtc }
                }

                Record-WorkerExit $worker
            }
        }

        Start-Sleep -Milliseconds $PollMilliseconds
    }

    foreach ($worker in @($workers.Values)) {
        Record-WorkerExit $worker

        if (-not $worker.Process.HasExited) {
            Write-Evidence @{ Event = 'WorkerAliveAtObserverStop'; WorkerKey = $worker.Key; ProcessId = $worker.Process.Id; InvocationKind = $worker.Kind }
        }
    }

    Write-Evidence @{ Event = 'ObserverStopped'; Reason = $reason; KnownWorkerCount = $workers.Count; GuiHasExited = $guiProcess.HasExited }
}
finally {
    foreach ($worker in @($workers.Values)) {
        $worker.Process.Dispose()
    }

    if ($null -ne $guiProcess) {
        $guiProcess.Dispose()
    }

    if ($null -ne $evidenceWriter) {
        $evidenceWriter.Dispose()
    }
    elseif ($null -ne $evidenceStream) {
        $evidenceStream.Dispose()
    }
}
