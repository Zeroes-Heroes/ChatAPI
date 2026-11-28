using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncServerDataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncAccounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LegacyUserId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncAccounts", x => x.Id);
                    table.CheckConstraint("CK_SyncAccounts_EndedAtUtc_NotBeforeCreatedAtUtc", "\"EndedAtUtc\" IS NULL OR \"EndedAtUtc\" >= \"CreatedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_SyncAccounts_LegacyUserId_Users_Id",
                        column: x => x.LegacyUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncChats",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncChats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncAccountCatalogCounters",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NextCatalogSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncAccountCatalogCounters", x => x.AccountId);
                    table.CheckConstraint("CK_SyncAccountCatalogCounters_NextCatalogSequence_Positive", "\"NextCatalogSequence\" > 0");
                    table.ForeignKey(
                        name: "FK_SyncAccountCatalogCounters_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncDeviceRegistrations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncDeviceRegistrations", x => x.Id);
                    table.UniqueConstraint("AK_SyncDeviceRegistrations_AccountId_Id", x => new { x.AccountId, x.Id });
                    table.ForeignKey(
                        name: "FK_SyncDeviceRegistrations_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncCatalogEntries",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CatalogSequence = table.Column<long>(type: "bigint", nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCatalogEntries", x => new { x.AccountId, x.CatalogSequence });
                    table.CheckConstraint("CK_SyncCatalogEntries_CatalogSequence_Positive", "\"CatalogSequence\" > 0");
                    table.ForeignKey(
                        name: "FK_SyncCatalogEntries_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncCatalogEntries_ChatId_SyncChats_Id",
                        column: x => x.ChatId,
                        principalTable: "SyncChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncChatChanges",
                columns: table => new
                {
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SecondaryId = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    MembershipEventKind = table.Column<int>(type: "integer", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncChatChanges", x => new { x.ChatId, x.ChangeSequence });
                    table.CheckConstraint("CK_SyncChatChanges_ChangeSequence_Positive", "\"ChangeSequence\" > 0");
                    table.ForeignKey(
                        name: "FK_SyncChatChanges_ChatId_SyncChats_Id",
                        column: x => x.ChatId,
                        principalTable: "SyncChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncChatSequenceCounters",
                columns: table => new
                {
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NextMessageSequence = table.Column<long>(type: "bigint", nullable: false),
                    NextChangeSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncChatSequenceCounters", x => x.ChatId);
                    table.CheckConstraint("CK_SyncChatSequenceCounters_NextChangeSequence_Positive", "\"NextChangeSequence\" > 0");
                    table.CheckConstraint("CK_SyncChatSequenceCounters_NextMessageSequence_Positive", "\"NextMessageSequence\" > 0");
                    table.ForeignKey(
                        name: "FK_SyncChatSequenceCounters_ChatId_SyncChats_Id",
                        column: x => x.ChatId,
                        principalTable: "SyncChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncMemberships",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RightsVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<int>(type: "integer", nullable: true),
                    EndedByAccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMemberships", x => x.Id);
                    table.UniqueConstraint("AK_SyncMemberships_ChatId_AccountId_Id", x => new { x.ChatId, x.AccountId, x.Id });
                    table.UniqueConstraint("AK_SyncMemberships_ChatId_Id", x => new { x.ChatId, x.Id });
                    table.ForeignKey(
                        name: "FK_SyncMemberships_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMemberships_ChatId_SyncChats_Id",
                        column: x => x.ChatId,
                        principalTable: "SyncChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMemberships_EndedByAccountId_SyncAccounts_Id",
                        column: x => x.EndedByAccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncLocalInstances",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLocalInstances", x => x.Id);
                    table.UniqueConstraint("AK_SyncLocalInstances_DeviceRegistrationId_Id", x => new { x.DeviceRegistrationId, x.Id });
                    table.ForeignKey(
                        name: "FK_SyncLocalInstances_DeviceRegistrationId_SyncDeviceRegistrations_Id",
                        column: x => x.DeviceRegistrationId,
                        principalTable: "SyncDeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncMembershipHistoryGrants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FromMessageSequence = table.Column<long>(type: "bigint", nullable: false),
                    ThroughMessageSequence = table.Column<long>(type: "bigint", nullable: true),
                    FrozenAtChangeSequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMembershipHistoryGrants", x => x.Id);
                    table.CheckConstraint("CK_SyncMembershipHistoryGrants_FromMessageSequence_Positive", "\"FromMessageSequence\" > 0");
                    table.CheckConstraint("CK_SyncMembershipHistoryGrants_FrozenAtChangeSequence_Positive", "\"FrozenAtChangeSequence\" IS NULL OR \"FrozenAtChangeSequence\" > 0");
                    table.CheckConstraint("CK_SyncMembershipHistoryGrants_ThroughMessageSequence_Valid", "\"ThroughMessageSequence\" IS NULL OR \"ThroughMessageSequence\" >= \"FromMessageSequence\"");
                    table.ForeignKey(
                        name: "FK_SyncMembershipHistoryGrants_MembershipId_SyncMemberships_Id",
                        column: x => x.MembershipId,
                        principalTable: "SyncMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncMembershipMetadataSnapshots",
                columns: table => new
                {
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ChatKindAtSnapshot = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMembershipMetadataSnapshots", x => x.MembershipId);
                    table.ForeignKey(
                        name: "FK_SyncMembershipMetadataSnapshots_MembershipId_SyncMemberships_Id",
                        column: x => x.MembershipId,
                        principalTable: "SyncMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncMessages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SenderAccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SenderMembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MessageSequence = table.Column<long>(type: "bigint", nullable: false),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    OperationKind = table.Column<int>(type: "integer", nullable: false),
                    AuthorKeyId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AuthorKeyVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SignedPackage = table.Column<string>(type: "text", nullable: false),
                    PackageDigest = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMessages", x => x.Id);
                    table.UniqueConstraint("AK_SyncMessages_ChatId_Id", x => new { x.ChatId, x.Id });
                    table.UniqueConstraint("AK_SyncMessages_ChatId_SenderAccountId_Id", x => new { x.ChatId, x.SenderAccountId, x.Id });
                    table.CheckConstraint("CK_SyncMessages_ChangeSequence_Positive", "\"ChangeSequence\" > 0");
                    table.CheckConstraint("CK_SyncMessages_MessageSequence_Positive", "\"MessageSequence\" > 0");
                    table.ForeignKey(
                        name: "FK_SyncMessages_ChatId_SenderAccountId_SenderMembershipId_SyncMemberships",
                        columns: x => new { x.ChatId, x.SenderAccountId, x.SenderMembershipId },
                        principalTable: "SyncMemberships",
                        principalColumns: new[] { "ChatId", "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMessages_ChatId_SyncChats_Id",
                        column: x => x.ChatId,
                        principalTable: "SyncChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMessages_SenderAccountId_SyncAccounts_Id",
                        column: x => x.SenderAccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncMembershipMetadataSnapshotMembers",
                columns: table => new
                {
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ParticipantAccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ParticipantMembershipIdAtSnapshot = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ParticipantStateAtSnapshot = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMembershipMetadataSnapshotMembers", x => new { x.MembershipId, x.ParticipantAccountId });
                    table.ForeignKey(
                        name: "FK_SyncMembershipMetadataSnapshotMembers_ChatId_MembershipId_SyncMemberships",
                        columns: x => new { x.ChatId, x.MembershipId },
                        principalTable: "SyncMemberships",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMembershipMetadataSnapshotMembers_ChatId_ParticipantMembershipId_SyncMemberships",
                        columns: x => new { x.ChatId, x.ParticipantMembershipIdAtSnapshot },
                        principalTable: "SyncMemberships",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMembershipMetadataSnapshotMembers_MembershipId_SyncMembershipMetadataSnapshots_MembershipId",
                        column: x => x.MembershipId,
                        principalTable: "SyncMembershipMetadataSnapshots",
                        principalColumn: "MembershipId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncMembershipMetadataSnapshotMembers_ParticipantAccountId_SyncAccounts_Id",
                        column: x => x.ParticipantAccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncMembershipMessageStatusSnapshots",
                columns: table => new
                {
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecipientAccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SeenReason = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMembershipMessageStatusSnapshots", x => new { x.MembershipId, x.MessageId, x.RecipientAccountId });
                    table.ForeignKey(
                        name: "FK_SyncMembershipMessageStatusSnapshots_ChatId_MembershipId_SyncMemberships",
                        columns: x => new { x.ChatId, x.MembershipId },
                        principalTable: "SyncMemberships",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncMembershipMessageStatusSnapshots_ChatId_MessageId_SyncMessages",
                        columns: x => new { x.ChatId, x.MessageId },
                        principalTable: "SyncMessages",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMembershipMessageStatusSnapshots_RecipientAccountId_SyncAccounts_Id",
                        column: x => x.RecipientAccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncMessageDeviceStates",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LocalInstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CiphertextStoredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ObservedSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMessageDeviceStates", x => new { x.MessageId, x.DeviceRegistrationId, x.LocalInstanceId });
                    table.ForeignKey(
                        name: "FK_SyncMessageDeviceStates_AccountId_DeviceRegistrationId_SyncDeviceRegistrations",
                        columns: x => new { x.AccountId, x.DeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMessageDeviceStates_ChatId_AccountId_MembershipId_SyncMemberships",
                        columns: x => new { x.ChatId, x.AccountId, x.MembershipId },
                        principalTable: "SyncMemberships",
                        principalColumns: new[] { "ChatId", "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMessageDeviceStates_ChatId_MessageId_SyncMessages",
                        columns: x => new { x.ChatId, x.MessageId },
                        principalTable: "SyncMessages",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncMessageDeviceStates_DeviceRegistrationId_LocalInstanceId_SyncLocalInstances",
                        columns: x => new { x.DeviceRegistrationId, x.LocalInstanceId },
                        principalTable: "SyncLocalInstances",
                        principalColumns: new[] { "DeviceRegistrationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncMessageKeys",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KeyVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProtectedKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    Tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMessageKeys", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_SyncMessageKeys_MessageId_SyncMessages_Id",
                        column: x => x.MessageId,
                        principalTable: "SyncMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncMessageRecipientStates",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SeenReason = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMessageRecipientStates", x => new { x.MessageId, x.MembershipId });
                    table.ForeignKey(
                        name: "FK_SyncMessageRecipientStates_ChatId_AccountId_MembershipId_SyncMemberships",
                        columns: x => new { x.ChatId, x.AccountId, x.MembershipId },
                        principalTable: "SyncMemberships",
                        principalColumns: new[] { "ChatId", "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncMessageRecipientStates_ChatId_MessageId_SyncMessages",
                        columns: x => new { x.ChatId, x.MessageId },
                        principalTable: "SyncMessages",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncOperationResults",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceRegistrationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LocalInstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ClientOperationId = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    PackageDigest = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorCode = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncOperationResults", x => new { x.AccountId, x.DeviceRegistrationId, x.LocalInstanceId, x.ClientOperationId });
                    table.ForeignKey(
                        name: "FK_SyncOperationResults_AccountId_DeviceRegistrationId_SyncDeviceRegistrations",
                        columns: x => new { x.AccountId, x.DeviceRegistrationId },
                        principalTable: "SyncDeviceRegistrations",
                        principalColumns: new[] { "AccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncOperationResults_AccountId_SyncAccounts_Id",
                        column: x => x.AccountId,
                        principalTable: "SyncAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncOperationResults_ChatId_AccountId_MessageId_SyncMessages",
                        columns: x => new { x.ChatId, x.AccountId, x.MessageId },
                        principalTable: "SyncMessages",
                        principalColumns: new[] { "ChatId", "SenderAccountId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncOperationResults_DeviceRegistrationId_LocalInstanceId_SyncLocalInstances",
                        columns: x => new { x.DeviceRegistrationId, x.LocalInstanceId },
                        principalTable: "SyncLocalInstances",
                        principalColumns: new[] { "DeviceRegistrationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LeasedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncOutbox_ChatId_MessageId_SyncMessages",
                        columns: x => new { x.ChatId, x.MessageId },
                        principalTable: "SyncMessages",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncOutbox_SyncChats_ChatId",
                        column: x => x.ChatId,
                        principalTable: "SyncChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncUnreadStates",
                columns: table => new
                {
                    MembershipId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChatId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    OldestUnreadMessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LatestUnreadMessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LatestUnreadCreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncUnreadStates", x => x.MembershipId);
                    table.ForeignKey(
                        name: "FK_SyncUnreadStates_ChatId_LatestUnreadMessageId_SyncMessages",
                        columns: x => new { x.ChatId, x.LatestUnreadMessageId },
                        principalTable: "SyncMessages",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncUnreadStates_ChatId_MembershipId_SyncMemberships",
                        columns: x => new { x.ChatId, x.MembershipId },
                        principalTable: "SyncMemberships",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncUnreadStates_ChatId_OldestUnreadMessageId_SyncMessages",
                        columns: x => new { x.ChatId, x.OldestUnreadMessageId },
                        principalTable: "SyncMessages",
                        principalColumns: new[] { "ChatId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncUnreadStates_MembershipId_SyncMemberships_Id",
                        column: x => x.MembershipId,
                        principalTable: "SyncMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncAccounts_LegacyUserId_Active",
                table: "SyncAccounts",
                column: "LegacyUserId",
                unique: true,
                filter: "\"EndedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SyncCatalogEntries_AccountId_ChatId",
                table: "SyncCatalogEntries",
                columns: new[] { "AccountId", "ChatId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncCatalogEntries_ChatId",
                table: "SyncCatalogEntries",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMembershipHistoryGrants_MembershipId_FromMessageSequence",
                table: "SyncMembershipHistoryGrants",
                columns: new[] { "MembershipId", "FromMessageSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncMembershipMessageStatusSnapshots_ChatId_MembershipId",
                table: "SyncMembershipMessageStatusSnapshots",
                columns: new[] { "ChatId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMembershipMessageStatusSnapshots_ChatId_MessageId",
                table: "SyncMembershipMessageStatusSnapshots",
                columns: new[] { "ChatId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMembershipMessageStatusSnapshots_RecipientAccountId",
                table: "SyncMembershipMessageStatusSnapshots",
                column: "RecipientAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMembershipMetadataSnapshotMembers_ChatId_MembershipId",
                table: "SyncMembershipMetadataSnapshotMembers",
                columns: new[] { "ChatId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMembershipMetadataSnapshotMembers_ChatId_ParticipantMem~",
                table: "SyncMembershipMetadataSnapshotMembers",
                columns: new[] { "ChatId", "ParticipantMembershipIdAtSnapshot" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMembershipMetadataSnapshotMembers_ParticipantAccountId",
                table: "SyncMembershipMetadataSnapshotMembers",
                column: "ParticipantAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMemberships_AccountId",
                table: "SyncMemberships",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMemberships_ChatId_AccountId",
                table: "SyncMemberships",
                columns: new[] { "ChatId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMemberships_EndedByAccountId",
                table: "SyncMemberships",
                column: "EndedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessageDeviceStates_AccountId_DeviceRegistrationId",
                table: "SyncMessageDeviceStates",
                columns: new[] { "AccountId", "DeviceRegistrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessageDeviceStates_ChatId_AccountId",
                table: "SyncMessageDeviceStates",
                columns: new[] { "ChatId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessageDeviceStates_ChatId_AccountId_MembershipId",
                table: "SyncMessageDeviceStates",
                columns: new[] { "ChatId", "AccountId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessageDeviceStates_ChatId_MessageId",
                table: "SyncMessageDeviceStates",
                columns: new[] { "ChatId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessageDeviceStates_DeviceRegistrationId_LocalInstanceId",
                table: "SyncMessageDeviceStates",
                columns: new[] { "DeviceRegistrationId", "LocalInstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessageRecipientStates_ChatId_AccountId",
                table: "SyncMessageRecipientStates",
                columns: new[] { "ChatId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessageRecipientStates_ChatId_AccountId_MembershipId",
                table: "SyncMessageRecipientStates",
                columns: new[] { "ChatId", "AccountId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessageRecipientStates_ChatId_MessageId",
                table: "SyncMessageRecipientStates",
                columns: new[] { "ChatId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessages_ChatId_ChangeSequence",
                table: "SyncMessages",
                columns: new[] { "ChatId", "ChangeSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessages_ChatId_MessageSequence",
                table: "SyncMessages",
                columns: new[] { "ChatId", "MessageSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessages_ChatId_SenderAccountId_SenderMembershipId",
                table: "SyncMessages",
                columns: new[] { "ChatId", "SenderAccountId", "SenderMembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMessages_SenderAccountId",
                table: "SyncMessages",
                column: "SenderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncOperationResults_ChatId_AccountId_MessageId",
                table: "SyncOperationResults",
                columns: new[] { "ChatId", "AccountId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncOperationResults_DeviceRegistrationId_LocalInstanceId",
                table: "SyncOperationResults",
                columns: new[] { "DeviceRegistrationId", "LocalInstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncOutbox_ChatId_MessageId",
                table: "SyncOutbox",
                columns: new[] { "ChatId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncOutbox_Status_LeasedUntilUtc_Id",
                table: "SyncOutbox",
                columns: new[] { "Status", "LeasedUntilUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncUnreadStates_ChatId_LatestUnreadMessageId",
                table: "SyncUnreadStates",
                columns: new[] { "ChatId", "LatestUnreadMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncUnreadStates_ChatId_MembershipId",
                table: "SyncUnreadStates",
                columns: new[] { "ChatId", "MembershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncUnreadStates_ChatId_OldestUnreadMessageId",
                table: "SyncUnreadStates",
                columns: new[] { "ChatId", "OldestUnreadMessageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncAccountCatalogCounters");

            migrationBuilder.DropTable(
                name: "SyncCatalogEntries");

            migrationBuilder.DropTable(
                name: "SyncChatChanges");

            migrationBuilder.DropTable(
                name: "SyncChatSequenceCounters");

            migrationBuilder.DropTable(
                name: "SyncMembershipHistoryGrants");

            migrationBuilder.DropTable(
                name: "SyncMembershipMessageStatusSnapshots");

            migrationBuilder.DropTable(
                name: "SyncMembershipMetadataSnapshotMembers");

            migrationBuilder.DropTable(
                name: "SyncMessageDeviceStates");

            migrationBuilder.DropTable(
                name: "SyncMessageKeys");

            migrationBuilder.DropTable(
                name: "SyncMessageRecipientStates");

            migrationBuilder.DropTable(
                name: "SyncOperationResults");

            migrationBuilder.DropTable(
                name: "SyncOutbox");

            migrationBuilder.DropTable(
                name: "SyncUnreadStates");

            migrationBuilder.DropTable(
                name: "SyncMembershipMetadataSnapshots");

            migrationBuilder.DropTable(
                name: "SyncLocalInstances");

            migrationBuilder.DropTable(
                name: "SyncMessages");

            migrationBuilder.DropTable(
                name: "SyncDeviceRegistrations");

            migrationBuilder.DropTable(
                name: "SyncMemberships");

            migrationBuilder.DropTable(
                name: "SyncAccounts");

            migrationBuilder.DropTable(
                name: "SyncChats");
        }
    }
}
