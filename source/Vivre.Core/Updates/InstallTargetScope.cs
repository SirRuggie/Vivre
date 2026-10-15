using Vivre.Core.Models;

namespace Vivre.Core.Updates;

/// <summary>Captured rows and host identities for one install confirmation.</summary>
public sealed class InstallTargetScope
{
    private readonly string[] _names;

    private InstallTargetScope(IEnumerable<Computer> targets)
    {
        Targets = Array.AsReadOnly(targets.ToArray());
        _names = Targets.Select(c => c.Name).ToArray();
    }

    public IReadOnlyList<Computer> Targets { get; }

    public static InstallTargetScope Capture(IEnumerable<Computer> targets) => new(targets);

    public bool IsUnchanged() =>
        !Targets.Where((row, i) => !string.Equals(row.Name, _names[i], StringComparison.Ordinal)).Any();
}
