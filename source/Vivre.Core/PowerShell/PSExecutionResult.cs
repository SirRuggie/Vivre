using System.Management.Automation;

namespace Vivre.Core.PowerShell;

/// <summary>
/// Outcome of running a script through <see cref="IPowerShellHost"/>. Captures the
/// object pipeline plus the error/warning streams — the legacy app swallowed those,
/// which is exactly why failures were silent (REBUILD_PLAN.md §8.2). Errors and
/// warnings are pre-formatted strings so callers can log/show them without taking a
/// dependency on the System.Management.Automation record types.
/// </summary>
/// <param name="Output">Objects emitted on the success pipeline.</param>
/// <param name="Errors">Formatted records from the error stream.</param>
/// <param name="Warnings">Formatted records from the warning stream.</param>
/// <param name="HadErrors">True if the engine flagged errors (even when no record was emitted).</param>
public sealed record PSExecutionResult(
    IReadOnlyList<PSObject> Output,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool HadErrors);
