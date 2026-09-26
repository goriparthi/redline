// Points the diagnostics log at a temp file before any test runs, so failures logged by the
// code under test never land in the real ~/.local/share/redline.
using System.Runtime.CompilerServices;

namespace Redline.Core.Tests;

internal static class Setup
{
    [ModuleInitializer]
    internal static void Init() =>
        Redline.Core.Diag.Configure("test", Path.Combine(Path.GetTempPath(), "redline-core-tests-diag-" + Environment.ProcessId + ".ndjson"));
}
