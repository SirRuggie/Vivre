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

    public Task<string> EnableAsync(string host, ConnectionCredential? credential = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        cancellationToken.ThrowIfCancellationRequested();

        // CimSession.InvokeMethod is synchronous; run it off the caller's thread.
        return Task.Run(() => Enable(host, credential), cancellationToken);
    }

    private static string Enable(string host, ConnectionCredential? credential)
    {
        try
        {
            using var options = new DComSessionOptions();
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

            using CimMethodResult result =
                session.InvokeMethod(@"root\cimv2", "Win32_Process", "Create", arguments);

            uint returnValue = Convert.ToUInt32(result.ReturnValue.Value);
            if (returnValue != 0)
            {
                throw new WinRmEnableException(
                    $"Win32_Process.Create on '{host}' returned {returnValue} ({DescribeReturn(returnValue)}).");
            }

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
