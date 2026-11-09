using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrackBoard.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLapTopSpeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "top_speed_kmh",
                table: "laps",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "top_speed_kmh",
                table: "laps");
        }
    }
}
