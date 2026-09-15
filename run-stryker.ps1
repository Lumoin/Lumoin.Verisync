#Requires -Version 7.4

# Runs Stryker.NET mutation testing over Lumoin.Verisync.Core against the suite in test/Lumoin.Verisync.Tests.
# Stryker is invoked from inside the test project directory with the mutated project named explicitly, because the
# test project references Core, Json and Cbor and Stryker's auto-detection refuses a test project with more than one
# project reference.
#
# The suite and CI run under Microsoft.Testing.Platform, but Stryker drives the suite through its VSTest runner
# (stryker-config.json sets "test-runner": "vstest"). The script switches the test project onto VSTest for the
# run's duration: in Directory.Build.props it replaces EnableMSTestRunner and TestingPlatformDotnetTestSupport with
# UseVSTest, and in the test project it drops TestingExtensionsProfile, because MSTest.Sdk's VSTest targets refuse
# to build while either of those settings is present. Directory.Build.props, the test project file and its
# restored packages.lock.json are reverted from git once the run ends, however it ends.
#
# The script also switches the test assembly from parallel to serial execution for the run's duration, by replacing
# the [assembly: Parallelize(...)] attribute in Properties/AssemblyProperties.cs with [assembly: DoNotParallelize].
# That attribute is the only setting that governs MSTest's parallel execution in this project, under both the
# Microsoft.Testing.Platform host the suite normally runs on and the VSTest host Stryker drives. Serializing test
# execution keeps Stryker's per-test coverage analysis from mapping a mutant to a test that did not cover it, which
# happens when the tests it selects run concurrently.
#
# The script refuses to start if any of the files it switches already carries an uncommitted change, since reverting
# them would destroy that work (exit 2), and it refuses to touch any of them if the exact text it expects to replace
# has moved, so a drift in those files fails loudly instead of building under the wrong host or the wrong execution
# mode (exit 3). Every switched file is reverted from git once the run ends, however it ends.
#
# Once the four files are switched and before Stryker is ever invoked, the -InJob child builds the switched test
# project on its own, with 'dotnet build ... -c Debug', from the repository root so the global.json pin applies,
# with its full output and an MSBuild binary log kept as preflight.log and preflight.binlog under this run's own
# output folder. This exists because a machine that cannot restore or build the switched tree at all -- a NuGet
# audit or signature-allowlist failure, for example, since this repository audits packages and requires signatures
# from an owners allowlist, and the VSTest switch changes the test project's package graph a fresh machine restores
# for the first time -- can otherwise reach Stryker's own analysis step and fail there with no error text of its
# own, since Stryker's diagnostic dump does not carry MSBuild's or NuGet's error text. On success the child prints
# one line saying the switched tree built and goes on to invoke Stryker as before; on failure it never invokes
# Stryker at all, and instead prints the build's own error lines (every line matching ': error ' or an NU, NETSDK,
# MSB or CS code, capped at a sensible number with the omitted count named) and the paths of preflight.log and
# preflight.binlog, then ends with exit 17 (PREFLIGHTFAILED) after the normal verified revert. The binary log
# embeds environment variables, so it stays under this run's own output folder on this machine and is never copied
# to a share, the coordination branch or a commit.
#
# A run over the whole of Core is long, so a round is scoped by one or more -Mutate globs relative to the mutated
# project. String-literal mutations are excluded in stryker-config.json: an exception message the suite does not pin
# is not a defect, and those survivors would bury the logic survivors a round exists to find.
#
# -Concurrency sets Stryker's own --concurrency flag (the number of concurrent test runner instances Stryker starts;
# this is independent of, and does not restore, in-assembly test parallelism). -AdditionalTimeoutMs sets Stryker's
# additional-timeout, which is a config-file-only setting with no CLI flag: when given, the script writes a
# temporary copy of stryker-config.json with that key added and passes --config-file pointing at the copy, and never
# edits the committed stryker-config.json. -Verbosity passes Stryker's --verbosity flag through unchanged, for a
# single diagnostic run. The script always passes --log-to-file so every round leaves Stryker's own log under its
# output folder's logs subdirectory.
#
# Stryker's own project analysis -- a design-time build Buildalyzer runs to learn each project's references, source
# files and target framework -- does not necessarily use the MSBuild of the SDK global.json pins for this
# repository: Buildalyzer chooses between a .NET-SDK environment and a .NET Framework environment per project, and
# the latter searches installed Visual Studio or Build Tools copies for an msbuild.exe, which on a machine that also
# has an older one installed can hand the analysis an MSBuild that cannot evaluate a project targeting the pinned
# SDK's own target framework at all. Before Stryker ever runs, the script resolves the MSBuild belonging to the SDK
# global.json selects -- by running 'dotnet --version' from the repository root, reading that version's base
# installation folder from 'dotnet --list-sdks', and requiring '<folder>\<version>\MSBuild.exe' to exist -- and
# passes it to Stryker with --msbuild-path, which Stryker itself forwards to Buildalyzer as the MSBUILD_EXE_PATH
# environment variable for its project analysis, ahead of Buildalyzer's own auto-detection. --msbuild-path steers
# only that analysis: Stryker's own later build steps still run as plain 'dotnet build', which already follows this
# repository's global.json on its own and is not steered by --msbuild-path or by this resolution. -MSBuildPath
# overrides the resolved path outright, for example to point at a different installation on purpose or to reproduce
# a failure against one; a relative override is resolved against the repository root before it is checked or handed
# to the child process. Either way, the given or resolved path must exist as a file, and the run refuses with exit
# 16 before anything is switched when it does not. The MSBuild actually in use is printed near the start of every
# run. -Diagnostics passes Stryker's own --diag switch, which raises the whole run's logging to Trace on both the
# console and Stryker's own log file -- overriding -Verbosity -- and adds Buildalyzer's design-time build detail,
# the properties and items it saw and which MSBuild ran it, to what that Trace level already prints. A Trace-level
# round produces very large logs, so pair -Diagnostics with a narrow -Mutate rather than a full round.
#
# The script only ever does its real work as a child of itself. Invoked without -InJob (an internal switch, not
# meant to be passed by hand and left out of the usage examples below), it validates its parameters, writes every
# one of them to a temporary JSON file, relaunches itself through [Environment]::ProcessPath (this same pwsh, never
# whatever "pwsh" resolves to on PATH) as "<that path> -NoProfile -File <this script> -InJob -ParamsFile <that
# file>", waits for it synchronously with its output streaming straight through, deletes the file, and exits with
# the child's exit code. It never touches a switched file and never joins the job object described below, so the
# limit that job carries never outlives the run and never reaches whatever process called this script. Parameters
# travel through a file rather than the child's own command line because pwsh -File hands its remaining arguments to
# the operating system as a native command line: passing several -Mutate patterns as separate tokens after a single
# -Mutate binds only the first one and silently drops the rest, and a comma-joined PowerShell array literal fares no
# better, since a native command line never sees PowerShell's array syntax and receives the literal, comma-and-quote
# -laden text instead. A temporary JSON file sidesteps both failure modes: every parameter, -Mutate included, is
# reconstructed by the child exactly as this process held it.
#
# Before relaunching, this process refuses a -Mutate pattern containing a single quote, a double quote or a comma
# (exit 7): such a character is the signature of a pattern that already lost its shape crossing some earlier command
# line, and a script that pressed on would hand Stryker a glob it cannot mean. -Round is required (exit 7 with a
# usage line when it is missing) and is restricted to letters, digits, '.', '_' and '-', and forbidden to be '.' or
# '..' (exit 7), since it becomes a single path segment under tempdocs/stryker and must never carry a path
# separator or a directory-traversal segment. [CmdletBinding(PositionalBinding = $false)] makes a stray positional
# argument a binding error instead of silently landing in the wrong parameter.
#
# Only the -InJob child runs the rest of what follows. It re-validates -Round and -Mutate exactly as above, then
# checks that the files it is about to switch are clean (exit 2) and unmoved (exit 3), and that the machine has
# enough free commit memory for the ceiling and concurrency it is about to ask for (exit 6; see below). Once those
# hold, it records this run's UTC start time and creates this run's own output folder,
# tempdocs/stryker/<Round>/<UTC start, formatted yyyyMMddTHHmmssZ with the invariant culture>, appending -2, -3 and
# so on if a folder by that exact name already exists (the same second, or a clock step back), and passes that
# exact folder to Stryker as --output. Every run gets a folder of its own, so a same-day rerun of the same round
# name, or a run that crosses midnight into a second day-stamped log file, can never inherit another run's Stryker
# log, VSTest log or mutation-report.json: the invalidation analysis below reads only log-*.txt and Runner *-log.txt
# files inside this run's own folder, accepts a mutation-report.json only from inside it, and ignores any line in
# those files timestamped before this run's own recorded start. The run folder's path is printed near the start of
# the run and again at the end.
#
# Next, first thing before anything is switched, the child creates a Windows job object carrying a per-process
# committed-memory ceiling. A mutant can turn a bounded loop into an unbounded allocation, and Stryker's own timeout
# does not bound how large a test host grows before that timeout ends it. The job is created and this process is
# joined to it before any switched file is touched, so a failure to set up the limit (exit 4) leaves nothing
# switched. Windows adds every process a job member starts afterwards to the same job automatically, so "dotnet
# stryker", Stryker.CLI, its build, VSTest and every test host inherit the limit with no window for a child to start
# unbounded, nested to any depth (job nesting like this is supported on Windows 8 / Windows Server 2012 and later).
# -ProcessMemoryCeilingGB (default 3) sets that limit in gigabytes, per process, not summed across the run's
# processes; passing 0 disables it, and the script says so instead of silently enforcing one. A ceiling that is
# negative, non-finite (NaN or infinite), or positive but under 1 GB without -AllowLowCeiling is refused outright
# (exit 7): a sub-gigabyte ceiling is almost never anything but a deliberate low-ceiling experiment, and one could
# otherwise starve this very process before it can revert the switched files. The job is also created with
# JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so closing its last handle kills every process still assigned to it; the script
# never closes the handle itself, only this process's own exit does, cleaning up anything the run left behind.
# Before invoking Stryker, the child also sets MSBUILDDISABLENODEREUSE, UseSharedCompilation and
# DOTNET_CLI_USE_MSBUILD_SERVER in its own environment to keep MSBuild worker nodes and the Roslyn compiler server
# from starting as reusable, long-lived processes inside the limited job, where an unrelated later build on the same
# machine could attach to one and be killed by it when this run's job closes.
#
# The kernel enforces the ceiling at allocation time, not by polling, and a process pinned at it can fail in either
# of two ways: a single large allocation the kernel refuses outright, which a managed process normally surfaces as a
# catchable out-of-memory exception, or a process whose commit was already at the ceiling failing a smaller, later
# allocation it cannot survive, such as a thread's stack-guard-page commit, which can crash the process outright
# (for example with a stack overflow) rather than raising anything catchable. Which of these happens, and what if
# anything Stryker then records for the mutant in flight, is not something this script claims to know or control.
#
# The job is associated with an I/O completion port that a background thread drains for the life of this process,
# recording, with its process id, its parent's process id and a timestamp, every new process the job gains, every
# process that exits from it (normally or not), and every process that hits the memory ceiling; each new process is
# opened long enough to read its image name and its parent process id (from a Toolhelp32 snapshot taken at that
# moment), and each exit is read for its peak committed memory and exit code before the handle closes. Microsoft
# documents job-object completion-port notifications as not guaranteed to be delivered, so the job's own
# kernel-tracked peak process memory, queried independently of the port, is this script's backstop: if that peak
# reached 95% of the ceiling but no process was ever attributed a memory-limit hit through the port, the round is
# invalidated as a ceiling hit with no attributed process rather than reported as clean on the strength of a
# possibly-missed notification. The drain loop itself runs inside a try/catch; if it ever stops on an exception, the
# child prints a line starting ATTRIBUTIONFAILED and invalidates the round, since attribution for the rest of the
# run cannot be trusted from that point on.
#
# The process this script starts is "dotnet stryker"'s own launcher, a dotnet.exe that resolves and starts the
# Stryker.CLI tool; it is not the process that does the mutation testing. That engine is a further dotnet.exe whose
# parent process, per the same Toolhelp32 snapshot, is the launcher this script started. Before reading back what
# the job recorded, the child waits up to five seconds for the drain thread to record the exit of both the launcher
# and every dotnet.exe it identified as an engine, since the launcher's own process exiting is not proof the port has
# yet delivered that news. The end-of-run report groups every process the job saw by role: Stryker launcher, Stryker
# engine, test hosts, csc (the C# compiler), MSBuild nodes (every other dotnet.exe and MSBuild.exe, since Stryker's
# own build steps and MSBuild's worker nodes share that image name and this script does not inspect command lines to
# tell them apart), and anything else by its own image name; each role's highest peak committed memory and process
# count are printed. "Stryker's own process exited abnormally", one of the invalidation reasons below, watches both
# the launcher and every engine it identified this way.
#
# Once Stryker returns, the child parses its own main log's timestamps with
# [DateTimeOffset]::ParseExact(<timestamp>, 'o', [CultureInfo]::InvariantCulture) and VSTest's Runner *-log.txt
# "yyyy/MM/dd, HH:mm:ss.fff" timestamps (which carry no time zone) with [DateTime]::ParseExact using the same
# invariant culture, so a server whose culture uses "." as a separator parses identically to one that uses "/".
# Both log files are streamed line by line rather than read whole into memory, since a long round's Runner log can
# run to hundreds of megabytes. The Runner log's zone-less timestamps are read as the time zone recorded in this
# run's own run-manifest.json (TimeZoneInfo.Local.Id at the moment the run started, since that is the zone VSTest
# itself was writing in), not the analyzing machine's own zone, so -AnalyzeOnly on a different machine in a
# different zone still places every Runner-log event against the same absolute boundary the live run used. The
# pre-mutant-testing boundary is the earliest '"Runner N": Testing [' line, at or after this run's recorded UTC
# start, across every log-*.txt file in this run's own output folder. If such a line exists but its timestamp
# cannot be parsed, or a test-host exit line that would otherwise decide the round's validity cannot be parsed, the
# child fails closed: it invalidates the round and names the file and line it could not read, rather than trusting
# a boundary or a verdict it cannot actually compute. Every recorded process event is also identified by instance,
# not by process id alone: Windows reuses process ids within a single round, so a process id's own record is
# matched only to the events between its own NewProcess record and its own exit, never to an unrelated process
# that later reuses the same id, and the same instance-scoped matching decides which process ids are test hosts.
#
# Alongside that, since Stryker's initial test run and its coverage capture both run the unmutated suite and a fault
# in either taints everything scored afterwards, the child prints a line starting ROUNDINVALIDATED and, once the
# revert below has run, exits 5 when: a test host exited abnormally or with a non-zero exit code before the boundary
# above (cross-checked against VSTest's own Runner *-log.txt text, which runs even when the memory ceiling is
# disabled, since it needs no job); any process that is not a test host hits the memory ceiling at any time; a test
# host hits the memory ceiling before the boundary; the job's own peak reached 95% of the ceiling with nothing
# attributed to a memory-limit hit; the drain thread itself failed; or Stryker's launcher or an engine exits
# abnormally at any time. A test host hitting the ceiling after the boundary does not invalidate the round; the
# child prints a line starting MEMORYCEILINGREACHED naming that host and the time instead, as information only.
# With -ProcessMemoryCeilingGB 0, every job-based check above is off and the run says so; only the VSTest-log
# cross-check still runs, since it depends on nothing but Stryker's own log files.
#
# -ProcessMemoryCeilingGB 0 disables the limit outright. Above 0 it must be at least 1 unless -AllowLowCeiling is
# also given (see above), and the child also refuses to start (exit 6) when the machine's free commit memory
# (Win32_OperatingSystem's FreeVirtualMemory) is below the ceiling times two more than the effective concurrency,
# effective concurrency being -Concurrency when given or, when it is not, Stryker's own default of the machine's
# logical processor count divided by two with a floor of one, as documented under the concurrency option at
# stryker-mutator.io/docs/stryker-net/configuration: a round that cannot fit that many processes at that ceiling in
# what is free right now is not one this script will start blind to.
#
# A round in which Stryker tested zero mutants -- no mutation-report.json in this run's own output folder, or one
# whose files carry no mutants at all -- exits 10, never 0, since a round that mutated nothing found nothing by
# construction, not by a clean pass. Any other non-zero exit code from Stryker's own launcher process maps to exit
# 9, printed beside Stryker's own raw exit code, rather than passing that code through as this script's own. Where
# more than one of these outcomes applies to the same run, the child reports the first that fits in this order: a
# failed revert (8), the switched tree failing to build in the preflight check before Stryker starts (17), an
# invalidated round (5), an unexpected error the child itself raised (12), a run terminated for exceeding
# -MaxRunMinutes (14), a failed Stryker run (9), zero mutants tested (10), then success (0). A preflight failure is
# ranked just below a failed revert because it means Stryker never started at all, so nothing about its own round
# -- invalidation, a timeout, its own exit code or how many mutants it tested -- can be trusted or even applies. An
# unexpected error or a timeout is ranked above a failed or zero-mutant Stryker result because either one means
# Stryker's own exit code and mutation report, if either exists at all, cannot be trusted to mean what they
# normally would. Every path this script can take through the child, and every refusal in the outer process before
# it relaunches, ends by printing a line "RUNNEREXIT=<code> <name>", one of:
#   0 success
#   2 dirty tree (a switched file already carries an uncommitted change)
#   3 switch text drifted (the text a switch expects to replace has moved)
#   4 job setup failed (the memory-limiting job object could not be created or joined)
#   5 round invalidated (see the invalidation rules above)
#   6 not enough free commit memory for the requested ceiling and concurrency
#   7 invalid parameters (a -Mutate pattern with a quote or a comma, a non-finite or negative ceiling, a ceiling
#     under 1 GB without -AllowLowCeiling, a missing -Round, a -Round containing anything but letters, digits,
#     '.', '_' or '-', or equal to '.' or '..', a -MaxRunMinutes that is not finite, is negative, or is above
#     30000, or an -AnalyzeOnly folder that is not one of this script's own run folders)
#   8 revert failed (REVERTFAILED; the switched files could not be restored -- the tree is left switched)
#   9 Stryker itself failed (its own raw exit code is printed alongside this one)
#  10 zero mutants tested
#  11 relaunch failed (the child produced no exit code at all)
#  12 an unexpected error not covered by any of the above (printed at the moment it is caught, not only at the end)
#  13 the child crashed (CHILDCRASHED; the outer process saw a child exit code outside this table, meaning the
#     child died rather than finishing normally -- see below)
#  14 the run exceeded -MaxRunMinutes and was terminated (RUNTIMEEXCEEDED)
#  15 a git command this script depends on failed, or git itself could not be found (GITFAILED), reported before
#     anything is switched
#  16 the MSBuild this run needed could not be confirmed (MSBUILDNOTFOUND): either -MSBuildPath was given and does
#     not exist, or it was not given and the SDK global.json pins could not be resolved to an existing MSBuild.exe
#     via 'dotnet --version' and 'dotnet --list-sdks' from the repository root -- reported before anything is
#     switched
#  17 the switched tree failed to build in the preflight check that runs just before Stryker starts
#     (PREFLIGHTFAILED): a plain 'dotnet build' of the switched test project, from the repository root so
#     global.json applies, failed; its error lines are printed (capped, with the omitted count named), and its
#     full console output and binary log are saved as preflight.log and preflight.binlog under the run folder --
#     the binary log embeds environment variables and stays on this machine, never copied to a share, the
#     coordination branch or a commit -- Stryker is never invoked, and this is reported after the normal verified
#     revert
#
# This table does not apply when a pwsh parameter-binding error or a failed #Requires check happens instead: pwsh
# itself ends the process, with its own exit code (commonly 1), before a single line of this script runs, so
# neither of those two cases prints a RUNNEREXIT line.
#
# Every git command this script depends on -- the dirty-tree check at the start, and the checkout and status calls
# that revert the switched files at the end -- has its own exit code checked; git's stderr, when there is any, is
# folded into the message printed for whichever refusal or failure follows. Before any of those calls, the child
# also verifies with Get-Command that git resolves at all. A failing 'git status' at the start refuses to run with
# exit 15 and nothing switched; a failing 'git checkout' or 'git status' at the end -- including one that ran but
# still reports the switched files dirty -- sets the run's own result to a failed revert (exit 8), and the line
# "Reverted the VSTest and serialization switches ..." is printed only when every git call involved both succeeded
# and reported the four files clean.
#
# The outer process that relaunches this script never joins the job object the child sets up, but it still watches
# for the child dying outright: a child exit code that is not one of this script's own documented codes means the
# child process crashed rather than finishing normally, taking down whatever it had switched with it. Before ever
# relaunching, the outer process checks the four switched files itself with the same dirty-tree check the child
# would otherwise run first (exit 2 if dirty, exit 15 if git fails) and records a clean result, rather than trust
# that the child reached its own copy of that check before it died -- a child can crash before running any of its
# own logic at all, for example if pwsh itself fails to start. Only when that outer record says the files were
# clean before the child started does a crash make the outer process check them again and, if they are now dirty,
# run the same verified revert the child itself would have run; either way it then prints a git status of the four
# files. Without that record, a crash is reported without touching any file, since a file already dirty for a
# reason this run never confirmed must not be reverted on the strength of a guess. The run ends with exit 13
# (CHILDCRASHED) naming the child's raw exit code, or exit 8 if a revert this process did attempt could not confirm
# the files clean afterwards.
#
# -MaxRunMinutes (default 0, meaning no limit; capped at 30000, about 20.8 days, since the millisecond value this
# script computes from it would otherwise overflow a 32-bit timeout well before that) bounds how long the child
# waits for Stryker's own launcher process before giving up on the round. When the limit is exceeded, the child
# terminates every other process still assigned to this run's job object -- found from the job's own membership
# list, not by walking a process tree, so a process that broke away from its parent is still reached, and this
# script's own controlling process is left running since it must still run the revert below -- runs that revert,
# and ends with exit 14 (RUNTIMEEXCEEDED) rather than reporting the round invalid over the abnormal exits this
# termination itself causes. With -ProcessMemoryCeilingGB 0 there is no job object to read a membership list from,
# so a timeout instead kills only Stryker's own launcher and the process tree under it, which misses a process that
# broke away from that tree and leaves it running as an orphan; the memory-ceiling checks described above are
# already off in that case for the same reason -- there is no job.
#
# -AnalyzeOnly <run folder> is a diagnostic, not a normal launch form: given the full path this script printed for
# an earlier run (tempdocs/stryker/<Round>/<UTC start>), it re-runs only the invalidation analysis above -- reading
# that folder's run-manifest.json and process-records.json (both written by the run that produced it) alongside its
# log files -- and prints the same verdict and the same exit code that run itself reported for outcomes an
# after-the-fact analysis can recompute (0, 5, 9, 10, 12 and 17; 14 too, when the manifest recorded that the live
# run was terminated for exceeding -MaxRunMinutes. A revert failure, a relaunch failure and a crashed child are
# properties of a live process, not of the folder it left behind, so -AnalyzeOnly never reports 8, 11 or 13). It
# touches no file in the repository and joins no job object.
#
# Usage:
#   & .\run-stryker.ps1 -Round consensus -Mutate 'QuePaxa*.cs','HostId.cs'
#   & .\run-stryker.ps1 -Round all
#   & .\run-stryker.ps1 -Round r2-smoke-clusterid -Mutate 'ClusterId.cs' -Concurrency 2 -AdditionalTimeoutMs 30000
#   & .\run-stryker.ps1 -Round r2-smoke-clusterid -Mutate 'ClusterId.cs' -MSBuildPath 'C:\MsBuild\MSBuild.exe' -Diagnostics
#   pwsh -NoProfile -Command "& 'C:\full\path\to\run-stryker.ps1' -Round r2-smoke-clusterid -Mutate 'a.cs','b.cs'; exit $LASTEXITCODE"
#   & .\run-stryker.ps1 -AnalyzeOnly 'tempdocs\stryker\r2-smoke-clusterid\20260912T180334Z'
#
# The first form is for an interactive PowerShell session; the fourth, verified against a real cmd.exe invocation,
# is the form to give Task Scheduler, cmd, or any other caller that is not already running PowerShell code -- it is
# the only form that reliably carries more than one -Mutate pattern across that boundary, which is exactly why the
# script relaunches itself through a parameter file rather than ever depending on this form itself for more than
# one pattern. Under -Command, pwsh's own process exit code is 0 or 1 regardless of what a script inside it exits
# with, unless the command string itself ends by re-exiting with $LASTEXITCODE, which the fourth form above does;
# without it, this script's own RUNNEREXIT line still names the true code, but a caller such as Task Scheduler that
# branches on the process exit code sees only 0 or 1.

[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Round = '',

    [string[]]$Mutate = @(),

    [int]$Concurrency = 0,

    [int]$AdditionalTimeoutMs = 0,

    [string]$Verbosity = '',

    [double]$ProcessMemoryCeilingGB = 3,

    [switch]$AllowLowCeiling,

    [double]$MaxRunMinutes = 0,

    [string]$MSBuildPath = '',

    [switch]$Diagnostics,

    [string]$AnalyzeOnly = '',

    [Parameter(DontShow = $true)]
    [switch]$InJob,

    [Parameter(DontShow = $true)]
    [string]$ParamsFile = ''
)

$ErrorActionPreference = 'Continue'

$script:exitCodeNames = @{
    0  = 'SUCCESS'
    2  = 'DIRTYTREE'
    3  = 'SWITCHTEXTDRIFTED'
    4  = 'JOBSETUPFAILED'
    5  = 'ROUNDINVALIDATED'
    6  = 'INSUFFICIENTMEMORY'
    7  = 'INVALIDPARAMETERS'
    8  = 'REVERTFAILED'
    9  = 'STRYKERFAILED'
    10 = 'ZEROMUTANTSTESTED'
    11 = 'RELAUNCHFAILED'
    12 = 'UNEXPECTEDERROR'
    13 = 'CHILDCRASHED'
    14 = 'RUNTIMEEXCEEDED'
    15 = 'GITFAILED'
    16 = 'MSBUILDNOTFOUND'
    17 = 'PREFLIGHTFAILED'
}

# The exit codes the -InJob child can ever report on purpose. A code the outer process receives from its child
# that is not in this list means the child crashed rather than exiting through Exit-Runner; 11 and 13 are excluded
# on purpose, since only the outer process itself ever reports those two.
$script:childExpectedExitCodes = @(0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 15, 16, 17)

# Prints the given message, if any, then the one RUNNEREXIT line every path through this script ends with, and
# exits with that code. This is the only place in the script that calls exit, so every path names its outcome the
# same way.
function Exit-Runner
{
    param(
        [int]$Code,

        [string]$Message = $null
    )

    if($Message)
    {
        Write-Host $Message
    }

    $name = if($script:exitCodeNames.ContainsKey($Code)) { $script:exitCodeNames[$Code] } else { 'UNKNOWN' }

    Write-Host "RUNNEREXIT=$Code $name"
    exit $Code
}

# Converts the leading ISO-8601 timestamp on one of Stryker's own log-*.txt lines, such as
# "2026-09-12T16:55:41.7020467+03:00  [DBG] ...", to UTC, parsed with the invariant culture regardless of the
# machine's own. Returns $null when the line does not start with a timestamp in that shape or that timestamp does
# not parse.
function ConvertFrom-MainLogTimestamp
{
    param([string]$Line)

    $match = [regex]::Match($Line, '^(\S+)\s+\[')
    if(-not $match.Success)
    {
        return $null
    }

    $parsed = [DateTimeOffset]::MinValue
    $parsedOk = [DateTimeOffset]::TryParseExact(
        $match.Groups[1].Value,
        'o',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::None,
        [ref]$parsed)

    if(-not $parsedOk)
    {
        return $null
    }

    return $parsed.UtcDateTime
}

# Converts the "yyyy/MM/dd, HH:mm:ss.fff" timestamp embedded in one of VSTest's own Runner *-log.txt trace lines to
# one or more UTC instants, parsed with the invariant culture so a machine whose own culture uses a different date
# or time separator still reads the literal "/" and ":" VSTest wrote. Those lines carry no time zone, so the value
# is read as a wall-clock reading in the given $TimeZone -- the zone VSTest itself was writing in, not necessarily
# the zone of the machine doing the parsing -- rather than assumed to already be UTC or the analyzing machine's own
# local time. A reading that falls in $TimeZone's repeated fall-back hour is ambiguous between two real instants an
# hour apart; this returns both, earliest first, instead of silently picking the standard-time one .NET would
# default to, so a caller can resolve the ambiguity conservatively rather than trust a guess. Returns $null when
# the line carries no such timestamp or that timestamp does not parse.
function ConvertFrom-RunnerLogTimestamp
{
    param(
        [string]$Line,
        [System.TimeZoneInfo]$TimeZone
    )

    $match = [regex]::Match($Line, '(\d{4}/\d{2}/\d{2}), (\d{2}:\d{2}:\d{2}\.\d{3})')
    if(-not $match.Success)
    {
        return $null
    }

    $combined = "$($match.Groups[1].Value) $($match.Groups[2].Value)"
    $parsedUnspecified = [DateTime]::MinValue
    $parsedOk = [DateTime]::TryParseExact(
        $combined,
        'yyyy/MM/dd HH:mm:ss.fff',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::None,
        [ref]$parsedUnspecified)

    if(-not $parsedOk)
    {
        return $null
    }

    # The format string above carries no time zone specifier, so TryParseExact always returns Kind=Unspecified. A
    # reading in the repeated fall-back hour maps to two real UTC instants exactly one DST adjustment apart; both
    # are returned, sorted ascending, rather than the single standard-time instant ConvertTimeToUtc would return.
    if($TimeZone.IsAmbiguousTime($parsedUnspecified))
    {
        $offsets = $TimeZone.GetAmbiguousTimeOffsets($parsedUnspecified)
        return @($offsets | ForEach-Object { [DateTime]::SpecifyKind($parsedUnspecified - $_, [DateTimeKind]::Utc) } | Sort-Object)
    }

    return @([System.TimeZoneInfo]::ConvertTimeToUtc($parsedUnspecified, $TimeZone))
}

# Labels one recorded process by the role it plays in a round, for the peak-memory report: the Stryker launcher
# this script itself started, a Stryker engine (a dotnet.exe whose parent is that launcher), a VSTest test host, the
# C# compiler, an MSBuild node (every other dotnet.exe or MSBuild.exe, since Stryker's own build steps and MSBuild's
# worker nodes are not told apart by image name alone), or, failing those, its own image name.
function Get-StrykerProcessRoleLabel
{
    param(
        [int]$ProcessId,
        [string]$ImageName,
        [int]$ParentProcessId,
        $StrykerProcessId
    )

    if($null -ne $StrykerProcessId -and $ProcessId -eq [int]$StrykerProcessId)
    {
        return 'Stryker launcher'
    }

    if($null -ne $StrykerProcessId -and $ParentProcessId -eq [int]$StrykerProcessId -and $ImageName -ieq 'dotnet.exe')
    {
        return 'Stryker engine'
    }

    if($ImageName -ieq 'testhost.exe')
    {
        return 'test hosts'
    }

    if($ImageName -ieq 'csc.exe')
    {
        return 'csc'
    }

    if($ImageName -ieq 'MSBuild.exe' -or $ImageName -ieq 'dotnet.exe')
    {
        return 'MSBuild nodes'
    }

    if($ImageName)
    {
        return $ImageName
    }

    return '<unknown>'
}

# Converts a value read back from JSON into a UTC DateTime with the invariant culture. ConvertFrom-Json converts an
# ISO-8601-looking JSON string into a real DateTime itself (preserving Kind as Utc for a "...Z" value), so this
# accepts that value as-is rather than re-stringifying it with [string], which would format it using the current
# culture and then fail to parse it back with the invariant one. A value that arrived as a plain string, such as a
# hand-edited file, is still parsed with ParseExact and the invariant culture.
function ConvertTo-InvariantUtcDateTime
{
    param($Value)

    if($Value -is [DateTime])
    {
        if($Value.Kind -eq [DateTimeKind]::Utc)
        {
            return $Value
        }

        return $Value.ToUniversalTime()
    }

    return [DateTime]::ParseExact(
        [string]$Value,
        'o',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
}

# Normalizes a raw array of job-process records -- either the live .NET struct array from
# StrykerJobMemoryLimiter.GetRecordsSnapshot, or the PSCustomObject array ConvertFrom-Json produces when reading a
# persisted process-records.json back for -AnalyzeOnly -- into one shape, parsing TimestampUtc with the invariant
# culture whenever it did not already arrive as a DateTime. Each record also gets an InstanceKey, "<process
# id>#<instance number>", that identifies the specific process the record is about rather than just the process id
# Windows assigned it: the records for one process id are assumed to arrive in the chronological order they were
# recorded, so a NewProcess record starts a new instance and every later record for that same process id belongs
# to that instance until the next NewProcess record for it starts another. This is what lets a reused process id
# -- Windows recycles them within a single round -- be told apart as the two or more distinct processes it was,
# instead of merged into one.
function ConvertTo-NormalizedProcessRecords
{
    param([object[]]$RawRecords)

    $normalized = [System.Collections.Generic.List[object]]::new()
    $instanceNumberByProcessId = @{}

    foreach($raw in $RawRecords)
    {
        $timestamp = ConvertTo-InvariantUtcDateTime -Value $raw.TimestampUtc
        $processId = [int]$raw.ProcessId
        $eventType = [string]$raw.EventType

        if(-not $instanceNumberByProcessId.ContainsKey($processId))
        {
            $instanceNumberByProcessId[$processId] = 0
        }

        if($eventType -eq 'NewProcess' -or $instanceNumberByProcessId[$processId] -eq 0)
        {
            # A NewProcess record always starts a fresh instance; a non-NewProcess record for a process id this
            # run has not seen before starts one too, since its own NewProcess record was never captured (the
            # process may already have been a job member before the drain thread attached).
            $instanceNumberByProcessId[$processId]++
        }

        $normalized.Add([pscustomobject]@{
            ProcessId              = $processId
            ParentProcessId        = [int]$raw.ParentProcessId
            ImageName              = [string]$raw.ImageName
            EventType              = $eventType
            TimestampUtc           = $timestamp
            PeakPagefileUsageBytes = [long]$raw.PeakPagefileUsageBytes
            ExitCode               = [int]$raw.ExitCode
            InstanceKey            = "$processId#$($instanceNumberByProcessId[$processId])"
        })
    }

    return $normalized.ToArray()
}

# Finds the earliest '"Runner N": Testing [' line at or after $RunStartUtc across every given main log file, using
# the invariant-culture parser above. A line matching that text whose timestamp will not parse is reported back as
# a parse failure rather than silently skipped, so a caller can fail the round closed instead of trusting a
# boundary it could not actually read.
function Get-MutantTestingBoundary
{
    param(
        [System.IO.FileInfo[]]$MainLogFiles,
        [DateTime]$RunStartUtc
    )

    $boundaryUtc = $null
    $parseFailureReasons = [System.Collections.Generic.List[string]]::new()

    foreach($logFile in $MainLogFiles)
    {
        $stream = $null
        $reader = $null
        try
        {
            # Shared for read, write and delete, so a log Stryker or a watching process still holds open for
            # writing is still readable here: a log this analysis cannot open is a reason to invalidate the round,
            # never a file to silently skip.
            $stream = [System.IO.FileStream]::new(
                $logFile.FullName,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
            $reader = [System.IO.StreamReader]::new($stream)

            while($null -ne ($line = $reader.ReadLine()))
            {
                if($line -notmatch '"Runner \d+": Testing \[')
                {
                    continue
                }

                $parsedUtc = ConvertFrom-MainLogTimestamp -Line $line
                if($null -eq $parsedUtc)
                {
                    $parseFailureReasons.Add("could not parse the timestamp on a Testing line in $($logFile.Name): '$line'")
                    continue
                }

                if($parsedUtc -lt $RunStartUtc)
                {
                    continue
                }

                if($null -eq $boundaryUtc -or $parsedUtc -lt $boundaryUtc)
                {
                    $boundaryUtc = $parsedUtc
                }
            }
        }
        catch
        {
            $parseFailureReasons.Add("could not read $($logFile.Name): $($_.Exception.Message)")
        }
        finally
        {
            if($reader)
            {
                $reader.Dispose()
            }
            elseif($stream)
            {
                $stream.Dispose()
            }
        }
    }

    return [pscustomobject]@{
        BoundaryUtc         = $boundaryUtc
        ParseFailureReasons = $parseFailureReasons.ToArray()
    }
}

# Cross-checks test-host exits against VSTest's own Runner *-log.txt text: a non-zero "Testhost processId: N exited
# with exitcode: C" line at or after $RunStartUtc and before $BoundaryUtc (or before anything, when $BoundaryUtc is
# $null because no Testing line was ever found) names an invalidation reason. This runs even when the memory
# ceiling is disabled, since it depends on nothing but Stryker's and VSTest's own log files. A matching line whose
# timestamp will not parse is reported back as its own invalidation reason instead of being skipped, since it might
# be exactly the fault this check exists to catch.
function Get-RunnerLogCrossCheckReasons
{
    param(
        [System.IO.FileInfo[]]$RunnerLogFiles,
        [DateTime]$RunStartUtc,
        $BoundaryUtc,
        [System.TimeZoneInfo]$RunnerLogTimeZone
    )

    $reasons = [System.Collections.Generic.List[string]]::new()

    foreach($runnerLogFile in $RunnerLogFiles)
    {
        $stream = $null
        $reader = $null
        try
        {
            # Shared for read, write and delete, so a log VSTest or a watching process still holds open for writing
            # is still readable here: a log this analysis cannot open is a reason to invalidate the round, never a
            # file to silently skip.
            $stream = [System.IO.FileStream]::new(
                $runnerLogFile.FullName,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
            $reader = [System.IO.StreamReader]::new($stream)

            while($null -ne ($line = $reader.ReadLine()))
            {
                $exitMatch = [regex]::Match($line, 'TestHostManagerCallbacks\.ExitCallBack: Testhost processId: (\d+) exited with exitcode: (-?\d+)')
                if(-not $exitMatch.Success)
                {
                    continue
                }

                $exitedProcessId = [int]$exitMatch.Groups[1].Value
                $exitedCode = [int]$exitMatch.Groups[2].Value
                if($exitedCode -eq 0)
                {
                    continue
                }

                $exitedTimestampCandidates = ConvertFrom-RunnerLogTimestamp -Line $line -TimeZone $RunnerLogTimeZone
                if($null -eq $exitedTimestampCandidates)
                {
                    $reasons.Add("could not parse the timestamp on a non-zero test host exit line in $($runnerLogFile.Name): '$line'")
                    continue
                }

                $earliestCandidateUtc = $exitedTimestampCandidates[0]
                $latestCandidateUtc = $exitedTimestampCandidates[-1]

                # An ambiguous reading (ConvertFrom-RunnerLogTimestamp returns two candidates for one) is resolved
                # conservatively in both directions: the run-start filter below only skips it when even the latest
                # candidate predates the run, and the boundary check below flags it when even the earliest
                # candidate could predate mutant testing -- so an ambiguous exit is never silently placed on
                # whichever side of either comparison happens to look clean.
                if($latestCandidateUtc -lt $RunStartUtc)
                {
                    continue
                }

                if($null -eq $BoundaryUtc -or $earliestCandidateUtc -lt $BoundaryUtc)
                {
                    $ambiguityNote = if($exitedTimestampCandidates.Count -gt 1) { ' (ambiguous local time in the recorded zone; treated conservatively)' } else { '' }
                    $reasons.Add("testhost.exe (PID $exitedProcessId) exited with exit code $exitedCode at $($earliestCandidateUtc.ToString('o'))$ambiguityNote per $($runnerLogFile.Name), before mutant testing began.")
                }
            }
        }
        catch
        {
            $reasons.Add("could not read $($runnerLogFile.Name): $($_.Exception.Message)")
        }
        finally
        {
            if($reader)
            {
                $reader.Dispose()
            }
            elseif($stream)
            {
                $stream.Dispose()
            }
        }
    }

    return $reasons.ToArray()
}

# Stryker does not pretty-print mutation-report.json, so a large report is one enormous line; Get-Content -Raw
# followed by ConvertFrom-Json holds that whole line as a string and then again, several times larger, as a parsed
# object graph, inside a job whose own committed-memory ceiling this script also enforces on the process doing that
# parsing. This scans the file's UTF-8 bytes through a growing-but-bounded buffer instead, counting "status" values
# without ever materializing the file or an object graph for it in memory at once.
$script:mutationReportStatusCounterSource = @'
using System;
using System.IO;
using System.Text.Json;

// Counts, without holding a whole mutation-report.json or a parsed object graph of it in memory at once, how many
// "status" property values in it equal one of the given tested-status strings.
public static class StrykerMutationReportStatusCounter
{
    public static long CountTestedStatuses(string path, string[] testedStatuses)
    {
        long count = 0;
        var state = new JsonReaderState(new JsonReaderOptions { AllowTrailingCommas = true });
        var buffer = new byte[65536];
        int dataLength = 0;
        bool sawStatusPropertyName = false;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            while (true)
            {
                int bytesRead = stream.Read(buffer, dataLength, buffer.Length - dataLength);
                dataLength += bytesRead;
                bool isFinalBlock = bytesRead == 0;

                var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, 0, dataLength), isFinalBlock, state);

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        sawStatusPropertyName = reader.ValueTextEquals("status");
                    }
                    else if (reader.TokenType == JsonTokenType.String && sawStatusPropertyName)
                    {
                        string value = reader.GetString();
                        for (int i = 0; i < testedStatuses.Length; i++)
                        {
                            if (string.Equals(value, testedStatuses[i], StringComparison.Ordinal))
                            {
                                count++;
                                break;
                            }
                        }

                        sawStatusPropertyName = false;
                    }
                }

                state = reader.CurrentState;
                int consumed = checked((int)reader.BytesConsumed);

                if (isFinalBlock)
                {
                    break;
                }

                if (consumed == 0 && dataLength == buffer.Length)
                {
                    // No token could complete out of a full buffer, such as one mutant's embedded mutated-source
                    // string running longer than it; grow the buffer instead of looping on the same unreadable
                    // bytes forever.
                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                int remaining = dataLength - consumed;
                if (consumed > 0 && remaining > 0)
                {
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
                }

                dataLength = remaining;
            }
        }

        return count;
    }
}
'@

# Reads the newest mutation-report.json under $OutputDir, if any, and counts the mutants in it whose status shows
# they actually ran against the suite (Killed, Survived, Timeout, NoCoverage or RuntimeError), as opposed to one
# Stryker never ran at all (Ignored, by a mutate or mutation-type filter or an already-covered block, or
# CompileError). A round with no report at all, or a report whose files carry no mutant with one of those
# ran-it statuses, tested zero mutants: a -Mutate pattern matching nothing still leaves every mutant Ignored, never
# run, exactly like this.
function Test-ZeroMutantsTested
{
    param([string]$OutputDir)

    $report = Get-ChildItem -LiteralPath $OutputDir -Filter 'mutation-report.json' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if(-not $report)
    {
        return [pscustomobject]@{ ZeroMutantsTested = $true; ReportPath = $null }
    }

    $testedStatuses = @('Killed', 'Survived', 'Timeout', 'NoCoverage', 'RuntimeError')
    $totalMutantsTested = 0
    try
    {
        if(-not ('StrykerMutationReportStatusCounter' -as [type]))
        {
            Add-Type -TypeDefinition $script:mutationReportStatusCounterSource -Language CSharp -ReferencedAssemblies 'System.Text.Json', 'System.Memory'
        }

        $totalMutantsTested = [StrykerMutationReportStatusCounter]::CountTestedStatuses($report.FullName, $testedStatuses)
    }
    catch
    {
        $totalMutantsTested = 0
    }

    return [pscustomobject]@{ ZeroMutantsTested = ($totalMutantsTested -eq 0); ReportPath = $report.FullName }
}

# Runs every invalidation check this script makes, from a run's own output folder, its recorded UTC start, its
# ceiling, its launcher's process id, its (possibly persisted-and-reloaded) process records, the job's own peak
# process memory, and whether its drain thread ever failed. Both the live run below and -AnalyzeOnly call this same
# function, so a finished run and its later re-analysis can never disagree by construction.
function Invoke-RoundAnalysis
{
    param(
        [string]$OutputDir,
        [DateTime]$RunStartUtc,
        [double]$ProcessMemoryCeilingGBValue,
        $StrykerProcessId,
        [object[]]$Records,
        [long]$PeakProcessMemoryBytes,
        [string]$DrainFailureMessage,
        [System.TimeZoneInfo]$RunnerLogTimeZone,
        [bool]$IgnoreProcessExitsDueToTimeout = $false
    )

    $processMemoryCeilingBytesValue = [long]([math]::Round($ProcessMemoryCeilingGBValue * 1GB))

    $logsDir = Join-Path $OutputDir 'logs'
    $mainLogFiles = @(Get-ChildItem -LiteralPath $logsDir -Filter 'log-*.txt' -ErrorAction SilentlyContinue)
    $runnerLogFiles = @(Get-ChildItem -LiteralPath $logsDir -Filter 'Runner *-log.txt' -ErrorAction SilentlyContinue)

    $boundary = Get-MutantTestingBoundary -MainLogFiles $mainLogFiles -RunStartUtc $RunStartUtc

    $invalidationReasons = [System.Collections.Generic.List[string]]::new()
    $invalidationReasons.AddRange([string[]]@($boundary.ParseFailureReasons))

    if(-not $IgnoreProcessExitsDueToTimeout)
    {
        # A run this script itself terminated for exceeding -MaxRunMinutes fills both this cross-check and the
        # job-record exit check below with exits that termination caused, not evidence the round was already
        # unsound; that run is reported by its own code (14) instead, so neither check runs against it.
        $invalidationReasons.AddRange([string[]]@(Get-RunnerLogCrossCheckReasons -RunnerLogFiles $runnerLogFiles -RunStartUtc $RunStartUtc -BoundaryUtc $boundary.BoundaryUtc -RunnerLogTimeZone $RunnerLogTimeZone))
    }

    $roleLines = [System.Collections.Generic.List[string]]::new()
    $memoryCeilingReachedLines = [System.Collections.Generic.List[string]]::new()
    $attributionFailed = [bool]$DrainFailureMessage

    if($attributionFailed)
    {
        Write-Host "ATTRIBUTIONFAILED: the completion-port drain thread stopped unexpectedly ($DrainFailureMessage); process attribution for this round cannot be trusted."
    }

    if($ProcessMemoryCeilingGBValue -gt 0)
    {
        $records = @(ConvertTo-NormalizedProcessRecords -RawRecords $Records | Where-Object { $_.TimestampUtc -ge $RunStartUtc })

        $testHostInstanceKeys = [System.Collections.Generic.HashSet[string]]::new()
        foreach($record in $records)
        {
            if($record.EventType -eq 'NewProcess' -and $record.ImageName -ieq 'testhost.exe')
            {
                [void]$testHostInstanceKeys.Add($record.InstanceKey)
            }
        }

        $runnerLogHostPids = [System.Collections.Generic.HashSet[int]]::new()
        foreach($runnerLogFile in $runnerLogFiles)
        {
            $hostMatches = Select-String -LiteralPath $runnerLogFile.FullName -Pattern '"HostProcessId":(\d+)' -ErrorAction SilentlyContinue
            foreach($hostMatch in $hostMatches)
            {
                [void]$runnerLogHostPids.Add([int]$hostMatch.Matches[0].Groups[1].Value)
            }
        }

        # A record counts as a test host either because its own instance's NewProcess event named it testhost.exe,
        # or because VSTest's own Runner log named its process id a test host and this record's own image name is
        # testhost.exe or unidentified. It is never counted a test host on process-id membership alone: a later,
        # differently-named process that happens to reuse a former test host's process id is judged by its own
        # image name, not by who held that id before it.
        $testRecordIsTestHost = {
            param($record)

            if($testHostInstanceKeys.Contains($record.InstanceKey))
            {
                return $true
            }

            return $runnerLogHostPids.Contains($record.ProcessId) -and ($record.ImageName -ieq 'testhost.exe' -or $record.ImageName -eq '<unknown>')
        }

        $engineInstanceKeys = [System.Collections.Generic.HashSet[string]]::new()
        foreach($record in $records)
        {
            if($record.EventType -eq 'NewProcess' -and $null -ne $StrykerProcessId -and $record.ParentProcessId -eq [int]$StrykerProcessId -and $record.ImageName -ieq 'dotnet.exe')
            {
                [void]$engineInstanceKeys.Add($record.InstanceKey)
            }
        }

        $peakBytesByInstance = @{}
        $imageNameByInstance = @{}
        $parentByInstance = @{}
        $processIdByInstance = @{}
        foreach($record in $records)
        {
            $key = $record.InstanceKey

            if($record.PeakPagefileUsageBytes -gt 0 -and (-not $peakBytesByInstance.ContainsKey($key) -or $record.PeakPagefileUsageBytes -gt $peakBytesByInstance[$key]))
            {
                $peakBytesByInstance[$key] = $record.PeakPagefileUsageBytes
            }

            if($record.ImageName -and $record.ImageName -ne '<unknown>' -and -not $imageNameByInstance.ContainsKey($key))
            {
                $imageNameByInstance[$key] = $record.ImageName
            }

            if(-not $parentByInstance.ContainsKey($key))
            {
                $parentByInstance[$key] = $record.ParentProcessId
            }

            if(-not $processIdByInstance.ContainsKey($key))
            {
                $processIdByInstance[$key] = $record.ProcessId
            }
        }

        $roleStats = @{}
        $seenInstanceKeys = [System.Collections.Generic.HashSet[string]]::new()
        [void]$seenInstanceKeys.UnionWith([string[]]$peakBytesByInstance.Keys)
        [void]$seenInstanceKeys.UnionWith([string[]]$imageNameByInstance.Keys)

        foreach($instanceKey in $seenInstanceKeys)
        {
            $image = if($imageNameByInstance.ContainsKey($instanceKey)) { $imageNameByInstance[$instanceKey] } else { '<unknown>' }
            $peak = if($peakBytesByInstance.ContainsKey($instanceKey)) { $peakBytesByInstance[$instanceKey] } else { 0 }
            $parentProcessId = if($parentByInstance.ContainsKey($instanceKey)) { $parentByInstance[$instanceKey] } else { -1 }
            $processId = $processIdByInstance[$instanceKey]
            $role = Get-StrykerProcessRoleLabel -ProcessId $processId -ImageName $image -ParentProcessId $parentProcessId -StrykerProcessId $StrykerProcessId

            if(-not $roleStats.ContainsKey($role))
            {
                $roleStats[$role] = [ordered]@{ PeakBytes = 0; Count = 0 }
            }

            $roleStats[$role].Count++
            if($peak -gt $roleStats[$role].PeakBytes)
            {
                $roleStats[$role].PeakBytes = $peak
            }
        }

        foreach($role in ($roleStats.Keys | Sort-Object))
        {
            $rolePeakGb = [math]::Round($roleStats[$role].PeakBytes / 1GB, 2)
            $roleLines.Add("Role '$role': peak $rolePeakGb GB across $($roleStats[$role].Count) process(es).")
        }

        foreach($record in $records)
        {
            $isExitEvent = $record.EventType -eq 'ExitProcess' -or $record.EventType -eq 'AbnormalExitProcess'
            if($isExitEvent -and -not $IgnoreProcessExitsDueToTimeout -and (& $testRecordIsTestHost $record))
            {
                $isAbnormal = $record.EventType -eq 'AbnormalExitProcess'
                $isNonZeroExit = $record.EventType -eq 'ExitProcess' -and $record.ExitCode -ne 0
                if(($isAbnormal -or $isNonZeroExit) -and ($null -eq $boundary.BoundaryUtc -or $record.TimestampUtc -lt $boundary.BoundaryUtc))
                {
                    $invalidationReasons.Add("$($record.ImageName) (PID $($record.ProcessId)) $($record.EventType) with exit code $($record.ExitCode) at $($record.TimestampUtc.ToString('o')), before mutant testing began.")
                }
            }

            if($record.EventType -eq 'ProcessMemoryLimit')
            {
                if(-not (& $testRecordIsTestHost $record))
                {
                    $invalidationReasons.Add("$($record.ImageName) (PID $($record.ProcessId)) hit the $ProcessMemoryCeilingGBValue GB memory ceiling at $($record.TimestampUtc.ToString('o')) and is not a test host.")
                }
                elseif($null -eq $boundary.BoundaryUtc -or $record.TimestampUtc -lt $boundary.BoundaryUtc)
                {
                    $invalidationReasons.Add("$($record.ImageName) (PID $($record.ProcessId)) hit the $ProcessMemoryCeilingGBValue GB memory ceiling at $($record.TimestampUtc.ToString('o')), before mutant testing began.")
                }
                else
                {
                    $memoryCeilingReachedLines.Add("MEMORYCEILINGREACHED: test host $($record.ImageName) (PID $($record.ProcessId)) reached the $ProcessMemoryCeilingGBValue GB ceiling at $($record.TimestampUtc.ToString('o')) during mutant testing.")
                }
            }

            if($record.EventType -eq 'AbnormalExitProcess' -and -not $IgnoreProcessExitsDueToTimeout -and $null -ne $StrykerProcessId -and ($record.ProcessId -eq [int]$StrykerProcessId -or $engineInstanceKeys.Contains($record.InstanceKey)))
            {
                $invalidationReasons.Add("Stryker's own process $($record.ImageName) (PID $($record.ProcessId)) crashed at $($record.TimestampUtc.ToString('o')).")
            }
        }

        foreach($line in $memoryCeilingReachedLines)
        {
            Write-Host $line
        }

        $anyMemoryLimitRecordAttributed = @($records | Where-Object { $_.EventType -eq 'ProcessMemoryLimit' }).Count -gt 0
        $ceilingHitThresholdBytes = $processMemoryCeilingBytesValue * 0.95
        if($PeakProcessMemoryBytes -ge $ceilingHitThresholdBytes -and -not $anyMemoryLimitRecordAttributed)
        {
            $peakGbForMessage = [math]::Round($PeakProcessMemoryBytes / 1GB, 2)
            $invalidationReasons.Add("the job's peak process memory reached $peakGbForMessage GB, at least 95% of the $ProcessMemoryCeilingGBValue GB ceiling, but no process was ever attributed a memory-limit hit through the completion port.")
        }
    }
    else
    {
        Write-Host 'The per-process memory ceiling is disabled (-ProcessMemoryCeilingGB 0): job-based memory and crash attribution are off for this run; only the VSTest-log cross-check above still runs.'
    }

    $invalidationReasons = @($invalidationReasons | Select-Object -Unique)
    foreach($reason in $invalidationReasons)
    {
        Write-Host "ROUNDINVALIDATED: $reason"
    }

    $roundInvalidated = ($invalidationReasons.Count -gt 0) -or $attributionFailed

    return [pscustomobject]@{
        RoundInvalidated    = $roundInvalidated
        InvalidationReasons = $invalidationReasons
        RoleLines           = $roleLines.ToArray()
    }
}

# Applies this script's one exit-code precedence -- a failed revert, then a preflight build failure, then an
# invalidated round, then an unexpected error, then a run terminated for exceeding -MaxRunMinutes, then a failed
# Stryker run, then a round that tested zero mutants -- to decide the single exit code and message a run or an
# -AnalyzeOnly re-analysis reports.
function Get-FinalRunnerExit
{
    param(
        [bool]$RevertFailed,
        [string]$RevertFailureDetail,
        [bool]$PreflightFailed,
        [string]$PreflightFailureDetail,
        [bool]$RoundInvalidated,
        [string[]]$InvalidationReasons,
        $StrykerExitCode,
        [bool]$ZeroMutantsTested,
        [string]$UnexpectedErrorMessage,
        [bool]$RunTimeExceeded = $false
    )

    if($RevertFailed)
    {
        return [pscustomobject]@{ Code = 8; Message = "REVERTFAILED: $RevertFailureDetail" }
    }

    if($PreflightFailed)
    {
        return [pscustomobject]@{ Code = 17; Message = "PREFLIGHTFAILED: $PreflightFailureDetail" }
    }

    if($RoundInvalidated)
    {
        $reasonText = if($InvalidationReasons.Count -gt 0) { $InvalidationReasons -join ' ' } else { 'attribution could not be trusted for this round.' }
        return [pscustomobject]@{ Code = 5; Message = "The round is invalid: $reasonText" }
    }

    if($UnexpectedErrorMessage)
    {
        return [pscustomobject]@{ Code = 12; Message = $UnexpectedErrorMessage }
    }

    if($RunTimeExceeded)
    {
        return [pscustomobject]@{ Code = 14; Message = 'The run exceeded its -MaxRunMinutes limit and was terminated.' }
    }

    if($null -ne $StrykerExitCode -and [int]$StrykerExitCode -ne 0)
    {
        return [pscustomobject]@{ Code = 9; Message = "Stryker itself exited with code $StrykerExitCode." }
    }

    if($ZeroMutantsTested)
    {
        return [pscustomobject]@{ Code = 10; Message = 'Stryker tested zero mutants in this round.' }
    }

    return [pscustomobject]@{ Code = 0; Message = 'The round completed successfully.' }
}

# Refuses (exit 7) a -Mutate pattern carrying a single quote, a double quote or a comma: on some command line
# between here and Stryker, a character like that is the signature of a pattern that already lost its shape.
function Test-MutatePatternsOrExit
{
    param([string[]]$Patterns)

    foreach($pattern in $Patterns)
    {
        if($pattern.Contains("'") -or $pattern.Contains('"') -or $pattern.Contains(','))
        {
            Exit-Runner -Code 7 -Message "Refusing to start: -Mutate pattern '$pattern' contains a quote character or a comma, which a command line cannot carry reliably from every caller. No file was changed."
        }
    }
}

# Refuses (exit 7) a ceiling that is not a finite number, is negative, or is positive but under 1 GB without
# -AllowLowCeiling.
function Test-CeilingShapeOrExit
{
    param(
        [double]$CeilingGB,
        [bool]$AllowLow
    )

    if([double]::IsNaN($CeilingGB) -or [double]::IsInfinity($CeilingGB))
    {
        Exit-Runner -Code 7 -Message "Refusing to start: -ProcessMemoryCeilingGB $CeilingGB is not a finite number. No file was changed."
    }

    if($CeilingGB -lt 0)
    {
        Exit-Runner -Code 7 -Message "Refusing to start: -ProcessMemoryCeilingGB $CeilingGB is negative. No file was changed."
    }

    if($CeilingGB -gt 0 -and $CeilingGB -lt 1 -and -not $AllowLow)
    {
        Exit-Runner -Code 7 -Message "Refusing to start: -ProcessMemoryCeilingGB $CeilingGB is below 1 GB; pass 0 to disable the limit or -AllowLowCeiling to run a deliberate low-ceiling experiment. No file was changed."
    }
}

# Refuses (exit 7) a -Round name that is not made up only of letters, digits, '.', '_' and '-', or that is exactly
# '.' or '..': -Round becomes a single path segment under tempdocs/stryker, so a path separator or a
# directory-traversal segment in it must never reach the file system.
function Test-RoundNameOrExit
{
    param([string]$Name)

    if($Name -cnotmatch '\A[A-Za-z0-9._-]+\z' -or $Name -eq '.' -or $Name -eq '..')
    {
        Exit-Runner -Code 7 -Message "Refusing to start: -Round '$Name' must contain only letters, digits, '.', '_' or '-', and must not be '.' or '..'. No file was changed."
    }
}

# Refuses (exit 7) a -MaxRunMinutes that is not a finite number, is negative, or is above 30000 (about 20.8 days):
# the millisecond value this script computes from -MaxRunMinutes would overflow a 32-bit timeout well past that, so
# a value with no realistic use is refused outright rather than left to fail later, mid-run, with the tree already
# switched. Zero means no limit.
function Test-MaxRunMinutesOrExit
{
    param([double]$Minutes)

    if([double]::IsNaN($Minutes) -or [double]::IsInfinity($Minutes) -or $Minutes -lt 0 -or $Minutes -gt 30000)
    {
        Exit-Runner -Code 7 -Message "Refusing to start: -MaxRunMinutes $Minutes must be zero (no limit) or a finite number of minutes from 0 through 30000. No file was changed."
    }
}

# Refuses (exit 15) when git cannot be found on PATH at all, before anything this script depends on git for is
# attempted.
function Test-GitResolvesOrExit
{
    if(-not (Get-Command git -ErrorAction SilentlyContinue))
    {
        Exit-Runner -Code 15 -Message 'Refusing to start: git was not found on PATH; reverting the VSTest and serialization switches depends on it. No file was changed.'
    }
}

# Resolves the MSBuild.exe Stryker must be handed for its project analysis (via --msbuild-path, which Stryker
# forwards to Buildalyzer as MSBUILD_EXE_PATH), so that analysis is never left to Buildalyzer's own auto-detection,
# which can find an older Visual Studio or Build Tools MSBuild incapable of evaluating a project targeting the
# pinned SDK's target framework. Stryker's own later build steps run as plain 'dotnet build' and already follow
# global.json on their own; this resolution does not steer them. A non-empty $OverridePath must exist as a file, or
# this refuses with exit 16 before anything is switched. Otherwise, this runs 'dotnet --version' from $RepoRoot --
# so the SDK global.json pins there is the version resolved, exactly as it would be for any other dotnet invocation
# from this repository -- reads 'dotnet --list-sdks' for that version's base installation folder, and requires
# '<folder>\<version>\MSBuild.exe' to exist, refusing with exit 16 and naming the step that failed rather than
# silently falling back to whatever MSBuild Stryker or Buildalyzer would have found on their own.
function Resolve-PinnedMsBuildPathOrExit
{
    param(
        [string]$RepoRoot,
        [string]$OverridePath
    )

    if($OverridePath)
    {
        if(-not (Test-Path -LiteralPath $OverridePath -PathType Leaf))
        {
            Exit-Runner -Code 16 -Message "Refusing to start: -MSBuildPath '$OverridePath' does not exist. No file was changed."
        }

        return $OverridePath
    }

    Push-Location -LiteralPath $RepoRoot
    try
    {
        $dotnetVersionOutput = & dotnet --version 2>&1
        $dotnetVersionExitCode = $LASTEXITCODE
        $listSdksOutput = & dotnet --list-sdks 2>&1
        $listSdksExitCode = $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }

    if($dotnetVersionExitCode -ne 0)
    {
        Exit-Runner -Code 16 -Message "Refusing to start: 'dotnet --version' failed with exit code $dotnetVersionExitCode in '$RepoRoot', so the SDK global.json pins there could not be determined; pass -MSBuildPath to override. No file was changed. Output: $dotnetVersionOutput"
    }

    $pinnedSdkVersion = ([string]($dotnetVersionOutput | Select-Object -Last 1)).Trim()
    if(-not $pinnedSdkVersion)
    {
        Exit-Runner -Code 16 -Message "Refusing to start: 'dotnet --version' printed nothing in '$RepoRoot'; pass -MSBuildPath to override. No file was changed."
    }

    if($listSdksExitCode -ne 0)
    {
        Exit-Runner -Code 16 -Message "Refusing to start: 'dotnet --list-sdks' failed with exit code $listSdksExitCode, so the pinned SDK version '$pinnedSdkVersion' could not be located; pass -MSBuildPath to override. No file was changed."
    }

    $sdkBaseFolder = $null
    foreach($line in @($listSdksOutput))
    {
        $sdkLineMatch = [regex]::Match([string]$line, '^(?<version>\S+)\s+\[(?<base>.+)\]\s*$')
        if($sdkLineMatch.Success -and $sdkLineMatch.Groups['version'].Value -eq $pinnedSdkVersion)
        {
            $sdkBaseFolder = $sdkLineMatch.Groups['base'].Value
            break
        }
    }

    if(-not $sdkBaseFolder)
    {
        Exit-Runner -Code 16 -Message "Refusing to start: the pinned SDK version '$pinnedSdkVersion' (from 'dotnet --version' in '$RepoRoot') was not found in 'dotnet --list-sdks'; pass -MSBuildPath to override. No file was changed."
    }

    $candidateMsBuildPath = Join-Path (Join-Path $sdkBaseFolder $pinnedSdkVersion) 'MSBuild.exe'
    if(-not (Test-Path -LiteralPath $candidateMsBuildPath -PathType Leaf))
    {
        Exit-Runner -Code 16 -Message "Refusing to start: the pinned SDK's MSBuild was expected at '$candidateMsBuildPath' but does not exist; pass -MSBuildPath to override. No file was changed."
    }

    return $candidateMsBuildPath
}

# Runs one git command against $RepoRoot and keeps its stdout and stderr apart: PowerShell wraps a native command's
# stderr lines as error records when merged with 2>&1, so this splits them back out instead of testing merged
# output for emptiness, where a stderr-only warning on an otherwise successful call would misread as dirty
# porcelain output or as part of a checkout's result. StdOutLines is what this script parses; StdErrLines is only
# ever used inside a message.
function Invoke-GitCommand
{
    param(
        [string]$RepoRoot,
        [string[]]$Arguments
    )

    $merged = & git -C $RepoRoot @Arguments 2>&1
    $exitCode = $LASTEXITCODE

    $stdOutLines = @($merged | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { [string]$_ })
    $stdErrLines = @($merged | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { $_.ToString() })

    return [pscustomobject]@{
        ExitCode    = $exitCode
        StdOutLines = $stdOutLines
        StdErrLines = $stdErrLines
    }
}

# Runs 'git status --porcelain' scoped to $Paths against $RepoRoot, via Invoke-GitCommand above.
function Get-GitPorcelainStatus
{
    param(
        [string]$RepoRoot,
        [string[]]$Paths
    )

    return Invoke-GitCommand -RepoRoot $RepoRoot -Arguments (@('status', '--porcelain', '--') + $Paths)
}

# Runs 'git checkout HEAD --' scoped to $Paths against $RepoRoot, via Invoke-GitCommand above.
function Invoke-GitCheckoutHead
{
    param(
        [string]$RepoRoot,
        [string[]]$Paths
    )

    return Invoke-GitCommand -RepoRoot $RepoRoot -Arguments (@('checkout', 'HEAD', '--') + $Paths)
}

if($AnalyzeOnly)
{
    if(-not (Test-Path -LiteralPath $AnalyzeOnly -PathType Container))
    {
        Exit-Runner -Code 7 -Message "Refusing to analyze: '$AnalyzeOnly' is not a folder."
    }

    $manifestPath = Join-Path $AnalyzeOnly 'run-manifest.json'
    if(-not (Test-Path -LiteralPath $manifestPath))
    {
        Exit-Runner -Code 7 -Message "Refusing to analyze: '$AnalyzeOnly' has no run-manifest.json; it is not a run folder this script produced."
    }

    $unexpectedErrorMessage = $null
    $exitToReport = $null

    try
    {
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

        $runStartUtc = ConvertTo-InvariantUtcDateTime -Value $manifest.RunStartUtc
        $processMemoryCeilingGBFromManifest = [double]$manifest.ProcessMemoryCeilingGB
        $strykerProcessIdFromManifest = if($null -ne $manifest.StrykerProcessId) { [int]$manifest.StrykerProcessId } else { $null }
        $strykerExitCodeFromManifest = if($null -ne $manifest.StrykerExitCode) { [int]$manifest.StrykerExitCode } else { $null }
        $peakProcessMemoryBytesFromManifest = if($null -ne $manifest.PeakProcessMemoryBytes) { [long]$manifest.PeakProcessMemoryBytes } else { 0 }
        $drainFailureMessageFromManifest = if($manifest.DrainFailureMessage) { [string]$manifest.DrainFailureMessage } else { $null }
        $liveUnexpectedErrorMessageFromManifest = if($manifest.UnexpectedErrorMessage) { [string]$manifest.UnexpectedErrorMessage } else { $null }
        $runTimeExceededFromManifest = [bool]$manifest.RunTimeExceeded
        $preflightFailedFromManifest = [bool]$manifest.PreflightFailed
        $preflightFailureDetailFromManifest = if($manifest.PreflightFailureDetail) { [string]$manifest.PreflightFailureDetail } else { $null }

        # The live run recorded the time zone it read VSTest's zone-less Runner-log timestamps in; an older run
        # folder written before this field existed falls back to the analyzing machine's own zone, which only
        # reproduces the live verdict exactly when the two machines share a zone.
        $runnerLogTimeZoneFromManifest = [System.TimeZoneInfo]::Local
        if($manifest.RunnerLogTimeZoneId)
        {
            $runnerLogTimeZoneFromManifest = [System.TimeZoneInfo]::FindSystemTimeZoneById([string]$manifest.RunnerLogTimeZoneId)
        }

        $recordsPath = Join-Path $AnalyzeOnly 'process-records.json'
        $recordsFromDisk = @()
        if(Test-Path -LiteralPath $recordsPath)
        {
            $recordsFromDisk = @(Get-Content -Raw -LiteralPath $recordsPath | ConvertFrom-Json)
        }

        Write-Host "Analyzing run folder: $AnalyzeOnly"

        $analysis = Invoke-RoundAnalysis `
            -OutputDir $AnalyzeOnly `
            -RunStartUtc $runStartUtc `
            -ProcessMemoryCeilingGBValue $processMemoryCeilingGBFromManifest `
            -StrykerProcessId $strykerProcessIdFromManifest `
            -Records $recordsFromDisk `
            -PeakProcessMemoryBytes $peakProcessMemoryBytesFromManifest `
            -DrainFailureMessage $drainFailureMessageFromManifest `
            -RunnerLogTimeZone $runnerLogTimeZoneFromManifest `
            -IgnoreProcessExitsDueToTimeout $runTimeExceededFromManifest

        foreach($roleLine in $analysis.RoleLines)
        {
            Write-Host $roleLine
        }

        $zeroMutantsResult = Test-ZeroMutantsTested -OutputDir $AnalyzeOnly

        if($liveUnexpectedErrorMessageFromManifest)
        {
            Write-Host $liveUnexpectedErrorMessageFromManifest
        }

        $exitToReport = Get-FinalRunnerExit `
            -RevertFailed $false `
            -RevertFailureDetail $null `
            -PreflightFailed $preflightFailedFromManifest `
            -PreflightFailureDetail $preflightFailureDetailFromManifest `
            -RoundInvalidated $analysis.RoundInvalidated `
            -InvalidationReasons $analysis.InvalidationReasons `
            -StrykerExitCode $strykerExitCodeFromManifest `
            -ZeroMutantsTested $zeroMutantsResult.ZeroMutantsTested `
            -UnexpectedErrorMessage $liveUnexpectedErrorMessageFromManifest `
            -RunTimeExceeded $runTimeExceededFromManifest
    }
    catch
    {
        $unexpectedErrorMessage = "Unexpected error while analyzing '$AnalyzeOnly': $($_.Exception.Message)"
        Write-Host $unexpectedErrorMessage
        $exitToReport = [pscustomobject]@{ Code = 12; Message = $unexpectedErrorMessage }
    }

    Write-Host "Analyzed run folder: $AnalyzeOnly"
    Exit-Runner -Code $exitToReport.Code -Message $exitToReport.Message
}

if($InJob)
{
    if(-not $ParamsFile -or -not (Test-Path -LiteralPath $ParamsFile))
    {
        Exit-Runner -Code 7 -Message 'Refusing to start: -InJob requires -ParamsFile pointing at a readable parameter file written by the relaunch step below; this switch is not meant to be passed by hand.'
    }

    $roundTrippedParameters = Get-Content -Raw -LiteralPath $ParamsFile | ConvertFrom-Json
    Remove-Item -LiteralPath $ParamsFile -Force -ErrorAction SilentlyContinue

    $Round = [string]$roundTrippedParameters.Round
    $Mutate = @($roundTrippedParameters.Mutate)
    $Concurrency = [int]$roundTrippedParameters.Concurrency
    $AdditionalTimeoutMs = [int]$roundTrippedParameters.AdditionalTimeoutMs
    $Verbosity = [string]$roundTrippedParameters.Verbosity
    $ProcessMemoryCeilingGB = [double]$roundTrippedParameters.ProcessMemoryCeilingGB
    $AllowLowCeiling = [bool]$roundTrippedParameters.AllowLowCeiling
    $MaxRunMinutes = [double]$roundTrippedParameters.MaxRunMinutes
    $MSBuildPath = [string]$roundTrippedParameters.MSBuildPath
    $Diagnostics = [bool]$roundTrippedParameters.Diagnostics
}

if([string]::IsNullOrEmpty($Round))
{
    Exit-Runner -Code 7 -Message 'Usage: & .\run-stryker.ps1 -Round <name> [-Mutate <pattern>[,<pattern>...]] [-Concurrency <n>] [-AdditionalTimeoutMs <ms>] [-Verbosity <level>] [-ProcessMemoryCeilingGB <gb>] [-AllowLowCeiling] [-MaxRunMinutes <n>] [-MSBuildPath <path>] [-Diagnostics] -- or -AnalyzeOnly <run folder>. See the header for the verified unattended launch form.'
}

Test-RoundNameOrExit -Name $Round
Test-MutatePatternsOrExit -Patterns $Mutate
Test-CeilingShapeOrExit -CeilingGB $ProcessMemoryCeilingGB -AllowLow ([bool]$AllowLowCeiling)
Test-MaxRunMinutesOrExit -Minutes $MaxRunMinutes
Test-GitResolvesOrExit

if($MSBuildPath)
{
    # A relative override means relative to the repository root, not to whatever directory this process happens to
    # be started from: Stryker itself later runs with its working directory set to the test project folder, so an
    # override normalized here, before the existence check below and before it is round-tripped to the -InJob
    # child, prints and resolves to the same file everywhere it is read.
    $MSBuildPath = [System.IO.Path]::GetFullPath($MSBuildPath, $PSScriptRoot)
}

$resolvedMsBuildPath = Resolve-PinnedMsBuildPathOrExit -RepoRoot $PSScriptRoot -OverridePath $MSBuildPath

$repoRoot = $PSScriptRoot
$switchedFiles = @(
    'Directory.Build.props'
    'test/Lumoin.Verisync.Tests/Lumoin.Verisync.Tests.csproj'
    'test/Lumoin.Verisync.Tests/packages.lock.json'
    'test/Lumoin.Verisync.Tests/Properties/AssemblyProperties.cs'
)

if(-not $InJob)
{
    # This process checks the switched files itself, before the child that would actually switch them ever starts,
    # so the crash path below is never left trusting an assumption that the child ran its own dirty-tree guard: the
    # child can die (pwsh failing to start, or an outside kill in its first moments) before it ever gets there. A
    # dirty result here refuses exactly as the child's own guard would (exit 2); a git failure refuses with exit 15.
    # A clean result is recorded, and only that record -- never the mere fact that a crash happened -- lets the
    # crash path below decide it is safe to touch these files at all.
    $preLaunchStatus = Get-GitPorcelainStatus -RepoRoot $repoRoot -Paths $switchedFiles
    if($preLaunchStatus.ExitCode -ne 0)
    {
        Exit-Runner -Code 15 -Message ("Refusing to start: 'git status' failed with exit code $($preLaunchStatus.ExitCode) and did not report whether the switched files are clean; nothing was switched:" + [Environment]::NewLine + ($preLaunchStatus.StdErrLines -join [Environment]::NewLine))
    }

    if($preLaunchStatus.StdOutLines.Count -gt 0)
    {
        Exit-Runner -Code 2 -Message ("Refusing to start: reverting the VSTest and serialization switches restores these files from git, and they already carry uncommitted changes that a revert would destroy:" + [Environment]::NewLine + ($preLaunchStatus.StdOutLines -join [Environment]::NewLine))
    }

    $filesConfirmedCleanBeforeChildStarted = $true

    # This process relaunches itself and reports the child's exit code; it never joins the job object the child
    # sets up, so the limit that job carries never outlives the run and never reaches this process or whatever
    # called it.
    $roundTripFilePath = Join-Path ([System.IO.Path]::GetTempPath()) "run-stryker-params-$([System.Guid]::NewGuid().ToString('N')).json"
    $roundTripPayload = [ordered]@{
        Round                  = $Round
        Mutate                 = @($Mutate)
        Concurrency            = $Concurrency
        AdditionalTimeoutMs    = $AdditionalTimeoutMs
        Verbosity              = $Verbosity
        ProcessMemoryCeilingGB = $ProcessMemoryCeilingGB
        AllowLowCeiling        = [bool]$AllowLowCeiling
        MaxRunMinutes          = $MaxRunMinutes
        MSBuildPath            = $MSBuildPath
        Diagnostics            = [bool]$Diagnostics
    }

    ($roundTripPayload | ConvertTo-Json -Depth 5) | Set-Content -Encoding utf8 -LiteralPath $roundTripFilePath

    $relaunchPath = [Environment]::ProcessPath
    $childExitCode = $null
    try
    {
        & $relaunchPath -NoProfile -File $PSCommandPath -InJob -ParamsFile $roundTripFilePath
        $childExitCode = $LASTEXITCODE
    }
    catch
    {
        $childExitCode = $null
    }

    if(Test-Path -LiteralPath $roundTripFilePath)
    {
        Remove-Item -LiteralPath $roundTripFilePath -Force -ErrorAction SilentlyContinue
    }

    if($null -eq $childExitCode)
    {
        Exit-Runner -Code 11 -Message "Refusing to report a result: relaunching through '$relaunchPath' produced no exit code from the child."
    }

    if($script:childExpectedExitCodes -notcontains $childExitCode)
    {
        # A code the child never reports on purpose means it crashed rather than exiting through Exit-Runner,
        # taking down whatever it had switched with it.
        Write-Host "The child process exited with code $childExitCode, which is outside this script's own exit-code table; it likely crashed rather than finishing normally."

        $crashRevertFailed = $false

        if(-not $filesConfirmedCleanBeforeChildStarted)
        {
            # This process's own pre-launch check above never confirmed a clean baseline for these files, so any
            # dirtiness found now cannot be attributed to this run; touching them here could destroy uncommitted
            # work this run never switched in the first place. There is nothing to revert to that this process can
            # trust, so the crash is reported without touching any file.
            Write-Host 'Skipping the crash-path revert: this run never confirmed the switched files were clean before the child started, so touching them now could destroy work that predates this run.'
        }
        else
        {
            $preRevertStatus = Get-GitPorcelainStatus -RepoRoot $repoRoot -Paths $switchedFiles

            if($preRevertStatus.ExitCode -ne 0)
            {
                $crashRevertFailed = $true
                Write-Host "Could not check whether the switched files need reverting: 'git status' failed with exit code $($preRevertStatus.ExitCode): $($preRevertStatus.StdErrLines -join '; ')"
            }
            elseif($preRevertStatus.StdOutLines.Count -gt 0)
            {
                $crashRevertAttempt = 0
                $crashCheckout = $null
                do
                {
                    $crashRevertAttempt++
                    $crashCheckout = Invoke-GitCheckoutHead -RepoRoot $repoRoot -Paths $switchedFiles

                    if($crashCheckout.ExitCode -ne 0 -and $crashRevertAttempt -lt 5)
                    {
                        Start-Sleep -Seconds 1
                    }
                }
                while($crashCheckout.ExitCode -ne 0 -and $crashRevertAttempt -lt 5)

                if($crashCheckout.ExitCode -ne 0)
                {
                    $crashRevertFailed = $true
                    Write-Host "'git checkout HEAD -- <switched files>' failed with exit code $($crashCheckout.ExitCode) after $crashRevertAttempt attempt(s): $($crashCheckout.StdErrLines -join '; ')"
                }
            }

            $finalStatus = Get-GitPorcelainStatus -RepoRoot $repoRoot -Paths $switchedFiles
            Write-Host "git status of the switched files after handling the crash: $(if($finalStatus.StdOutLines.Count -gt 0) { $finalStatus.StdOutLines -join '; ' } else { '(clean)' })"

            if($finalStatus.ExitCode -ne 0 -or $finalStatus.StdOutLines.Count -gt 0)
            {
                $crashRevertFailed = $true
            }
        }

        if($crashRevertFailed)
        {
            Exit-Runner -Code 8 -Message "REVERTFAILED: the child process (exit code $childExitCode) crashed, and the switched files could not be confirmed clean afterwards."
        }

        Exit-Runner -Code 13 -Message "CHILDCRASHED: the child process exited with code $childExitCode, which is outside this script's own exit-code table; it did not run to completion."
    }

    exit $childExitCode
}

$configFile = Join-Path $repoRoot 'stryker-config.json'
$testProjectDir = Join-Path $repoRoot 'test/Lumoin.Verisync.Tests'

$directoryBuildPropsPath = Join-Path $repoRoot 'Directory.Build.props'
$testCsprojPath = Join-Path $testProjectDir 'Lumoin.Verisync.Tests.csproj'
$assemblyPropertiesPath = Join-Path $testProjectDir 'Properties/AssemblyProperties.cs'

$dirtyStatus = Get-GitPorcelainStatus -RepoRoot $repoRoot -Paths $switchedFiles
if($dirtyStatus.ExitCode -ne 0)
{
    Exit-Runner -Code 15 -Message ("Refusing to start: 'git status' failed with exit code $($dirtyStatus.ExitCode) and did not report whether the switched files are clean; nothing was switched:" + [Environment]::NewLine + ($dirtyStatus.StdErrLines -join [Environment]::NewLine))
}

if($dirtyStatus.StdOutLines.Count -gt 0)
{
    Exit-Runner -Code 2 -Message ("Refusing to start: reverting the VSTest and serialization switches restores these files from git, and they already carry uncommitted changes that a revert would destroy:" + [Environment]::NewLine + ($dirtyStatus.StdOutLines -join [Environment]::NewLine))
}

function Set-FileTextPreservingEncoding
{
    param(
        [string]$Path,
        [string]$Content
    )

    $existingBytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = $existingBytes.Length -ge 3 -and $existingBytes[0] -eq 0xEF -and $existingBytes[1] -eq 0xBB -and $existingBytes[2] -eq 0xBF
    $encoding = [System.Text.UTF8Encoding]::new($hasBom)

    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

$directoryBuildPropsText = [System.IO.File]::ReadAllText($directoryBuildPropsPath)
$testCsprojText = [System.IO.File]::ReadAllText($testCsprojPath)
$assemblyPropertiesText = [System.IO.File]::ReadAllText($assemblyPropertiesPath)

$mstestRunnerLines = "    <EnableMSTestRunner>true</EnableMSTestRunner>`n    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>"
$useVSTestLine = '    <UseVSTest>true</UseVSTest>'
$testingExtensionsProfileLine = "    <TestingExtensionsProfile>AllMicrosoft</TestingExtensionsProfile>`n"
$parallelizeAttributeLine = '[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]'
$doNotParallelizeAttributeLine = '[assembly: DoNotParallelize]'

$missingSwitchText = @()
if(-not $directoryBuildPropsText.Contains($mstestRunnerLines))
{
    $missingSwitchText += 'Directory.Build.props'
}

if(-not $testCsprojText.Contains($testingExtensionsProfileLine))
{
    $missingSwitchText += 'test/Lumoin.Verisync.Tests/Lumoin.Verisync.Tests.csproj'
}

if(-not $assemblyPropertiesText.Contains($parallelizeAttributeLine))
{
    $missingSwitchText += 'test/Lumoin.Verisync.Tests/Properties/AssemblyProperties.cs'
}

if($missingSwitchText.Count -gt 0)
{
    Exit-Runner -Code 3 -Message "Refusing to start: the text the VSTest and serialization switches expect to replace has moved in $($missingSwitchText -join ' and '); no file was changed."
}

$processMemoryCeilingBytes = [long]([math]::Round($ProcessMemoryCeilingGB * 1GB))

if($ProcessMemoryCeilingGB -gt 0)
{
    $effectiveConcurrency = $Concurrency
    if($effectiveConcurrency -le 0)
    {
        # Stryker's own documentation (stryker-mutator.io/docs/stryker-net/configuration, the concurrency option)
        # states its default is the machine's logical processor count divided by two, with a floor of one; this
        # mirrors that default so the free-memory check below reflects what Stryker will actually start when
        # -Concurrency is not given.
        $effectiveConcurrency = [math]::Max(1, [int][math]::Floor([Environment]::ProcessorCount / 2))
    }

    $freeVirtualMemoryBytes = (Get-CimInstance Win32_OperatingSystem).FreeVirtualMemory * 1KB
    $requiredFreeBytes = ($effectiveConcurrency + 2) * $processMemoryCeilingBytes

    if($freeVirtualMemoryBytes -lt $requiredFreeBytes)
    {
        $freeGb = [math]::Round($freeVirtualMemoryBytes / 1GB, 2)
        $requiredGb = [math]::Round($requiredFreeBytes / 1GB, 2)
        Exit-Runner -Code 6 -Message "Refusing to start: free commit memory is $freeGb GB, but $effectiveConcurrency-way concurrency at a $ProcessMemoryCeilingGB GB ceiling needs at least $requiredGb GB free ((concurrency + 2) x ceiling). No file was changed."
    }
}

$runStartUtc = [DateTime]::UtcNow
$runFolderName = $runStartUtc.ToString('yyyyMMddTHHmmssZ', [System.Globalization.CultureInfo]::InvariantCulture)
$outputDirBase = Join-Path $repoRoot "tempdocs/stryker/$Round/$runFolderName"
$outputDir = $outputDirBase
$outputDirSuffix = 1
while(Test-Path -LiteralPath $outputDir)
{
    $outputDirSuffix++
    $outputDir = "$outputDirBase-$outputDirSuffix"
}

try
{
    New-Item -ItemType Directory -Path $outputDir -Force -ErrorAction Stop | Out-Null
}
catch
{
    Exit-Runner -Code 12 -Message "UNEXPECTEDERROR: could not create the run folder '$outputDir': $($_.Exception.Message)"
}

Write-Host "Run folder: $outputDir"
Write-Host "MSBuild in use: $resolvedMsBuildPath"

# Every process this run starts afterwards -- "dotnet stryker", Stryker.CLI, its build, VSTest, and every test host
# -- inherits the job below, because Windows adds a process started by a job member to the same job automatically.
# The job is created and this process is joined to it before any switched file is touched, so a failure to set up
# the memory limit leaves nothing switched and fails loudly with its own exit code instead of running the round
# unbounded.
$jobObjectSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// One event this run's job object reported about one of its member processes: a new process joining the job, a
// process exiting it (normally or not), or a process hitting the job's per-process memory ceiling.
// PeakPagefileUsageBytes is a real value at an ExitProcess, AbnormalExitProcess or ProcessMemoryLimit event when
// the process could still be queried, -1 when it could not, and 0 at a NewProcess event, which predates any usage
// worth recording. ExitCode is only meaningful at an ExitProcess or AbnormalExitProcess event and reads 0
// otherwise.
public struct StrykerJobProcessRecord
{
    public int ProcessId;
    public int ParentProcessId;
    public string ImageName;
    public string EventType;
    public DateTime TimestampUtc;
    public long PeakPagefileUsageBytes;
    public int ExitCode;
}

// Wraps the Win32 job object API so a PowerShell script can put every process of a run under one kernel-enforced,
// per-process committed-memory ceiling, and attribute what happens at that ceiling to the process it happened to,
// including which process started which. A job object is an unnamed kernel object that groups processes; Windows
// adds a process created by any current member of a job to the same job automatically, so joining this runner's
// own process before it starts any child covers every process the run ever starts, nested to any depth, with no
// race for a later-started process to slip outside the limit.
public static class StrykerJobMemoryLimiter
{
    private const int JobObjectAssociateCompletionPortInformation = 7;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    // Job-object completion-port message identifiers. There is deliberately no value 5 in this set; Windows never
    // defines one.
    private const uint JobObjectMsgNewProcess = 6;
    private const uint JobObjectMsgExitProcess = 7;
    private const uint JobObjectMsgAbnormalExitProcess = 8;
    private const uint JobObjectMsgProcessMemoryLimit = 9;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessTerminate = 0x0001;

    private const uint Th32csSnapProcess = 0x00000002;

    // The most process ids TerminateOtherJobMembers below will read back from one job; a round is not expected to
    // ever have this many processes assigned to its job at once.
    private const int MaxTrackedJobMemberCount = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT
    {
        public IntPtr CompletionKey;
        public IntPtr CompletionPort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private struct OpenProcessEntry
    {
        public IntPtr Handle;
        public string ImageName;
        public int ParentProcessId;
    }

    private struct ExitedProcessEntry
    {
        public string ImageName;
        public int ParentProcessId;
        public long PeakPagefileUsageBytes;
        public int ExitCode;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr processHandle, IntPtr jobHandle, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateIoCompletionPort(IntPtr FileHandle, IntPtr ExistingCompletionPort, UIntPtr CompletionKey, uint NumberOfConcurrentThreads);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetQueuedCompletionStatus(IntPtr CompletionPort, out uint lpNumberOfBytesTransferred, out IntPtr lpCompletionKey, out IntPtr lpOverlapped, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    private static readonly object sync = new object();
    private static readonly List<StrykerJobProcessRecord> records = new List<StrykerJobProcessRecord>();
    private static readonly Dictionary<int, OpenProcessEntry> openProcesses = new Dictionary<int, OpenProcessEntry>();
    private static readonly Dictionary<int, ExitedProcessEntry> lastKnownExits = new Dictionary<int, ExitedProcessEntry>();
    private static IntPtr completionPort = IntPtr.Zero;
    private static volatile string drainFailureMessage = null;

    // Creates an unnamed job object whose members must each stay under processMemoryLimitBytes of committed
    // memory, and that kills every member process still alive when the job's last handle closes. Throws with the
    // failing call's name and its Win32 error code if either step fails, closing the partially-configured handle
    // first so nothing leaks.
    public static IntPtr CreateJob(long processMemoryLimitBytes)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateJobObject failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
        }

        var limits = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitProcessMemory | JobObjectLimitKillOnJobClose;
        limits.ProcessMemoryLimit = (UIntPtr)(ulong)processMemoryLimitBytes;

        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr limitsPointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(limits, limitsPointer, false);

            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, limitsPointer, (uint)length))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(job);
                throw new InvalidOperationException("SetInformationJobObject failed with Win32 error " + error + ".");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(limitsPointer);
        }

        return job;
    }

    // Creates an I/O completion port, associates it with the job so Windows reports every member process's
    // arrival, departure and memory-limit hits to it, and starts the background thread that drains those reports
    // for the rest of this process's life. Microsoft documents these notifications as not guaranteed to be
    // delivered, so the job's own kernel-tracked peak (QueryPeakUsage below) is this script's backstop for the
    // memory-ceiling check specifically.
    public static void AttachCompletionPort(IntPtr job)
    {
        IntPtr port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
        if (port == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateIoCompletionPort failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
        }

        var association = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT
        {
            CompletionKey = job,
            CompletionPort = port
        };

        int length = Marshal.SizeOf(typeof(JOBOBJECT_ASSOCIATE_COMPLETION_PORT));
        IntPtr associationPointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(association, associationPointer, false);

            if (!SetInformationJobObject(job, JobObjectAssociateCompletionPortInformation, associationPointer, (uint)length))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(port);
                throw new InvalidOperationException("SetInformationJobObject (completion port) failed with Win32 error " + error + ".");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(associationPointer);
        }

        completionPort = port;

        var thread = new Thread(DrainCompletionPort);
        thread.IsBackground = true;
        thread.Name = "StrykerJobAttribution";
        thread.Start();
    }

    // Adds an existing process to the job, so it and every process it starts afterwards become subject to the
    // job's memory limit.
    public static void AssignProcess(IntPtr job, IntPtr processHandle)
    {
        if (!AssignProcessToJobObject(job, processHandle))
        {
            throw new InvalidOperationException("AssignProcessToJobObject failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
        }
    }

    // Returns null while the drain thread is alive and healthy, or the message of the exception that stopped it.
    public static string GetDrainFailureMessage()
    {
        return drainFailureMessage;
    }

    private static void DrainCompletionPort()
    {
        try
        {
            while (true)
            {
                uint messageId;
                IntPtr completionKey;
                IntPtr overlappedValue;

                bool succeeded = GetQueuedCompletionStatus(completionPort, out messageId, out completionKey, out overlappedValue, uint.MaxValue);
                if (!succeeded)
                {
                    drainFailureMessage = "GetQueuedCompletionStatus returned false with Win32 error " + Marshal.GetLastWin32Error() + ".";
                    return;
                }

                int processId = unchecked((int)overlappedValue.ToInt64());

                if (messageId == JobObjectMsgNewProcess)
                {
                    HandleNewProcess(processId);
                }
                else if (messageId == JobObjectMsgExitProcess)
                {
                    HandleExitProcess(processId, "ExitProcess");
                }
                else if (messageId == JobObjectMsgAbnormalExitProcess)
                {
                    HandleExitProcess(processId, "AbnormalExitProcess");
                }
                else if (messageId == JobObjectMsgProcessMemoryLimit)
                {
                    HandleMemoryLimit(processId);
                }
            }
        }
        catch (Exception ex)
        {
            drainFailureMessage = ex.Message;
        }
    }

    private static string QueryImageName(IntPtr handle)
    {
        var buffer = new StringBuilder(1024);
        int size = buffer.Capacity;
        if (QueryFullProcessImageName(handle, 0, buffer, ref size))
        {
            return System.IO.Path.GetFileName(buffer.ToString(0, size));
        }

        return null;
    }

    // Walks a fresh Toolhelp32 snapshot, taken at NEW_PROCESS time, for the entry matching processId and returns
    // its parent process id, or -1 when the snapshot could not be taken or no matching entry was found (the
    // process may already have exited by the time the snapshot is walked).
    private static int QueryParentProcessId(int processId)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return -1;
        }

        try
        {
            var entry = default(PROCESSENTRY32);
            entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

            if (!Process32First(snapshot, ref entry))
            {
                return -1;
            }

            do
            {
                if (entry.th32ProcessID == (uint)processId)
                {
                    return unchecked((int)entry.th32ParentProcessID);
                }
            }
            while (Process32Next(snapshot, ref entry));

            return -1;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static void HandleNewProcess(int processId)
    {
        string imageName = "<unknown>";
        int parentProcessId = QueryParentProcessId(processId);
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
        if (handle != IntPtr.Zero)
        {
            imageName = QueryImageName(handle) ?? "<unknown>";
            lock (sync)
            {
                openProcesses[processId] = new OpenProcessEntry { Handle = handle, ImageName = imageName, ParentProcessId = parentProcessId };
            }
        }

        AddRecord(processId, parentProcessId, imageName, "NewProcess", 0, 0);
    }

    private static void HandleExitProcess(int processId, string eventType)
    {
        string imageName = "<unknown>";
        int parentProcessId = -1;
        long peakBytes = -1;
        int exitCode = 0;

        lock (sync)
        {
            OpenProcessEntry entry;
            if (openProcesses.TryGetValue(processId, out entry))
            {
                imageName = entry.ImageName;
                parentProcessId = entry.ParentProcessId;

                var counters = default(PROCESS_MEMORY_COUNTERS);
                counters.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS));
                if (GetProcessMemoryInfo(entry.Handle, out counters, counters.cb))
                {
                    peakBytes = (long)(ulong)counters.PeakPagefileUsage;
                }

                uint rawExitCode;
                if (GetExitCodeProcess(entry.Handle, out rawExitCode))
                {
                    exitCode = unchecked((int)rawExitCode);
                }

                CloseHandle(entry.Handle);
                openProcesses.Remove(processId);
                lastKnownExits[processId] = new ExitedProcessEntry { ImageName = imageName, ParentProcessId = parentProcessId, PeakPagefileUsageBytes = peakBytes, ExitCode = exitCode };
            }
            else
            {
                // A process can report both an exit and an abnormal exit; whichever arrives second finds the
                // handle already closed by the first and reuses what that one read instead of reopening anything.
                ExitedProcessEntry cached;
                if (lastKnownExits.TryGetValue(processId, out cached))
                {
                    imageName = cached.ImageName;
                    parentProcessId = cached.ParentProcessId;
                    peakBytes = cached.PeakPagefileUsageBytes;
                    exitCode = cached.ExitCode;
                }
            }
        }

        AddRecord(processId, parentProcessId, imageName, eventType, peakBytes, exitCode);
    }

    private static void HandleMemoryLimit(int processId)
    {
        string imageName = "<unknown>";
        int parentProcessId = -1;
        long peakBytes = -1;

        lock (sync)
        {
            OpenProcessEntry entry;
            if (openProcesses.TryGetValue(processId, out entry))
            {
                imageName = entry.ImageName;
                parentProcessId = entry.ParentProcessId;

                var counters = default(PROCESS_MEMORY_COUNTERS);
                counters.cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS));
                if (GetProcessMemoryInfo(entry.Handle, out counters, counters.cb))
                {
                    peakBytes = (long)(ulong)counters.PeakPagefileUsage;
                }
            }
        }

        AddRecord(processId, parentProcessId, imageName, "ProcessMemoryLimit", peakBytes, 0);
    }

    private static void AddRecord(int processId, int parentProcessId, string imageName, string eventType, long peakBytes, int exitCode)
    {
        var record = new StrykerJobProcessRecord
        {
            ProcessId = processId,
            ParentProcessId = parentProcessId,
            ImageName = imageName,
            EventType = eventType,
            TimestampUtc = DateTime.UtcNow,
            PeakPagefileUsageBytes = peakBytes,
            ExitCode = exitCode
        };

        lock (sync)
        {
            records.Add(record);
        }
    }

    // Returns every record collected so far. This takes a lock only long enough to copy the list, so a caller
    // such as this script's own finally block never blocks on it.
    public static StrykerJobProcessRecord[] GetRecordsSnapshot()
    {
        lock (sync)
        {
            return records.ToArray();
        }
    }

    // Reads the highest committed-memory usage the kernel has recorded for any single process ever in the job,
    // and for the job as a whole, in bytes.
    public static void QueryPeakUsage(IntPtr job, out long peakProcessBytes, out long peakJobBytes)
    {
        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr infoPointer = Marshal.AllocHGlobal(length);
        try
        {
            uint returnedLength;
            if (!QueryInformationJobObject(job, JobObjectExtendedLimitInformation, infoPointer, (uint)length, out returnedLength))
            {
                throw new InvalidOperationException("QueryInformationJobObject failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
            }

            var info = (JOBOBJECT_EXTENDED_LIMIT_INFORMATION)Marshal.PtrToStructure(infoPointer, typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            peakProcessBytes = (long)(ulong)info.PeakProcessMemoryUsed;
            peakJobBytes = (long)(ulong)info.PeakJobMemoryUsed;
        }
        finally
        {
            Marshal.FreeHGlobal(infoPointer);
        }
    }

    // Closes the job handle. If this is the last open handle to the job, every process still assigned to it is
    // killed immediately, because the job was created with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
    public static void CloseJob(IntPtr job)
    {
        CloseHandle(job);
    }

    // Terminates every process the job currently owns except this calling process itself, for a round that has
    // run past its own -MaxRunMinutes limit. TerminateJobObject cannot be used directly here, since this process
    // is itself assigned to the job and that call would terminate it too, before it could run the revert; this
    // instead reads the job's own membership list and calls TerminateProcess on every id in it but the caller's
    // own, so the process that must still run the revert survives. A member the job reports that has already
    // exited by the time this opens it is skipped rather than treated as a failure. Between reading that snapshot
    // and opening a listed id, the process it named can exit and Windows can recycle the id onto an unrelated
    // process, so each one opened is re-confirmed still a member of this job with IsProcessInJob immediately before
    // TerminateProcess, and skipped, not terminated, when that confirmation fails or says otherwise.
    public static void TerminateOtherJobMembers(IntPtr job, uint exitCode)
    {
        int currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        int headerSize = sizeof(uint) * 2;
        int bufferSize = headerSize + MaxTrackedJobMemberCount * IntPtr.Size;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            uint returnedLength;
            if (!QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, (uint)bufferSize, out returnedLength))
            {
                return;
            }

            int listedCount = Marshal.ReadInt32(buffer, 4);
            for (int i = 0; i < listedCount && i < MaxTrackedJobMemberCount; i++)
            {
                IntPtr rawProcessId = Marshal.ReadIntPtr(buffer, headerSize + i * IntPtr.Size);
                int processId = unchecked((int)rawProcessId.ToInt64());
                if (processId == currentProcessId)
                {
                    continue;
                }

                IntPtr handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, processId);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        bool isStillInThisJob;
                        if (IsProcessInJob(handle, job, out isStillInThisJob) && isStillInThisJob)
                        {
                            TerminateProcess(handle, exitCode);
                        }
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
'@

$jobHandle = [IntPtr]::Zero

if($ProcessMemoryCeilingGB -gt 0)
{
    try
    {
        if(-not ('StrykerJobMemoryLimiter' -as [type]))
        {
            Add-Type -TypeDefinition $jobObjectSource -Language CSharp
        }

        $jobHandle = [StrykerJobMemoryLimiter]::CreateJob($processMemoryCeilingBytes)
        [StrykerJobMemoryLimiter]::AttachCompletionPort($jobHandle)
        [StrykerJobMemoryLimiter]::AssignProcess($jobHandle, [System.Diagnostics.Process]::GetCurrentProcess().Handle)
    }
    catch
    {
        if($jobHandle -ne [IntPtr]::Zero)
        {
            [StrykerJobMemoryLimiter]::CloseJob($jobHandle)
        }

        Exit-Runner -Code 4 -Message "Refusing to start: could not create or join the memory-limiting job object ($($_.Exception.Message)); no file was changed."
    }

    Write-Host "Joined a job object limiting every process of this run to $ProcessMemoryCeilingGB GB of committed memory each, enforced by the kernel at allocation time and attributed through an I/O completion port."
}
else
{
    Write-Host 'The per-process memory ceiling is disabled (-ProcessMemoryCeilingGB 0): processes in this run are unbounded.'
}

# Keeps MSBuild worker nodes and the Roslyn compiler server from starting as reusable, long-lived processes inside
# the job above, where a later, unrelated build on this machine could attach to one and be killed by it once this
# run's job closes.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:UseSharedCompilation = 'false'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

$strykerExitCode = $null
$strykerProcessId = $null
$unexpectedErrorMessage = $null
$revertFailed = $false
$revertFailureDetail = $null
$preflightFailed = $false
$preflightFailureDetail = $null
$peakProcessMemoryBytes = 0
$peakJobMemoryBytes = 0
$recordsSnapshot = @()
$runTimeExceeded = $false
$runnerLogTimeZone = [System.TimeZoneInfo]::Local

try
{
    Set-FileTextPreservingEncoding -Path $directoryBuildPropsPath -Content $directoryBuildPropsText.Replace($mstestRunnerLines, $useVSTestLine)
    Set-FileTextPreservingEncoding -Path $testCsprojPath -Content $testCsprojText.Replace($testingExtensionsProfileLine, '')
    Set-FileTextPreservingEncoding -Path $assemblyPropertiesPath -Content $assemblyPropertiesText.Replace($parallelizeAttributeLine, $doNotParallelizeAttributeLine)

    # Stryker's own diagnostic dump does not carry MSBuild's or NuGet's error text, so a machine that cannot
    # restore or build the switched tree at all -- rather than merely analyze it differently -- would otherwise
    # reach Stryker with nothing useful to say about why. Building the switched tree here, before Stryker ever
    # starts, surfaces that real error directly.
    $preflightLogPath = Join-Path $outputDir 'preflight.log'
    $preflightBinlogPath = Join-Path $outputDir 'preflight.binlog'
    $preflightProjectRelativePath = 'test/Lumoin.Verisync.Tests/Lumoin.Verisync.Tests.csproj'

    Write-Host "Preflight: building the switched tree before Stryker starts (log $preflightLogPath, binary log $preflightBinlogPath)."

    Push-Location -LiteralPath $repoRoot
    try
    {
        $preflightOutputRaw = & dotnet build $preflightProjectRelativePath -c Debug "-bl:$preflightBinlogPath" 2>&1
        $preflightExitCode = $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }

    $preflightOutputLines = @($preflightOutputRaw | ForEach-Object { [string]$_ })
    $preflightOutputLines | Set-Content -Encoding utf8 -LiteralPath $preflightLogPath

    if($preflightExitCode -ne 0)
    {
        $preflightFailed = $true
        $preflightFailureDetail = "building the switched tree failed with exit code $preflightExitCode before Stryker was invoked; see $preflightLogPath and $preflightBinlogPath."

        # A sensible cap keeps a build with hundreds of restore errors from flooding the console; preflight.log
        # always carries the full, uncapped output regardless.
        $maxPreflightErrorLinesToPrint = 50
        $preflightErrorLinePattern = ': error |\bNU\d{3,5}\b|\bNETSDK\d{3,5}\b|\bMSB\d{3,5}\b|\bCS\d{3,5}\b'
        $matchedPreflightErrorLines = @($preflightOutputLines | Where-Object { $_ -match $preflightErrorLinePattern })
        $preflightErrorLinesToPrint = @($matchedPreflightErrorLines | Select-Object -First $maxPreflightErrorLinesToPrint)
        $omittedPreflightErrorLineCount = $matchedPreflightErrorLines.Count - $preflightErrorLinesToPrint.Count

        Write-Host "PREFLIGHTFAILED: $preflightFailureDetail"
        foreach($preflightErrorLine in $preflightErrorLinesToPrint)
        {
            Write-Host "  $preflightErrorLine"
        }

        if($omittedPreflightErrorLineCount -gt 0)
        {
            Write-Host "  ... $omittedPreflightErrorLineCount more error line(s) omitted; see $preflightLogPath."
        }

        Write-Host "Preflight build log: $preflightLogPath"
        Write-Host "Preflight build binary log: $preflightBinlogPath (embeds environment variables; it stays on this machine and is never copied to a share, the coordination branch or a commit)."
        Write-Host 'Stryker will not be invoked because the switched tree did not build.'
    }
    else
    {
        Write-Host 'Preflight: the switched tree built.'
    }

    if(-not $preflightFailed)
    {
        $strykerConfigFile = $configFile
        if($AdditionalTimeoutMs -gt 0)
        {
            $roundConfig = Get-Content -Raw $configFile | ConvertFrom-Json
            $roundConfig.'stryker-config' | Add-Member -NotePropertyName 'additional-timeout' -NotePropertyValue $AdditionalTimeoutMs -Force
            $strykerConfigFile = Join-Path $outputDir 'stryker-config.round.json'
            ($roundConfig | ConvertTo-Json -Depth 10) | Set-Content -Encoding utf8 $strykerConfigFile
        }

        $arguments = @(
            'stryker'
            '--config-file', $strykerConfigFile
            '--output', $outputDir
            '--project', 'Lumoin.Verisync.Core.csproj'
            '--log-to-file'
            '--msbuild-path', $resolvedMsBuildPath
        )

        if($Concurrency -gt 0)
        {
            $arguments += @('--concurrency', "$Concurrency")
        }

        if($Verbosity)
        {
            $arguments += @('--verbosity', $Verbosity)
        }

        if($Diagnostics)
        {
            $arguments += @('--diag')
        }

        foreach($pattern in $Mutate)
        {
            $arguments += @('--mutate', $pattern)
        }

        Write-Host "Running Stryker round '$Round' over Lumoin.Verisync.Core with $($Mutate.Count) mutate pattern(s)."

        # Started through ProcessStartInfo, rather than "& dotnet @arguments", so this script learns Stryker's own
        # launcher process id up front (needed to attribute job-object events to it and to its engine) and so every
        # mutate pattern reaches dotnet.exe as its own literal argument regardless of the characters it contains, with
        # no shell re-parsing in between.
        $strykerStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $strykerStartInfo.FileName = 'dotnet'
        foreach($argument in $arguments)
        {
            $strykerStartInfo.ArgumentList.Add($argument)
        }
        $strykerStartInfo.WorkingDirectory = $testProjectDir
        $strykerStartInfo.UseShellExecute = $false

        $strykerProcess = [System.Diagnostics.Process]::Start($strykerStartInfo)
        $strykerProcessId = $strykerProcess.Id

        if($MaxRunMinutes -gt 0)
        {
            $maxRunMilliseconds = [int]([math]::Ceiling($MaxRunMinutes * 60000))
            $exitedWithinLimit = $strykerProcess.WaitForExit($maxRunMilliseconds)
            if(-not $exitedWithinLimit)
            {
                $runTimeExceeded = $true
                Write-Host "RUNTIMEEXCEEDED: Stryker's launcher did not exit within $MaxRunMinutes minute(s); terminating every other process this run's job object owns."

                if($jobHandle -ne [IntPtr]::Zero)
                {
                    [StrykerJobMemoryLimiter]::TerminateOtherJobMembers($jobHandle, 1)
                }
                else
                {
                    # With no job object to read a membership list from (-ProcessMemoryCeilingGB 0), this can only
                    # walk and kill the launcher's own process tree, which misses a process that broke away from it.
                    # Kill($true) throws if the launcher has already exited in between, which must not stop this
                    # timeout from still being reported as RUNTIMEEXCEEDED below.
                    try
                    {
                        $strykerProcess.Kill($true)
                    }
                    catch
                    {
                        Write-Host "Could not kill Stryker's launcher process tree: $($_.Exception.Message); it may have already exited on its own."
                    }
                }

                [void]$strykerProcess.WaitForExit(10000)
            }
        }
        else
        {
            $strykerProcess.WaitForExit()
        }

        $strykerExitCode = if($strykerProcess.HasExited) { $strykerProcess.ExitCode } else { $null }

        $report = Get-ChildItem -LiteralPath $outputDir -Filter 'mutation-report.json' -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if($report)
        {
            Write-Host "Stryker report for round '$Round': $($report.FullName)"
        }
        else
        {
            Write-Host "Stryker finished round '$Round' without a mutation-report.json under $outputDir."
        }
    }
}
catch
{
    $unexpectedErrorMessage = "Unexpected error: $($_.Exception.Message)"
    Write-Host $unexpectedErrorMessage
}
finally
{
    $drainFailureMessage = $null
    $analysis = [pscustomobject]@{ RoundInvalidated = $false; InvalidationReasons = @(); RoleLines = @() }

    try
    {
    if($jobHandle -ne [IntPtr]::Zero)
    {
        # Microsoft documents job-object completion-port notifications as not guaranteed, so the launcher's own
        # WaitForExit returning is not proof the port has yet delivered that exit; this waits up to five seconds
        # for the drain thread to record the exit of the launcher and of every dotnet.exe it has identified so far
        # as one of the launcher's direct children (a Stryker engine), before the snapshot below is taken.
        $waitDeadlineUtc = [DateTime]::UtcNow.AddSeconds(5)
        do
        {
            $recordsForDrainCheck = [StrykerJobMemoryLimiter]::GetRecordsSnapshot()

            $exitedProcessIds = [System.Collections.Generic.HashSet[int]]::new()
            foreach($record in $recordsForDrainCheck)
            {
                if($record.EventType -eq 'ExitProcess' -or $record.EventType -eq 'AbnormalExitProcess')
                {
                    [void]$exitedProcessIds.Add($record.ProcessId)
                }
            }

            $engineCandidateIds = [System.Collections.Generic.HashSet[int]]::new()
            foreach($record in $recordsForDrainCheck)
            {
                if($record.EventType -eq 'NewProcess' -and $null -ne $strykerProcessId -and $record.ParentProcessId -eq [int]$strykerProcessId -and $record.ImageName -ieq 'dotnet.exe')
                {
                    [void]$engineCandidateIds.Add($record.ProcessId)
                }
            }

            $launcherDrained = $null -eq $strykerProcessId -or $exitedProcessIds.Contains([int]$strykerProcessId)
            $enginesDrained = $true
            foreach($engineId in $engineCandidateIds)
            {
                if(-not $exitedProcessIds.Contains($engineId))
                {
                    $enginesDrained = $false
                }
            }

            if($launcherDrained -and $enginesDrained)
            {
                break
            }

            Start-Sleep -Milliseconds 100
        }
        while([DateTime]::UtcNow -lt $waitDeadlineUtc)

        try
        {
            [StrykerJobMemoryLimiter]::QueryPeakUsage($jobHandle, [ref]$peakProcessMemoryBytes, [ref]$peakJobMemoryBytes)
            $peakProcessGb = [math]::Round($peakProcessMemoryBytes / 1GB, 2)
            $peakJobGb = [math]::Round($peakJobMemoryBytes / 1GB, 2)
            Write-Host "Largest process peaked at $peakProcessGb GB private against a ceiling of $ProcessMemoryCeilingGB GB; the whole run peaked at $peakJobGb GB."
        }
        catch
        {
            Write-Host "Could not read the job object's peak memory usage: $($_.Exception.Message)"
        }

        $drainFailureMessage = [StrykerJobMemoryLimiter]::GetDrainFailureMessage()
        $recordsSnapshot = [StrykerJobMemoryLimiter]::GetRecordsSnapshot()

        $recordsForJson = $recordsSnapshot | ForEach-Object {
            [pscustomobject]@{
                ProcessId              = $_.ProcessId
                ParentProcessId        = $_.ParentProcessId
                ImageName              = $_.ImageName
                EventType              = $_.EventType
                TimestampUtc           = $_.TimestampUtc.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
                PeakPagefileUsageBytes = $_.PeakPagefileUsageBytes
                ExitCode               = $_.ExitCode
            }
        }
        (ConvertTo-Json -InputObject @($recordsForJson) -Depth 5) | Set-Content -Encoding utf8 -LiteralPath (Join-Path $outputDir 'process-records.json')
    }
    else
    {
        (ConvertTo-Json -InputObject @()) | Set-Content -Encoding utf8 -LiteralPath (Join-Path $outputDir 'process-records.json')
    }

    $manifest = [ordered]@{
        Round                   = $Round
        RunStartUtc             = $runStartUtc.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
        ProcessMemoryCeilingGB  = $ProcessMemoryCeilingGB
        PreflightFailed         = $preflightFailed
        PreflightFailureDetail  = $preflightFailureDetail
        StrykerProcessId        = $strykerProcessId
        StrykerExitCode         = $strykerExitCode
        PeakProcessMemoryBytes  = $peakProcessMemoryBytes
        PeakJobMemoryBytes      = $peakJobMemoryBytes
        DrainFailureMessage     = $drainFailureMessage
        RunnerLogTimeZoneId     = $runnerLogTimeZone.Id
        UnexpectedErrorMessage  = $unexpectedErrorMessage
        RunTimeExceeded         = $runTimeExceeded
    }
    ($manifest | ConvertTo-Json -Depth 5) | Set-Content -Encoding utf8 -LiteralPath (Join-Path $outputDir 'run-manifest.json')

    $analysis = Invoke-RoundAnalysis `
        -OutputDir $outputDir `
        -RunStartUtc $runStartUtc `
        -ProcessMemoryCeilingGBValue $ProcessMemoryCeilingGB `
        -StrykerProcessId $strykerProcessId `
        -Records @($recordsSnapshot) `
        -PeakProcessMemoryBytes $peakProcessMemoryBytes `
        -DrainFailureMessage $drainFailureMessage `
        -RunnerLogTimeZone $runnerLogTimeZone `
        -IgnoreProcessExitsDueToTimeout $runTimeExceeded

    foreach($roleLine in $analysis.RoleLines)
    {
        Write-Host $roleLine
    }
    }
    catch
    {
        Write-Host "Could not complete the memory and invalidation analysis for this run: $($_.Exception.Message)"

        if(-not $unexpectedErrorMessage)
        {
            $unexpectedErrorMessage = "Unexpected error during analysis: $($_.Exception.Message)"
        }
    }

    # The revert restores the switched files from the commit at HEAD, never from the index, and retries while
    # another git process (a watcher's status or diff) is holding the repository. Git itself runs as a child of
    # this process while the job object is still open, so it stays subject to the same memory limit and finishes
    # normally; the job handle is closed only when the script exits below, never here. A failing checkout or a
    # failing status check both count as a failed revert, distinct from a checkout that ran cleanly but still left
    # the files reported dirty.
    $revertAttempt = 0
    $revertCheckout = $null
    do
    {
        $revertAttempt++
        $revertCheckout = Invoke-GitCheckoutHead -RepoRoot $repoRoot -Paths $switchedFiles

        if($revertCheckout.ExitCode -ne 0 -and $revertAttempt -lt 5)
        {
            Start-Sleep -Seconds 1
        }
    }
    while($revertCheckout.ExitCode -ne 0 -and $revertAttempt -lt 5)

    if($revertCheckout.ExitCode -ne 0)
    {
        $revertFailed = $true
        $revertFailureDetail = "'git checkout HEAD -- <switched files>' failed with exit code $($revertCheckout.ExitCode) after $revertAttempt attempt(s): $($revertCheckout.StdErrLines -join '; ')"
    }
    else
    {
        # A clean status is the only proof the revert actually landed, so the run's own exit code depends on it
        # rather than on Stryker's result or the checkout command's own exit code.
        $revertStatus = Get-GitPorcelainStatus -RepoRoot $repoRoot -Paths $switchedFiles

        if($revertStatus.ExitCode -ne 0)
        {
            $revertFailed = $true
            $revertFailureDetail = "'git status' after the revert failed with exit code $($revertStatus.ExitCode): $($revertStatus.StdErrLines -join '; ')"
        }
        elseif($revertStatus.StdOutLines.Count -gt 0)
        {
            $revertFailed = $true
            $revertFailureDetail = $revertStatus.StdOutLines -join '; '
        }
        else
        {
            Write-Host 'Reverted the VSTest and serialization switches: Directory.Build.props, the test project file, its lock file and Properties/AssemblyProperties.cs are back to their committed state.'
        }
    }
}

$zeroMutantsResult = Test-ZeroMutantsTested -OutputDir $outputDir

$finalExit = Get-FinalRunnerExit `
    -RevertFailed $revertFailed `
    -RevertFailureDetail $revertFailureDetail `
    -PreflightFailed $preflightFailed `
    -PreflightFailureDetail $preflightFailureDetail `
    -RoundInvalidated $analysis.RoundInvalidated `
    -InvalidationReasons $analysis.InvalidationReasons `
    -StrykerExitCode $strykerExitCode `
    -ZeroMutantsTested $zeroMutantsResult.ZeroMutantsTested `
    -UnexpectedErrorMessage $unexpectedErrorMessage `
    -RunTimeExceeded $runTimeExceeded

Write-Host "Run folder: $outputDir"
Exit-Runner -Code $finalExit.Code -Message $finalExit.Message
