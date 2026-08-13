using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RazorDbManager.Core;
using RazorDbManager.MySql.Transfer;

namespace RazorDbManager.MySql.Tests;

public sealed class LargeTransferPipelineTests
{
    private const long OneGiB = 1024L * 1024 * 1024;
    private const int BufferSize = 1024 * 1024;

    [Fact]
    public async Task OneGiBExportPipeline_ReusesOneBufferAndKeepsAccountingBounded()
    {
        byte[] buffer = new byte[BufferSize];
        RandomNumberGenerator.Fill(buffer);
        MemorylessArtifactContentStream artifactContent = new();
        RazorDbArtifactWriteSession artifact = new(
            new RazorDbArtifactDescriptor(
                new string('a', 48),
                "Main",
                "alice",
                "large-export.csv",
                "text/csv",
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1)),
            artifactContent);
        using IncrementalHash workerHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using Stream hashing = CreateProductHashingStream(artifact.Content, workerHash);
        await using CountingStream counting = new(hashing, OneGiB);

        int measurementThread = Environment.CurrentManagedThreadId;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (long written = 0; written < OneGiB; written += buffer.Length)
        {
            await counting.WriteAsync(buffer);
        }
        Assert.Equal(measurementThread, Environment.CurrentManagedThreadId);
        long allocatedByPipeline = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        long hashingBytes = GetProductHashingBytes(hashing);
        string workerDigest = Convert.ToHexStringLower(workerHash.GetHashAndReset());
        string artifactDigest = artifactContent.GetHashAndReset();

        Assert.Equal(OneGiB, counting.BytesProcessed);
        Assert.Equal(OneGiB, hashingBytes);
        Assert.Equal(OneGiB, artifactContent.BytesWritten);
        Assert.Equal(OneGiB / BufferSize, artifactContent.WriteCalls);
        Assert.Equal(BufferSize, artifactContent.MaximumWriteSize);
        Assert.Equal(1, artifactContent.DistinctBackingBufferCount);
        Assert.True(
            allocatedByPipeline < 8 * 1024 * 1024,
            $"The 1 GiB streaming pipeline allocated {allocatedByPipeline:N0} bytes on its execution thread.");
        Assert.Equal(workerDigest, artifactDigest);

        RazorDbException exception = await Assert.ThrowsAsync<RazorDbException>(
            () => counting.WriteAsync(new byte[1]).AsTask());

        Assert.Equal(RazorDbErrorCode.LimitExceeded, exception.Code);
        Assert.Equal(OneGiB, artifactContent.BytesWritten);
        Assert.Equal(OneGiB, GetProductHashingBytes(hashing));
    }

    private static Stream CreateProductHashingStream(Stream artifactContent, IncrementalHash hash)
    {
        Type workerType = typeof(global::RazorDbManager.RazorDbManagerRouting).Assembly.GetType(
            "RazorDbManager.RazorDbJobWorker",
            throwOnError: true)!;
        Type hashingStreamType = workerType.GetNestedType(
            "HashingWriteStream",
            BindingFlags.NonPublic) ?? throw new InvalidOperationException("The export hashing stream was not found.");
        return Assert.IsAssignableFrom<Stream>(Activator.CreateInstance(
            hashingStreamType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [artifactContent, hash],
            culture: null));
    }

    private static long GetProductHashingBytes(Stream hashing)
    {
        PropertyInfo property = hashing.GetType().GetProperty(
            "BytesWritten",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("The export hashing stream byte counter was not found.");
        return Assert.IsType<long>(property.GetValue(hashing));
    }

    private sealed class MemorylessArtifactContentStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private byte[]? _backingBuffer;

        public long BytesWritten { get; private set; }
        public long WriteCalls { get; private set; }
        public int MaximumWriteSize { get; private set; }
        public int DistinctBackingBufferCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

        public string GetHashAndReset() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrackBuffer(buffer);
            _hash.AppendData(buffer.Span);
            BytesWritten += buffer.Length;
            WriteCalls++;
            MaximumWriteSize = Math.Max(MaximumWriteSize, buffer.Length);
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        }

        private void TrackBuffer(ReadOnlyMemory<byte> buffer)
        {
            if (!MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment)
                || segment.Array is null)
            {
                throw new InvalidOperationException("The transfer did not retain the caller's fixed buffer.");
            }

            if (_backingBuffer is null)
            {
                _backingBuffer = segment.Array;
                DistinctBackingBufferCount = 1;
            }
            else if (!ReferenceEquals(_backingBuffer, segment.Array))
            {
                DistinctBackingBufferCount++;
            }
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
