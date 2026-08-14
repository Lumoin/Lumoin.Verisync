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
