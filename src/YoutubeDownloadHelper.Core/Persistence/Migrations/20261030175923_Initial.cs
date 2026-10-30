using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YoutubeDownloadHelper.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schema_info",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schema_info", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value_json = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "tools",
                columns: table => new
                {
                    tool = table.Column<string>(type: "TEXT", nullable: false),
                    current = table.Column<string>(type: "TEXT", nullable: true),
                    previous = table.Column<string>(type: "TEXT", nullable: true),
                    channel = table.Column<string>(type: "TEXT", nullable: true),
                    last_check = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tools", x => x.tool);
                });

            migrationBuilder.CreateTable(
                name: "watchers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: false),
                    channel_id = table.Column<string>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    include_videos = table.Column<bool>(type: "INTEGER", nullable: false),
                    include_shorts = table.Column<bool>(type: "INTEGER", nullable: false),
                    include_past_streams = table.Column<bool>(type: "INTEGER", nullable: false),
                    include_members_only = table.Column<bool>(type: "INTEGER", nullable: false),
                    preset_id = table.Column<string>(type: "TEXT", nullable: false),
                    folder_override = table.Column<string>(type: "TEXT", nullable: true),
                    interval_minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    backfill = table.Column<string>(type: "TEXT", nullable: false),
                    backfill_count = table.Column<int>(type: "INTEGER", nullable: true),
                    last_checked_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", nullable: true),
                    needs_login = table.Column<bool>(type: "INTEGER", nullable: false),
                    account_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watchers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: false),
                    extractor = table.Column<string>(type: "TEXT", nullable: true),
                    video_id = table.Column<string>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    channel = table.Column<string>(type: "TEXT", nullable: true),
                    channel_id = table.Column<string>(type: "TEXT", nullable: true),
                    upload_date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    duration_seconds = table.Column<double>(type: "REAL", nullable: true),
                    availability = table.Column<string>(type: "TEXT", nullable: true),
                    live_status = table.Column<string>(type: "TEXT", nullable: true),
                    preset_id = table.Column<string>(type: "TEXT", nullable: false),
                    folder = table.Column<string>(type: "TEXT", nullable: true),
                    force = table.Column<bool>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    position = table.Column<long>(type: "INTEGER", nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<long>(type: "INTEGER", nullable: true),
                    interrupted = table.Column<bool>(type: "INTEGER", nullable: false),
                    error_category = table.Column<string>(type: "TEXT", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", nullable: true),
                    error_args_json = table.Column<string>(type: "TEXT", nullable: true),
                    error_message = table.Column<string>(type: "TEXT", nullable: true),
                    file_path = table.Column<string>(type: "TEXT", nullable: true),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    downloaded_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    finished_at = table.Column<long>(type: "INTEGER", nullable: true),
                    watcher_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    account_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_items_watchers",
                        column: x => x.watcher_id,
                        principalTable: "watchers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "watcher_seen",
                columns: table => new
                {
                    watcher_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    extractor = table.Column<string>(type: "TEXT", nullable: false),
                    video_id = table.Column<string>(type: "TEXT", nullable: false),
                    seen_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watcher_seen", x => new { x.watcher_id, x.extractor, x.video_id });
                    table.ForeignKey(
                        name: "fk_watcher_seen_watchers",
                        column: x => x.watcher_id,
                        principalTable: "watchers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    at = table.Column<long>(type: "INTEGER", nullable: false),
                    from_state = table.Column<string>(type: "TEXT", nullable: true),
                    to_state = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: true),
                    error_category = table.Column<string>(type: "TEXT", nullable: true),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    exit_code = table.Column<int>(type: "INTEGER", nullable: true),
                    output_tail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_item_events_items",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_events_item_id_id",
                table: "item_events",
                columns: new[] { "item_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_items_extractor_video_id",
                table: "items",
                columns: new[] { "extractor", "video_id" });

            migrationBuilder.CreateIndex(
                name: "ix_items_finished_at_id",
                table: "items",
                columns: new[] { "finished_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_items_state_position",
                table: "items",
                columns: new[] { "state", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_items_watcher_id",
                table: "items",
                column: "watcher_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_events");

            migrationBuilder.DropTable(
                name: "schema_info");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "tools");

            migrationBuilder.DropTable(
                name: "watcher_seen");

            migrationBuilder.DropTable(
                name: "items");

            migrationBuilder.DropTable(
                name: "watchers");
        }
    }
}
