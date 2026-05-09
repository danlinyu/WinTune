# Async.psm1 -- runspace-backed background ops with progress queues.
#
# Why this exists: long operations (dedup scan, cache cleanup, diagnostics)
# previously ran synchronously on the WPF dispatcher thread, freezing the
# window for seconds-to-minutes. This module wraps them in a background
# PowerShell runspace and exposes a thread-safe progress queue the UI thread
# polls via DispatcherTimer.
#
# Pattern (from a WPF event handler):
#
#     $script:CleanupOp = Start-AsyncOp `
#         -Script {
#             param($targets, $modulePath, $Progress)
#             Import-Module $modulePath -Force
#             & $Progress @{ msg = "starting"; pct = 0 }
#             Invoke-Cleanup -Targets $targets
#         } `
#         -Arguments @{ targets = $targets; modulePath = $modPath }
#
#     $poller = New-Object System.Windows.Threading.DispatcherTimer
#     $poller.Interval = [TimeSpan]::FromMilliseconds(200)
#     $poller.Add_Tick({
#         foreach ($p in Receive-AsyncProgress $script:CleanupOp) {
#             $ui.StatusLabel.Text = $p.msg
#         }
#         if (Test-AsyncOpComplete $script:CleanupOp) {
#             $poller.Stop()
#             $r = Receive-AsyncOp $script:CleanupOp
#             # ... handle $r.Success / $r.Result / $r.Error
#         }
#     })
#     $poller.Start()
#
# A user-supplied $Progress closure is injected automatically and enqueues
# onto a System.Collections.Concurrent.ConcurrentQueue safe for cross-thread
# producer/consumer. The user script invokes & $Progress with whatever
# payload it wants.

function Start-AsyncOp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock]$Script,
        [hashtable]$Arguments = @{}
    )

    $progressQueue = [System.Collections.Concurrent.ConcurrentQueue[object]]::new()

    # Wrapper passes through the user's args plus an injected $Progress
    # closure that enqueues onto the shared queue.
    $wrapper = {
        param($UserScript, $UserArgs, $Queue)
        $argsWithProgress = @{}
        foreach ($k in $UserArgs.Keys) { $argsWithProgress[$k] = $UserArgs[$k] }
        $argsWithProgress['Progress'] = { param($info) $Queue.Enqueue($info) }.GetNewClosure()
        & $UserScript @argsWithProgress
    }

    $ps = [PowerShell]::Create()
    [void]$ps.AddScript($wrapper).
        AddArgument($Script).
        AddArgument($Arguments).
        AddArgument($progressQueue)

    $async = $ps.BeginInvoke()

    [pscustomobject]@{
        PowerShell    = $ps
        Async         = $async
        ProgressQueue = $progressQueue
    }
}

function Stop-AsyncOp {
    # Best-effort cancellation. Stops the runspace pipeline; in-flight cmdlets
    # may need to reach a checkpoint before noticing.
    param($Op)
    if (-not $Op) { return }
    try { $Op.PowerShell.Stop() } catch {}
    try { $Op.PowerShell.Dispose() } catch {}
}

function Test-AsyncOpComplete {
    param($Op)
    if (-not $Op) { return $true }
    [bool]$Op.Async.IsCompleted
}

function Receive-AsyncProgress {
    # Drains all queued progress payloads. Returns array (possibly empty).
    # Unary-comma wraps so PowerShell's return-value unwrapping doesn't turn
    # an empty array back into $null.
    param($Op)
    if (-not $Op) { return ,@() }
    $items = New-Object System.Collections.ArrayList
    $msg = $null
    while ($Op.ProgressQueue.TryDequeue([ref]$msg)) { [void]$items.Add($msg) }
    return ,$items.ToArray()
}

function Receive-AsyncOp {
    # Call exactly once after Test-AsyncOpComplete returns true. Disposes the
    # underlying PowerShell instance.
    param($Op)
    if (-not $Op) { return [pscustomobject]@{ Success = $false; Result = $null; Error = 'No op' } }
    try {
        $r = $Op.PowerShell.EndInvoke($Op.Async)
        return [pscustomobject]@{ Success = $true; Result = $r; Error = $null }
    } catch {
        return [pscustomobject]@{ Success = $false; Result = $null; Error = $_.Exception.Message }
    } finally {
        try { $Op.PowerShell.Dispose() } catch {}
    }
}

Export-ModuleMember -Function Start-AsyncOp, Stop-AsyncOp, Test-AsyncOpComplete, Receive-AsyncProgress, Receive-AsyncOp
