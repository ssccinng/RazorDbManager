using System.Text;
using RazorDbManager.Core;
using RazorDbManager.MySql.Transfer;

namespace RazorDbManager.MySql.Tests;

public sealed class CsvRecordLimitStreamTests
{
    [Fact]
    public async Task ReadAsync_RejectsWholeMultilineRecordByUtf8ByteCount()
    {
        var bytes = Encoding.UTF8.GetBytes("id,text\n1,\"数据库\nsecond line\"\n");
        await using var source = new MemoryStream(bytes);
        await using var limited = new CsvRecordLimitStream(source, 20);

        var exception = await Assert.ThrowsAsync<RazorDbException>(() => DrainAsync(limited, 3));

        Assert.Equal(RazorDbErrorCode.LimitExceeded, exception.Code);
        Assert.Contains("CSV record", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ResetsLimitAtEachUnquotedRecordBoundary()
    {
        const string csv = "12345\n67890\nabcde\n";
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await using var limited = new CsvRecordLimitStream(source, 6);

        var result = await DrainAsync(limited, 2);

        Assert.Equal(csv, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public async Task ReadAsync_DoesNotResetAtNewlineInsideQuotedField()
    {
        const string csv = "\"abc\ndef\"\n";
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await using var limited = new CsvRecordLimitStream(source, 7);

        await Assert.ThrowsAsync<RazorDbException>(() => DrainAsync(limited, 1));
    }

    private static async Task<byte[]> DrainAsync(Stream source, int bufferSize)
    {
        await using var destination = new MemoryStream();
        var buffer = new byte[bufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read));
        }

        return destination.ToArray();
    }
}
