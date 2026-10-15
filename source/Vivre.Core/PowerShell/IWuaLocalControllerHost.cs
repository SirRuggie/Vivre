using System.Management.Automation;

namespace Vivre.Core.PowerShell;

/// <summary>
/// Internal capability for fixed, app-generated WUA bootstrap and cleanup controllers. The opaque
/// script token cannot be constructed outside Vivre.Core, so operator-supplied scripts cannot enter
/// the authorization-isolated runspace.
/// </summary>
internal interface IWuaLocalControllerHost
{
    Task<PSExecutionResult> RunWuaControllerAsync(
        WuaControllerScript controller,
        Action<PSObject> onOutput,
        CancellationToken cancellationToken = default);
}

internal sealed class WuaControllerScript
{
    private WuaControllerScript(string text) => Text = text;

    internal string Text { get; }

    internal static WuaControllerScript FromGenerated(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new WuaControllerScript(text);
    }
}
