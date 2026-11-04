using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrackBoard.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDragResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "drag_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    time_ms = table.Column<int>(type: "integer", nullable: false),
                    trap_speed_kmh = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drag_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_drag_results_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_drag_results_metric_time_ms",
                table: "drag_results",
                columns: new[] { "metric", "time_ms" });

            migrationBuilder.CreateIndex(
                name: "ix_drag_results_session_id_metric",
                table: "drag_results",
                columns: new[] { "session_id", "metric" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "drag_results");
        }
    }
}
