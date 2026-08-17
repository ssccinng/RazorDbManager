# RazorDbManager

[English](README.md) | [简体中文](README.zh-CN.md)

RazorDbManager is a self-contained database-management surface for .NET 10
Blazor Web Apps. The first provider supports MySQL and MariaDB with an
Interactive Server UI, bounded data access, row editing, structured schema
changes, a separately gated SQL console, streaming transfers, and audit hooks.

The data workspace includes quick text search, per-column structured `WHERE`
filters, sortable paging, a prominent insert workflow,
selected-row CSV export, and atomic optimistic batch deletion for bounded
InnoDB selections. The SQL console can generate quoted `SELECT`, `WHERE`,
`INSERT`, `UPDATE`, and `DELETE` templates for the current table; generated
`UPDATE` and `DELETE` templates start with `WHERE 1 = 0` and are never executed
automatically.

Every data-page query exposes the parameterized command text that the provider
actually executed, together with bounded parameter value previews and per-command
timing. The component also keeps the latest 50 queries in its in-memory session
activity. Query commands and parameter values are not persisted to the audit store;
they disappear when the component circuit ends or switches databases.

The browser never receives a database connection string. Every operation
request is checked against the registered capability ceiling, the host
application's authorization policies, the allowed-schema list, and the
database account's grants. Queued-job reauthorization is described below.
The SQL workspace uses a bundled CodeMirror 6 editor; the host does not need a
JavaScript package manager or additional script tag.

## Install from NuGet

Install the public
[`RazorDbManager.MySql`](https://www.nuget.org/packages/RazorDbManager.MySql/1.0.0)
package. It brings the UI and provider-neutral contracts as transitive
dependencies, so this is the only package a MySQL or MariaDB host needs to
reference.

With the .NET CLI:

```shell
dotnet add package RazorDbManager.MySql --version 1.0.0
```

With a project file:

```xml
<ItemGroup>
  <PackageReference Include="RazorDbManager.MySql" Version="1.0.0" />
</ItemGroup>
```

In Visual Studio, open **Manage NuGet Packages**, search for
`RazorDbManager.MySql`, select version `1.0.0`, and install it into the Blazor
host project.

Register Interactive Server components, authentication policies, and a named
database in `Program.cs`:

```csharp
using RazorDbManager;
using RazorDbManager.Core;
using RazorDbManager.MySql;

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(RazorDbManagerPolicies.Access,
        policy => policy.RequireRole("DatabaseAdmin"))
    .AddPolicy(RazorDbManagerPolicies.HighRisk,
        policy => policy.RequireRole("DatabaseAdmin"));

builder.Services
    .AddRazorDbManager(options => options.DefaultDatabaseId = "Main")
    .AddMySql("Main", options =>
    {
        options.ConnectionStringName = "MainDatabase";
        options.EnabledCapabilities = RazorDbCapabilitySets.DataEditor;
    });
```

Map the protected support endpoints and include the RCL's routable page:

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorDbManagerEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddRazorDbManagerPages();
```

The middleware calls must run before the endpoints are mapped.

Keep the generated host stylesheet reference from the Blazor template (for
example `RazorDbManager.Sample.styles.css`). ASP.NET Core folds the RCL's CSS
isolation bundle into that stylesheet, so no separate UI-framework asset is
required.

In the host's `Routes.razor`, supply the RCL assembly to the router:

```razor
<Router AppAssembly="typeof(Program).Assembly"
        AdditionalAssemblies="RazorDbManager.RazorDbManagerRouting.Assemblies">
    ...
</Router>
```

The built-in page is then available at `/db-manager`. To place the workspace
inside an existing authorized page instead, use:

```razor
@using RazorDbManager.Components
@rendermode InteractiveServer

<DatabaseManager DatabaseId="Main" />
```

Only a logical database id crosses the component boundary. `ReadOnly="true"`
can narrow an instance, while `Class` and unmatched attributes customize its
root element. Component parameters can never grant a capability. Import and
export-download forms carry a short-lived, Data Protection-backed component
scope bound to the current actor, database id, and `ReadOnly` value; the HTTP
endpoints reject missing, mismatched, expired, or read-only scopes. An active
Interactive Server circuit refreshes the scope before it expires, so a transfer
page left open remains usable without extending the validity of any individual
token.

`ReadOnly` applies to that component instance. A host that only embeds a
read-only manager should omit `AddRazorDbManagerPages()` and the RCL router
assembly when it doesn't need `/db-manager`. If the built-in route is exposed,
set `options.BuiltInPageReadOnly = true` to make that entry point read-only too.

## Connection security

For local development, keep the real connection string out of
`appsettings.Development.json`. The sample project has a stable User Secrets id,
so configure it with:

```shell
dotnet user-secrets set "ConnectionStrings:MainDatabase" "Server=localhost;Database=app;User ID=razordb_reader;Password=...;SslMode=VerifyFull;PersistSecurityInfo=false;AllowLoadLocalInfile=false" --project samples/RazorDbManager.Sample
```

The sample enables only metadata browsing, row reads, and protected binary
downloads by default. Its `RazorDbManagerSample` section exposes explicit
development switches for row editing, import/export, structured DDL, and the
SQL console. Enabling one of those switches never creates database privileges;
configure the corresponding least-privilege credential slot as well.
The sample deliberately refuses to start outside the `Development` environment
and accepts only loopback requests because its fixed demonstration identity is
not a login system. Production applications must install the packages into a
host with real authentication.

Production connection strings must use TLS host verification and must keep
credential persistence and local infile loading disabled:

```json
{
  "ConnectionStrings": {
    "MainDatabase": "Server=db.example.com;Database=app;User ID=razordb_reader;Password=...;SslMode=VerifyFull;PersistSecurityInfo=false;AllowLoadLocalInfile=false"
  }
}
```

`DataEditor` contains metadata browse, row read, insert, update, and delete.
Add `RazorDbCapability.DownloadBinary` explicitly to enable protected, streamed BLOB
and geometry downloads. Downloads require a current primary or safe non-null unique
row identity, use actor-bound single-use links, and default to a 25 MiB maximum value.
Schema changes, destructive schema changes, imports, exports, and arbitrary SQL
are never included implicitly. Give those operations separate least-privilege
credentials using `WriterConnectionStringName`,
`SchemaConnectionStringName`, and `SqlConsoleConnectionStringName`. Sharing the
read/write credential for DDL or SQL requires the explicit
`AllowSharedHighRiskCredential` option.

RazorDbManager does not provide login UI. The host owns authentication and must
configure `RazorDbManager.Access`. It must also configure
`RazorDbManager.HighRisk` when arbitrary SQL or schema capabilities are enabled.
Enabling any SQL or schema capability requires the host to replace the default
`IRazorDbSessionValidator`; startup fails closed until it does. Use that validator
with a revalidating server authentication-state provider to enforce current
account, recent-authentication, or MFA state. Queued transfers are rechecked
immediately before execution.
Enabling import or export requires the host to register an identity-aware
`IRazorDbBackgroundAuthorizer`; startup fails while the default implementation
is still registered. The replacement must resolve the actor's current account
state and current Access/HighRisk policy equivalents rather than trusting the
claims captured when the task was queued.

## Packages

- `RazorDbManager.Core` contains provider-neutral models and contracts.
- `RazorDbManager` contains the RCL, authorization layer, protected endpoints,
  local single-instance stores, CSS, icons, and English/Chinese resources.
- `RazorDbManager.MySql` contains MySQL/MariaDB metadata, query, mutation, DDL,
  SQL, and transfer implementations.

The default local stores live below `App_Data/RazorDbManager`, outside
`wwwroot`. SQLite persists audit records, transfer jobs, one-time token nonces,
and per-user preferences. Terminal job summaries are retained for 30 days by
default (`TerminalJobRetention`); audit records remain append-only. Multi-instance deployments must replace the job,
artifact, audit, preference, and operation-token stores and share ASP.NET Core
Data Protection keys.

`DatabaseMetadata.EffectiveCapabilities` is the configured application ceiling
after credential-slot availability is applied. The authorized
`/_razor-db-manager/status` endpoint performs a live reader connection check and
conservatively parses `SHOW GRANTS` for diagnostics, including unresolved roles,
missing read grants, and an over-privileged reader credential. Those discovered
capabilities never authorize or disable an operation: MySQL/MariaDB remains the
final grants boundary and denied commands are returned as sanitized provider
errors. Use dedicated accounts for read, write, schema, and SQL-console purposes
instead of treating diagnostic capability discovery as a privilege boundary.
The status response includes only registrations for which the current actor is
resource-authorized to browse metadata.

## Build and test

The repository pins the stable .NET SDK in `global.json`:

```shell
dotnet restore RazorDbManager.slnx --configfile NuGet.Config
dotnet build RazorDbManager.slnx -c Release --no-restore
dotnet test RazorDbManager.slnx -c Release --no-build
```

Set `RAZORDB_TEST_CONNECTION` to run provider integration tests against MySQL
8.4 or MariaDB 11.8. Never use production data or credentials for tests.
Pull requests run those two current baselines; the scheduled workflow also runs
MySQL 9.7 and MariaDB 10.11/11.4 compatibility targets.

See [SECURITY.md](SECURITY.md) before exposing the manager in any environment.
Bundled browser dependencies are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
