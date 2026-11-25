using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstanceLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "instance_lock",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    locked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instance_lock", x => x.id);
                    table.ForeignKey(
                        name: "FK_instance_lock_users_locked_by_user_id",
                        column: x => x.locked_by_user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_instance_lock_locked_by_user_id",
                schema: "core",
                table: "instance_lock",
                column: "locked_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "instance_lock",
                schema: "core");
        }
    }
}
