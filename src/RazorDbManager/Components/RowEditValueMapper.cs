using RazorDbManager.Core;

namespace RazorDbManager.Components;

internal static class RowEditValueMapper
{
    public static EditValue Build(
        DbColumnMetadata column,
        bool isInsert,
        DbValue? originalValue,
        string text,
        bool isNull,
        bool isOmitted)
    {
        if (column.Type.Kind is DbDataKind.Binary or DbDataKind.Geometry)
        {
            return EditValue.Omitted;
        }
        if (isOmitted) return EditValue.Omitted;
        if (isNull)
        {
            return !isInsert && originalValue?.IsNull == true ? EditValue.Omitted : EditValue.Null;
        }

        DbValue value = ParseValue(column.Type.Kind, text);
        return !isInsert && originalValue is not null && originalValue.Equals(value)
            ? EditValue.Omitted
            : EditValue.FromValue(value);
    }

    private static DbValue ParseValue(DbDataKind kind, string value) => kind switch
    {
        DbDataKind.SignedInteger => DbValue.FromText(DbValueKind.SignedInteger, value),
        DbDataKind.UnsignedInteger => DbValue.FromText(DbValueKind.UnsignedInteger, value),
        DbDataKind.Decimal => DbValue.FromText(DbValueKind.Decimal, value),
        DbDataKind.FloatingPoint => DbValue.FromText(DbValueKind.FloatingPoint, value),
        DbDataKind.Boolean => DbValue.FromText(DbValueKind.Boolean, value),
        DbDataKind.Date => DbValue.FromText(DbValueKind.Date, value),
        DbDataKind.Time => DbValue.FromText(DbValueKind.Time, value),
        DbDataKind.DateTime => DbValue.FromText(DbValueKind.DateTime, value),
        DbDataKind.Timestamp => DbValue.FromText(DbValueKind.Timestamp, value),
        DbDataKind.Json => DbValue.FromText(DbValueKind.Json, value),
        DbDataKind.Enum => DbValue.FromText(DbValueKind.Enum, value),
        DbDataKind.Set => DbValue.FromText(DbValueKind.Set, value),
        DbDataKind.BitString => DbValue.FromText(DbValueKind.BitString, value),
        DbDataKind.Guid => DbValue.FromText(DbValueKind.Guid, value),
        DbDataKind.ProviderSpecific => DbValue.FromText(DbValueKind.ProviderSpecific, value),
        _ => DbValue.FromString(value),
    };
}
