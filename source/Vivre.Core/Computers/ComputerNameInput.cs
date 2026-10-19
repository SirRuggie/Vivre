namespace Vivre.Core.Computers;

/// <summary>The shared plain-text input format for Paste and Quick Add.</summary>
public static class ComputerNameInput
{
    public static string[] Parse(string? text) => (text ?? string.Empty)
        .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
