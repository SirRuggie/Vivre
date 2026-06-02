namespace Vivre.Core.Vitals;

/// <summary>
/// Thrown when a vitals pull returns no usable data — typically because the host was unreachable
/// over WinRM, or every probe was denied. Carries the first underlying PowerShell error when
/// available (mirrors <see cref="Sccm.SccmQueryException"/>).
/// </summary>
public sealed class VitalsProbeException : Exception
{
    public VitalsProbeException(string message) : base(message)
    {
    }
}
