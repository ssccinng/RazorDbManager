using RazorDbManager.Core;

namespace RazorDbManager.Sample;

public sealed class SampleDatabaseOptions
{
    internal const string SectionName = "RazorDbManagerSample";

    public string ReaderConnectionStringName { get; set; } = "MainDatabase";
    public string? WriterConnectionStringName { get; set; }
    public string? SchemaConnectionStringName { get; set; }
    public string? SqlConsoleConnectionStringName { get; set; }
    public string[] AllowedSchemas { get; set; } = [];
    public bool AllowInsecureDevelopmentConnection { get; set; }
    public bool EnableDataEditing { get; set; }
    public bool EnableImport { get; set; }
    public bool EnableExport { get; set; }
    public bool EnableSchemaChanges { get; set; }
    public bool EnableDestructiveSchemaChanges { get; set; }
    public bool EnableSqlConsole { get; set; }
    public bool EnableSqlRestore { get; set; }
    public bool AllowSharedHighRiskCredential { get; set; }

    public RazorDbCapability EnabledCapabilities()
    {
        RazorDbCapability capabilities = RazorDbCapabilitySets.ReadOnly | RazorDbCapability.DownloadBinary;
        if (EnableDataEditing)
        {
            capabilities |= RazorDbCapability.InsertRows
                | RazorDbCapability.UpdateRows
                | RazorDbCapability.DeleteRows;
        }
        if (EnableImport) capabilities |= RazorDbCapability.Import;
        if (EnableExport) capabilities |= RazorDbCapability.Export;
        if (EnableSchemaChanges) capabilities |= RazorDbCapability.ModifySchema;
        if (EnableDestructiveSchemaChanges)
        {
            capabilities |= RazorDbCapability.ModifySchema | RazorDbCapability.DestructiveSchema;
        }
        if (EnableSqlConsole) capabilities |= RazorDbCapability.ExecuteSql;
        return capabilities;
    }
}
