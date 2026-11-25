using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "session_recordings",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: true),
                    host_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    session_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    truncated = table.Column<bool>(type: "boolean", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_recordings", x => x.id);
                    table.ForeignKey(
                        name: "FK_session_recordings_hosts_host_id",
                        column: x => x.host_id,
                        principalSchema: "core",
                        principalTable: "hosts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_session_recordings_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_recordings_host_id",
                schema: "core",
                table: "session_recordings",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_recordings_started_at_utc",
                schema: "core",
                table: "session_recordings",
                column: "started_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_session_recordings_user_id",
                schema: "core",
                table: "session_recordings",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_recordings",
                schema: "core");
        }
    }
}
