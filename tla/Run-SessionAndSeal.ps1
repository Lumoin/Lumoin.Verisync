param(
    [int]$Workers = 4
)

$ErrorActionPreference = "Stop"

$sany = "C:\tools\tlaplus\sany.cmd"
$tlc = "C:\tools\tlaplus\tlc.cmd"

$modules = @(
    ".\SessionPair.tla",
    ".\SealProtocol.tla",
    ".\SealRebirth.tla"
)

# Expected 'red' runs are the negative models; a green negative means the
# model lost its defect and the whole run fails.
$runs = @(
    @{ Config = ".\MCSessionPairSafety.cfg";       Spec = ".\SessionPair.tla";  Expect = "green" },
    @{ Config = ".\MCSessionPairTC.cfg";           Spec = ".\SessionPair.tla";  Expect = "green" },
    @{ Config = ".\MCSessionPairCrashTC.cfg";      Spec = ".\SessionPair.tla";  Expect = "green" },
    @{ Config = ".\MCSessionPairDrainClobber.cfg"; Spec = ".\SessionPair.tla";  Expect = "red" },
    @{ Config = ".\MCSessionPairEagerDrops.cfg";   Spec = ".\SessionPair.tla";  Expect = "red" },
    @{ Config = ".\MCSessionPairLiveness.cfg";     Spec = ".\SessionPair.tla";  Expect = "green" },
    @{ Config = ".\MCSealSafety.cfg";              Spec = ".\SealProtocol.tla"; Expect = "green" },
    @{ Config = ".\MCSealIsland.cfg";              Spec = ".\SealProtocol.tla"; Expect = "red" },
    @{ Config = ".\MCSealRace.cfg";                Spec = ".\SealProtocol.tla"; Expect = "red" },
    @{ Config = ".\MCSealProbeOnly.cfg";           Spec = ".\SealProtocol.tla"; Expect = "red" },
    @{ Config = ".\MCSealGuarded.cfg";             Spec = ".\SealProtocol.tla"; Expect = "green" },
    @{ Config = ".\MCRebirthDetector.cfg";         Spec = ".\SealRebirth.tla";  Expect = "green" },
    @{ Config = ".\MCRebirthSilent.cfg";           Spec = ".\SealRebirth.tla";  Expect = "red" }
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
        & $tlc -workers $Workers -checkpoint 0 -config $run.Config $run.Spec
        $code = $LASTEXITCODE
        # TLC: 0 = no error, 12 = safety violation, 13 = liveness violation.
        $outcome = if ($code -eq 0) { "green" } elseif ($code -in 12, 13) { "red" } else { "error($code)" }
        if ($outcome -ne $run.Expect) {
            $failures += "$($run.Config): expected $($run.Expect), got $outcome"
        }

        Write-Host "--- $($run.Config): $outcome ---`n"
    }

    if ($failures.Count -gt 0) {
        Write-Host "UNEXPECTED OUTCOMES:" -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "The model-check matrix deviated from its pinned expectations."
    }

    Write-Host "The full matrix matches its pinned expectations." -ForegroundColor Green
    # The last TLC run is an expected violation, so its exit code must not leak as ours.
    exit 0
}
finally {
    Pop-Location
}
