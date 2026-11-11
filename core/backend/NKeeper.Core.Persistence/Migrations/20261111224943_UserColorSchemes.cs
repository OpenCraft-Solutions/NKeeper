using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserColorSchemes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "color_scheme",
                schema: "core",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "navy");

            migrationBuilder.AddColumn<string>(
                name: "custom_color_schemes",
                schema: "core",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "color_scheme",
                schema: "core",
                table: "users");

            migrationBuilder.DropColumn(
                name: "custom_color_schemes",
                schema: "core",
                table: "users");
        }
    }
}
