using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JumpHostsAndTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "jump_credential_id",
                schema: "core",
                table: "hosts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "jump_host_id",
                schema: "core",
                table: "hosts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "jump_service_id",
                schema: "core",
                table: "hosts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "host_trusted_keys",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    address = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    algorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    trusted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trusted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_trusted_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_host_trusted_keys_hosts_host_id",
                        column: x => x.host_id,
                        principalSchema: "core",
                        principalTable: "hosts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_host_trusted_keys_users_trusted_by_user_id",
                        column: x => x.trusted_by_user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_hosts_jump_credential_id",
                schema: "core",
                table: "hosts",
                column: "jump_credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_hosts_jump_host_id",
                schema: "core",
                table: "hosts",
                column: "jump_host_id");

            migrationBuilder.CreateIndex(
                name: "IX_hosts_jump_service_id",
                schema: "core",
                table: "hosts",
                column: "jump_service_id");

            migrationBuilder.CreateIndex(
                name: "IX_host_trusted_keys_host_id_kind_address_port_algorithm",
                schema: "core",
                table: "host_trusted_keys",
                columns: new[] { "host_id", "kind", "address", "port", "algorithm" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_host_trusted_keys_trusted_by_user_id",
                schema: "core",
                table: "host_trusted_keys",
                column: "trusted_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_hosts_host_credentials_jump_credential_id",
                schema: "core",
                table: "hosts",
                column: "jump_credential_id",
                principalSchema: "core",
                principalTable: "host_credentials",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_hosts_host_services_jump_service_id",
                schema: "core",
                table: "hosts",
                column: "jump_service_id",
                principalSchema: "core",
                principalTable: "host_services",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_hosts_hosts_jump_host_id",
                schema: "core",
                table: "hosts",
                column: "jump_host_id",
                principalSchema: "core",
                principalTable: "hosts",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_hosts_host_credentials_jump_credential_id",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropForeignKey(
                name: "FK_hosts_host_services_jump_service_id",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropForeignKey(
                name: "FK_hosts_hosts_jump_host_id",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropTable(
                name: "host_trusted_keys",
                schema: "core");

            migrationBuilder.DropIndex(
                name: "IX_hosts_jump_credential_id",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropIndex(
                name: "IX_hosts_jump_host_id",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropIndex(
                name: "IX_hosts_jump_service_id",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "jump_credential_id",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "jump_host_id",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "jump_service_id",
                schema: "core",
                table: "hosts");
        }
    }
}
