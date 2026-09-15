// The property tests use CsCheck's Gen...Sample(...) and run unseeded, so each run explores fresh
// random cases. When a Sample fails it has already shrunk to a minimal counter-example and prints the
// seed that produced it, for example:
//
//     CsCheck.CsCheckException: Set seed: "0ycPmO1H_kG7" or -e CsCheck_Seed=0ycPmO1H_kG7 to reproduce (3 shrinks, 43 skipped, 100 total).
//
// To rerun exactly that case, set the CsCheck_Seed environment variable to the printed value. CsCheck
// reads it for the whole process and applies it to every Sample run, so combine it with a test filter.
// CsCheck_Iter overrides the iteration count (default 100), and CsCheck_Time runs for a number of
// seconds instead.
//
// The suite is a Microsoft.Testing.Platform application, so run the application itself rather than
// dotnet test. Reproduce it in PowerShell like this:
//
//     $env:CsCheck_Seed = "0ycPmO1H_kG7"
//     dotnet run --project test/Lumoin.Verisync.Tests -c Release -- --filter "FullyQualifiedName~GCounterPropertyTests"
//     Remove-Item Env:\CsCheck_Seed
//
// Or reproduce it in bash like this:
//     CsCheck_Seed=0ycPmO1H_kG7 CsCheck_Iter=1000 \
//       dotnet run --project test/Lumoin.Verisync.Tests -c Release -- --filter "FullyQualifiedName~GCounterPropertyTests"
//
// Never pin a seed anywhere persistent, such as a settings file, the project file or a CI environment:
// a fixed seed turns the property tests into a single example and destroys their exploration. Pin one
// only on the command line for the length of one debugging session, then clear it.

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
[assembly: DiscoverInternals]
