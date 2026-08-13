using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RazorDbManager.Core;
using RazorDbManager.Tests.Infrastructure;

namespace RazorDbManager.Tests;

public sealed class EndpointAuthorizationTests
{
    [Fact]
    public async Task BinaryEndpoint_StreamsOnceForBoundActorAndAuditsLifecycle()
    {
        RecordingAuditSink audit = new();
        StubBinaryDataProvider data = new([0, 1, 255]);
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            auditSink: audit,
            dataProvider: data);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);

        using HttpResponseMessage issue = await IssueBinaryTokenAsync(client, "alice");
        Assert.Equal(HttpStatusCode.Redirect, issue.StatusCode);
        string url = Assert.IsType<Uri>(issue.Headers.Location).OriginalString;
        Assert.DoesNotContain("PRIMARY", url, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant_id", url, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("42")), url, StringComparison.Ordinal);

        UriBuilder tamperedBuilder = new(new Uri(client.BaseAddress!, url));
        string tampered = tamperedBuilder.Query.Replace("payload=", "payload=x", StringComparison.Ordinal);
        tamperedBuilder.Query = tampered.TrimStart('?');
        using HttpResponseMessage tamperedResponse = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, tamperedBuilder.Uri.PathAndQuery, "alice"));
        Assert.Equal(HttpStatusCode.BadRequest, tamperedResponse.StatusCode);
        Assert.Equal(1, data.OpenCalls);

        using HttpResponseMessage wrongActor = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, url, "bob"));
        Assert.Equal(HttpStatusCode.Forbidden, wrongActor.StatusCode);
        using HttpResponseMessage download = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, url, "alice"));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal([0, 1, 255], await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("no-store", download.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);

        using HttpResponseMessage replay = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, url, "alice"));
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(2, data.OpenCalls);
        RazorDbAuditRecord[] lifecycle = audit.Records
            .Where(record => record.ActorId == "alice" && record.Operation == RazorDbOperation.DownloadBinary)
            .ToArray();
        Assert.Equal([RazorDbAuditStatus.Started, RazorDbAuditStatus.Completed, RazorDbAuditStatus.Denied],
            lifecycle.Select(record => record.Status));
        Assert.All(lifecycle, record => Assert.Equal("payload", record.Resource?.Subresource));
    }

    [Fact]
    public async Task BinaryEndpoint_DeniesCapabilityBeforeOpeningProvider()
    {
        RecordingAuditSink audit = new();
        StubBinaryDataProvider data = new([1]);
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            auditSink: audit,
            dataProvider: data);
        host.SetCapabilities(RazorDbCapabilitySets.DataEditor);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);

        using HttpResponseMessage response = await IssueBinaryTokenAsync(client, "alice");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, data.OpenCalls);
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal("capability-disabled", denied.ResultCode);
    }

    [Fact]
    public async Task BinaryEndpoint_FailsClosedWhenStartedAuditCannotBeWritten()
    {
        RecordingAuditSink audit = new(record =>
            record.Operation == RazorDbOperation.DownloadBinary && record.Status == RazorDbAuditStatus.Started
                ? new IOException("audit unavailable")
                : null);
        StubBinaryDataProvider data = new([1, 2]);
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            auditSink: audit,
            dataProvider: data);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        using HttpResponseMessage issue = await IssueBinaryTokenAsync(client, "alice");
        string url = Assert.IsType<Uri>(issue.Headers.Location).OriginalString;

        await Assert.ThrowsAsync<IOException>(async () =>
            await client.SendAsync(RazorDbTestHost.Request(HttpMethod.Get, url, "alice")));
        Assert.Equal(1, data.OpenCalls);
    }

    [Fact]
    public async Task BinaryEndpoint_RejectsProviderLengthAboveRegistrationLimitAndDisposesSession()
    {
        RecordingAuditSink audit = new();
        StubBinaryDataProvider data = new([1], reportedLengths: [1, 30L * 1024 * 1024]);
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            auditSink: audit,
            dataProvider: data);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        using HttpResponseMessage issue = await IssueBinaryTokenAsync(client, "alice");
        string url = Assert.IsType<Uri>(issue.Headers.Location).OriginalString;

        using HttpResponseMessage response = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, url, "alice"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(2, data.DisposeCalls);
        Assert.Contains(audit.Records, record =>
            record.Status == RazorDbAuditStatus.Failed && record.ResultCode == "limitexceeded");
    }
    [Fact]
    public async Task StatusEndpoint_RequiresAuthenticationAndDisablesCaching()
    {
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync();
        HttpClient client = Assert.IsType<HttpClient>(host.Client);

        using HttpResponseMessage anonymous = await client.SendAsync(RazorDbTestHost.Request(HttpMethod.Get, "/_razor-db-manager/status"));
        using HttpResponseMessage authenticated = await client.SendAsync(RazorDbTestHost.Request(HttpMethod.Get, "/_razor-db-manager/status", "alice"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Contains("no-store", authenticated.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        string json = await authenticated.Content.ReadAsStringAsync();
        Assert.Contains("Main", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Unused", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusEndpoint_AuditsAuthenticatedAccessPolicyDenial()
    {
        RecordingAuditSink audit = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(auditSink: audit);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);

        using HttpResponseMessage response = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, "/_razor-db-manager/status", "blocked"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal("blocked", denied.ActorId);
        Assert.Equal("Main", denied.DatabaseId);
        Assert.Equal(RazorDbOperation.BrowseMetadata, denied.Operation);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal("access-policy", denied.ResultCode);
    }

    [Fact]
    public async Task ArtifactEndpoint_EnforcesOwnerAndConsumesDownloadTokenOnce()
    {
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync();
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        IRazorDbArtifactStore artifacts = host.Services.GetRequiredService<IRazorDbArtifactStore>();
        byte[] payload = "private-export"u8.ToArray();
        RazorDbArtifactWriteSession write = await artifacts.CreateWriteAsync(new RazorDbArtifactCreateRequest(
            "Main", "alice", "export.csv", "text/csv", DateTimeOffset.UtcNow.AddMinutes(5)));
        await write.Content.WriteAsync(payload);
        await write.Content.DisposeAsync();
        string digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        RazorDbArtifactDescriptor artifact = await artifacts.CompleteWriteAsync(write.Descriptor.Id, payload.Length, digest);

        (string bobCookie, string bobToken) = await GetAntiforgeryAsync(client, "bob");
        (string aliceCookie, string aliceToken) = await GetAntiforgeryAsync(client, "alice");

        using HttpResponseMessage bobIssue = await client.SendAsync(TokenRequest(
            $"/_razor-db-manager/artifacts/{artifact.Id}/token", "bob", bobCookie, bobToken,
            ComponentScope(host, "bob")));
        Assert.Equal(HttpStatusCode.Forbidden, bobIssue.StatusCode);

        using HttpResponseMessage issue = await client.SendAsync(TokenRequest(
            $"/_razor-db-manager/artifacts/{artifact.Id}/token", "alice", aliceCookie, aliceToken,
            ComponentScope(host, "alice")));
        Assert.Equal(HttpStatusCode.Redirect, issue.StatusCode);
        string url = Assert.IsType<Uri>(issue.Headers.Location).OriginalString;

        using HttpResponseMessage download = await client.SendAsync(RazorDbTestHost.Request(HttpMethod.Get, url, "alice"));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", download.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);

        using HttpResponseMessage replay = await client.SendAsync(RazorDbTestHost.Request(HttpMethod.Get, url, "alice"));
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
    }

    [Fact]
    public async Task ArtifactTokenEndpoint_DeniesWhenExportCapabilityIsRevokedAndAuditsDenial()
    {
        RecordingAuditSink audit = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(auditSink: audit);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        RazorDbArtifactDescriptor artifact = await CreateCompletedArtifactAsync(host, "alice");
        host.SetCapabilities(RazorDbCapabilitySets.DataEditor);

        using HttpResponseMessage response = await IssueArtifactTokenAsync(client, host, artifact.Id, "alice");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal(RazorDbOperation.Export, denied.Operation);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal("alice", denied.ActorId);
        Assert.Equal("Main", denied.DatabaseId);
        Assert.Equal(artifact.Id, denied.Resource?.Subresource);
        Assert.Equal("capability-disabled", denied.ResultCode);
    }

    [Fact]
    public async Task ArtifactTokenEndpoint_DeniesResourceRevocationEvenWhenDeniedAuditFails()
    {
        RecordingAuthorizer authorizer = new(_ => RazorDbAuthorizationResult.Denied("test-resource-denied"));
        RecordingAuditSink audit = new(record =>
            record.Status == RazorDbAuditStatus.Denied
                ? new IOException("secret audit failure")
                : null);
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(authorizer, audit);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        RazorDbArtifactDescriptor artifact = await CreateCompletedArtifactAsync(host, "alice");

        using HttpResponseMessage response = await IssueArtifactTokenAsync(client, host, artifact.Id, "alice");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain("secret audit failure", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        RazorDbAuthorizationContext context = Assert.Single(authorizer.Contexts);
        Assert.Equal("alice", context.Actor.Id);
        Assert.Equal("Main", context.Registration.Id);
        Assert.Equal(RazorDbOperation.Export, context.Operation);
        Assert.Equal(RazorDbCapability.Export, context.RequiredCapability);
        Assert.Equal(artifact.Id, context.Resource?.Subresource);
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal("test-resource-denied", denied.ResultCode);
    }

    [Fact]
    public async Task ArtifactDownload_RechecksRevokedExportCapabilityAndAuditsDenial()
    {
        RecordingAuditSink audit = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(auditSink: audit);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        RazorDbArtifactDescriptor artifact = await CreateCompletedArtifactAsync(host, "alice");
        using HttpResponseMessage issue = await IssueArtifactTokenAsync(client, host, artifact.Id, "alice");
        Assert.Equal(HttpStatusCode.Redirect, issue.StatusCode);
        string url = Assert.IsType<Uri>(issue.Headers.Location).OriginalString;
        host.SetCapabilities(RazorDbCapabilitySets.DataEditor);

        using HttpResponseMessage response = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, url, "alice"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal(RazorDbOperation.Export, denied.Operation);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal("capability-disabled", denied.ResultCode);
        Assert.Equal(artifact.Id, denied.Resource?.Subresource);
    }

    [Fact]
    public async Task ArtifactDownload_RechecksEverySourceTableAfterTokenIssuance()
    {
        bool revoked = false;
        RecordingAuthorizer authorizer = new(context =>
            revoked && context.Resource is { Schema: "app", Object: "secret_orders" }
                ? RazorDbAuthorizationResult.Denied("source-table-revoked")
                : RazorDbAuthorizationResult.Allowed);
        RecordingAuditSink audit = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(authorizer, audit);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        RazorDbArtifactDescriptor artifact = await CreateCompletedArtifactAsync(
            host,
            "alice",
            [new RazorDbResource("app", "orders"), new RazorDbResource("app", "secret_orders")]);
        using HttpResponseMessage issue = await IssueArtifactTokenAsync(client, host, artifact.Id, "alice");
        Assert.Equal(HttpStatusCode.Redirect, issue.StatusCode);
        string url = Assert.IsType<Uri>(issue.Headers.Location).OriginalString;
        revoked = true;

        using HttpResponseMessage response = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, url, "alice"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(authorizer.Contexts, context => context.Resource is { Schema: "app", Object: "secret_orders" });
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal("source-table-revoked", denied.ResultCode);
        Assert.Equal("secret_orders", denied.Resource?.Object);
    }

    [Fact]
    public async Task ArtifactDownload_RechecksCurrentAllowedSchemasAfterTokenIssuance()
    {
        RecordingAuditSink audit = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(auditSink: audit);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        RazorDbArtifactDescriptor artifact = await CreateCompletedArtifactAsync(
            host,
            "alice",
            [new RazorDbResource("app", "orders")]);
        using HttpResponseMessage issue = await IssueArtifactTokenAsync(client, host, artifact.Id, "alice");
        Assert.Equal(HttpStatusCode.Redirect, issue.StatusCode);
        string url = Assert.IsType<Uri>(issue.Headers.Location).OriginalString;
        host.SetAllowedSchemas("archive");

        using HttpResponseMessage response = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, url, "alice"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal("schema-not-allowed", denied.ResultCode);
        Assert.Equal("app", denied.Resource?.Schema);
        Assert.Equal("orders", denied.Resource?.Object);
    }

    [Fact]
    public async Task ImportEndpoint_DeniesDisabledCapabilityBeforeWritingArtifactAndAuditsDenial()
    {
        RecordingAuditSink audit = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(auditSink: audit);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        (string cookie, string requestToken, string fieldName) = await GetAntiforgeryDetailsAsync(client, "alice");
        using MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(requestToken), fieldName);
        multipart.Add(new StringContent(ComponentScope(host, "alice")), RazorDbComponentScopeProtector.FormFieldName);
        multipart.Add(new StringContent("Main"), "databaseId");
        multipart.Add(new StringContent("app"), "schema");
        multipart.Add(new StringContent("users"), "table");
        ByteArrayContent file = new(Encoding.UTF8.GetBytes("id,name\r\n1,Alice\r\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        multipart.Add(file, "file", "data.csv");
        using HttpRequestMessage request = RazorDbTestHost.Request(
            HttpMethod.Post,
            "/_razor-db-manager/imports",
            "alice");
        request.Headers.Add("Cookie", cookie);
        request.Content = multipart;

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        string artifactRoot = Path.Combine(host.StoragePath, "artifacts");
        Assert.Empty(Directory.Exists(artifactRoot) ? Directory.GetFiles(artifactRoot) : []);
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal(RazorDbOperation.Import, denied.Operation);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal("alice", denied.ActorId);
        Assert.Equal("Main", denied.DatabaseId);
        Assert.Equal("app", denied.Resource?.Schema);
        Assert.Equal("users", denied.Resource?.Object);
        Assert.Equal("capability-disabled", denied.ResultCode);
    }

    [Theory]
    [InlineData("missing", "component-scope-missing")]
    [InlineData("expired", "component-scope-invalid")]
    [InlineData("read-only", "component-scope-read-only")]
    [InlineData("wrong-actor", "component-scope-actor")]
    [InlineData("wrong-database", "component-scope-database")]
    public async Task ImportEndpoint_RequiresWritableBoundComponentScopeBeforeWritingArtifact(
        string scopeKind,
        string expectedReason)
    {
        RecordingAuditSink audit = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            auditSink: audit,
            capabilities: RazorDbCapabilitySets.DataEditor | RazorDbCapability.Import);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        (string cookie, string requestToken, string fieldName) = await GetAntiforgeryDetailsAsync(client, "alice");
        string? componentScope = scopeKind switch
        {
            "missing" => null,
            "expired" => ComponentScope(host, "alice", lifetime: TimeSpan.FromMilliseconds(20)),
            "read-only" => ComponentScope(host, "alice", readOnly: true),
            "wrong-actor" => ComponentScope(host, "bob"),
            "wrong-database" => ComponentScope(host, "alice", databaseId: "Archive"),
            _ => throw new InvalidOperationException(scopeKind),
        };
        if (scopeKind == "expired") await Task.Delay(100);
        using MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(requestToken), fieldName);
        if (componentScope is not null)
            multipart.Add(new StringContent(componentScope), RazorDbComponentScopeProtector.FormFieldName);
        multipart.Add(new StringContent("Main"), "databaseId");
        multipart.Add(new StringContent("app"), "schema");
        multipart.Add(new StringContent("users"), "table");
        ByteArrayContent file = new("id,name\r\n1,Alice\r\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        multipart.Add(file, "file", "data.csv");
        using HttpRequestMessage request = RazorDbTestHost.Request(
            HttpMethod.Post,
            "/_razor-db-manager/imports",
            "alice");
        request.Headers.Add("Cookie", cookie);
        request.Content = multipart;

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        string artifactRoot = Path.Combine(host.StoragePath, "artifacts");
        Assert.Empty(Directory.Exists(artifactRoot) ? Directory.GetFiles(artifactRoot) : []);
        Assert.Empty(await host.Services.GetRequiredService<IRazorDbJobStore>().ListAsync(
            new RazorDbJobQuery(DatabaseId: "Main", ActorId: "alice")));
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal(RazorDbOperation.Import, denied.Operation);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal(expectedReason, denied.ResultCode);
    }

    [Theory]
    [InlineData(false, false, "component-scope-missing")]
    [InlineData(true, true, "component-scope-read-only")]
    public async Task ArtifactTokenEndpoint_RequiresWritableComponentScope(
        bool includeScope,
        bool readOnly,
        string expectedReason)
    {
        RecordingAuditSink audit = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(auditSink: audit);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        RazorDbArtifactDescriptor artifact = await CreateCompletedArtifactAsync(host, "alice");
        (string cookie, string requestToken) = await GetAntiforgeryAsync(client, "alice");
        string? componentScope = includeScope ? ComponentScope(host, "alice", readOnly) : null;

        using HttpResponseMessage response = await client.SendAsync(TokenRequest(
            $"/_razor-db-manager/artifacts/{artifact.Id}/token",
            "alice",
            cookie,
            requestToken,
            componentScope));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal(RazorDbOperation.Export, denied.Operation);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal(expectedReason, denied.ResultCode);
    }

    [Fact]
    public async Task ImportEndpoint_PersistsValidatedCsvOptionsWithQueuedJob()
    {
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            capabilities: RazorDbCapabilitySets.DataEditor
                | RazorDbCapability.Import
                | RazorDbCapability.Export
                | RazorDbCapability.DownloadBinary);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        (string cookie, string requestToken, string fieldName) = await GetAntiforgeryDetailsAsync(client, "alice");
        using MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(requestToken), fieldName);
        multipart.Add(new StringContent(ComponentScope(host, "alice")), RazorDbComponentScopeProtector.FormFieldName);
        multipart.Add(new StringContent("Main"), "databaseId");
        multipart.Add(new StringContent("app"), "schema");
        multipart.Add(new StringContent("users"), "table");
        multipart.Add(new StringContent("false"), "hasHeader");
        multipart.Add(new StringContent(";"), "delimiter");
        multipart.Add(new StringContent("NULL"), "nullToken");
        multipart.Add(new StringContent("true"), "continueOnError");
        multipart.Add(new StringContent("true"), "decodeProtectedValues");
        ByteArrayContent file = new(Encoding.UTF8.GetBytes("1;Alice\r\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        multipart.Add(file, "file", "data.csv");
        using HttpRequestMessage request = RazorDbTestHost.Request(
            HttpMethod.Post,
            "/_razor-db-manager/imports",
            "alice");
        request.Headers.Add("Cookie", cookie);
        request.Content = multipart;

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        IRazorDbJobStore jobs = host.Services.GetRequiredService<IRazorDbJobStore>();
        RazorDbJobRecord job = Assert.Single(await jobs.ListAsync(new RazorDbJobQuery(
            DatabaseId: "Main",
            ActorId: "alice")));
        Assert.Equal(RazorDbJobKind.CsvImport, job.Kind);
        Assert.Equal("False", job.Parameters["hasHeader"]);
        Assert.Equal(";", job.Parameters["delimiter"]);
        Assert.Equal("NULL", job.Parameters["nullToken"]);
        Assert.Equal("True", job.Parameters["continueOnError"]);
        Assert.Equal("True", job.Parameters["decodeProtectedValues"]);
        Assert.Equal(job.InputArtifactId, job.Parameters["inputArtifactId"]);
        Assert.Equal("9", job.Parameters["inputArtifactLength"]);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("1;Alice\r\n"))),
            job.Parameters["inputArtifactSha256"]);
        Assert.DoesNotContain("1;Alice", string.Join('|', job.Parameters.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlRestoreEndpoint_RequiresImportAndExecuteSqlSessionValidation()
    {
        RecordingSessionValidator sessions = new(context =>
            context.Operation == RazorDbOperation.ExecuteSql
                ? new RazorDbSessionValidationResult(false, ReasonCode: "sql-session-invalid")
                : new RazorDbSessionValidationResult(true, DateTimeOffset.UtcNow.AddMinutes(5)));
        RecordingAuditSink audit = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            auditSink: audit,
            capabilities: RazorDbCapabilitySets.DataEditor
                | RazorDbCapability.Import
                | RazorDbCapability.ExecuteSql,
            sessionValidator: sessions,
            addHighRiskPolicy: true);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        (string cookie, string requestToken, string fieldName) = await GetAntiforgeryDetailsAsync(client, "alice");
        using MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(requestToken), fieldName);
        multipart.Add(new StringContent(ComponentScope(host, "alice")), RazorDbComponentScopeProtector.FormFieldName);
        multipart.Add(new StringContent("Main"), "databaseId");
        ByteArrayContent file = new("SELECT 1;"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/sql");
        multipart.Add(file, "file", "restore.sql");
        using HttpRequestMessage request = RazorDbTestHost.Request(
            HttpMethod.Post,
            "/_razor-db-manager/imports",
            "alice");
        request.Headers.Add("Cookie", cookie);
        request.Content = multipart;

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            [RazorDbOperation.Import, RazorDbOperation.ExecuteSql],
            sessions.Contexts.Select(context => context.Operation));
        RazorDbAuditRecord denied = Assert.Single(audit.Records);
        Assert.Equal(RazorDbOperation.ExecuteSql, denied.Operation);
        Assert.Equal(RazorDbAuditStatus.Denied, denied.Status);
        Assert.Equal("sql-session-invalid", denied.ResultCode);
        string artifactRoot = Path.Combine(host.StoragePath, "artifacts");
        Assert.Empty(Directory.Exists(artifactRoot) ? Directory.GetFiles(artifactRoot) : []);
    }

    [Fact]
    public async Task ImportWorker_CommittedSuccessWinsCancellationRequestRace()
    {
        CommitWindowTransferProvider transfer = new();
        RecordingAuditSink audit = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            auditSink: audit,
            capabilities: RazorDbCapabilitySets.DataEditor | RazorDbCapability.Import,
            transferProvider: transfer);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        (string cookie, string requestToken, string fieldName) = await GetAntiforgeryDetailsAsync(client, "alice");
        using MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(requestToken), fieldName);
        multipart.Add(new StringContent(ComponentScope(host, "alice")), RazorDbComponentScopeProtector.FormFieldName);
        multipart.Add(new StringContent("Main"), "databaseId");
        multipart.Add(new StringContent("app"), "schema");
        multipart.Add(new StringContent("users"), "table");
        byte[] payload = "id,name\r\n1,Alice\r\n"u8.ToArray();
        ByteArrayContent file = new(payload);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        multipart.Add(file, "file", "data.csv");
        using HttpRequestMessage request = RazorDbTestHost.Request(
            HttpMethod.Post,
            "/_razor-db-manager/imports",
            "alice");
        request.Headers.Add("Cookie", cookie);
        request.Content = multipart;

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        IRazorDbJobStore jobs = host.Services.GetRequiredService<IRazorDbJobStore>();
        RazorDbJobRecord submitted = Assert.Single(await jobs.ListAsync(new RazorDbJobQuery(
            DatabaseId: "Main",
            ActorId: "alice")));
        Task<RazorDbJobRecord> earlyTerminal = WaitForTerminalAsync(jobs, submitted.Id);
        Task first = await Task.WhenAny(transfer.Committed, earlyTerminal);
        if (first == earlyTerminal)
        {
            RazorDbJobRecord failed = await earlyTerminal;
            string auditTrail = string.Join(",", audit.Records.Select(record =>
                $"{record.Operation}:{record.Status}:{record.ResultCode}:{record.Resource?.Schema}/{record.Resource?.Object}"));
            Assert.Fail($"The worker terminated before the provider commit window: {failed.Status}/{failed.ResultCode}; audit={auditTrail}.");
        }

        RazorDbJobRecord running = Assert.IsType<RazorDbJobRecord>(await jobs.GetAsync(submitted.Id));
        Assert.Equal(RazorDbJobStatus.Running, running.Status);
        RazorDbJobRecord requested = Assert.IsType<RazorDbJobRecord>(
            await jobs.RequestCancellationAsync(running.Id, "alice"));
        Assert.True(requested.CancellationRequested);
        Assert.Equal(RazorDbJobStatus.Running, requested.Status);

        transfer.Release();
        RazorDbJobRecord terminal = await WaitForTerminalAsync(jobs, running.Id);

        Assert.Equal(RazorDbJobStatus.Completed, terminal.Status);
        Assert.True(terminal.CancellationRequested);
        Assert.Null(terminal.ResultCode);
        Assert.Contains(audit.Records, record =>
            record.Operation == RazorDbOperation.Import
            && record.Status == RazorDbAuditStatus.Completed
            && record.PayloadHash == Convert.ToHexStringLower(SHA256.HashData(payload))
            && record.Metadata["inputArtifactId"] == running.InputArtifactId
            && record.Metadata["inputArtifactLength"] == payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ImportWorker_PostCommitArtifactCleanupFailureDoesNotRewriteCompletedOutcome()
    {
        CommitWindowTransferProvider transfer = new();
        ThrowingDeleteArtifactStore artifacts = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            capabilities: RazorDbCapabilitySets.DataEditor | RazorDbCapability.Import,
            transferProvider: transfer,
            artifactStore: artifacts);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        RazorDbJobRecord submitted = await UploadCsvImportAsync(client, host, "alice");

        await transfer.Committed;
        transfer.Release();
        RazorDbJobRecord terminal = await WaitForTerminalAsync(
            host.Services.GetRequiredService<IRazorDbJobStore>(),
            submitted.Id);

        Assert.Equal(RazorDbJobStatus.Completed, terminal.Status);
        Assert.Null(terminal.ResultCode);
        Assert.Equal(1, artifacts.DeleteCalls);
        Assert.NotNull(await artifacts.OpenReadAsync(terminal.InputArtifactId!));
    }

    [Fact]
    public async Task ImportWorker_RejectsNonSeekableArtifactBeforeInvokingProvider()
    {
        RecordingImportTransferProvider transfer = new();
        NonSeekableArtifactStore artifacts = new();
        await using RazorDbTestHost host = await RazorDbTestHost.CreateEndpointHostAsync(
            capabilities: RazorDbCapabilitySets.DataEditor | RazorDbCapability.Import,
            transferProvider: transfer,
            artifactStore: artifacts);
        HttpClient client = Assert.IsType<HttpClient>(host.Client);
        RazorDbJobRecord submitted = await UploadCsvImportAsync(client, host, "alice");

        RazorDbJobRecord terminal = await WaitForTerminalAsync(
            host.Services.GetRequiredService<IRazorDbJobStore>(),
            submitted.Id);

        Assert.Equal(RazorDbJobStatus.Failed, terminal.Status);
        Assert.Equal(RazorDbErrorCode.Forbidden.ToString(), terminal.ResultCode);
        Assert.Equal(0, transfer.ImportCalls);
    }

    private static async Task<RazorDbJobRecord> UploadCsvImportAsync(
        HttpClient client,
        RazorDbTestHost host,
        string actor)
    {
        (string cookie, string requestToken, string fieldName) = await GetAntiforgeryDetailsAsync(client, actor);
        using MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(requestToken), fieldName);
        multipart.Add(new StringContent(ComponentScope(host, actor)), RazorDbComponentScopeProtector.FormFieldName);
        multipart.Add(new StringContent("Main"), "databaseId");
        multipart.Add(new StringContent("app"), "schema");
        multipart.Add(new StringContent("users"), "table");
        ByteArrayContent file = new("id,name\r\n1,Alice\r\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        multipart.Add(file, "file", "data.csv");
        using HttpRequestMessage request = RazorDbTestHost.Request(
            HttpMethod.Post,
            "/_razor-db-manager/imports",
            actor);
        request.Headers.Add("Cookie", cookie);
        request.Content = multipart;
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(await host.Services.GetRequiredService<IRazorDbJobStore>().ListAsync(
            new RazorDbJobQuery(DatabaseId: "Main", ActorId: actor)));
    }

    private static async Task<RazorDbJobRecord> WaitForTerminalAsync(
        IRazorDbJobStore jobs,
        Guid jobId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            RazorDbJobRecord job = Assert.IsType<RazorDbJobRecord>(await jobs.GetAsync(jobId, timeout.Token));
            if (job.Status is RazorDbJobStatus.Completed or RazorDbJobStatus.Failed or RazorDbJobStatus.Cancelled)
                return job;
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class CommitWindowTransferProvider : IRazorDbTransferProvider
    {
        private readonly TaskCompletionSource _committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Committed => _committed.Task;

        public void Release() => _release.TrySetResult();

        public ValueTask<TransferResult> ExportAsync(
            ExportRequest request,
            Stream destination,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<TransferResult>(new NotSupportedException());

        public async ValueTask<TransferResult> ImportAsync(
            ImportRequest request,
            Stream source,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using MemoryStream captured = new();
            await source.CopyToAsync(captured, cancellationToken);
            _committed.TrySetResult();
            await _release.Task;
            progress?.Report(new TransferProgress(1, captured.Length, request.Table));
            return new TransferResult(1, captured.Length, []);
        }
    }

    private sealed class RecordingImportTransferProvider : IRazorDbTransferProvider
    {
        public int ImportCalls { get; private set; }

        public ValueTask<TransferResult> ExportAsync(
            ExportRequest request,
            Stream destination,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<TransferResult>(new NotSupportedException());

        public ValueTask<TransferResult> ImportAsync(
            ImportRequest request,
            Stream source,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ImportCalls++;
            return ValueTask.FromResult(new TransferResult(1, 0, []));
        }
    }

    private class InMemoryArtifactStore : IRazorDbArtifactStore
    {
        private readonly Dictionary<string, (RazorDbArtifactDescriptor Descriptor, byte[] Content)> _completed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (RazorDbArtifactDescriptor Descriptor, MemoryStream Content)> _pending = new(StringComparer.Ordinal);

        public virtual ValueTask<RazorDbArtifactWriteSession> CreateWriteAsync(
            RazorDbArtifactCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            RazorDbArtifactDescriptor descriptor = new(
                id, request.DatabaseId, request.ActorId, request.FileName, request.ContentType,
                null, DateTimeOffset.UtcNow, request.ExpiresAt, SourceResources: request.SourceResources);
            MemoryStream content = new();
            _pending[id] = (descriptor, content);
            return ValueTask.FromResult(new RazorDbArtifactWriteSession(descriptor, content));
        }

        public virtual ValueTask<RazorDbArtifactDescriptor> CompleteWriteAsync(
            string artifactId,
            long length,
            string sha256,
            CancellationToken cancellationToken = default)
        {
            (RazorDbArtifactDescriptor descriptor, MemoryStream content) = _pending[artifactId];
            RazorDbArtifactDescriptor completed = descriptor with { Length = length, Sha256 = sha256 };
            _completed[artifactId] = (completed, content.ToArray());
            _pending.Remove(artifactId);
            return ValueTask.FromResult(completed);
        }

        public virtual ValueTask<RazorDbArtifactReadSession?> OpenReadAsync(
            string artifactId,
            CancellationToken cancellationToken = default)
        {
            if (!_completed.TryGetValue(artifactId, out var stored))
                return ValueTask.FromResult<RazorDbArtifactReadSession?>(null);
            return ValueTask.FromResult<RazorDbArtifactReadSession?>(new(
                stored.Descriptor,
                CreateReadStream(stored.Content)));
        }

        protected virtual Stream CreateReadStream(byte[] content) => new MemoryStream(content, writable: false);

        public virtual ValueTask DeleteAsync(string artifactId, CancellationToken cancellationToken = default)
        {
            _completed.Remove(artifactId);
            _pending.Remove(artifactId);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);
    }

    private sealed class ThrowingDeleteArtifactStore : InMemoryArtifactStore
    {
        public int DeleteCalls { get; private set; }

        public override ValueTask DeleteAsync(string artifactId, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return ValueTask.FromException(new IOException("Simulated artifact cleanup failure."));
        }
    }

    private sealed class NonSeekableArtifactStore : InMemoryArtifactStore
    {
        protected override Stream CreateReadStream(byte[] content) => new NonSeekableReadStream(content);
    }

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static async Task<RazorDbArtifactDescriptor> CreateCompletedArtifactAsync(
        RazorDbTestHost host,
        string actor,
        IReadOnlyList<RazorDbResource>? sourceResources = null)
    {
        IRazorDbArtifactStore artifacts = host.Services.GetRequiredService<IRazorDbArtifactStore>();
        byte[] payload = "private-export"u8.ToArray();
        RazorDbArtifactWriteSession write = await artifacts.CreateWriteAsync(new RazorDbArtifactCreateRequest(
            "Main", actor, "export.csv", "text/csv", DateTimeOffset.UtcNow.AddMinutes(5), sourceResources));
        await write.Content.WriteAsync(payload);
        await write.Content.DisposeAsync();
        string digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        return await artifacts.CompleteWriteAsync(write.Descriptor.Id, payload.Length, digest);
    }

    private static async Task<HttpResponseMessage> IssueArtifactTokenAsync(
        HttpClient client,
        RazorDbTestHost host,
        string artifactId,
        string actor,
        bool readOnly = false)
    {
        (string cookie, string token) = await GetAntiforgeryAsync(client, actor);
        return await client.SendAsync(TokenRequest(
            $"/_razor-db-manager/artifacts/{artifactId}/token", actor, cookie, token,
            ComponentScope(host, actor, readOnly)));
    }

    private static async Task<(string Cookie, string RequestToken)> GetAntiforgeryAsync(HttpClient client, string actor)
    {
        (string cookie, string requestToken, _) = await GetAntiforgeryDetailsAsync(client, actor);
        return (cookie, requestToken);
    }

    private static async Task<(string Cookie, string RequestToken, string FieldName)> GetAntiforgeryDetailsAsync(
        HttpClient client,
        string actor)
    {
        using HttpResponseMessage response = await client.SendAsync(
            RazorDbTestHost.Request(HttpMethod.Get, "/_test/antiforgery", actor));
        response.EnsureSuccessStatusCode();
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string requestToken = body.RootElement.GetProperty("token").GetString()!;
        string fieldName = body.RootElement.GetProperty("field").GetString()!;
        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return (setCookie.Split(';', 2)[0], requestToken, fieldName);
    }

    private static HttpRequestMessage TokenRequest(
        string uri,
        string actor,
        string cookie,
        string requestToken,
        string? componentScope = null)
    {
        HttpRequestMessage request = RazorDbTestHost.Request(HttpMethod.Post, uri, actor);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("RequestVerificationToken", requestToken);
        request.Content = new FormUrlEncodedContent(componentScope is null
            ? []
            : [new KeyValuePair<string, string>(RazorDbComponentScopeProtector.FormFieldName, componentScope)]);
        return request;
    }

    private static string ComponentScope(
        RazorDbTestHost host,
        string actor,
        bool readOnly = false,
        string databaseId = "Main",
        TimeSpan? lifetime = null)
    {
        RazorDbComponentScopeProtector protector =
            host.Services.GetRequiredService<RazorDbComponentScopeProtector>();
        return lifetime is TimeSpan value
            ? protector.Protect(actor, databaseId, readOnly, value)
            : protector.Protect(actor, databaseId, readOnly);
    }

    private static async Task<HttpResponseMessage> IssueBinaryTokenAsync(HttpClient client, string actor)
    {
        (string cookie, string requestToken) = await GetAntiforgeryAsync(client, actor);
        string payload = JsonSerializer.Serialize(new
        {
            databaseId = "Main",
            schema = "app",
            table = "files",
            column = "payload",
            keyName = "PRIMARY",
            identity = new[]
            {
                new { column = "tenant_id", kind = DbValueKind.SignedInteger, text = "7", base64 = (string?)null },
                new { column = "id", kind = DbValueKind.UnsignedInteger, text = "42", base64 = (string?)null },
            },
        });
        HttpRequestMessage request = RazorDbTestHost.Request(
            HttpMethod.Post,
            "/_razor-db-manager/binary/token",
            actor);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("RequestVerificationToken", requestToken);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["payload"] = payload });
        return await client.SendAsync(request);
    }

    private sealed class StubBinaryDataProvider(byte[] content, IReadOnlyList<long>? reportedLengths = null) : IRazorDbDataProvider
    {
        public int OpenCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public ValueTask<IRazorDbBinaryReadSession> OpenBinaryAsync(
            BinaryCellRequest request,
            CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            long reportedLength = reportedLengths is not null && OpenCalls <= reportedLengths.Count
                ? reportedLengths[OpenCalls - 1]
                : content.LongLength;
            return ValueTask.FromResult<IRazorDbBinaryReadSession>(new Session(
                content,
                reportedLength,
                () => DisposeCalls++));
        }

        public ValueTask<RowPage> QueryRowsAsync(RowQueryRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<RowMutationResult> InsertRowAsync(InsertRowRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<RowMutationResult> UpdateRowAsync(UpdateRowRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<RowMutationResult> DeleteRowAsync(DeleteRowRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private sealed class Session(byte[] content, long length, Action disposed) : IRazorDbBinaryReadSession
        {
            public BinaryCellDescriptor Descriptor { get; } = new(
                length,
                "application/octet-stream",
                "files-payload.bin",
                DbDataKind.Binary);

            public async ValueTask CopyToAsync(Stream destination, CancellationToken cancellationToken = default) =>
                await destination.WriteAsync(content, cancellationToken);

            public ValueTask DisposeAsync()
            {
                disposed();
                return ValueTask.CompletedTask;
            }
        }
    }
}
