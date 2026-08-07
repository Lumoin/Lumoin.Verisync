param(
    [int]$Workers = 4
)

$ErrorActionPreference = "Stop"

$sany = "C:\tools\tlaplus\sany.cmd"
$tlc = "C:\tools\tlaplus\tlc.cmd"

$modules = @(
    ".\QuePaxaAbstract.tla",
    ".\QuePaxaConcrete.tla"
)

# Expected 'red' runs are the negative models; a green negative means the model
# lost its defect and the whole run fails. A red run must also name the
# invariant the configuration claims to break, because a negative that goes red
# on something else has stopped testing what it was written for.
$runs = @(
    @{ Config = ".\MCAbstractSafety.cfg";               Spec = ".\QuePaxaAbstract.tla"; Expect = "green" },
    @{ Config = ".\MCAbstractCrash.cfg";                Spec = ".\QuePaxaAbstract.tla"; Expect = "green" },
    @{ Config = ".\MCAbstractTieDetection.cfg";         Spec = ".\QuePaxaAbstract.tla"; Expect = "green" },
    @{ Config = ".\MCAbstractSweep.cfg";                Spec = ".\QuePaxaAbstract.tla"; Expect = "green" },
    @{ Config = ".\MCAbstractNoUniversalWitness.cfg";   Spec = ".\QuePaxaAbstract.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCAbstractDecideOnCommon.cfg";       Spec = ".\QuePaxaAbstract.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCAbstractCarryExistent.cfg";        Spec = ".\QuePaxaAbstract.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCAbstractTiedPriorities.cfg";       Spec = ".\QuePaxaAbstract.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCConcreteSingleLeader.cfg";         Spec = ".\QuePaxaConcrete.tla"; Expect = "green" },
    @{ Config = ".\MCConcreteTwoLeaders.cfg";           Spec = ".\QuePaxaConcrete.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCConcreteIdenticalKeyOnly.cfg";     Spec = ".\QuePaxaConcrete.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCConcreteFirstClaimBindsOnly.cfg";  Spec = ".\QuePaxaConcrete.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCConcreteConfiguredLeaderOnly.cfg"; Spec = ".\QuePaxaConcrete.tla"; Expect = "green" },
    @{ Config = ".\MCConcreteTwoLeadersGuarded.cfg";    Spec = ".\QuePaxaConcrete.tla"; Expect = "green" },
    @{ Config = ".\MCConcreteDeclaredScheduleBinds.cfg";      Spec = ".\QuePaxaConcrete.tla"; Expect = "green" },
    @{ Config = ".\MCConcreteSingleLeaderDeclaredSchedule.cfg"; Spec = ".\QuePaxaConcrete.tla"; Expect = "green" }
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
