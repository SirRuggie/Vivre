using System.Management.Automation;

namespace Vivre.Core.Columns;

/// <summary>One user-defined grid column: a display <paramref name="Name"/> (also the header + the
/// per-machine value key) and a PowerShell one-liner <paramref name="Script"/> that runs on each machine
/// and whose output becomes that machine's cell.</summary>
public sealed record CustomColumnSpec(string Name, string Script);

/// <summary>Thrown when a custom-column run couldn't read the target at all (unreachable / nothing returned).</summary>
public sealed class CustomColumnProbeException : Exception
{
    public CustomColumnProbeException(string message) : base(message)
    {
    }
}

/// <summary>
/// Runs a set of user-defined column scripts on a machine and returns each column's value — the engine
/// behind custom grid columns. One round-trip per host evaluates every column (like <see
/// cref="Vitals.VitalsProbe"/> returns many fields in one call), so adding a column or refreshing the grid
/// is one call per machine, not one per column.
/// </summary>
public interface ICustomColumnProbe
{
    /// <summary>
    /// Evaluates each column's script on <paramref name="host"/> (local when
    /// <see cref="PowerShell.HostName.IsLocal"/>, else WinRM) and returns a column-name → value map. A
    /// column whose script errors yields an <c>"ERR: …"</c> value rather than failing the others.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> RunAsync(
        string host,
        IReadOnlyList<CustomColumnSpec> columns,
        PSCredential? credential,
        CancellationToken cancellationToken);
}
