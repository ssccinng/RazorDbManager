using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using RazorDbManager.Core;

namespace RazorDbManager;

/// <summary>Maps RazorDbManager support and routed component endpoints.</summary>
public static class RazorDbManagerEndpointExtensions
{
    /// <summary>Adds the RCL page assembly to a mapped Blazor component application.</summary>
    public static RazorComponentsEndpointConventionBuilder AddRazorDbManagerPages(this RazorComponentsEndpointConventionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddAdditionalAssemblies(typeof(RazorDbManagerRouting).Assembly);
    }

    /// <summary>Maps authorized support endpoints used for artifact downloads and health probes.</summary>
    public static IEndpointRouteBuilder MapRazorDbManagerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder group = endpoints.MapGroup("/_razor-db-manager")
            .RequireAuthorization()
            .WithGroupName("RazorDbManager");

        group.MapGet("/status", StatusAsync)
            .AddEndpointFilter<RazorDbNoStoreFilter>();

        group.MapPost("/imports", UploadImportAsync)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1_073_741_824))
            .AddEndpointFilter<RazorDbNoStoreFilter>();
        group.MapPost("/artifacts/{artifactId}/token", IssueDownloadTokenAsync)
            .RequireAuthorization()
            .AddEndpointFilter<RazorDbNoStoreFilter>();
        group.MapGet("/artifacts/{artifactId}", DownloadArtifactAsync)
            .RequireAuthorization()
            .AddEndpointFilter<RazorDbNoStoreFilter>();
        group.MapPost("/binary/token", IssueBinaryDownloadTokenAsync)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(32 * 1024))
            .RequireAuthorization()
            .AddEndpointFilter<RazorDbNoStoreFilter>();
        group.MapGet("/binary", DownloadBinaryAsync)
            .RequireAuthorization()
            .AddEndpointFilter<RazorDbNoStoreFilter>();

        return endpoints;
    }

    private static async Task<IResult> StatusAsync(
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        IRazorDbProviderRegistry registry,
        IRazorDbManagerAuthorizer authorizer,
        IRazorDbAuditSink auditSink,
        IOptions<RazorDbManagerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        string? actorId = ActorId(user);
        if (actorId is null) return Results.Unauthorized();
        if (!(await authorization.AuthorizeAsync(user, RazorDbManagerPolicies.Access)).Succeeded)
        {
            await AppendDeniedAuditAsync(
                auditSink,
                loggerFactory,
                new RazorDbActor(actorId, user.Identity?.Name),
                options.Value.DefaultDatabaseId ?? "unconfigured",
                RazorDbOperation.BrowseMetadata,
                null,
                "access-policy",
                cancellationToken);
            return Results.Forbid();
        }

        var actor = new RazorDbActor(actorId, user.Identity?.Name);
        var authorizedRegistrations = new List<DatabaseRegistration>();
        foreach (DatabaseRegistration registration in registry.Registrations)
        {
            RazorDbAuthorizationResult resourceAuthorization = registration.EnabledCapabilities
                .Includes(RazorDbCapability.BrowseMetadata)
                ? await authorizer.AuthorizeAsync(new RazorDbAuthorizationContext(
                    actor,
                    registration,
                    RazorDbOperation.BrowseMetadata,
                    RazorDbCapability.BrowseMetadata), cancellationToken)
                : RazorDbAuthorizationResult.Denied("capability-disabled");
            if (resourceAuthorization.IsAllowed)
            {
                authorizedRegistrations.Add(registration);
                continue;
            }

            await AppendDeniedAuditAsync(
                auditSink,
                loggerFactory,
                actor,
                registration.Id,
                RazorDbOperation.BrowseMetadata,
                null,
                resourceAuthorization.ReasonCode ?? "resource-denied",
                cancellationToken);
        }

        ILogger statusLogger = loggerFactory.CreateLogger("RazorDbManager.Status");
        StatusDatabaseResult[] databases = await Task.WhenAll(authorizedRegistrations.Select(registration =>
            ProbeDatabaseAsync(registry, registration, statusLogger, cancellationToken)));
        bool ready = databases.Length > 0
            && databases.All(database => database.Status == "ready");
        return Results.Ok(new
        {
            status = ready ? "ready" : "degraded",
            databases,
        });
    }

    private static async Task<StatusDatabaseResult> ProbeDatabaseAsync(
        IRazorDbProviderRegistry registry,
        DatabaseRegistration registration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            IRazorDbProvider provider = await registry
                .GetProviderAsync(registration.Id, cancellationToken)
                .ConfigureAwait(false);
            if (provider is not IRazorDbProviderHealthProbe healthProbe)
            {
                return DegradedStatus(
                    registration,
                    started,
                    "health-probe-not-supported");
            }

            RazorDbProviderHealthReport report = await healthProbe
                .CheckHealthAsync(cancellationToken)
                .ConfigureAwait(false);
            StatusDiagnosticResult[] diagnostics = report.Diagnostics
                .Take(32)
                .Select(diagnostic => new StatusDiagnosticResult(
                    SanitizeDiagnosticCode(diagnostic.Code),
                    diagnostic.Severity == RazorDbHealthDiagnosticSeverity.Information
                        ? "information"
                        : "warning"))
                .ToArray();
            return new StatusDatabaseResult(
                registration.Id,
                registration.ProviderName,
                report.Status == RazorDbHealthStatus.Ready ? "ready" : "degraded",
                ElapsedMilliseconds(started),
                SanitizeStatusValue(report.ProductName, 64),
                SanitizeStatusValue(report.ProductVersion, 128),
                SanitizeStatusValue(report.CurrentDatabase, 256),
                CapabilityNames(report.DiagnosticCapabilities),
                diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Database health probe failed for registration {DatabaseId} with error type {ErrorType}.",
                registration.Id,
                exception.GetType().Name);
            return DegradedStatus(registration, started, "health-probe-failed");
        }
    }

    private static StatusDatabaseResult DegradedStatus(
        DatabaseRegistration registration,
        long started,
        string diagnosticCode) =>
        new(
            registration.Id,
            registration.ProviderName,
            "degraded",
            ElapsedMilliseconds(started),
            null,
            null,
            null,
            [],
            [new StatusDiagnosticResult(diagnosticCode, "warning")]);

    private static long ElapsedMilliseconds(long started) =>
        Math.Max(0, (long)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds));

    private static string[] CapabilityNames(RazorDbCapability capabilities)
    {
        (RazorDbCapability Capability, string Name)[] known =
        [
            (RazorDbCapability.BrowseMetadata, nameof(RazorDbCapability.BrowseMetadata)),
            (RazorDbCapability.ReadRows, nameof(RazorDbCapability.ReadRows)),
            (RazorDbCapability.InsertRows, nameof(RazorDbCapability.InsertRows)),
            (RazorDbCapability.UpdateRows, nameof(RazorDbCapability.UpdateRows)),
            (RazorDbCapability.DeleteRows, nameof(RazorDbCapability.DeleteRows)),
            (RazorDbCapability.ModifySchema, nameof(RazorDbCapability.ModifySchema)),
            (RazorDbCapability.DestructiveSchema, nameof(RazorDbCapability.DestructiveSchema)),
            (RazorDbCapability.ExecuteSql, nameof(RazorDbCapability.ExecuteSql)),
            (RazorDbCapability.Import, nameof(RazorDbCapability.Import)),
            (RazorDbCapability.Export, nameof(RazorDbCapability.Export)),
            (RazorDbCapability.DownloadBinary, nameof(RazorDbCapability.DownloadBinary)),
        ];
        return known
            .Where(item => capabilities.Includes(item.Capability))
            .Select(item => item.Name)
            .ToArray();
    }

    private static string SanitizeDiagnosticCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "provider-diagnostic-invalid";
        string sanitized = new(value
            .Take(64)
            .Select(character => character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '.'
                    ? character
                    : '-')
            .ToArray());
        return sanitized.Length == 0 ? "provider-diagnostic-invalid" : sanitized;
    }

    private static string? SanitizeStatusValue(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return new string(value
            .Where(character => !char.IsControl(character))
            .Take(maximumLength)
            .ToArray());
    }

    private sealed record StatusDatabaseResult(
        string Id,
        string ProviderName,
        string Status,
        long LatencyMilliseconds,
        string? ProductName,
        string? ProductVersion,
        string? CurrentDatabase,
        IReadOnlyList<string> DiagnosticCapabilities,
        IReadOnlyList<StatusDiagnosticResult> Diagnostics);

    private sealed record StatusDiagnosticResult(string Code, string Severity);

    private static async Task<IResult> UploadImportAsync(
        HttpContext httpContext,
        ClaimsPrincipal user,
        IAntiforgery antiforgery,
        IOptions<AntiforgeryOptions> antiforgeryOptions,
        IRazorDbProviderRegistry registry,
        IRazorDbManagerAuthorizer authorizer,
        IAuthorizationService authorization,
        IRazorDbSessionValidator sessionValidator,
        IRazorDbAuditSink auditSink,
        IRazorDbArtifactStore artifacts,
        IRazorDbJobStore jobs,
        RazorDbTransferAdmissionCoordinator transferAdmission,
        RazorDbComponentScopeProtector componentScopes,
        IRazorDbOperationTokenStore tokens,
        IOptions<RazorDbManagerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        HttpRequest request = httpContext.Request;
        string? actorId = ActorId(user);
        if (actorId is null) return Results.Unauthorized();
        if (!TryGetMultipartBoundary(request.ContentType, out string boundary)) return Results.BadRequest("A valid multipart/form-data boundary is required.");

        long absoluteUploadLimit = Math.Min(options.Value.MaximumUploadBytes, options.Value.ResourceLimits.MaximumUploadBytes);
        if (absoluteUploadLimit <= 0) return Results.Problem("The upload limit is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        if (request.ContentLength is > 0 && request.ContentLength > absoluteUploadLimit + 65_536)
            return Results.BadRequest("The upload exceeds the configured limit.");

        MultipartReader reader = new(boundary, request.Body)
        {
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024,
            BodyLengthLimit = absoluteUploadLimit,
        };
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        RazorDbArtifactWriteSession? write = null;
        RazorDbArtifactDescriptor? completedArtifact = null;
        TransferFormat format = default;
        DbObjectName? table = null;
        DatabaseRegistration? registration = null;
        RazorDbResourceLimits? limits = null;
        RazorDbResource? resource = null;
        string? databaseId = null;
        bool antiforgeryValidated = false;
        bool fileSeen = false;
        IDisposable? admission = null;
        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                FormMultipartSection? formSection = section.AsFormDataSection();
                if (formSection is not null)
                {
                    if (fileSeen) throw new InvalidDataException("Form fields must precede the uploaded file.");
                    if (fields.Count >= 12) throw new InvalidDataException("Too many form fields were submitted.");
                    string value = await ReadSmallFormValueAsync(formSection, cancellationToken);
                    if (!fields.TryAdd(formSection.Name, value)) throw new InvalidDataException("Duplicate form fields are not allowed.");
                    if (string.Equals(formSection.Name, antiforgeryOptions.Value.FormFieldName, StringComparison.Ordinal))
                    {
                        string? headerName = antiforgeryOptions.Value.HeaderName;
                        if (string.IsNullOrWhiteSpace(headerName)) throw new InvalidOperationException("Antiforgery header validation is disabled.");
                        request.Headers[headerName] = value;
                        await antiforgery.ValidateRequestAsync(httpContext);
                        antiforgeryValidated = true;
                    }
                    continue;
                }

                FileMultipartSection? fileSection = section.AsFileSection();
                if (fileSection is null || !string.Equals(fileSection.Name, "file", StringComparison.Ordinal))
                    throw new InvalidDataException("The multipart request contains an unsupported section.");
                if (fileSeen) throw new InvalidDataException("Only one upload file is allowed.");
                if (!antiforgeryValidated) throw new InvalidDataException("The antiforgery field must precede the uploaded file.");
                fileSeen = true;

                databaseId = RequiredField(fields, "databaseId", 128);
                string schema = OptionalField(fields, "schema", 256);
                string tableName = OptionalField(fields, "table", 256);
                string fileName = Path.GetFileName(fileSection.FileName);
                if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255) throw new InvalidDataException("The upload file name is invalid.");
                string extension = Path.GetExtension(fileName);
                format = extension.Equals(".sql", StringComparison.OrdinalIgnoreCase) ? TransferFormat.Sql
                    : extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? TransferFormat.Csv
                    : throw new InvalidDataException("Only .csv and .sql files are supported.");
                table = !string.IsNullOrWhiteSpace(schema) && !string.IsNullOrWhiteSpace(tableName)
                    ? new DbObjectName(schema, tableName) : null;
                if (format == TransferFormat.Csv && table is null) throw new InvalidDataException("A target table is required for CSV import.");

                registration = registry.GetRequiredRegistration(databaseId);
                limits = registration.ResourceLimits ?? options.Value.ResourceLimits;
                long uploadLimit = Math.Min(limits.MaximumUploadBytes, options.Value.MaximumUploadBytes);
                if (uploadLimit <= 0) throw new InvalidOperationException("The database upload limit is not configured.");
                RazorDbActor actor = new(actorId, user.Identity?.Name);
                resource = table is null ? null : RazorDbResource.FromObject(table.Value);
                RazorDbComponentScopeValidation componentScope = componentScopes.Validate(
                    OptionalField(fields, RazorDbComponentScopeProtector.FormFieldName, 4_096),
                    actorId,
                    registration.Id);
                if (!componentScope.IsValid || componentScope.IsReadOnly)
                {
                    await AppendDeniedAuditAsync(
                        auditSink,
                        loggerFactory,
                        actor,
                        registration.Id,
                        RazorDbOperation.Import,
                        resource,
                        componentScope.IsValid ? "component-scope-read-only" : componentScope.ReasonCode,
                        cancellationToken);
                    return Results.Forbid();
                }
                bool accessAllowed = (await authorization.AuthorizeAsync(user, RazorDbManagerPolicies.Access)).Succeeded;
                bool schemaAllowed = resource?.Schema is null
                    || registration.AllowedSchemas.Count == 0
                    || registration.AllowedSchemas.Contains(resource.Schema, StringComparer.OrdinalIgnoreCase);
                RazorDbAuthorizationResult importAuthorization = accessAllowed
                    && registration.EnabledCapabilities.Includes(RazorDbCapability.Import)
                    && schemaAllowed
                        ? await authorizer.AuthorizeAsync(new RazorDbAuthorizationContext(
                            actor,
                            registration,
                            RazorDbOperation.Import,
                            RazorDbCapability.Import,
                            resource), cancellationToken)
                        : RazorDbAuthorizationResult.Denied(
                            !accessAllowed ? "access-policy"
                            : !schemaAllowed ? "schema-not-allowed"
                            : "capability-disabled");
                if (!importAuthorization.IsAllowed)
                {
                    await AppendDeniedAuditAsync(
                        auditSink,
                        loggerFactory,
                        actor,
                        registration.Id,
                        RazorDbOperation.Import,
                        resource,
                        importAuthorization.ReasonCode ?? "resource-denied",
                        cancellationToken);
                    return Results.Forbid();
                }
                if (format == TransferFormat.Sql)
                {
                    bool highRiskAllowed = (await authorization.AuthorizeAsync(user, RazorDbManagerPolicies.HighRisk)).Succeeded;
                    RazorDbAuthorizationResult sqlAuthorization = highRiskAllowed
                        && registration.EnabledCapabilities.Includes(RazorDbCapability.ExecuteSql)
                            ? await authorizer.AuthorizeAsync(new RazorDbAuthorizationContext(
                                actor,
                                registration,
                                RazorDbOperation.ExecuteSql,
                                RazorDbCapability.ExecuteSql,
                                resource), cancellationToken)
                            : RazorDbAuthorizationResult.Denied(
                                highRiskAllowed ? "capability-disabled" : "high-risk-policy");
                    if (!sqlAuthorization.IsAllowed)
                    {
                        await AppendDeniedAuditAsync(
                            auditSink,
                            loggerFactory,
                            actor,
                            registration.Id,
                            RazorDbOperation.ExecuteSql,
                            resource,
                            sqlAuthorization.ReasonCode ?? "resource-denied",
                            cancellationToken);
                        return Results.Forbid();
                    }

                    RazorDbSessionValidationResult importSession = await sessionValidator.ValidateAsync(
                        new RazorDbSessionValidationContext(
                            actor,
                            registration,
                            RazorDbOperation.Import,
                            resource),
                        cancellationToken);
                    if (!importSession.IsValid)
                    {
                        await AppendDeniedAuditAsync(
                            auditSink,
                            loggerFactory,
                            actor,
                            registration.Id,
                            RazorDbOperation.Import,
                            resource,
                            importSession.ReasonCode ?? "session-invalid",
                            cancellationToken);
                        return Results.Forbid();
                    }

                    RazorDbSessionValidationResult sqlSession = await sessionValidator.ValidateAsync(
                        new RazorDbSessionValidationContext(
                            actor,
                            registration,
                            RazorDbOperation.ExecuteSql,
                            resource),
                        cancellationToken);
                    if (!sqlSession.IsValid)
                    {
                        await AppendDeniedAuditAsync(
                            auditSink,
                            loggerFactory,
                            actor,
                            registration.Id,
                            RazorDbOperation.ExecuteSql,
                            resource,
                            sqlSession.ReasonCode ?? "session-invalid",
                            cancellationToken);
                        return Results.Forbid();
                    }
                }

                admission = await transferAdmission.EnterAsync(databaseId, actorId, cancellationToken);

                string contentType = format == TransferFormat.Csv ? "text/csv" : "application/sql";
                write = await artifacts.CreateWriteAsync(new RazorDbArtifactCreateRequest(databaseId, actorId, fileName, contentType, DateTimeOffset.UtcNow.Add(options.Value.ArtifactLifetime)), cancellationToken);
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Stream fileStream = fileSection.FileStream ?? throw new InvalidDataException("The upload file stream is unavailable.");
                byte[] buffer = new byte[64 * 1024];
                long length = 0;
                while (true)
                {
                    int read = await fileStream.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    length += read;
                    if (length > uploadLimit) throw new InvalidDataException("The upload exceeds the configured limit.");
                    hash.AppendData(buffer, 0, read);
                    await write.Content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (length == 0) throw new InvalidDataException("The upload file is empty.");
                await write.Content.DisposeAsync();
                string digest = Convert.ToHexStringLower(hash.GetHashAndReset());
                completedArtifact = await artifacts.CompleteWriteAsync(write.Descriptor.Id, length, digest, cancellationToken);
            }

            if (!antiforgeryValidated || !fileSeen || completedArtifact is null || databaseId is null || registration is null || limits is null)
                throw new InvalidDataException("A database and file are required.");
            Dictionary<string, string> parameters = new(StringComparer.Ordinal)
            {
                ["format"] = format.ToString(),
                ["table"] = System.Text.Json.JsonSerializer.Serialize(table),
                ["inputArtifactId"] = completedArtifact.Id,
                ["inputArtifactLength"] = (completedArtifact.Length
                    ?? throw new InvalidOperationException("The completed upload artifact has no length."))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["inputArtifactSha256"] = !string.IsNullOrWhiteSpace(completedArtifact.Sha256)
                    ? completedArtifact.Sha256
                    : throw new InvalidOperationException("The completed upload artifact has no digest."),
            };
            if (format == TransferFormat.Csv)
            {
                parameters["hasHeader"] = ParseBooleanField(fields, "hasHeader", defaultValue: true)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameters["delimiter"] = ParseDelimiterField(fields).ToString();
                parameters["nullToken"] = OptionalField(fields, "nullToken", 64) is { Length: > 0 } token
                    ? token : "\\N";
                parameters["continueOnError"] = ParseBooleanField(fields, "continueOnError")
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameters["decodeProtectedValues"] = ParseBooleanField(fields, "decodeProtectedValues")
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            string payloadHash = HashJobParameters(parameters);
            RazorDbOperationToken envelope = await tokens.IssueAsync(new RazorDbOperationTokenContext(actorId, databaseId, RazorDbOperation.Import, resource, string.Empty, payloadHash), options.Value.JobAuthorizationLifetime, cancellationToken);
            parameters["authorizationToken"] = envelope.Value;
            parameters["authorizationHash"] = payloadHash;
            RazorDbJobKind kind = format == TransferFormat.Csv ? RazorDbJobKind.CsvImport : RazorDbJobKind.SqlRestore;
            RazorDbJobRecord job = await jobs.CreateAsync(new RazorDbJobCreateRequest(databaseId, actorId, kind, completedArtifact.Id, parameters), cancellationToken);
            return Results.Content($"<!doctype html><meta charset=utf-8><style>body{{font:14px system-ui;padding:16px;color:#185b3d}}</style>Upload accepted. Job {job.Id} queued.", "text/html");
        }
        catch (InvalidDataException exception)
        {
            if (write is not null)
            {
                await write.Content.DisposeAsync();
                await artifacts.DeleteAsync(write.Descriptor.Id, CancellationToken.None);
            }
            return Results.BadRequest(exception.Message);
        }
        catch (AntiforgeryValidationException)
        {
            if (write is not null)
            {
                await write.Content.DisposeAsync();
                await artifacts.DeleteAsync(write.Descriptor.Id, CancellationToken.None);
            }
            return Results.BadRequest("The antiforgery token is invalid.");
        }
        catch (RazorDbException exception) when (exception.Code == RazorDbErrorCode.LimitExceeded)
        {
            if (write is not null)
            {
                await write.Content.DisposeAsync();
                await artifacts.DeleteAsync(write.Descriptor.Id, CancellationToken.None);
            }
            return Results.Content(
                "<!doctype html><meta charset=utf-8><p>Transfer job limit reached. Wait for an active job to finish.</p>",
                "text/html",
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch
        {
            if (write is not null)
            {
                await write.Content.DisposeAsync();
                await artifacts.DeleteAsync(write.Descriptor.Id, CancellationToken.None);
            }
            throw;
        }
        finally
        {
            admission?.Dispose();
        }
    }

    private static async Task<IResult> IssueDownloadTokenAsync(
        string artifactId,
        HttpContext httpContext,
        ClaimsPrincipal user,
        IAntiforgery antiforgery,
        IAuthorizationService authorization,
        IRazorDbProviderRegistry registry,
        IRazorDbManagerAuthorizer authorizer,
        IRazorDbAuditSink auditSink,
        IRazorDbArtifactStore artifacts,
        IRazorDbOperationTokenStore tokens,
        RazorDbComponentScopeProtector componentScopes,
        IOptions<RazorDbManagerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        string? actorId = ActorId(user);
        if (actorId is null) return Results.Unauthorized();
        try { await antiforgery.ValidateRequestAsync(httpContext); }
        catch (AntiforgeryValidationException) { return Results.BadRequest("The antiforgery token is invalid."); }
        IFormCollection form;
        try { form = await httpContext.Request.ReadFormAsync(cancellationToken); }
        catch (InvalidDataException) { return Results.BadRequest("The artifact token request is invalid."); }
        RazorDbArtifactReadSession? session = await artifacts.OpenReadAsync(artifactId, cancellationToken);
        if (session is null) return Results.NotFound();
        RazorDbArtifactDescriptor descriptor = session.Descriptor;
        await session.Content.DisposeAsync();
        RazorDbResource resource = new(Subresource: descriptor.Id);
        RazorDbActor actor = new(actorId, user.Identity?.Name);
        RazorDbComponentScopeValidation componentScope = componentScopes.Validate(
            form[RazorDbComponentScopeProtector.FormFieldName].ToString(),
            actorId,
            descriptor.DatabaseId);
        if (!componentScope.IsValid || componentScope.IsReadOnly)
        {
            await AppendDeniedAuditAsync(
                auditSink,
                loggerFactory,
                actor,
                descriptor.DatabaseId,
                RazorDbOperation.Export,
                resource,
                componentScope.IsValid ? "component-scope-read-only" : componentScope.ReasonCode,
                cancellationToken);
            return Results.Forbid();
        }
        (string? denialReason, RazorDbResource denialResource) = await ArtifactExportDenialAsync(
            descriptor, actor, user, authorization, registry, authorizer, cancellationToken);
        if (denialReason is not null)
        {
            await AppendDeniedAuditAsync(
                auditSink,
                loggerFactory,
                actor,
                descriptor.DatabaseId,
                RazorDbOperation.Export,
                denialResource,
                denialReason,
                cancellationToken);
            return Results.Forbid();
        }

        RazorDbOperationTokenContext context = DownloadContext(descriptor, actorId);
        RazorDbOperationToken token = await tokens.IssueAsync(context, options.Value.DownloadTokenLifetime, cancellationToken);
        string url = $"/_razor-db-manager/artifacts/{Uri.EscapeDataString(artifactId)}?token={Uri.EscapeDataString(token.Value)}";
        return Results.Redirect(url);
    }

    private static async Task<IResult> DownloadArtifactAsync(
        string artifactId,
        string? token,
        ClaimsPrincipal user,
        HttpContext httpContext,
        IAuthorizationService authorization,
        IRazorDbProviderRegistry registry,
        IRazorDbManagerAuthorizer authorizer,
        IRazorDbAuditSink auditSink,
        IRazorDbArtifactStore artifacts,
        IRazorDbOperationTokenStore tokens,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store, max-age=0";
        httpContext.Response.Headers.Pragma = "no-cache";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        string? actorId = ActorId(user);
        if (actorId is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(token)) return Results.BadRequest();
        RazorDbArtifactReadSession? session = await artifacts.OpenReadAsync(artifactId, cancellationToken);
        if (session is null) return Results.NotFound();
        RazorDbArtifactDescriptor descriptor = session.Descriptor;
        RazorDbResource resource = new(Subresource: descriptor.Id);
        RazorDbActor actor = new(actorId, user.Identity?.Name);
        (string? denialReason, RazorDbResource denialResource) = await ArtifactExportDenialAsync(
            descriptor, actor, user, authorization, registry, authorizer, cancellationToken);
        if (denialReason is not null)
        {
            await session.Content.DisposeAsync();
            await AppendDeniedAuditAsync(
                auditSink, loggerFactory, actor, descriptor.DatabaseId, RazorDbOperation.Export, denialResource, denialReason, cancellationToken);
            return Results.Forbid();
        }

        RazorDbOperationTokenResult consumed = await tokens.ConsumeAsync(token, DownloadContext(session.Descriptor, actorId), DateTimeOffset.UtcNow, cancellationToken);
        if (!consumed.IsValid) { await session.Content.DisposeAsync(); return Results.Forbid(); }
        return Results.Stream(session.Content, session.Descriptor.ContentType, session.Descriptor.FileName, enableRangeProcessing: false);
    }

    private static async ValueTask<(string? Reason, RazorDbResource Resource)> ArtifactExportDenialAsync(
        RazorDbArtifactDescriptor descriptor,
        RazorDbActor actor,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        IRazorDbProviderRegistry registry,
        IRazorDbManagerAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        RazorDbResource artifactResource = new(Subresource: descriptor.Id);
        if (!string.Equals(descriptor.ActorId, actor.Id, StringComparison.Ordinal)) return ("artifact-owner", artifactResource);

        DatabaseRegistration registration;
        try
        {
            registration = registry.GetRequiredRegistration(descriptor.DatabaseId);
        }
        catch (KeyNotFoundException)
        {
            return ("registration-not-found", artifactResource);
        }

        bool access = (await authorization.AuthorizeAsync(user, RazorDbManagerPolicies.Access)).Succeeded;
        if (!access) return ("access-policy", artifactResource);
        if (!registration.EnabledCapabilities.Includes(RazorDbCapability.Export)) return ("capability-disabled", artifactResource);

        IReadOnlyList<RazorDbResource> resources = descriptor.SourceResources is { Count: > 0 }
            ? descriptor.SourceResources
            : [artifactResource];
        IReadOnlyCollection<string> allowedSchemas = registration.AllowedSchemas;
        if (resources.Any(resource => resource.Schema is not null) && allowedSchemas.Count == 0)
        {
            try
            {
                IRazorDbProvider provider = await registry.GetProviderAsync(descriptor.DatabaseId, cancellationToken);
                DatabaseMetadata metadata = await provider.Metadata.GetDatabaseAsync(
                    new MetadataRequest(descriptor.DatabaseId), cancellationToken);
                allowedSchemas = metadata.Schemas.Select(schema => schema.Name).ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return ("schema-resolution-failed", artifactResource);
            }
        }
        foreach (RazorDbResource resource in resources)
        {
            if (resource.Schema is not null
                && !allowedSchemas.Contains(resource.Schema, StringComparer.OrdinalIgnoreCase))
                return ("schema-not-allowed", resource);
            RazorDbAuthorizationResult result = await authorizer.AuthorizeAsync(
                new RazorDbAuthorizationContext(
                    actor,
                    registration,
                    RazorDbOperation.Export,
                    RazorDbCapability.Export,
                    resource),
                cancellationToken);
            if (!result.IsAllowed) return (result.ReasonCode ?? "resource-denied", resource);
        }
        return (null, artifactResource);
    }

    private static RazorDbOperationTokenContext DownloadContext(RazorDbArtifactDescriptor descriptor, string actorId) =>
        new(
            actorId,
            descriptor.DatabaseId,
            RazorDbOperation.Export,
            new RazorDbResource(Subresource: descriptor.Id),
            ArtifactSourceHash(descriptor.SourceResources),
            descriptor.Sha256 ?? string.Empty);

    private static string ArtifactSourceHash(IReadOnlyList<RazorDbResource>? resources)
    {
        string canonical = string.Join('\n', (resources ?? [])
            .Select(resource => $"{resource.Schema}\u001f{resource.Object}\u001f{resource.Subresource}")
            .Order(StringComparer.Ordinal));
        return HashText(canonical);
    }

    private static async Task<IResult> IssueBinaryDownloadTokenAsync(
        HttpContext httpContext,
        ClaimsPrincipal user,
        IAntiforgery antiforgery,
        IAuthorizationService authorization,
        IRazorDbProviderRegistry registry,
        IRazorDbManagerAuthorizer authorizer,
        IRazorDbAuditSink auditSink,
        IRazorDbOperationTokenStore tokens,
        IDataProtectionProvider dataProtection,
        IOptions<RazorDbManagerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        string? actorId = ActorId(user);
        if (actorId is null) return Results.Unauthorized();
        try { await antiforgery.ValidateRequestAsync(httpContext); }
        catch (AntiforgeryValidationException) { return Results.BadRequest("The antiforgery token is invalid."); }

        IFormCollection form;
        try { form = await httpContext.Request.ReadFormAsync(cancellationToken); }
        catch (InvalidDataException) { return Results.BadRequest("The binary download request is invalid."); }
        string serialized = form["payload"].ToString();
        if (!TryParseBinaryRequest(serialized, out BinaryCellRequest? parsedRequest))
            return Results.BadRequest("The binary download request is invalid.");
        BinaryCellRequest request = parsedRequest!;

        RazorDbActor actor = new(actorId, user.Identity?.Name);
        RazorDbResource resource = new(request.Table.Schema, request.Table.Name, request.Column);
        (DatabaseRegistration? registration, string? denial) = await AuthorizeBinaryAsync(
            request, actor, user, authorization, registry, authorizer, cancellationToken);
        if (denial is not null || registration is null)
        {
            await AppendDeniedAuditAsync(
                auditSink, loggerFactory, actor, request.DatabaseId, RazorDbOperation.DownloadBinary,
                resource, denial ?? "registration-not-found", cancellationToken);
            return Results.Forbid();
        }

        try
        {
            IRazorDbProvider provider = await registry.GetProviderAsync(request.DatabaseId, cancellationToken);
            await using IRazorDbBinaryReadSession session = await provider.Data.OpenBinaryAsync(request, cancellationToken);
            EnsureBinaryLength(session.Descriptor, registration, options.Value);
        }
        catch (RazorDbException exception)
        {
            await AppendDeniedAuditAsync(
                auditSink, loggerFactory, actor, request.DatabaseId, RazorDbOperation.DownloadBinary,
                resource, BinaryResultCode(exception), cancellationToken);
            return BinaryFailure(exception);
        }
        catch (KeyNotFoundException)
        {
            await AppendDeniedAuditAsync(
                auditSink, loggerFactory, actor, request.DatabaseId, RazorDbOperation.DownloadBinary,
                resource, "not-found", cancellationToken);
            return Results.NotFound();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("RazorDbManager.BinaryDownload").LogError(
                "Binary download validation failed for database {DatabaseId} with {ErrorType}.",
                request.DatabaseId,
                exception.GetType().Name);
            return Results.Problem("The database could not validate the binary value.", statusCode: StatusCodes.Status502BadGateway);
        }

        string payloadHash = HashText(serialized);
        RazorDbOperationToken token = await tokens.IssueAsync(
            BinaryDownloadContext(actorId, request, payloadHash),
            options.Value.DownloadTokenLifetime,
            cancellationToken);
        ITimeLimitedDataProtector protector = BinaryPayloadProtector(dataProtection);
        string protectedPayload = protector.Protect(serialized, options.Value.DownloadTokenLifetime);
        string url = $"/_razor-db-manager/binary?payload={Uri.EscapeDataString(protectedPayload)}&token={Uri.EscapeDataString(token.Value)}";
        return Results.Redirect(url);
    }

    private static async Task<IResult> DownloadBinaryAsync(
        string? payload,
        string? token,
        HttpContext httpContext,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        IRazorDbProviderRegistry registry,
        IRazorDbManagerAuthorizer authorizer,
        IRazorDbAuditSink auditSink,
        IRazorDbOperationTokenStore tokens,
        IDataProtectionProvider dataProtection,
        IOptions<RazorDbManagerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store, max-age=0";
        httpContext.Response.Headers.Pragma = "no-cache";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        string? actorId = ActorId(user);
        if (actorId is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(token)
            || !TryUnprotectBinaryRequest(dataProtection, payload, out string serialized, out BinaryCellRequest? parsedRequest))
            return Results.BadRequest("The binary download request is invalid.");
        BinaryCellRequest request = parsedRequest!;

        RazorDbActor actor = new(actorId, user.Identity?.Name);
        RazorDbResource resource = new(request.Table.Schema, request.Table.Name, request.Column);
        (DatabaseRegistration? registration, string? denial) = await AuthorizeBinaryAsync(
            request, actor, user, authorization, registry, authorizer, cancellationToken);
        if (denial is not null || registration is null)
        {
            await AppendDeniedAuditAsync(
                auditSink, loggerFactory, actor, request.DatabaseId, RazorDbOperation.DownloadBinary,
                resource, denial ?? "registration-not-found", cancellationToken);
            return Results.Forbid();
        }

        string payloadHash = HashText(serialized);
        RazorDbOperationTokenResult consumed = await tokens.ConsumeAsync(
            token,
            BinaryDownloadContext(actorId, request, payloadHash),
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (!consumed.IsValid)
        {
            await AppendDeniedAuditAsync(
                auditSink, loggerFactory, actor, request.DatabaseId, RazorDbOperation.DownloadBinary,
                resource, consumed.ReasonCode ?? "token-invalid", cancellationToken);
            return Results.Forbid();
        }

        Guid correlationId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        await auditSink.AppendAsync(new RazorDbAuditRecord
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            Timestamp = startedAt,
            ActorId = actor.Id,
            DatabaseId = request.DatabaseId,
            Operation = RazorDbOperation.DownloadBinary,
            Status = RazorDbAuditStatus.Started,
            Resource = resource,
            PayloadHash = payloadHash,
        }, cancellationToken);

        IRazorDbBinaryReadSession session;
        try
        {
            IRazorDbProvider provider = await registry.GetProviderAsync(request.DatabaseId, cancellationToken);
            IRazorDbBinaryReadSession opened = await provider.Data.OpenBinaryAsync(request, cancellationToken);
            try
            {
                EnsureBinaryLength(opened.Descriptor, registration, options.Value);
                session = opened;
            }
            catch
            {
                await opened.DisposeAsync();
                throw;
            }
        }
        catch (Exception exception)
        {
            RazorDbAuditStatus status = exception is OperationCanceledException
                ? RazorDbAuditStatus.Cancelled
                : RazorDbAuditStatus.Failed;
            await AppendBinaryOutcomeAsync(
                auditSink, loggerFactory, correlationId, actor, request.DatabaseId, resource,
                payloadHash, status, BinaryResultCode(exception), startedAt, null, CancellationToken.None);
            return exception switch
            {
                RazorDbException razorDb => BinaryFailure(razorDb),
                KeyNotFoundException => Results.NotFound(),
                OperationCanceledException => Results.StatusCode(499),
                _ => Results.Problem("The database could not open the binary value.", statusCode: StatusCodes.Status502BadGateway),
            };
        }

        BinaryCellDescriptor descriptor = session.Descriptor;
        httpContext.Response.ContentLength = descriptor.Length;
        return Results.Stream(async (Stream destination) =>
        {
            await using (session)
            {
                try
                {
                    await session.CopyToAsync(destination, httpContext.RequestAborted);
                    await AppendBinaryOutcomeAsync(
                        auditSink, loggerFactory, correlationId, actor, request.DatabaseId, resource,
                        payloadHash, RazorDbAuditStatus.Completed, "completed", startedAt,
                        descriptor.Length, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    RazorDbAuditStatus status = exception is OperationCanceledException
                        ? RazorDbAuditStatus.Cancelled
                        : RazorDbAuditStatus.Failed;
                    await AppendBinaryOutcomeAsync(
                        auditSink, loggerFactory, correlationId, actor, request.DatabaseId, resource,
                        payloadHash, status, BinaryResultCode(exception), startedAt, null, CancellationToken.None);
                    httpContext.Abort();
                }
            }
        }, descriptor.ContentType, descriptor.FileName);
    }

    private static async ValueTask<(DatabaseRegistration? Registration, string? Denial)> AuthorizeBinaryAsync(
        BinaryCellRequest request,
        RazorDbActor actor,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        IRazorDbProviderRegistry registry,
        IRazorDbManagerAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        DatabaseRegistration registration;
        try { registration = registry.GetRequiredRegistration(request.DatabaseId); }
        catch (KeyNotFoundException) { return (null, "registration-not-found"); }
        if (!(await authorization.AuthorizeAsync(user, RazorDbManagerPolicies.Access)).Succeeded)
            return (registration, "access-policy");
        if (!registration.EnabledCapabilities.Includes(RazorDbCapability.DownloadBinary))
            return (registration, "capability-disabled");
        if (registration.AllowedSchemas.Count > 0
            && !registration.AllowedSchemas.Contains(request.Table.Schema, StringComparer.OrdinalIgnoreCase))
            return (registration, "schema-not-allowed");

        RazorDbAuthorizationResult result = await authorizer.AuthorizeAsync(new RazorDbAuthorizationContext(
            actor,
            registration,
            RazorDbOperation.DownloadBinary,
            RazorDbCapability.DownloadBinary,
            new RazorDbResource(request.Table.Schema, request.Table.Name, request.Column)), cancellationToken);
        return (registration, result.IsAllowed ? null : result.ReasonCode ?? "resource-denied");
    }

    private static bool TryUnprotectBinaryRequest(
        IDataProtectionProvider dataProtection,
        string? protectedPayload,
        out string serialized,
        out BinaryCellRequest? request)
    {
        serialized = string.Empty;
        request = null;
        if (string.IsNullOrWhiteSpace(protectedPayload) || protectedPayload.Length > 16_384) return false;
        try { serialized = BinaryPayloadProtector(dataProtection).Unprotect(protectedPayload); }
        catch (CryptographicException) { return false; }
        return TryParseBinaryRequest(serialized, out request);
    }

    private static ITimeLimitedDataProtector BinaryPayloadProtector(IDataProtectionProvider provider) =>
        provider.CreateProtector("RazorDbManager.BinaryDownloadPayload.v1").ToTimeLimitedDataProtector();

    private static bool TryParseBinaryRequest(string serialized, out BinaryCellRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(serialized) || Encoding.UTF8.GetByteCount(serialized) > 4_096) return false;
        try
        {
            BinaryDownloadPayload? payload = JsonSerializer.Deserialize<BinaryDownloadPayload>(serialized, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                MaxDepth = 8,
            });
            if (payload is null || payload.Identity is null || payload.Identity.Count is < 1 or > 64)
                return false;
            Dictionary<string, DbValue> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (BinaryIdentityValue? item in payload.Identity)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Column) || item.Column.Length > 256
                    || !Enum.IsDefined(item.Kind) || item.Kind == DbValueKind.Null)
                    return false;
                DbValue value;
                if (item.Kind is DbValueKind.Binary or DbValueKind.Geometry)
                {
                    if (item.Base64 is null || item.Text is not null) return false;
                    byte[] bytes = Convert.FromBase64String(item.Base64);
                    if (bytes.Length > 3_072) return false;
                    value = DbValue.FromBinary(bytes, item.Kind);
                }
                else
                {
                    if (item.Text is null || item.Base64 is not null || item.Text.Length > 3_072) return false;
                    value = DbValue.FromText(item.Kind, item.Text);
                }
                if (!values.TryAdd(item.Column, value)) return false;
            }

            request = new BinaryCellRequest(
                payload.DatabaseId,
                new DbObjectName(payload.Schema, payload.Table),
                payload.Column,
                new RowIdentity(payload.KeyName, values)).Validate();
            return request.Column.Length <= 256 && request.Identity.KeyName.Length <= 256
                && request.DatabaseId.Length <= 128
                && request.Table.Schema.Length <= 256 && request.Table.Name.Length <= 256;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static RazorDbOperationTokenContext BinaryDownloadContext(
        string actorId,
        BinaryCellRequest request,
        string payloadHash) =>
        new(actorId, request.DatabaseId, RazorDbOperation.DownloadBinary,
            new RazorDbResource(request.Table.Schema, request.Table.Name, request.Column),
            string.Empty, payloadHash);

    private static void EnsureBinaryLength(
        BinaryCellDescriptor descriptor,
        DatabaseRegistration registration,
        RazorDbManagerOptions options)
    {
        long maximum = (registration.ResourceLimits ?? options.ResourceLimits).MaximumBinaryDownloadBytes;
        if (descriptor.Length < 0 || descriptor.Length > maximum)
            throw new RazorDbException(RazorDbErrorCode.LimitExceeded, "The binary value exceeds the configured download limit.");
        if (descriptor.Kind is not (DbDataKind.Binary or DbDataKind.Geometry))
            throw new RazorDbException(RazorDbErrorCode.ProviderFailure, "The provider returned an invalid binary descriptor.");
    }

    private static async ValueTask AppendBinaryOutcomeAsync(
        IRazorDbAuditSink auditSink,
        ILoggerFactory loggerFactory,
        Guid correlationId,
        RazorDbActor actor,
        string databaseId,
        RazorDbResource resource,
        string payloadHash,
        RazorDbAuditStatus status,
        string resultCode,
        DateTimeOffset startedAt,
        long? length,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditSink.AppendAsync(new RazorDbAuditRecord
            {
                Id = Guid.NewGuid(),
                CorrelationId = correlationId,
                Timestamp = DateTimeOffset.UtcNow,
                ActorId = actor.Id,
                DatabaseId = databaseId,
                Operation = RazorDbOperation.DownloadBinary,
                Status = status,
                Resource = resource,
                PayloadHash = payloadHash,
                ResultCode = resultCode,
                Duration = DateTimeOffset.UtcNow - startedAt,
                Metadata = length is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["bytes"] = length.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("RazorDbManager.BinaryDownload").LogCritical(
                "RazorDbManager could not persist a terminal binary download audit for database {DatabaseId} and actor {ActorId} because {ErrorType} occurred.",
                databaseId,
                actor.Id,
                exception.GetType().Name);
        }
    }

    private static IResult BinaryFailure(RazorDbException exception) => exception.Code switch
    {
        RazorDbErrorCode.Validation => Results.BadRequest(exception.Message),
        RazorDbErrorCode.NotFound => Results.NotFound(),
        RazorDbErrorCode.Forbidden => Results.Forbid(),
        RazorDbErrorCode.Conflict => Results.Conflict(),
        RazorDbErrorCode.LimitExceeded => Results.Problem(exception.Message, statusCode: StatusCodes.Status413PayloadTooLarge),
        _ => Results.Problem("The database could not read the binary value.", statusCode: StatusCodes.Status502BadGateway),
    };

    private static string BinaryResultCode(Exception exception) => exception switch
    {
        RazorDbException value => value.Code.ToString().ToLowerInvariant(),
        KeyNotFoundException => "not-found",
        OperationCanceledException => "cancelled",
        _ => "provider-failure",
    };

    private static string HashText(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async ValueTask AppendDeniedAuditAsync(
        IRazorDbAuditSink auditSink,
        ILoggerFactory loggerFactory,
        RazorDbActor actor,
        string databaseId,
        RazorDbOperation operation,
        RazorDbResource? resource,
        string resultCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditSink.AppendAsync(new RazorDbAuditRecord
            {
                Id = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                ActorId = actor.Id,
                DatabaseId = databaseId,
                Operation = operation,
                Status = RazorDbAuditStatus.Denied,
                Resource = resource,
                ResultCode = resultCode,
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("RazorDbManager.ArtifactAuthorization").LogCritical(
                "RazorDbManager could not persist a denied {Operation} authorization audit for database {DatabaseId} and actor {ActorId} because {ErrorType} occurred.",
                operation,
                databaseId,
                actor.Id,
                exception.GetType().Name);
        }
    }

    private static string? ActorId(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
        ? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? user.Identity.Name
        : null;

    private static string HashJobParameters(IReadOnlyDictionary<string, string> parameters)
    {
        string canonical = string.Join("\n", parameters.Where(pair => !pair.Key.StartsWith("authorization", StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool TryGetMultipartBoundary(string? contentType, out string boundary)
    {
        boundary = string.Empty;
        if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed)
            || !string.Equals(parsed.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase)) return false;
        string? candidate = HeaderUtilities.RemoveQuotes(parsed.Boundary).Value;
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 128) return false;
        boundary = candidate;
        return true;
    }

    private static async Task<string> ReadSmallFormValueAsync(FormMultipartSection section, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(section.Section.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 512, leaveOpen: true);
        char[] buffer = new char[512];
        StringBuilder value = new();
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (value.Length + read > 4_096) throw new InvalidDataException("A form field exceeds the configured limit.");
            value.Append(buffer, 0, read);
        }
        return value.ToString();
    }

    private static string RequiredField(IReadOnlyDictionary<string, string> fields, string name, int maximumLength)
    {
        string value = OptionalField(fields, name, maximumLength);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"The {name} field is required.");
        return value;
    }

    private static string OptionalField(IReadOnlyDictionary<string, string> fields, string name, int maximumLength)
    {
        if (!fields.TryGetValue(name, out string? value)) return string.Empty;
        if (value.Length > maximumLength) throw new InvalidDataException($"The {name} field exceeds the configured limit.");
        return value;
    }

    private static bool ParseBooleanField(
        IReadOnlyDictionary<string, string> fields,
        string name,
        bool defaultValue = false)
    {
        string value = OptionalField(fields, name, 5);
        if (value.Length == 0) return defaultValue;
        return bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new InvalidDataException($"The {name} field is invalid.");
    }

    private static char ParseDelimiterField(IReadOnlyDictionary<string, string> fields)
    {
        string value = OptionalField(fields, "delimiter", 4);
        if (string.IsNullOrEmpty(value)) return ',';
        string decoded = value switch
        {
            "\\t" => "\t",
            "tab" => "\t",
            _ => value,
        };
        return decoded.Length == 1 && decoded[0] is not '\r' and not '\n' and not '"'
            ? decoded[0]
            : throw new InvalidDataException("The delimiter field must contain one safe character.");
    }

    private sealed record BinaryDownloadPayload(
        string DatabaseId,
        string Schema,
        string Table,
        string Column,
        string KeyName,
        IReadOnlyList<BinaryIdentityValue?> Identity);

    private sealed record BinaryIdentityValue(
        string Column,
        DbValueKind Kind,
        string? Text,
        string? Base64);
}
