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
        & $java @sanyArguments $module
        if ($LASTEXITCODE -ne 0) {
            throw "SANY failed for $module"
        }
    }

    $failures = @()
    foreach ($run in $runs) {
        Write-Host "=== TLC $($run.Config) (expected: $($run.Expect)) ==="
        & $java @tlcArguments -workers $Workers -checkpoint 0 -config $run.Config $run.Spec
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
