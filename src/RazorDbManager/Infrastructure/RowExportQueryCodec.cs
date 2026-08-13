using System.Text.Json;
using System.Text.Json.Serialization;
using RazorDbManager.Core;

namespace RazorDbManager;

internal static class RowExportQueryCodec
{
    private const int MaximumSerializedLength = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(RowExportQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string json = JsonSerializer.Serialize(QueryDto.From(query), JsonOptions);
        if (json.Length > MaximumSerializedLength)
            throw new RazorDbException(RazorDbErrorCode.LimitExceeded, "The export row selection is too large.");
        return json;
    }

    public static RowExportQuery Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumSerializedLength)
            throw Invalid();
        try
        {
            QueryDto dto = JsonSerializer.Deserialize<QueryDto>(json, JsonOptions) ?? throw Invalid();
            return dto.ToModel();
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            throw new RazorDbException(RazorDbErrorCode.Validation, "The queued export row selection is invalid.", exception);
        }
    }

    private static RazorDbException Invalid() =>
        new(RazorDbErrorCode.Validation, "The queued export row selection is invalid.");

    private sealed record QueryDto(FilterNodeDto? Filter, IReadOnlyList<DbSort>? Sorts, IReadOnlyList<string>? Columns)
    {
        public static QueryDto From(RowExportQuery query) => new(
            query.Filter is null ? null : FilterNodeDto.From(query.Filter),
            query.Sorts,
            query.Columns);

        public RowExportQuery ToModel() => new(Filter?.ToModel(), Sorts, Columns);
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(ComparisonDto), "comparison")]
    [JsonDerivedType(typeof(NullDto), "null")]
    [JsonDerivedType(typeof(InDto), "in")]
    [JsonDerivedType(typeof(LogicalDto), "logical")]
    private abstract record FilterNodeDto
    {
        public abstract FilterExpression ToModel();

        public static FilterNodeDto From(FilterExpression filter) => filter switch
        {
            ComparisonFilter value => new ComparisonDto(value.Column, value.Operator, ValueDto.From(value.Value)),
            NullFilter value => new NullDto(value.Column, value.IsNull),
            InFilter value => new InDto(value.Column, value.Values.Select(ValueDto.From).ToArray(), value.Negated),
            LogicalFilter value => new LogicalDto(value.Operator, value.Terms.Select(From).ToArray()),
            _ => throw new RazorDbException(RazorDbErrorCode.Validation, "The export filter type is not supported."),
        };
    }

    private sealed record ComparisonDto(string Column, DbComparisonOperator Operator, ValueDto Value) : FilterNodeDto
    {
        public override FilterExpression ToModel() =>
            new ComparisonFilter(Required(Column), Operator, Value.ToModel());
    }

    private sealed record NullDto(string Column, bool IsNull) : FilterNodeDto
    {
        public override FilterExpression ToModel() => new NullFilter(Required(Column), IsNull);
    }

    private sealed record InDto(string Column, IReadOnlyList<ValueDto> Values, bool Negated) : FilterNodeDto
    {
        public override FilterExpression ToModel() => new InFilter(
            Required(Column),
            Values?.Select(value => value.ToModel()).ToArray() ?? throw Invalid(),
            Negated);
    }

    private sealed record LogicalDto(DbLogicalOperator Operator, IReadOnlyList<FilterNodeDto> Terms) : FilterNodeDto
    {
        public override FilterExpression ToModel() => new LogicalFilter(
            Operator,
            Terms?.Select(term => term?.ToModel() ?? throw Invalid()).ToArray() ?? throw Invalid());
    }

    private sealed record ValueDto(DbValueKind Kind, string? Text, string? Base64)
    {
        public static ValueDto From(DbValue value) => value.Kind switch
        {
            DbValueKind.Null => new(value.Kind, null, null),
            DbValueKind.Binary or DbValueKind.Geometry => new(value.Kind, null, Convert.ToBase64String(value.Binary.Span)),
            _ => new(value.Kind, value.Text, null),
        };

        public DbValue ToModel() => Kind switch
        {
            DbValueKind.Null when Text is null && Base64 is null => DbValue.Null,
            DbValueKind.Binary or DbValueKind.Geometry when Text is null && Base64 is not null =>
                DbValue.FromBinary(Convert.FromBase64String(Base64), Kind),
            not (DbValueKind.Null or DbValueKind.Binary or DbValueKind.Geometry) when Text is not null && Base64 is null =>
                DbValue.FromText(Kind, Text),
            _ => throw Invalid(),
        };
    }

    private static string Required(string value) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw Invalid();
}
