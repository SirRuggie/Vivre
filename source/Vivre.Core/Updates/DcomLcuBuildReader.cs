using System.Globalization;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Vivre.Core.Updates;

/// <inheritdoc cref="ILcuBuildReader"/>
/// <remarks>
/// Reads the build over a DCOM <see cref="CimSession"/> on the ambient Windows login (mirrors
/// <c>DcomVitalsProbe</c>), via <c>StdRegProv</c> — so it works on the Vision boxes where WinRM/Kerberos
/// is rejected. Any failure (offline, still booting, DCOM not up, denied) returns nulls so the caller
/// retries rather than declaring a rollback.
/// </remarks>
public sealed class DcomLcuBuildReader : ILcuBuildReader
{
    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(8);
    private const uint HklmHive = 0x80000002;
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public Task<(int? CurrentBuild, int? Ubr)> ReadAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // CIM calls are synchronous — run off the caller's thread so the wave stays responsive.
        return Task.Run(() => ReadSync(host, cancellationToken), cancellationToken);
    }

    private static (int? CurrentBuild, int? Ubr) ReadSync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var options = new DComSessionOptions { Timeout = CimTimeout };
            using CimSession session = CimSession.Create(host, options);
            using var cimOptions = new CimOperationOptions
            {
                Timeout = CimTimeout,
                CancellationToken = cancellationToken,
            };

            int? currentBuild = ReadStringAsInt(session, cimOptions, "CurrentBuild");
            int? ubr = ReadDword(session, cimOptions, "UBR");
            return (currentBuild, ubr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline / still booting / DCOM not up / denied — not a verdict; the caller retries.
            return (null, null);
        }
    }

    private static int? ReadStringAsInt(CimSession session, CimOperationOptions cimOptions, string valueName)
    {
        string? s = InvokeRegRead(session, cimOptions, "GetStringValue", valueName, "sValue")?.ToString();
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    private static int? ReadDword(CimSession session, CimOperationOptions cimOptions, string valueName)
    {
        object? v = InvokeRegRead(session, cimOptions, "GetDWORDValue", valueName, "uValue");
        if (v is null)
        {
            return null;
        }

        try
        {
            return (int)Convert.ToUInt32(v);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Invokes a StdRegProv read method (GetStringValue / GetDWORDValue) against the
    /// CurrentVersion key and returns the named out-parameter, or null when the value is absent / the
    /// call failed (ReturnValue != 0).</summary>
    private static object? InvokeRegRead(
        CimSession session, CimOperationOptions cimOptions, string method, string valueName, string outParam)
    {
        using var inParams = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("hDefKey", HklmHive, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("sSubKeyName", CurrentVersionKey, CimType.String, CimFlags.In),
            CimMethodParameter.Create("sValueName", valueName, CimType.String, CimFlags.In),
        };

        using CimMethodResult result = session.InvokeMethod(@"root\cimv2", "StdRegProv", method, inParams, cimOptions);
        object? rv = result.ReturnValue?.Value;
        if (rv is null || Convert.ToUInt32(rv) != 0)
        {
            return null; // non-zero ReturnValue = value/key not found or access denied
        }

        return result.OutParameters?[outParam]?.Value;
    }
}
