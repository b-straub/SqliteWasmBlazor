using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SqliteWasmBlazor.TestApp.TestInfrastructure.Tests.Migrations.Upgrade;

/// <summary>
/// Row type for the migration-upgrade probe. Deliberately small: these tests
/// measure what a migration costs over many rows, so the rows should cost as
/// little as possible themselves.
/// </summary>
public sealed class ProbeRow
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Payload { get; set; } = string.Empty;
    public DateTime StampedAt { get; set; }
}

/// <summary>
/// A context that exists solely so there is a <i>second</i> migration to apply.
///
/// <para>
/// <c>TodoDbContext</c> ships exactly one migration on purpose —
/// a schema change here means regenerating <c>InitialCreate</c> and resetting,
/// not migrating forward. That leaves the upgrade path consumers actually
/// depend on with nothing to exercise it, because a migration applied to the
/// empty database a reset just produced proves nothing about a migration
/// applied to a populated one.
/// </para>
///
/// <para>
/// So this context has two: <see cref="V1Create"/> makes the table, and
/// <see cref="V2AddIndex"/> adds an index over whatever rows are in it by
/// then. Stopping at V1 is <c>IMigrator.MigrateAsync(V1Id)</c>; the tests then
/// seed and let <c>MigrateAsync()</c> apply V2 for real.
/// </para>
/// </summary>
public sealed class MigrationProbeContext(DbContextOptions<MigrationProbeContext> options)
    : DbContext(options)
{
    public const string DatabaseName = "MigrationProbeDb.db";

    /// <summary>Migration id to stop at when staging a pre-upgrade database.</summary>
    public const string V1Id = "20260101000001_Create";

    /// <summary>The migration under test — applied over rows that already exist.</summary>
    public const string V2Id = "20260101000002_AddIndex";

    public const string IndexName = "IX_ProbeRows_StampedAt";

    public DbSet<ProbeRow> Rows => Set<ProbeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ProbeRow>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.ToTable("ProbeRows");
            entity.HasIndex(r => r.StampedAt).HasDatabaseName(IndexName);
        });
    }
}

/// <summary>
/// Written by hand rather than generated. The TestApp is a Blazor WebAssembly
/// project, so <c>dotnet ef migrations add</c> cannot load it, and the
/// design-time snapshot a generated migration carries is not needed at
/// runtime — <c>Migrate()</c> discovers migrations by these two attributes and
/// runs <see cref="Up"/>. The id must keep EF's 15-character timestamp prefix:
/// <c>MigrationsIdGenerator.GetName</c> takes <c>Substring(16)</c> and throws
/// on anything shorter.
/// </summary>
[DbContext(typeof(MigrationProbeContext))]
[Migration(MigrationProbeContext.V1Id)]
public sealed class V1Create : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "ProbeRows",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Payload = table.Column<string>(maxLength: 64, nullable: false),
                StampedAt = table.Column<DateTime>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ProbeRows", x => x.Id));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ProbeRows");
}

/// <summary>
/// The migration under test: an index build, which is the shape of schema
/// change whose cost scales with the rows already present.
/// </summary>
[DbContext(typeof(MigrationProbeContext))]
[Migration(MigrationProbeContext.V2Id)]
public sealed class V2AddIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateIndex(
            name: MigrationProbeContext.IndexName,
            table: "ProbeRows",
            column: "StampedAt");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropIndex(
            name: MigrationProbeContext.IndexName,
            table: "ProbeRows");
}
