using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Hardware : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cpu_cores",
                schema: "core",
                table: "hosts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_virtual_machine",
                schema: "core",
                table: "hosts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "mac_address",
                schema: "core",
                table: "hosts",
                type: "character varying(17)",
                maxLength: 17,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "memory_mib",
                schema: "core",
                table: "hosts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "serial_number",
                schema: "core",
                table: "hosts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cpu_cores",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "is_virtual_machine",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "mac_address",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "memory_mib",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "serial_number",
                schema: "core",
                table: "hosts");
        }
    }
}
