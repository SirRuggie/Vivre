using System.Text.Json;
using Vivre.Core.IO;
using Vivre.Core.Operations;

namespace Vivre.Core.Deploy;

internal sealed record StagingTransferRecord(string Id, string OwnerToken, string Host, string Directory, bool InitializationConfirmed = false);

/// <summary>Durable exact-path cleanup records. An exclusive local file lease excludes active transfers
/// and concurrent retry attempts, including other Vivre processes using the same journal.</summary>
public sealed class StagingTransferJournal
{
    private readonly string _directory;

    public StagingTransferJournal(string? directory = null) => _directory = Path.GetFullPath(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vivre", "staging-transfers"));

    internal Lease Create(string host, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        string id = Guid.NewGuid().ToString("N");
        var record = new StagingTransferRecord(id, Guid.NewGuid().ToString("N"), host,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, ".vivre-transfer-" + id));
        Directory.CreateDirectory(_directory);
        Lease lease = Acquire(id) ?? throw new IOException("Could not acquire the new transfer journal lease.");
        try
        {
            AtomicFileWriter.Write(RecordPath(id), JsonSerializer.Serialize(record));
            lease.Record = record;
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal IEnumerable<Lease> AcquirePending(string host)
    {
        if (!Directory.Exists(_directory)) yield break;
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (!IsId(id)) throw new InvalidDataException($"Invalid staging cleanup record: {file}");
            Lease? lease = Acquire(id);
            if (lease is null) continue; // Active uploader or another cleanup owns this record.
            bool handedOff = false;
            try
            {
                if (!File.Exists(file)) continue; // Another owner completed before we acquired the lease.
                StagingTransferRecord record = JsonSerializer.Deserialize<StagingTransferRecord>(File.ReadAllText(file))
                    ?? throw new InvalidDataException($"Empty staging cleanup record: {file}");
                Validate(record, id);
                if (!string.Equals(HostOperationCoordinator.NormalizeHost(record.Host),
                    HostOperationCoordinator.NormalizeHost(host), StringComparison.OrdinalIgnoreCase)) continue;
                lease.Record = record;
                handedOff = true;
                yield return lease;
            }
            finally { if (!handedOff) lease.Dispose(); }
        }
    }

    private Lease? Acquire(string id)
    {
        try
        {
            var stream = new FileStream(Path.Combine(_directory, id + ".lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new Lease(this, id, stream);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
    }

    private string RecordPath(string id) => Path.Combine(_directory, id + ".json");
    private static bool IsId(string? value) => value is not null && Guid.TryParseExact(value, "N", out _);

    private static void Validate(StagingTransferRecord record, string id)
    {
        if (record.Id != id || !IsId(record.OwnerToken) || string.IsNullOrWhiteSpace(record.Host) ||
            string.IsNullOrWhiteSpace(record.Directory) || !Path.IsPathFullyQualified(record.Directory) ||
            !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(record.Directory)),
                ".vivre-transfer-" + id, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(record.Directory), record.Directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Staging cleanup record has invalid ownership or path data; cleanup refused.");
    }

    internal sealed class Lease(StagingTransferJournal journal, string id, FileStream stream) : IDisposable
    {
        internal StagingTransferRecord Record { get; set; } = null!;
        internal void MarkInitialized()
        {
            Record = Record with { InitializationConfirmed = true };
            AtomicFileWriter.Write(journal.RecordPath(id), JsonSerializer.Serialize(Record));
        }
        internal void Complete() => File.Delete(journal.RecordPath(id));
        public void Dispose() => stream.Dispose();
    }
}
