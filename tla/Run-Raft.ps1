param(
    [int]$Workers = 4
)

$ErrorActionPreference = "Stop"

$sany = "C:\tools\tlaplus\sany.cmd"
$tlc = "C:\tools\tlaplus\tlc.cmd"

$modules = @(
    ".\RaftElection.tla"
)

# Expected 'red' runs are the negative models; a green negative means the model
# lost its defect and the whole run fails. A red run must also name the
# invariant the configuration claims to break, because a negative that goes red
# on something else has stopped testing what it was written for.
$runs = @(
    @{ Config = ".\MCRaftElectionClosed.cfg";            Spec = ".\RaftElection.tla"; Expect = "green" },
    @{ Config = ".\MCRaftElectionOutsiderUnguarded.cfg"; Spec = ".\RaftElection.tla"; Expect = "red"; Invariant = "ElectionSafety" },
    @{ Config = ".\MCRaftElectionOutsiderGuarded.cfg";   Spec = ".\RaftElection.tla"; Expect = "green" },
    @{ Config = ".\MCRaftElectionTermInflation.cfg";     Spec = ".\RaftElection.tla"; Expect = "red"; Invariant = "NoTermInflation" },
    @{ Config = ".\MCRaftElectionFilterFirst.cfg";       Spec = ".\RaftElection.tla"; Expect = "green" }
)

Push-Location $PSScriptRoot
try {
    foreach ($module in $modules) {
        Write-Host "=== SANY $module ==="
        & $sany $module
        if ($LASTEXITCODE -ne 0) {
            throw "SANY failed for $module"
        }
    }

    $failures = @()
    foreach ($run in $runs) {
        Write-Host "=== TLC $($run.Config) (expected: $($run.Expect)) ==="
        # Each configuration gets its own metadata directory. Two TLC runs
        # sharing one collide, and the collision looks like a spec error.
        $meta = Join-Path ".\states" ([IO.Path]::GetFileNameWithoutExtension($run.Config))
        $captured = & $tlc -workers $Workers -checkpoint 0 -metadir $meta -config $run.Config $run.Spec 2>&1
        $code = $LASTEXITCODE
        $captured | ForEach-Object { Write-Host $_ }

        # TLC reports 0 for no error, 12 for a safety violation and 13 for a liveness violation.
        $outcome = if ($code -eq 0) { "green" } elseif ($code -in 12, 13) { "red" } else { "error($code)" }
        if ($outcome -ne $run.Expect) {
            $failures += "$($run.Config): expected $($run.Expect), got $outcome"
        }
        elseif ($outcome -eq "red" -and $run.ContainsKey("Invariant")) {
            $wanted = "Invariant $($run.Invariant) is violated"
            if (-not ($captured | Select-String -SimpleMatch $wanted -Quiet)) {
                $failures += "$($run.Config): red, but not on $($run.Invariant)"
            }
        }

        Write-Host "--- $($run.Config): $outcome ---`n"
    }

    if ($failures.Count -gt 0) {
        Write-Host "UNEXPECTED OUTCOMES:" -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "The model-check matrix deviated from its pinned expectations."
    }

    Write-Host "The full matrix matches its pinned expectations." -ForegroundColor Green
    # The last TLC run may be an expected violation, so its exit code must not leak as ours.
    exit 0
}
finally {
    Pop-Location
}
