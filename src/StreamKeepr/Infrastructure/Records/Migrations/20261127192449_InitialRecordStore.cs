using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamKeepr.Infrastructure.Records.Migrations;

/// <inheritdoc />
public partial class _20261127192449_InitialRecordStore : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AdminToken",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                Ciphertext = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AdminToken", x => x.Id);
                table.CheckConstraint("CK_AdminToken_Singleton", "\"Id\" = 1");
            });

        migrationBuilder.CreateTable(
            name: "AppliedSeeds",
            columns: table => new
            {
                Name = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AppliedSeeds", x => x.Name);
            });

        migrationBuilder.CreateTable(
            name: "Channels",
            columns: table => new
            {
                Login = table.Column<string>(type: "TEXT", nullable: false),
                Position = table.Column<int>(type: "INTEGER", nullable: false),
                Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                UserId = table.Column<string>(type: "TEXT", nullable: true),
                DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                AvatarUrl = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Channels", x => x.Login);
            });

        migrationBuilder.CreateTable(
            name: "EventSubConduit",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                ConduitId = table.Column<string>(type: "TEXT", nullable: false),
                ClientId = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EventSubConduit", x => x.Id);
                table.CheckConstraint("CK_EventSubConduit_Singleton", "\"Id\" = 1");
            });

        migrationBuilder.CreateTable(
            name: "Sessions",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Channel = table.Column<string>(type: "TEXT", nullable: false),
                StreamId = table.Column<string>(type: "TEXT", nullable: false),
                StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                PartCount = table.Column<int>(type: "INTEGER", nullable: true),
                SegmentCount = table.Column<int>(type: "INTEGER", nullable: true),
                HasThumbnail = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Sessions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ChatSessions",
            columns: table => new
            {
                SessionId = table.Column<string>(type: "TEXT", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                Transport = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                LeadInSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                LastMessageAt = table.Column<long>(type: "INTEGER", nullable: true),
                MessageCount = table.Column<int>(type: "INTEGER", nullable: false),
                StoredBytes = table.Column<long>(type: "INTEGER", nullable: false),
                Truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", nullable: true),
                PurgedCount = table.Column<int>(type: "INTEGER", nullable: true),
                PurgedAt = table.Column<long>(type: "INTEGER", nullable: true),
                PurgedDeletedText = table.Column<bool>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ChatSessions", x => x.SessionId);
                table.ForeignKey(
                    name: "FK_ChatSessions_Sessions_SessionId",
                    column: x => x.SessionId,
                    principalTable: "Sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ResumePositions",
            columns: table => new
            {
                SessionId = table.Column<string>(type: "TEXT", nullable: false),
                PositionSeconds = table.Column<double>(type: "REAL", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ResumePositions", x => x.SessionId);
                table.ForeignKey(
                    name: "FK_ResumePositions_Sessions_SessionId",
                    column: x => x.SessionId,
                    principalTable: "Sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "SessionMetadataChanges",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SessionId = table.Column<string>(type: "TEXT", nullable: false),
                At = table.Column<long>(type: "INTEGER", nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: false),
                Game = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SessionMetadataChanges", x => x.Id);
                table.ForeignKey(
                    name: "FK_SessionMetadataChanges_Sessions_SessionId",
                    column: x => x.SessionId,
                    principalTable: "Sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ViewerSamples",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SessionId = table.Column<string>(type: "TEXT", nullable: false),
                OffsetSeconds = table.Column<double>(type: "REAL", nullable: false),
                Count = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ViewerSamples", x => x.Id);
                table.ForeignKey(
                    name: "FK_ViewerSamples_Sessions_SessionId",
                    column: x => x.SessionId,
                    principalTable: "Sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ChatEvents",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SessionId = table.Column<string>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                At = table.Column<long>(type: "INTEGER", nullable: false),
                TargetMessageId = table.Column<string>(type: "TEXT", nullable: true),
                TargetUserId = table.Column<string>(type: "TEXT", nullable: true),
                TargetLogin = table.Column<string>(type: "TEXT", nullable: true),
                FromAt = table.Column<long>(type: "INTEGER", nullable: true),
                ToAt = table.Column<long>(type: "INTEGER", nullable: true),
                Count = table.Column<int>(type: "INTEGER", nullable: true),
                Reason = table.Column<string>(type: "TEXT", nullable: true),
                EmoteOnly = table.Column<bool>(type: "INTEGER", nullable: true),
                SubscribersOnly = table.Column<bool>(type: "INTEGER", nullable: true),
                UniqueChat = table.Column<bool>(type: "INTEGER", nullable: true),
                SlowSeconds = table.Column<int>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ChatEvents", x => x.Id);
                table.ForeignKey(
                    name: "FK_ChatEvents_ChatSessions_SessionId",
                    column: x => x.SessionId,
                    principalTable: "ChatSessions",
                    principalColumn: "SessionId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ChatMessages",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SessionId = table.Column<string>(type: "TEXT", nullable: false),
                MessageId = table.Column<string>(type: "TEXT", nullable: false),
                At = table.Column<long>(type: "INTEGER", nullable: false),
                Local = table.Column<bool>(type: "INTEGER", nullable: true),
                UserId = table.Column<string>(type: "TEXT", nullable: true),
                Login = table.Column<string>(type: "TEXT", nullable: false),
                Display = table.Column<string>(type: "TEXT", nullable: false),
                Color = table.Column<string>(type: "TEXT", nullable: true),
                BadgesJson = table.Column<string>(type: "TEXT", nullable: false),
                Text = table.Column<string>(type: "TEXT", nullable: true),
                EmotesJson = table.Column<string>(type: "TEXT", nullable: false),
                Bits = table.Column<int>(type: "INTEGER", nullable: true),
                ReplyMessageId = table.Column<string>(type: "TEXT", nullable: true),
                ReplyLogin = table.Column<string>(type: "TEXT", nullable: true),
                ReplyDisplay = table.Column<string>(type: "TEXT", nullable: true),
                ReplyText = table.Column<string>(type: "TEXT", nullable: true),
                IsAction = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ChatMessages", x => x.Id);
                table.ForeignKey(
                    name: "FK_ChatMessages_ChatSessions_SessionId",
                    column: x => x.SessionId,
                    principalTable: "ChatSessions",
                    principalColumn: "SessionId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Channels_Position",
            table: "Channels",
            column: "Position");

        migrationBuilder.CreateIndex(
            name: "IX_ChatEvents_SessionId_At",
            table: "ChatEvents",
            columns: new[] { "SessionId", "At" });

        migrationBuilder.CreateIndex(
            name: "IX_ChatMessages_SessionId_At",
            table: "ChatMessages",
            columns: new[] { "SessionId", "At" });

        migrationBuilder.CreateIndex(
            name: "IX_ChatMessages_SessionId_MessageId",
            table: "ChatMessages",
            columns: new[] { "SessionId", "MessageId" });

        migrationBuilder.CreateIndex(
            name: "IX_SessionMetadataChanges_SessionId_At",
            table: "SessionMetadataChanges",
            columns: new[] { "SessionId", "At" });

        migrationBuilder.CreateIndex(
            name: "IX_Sessions_Channel_StartedAt",
            table: "Sessions",
            columns: new[] { "Channel", "StartedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Sessions_Status",
            table: "Sessions",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_ViewerSamples_SessionId_OffsetSeconds",
            table: "ViewerSamples",
            columns: new[] { "SessionId", "OffsetSeconds" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AdminToken");

        migrationBuilder.DropTable(
            name: "AppliedSeeds");

        migrationBuilder.DropTable(
            name: "Channels");

        migrationBuilder.DropTable(
            name: "ChatEvents");

        migrationBuilder.DropTable(
            name: "ChatMessages");

        migrationBuilder.DropTable(
            name: "EventSubConduit");

        migrationBuilder.DropTable(
            name: "ResumePositions");

        migrationBuilder.DropTable(
            name: "SessionMetadataChanges");

        migrationBuilder.DropTable(
            name: "ViewerSamples");

        migrationBuilder.DropTable(
            name: "ChatSessions");

        migrationBuilder.DropTable(
            name: "Sessions");
    }
}
