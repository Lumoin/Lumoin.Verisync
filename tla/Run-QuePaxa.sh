#!/usr/bin/env bash
# The shell counterpart of Run-QuePaxa.ps1. Both read the same pinned matrix, so the two enforce one set of
# configurations and one expected outcome for each.
set -uo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

workers=4
toolchain_path="${TLAPLUS_HOME:-}"
tla2tools_jar="${TLA2TOOLS_JAR:-}"

while [ $# -gt 0 ]; do
    case "$1" in
        -Workers|--workers) workers="$2"; shift 2 ;;
        -ToolchainPath|--toolchain-path) toolchain_path="$2"; shift 2 ;;
        -Tla2ToolsJar|--tla2tools-jar) tla2tools_jar="$2"; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

# The toolchain is located rather than assumed, so a checkout carries no machine's directory layout.
# TLAPLUS_HOME or --toolchain-path names the directory holding tla2tools.jar, and TLA2TOOLS_JAR or
# --tla2tools-jar names the jar itself, for an installation that keeps it somewhere other than beside the
# rest of the toolchain.
resolve_tla2tools_jar() {
    if [ -n "$tla2tools_jar" ]; then
        if [ ! -f "$tla2tools_jar" ]; then
            echo "TLA2TOOLS_JAR or --tla2tools-jar names '$tla2tools_jar', which is not a file." >&2
            return 1
        fi

        printf '%s\n' "$tla2tools_jar"
        return 0
    fi

    if [ -n "$toolchain_path" ] && [ -f "$toolchain_path/tla2tools.jar" ]; then
        printf '%s\n' "$toolchain_path/tla2tools.jar"
        return 0
    fi

    echo "No tla2tools.jar was found. Set TLAPLUS_HOME or pass --toolchain-path naming the directory that holds tla2tools.jar, or set TLA2TOOLS_JAR or pass --tla2tools-jar naming the jar itself." >&2
    return 1
}

# The executable carries whatever name the operating system gives it, so both shapes are tried in the
# directory a candidate names.
resolve_java_executable() {
    for name in java java.exe; do
        if [ -x "$1/$name" ]; then
            printf '%s\n' "$1/$name"
            return 0
        fi
    done

    return 1
}

# The runtime bundled with the toolchain is taken first, JAVA_HOME second and java on PATH last, and that
# order is load-bearing rather than a preference. A machine can carry an older java on PATH for something
# else that the current toolchain does not run on, and a script that silently picked it would fail inside
# the jar in a way that reads as a specification error rather than as the missing prerequisite it is.
resolve_java() {
    if [ -n "$toolchain_path" ] && candidate=$(resolve_java_executable "$toolchain_path/jre/bin"); then
        printf '%s\n' "$candidate"
        return 0
    fi

    if [ -n "${JAVA_HOME:-}" ] && candidate=$(resolve_java_executable "$JAVA_HOME/bin"); then
        printf '%s\n' "$candidate"
        return 0
    fi

    if candidate=$(command -v java 2>/dev/null); then
        printf '%s\n' "$candidate"
        return 0
    fi

    echo "No java executable was found. The toolchain ships one under its own jre directory, so naming the toolchain is normally enough; otherwise set JAVA_HOME or put a java the toolchain runs on into PATH." >&2
    return 1
}

jar=$(resolve_tla2tools_jar) || exit 1
java=$(resolve_java) || exit 1

# The heap bound belongs to the run rather than to the machine, so TLA_JAVA_HEAP is read here and defaults
# to what the pinned matrices need. A value naming more than one flag is split, because java takes each flag
# as its own argument.
read -r -a heap_arguments <<< "${TLA_JAVA_HEAP:--Xmx2g}"

# Both tools are entered by main class over an explicit class path rather than through the jar manifest,
# because SANY is not the manifest's main class and one invocation shape for both is worth more than
# matching any particular phrase. TLC gets the parallel collector that a matrix run's throughput rests on,
# while SANY parses rather than model-checks and needs no model-checking heap.
tlc_arguments=("${heap_arguments[@]}" -XX:+UseParallelGC -cp "$jar" tlc2.TLC)
sany_arguments=(-Xmx512m -cp "$jar" tla2sany.SANY)

cd "$script_dir" || exit 1

# The matrix is read from Run-QuePaxa.matrix rather than restated here, so this runner and its PowerShell
# counterpart pin one set of configurations instead of two that can drift apart.
matrix="$script_dir/Run-QuePaxa.matrix"
if [ ! -f "$matrix" ]; then
    echo "The pinned matrix $matrix is missing. A runner with no matrix would report success having checked nothing." >&2
    exit 1
fi

mapfile -t runs < <(grep -vE '^[[:space:]]*(#|$)' "$matrix")
if [ "${#runs[@]}" -eq 0 ]; then
    echo "The pinned matrix $matrix names no configuration." >&2
    exit 1
fi

# SANY parses the distinct specifications the matrix names, in the order they first appear. A specification
# earns its parse by having a configuration, which is the rule that keeps an unpinned configuration out of
# this directory.
modules=()
for row in "${runs[@]}"; do
    IFS=$'\t' read -r _ row_spec _ _ <<< "$row"
    already=0
    for known in ${modules[@]+"${modules[@]}"}; do
        if [ "$known" = "$row_spec" ]; then
            already=1
            break
        fi
    done

    if [ "$already" -eq 0 ]; then
        modules+=("$row_spec")
    fi
done

# A module that extends another inherits the parent's SPECIFICATION name, so a torn-write configuration
# naming Spec instead of TornSpec would silently model-check the atomic parent and pass for the wrong
# reason. The check is here rather than in review because nothing else would ever catch it: the misdirected
# greens are required to reproduce the parent's counts anyway.
for cfg in ./MCTorn*.cfg; do
    if ! grep -qF "SPECIFICATION TornSpec" "$cfg"; then
        echo "$(basename "$cfg") must name SPECIFICATION TornSpec." >&2
        exit 1
    fi
done

for module in "${modules[@]}"; do
    echo "=== SANY ./$module ==="
    if ! "$java" "${sany_arguments[@]}" "$module"; then
        echo "SANY failed for $module" >&2
        exit 1
    fi
done

failures=()
for row in "${runs[@]}"; do
    IFS=$'\t' read -r config spec expect invariant <<< "$row"

    echo "=== TLC ./$config (expected: $expect) ==="

    # Each configuration gets its own metadata directory. Two TLC runs sharing one collide, and the
    # collision looks like a spec error.
    meta="./states/${config%.cfg}"
    captured=$("$java" "${tlc_arguments[@]}" -workers "$workers" -checkpoint 0 -metadir "$meta" -config "$config" "$spec" 2>&1)
    code=$?
    printf '%s\n' "$captured"

    # TLC reports 0 for no error, 12 for a safety violation and 13 for a liveness violation.
    if [ "$code" -eq 0 ]; then
        outcome=green
    elif [ "$code" -eq 12 ] || [ "$code" -eq 13 ]; then
        outcome=red
    else
        outcome="error($code)"
    fi

    if [ "$outcome" != "$expect" ]; then
        failures+=("./$config: expected $expect, got $outcome")
    elif [ "$outcome" = red ] && [ -n "$invariant" ]; then
        if ! printf '%s\n' "$captured" | grep -qF "Invariant $invariant is violated"; then
            failures+=("./$config: red, but not on $invariant")
        fi
    fi

    printf -- "--- ./%s: %s ---\n\n" "$config" "$outcome"
done

if [ "${#failures[@]}" -gt 0 ]; then
    echo "UNEXPECTED OUTCOMES:" >&2
    for failure in "${failures[@]}"; do
        echo "  $failure" >&2
    done

    echo "The model-check matrix deviated from its pinned expectations." >&2
    exit 1
fi

echo "The full matrix matches its pinned expectations."

# The last TLC run may be an expected violation, so its exit code must not leak as ours.
exit 0
