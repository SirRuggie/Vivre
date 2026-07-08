using Vivre.Core.Credentials;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Remoting;

/// <inheritdoc cref="IWinRmEnabler"/>
public sealed class WinRmEnabler : IWinRmEnabler
{
    // -NoProfile keeps it fast; -ExecutionPolicy Bypass mirrors the legacy plugin.
    private const string EnableCommand =
        "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"Enable-PSRemoting -Force\"";

    // Matches DcomRebootTrigger's CimTimeout — the other state-changing DCOM action (the read probes
    // use 8s). Bounds both the session and the InvokeMethod so a hung target's WMI provider can't pin
    // this call forever; this was the only DCOM site of seven with no timeout and no token on the invoke.
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(20);

    public Task<string> EnableAsync(string host, ConnectionCredential? credential = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        cancellationToken.ThrowIfCancellationRequested();

        // CimSession.InvokeMethod is synchronous; run it off the caller's thread.
        return Task.Run(() => Enable(host, credential, cancellationToken), cancellationToken);
    }

    private static string Enable(string host, ConnectionCredential? credential, CancellationToken cancellationToken)
    {
        try
        {
            using var options = new DComSessionOptions { Timeout = CimTimeout };
            if (credential is not null)
            {
                options.AddDestinationCredentials(new CimCredential(
                    PasswordAuthenticationMechanism.Default,
                    credential.Domain,
                    credential.UserName,
                    credential.Password));
            }

            using CimSession session = CimSession.Create(host, options);

            using var arguments = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("CommandLine", EnableCommand, CimFlags.In),
            };

            using var operationOptions = new CimOperationOptions
            {
                Timeout = CimTimeout,
                CancellationToken = cancellationToken,
            };
            using CimMethodResult result =
                session.InvokeMethod(@"root\cimv2", "Win32_Process", "Create", arguments, operationOptions);

            InterpretCreateReturn(host, result.ReturnValue?.Value);

            object? processId = result.OutParameters["ProcessId"]?.Value;
            return processId is null
                ? $"Enable-PSRemoting started on {host}"
                : $"Enable-PSRemoting started on {host} (PID {processId})";
        }
        catch (CimException ex)
        {
            throw new WinRmEnableException($"DCOM call to '{host}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>Interprets Win32_Process.Create's result code. A null code is a FAILURE: a
    /// successful Create always returns an explicit 0, and Convert.ToUInt32(null) would coerce a
    /// never-populated result to 0 — reporting a start that can't be confirmed. (Reading via
    /// <c>ReturnValue?.Value</c> at the call site also keeps a null ReturnValue parameter from
    /// escaping as a raw NRE past the CimException translation.)</summary>
    internal static uint InterpretCreateReturn(string host, object? rawReturnValue)
    {
        if (rawReturnValue is null)
        {
            throw new WinRmEnableException(
                $"Win32_Process.Create on '{host}' returned no result code — can't confirm Enable-PSRemoting started.");
        }

        uint returnValue = Convert.ToUInt32(rawReturnValue);
        if (returnValue != 0)
        {
            throw new WinRmEnableException(
                $"Win32_Process.Create on '{host}' returned {returnValue} ({DescribeReturn(returnValue)}).");
        }

        return returnValue;
    }

    private static string DescribeReturn(uint code) => code switch
    {
        2 => "access denied",
        3 => "insufficient privilege",
        8 => "unknown failure",
        9 => "path not found",
        21 => "invalid parameter",
        _ => "see Win32_Process.Create return codes",
    };
}
