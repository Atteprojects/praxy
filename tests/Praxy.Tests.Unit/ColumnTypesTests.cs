using Praxy.Tables;

namespace Praxy.Tests.Unit;

public class ColumnTypesTests
{
    [Fact]
    public void Relationship_is_a_registered_type_backed_by_uuid()
    {
        Assert.True(ColumnTypes.IsValid(ColumnTypes.Relationship));
        Assert.Contains(ColumnTypes.Relationship, ColumnTypes.All);
        Assert.Equal("uuid", ColumnTypes.PostgresType(ColumnTypes.Relationship, size: null));
    }

    [Fact]
    public void Relationship_array_storage_type_is_uuid_array()
    {
        Assert.Equal("uuid[]", ColumnTypes.PostgresStorageType(ColumnTypes.Relationship, size: null, isArray: true));
    }
}
