namespace RazorDbManager.MySql.Models;

internal enum MySqlValueState
{
    Value,
    Null,
    Default,
    Omitted,
}

internal sealed record MySqlDynamicValue(MySqlValueState State, object? Value = null)
{
    public static MySqlDynamicValue From(object? value) => value is null ? Null : new(MySqlValueState.Value, value);

    public static MySqlDynamicValue Null { get; } = new(MySqlValueState.Null);

    public static MySqlDynamicValue Default { get; } = new(MySqlValueState.Default);

    public static MySqlDynamicValue Omitted { get; } = new(MySqlValueState.Omitted);
}

internal sealed record MySqlDataRow(IReadOnlyDictionary<string, MySqlDynamicValue> Values);

internal sealed record MySqlSort(string Column, bool Descending = false);

internal sealed record MySqlPageCursor(IReadOnlyList<MySqlDynamicValue> Values);

internal sealed record MySqlBrowseRequest(
    string Schema,
    string Table,
    int PageSize = 100,
    long Offset = 0,
    MySqlPageCursor? Cursor = null,
    IReadOnlyList<MySqlSort>? Sort = null,
    Filters.MySqlFilter? Filter = null);

internal sealed record MySqlBrowseResult(
    IReadOnlyList<MySqlColumnMetadata> Columns,
    IReadOnlyList<MySqlDataRow> Rows,
    MySqlRowIdentity? Identity,
    MySqlPageCursor? NextCursor,
    bool HasMore);

internal sealed record MySqlInsertRequest(
    string Schema,
    string Table,
    IReadOnlyDictionary<string, MySqlDynamicValue> Values);

internal sealed record MySqlUpdateRequest(
    string Schema,
    string Table,
    IReadOnlyDictionary<string, MySqlDynamicValue> Identity,
    IReadOnlyDictionary<string, MySqlDynamicValue> OriginalValues,
    IReadOnlyDictionary<string, MySqlDynamicValue> Changes);

internal sealed record MySqlDeleteRequest(
    string Schema,
    string Table,
    IReadOnlyDictionary<string, MySqlDynamicValue> Identity,
    IReadOnlyDictionary<string, MySqlDynamicValue> OriginalValues);

internal sealed record MySqlMutationResult(int AffectedRows, MySqlDataRow? Row = null);

internal sealed class MySqlConcurrencyException(string message) : InvalidOperationException(message);
