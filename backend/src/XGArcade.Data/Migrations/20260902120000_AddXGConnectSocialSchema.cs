using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XGArcade.Data.Migrations
{
    /// <inheritdoc />
    // S-208/ADR-0103: Core.Social (COMP-16)'s FriendRequest/Friendship
    // (REQ-1401), Challenge (REQ-1402), MatchmakingOptIn (REQ-1403), and
    // Games.XGConnect (COMP-17)'s ConnectMatch/ConnectTargetPick
    // (REQ-1404/1405), ConnectChainStep (REQ-1406/1407), and
    // ConnectChatMessage (REQ-1410). Schema + repository CRUD only — no
    // service/business logic in this story. See ADR-0103 for the full
    // component-split and ConnectMatch-is-not-a-Round reasoning.
    public partial class AddXGConnectSocialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FriendRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FriendRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FriendRequests_Users_RequesterUserId",
                        column: x => x.RequesterUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FriendRequests_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Friendships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserBId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Friendships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Friendships_Users_UserAId",
                        column: x => x.UserAId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Friendships_Users_UserBId",
                        column: x => x.UserBId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengedUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResultingMatchId = table.Column<Guid>(type: "uuid", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Challenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Challenges_Users_ChallengerUserId",
                        column: x => x.ChallengerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Challenges_Users_ChallengedUserId",
                        column: x => x.ChallengedUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatchmakingOptIns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptedInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResultingMatchId = table.Column<Guid>(type: "uuid", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchmakingOptIns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchmakingOptIns_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectMatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerAUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlayerBUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeadlineUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectMatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectTargetPicks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectMatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectTargetPicks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectTargetPicks_ConnectMatches_ConnectMatchId",
                        column: x => x.ConnectMatchId,
                        principalTable: "ConnectMatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectTargetPicks_Players_TargetPlayerId",
                        column: x => x.TargetPlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectChainSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectMatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    CandidatePlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedClubName = table.Column<string>(type: "text", nullable: false),
                    IsValid = table.Column<bool>(type: "boolean", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectChainSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectChainSteps_ConnectMatches_ConnectMatchId",
                        column: x => x.ConnectMatchId,
                        principalTable: "ConnectMatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectChainSteps_Players_CandidatePlayerId",
                        column: x => x.CandidatePlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectMatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageText = table.Column<string>(type: "text", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectChatMessages_ConnectMatches_ConnectMatchId",
                        column: x => x.ConnectMatchId,
                        principalTable: "ConnectMatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FriendRequests_RequesterUserId_RecipientUserId",
                table: "FriendRequests",
                columns: new[] { "RequesterUserId", "RecipientUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_FriendRequests_RecipientUserId",
                table: "FriendRequests",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Friendships_UserAId_UserBId",
                table: "Friendships",
                columns: new[] { "UserAId", "UserBId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Friendships_UserBId",
                table: "Friendships",
                column: "UserBId");

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_ChallengerUserId_ChallengedUserId",
                table: "Challenges",
                columns: new[] { "ChallengerUserId", "ChallengedUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_ChallengedUserId",
                table: "Challenges",
                column: "ChallengedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchmakingOptIns_UserId",
                table: "MatchmakingOptIns",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchmakingOptIns_Status_ExpiresAt",
                table: "MatchmakingOptIns",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectTargetPicks_ConnectMatchId_UserId",
                table: "ConnectTargetPicks",
                columns: new[] { "ConnectMatchId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectTargetPicks_TargetPlayerId",
                table: "ConnectTargetPicks",
                column: "TargetPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectChainSteps_ConnectMatchId_UserId_Position_AttemptNumber",
                table: "ConnectChainSteps",
                columns: new[] { "ConnectMatchId", "UserId", "Position", "AttemptNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectChainSteps_CandidatePlayerId",
                table: "ConnectChainSteps",
                column: "CandidatePlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectChatMessages_ConnectMatchId_SentAt",
                table: "ConnectChatMessages",
                columns: new[] { "ConnectMatchId", "SentAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectChatMessages");

            migrationBuilder.DropTable(
                name: "ConnectChainSteps");

            migrationBuilder.DropTable(
                name: "ConnectTargetPicks");

            migrationBuilder.DropTable(
                name: "ConnectMatches");

            migrationBuilder.DropTable(
                name: "MatchmakingOptIns");

            migrationBuilder.DropTable(
                name: "Challenges");

            migrationBuilder.DropTable(
                name: "Friendships");

            migrationBuilder.DropTable(
                name: "FriendRequests");
        }
    }
}
