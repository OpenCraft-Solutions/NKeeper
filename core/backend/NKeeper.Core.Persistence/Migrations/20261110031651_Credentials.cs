using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Credentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "host_credentials",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    domain = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    protected_password = table.Column<byte[]>(type: "bytea", nullable: true),
                    protected_private_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    service_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_credentials", x => x.id);
                    table.ForeignKey(
                        name: "FK_host_credentials_hosts_host_id",
                        column: x => x.host_id,
                        principalSchema: "core",
                        principalTable: "hosts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_host_credentials_host_id",
                schema: "core",
                table: "host_credentials",
                column: "host_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "host_credentials",
                schema: "core");
        }
    }
}
