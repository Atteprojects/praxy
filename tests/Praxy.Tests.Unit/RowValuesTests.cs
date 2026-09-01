using System.Text.Json;
using Praxy.Core;
using Praxy.Persistence.Entities;
using Praxy.Tables;

namespace Praxy.Tests.Unit;

public class RowValuesTests
{
    private static ColumnDef Column(
        string type, bool required = false, bool isArray = false, int? size = null, string options = "{}") => new()
    {
        Id = Guid.NewGuid(), TableId = Guid.NewGuid(), Key = "col", Type = type, PhysicalName = "col_abc123",
        Required = required, IsArray = isArray, Size = size, Options = options,
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData(ColumnTypes.Integer, "42", typeof(long))]
    [InlineData(ColumnTypes.Float, "3.5", typeof(double))]
    [InlineData(ColumnTypes.Boolean, "true", typeof(bool))]
    [InlineData(ColumnTypes.String, "\"hello\"", typeof(string))]
    public void Scalar_values_convert_to_the_expected_clr_type(string type, string json, Type expected)
    {
        var column = Column(type, size: type == ColumnTypes.String ? 100 : null);
        var value = RowValues.ToWriteValue(column, "col", Parse(json));
        Assert.IsType(expected, value);
    }

    [Fact]
    public void Datetime_parses_iso8601_strings()
    {
        var column = Column(ColumnTypes.Datetime);
        var value = RowValues.ToWriteValue(column, "col", Parse("\"2026-01-01T00:00:00Z\""));
        Assert.IsType<DateTimeOffset>(value);
    }

    [Fact]
    public void String_over_declared_size_is_rejected()
    {
        var column = Column(ColumnTypes.String, size: 3);
        Assert.Throws<FormatException>(() => RowValues.ToWriteValue(column, "col", Parse("\"abcd\"")));
    }

    [Theory]
    [InlineData("\"not-an-email\"")]
    [InlineData("\"\"")]
    public void Invalid_email_is_rejected(string json)
    {
        var column = Column(ColumnTypes.Email);
        Assert.Throws<FormatException>(() => RowValues.ToWriteValue(column, "col", Parse(json)));
    }

    [Fact]
    public void Valid_email_is_accepted()
    {
        var column = Column(ColumnTypes.Email);
        var value = RowValues.ToWriteValue(column, "col", Parse("\"a@b.com\""));
        Assert.Equal("a@b.com", value);
    }

    [Theory]
    [InlineData("\"not a url\"")]
    [InlineData("\"just-plain-text-no-scheme\"")]
    public void Invalid_url_is_rejected(string json)
    {
        var column = Column(ColumnTypes.Url);
        Assert.Throws<FormatException>(() => RowValues.ToWriteValue(column, "col", Parse(json)));
    }

    [Fact]
    public void Valid_url_is_accepted()
    {
        var column = Column(ColumnTypes.Url);
        var value = RowValues.ToWriteValue(column, "col", Parse("\"https://example.com\""));
        Assert.Equal("https://example.com", value);
    }

    [Theory]
    [InlineData("\"not-an-ip\"")]
    [InlineData("\"999.999.999.999\"")]
    public void Invalid_ip_is_rejected(string json)
    {
        var column = Column(ColumnTypes.Ip);
        Assert.Throws<FormatException>(() => RowValues.ToWriteValue(column, "col", Parse(json)));
    }

    [Theory]
    [InlineData("\"192.168.1.1\"")]
    [InlineData("\"::1\"")]
    public void Valid_ip_is_accepted(string json)
    {
        var column = Column(ColumnTypes.Ip);
        RowValues.ToWriteValue(column, "col", Parse(json)); // does not throw
    }

    [Fact]
    public void Enum_value_must_be_a_declared_element()
    {
        var column = Column(ColumnTypes.Enum, options: """{"elements":["draft","published"]}""");
        Assert.Throws<FormatException>(() => RowValues.ToWriteValue(column, "col", Parse("\"archived\"")));
        var ok = RowValues.ToWriteValue(column, "col", Parse("\"draft\""));
        Assert.Equal("draft", ok);
    }

    [Fact]
    public void Array_column_requires_a_json_array()
    {
        var column = Column(ColumnTypes.Integer, isArray: true);
        Assert.Throws<FormatException>(() => RowValues.ToWriteValue(column, "col", Parse("1")));
    }

    [Fact]
    public void Array_column_converts_every_element()
    {
        var column = Column(ColumnTypes.Integer, isArray: true);
        var value = RowValues.ToWriteValue(column, "col", Parse("[1,2,3]"));
        Assert.Equal(new long[] { 1, 2, 3 }, Assert.IsType<long[]>(value));
    }

    [Fact]
    public void A_single_bad_element_fails_the_whole_array()
    {
        var column = Column(ColumnTypes.Email, isArray: true);
        Assert.Throws<FormatException>(() => RowValues.ToWriteValue(column, "col", Parse("[\"a@b.com\",\"not-an-email\"]")));
    }

    [Fact]
    public void Wrong_json_kind_is_rejected_with_a_field_specific_message()
    {
        var column = Column(ColumnTypes.Integer);
        var ex = Assert.Throws<FormatException>(() => RowValues.ToWriteValue(column, "views", Parse("\"not a number\"")));
        Assert.Contains("views", ex.Message);
    }

    /// <summary>
    /// Postgres' <c>text</c> type cannot represent U+0000 at all (found by Phase 9's query-compiler
    /// fuzz test — a filter value containing a null character reached Postgres and came back as an
    /// unhandled 500, <c>22021: invalid byte sequence for encoding "UTF8": 0x00</c>, not something a
    /// parameterized query protects against). Rejected at the same JSON-to-CLR string boundary every
    /// string-shaped column type shares, so both row writes and query filter values are covered.
    /// </summary>
    [Theory]
    [InlineData(ColumnTypes.String)]
    [InlineData(ColumnTypes.Enum)]
    public void A_string_containing_a_null_character_is_rejected(string type)
    {
        var column = type == ColumnTypes.Enum
            ? Column(type, options: """{"elements":["a b"]}""")
            : Column(type, size: 100);
        Assert.Throws<FormatException>(() => RowValues.ToWriteValue(column, "col", Parse("\"a\\u0000b\"")));
    }

    [Fact]
    public void Relationship_value_parses_a_wire_id_to_a_guid()
    {
        var column = Column(ColumnTypes.Relationship);
        var wireId = "0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4";
        var value = RowValues.ToWriteValue(column, "col", Parse($"\"{wireId}\""));
        Assert.Equal(wireId, Ids.Wire(Assert.IsType<Guid>(value)));
    }

    [Fact]
    public void Relationship_value_that_is_not_a_valid_id_is_rejected()
    {
        var column = Column(ColumnTypes.Relationship);
        Assert.Throws<FormatException>(() => RowValues.ToWriteValue(column, "col", Parse("\"not-an-id\"")));
    }

    [Fact]
    public void Relationship_array_converts_every_element_to_a_guid()
    {
        var column = Column(ColumnTypes.Relationship, isArray: true);
        var wireId = "0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4";
        var value = RowValues.ToWriteValue(column, "col", Parse($"[\"{wireId}\"]"));
        var array = Assert.IsType<Guid[]>(value);
        Assert.Equal(wireId, Ids.Wire(array[0]));
    }
}
