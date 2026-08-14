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
    ".\QuePaxaAbstract.tla",
    ".\QuePaxaConcrete.tla",
    ".\QuePaxaDurable.tla",
    ".\QuePaxaTornWrite.tla",
    ".\QuePaxaMembership.tla"
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
    @{ Config = ".\MCConcreteSingleLeaderDeclaredSchedule.cfg"; Spec = ".\QuePaxaConcrete.tla"; Expect = "green" },
    @{ Config = ".\MCConcreteMixedLeaderlessWideDowngrade.cfg";   Spec = ".\QuePaxaConcrete.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCConcreteMixedLeaderlessNarrowDowngrade.cfg"; Spec = ".\QuePaxaConcrete.tla"; Expect = "green" },
    @{ Config = ".\MCConcreteSplitLeadersWideDowngrade.cfg";      Spec = ".\QuePaxaConcrete.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCConcreteSplitLeadersNarrowDowngrade.cfg";    Spec = ".\QuePaxaConcrete.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCDurableRecorder.cfg";        Spec = ".\QuePaxaDurable.tla"; Expect = "green" },
    @{ Config = ".\MCDurableVolatileReply.cfg";   Spec = ".\QuePaxaDurable.tla"; Expect = "red"; Invariant = "ServedFirstIsStable" },
    @{ Config = ".\MCDurableFreshRestart.cfg";    Spec = ".\QuePaxaDurable.tla"; Expect = "red"; Invariant = "ServedFirstIsStable" },
    @{ Config = ".\MCDurablePartialPersist.cfg";  Spec = ".\QuePaxaDurable.tla"; Expect = "red"; Invariant = "ServedPriorAggregateIsStable" },
    @{ Config = ".\MCDurableStepZeroServed.cfg";  Spec = ".\QuePaxaDurable.tla"; Expect = "red"; Invariant = "StepZeroDurableStateWasNeverServed" },
    @{ Config = ".\MCTornAtomic.cfg";             Spec = ".\QuePaxaTornWrite.tla"; Expect = "green" },
    @{ Config = ".\MCTornAtomicUnvalidated.cfg";  Spec = ".\QuePaxaTornWrite.tla"; Expect = "green" },
    @{ Config = ".\MCTornRefusedTear.cfg";        Spec = ".\QuePaxaTornWrite.tla"; Expect = "red"; Invariant = "DurableStateIsRestorable" },
    @{ Config = ".\MCTornAcceptedTear.cfg";       Spec = ".\QuePaxaTornWrite.tla"; Expect = "red"; Invariant = "DurableRestoreKeepsEveryAnswer" },
    @{ Config = ".\MCTornRestoreLoses.cfg";       Spec = ".\QuePaxaTornWrite.tla"; Expect = "red"; Invariant = "ServedFirstIsStable" },
    @{ Config = ".\MCTornPriorAggregate.cfg";     Spec = ".\QuePaxaTornWrite.tla"; Expect = "red"; Invariant = "ServedPriorAggregateIsStable" },
    @{ Config = ".\MCTornDurableShape.cfg";       Spec = ".\QuePaxaTornWrite.tla"; Expect = "red"; Invariant = "TypeOK" },
    @{ Config = ".\MCTornStepZeroPremise.cfg";    Spec = ".\QuePaxaTornWrite.tla"; Expect = "green" },
    @{ Config = ".\MCTornUnguardedRestore.cfg";   Spec = ".\QuePaxaTornWrite.tla"; Expect = "red"; Invariant = "RegisterHoldsOnlyRecorderStates" },
    @{ Config = ".\MCTornFabricated.cfg";         Spec = ".\QuePaxaTornWrite.tla"; Expect = "red"; Invariant = "DurableRestoreKeepsEveryAnswer" },
    @{ Config = ".\MCMembershipShipped.cfg";           Spec = ".\QuePaxaMembership.tla"; Expect = "green" },
    @{ Config = ".\MCMembershipDisjointChange.cfg";    Spec = ".\QuePaxaMembership.tla"; Expect = "green" },
    @{ Config = ".\MCMembershipRemovedProposer.cfg";   Spec = ".\QuePaxaMembership.tla"; Expect = "green" },
    @{ Config = ".\MCMembershipGuardedGenesis.cfg";    Spec = ".\QuePaxaMembership.tla"; Expect = "green" },
    @{ Config = ".\MCMembershipLocalConfig.cfg";       Spec = ".\QuePaxaMembership.tla"; Expect = "red"; Invariant = "OneConfigurationPerInstance" },
    @{ Config = ".\MCMembershipSplitGenesis.cfg";      Spec = ".\QuePaxaMembership.tla"; Expect = "red"; Invariant = "Agreement" },
    @{ Config = ".\MCMembershipCrossClusterQuorum.cfg"; Spec = ".\QuePaxaMembership.tla"; Expect = "red"; Invariant = "NoCrossClusterDecision" },
    @{ Config = ".\MCMembershipServesAWindow.cfg";     Spec = ".\QuePaxaMembership.tla"; Expect = "red"; Invariant = "DecisionsCountOnlyCaughtUpHosts" },
    @{ Config = ".\MCMembershipOutsiderCounted.cfg";   Spec = ".\QuePaxaMembership.tla"; Expect = "red"; Invariant = "DecisionsCountOnlyMembers" },
    @{ Config = ".\MCMembershipEagerDecommission.cfg"; Spec = ".\QuePaxaMembership.tla"; Expect = "red"; Invariant = "LatestDecisionSurvives" }
)

Push-Location $PSScriptRoot
try {
    # A module that extends another inherits the parent's SPECIFICATION name, so
    # a torn-write configuration naming Spec instead of TornSpec would silently
    # model-check the atomic parent and pass for the wrong reason. The check is
    # here rather than in review because nothing else would ever catch it: the
    # misdirected greens are required to reproduce the parent's counts anyway.
    foreach ($cfg in Get-ChildItem ".\MCTorn*.cfg") {
        if (-not (Select-String -Path $cfg -SimpleMatch "SPECIFICATION TornSpec" -Quiet)) {
            throw "$($cfg.Name) must name SPECIFICATION TornSpec."
        }
    }

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
