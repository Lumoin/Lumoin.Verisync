param(
    [int]$Workers = 4,
    [string]$ToolchainPath = $env:TLAPLUS_HOME,
    [string]$Tla2ToolsJar = $env:TLA2TOOLS_JAR
)

$ErrorActionPreference = "Stop"

# The toolchain is located rather than assumed, so a checkout carries no machine's directory layout.
# TLAPLUS_HOME or -ToolchainPath names the directory holding tla2tools.jar, and TLA2TOOLS_JAR or
# -Tla2ToolsJar names the jar itself, for an installation that keeps it somewhere other than beside
# the rest of the toolchain.
function Resolve-Tla2ToolsJar {
    if ($Tla2ToolsJar) {
        if (-not (Test-Path -LiteralPath $Tla2ToolsJar -PathType Leaf)) {
            throw "TLA2TOOLS_JAR or -Tla2ToolsJar names '$Tla2ToolsJar', which is not a file."
        }

        return (Resolve-Path -LiteralPath $Tla2ToolsJar).Path
    }

    if ($ToolchainPath) {
        $candidate = Join-Path $ToolchainPath "tla2tools.jar"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "No tla2tools.jar was found. Set TLAPLUS_HOME or pass -ToolchainPath naming the directory that holds tla2tools.jar, or set TLA2TOOLS_JAR or pass -Tla2ToolsJar naming the jar itself."
}

# The executable carries whatever extension the operating system gives it, so both shapes are tried
# in the directory a candidate names.
function Resolve-JavaExecutable {
    param([string]$BinDirectory)

    foreach ($name in "java.exe", "java") {
        $candidate = Join-Path $BinDirectory $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

# The runtime bundled with the toolchain is taken first, JAVA_HOME second and java on PATH last, and
# that order is load-bearing rather than a preference. A machine can carry an older java on PATH for
# something else that the current toolchain does not run on, and a script that silently picked it
# would fail inside the jar in a way that reads as a specification error rather than as the missing
# prerequisite it is.
function Resolve-Java {
    if ($ToolchainPath) {
        $bundled = Resolve-JavaExecutable (Join-Path (Join-Path $ToolchainPath "jre") "bin")
        if ($bundled) {
            return $bundled
        }
    }

    if ($env:JAVA_HOME) {
        $declared = Resolve-JavaExecutable (Join-Path $env:JAVA_HOME "bin")
        if ($declared) {
            return $declared
        }
    }

    $onPath = Get-Command "java" -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) {
        return $onPath.Source
    }

    throw "No java executable was found. The toolchain ships one under its own jre directory, so naming the toolchain is normally enough; otherwise set JAVA_HOME or put a java the toolchain runs on into PATH."
}

$jar = Resolve-Tla2ToolsJar
$java = Resolve-Java

# The heap bound belongs to the run rather than to the machine, so TLA_JAVA_HEAP is read here and
# defaults to what the pinned matrices need. A value naming more than one flag is split, because java
# takes each flag as its own argument.
$heap = if ($env:TLA_JAVA_HEAP) { $env:TLA_JAVA_HEAP } else { "-Xmx2g" }
$heapArguments = @($heap -split "\s+" | Where-Object { $_ })

# Both tools are entered by main class over an explicit class path rather than through the jar
# manifest, because SANY is not the manifest's main class and one invocation shape for both is worth
# more than matching any particular phrase. TLC gets the parallel collector that a matrix run's
# throughput rests on, while SANY parses rather than model-checks and needs no model-checking heap.
$tlcArguments = $heapArguments + @("-XX:+UseParallelGC", "-cp", $jar, "tlc2.TLC")
$sanyArguments = @("-Xmx512m", "-cp", $jar, "tla2sany.SANY")

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
        & $java @sanyArguments $module
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
        $captured = & $java @tlcArguments -workers $Workers -checkpoint 0 -metadir $meta -config $run.Config $run.Spec 2>&1
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
