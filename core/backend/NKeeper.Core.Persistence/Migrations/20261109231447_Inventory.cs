using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Inventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hosts",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hosts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "host_access_grants",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tag_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tag_value = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_access_grants", x => x.id);
                    table.CheckConstraint("ck_host_access_grants_target", "(host_id IS NULL) <> (tag_name IS NULL)");
                    table.ForeignKey(
                        name: "FK_host_access_grants_hosts_host_id",
                        column: x => x.host_id,
                        principalSchema: "core",
                        principalTable: "hosts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_host_access_grants_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "host_tags",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    value = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_tags", x => x.id);
                    table.ForeignKey(
                        name: "FK_host_tags_hosts_host_id",
                        column: x => x.host_id,
                        principalSchema: "core",
                        principalTable: "hosts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_host_access_grants_host_id",
                schema: "core",
                table: "host_access_grants",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "IX_host_access_grants_user_id",
                schema: "core",
                table: "host_access_grants",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_host_tags_host_id_name_value",
                schema: "core",
                table: "host_tags",
                columns: new[] { "host_id", "name", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_host_tags_name",
                schema: "core",
                table: "host_tags",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_hosts_name",
                schema: "core",
                table: "hosts",
                column: "name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "host_access_grants",
                schema: "core");

            migrationBuilder.DropTable(
                name: "host_tags",
                schema: "core");

            migrationBuilder.DropTable(
                name: "hosts",
                schema: "core");
        }
    }
}
