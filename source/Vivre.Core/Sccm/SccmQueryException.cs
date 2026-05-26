namespace Vivre.Core.Sccm;

/// <summary>
/// Thrown when a ConfigMgr client query returns no usable data — typically because
/// the target isn't a ConfigMgr client (the <c>ROOT\ccm</c> namespace is absent) or
/// access was denied. Carries the first underlying PowerShell error when available.
/// </summary>
public sealed class SccmQueryException : Exception
{
    public SccmQueryException(string message) : base(message)
    {
    }
}
