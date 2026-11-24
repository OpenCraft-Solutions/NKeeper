using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HypervisorLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "hypervisor_id",
                schema: "core",
                table: "hosts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_hosts_hypervisor_id",
                schema: "core",
                table: "hosts",
                column: "hypervisor_id");

            migrationBuilder.AddForeignKey(
                name: "FK_hosts_hosts_hypervisor_id",
                schema: "core",
                table: "hosts",
                column: "hypervisor_id",
                principalSchema: "core",
                principalTable: "hosts",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_hosts_hosts_hypervisor_id",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropIndex(
                name: "IX_hosts_hypervisor_id",
                schema: "core",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "hypervisor_id",
                schema: "core",
                table: "hosts");
        }
    }
}
