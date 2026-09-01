namespace Praxy.Persistence.Entities;

/// <summary>
/// A schema-per-database tenant boundary: <c>px_&lt;hex32&gt;</c>. Deleting the row and dropping
/// the Postgres schema happen in the same transaction as everything else in this engine.
/// </summary>
public class Database
{
    public required Guid Id { get; set; }
    public required string ProjectId { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }

    /// <summary>The physical Postgres schema name, e.g. <c>px_0195a1b2c3d4...</c>.</summary>
    public required string SchemaName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A user table. <see cref="PhysicalName"/> never changes once created — renaming
/// <see cref="Key"/> is metadata-only, per architecture.md §4.1.
/// </summary>
public class TableDef
{
    public required Guid Id { get; set; }
    public required Guid DatabaseId { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public required string PhysicalName { get; set; }

    /// <summary>Off by default: table-level permissions alone govern access until turned on.</summary>
    public bool RowSecurity { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A column. <see cref="Options"/> carries type-specific extras (enum <c>elements</c>) as jsonb
/// so adding a new column type never needs a catalog migration.
/// </summary>
public class ColumnDef
{
    public required Guid Id { get; set; }
    public required Guid TableId { get; set; }
    public required string Key { get; set; }

    /// <summary>string | integer | float | boolean | datetime | email | url | ip | enum | relationship</summary>
    public required string Type { get; set; }

    public required string PhysicalName { get; set; }
    public bool Required { get; set; }
    public bool IsArray { get; set; }

    /// <summary>Byte size for <c>string</c>; unused otherwise.</summary>
    public int? Size { get; set; }

    /// <summary>FK to <see cref="TableDef"/>, same database only. Set only when <see cref="Type"/> is <c>relationship</c>.</summary>
    public Guid? TargetTableId { get; set; }

    /// <summary>Wire key is <c>default</c> — Appwrite's <c>xdefault</c> dodge is JS-only, we don't need it.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Reserved for future format hints (e.g. a stricter email/url validator variant).</summary>
    public string? Format { get; set; }

    /// <summary>jsonb: enum <c>elements</c> today.</summary>
    public string Options { get; set; } = "{}";

    /// <summary>available | processing | failed</summary>
    public string Status { get; set; } = "available";

    public string? Error { get; set; }
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class IndexDef
{
    public required Guid Id { get; set; }
    public required Guid TableId { get; set; }
    public required string Key { get; set; }

    /// <summary>key | unique | fulltext</summary>
    public required string Type { get; set; }

    public required string[] Columns { get; set; }

    /// <summary>asc | desc per entry in <see cref="Columns"/>; empty means all ascending.</summary>
    public string[] Orders { get; set; } = [];

    public required string PhysicalName { get; set; }

    /// <summary>available | processing | failed</summary>
    public string Status { get; set; } = "processing";

    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Table-level permission grant. <c>write</c> is never stored here — it is expanded to
/// create+update+delete at write time per the adopted Appwrite grammar.
/// </summary>
public class TablePermission
{
    public required Guid TableId { get; set; }

    /// <summary>read | create | update | delete</summary>
    public required string Action { get; set; }

    public required string Role { get; set; }
}
