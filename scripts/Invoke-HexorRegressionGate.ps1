#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('DiagnosticsStress', 'Concurrent', 'Sequential', 'FocusedWpf')][string]$Mode,
    [ValidateRange(1, 1000)][int]$Count = 1,
    [Parameter(Mandatory)][string]$ResultDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $root 'tests\bin\Release\net10.0-windows\win-x64\Hexor.Tests.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Build the test executable once before running this gate.' }
$null = New-Item -ItemType Directory -Path $ResultDirectory -Force
$ResultDirectory = (Resolve-Path -LiteralPath $ResultDirectory).Path
$resultPath = Join-Path $ResultDirectory 'result.json'
$dumpHelper = Join-Path $ResultDirectory 'Capture-HangDump.ps1'
if ((Test-Path -LiteralPath $resultPath) -or (Test-Path -LiteralPath $dumpHelper) -or
    @(Get-ChildItem -LiteralPath $ResultDirectory -File -Filter 'run-*').Count) {
    throw 'Gate evidence already exists; preserve it and choose a new result directory.'
}
$testHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path $executable) 'Hexor.Tests.dll') -Algorithm SHA256).Hash
$runs = [Collections.Generic.List[object]]::new()
$active = [Collections.Generic.List[object]]::new()
$failure = $null
$previousArtifactRoot = [Environment]::GetEnvironmentVariable('HEXOR_VALIDATION_ARTIFACTS', 'Process')

# Dumping runs in an independent, bounded helper so a native UI stall cannot
# prevent the parent from recording its checkpoint and terminating its own child.
$helperSource = @'
param([Parameter(Mandatory)][int]$TargetProcessId, [Parameter(Mandatory)][string]$DumpPath)
$ErrorActionPreference = 'Stop'
try {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class HexorHangDump {
    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MiniDumpWriteDump(IntPtr process, uint processId,
        IntPtr file, uint dumpType, IntPtr exception, IntPtr userStreams, IntPtr callback);
}
"@
    $target = [Diagnostics.Process]::GetProcessById($TargetProcessId)
    $stream = [IO.File]::Open($DumpPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        # Full memory (including managed/native heaps), handles, unloaded modules,
        # memory information and thread information for native/SOS stack analysis.
        if (-not [HexorHangDump]::MiniDumpWriteDump($target.Handle, [uint32]$TargetProcessId,
            $stream.SafeFileHandle.DangerousGetHandle(), 0x1826, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)) {
            throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
        }
    } finally { $stream.Dispose(); $target.Dispose() }
    Write-Output "Heap dump captured for PID $TargetProcessId."
    exit 0
} catch { Write-Error $_; exit 1 }
'@
$helperStream = [IO.File]::Open($dumpHelper, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try {
    $helperBytes = [Text.UTF8Encoding]::new($false).GetBytes($helperSource)
    $helperStream.Write($helperBytes, 0, $helperBytes.Length)
} finally { $helperStream.Dispose() }

function Start-Test([int]$Index) {
    $out = Join-Path $ResultDirectory "run-$Index.log"
    $err = Join-Path $ResultDirectory "run-$Index.err.log"
    if ((Test-Path -LiteralPath $out) -or (Test-Path -LiteralPath $err)) { throw "Preserve existing run $Index logs." }
    $arguments = switch ($Mode) {
        'DiagnosticsStress' { @('--stress-uia-diagnostics', "$Count") }
        'FocusedWpf' { @('--wpf-only') }
        default { @() }
    }
    # Real minimize/restore and rendering require a visible GUI process. Only the
    # independent dump helper is hidden.
    $parameters = @{ FilePath = $executable; PassThru = $true; RedirectStandardOutput = $out; RedirectStandardError = $err }
    if ($arguments.Count) { $parameters.ArgumentList = $arguments }
    $process = Start-Process @parameters
    $run = [pscustomobject]@{
        Process = $process; Index = $Index; Output = $out; Error = $err
        Clock = [Diagnostics.Stopwatch]::StartNew(); StartedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Reader = $null; PendingText = ''; LastOutputLine = ''; LastCheckpoint = $null
        LastProgressSeconds = 0.0; LastProgressUtc = [DateTimeOffset]::UtcNow.ToString('O'); ProgressTimeoutSeconds = 120
        TimedOut = $false; TimeoutKind = $null; AbortedDueToSiblingFailure = $false
        DumpPath = $null; DumpSucceeded = $false; DumpError = $null; DumpCaptureExitCode = $null; DumpCaptureTimedOut = $false
        TerminationError = $null
    }
    $active.Add($run)
    return $run
}
function Read-Progress($Run, [switch]$Final) {
    if (-not $Run.Reader) {
        if (-not (Test-Path -LiteralPath $Run.Output)) { return }
        $stream = [IO.File]::Open($Run.Output, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $Run.Reader = [IO.StreamReader]::new($stream)
    }
    $Run.PendingText += $Run.Reader.ReadToEnd()
    $lines = $Run.PendingText -split '\r?\n'
    $Run.PendingText = $lines[-1]
    $complete = $lines.Count - 1
    if ($Final -and $Run.PendingText.Length) { $complete = $lines.Count; $Run.PendingText = '' }
    for ($lineIndex = 0; $lineIndex -lt $complete; $lineIndex++) {
        $line = $lines[$lineIndex]
        if (-not $line.Length) { continue }
        $Run.LastOutputLine = $line
        $checkpoint = $line -match '^CHECKPOINT (?<label>.*?)\s+timeout=(?<seconds>[1-9]\d*)s\s*$'
        if ($checkpoint) {
            $Run.LastCheckpoint = $line
            $Run.ProgressTimeoutSeconds = [Math]::Min(600.0, [double]$Matches.seconds)
        }
        if ($checkpoint -or $line.StartsWith('PASS ')) {
            $Run.LastProgressSeconds = $Run.Clock.Elapsed.TotalSeconds
            $Run.LastProgressUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
    }
}
function Capture-HangDump($Run) {
    if ($Run.Process.HasExited) { return }
    $Run.DumpPath = Join-Path $ResultDirectory "run-$($Run.Index)-hang-heap.dmp"
    $dumpOut = Join-Path $ResultDirectory "run-$($Run.Index)-hang-dump.log"
    $dumpErr = Join-Path $ResultDirectory "run-$($Run.Index)-hang-dump.err.log"
    $helper = $null
    try {
        foreach ($path in @($Run.DumpPath, $dumpOut, $dumpErr)) {
            if (Test-Path -LiteralPath $path) { throw "Preserve existing dump evidence: $path" }
        }
        $helper = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -WindowStyle Hidden -PassThru `
            -ArgumentList @('-NoProfile', '-NonInteractive', '-File', "`"$dumpHelper`"", '-TargetProcessId', "$($Run.Process.Id)", '-DumpPath', "`"$($Run.DumpPath)`"") `
            -RedirectStandardOutput $dumpOut -RedirectStandardError $dumpErr
        if (-not $helper.WaitForExit(30000)) {
            $Run.DumpCaptureTimedOut = $true
            $helper.Kill($true)
            $null = $helper.WaitForExit(5000)
            throw 'Heap dump helper exceeded its 30-second timeout.'
        }
        $Run.DumpCaptureExitCode = $helper.ExitCode
        $Run.DumpSucceeded = $helper.ExitCode -eq 0 -and (Test-Path -LiteralPath $Run.DumpPath) -and (Get-Item -LiteralPath $Run.DumpPath).Length -gt 0
        if (-not $Run.DumpSucceeded) { throw "Heap dump helper exited with code $($helper.ExitCode); see $dumpErr" }
    } catch { $Run.DumpError = $_.Exception.Message }
    finally { if ($helper) { $helper.Dispose() } }
}
function Stop-Test($Run) {
    if ($Run.Process.HasExited) { return }
    Capture-HangDump $Run
    try {
        if (-not $Run.Process.HasExited) { $Run.Process.Kill($true) }
        if (-not $Run.Process.WaitForExit(5000)) { throw 'Owned test process did not exit within five seconds of termination.' }
    } catch { $Run.TerminationError = $_.Exception.Message }
}
function Complete-Test($Run) {
    Read-Progress $Run -Final
    $exited = $Run.Process.HasExited
    $code = if ($exited) { $Run.Process.ExitCode } else { $null }
    $summary = $Run.LastOutputLine
    $expected = if ($Mode -in @('DiagnosticsStress', 'FocusedWpf')) { '^ALL \d+ FOCUSED CHECKS PASSED$' } else { '^ALL \d+ CHECKS PASSED$' }
    $ok = $exited -and $code -eq 0 -and $summary -match $expected -and (Get-Item -LiteralPath $Run.Error).Length -eq 0 -and
        -not $Run.TimedOut -and -not $Run.AbortedDueToSiblingFailure -and -not $Run.TerminationError
    $runs.Add([pscustomobject]@{
        Run = $Run.Index; ProcessId = $Run.Process.Id; ExitCode = $code; Passed = $ok; Summary = $summary
        Output = $Run.Output; Error = $Run.Error; StartedUtc = $Run.StartedUtc; CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        ElapsedSeconds = [Math]::Round($Run.Clock.Elapsed.TotalSeconds, 3); LastCheckpoint = $Run.LastCheckpoint
        LastProgressUtc = $Run.LastProgressUtc; ProgressTimeoutSeconds = $Run.ProgressTimeoutSeconds
        TimedOut = $Run.TimedOut; TimeoutKind = $Run.TimeoutKind; AbortedDueToSiblingFailure = $Run.AbortedDueToSiblingFailure
        DumpPath = $Run.DumpPath; DumpSucceeded = $Run.DumpSucceeded; DumpError = $Run.DumpError
        DumpCaptureExitCode = $Run.DumpCaptureExitCode; DumpCaptureTimedOut = $Run.DumpCaptureTimedOut; TerminationError = $Run.TerminationError
    })
    if ($Run.Reader) { $Run.Reader.Dispose() }
    $Run.Process.Dispose()
    $null = $active.Remove($Run)
    Write-Host "Run $($Run.Index): exit=$code; $summary"
    return $ok
}
function Wait-Tests {
    while ($active.Count) {
        foreach ($run in $active.ToArray()) {
            Read-Progress $run
            if (-not $run.Process.HasExited) {
                $elapsed = $run.Clock.Elapsed.TotalSeconds
                if ($elapsed -ge 600 -or $elapsed - $run.LastProgressSeconds -ge $run.ProgressTimeoutSeconds) {
                    $run.TimedOut = $true
                    $run.TimeoutKind = if ($elapsed -ge 600) { 'TenMinuteSuiteLimit' } else { 'CheckpointProgressLimit' }
                    Stop-Test $run
                } else { continue }
            }
            $timedOut = $run.TimedOut
            $label = if ($run.LastCheckpoint) { $run.LastCheckpoint } else { $run.LastOutputLine }
            if (-not (Complete-Test $run)) {
                foreach ($sibling in $active.ToArray()) {
                    Read-Progress $sibling
                    if (-not $sibling.Process.HasExited) { $sibling.AbortedDueToSiblingFailure = $true; Stop-Test $sibling }
                    $null = Complete-Test $sibling
                }
                $reason = if ($timedOut) { 'timed out' } else { 'failed' }
                throw "Regression run $($run.Index) $reason at: $label"
            }
        }
        if ($active.Count) { Start-Sleep -Milliseconds 200 }
    }
}
try {
    [Environment]::SetEnvironmentVariable('HEXOR_VALIDATION_ARTIFACTS', (Join-Path $ResultDirectory 'artifacts'), 'Process')
    if ($Mode -eq 'Concurrent') {
        foreach ($index in 1..$Count) { $null = Start-Test $index }
        Wait-Tests
    } else {
        $runCount = if ($Mode -eq 'DiagnosticsStress') { 1 } else { $Count }
        foreach ($index in 1..$runCount) { $null = Start-Test $index; Wait-Tests }
    }
    if ($testHash -cne (Get-FileHash -LiteralPath (Join-Path (Split-Path $executable) 'Hexor.Tests.dll') -Algorithm SHA256).Hash) { throw 'Compiled tests changed during verification.' }
} catch { $failure = $_.Exception.Message }
finally {
    foreach ($run in $active.ToArray()) {
        $run.AbortedDueToSiblingFailure = $true
        Stop-Test $run
        $null = Complete-Test $run
    }
    [Environment]::SetEnvironmentVariable('HEXOR_VALIDATION_ARTIFACTS', $previousArtifactRoot, 'Process')
    $result = [ordered]@{
        Passed = -not $failure; Mode = $Mode; Count = $Count; TestDllSha256 = $testHash
        DefaultProgressTimeoutSeconds = 120; MaximumSuiteSeconds = 600; MaximumDumpCaptureSeconds = 30
        Runs = $runs.ToArray(); Failure = $failure
    } | ConvertTo-Json -Depth 6
    $resultStream = [IO.File]::Open($resultPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $resultBytes = [Text.UTF8Encoding]::new($false).GetBytes($result); $resultStream.Write($resultBytes, 0, $resultBytes.Length) }
    finally { $resultStream.Dispose() }
}
if ($failure) { throw $failure }
Write-Output "PASS $Mode gate; results: $resultPath"
