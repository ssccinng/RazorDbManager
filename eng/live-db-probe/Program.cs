using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using RazorDbManager;
using RazorDbManager.Core;
using RazorDbManager.MySql;

string configurationPath = args.Length == 1
    ? Path.GetFullPath(args[0])
    : throw new InvalidOperationException("Pass the host appsettings JSON path. User Secrets and environment variables are layered over it.");

IConfigurationRoot configuration = new ConfigurationBuilder()
    .SetBasePath(Path.GetDirectoryName(configurationPath)
        ?? throw new InvalidOperationException("The configuration path has no parent directory."))
    .AddJsonFile(Path.GetFileName(configurationPath), optional: false)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();
string connectionString = configuration.GetConnectionString("MainDatabase")
    ?? throw new InvalidOperationException(
        "MainDatabase is missing. Configure the sample's User Secrets or ConnectionStrings__MainDatabase.");

var builder = new MySqlConnectionStringBuilder(connectionString);
var report = new SortedDictionary<string, object?>
{
    ["configuredTlsMode"] = builder.SslMode.ToString(),
    ["persistSecurityInfo"] = builder.PersistSecurityInfo,
    ["allowLoadLocalInfile"] = builder.AllowLoadLocalInfile,
};

try
{
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();

    report["server"] = await RowAsync(connection, """
        SELECT VERSION() AS version,
               @@version_comment AS product,
               @@sql_mode AS sqlMode,
               @@lower_case_table_names AS lowerCaseTableNames,
               @@character_set_server AS characterSet,
               @@collation_server AS collation
        """);
    report["objectKinds"] = await RowsAsync(connection, """
        SELECT TABLE_TYPE AS kind, COUNT(*) AS count
        FROM information_schema.TABLES
        WHERE TABLE_SCHEMA = DATABASE()
        GROUP BY TABLE_TYPE
        ORDER BY TABLE_TYPE
        """);
    report["managerObjectTreeRead"] = await ManagerObjectTreeReadAsync(connection, builder.Database);
    report["engines"] = await RowsAsync(connection, """
        SELECT COALESCE(ENGINE, 'VIEW') AS engine, COUNT(*) AS count
        FROM information_schema.TABLES
        WHERE TABLE_SCHEMA = DATABASE()
        GROUP BY COALESCE(ENGINE, 'VIEW')
        ORDER BY engine
        """);
    report["columnTypes"] = await RowsAsync(connection, """
        SELECT DATA_TYPE AS dataType, COUNT(*) AS count
        FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE()
        GROUP BY DATA_TYPE
        ORDER BY DATA_TYPE
        """);
    report["schemaFeatures"] = await RowAsync(connection, """
        SELECT
          (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()) AS objects,
          (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()) AS columns,
          (SELECT COUNT(*) FROM information_schema.VIEWS WHERE TABLE_SCHEMA = DATABASE()) AS views,
          (SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = DATABASE()) AS routines,
          (SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE()) AS triggers,
          (SELECT COUNT(*) FROM information_schema.EVENTS WHERE EVENT_SCHEMA = DATABASE()) AS events,
          (SELECT COUNT(*) FROM information_schema.TABLES t
             WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_TYPE = 'BASE TABLE'
               AND NOT EXISTS (
                 SELECT 1 FROM information_schema.TABLE_CONSTRAINTS c
                 WHERE c.CONSTRAINT_SCHEMA = t.TABLE_SCHEMA
                   AND c.TABLE_NAME = t.TABLE_NAME
                   AND c.CONSTRAINT_TYPE = 'PRIMARY KEY')) AS tablesWithoutPrimaryKey
        """);

    IReadOnlyList<Dictionary<string, string?>> grants = await RowsAsync(connection, "SHOW GRANTS FOR CURRENT_USER");
    string grantText = string.Join('\n', grants.SelectMany(row => row.Values).Where(value => value is not null)).ToUpperInvariant();
    bool all = grantText.Contains("ALL PRIVILEGES", StringComparison.Ordinal);
    report["grants"] = new SortedDictionary<string, bool>
    {
        ["allPrivileges"] = all,
        ["select"] = all || HasGrant(grantText, "SELECT"),
        ["insert"] = all || HasGrant(grantText, "INSERT"),
        ["update"] = all || HasGrant(grantText, "UPDATE"),
        ["delete"] = all || HasGrant(grantText, "DELETE"),
        ["create"] = all || HasGrant(grantText, "CREATE"),
        ["alter"] = all || HasGrant(grantText, "ALTER"),
        ["drop"] = all || HasGrant(grantText, "DROP"),
        ["execute"] = all || HasGrant(grantText, "EXECUTE"),
        ["trigger"] = all || HasGrant(grantText, "TRIGGER"),
        ["event"] = all || HasGrant(grantText, "EVENT"),
        ["process"] = all || HasGrant(grantText, "PROCESS"),
        ["grantOption"] = grantText.Contains("GRANT OPTION", StringComparison.Ordinal),
    };
    report["provider"] = await ProbeProviderAsync(connectionString);
    report["connected"] = true;
}
catch (Exception exception)
{
    report["connected"] = false;
    report["errorType"] = exception.GetType().Name;
    report["errorCode"] = exception is MySqlException mysql ? mysql.ErrorCode.ToString() : null;
    report["error"] = Sanitize(exception.Message);
    Environment.ExitCode = 1;
}

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

static async Task<Dictionary<string, string?>> RowAsync(MySqlConnection connection, string sql) =>
    (await RowsAsync(connection, sql)).Single();

static async Task<IReadOnlyList<Dictionary<string, string?>>> RowsAsync(MySqlConnection connection, string sql)
{
    await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 15 };
    await using MySqlDataReader reader = await command.ExecuteReaderAsync();
    var rows = new List<Dictionary<string, string?>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            row[reader.GetName(ordinal)] = reader.IsDBNull(ordinal)
                ? null
                : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
        }
        rows.Add(row);
    }
    return rows;
}

static async Task<Dictionary<string, object?>> ProbeProviderAsync(string connectionString)
{
    WebApplicationBuilder web = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        EnvironmentName = Environments.Development,
        ApplicationName = typeof(Program).Assembly.GetName().Name,
    });
    web.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:LiveDatabase"] = connectionString,
    });
    web.Services.AddRazorComponents().AddInteractiveServerComponents();
    web.Services.AddAuthorizationBuilder()
        .AddPolicy(RazorDbManagerPolicies.Access, policy => policy.RequireAssertion(_ => true))
        .AddPolicy(RazorDbManagerPolicies.HighRisk, policy => policy.RequireAssertion(_ => true));
    web.Services
        .AddRazorDbManager(options => options.DefaultDatabaseId = "Live")
        .AddMySql("Live", options =>
        {
            options.ConnectionStringName = "LiveDatabase";
            options.EnabledCapabilities = RazorDbCapabilitySets.ReadOnly;
            options.AllowInsecureDevelopmentConnection = true;
        });
    await using WebApplication app = web.Build();
    try
    {
        IRazorDbProviderRegistry registry = app.Services.GetRequiredService<IRazorDbProviderRegistry>();
        IRazorDbProvider provider = await registry.GetProviderAsync("Live");
        DatabaseMetadata metadata = await provider.Metadata.GetDatabaseAsync(new MetadataRequest("Live"));
        RazorDbProviderHealthReport? health = provider is IRazorDbProviderHealthProbe healthProbe
            ? await healthProbe.CheckHealthAsync()
            : null;
        int metadataSucceeded = 0;
        int querySucceeded = 0;
        int safeIdentityObjects = 0;
        int metadataFailed = 0;
        int queryFailed = 0;
        foreach (DbObjectSummary item in metadata.Schemas.SelectMany(schema => schema.Objects))
        {
            try
            {
                DbTableMetadata table = await provider.Metadata.GetTableAsync(item.Name);
                metadataSucceeded++;
                if (table.RowIdentityKey is not null) safeIdentityObjects++;
                try
                {
                    _ = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                        "Live",
                        item.Name,
                        PageRequest.FromOffset(1, 0)));
                    querySucceeded++;
                }
                catch
                {
                    queryFailed++;
                }
            }
            catch
            {
                metadataFailed++;
            }
        }
        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["product"] = metadata.ProductName,
            ["version"] = metadata.ProductVersion,
            ["schemas"] = metadata.Schemas.Count,
            ["objects"] = metadata.Schemas.Sum(schema => schema.Objects.Count),
            ["metadataSucceeded"] = metadataSucceeded,
            ["metadataFailed"] = metadataFailed,
            ["querySucceeded"] = querySucceeded,
            ["queryFailed"] = queryFailed,
            ["safeIdentityObjects"] = safeIdentityObjects,
            ["healthStatus"] = health?.Status.ToString(),
            ["healthDiagnostics"] = health?.Diagnostics.Select(item => item.Code).ToArray() ?? [],
            ["diagnosticCapabilities"] = health?.DiagnosticCapabilities.ToString(),
        };
    }
    catch (Exception exception)
    {
        return new Dictionary<string, object?>
        {
            ["success"] = false,
            ["errorType"] = exception.GetType().Name,
            ["error"] = Sanitize(exception.Message),
        };
    }
}

static async Task<Dictionary<string, object?>> ManagerObjectTreeReadAsync(
    MySqlConnection connection,
    string schema)
{
    await using var command = new MySqlCommand("""
        SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE, TABLE_ROWS, TABLE_COMMENT
        FROM information_schema.TABLES
        WHERE TABLE_SCHEMA IN (@schema0)
        ORDER BY TABLE_SCHEMA, TABLE_NAME
        """, connection);
    command.Parameters.AddWithValue("@schema0", schema);
    await using MySqlDataReader reader = await command.ExecuteReaderAsync();
    int objects = 0;
    int nullRowCounts = 0;
    while (await reader.ReadAsync())
    {
        _ = reader.GetString(0);
        _ = reader.GetString(1);
        _ = reader.GetString(2);
        if (reader.IsDBNull(3)) nullRowCounts++; else _ = reader.GetInt64(3);
        if (!reader.IsDBNull(4)) _ = reader.GetString(4);
        objects++;
    }
    return new Dictionary<string, object?>
    {
        ["objects"] = objects,
        ["nullEstimatedRowCounts"] = nullRowCounts,
    };
}

static bool HasGrant(string grants, string privilege) =>
    grants.Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Contains(privilege, StringComparer.Ordinal);

static string Sanitize(string message)
{
    int newline = message.IndexOfAny(['\r', '\n']);
    return newline >= 0 ? message[..newline] : message;
}
