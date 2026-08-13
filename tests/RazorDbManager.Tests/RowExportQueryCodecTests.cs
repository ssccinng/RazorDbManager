using RazorDbManager.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace RazorDbManager.Tests;

public sealed class RowExportQueryCodecTests
{
    [Fact]
    public void RoundTrip_PreservesClosedFilterSortAndProjectionModel()
    {
        RowExportQuery query = new(
            new LogicalFilter(DbLogicalOperator.And,
            [
                new ComparisonFilter("name", DbComparisonOperator.Contains, DbValue.FromString("alice")),
                new InFilter("id", [DbValue.FromSignedInteger(1), DbValue.FromSignedInteger(2)]),
            ]),
            [new DbSort("name", DbSortDirection.Descending)],
            ["id", "name"]);

        RowExportQuery decoded = RowExportQueryCodec.Deserialize(RowExportQueryCodec.Serialize(query));

        Assert.Equal(query.Sorts, decoded.Sorts);
        Assert.Equal(query.Columns, decoded.Columns);
        LogicalFilter filter = Assert.IsType<LogicalFilter>(decoded.Filter);
        Assert.Equal(DbLogicalOperator.And, filter.Operator);
        Assert.Equal(2, filter.Terms.Count);
        Assert.Equal("alice", Assert.IsType<ComparisonFilter>(filter.Terms[0]).Value.Text);
        Assert.Equal("2", Assert.IsType<InFilter>(filter.Terms[1]).Values[1].Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"Filter\":{\"$type\":\"unknown\"}}")]
    [InlineData("{\"Unexpected\":true}")]
    public void Deserialize_RejectsMalformedOrExpandedPayloads(string json)
    {
        RazorDbException exception = Assert.Throws<RazorDbException>(() => RowExportQueryCodec.Deserialize(json));
        Assert.Equal(RazorDbErrorCode.Validation, exception.Code);
    }

    [Fact]
    public void Protector_RoundTripsWithoutEmbeddingPlaintextAndBindsDigest()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddDataProtection()
            .Services
            .BuildServiceProvider();
        RowExportQueryProtector protector = new(services.GetRequiredService<IDataProtectionProvider>());
        RowExportQuery query = new(
            new ComparisonFilter("email", DbComparisonOperator.Equal, DbValue.FromString("private@example.test")),
            [new DbSort("email")],
            ["email"]);

        ProtectedRowExportQuery protectedQuery = protector.Protect(query);
        RowExportQuery decoded = protector.Unprotect(protectedQuery.Payload, protectedQuery.PlaintextHash);

        Assert.DoesNotContain("private@example.test", protectedQuery.Payload, StringComparison.Ordinal);
        Assert.Equal("private@example.test", Assert.IsType<ComparisonFilter>(decoded.Filter).Value.Text);
        Assert.Throws<RazorDbException>(() => protector.Unprotect(protectedQuery.Payload, new string('0', 64)));
    }

    [Fact]
    public void Protector_PayloadCannotBeReadWithDifferentKeyRing()
    {
        string firstPath = Path.Combine(Path.GetTempPath(), "RazorDbManager.Tests", Guid.NewGuid().ToString("N"));
        string secondPath = Path.Combine(Path.GetTempPath(), "RazorDbManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        try
        {
            using ServiceProvider firstServices = new ServiceCollection()
                .AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(firstPath)).Services
                .BuildServiceProvider();
            using ServiceProvider secondServices = new ServiceCollection()
                .AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(secondPath)).Services
                .BuildServiceProvider();
            RowExportQueryProtector first = new(firstServices.GetRequiredService<IDataProtectionProvider>());
            RowExportQueryProtector second = new(secondServices.GetRequiredService<IDataProtectionProvider>());
            ProtectedRowExportQuery protectedQuery = first.Protect(new RowExportQuery(
                new ComparisonFilter("name", DbComparisonOperator.Equal, DbValue.FromString("sensitive"))));

            RazorDbException exception = Assert.Throws<RazorDbException>(
                () => second.Unprotect(protectedQuery.Payload, protectedQuery.PlaintextHash));

            Assert.Equal(RazorDbErrorCode.Forbidden, exception.Code);
            Assert.DoesNotContain("sensitive", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(firstPath, recursive: true);
            Directory.Delete(secondPath, recursive: true);
        }
    }

    [Fact]
    public void TerminalParameters_RemovesProtectedInputsButKeepsNonSensitiveDigest()
    {
        IReadOnlyDictionary<string, string> terminal = RowExportQueryProtector.TerminalParameters(
            new Dictionary<string, string>
            {
                [RowExportQueryProtector.PayloadParameter] = "ciphertext",
                [RowExportQueryProtector.HashParameter] = new string('b', 64),
                [RowExportQueryProtector.LegacyPlaintextParameter] = "legacy-sensitive-json",
                ["authorizationToken"] = "token",
                ["format"] = "Csv",
            });

        Assert.False(terminal.ContainsKey(RowExportQueryProtector.PayloadParameter));
        Assert.False(terminal.ContainsKey(RowExportQueryProtector.LegacyPlaintextParameter));
        Assert.False(terminal.ContainsKey("authorizationToken"));
        Assert.Equal(new string('b', 64), terminal[RowExportQueryProtector.HashParameter]);
        Assert.Equal("cleared", terminal[RowExportQueryProtector.ClearedParameter]);
        Assert.Equal("Csv", terminal["format"]);
    }
}
