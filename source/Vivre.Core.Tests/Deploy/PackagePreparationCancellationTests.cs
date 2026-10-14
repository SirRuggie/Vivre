using Vivre.Core.Deploy;

namespace Vivre.Core.Tests.Deploy;

public sealed class PackagePreparationCancellationTests
{
    [Fact]
    public void Cancellation_during_hash_stops_before_next_read()
    {
        using var cancel = new CancellationTokenSource();
        using var stream = new CancellingStream(cancel);
        Assert.ThrowsAny<OperationCanceledException>(() => PackagePublication.HashStream(stream, cancel.Token));
        Assert.Equal(1, stream.Reads);
    }

    [Fact]
    public void Cancelled_preparation_does_not_touch_source_or_destination()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PackagePublication.Prepare(
            @"C:\dummy-missing-source", false, @"C:\dummy-missing-target", true, cancel.Token));
    }

    private sealed class CancellingStream(CancellationTokenSource cancel) : MemoryStream(new byte[1024 * 1024])
    {
        internal int Reads;
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            int read = base.Read(buffer, offset, count);
            cancel.Cancel();
            return read;
        }
    }
}
