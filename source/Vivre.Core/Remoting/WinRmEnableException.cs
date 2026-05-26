namespace Vivre.Core.Remoting;

/// <summary>Thrown when enabling WinRM over DCOM fails (unreachable, access denied, non-zero return).</summary>
public sealed class WinRmEnableException : Exception
{
    public WinRmEnableException(string message) : base(message)
    {
    }

    public WinRmEnableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
