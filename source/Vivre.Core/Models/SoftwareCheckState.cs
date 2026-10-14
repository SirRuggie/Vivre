using Vivre.Core.Software;

namespace Vivre.Core.Models;

/// <summary>Software-check row lifecycle, including rows cancelled while waiting for a sweep slot.</summary>
public static class SoftwareCheckState
{
    public static async Task RunAsync(Computer computer, string query, string? serviceName,
        SemaphoreSlim throttle, Func<int, Task> check, CancellationToken token, Action<string>? queuedFailure = null)
    {
        int generation = Interlocked.Increment(ref computer.SoftwareCheckGeneration);
        // Clear only an error owned by the previous software check, preserving unrelated diagnostics.
        if (computer.SoftwareError is not null &&
            computer.LastError == $"Software check '{computer.SoftwareQuery}' — {computer.SoftwareError}")
            computer.LastError = null;
        computer.SoftwareFound = null;
        computer.SoftwareServiceDown = null;
        computer.SoftwareWarning = false;
        computer.SoftwareQueryWarning = null;
        computer.SoftwareServiceError = null;
        computer.SoftwareError = null;
        computer.SoftwareCheckedAt = null;
        computer.SoftwareQuery = query;
        computer.SoftwareServiceName = string.IsNullOrWhiteSpace(serviceName) ? null : serviceName;
        computer.SoftwareName = null;
        computer.SoftwareVersion = null;
        computer.SoftwareServiceState = null;
        computer.SoftwareCheck = $"Checking {query}…";
        try
        {
            await throttle.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            const string reason = "cancelled before check started";
            if (Fail(computer, query, reason, generation))
                queuedFailure?.Invoke(reason);
            throw;
        }
        try
        {
            await check(generation);
        }
        finally
        {
            throttle.Release();
        }
    }

    public static bool Complete(Computer computer, string query, SoftwareCheckResult result, int generation)
    {
        if (generation != computer.SoftwareCheckGeneration) return false;
        computer.SoftwareName = result.Name;
        computer.SoftwareVersion = result.Version;
        computer.SoftwareServiceState = result.ServiceState;
        computer.SoftwareQueryWarning = result.QueryWarning;
        computer.SoftwareServiceError = result.ServiceError;
        computer.SoftwareCheckedAt = DateTime.Now;
        computer.SoftwareFound = result.Found;
        computer.SoftwareWarning = !string.IsNullOrWhiteSpace(result.QueryWarning) || !string.IsNullOrWhiteSpace(result.ServiceError);
        computer.SoftwareServiceDown = result.ServiceError is not null || result.ServiceState is null
            ? null : result.Found && !string.Equals(result.ServiceState, "Running", StringComparison.OrdinalIgnoreCase);
        string label = string.IsNullOrWhiteSpace(result.Version) ? result.Name ?? query : $"{result.Name} {result.Version}";
        string servicePart = result.ServiceError is not null
            ? $" · service could not be checked — {result.ServiceError}"
            : result.ServiceState switch
            {
                null => string.Empty,
                "Running" => " · service running",
                "not found" => " · service not found",
                _ => $" · service {result.ServiceState.ToLowerInvariant()}",
            };
        computer.SoftwareCheck = (result.Found ? $"Installed — {label}" : $"Not installed — {query}") + servicePart;
        if (!string.IsNullOrWhiteSpace(result.QueryWarning))
            computer.SoftwareCheck += $" · partial check — {result.QueryWarning}";
        return true;
    }

    public static bool Fail(Computer computer, string query, string reason, int generation)
    {
        if (generation != computer.SoftwareCheckGeneration) return false;
        computer.SoftwareFound = null;
        computer.SoftwareServiceDown = null;
        computer.SoftwareWarning = true;
        computer.SoftwareError = reason;
        computer.SoftwareCheckedAt = DateTime.Now;
        computer.SoftwareCheck = $"Could not check — {reason}";
        computer.LastError = $"Software check '{query}' — {reason}";
        return true;
    }
}
