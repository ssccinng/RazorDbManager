using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorDbManager.Core;

namespace RazorDbManager;

internal sealed class RazorDbStoreInitializer(
    IRazorDbJobStore jobs,
    IRazorDbArtifactStore artifacts,
    IRazorDbAuditSink auditSink,
    ILogger<RazorDbStoreInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            IReadOnlyList<RazorDbJobRecord> interrupted = await jobs.ListAsync(
                new RazorDbJobQuery(Status: RazorDbJobStatus.Running, Limit: 500), cancellationToken);
            if (interrupted.Count == 0) break;
            foreach (RazorDbJobRecord job in interrupted)
            {
                bool uncertainImport = job.Kind is RazorDbJobKind.CsvImport or RazorDbJobKind.SqlRestore;
                string resultCode = uncertainImport
                    ? "host-interrupted-outcome-unknown"
                    : "host-interrupted";
                RazorDbJobRecord? recovered = await jobs.TryUpdateAsync(job.Id, job.Version, new RazorDbJobUpdate
                {
                    Status = RazorDbJobStatus.Failed,
                    RowsProcessed = job.RowsProcessed,
                    BytesProcessed = job.BytesProcessed,
                    OutputArtifactId = job.OutputArtifactId,
                    ResultCode = resultCode,
                    Parameters = RowExportQueryProtector.TerminalParameters(job.Parameters),
                }, cancellationToken);
                if (recovered is null) continue;
                await AppendRecoveryAuditAsync(job, resultCode, cancellationToken);
            }
        }
        _ = await artifacts.DeleteExpiredAsync(DateTimeOffset.UtcNow, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async ValueTask AppendRecoveryAuditAsync(
        RazorDbJobRecord job,
        string resultCode,
        CancellationToken cancellationToken)
    {
        bool isExport = job.Kind is RazorDbJobKind.CsvExport or RazorDbJobKind.SqlDump;
        try
        {
            await auditSink.AppendAsync(new RazorDbAuditRecord
            {
                Id = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                ActorId = job.ActorId,
                DatabaseId = job.DatabaseId,
                Operation = isExport ? RazorDbOperation.Export : RazorDbOperation.Import,
                Status = RazorDbAuditStatus.Failed,
                ResultCode = resultCode,
                PayloadHash = !isExport && job.Parameters.TryGetValue("inputArtifactSha256", out string? digest)
                    ? digest
                    : null,
                SqlClassification = job.Kind switch
                {
                    RazorDbJobKind.SqlRestore => "RESTORE",
                    RazorDbJobKind.SqlDump => "DUMP",
                    _ => null,
                },
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["jobId"] = job.Id.ToString("N"),
                    ["recovery"] = "startup",
                },
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                "RazorDbManager could not persist the startup recovery audit for job {JobId} because {ErrorType} occurred. The job remains terminal with result {ResultCode}.",
                job.Id,
                exception.GetType().Name,
                resultCode);
        }
    }
}
