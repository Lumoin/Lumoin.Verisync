param(
    [int]$Workers = 4,
    [string]$ToolchainPath = $env:TLAPLUS_HOME
)

$ErrorActionPreference = "Stop"

# The toolchain is located rather than assumed, so a checkout carries no machine's directory layout:
# TLAPLUS_HOME or -ToolchainPath names the directory holding the wrappers, and without either they
# are taken from PATH.
function Resolve-TlaWrapper {
    param([string]$Base)

    # The wrapper's extension belongs to the operating system rather than to this repository, so each
    # shape is tried in turn and the first that resolves is taken.
    foreach ($candidate in "$Base.cmd", "$Base.bat", "$Base.sh", $Base) {
        $path = if ($ToolchainPath) { Join-Path $ToolchainPath $candidate } else { $candidate }
        if (Get-Command $path -ErrorAction SilentlyContinue) {
            return $path
        }
    }

    throw "No $Base wrapper was found. Set TLAPLUS_HOME to the TLA+ toolchain directory, pass -ToolchainPath, or put the wrappers on PATH."
}

$sany = Resolve-TlaWrapper "sany"
$tlc = Resolve-TlaWrapper "tlc"

$modules = @(
    ".\RaftElection.tla",
    ".\RaftLog.tla"
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
    @{ Config = ".\MCRaftElectionFilterFirst.cfg";       Spec = ".\RaftElection.tla"; Expect = "green" },

    # Each log pair is a negative and the positive it licenses, at identical
    # constants. The bound a pair runs at is the one its own negative goes red
    # at, so it cannot be shortened without making the pair meaningless. All four
    # quotient the state space by ServerSymmetry, which is what holds
    # MCRaftLogFigure8 to about ninety seconds instead of the forty minutes the
    # brute-force run over the same bound costs.
    @{ Config = ".\MCRaftLogVolatileReplicas.cfg";       Spec = ".\RaftLog.tla";      Expect = "red"; Invariant = "CommittedIsDurableAtAQuorum" },
    @{ Config = ".\MCRaftLogDurable.cfg";                Spec = ".\RaftLog.tla";      Expect = "green" },
    @{ Config = ".\MCRaftLogFigure8Withdrawn.cfg";       Spec = ".\RaftLog.tla";      Expect = "red"; Invariant = "LeaderCompleteness" },
    @{ Config = ".\MCRaftLogFigure8.cfg";                Spec = ".\RaftLog.tla";      Expect = "green" }
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
