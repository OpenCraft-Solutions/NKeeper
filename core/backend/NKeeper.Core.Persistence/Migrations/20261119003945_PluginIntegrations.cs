using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PluginIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "host_data",
                schema: "core",
                columns: table => new
                {
                    plugin_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_data", x => new { x.plugin_id, x.host_id });
                    table.ForeignKey(
                        name: "FK_host_data_hosts_host_id",
                        column: x => x.host_id,
                        principalSchema: "core",
                        principalTable: "hosts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_integrations",
                schema: "core",
                columns: table => new
                {
                    plugin_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_integrations", x => new { x.plugin_id, x.name });
                    table.ForeignKey(
                        name: "FK_plugin_integrations_host_credentials_credential_id",
                        column: x => x.credential_id,
                        principalSchema: "core",
                        principalTable: "host_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plugin_integrations_host_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "core",
                        principalTable: "host_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plugin_integrations_hosts_host_id",
                        column: x => x.host_id,
                        principalSchema: "core",
                        principalTable: "hosts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plugin_integrations_users_set_by_user_id",
                        column: x => x.set_by_user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "plugin_settings",
                schema: "core",
                columns: table => new
                {
                    plugin_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_settings", x => x.plugin_id);
                    table.ForeignKey(
                        name: "FK_plugin_settings_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_host_data_host_id",
                schema: "core",
                table: "host_data",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_integrations_credential_id",
                schema: "core",
                table: "plugin_integrations",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_integrations_host_id",
                schema: "core",
                table: "plugin_integrations",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_integrations_service_id",
                schema: "core",
                table: "plugin_integrations",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_integrations_set_by_user_id",
                schema: "core",
                table: "plugin_integrations",
                column: "set_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_settings_updated_by_user_id",
                schema: "core",
                table: "plugin_settings",
                column: "updated_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "host_data",
                schema: "core");

            migrationBuilder.DropTable(
                name: "plugin_integrations",
                schema: "core");

            migrationBuilder.DropTable(
                name: "plugin_settings",
                schema: "core");
        }
    }
}
