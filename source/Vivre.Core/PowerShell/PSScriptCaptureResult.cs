namespace Vivre.Core.PowerShell;

public sealed record PSScriptCaptureLimits(int MaxRecordsPerStream = 200, int MaxCharactersPerStream = 64_000)
{
    public void Validate()
    {
        if (MaxRecordsPerStream <= 0) throw new ArgumentOutOfRangeException(nameof(MaxRecordsPerStream));
        if (MaxCharactersPerStream <= 0) throw new ArgumentOutOfRangeException(nameof(MaxCharactersPerStream));
    }
}

public sealed record PSScriptCaptureResult(
    IReadOnlyList<string> Output,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Verbose,
    IReadOnlyList<string> Debug,
    IReadOnlyList<string> Information,
    IReadOnlyList<string> Progress,
    bool HadErrors,
    bool WasTruncated);

public sealed class BoundedTextBuffer(int maximumCharacters)
{
    private readonly List<string> _lines = [];
    private int _length;
    public bool WasTruncated { get; private set; }
    public int MaximumCharacters { get; } = maximumCharacters > 0
        ? maximumCharacters : throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

    public void Append(string? value)
    {
        if (_length >= MaximumCharacters) { WasTruncated = true; return; }
        string text = value ?? string.Empty;
        int separator = _lines.Count == 0 ? 0 : Environment.NewLine.Length;
        int available = MaximumCharacters - _length - separator;
        if (available <= 0) { WasTruncated = true; return; }
        if (text.Length > available) { text = text[..available]; WasTruncated = true; }
        _lines.Add(text);
        _length += separator + text.Length;
    }

    public override string ToString() => string.Join(Environment.NewLine, _lines);
}

public static class PSScriptCapturePresentation
{
    public const int PerHostMaximumCharacters = 64_000;
    public const int AggregateMaximumCharacters = 256_000;

    public static string Format(PSScriptCaptureResult result, int maximumCharacters = PerHostMaximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (maximumCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        const string marker = "[output truncated]";
        if (maximumCharacters <= marker.Length + Environment.NewLine.Length)
            return result.WasTruncated ? marker[..Math.Min(maximumCharacters, marker.Length)] : FormatWithoutMarker(result, maximumCharacters);
        int bodyLimit = Math.Max(1, maximumCharacters - marker.Length - Environment.NewLine.Length);
        var buffer = new BoundedTextBuffer(bodyLimit);
        Append(buffer, result.Errors, "ERROR: ");
        Append(buffer, result.Warnings, "WARNING: ");
        Append(buffer, result.Output);
        Append(buffer, result.Verbose, "VERBOSE: ");
        Append(buffer, result.Debug, "DEBUG: ");
        Append(buffer, result.Information, "INFO: ");
        Append(buffer, result.Progress, "PROGRESS: ");
        string body = buffer.ToString();
        if (!result.WasTruncated && !buffer.WasTruncated) return body;
        return string.IsNullOrEmpty(body) ? marker : marker + Environment.NewLine + body;
    }

    private static string FormatWithoutMarker(PSScriptCaptureResult result, int maximumCharacters)
    {
        var buffer = new BoundedTextBuffer(maximumCharacters);
        Append(buffer, result.Errors, "ERROR: ");
        Append(buffer, result.Warnings, "WARNING: ");
        Append(buffer, result.Output);
        return buffer.ToString();
    }

    private static void Append(BoundedTextBuffer buffer, IReadOnlyList<string> values, string prefix = "")
    {
        foreach (string value in values) buffer.Append(prefix + value);
    }
}
