using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Acropolis.Platform.Infrastructure.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261004000100_InitialPlatformSchema")]
public sealed class InitialPlatformSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(PlatformDbContext.Schema);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep the schema: EF owns the history table and updates it after Down.
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.12");
        modelBuilder.HasDefaultSchema(PlatformDbContext.Schema);
    }
}
