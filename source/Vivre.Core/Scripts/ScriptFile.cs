namespace Vivre.Core.Scripts;

/// <summary>A saved PowerShell script in the library.</summary>
/// <param name="Name">Display name (file name without the .ps1 extension).</param>
/// <param name="FullPath">Absolute path to the .ps1 file.</param>
/// <param name="Category">Relative folder under the library root (e.g. "Reboot"); empty for a
/// script at the root. Used to build the cascading "Run script" menu.</param>
public sealed record ScriptFile(string Name, string FullPath, string Category = "");
