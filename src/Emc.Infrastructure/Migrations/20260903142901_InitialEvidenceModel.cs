using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialEvidenceModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    ActingUserId = table.Column<int>(type: "int", nullable: true),
                    ActingUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AffectedRecordType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AffectedRecordId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PreviousValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    SourceAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceRooms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OrganizationOrUnit = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceRooms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AuthoritativeMode = table.Column<int>(type: "int", nullable: false),
                    NumberingMode = table.Column<int>(type: "int", nullable: false),
                    AutomatedSystemApprovalReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AutomatedSystemApprovalDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AccreditedClassificationLevel = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LocalSuspenseReviewThresholdDays = table.Column<int>(type: "int", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActiveDirectorySid = table.Column<string>(type: "nvarchar(184)", maxLength: 184, nullable: false),
                    UserPrincipalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RankOrGrade = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OrganizationOrUnit = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Cases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseControlNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Synopsis = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    EvidenceRoomId = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClassificationMarking = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsClosed = table.Column<bool>(type: "bit", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cases_EvidenceRooms_EvidenceRoomId",
                        column: x => x.EvidenceRoomId,
                        principalTable: "EvidenceRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceRoomNumberingPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvidenceRoomId = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Layout = table.Column<int>(type: "int", nullable: false),
                    SequenceWidth = table.Column<int>(type: "int", nullable: false),
                    YearWidth = table.Column<int>(type: "int", nullable: false),
                    Separator = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Basis = table.Column<int>(type: "int", nullable: false),
                    AuthorityReference = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceRoomNumberingPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvidenceRoomNumberingPolicies_EvidenceRooms_EvidenceRoomId",
                        column: x => x.EvidenceRoomId,
                        principalTable: "EvidenceRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StorageLocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvidenceRoomId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageLocations_EvidenceRooms_EvidenceRoomId",
                        column: x => x.EvidenceRoomId,
                        principalTable: "EvidenceRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StorageLocations_StorageLocations_ParentId",
                        column: x => x.ParentId,
                        principalTable: "StorageLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TemporaryIdentifierCounters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvidenceRoomId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    LastOrdinal = table.Column<int>(type: "int", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemporaryIdentifierCounters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemporaryIdentifierCounters_EvidenceRooms_EvidenceRoomId",
                        column: x => x.EvidenceRoomId,
                        principalTable: "EvidenceRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustodianAppointments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvidenceRoomId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    AppointmentType = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AppointmentOrderReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AppointingAuthority = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PersonnelCategory = table.Column<int>(type: "int", nullable: false),
                    EligibilityAttested = table.Column<bool>(type: "bit", nullable: false),
                    SupersedesAppointmentId = table.Column<int>(type: "int", nullable: true),
                    SupersededByAppointmentId = table.Column<int>(type: "int", nullable: true),
                    RecordedByUserId = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodianAppointments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustodianAppointments_EvidenceRooms_EvidenceRoomId",
                        column: x => x.EvidenceRoomId,
                        principalTable: "EvidenceRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodianAppointments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustodyParties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    TitleOrGrade = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OrganizationOrAgency = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AccountableMailNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IdentificationVerified = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyParties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustodyParties_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    EvidenceRoomId = table.Column<int>(type: "int", nullable: true),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    GrantedByUserId = table.Column<int>(type: "int", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_EvidenceRooms_EvidenceRoomId",
                        column: x => x.EvidenceRoomId,
                        principalTable: "EvidenceRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceVouchers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<int>(type: "int", nullable: false),
                    EvidenceRoomId = table.Column<int>(type: "int", nullable: false),
                    TemporaryIdentifier = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PreparedByUserId = table.Column<int>(type: "int", nullable: false),
                    ReceivingActivity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ReceivingActivityLocation = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ReceivedFrom = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    RequestingOfficeCaseNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsRequestForAssistance = table.Column<bool>(type: "bit", nullable: false),
                    AcquiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcquiredAtLocal = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcquiredAtOffset = table.Column<TimeSpan>(type: "time", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReviewStage = table.Column<int>(type: "int", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SubmittedByUserId = table.Column<int>(type: "int", nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceVouchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvidenceVouchers_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceVouchers_EvidenceRooms_EvidenceRoomId",
                        column: x => x.EvidenceRoomId,
                        principalTable: "EvidenceRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustodianDutyAssumptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvidenceRoomId = table.Column<int>(type: "int", nullable: false),
                    PrimaryAppointmentId = table.Column<int>(type: "int", nullable: false),
                    AlternateAppointmentId = table.Column<int>(type: "int", nullable: false),
                    AlternateUserId = table.Column<int>(type: "int", nullable: false),
                    PrimaryAbsenceStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpectedAbsenceEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AlternateAssumedDutiesAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssumptionLedgerAttestation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PrimaryResumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResumptionLedgerAttestation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReasonForAbsence = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordedByUserId = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResumptionRecordedByUserId = table.Column<int>(type: "int", nullable: true),
                    ResumptionRecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodianDutyAssumptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustodianDutyAssumptions_CustodianAppointments_AlternateAppointmentId",
                        column: x => x.AlternateAppointmentId,
                        principalTable: "CustodianAppointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodianDutyAssumptions_CustodianAppointments_PrimaryAppointmentId",
                        column: x => x.PrimaryAppointmentId,
                        principalTable: "CustodianAppointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustodianDutyAssumptions_EvidenceRooms_EvidenceRoomId",
                        column: x => x.EvidenceRoomId,
                        principalTable: "EvidenceRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PrimaryCustodianTransitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvidenceRoomId = table.Column<int>(type: "int", nullable: false),
                    IncomingPrimaryAppointmentId = table.Column<int>(type: "int", nullable: false),
                    OutgoingPrimaryAppointmentId = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    JointInventoryCompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    JointInventoryReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DiscrepanciesResolved = table.Column<bool>(type: "bit", nullable: false),
                    LedgerAttestation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RecordedByUserId = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrimaryCustodianTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrimaryCustodianTransitions_CustodianAppointments_IncomingPrimaryAppointmentId",
                        column: x => x.IncomingPrimaryAppointmentId,
                        principalTable: "CustodianAppointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrimaryCustodianTransitions_CustodianAppointments_OutgoingPrimaryAppointmentId",
                        column: x => x.OutgoingPrimaryAppointmentId,
                        principalTable: "CustodianAppointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PrimaryCustodianTransitions_EvidenceRooms_EvidenceRoomId",
                        column: x => x.EvidenceRoomId,
                        principalTable: "EvidenceRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VoucherId = table.Column<int>(type: "int", nullable: false),
                    ItemNumber = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Quantity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UniqueDeviceIdentifier = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IsPossibleBiohazard = table.Column<bool>(type: "bit", nullable: false),
                    IsFungible = table.Column<bool>(type: "bit", nullable: false),
                    IsSealed = table.Column<bool>(type: "bit", nullable: false),
                    SealDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsCurrency = table.Column<bool>(type: "bit", nullable: false),
                    CurrencyDenominationBreakdown = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CurrencyTotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AccountabilityStatus = table.Column<int>(type: "int", nullable: false),
                    LastEventSequenceNumber = table.Column<int>(type: "int", nullable: false),
                    LastEventHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvidenceItems_EvidenceVouchers_VoucherId",
                        column: x => x.VoucherId,
                        principalTable: "EvidenceVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OfficialDocumentNumberAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VoucherId = table.Column<int>(type: "int", nullable: false),
                    EvidenceRoomId = table.Column<int>(type: "int", nullable: false),
                    DocumentNumber = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    NumberingPolicyId = table.Column<int>(type: "int", nullable: true),
                    CalendarYear = table.Column<int>(type: "int", nullable: false),
                    EnteredByUserId = table.Column<int>(type: "int", nullable: false),
                    EnteredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AttestedAssignedInAuthoritativeLedger = table.Column<bool>(type: "bit", nullable: false),
                    SupersedesAssignmentId = table.Column<int>(type: "int", nullable: true),
                    SupersessionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficialDocumentNumberAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfficialDocumentNumberAssignments_EvidenceRoomNumberingPolicies_NumberingPolicyId",
                        column: x => x.NumberingPolicyId,
                        principalTable: "EvidenceRoomNumberingPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OfficialDocumentNumberAssignments_EvidenceRooms_EvidenceRoomId",
                        column: x => x.EvidenceRoomId,
                        principalTable: "EvidenceRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OfficialDocumentNumberAssignments_EvidenceVouchers_VoucherId",
                        column: x => x.VoucherId,
                        principalTable: "EvidenceVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OfficialDocumentNumberAssignments_OfficialDocumentNumberAssignments_SupersedesAssignmentId",
                        column: x => x.SupersedesAssignmentId,
                        principalTable: "OfficialDocumentNumberAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VoucherReviewActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VoucherId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ResultingStage = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Narrative = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PaperFormCorrectedAndInitialedAttested = table.Column<bool>(type: "bit", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoucherReviewActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoucherReviewActions_EvidenceVouchers_VoucherId",
                        column: x => x.VoucherId,
                        principalTable: "EvidenceVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VoucherReviewActions_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvidenceItemId = table.Column<int>(type: "int", nullable: false),
                    SequenceNumber = table.Column<int>(type: "int", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OccurredAtLocal = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OccurredAtOffset = table.Column<TimeSpan>(type: "time", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByUserId = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SourceDocumentId = table.Column<int>(type: "int", nullable: true),
                    PreviousEventHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EventHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    HashSchemaVersion = table.Column<int>(type: "int", nullable: false),
                    EventKind = table.Column<string>(type: "nvarchar(21)", maxLength: 21, nullable: false),
                    CorrectsEventId = table.Column<int>(type: "int", nullable: true),
                    FieldName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OriginalValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CorrectedValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PreviousEffectiveValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PreviousEffectiveReferenceId = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<int>(type: "int", nullable: true),
                    MfrReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SupervisorNotifiedUserId = table.Column<int>(type: "int", nullable: true),
                    SupervisorNotifiedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SupervisorNotifiedGradeOrTitle = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SupervisorNotifiedOrganization = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SupervisorNotifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReferenceKind = table.Column<int>(type: "int", nullable: true),
                    OriginalReferenceId = table.Column<int>(type: "int", nullable: true),
                    CorrectedReferenceId = table.Column<int>(type: "int", nullable: true),
                    ReleasedByPartyId = table.Column<int>(type: "int", nullable: true),
                    ReceivedByPartyId = table.Column<int>(type: "int", nullable: true),
                    PurposeOfChangeOfCustody = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsScrcni = table.Column<bool>(type: "bit", nullable: true),
                    Destination = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Agency = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DocumentNumber = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    PreviousDocumentNumber = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    AttestedAssignedInAuthoritativeLedger = table.Column<bool>(type: "bit", nullable: true),
                    Laboratory = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ExaminationRequestReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ExhibitNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ExtractionDescription = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ResultReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StorageLocationId = table.Column<int>(type: "int", nullable: true),
                    StorageLocationPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LocationEvent_Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Action = table.Column<int>(type: "int", nullable: true),
                    PerformedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PurposeOfBreach = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SealEvent_MfrReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DirectingSupervisorName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    FromStatus = table.Column<int>(type: "int", nullable: true),
                    ToStatus = table.Column<int>(type: "int", nullable: true),
                    StatusEvent_Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemEvents_CustodyParties_ReceivedByPartyId",
                        column: x => x.ReceivedByPartyId,
                        principalTable: "CustodyParties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ItemEvents_CustodyParties_ReleasedByPartyId",
                        column: x => x.ReleasedByPartyId,
                        principalTable: "CustodyParties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ItemEvents_EvidenceItems_EvidenceItemId",
                        column: x => x.EvidenceItemId,
                        principalTable: "EvidenceItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ItemEvents_ItemEvents_CorrectsEventId",
                        column: x => x.CorrectsEventId,
                        principalTable: "ItemEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActingUserId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "ActingUserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_AffectedRecordType_AffectedRecordId",
                table: "AuditEvents",
                columns: new[] { "AffectedRecordType", "AffectedRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAtUtc",
                table: "AuditEvents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_EvidenceRoomId_CaseControlNumber",
                table: "Cases",
                columns: new[] { "EvidenceRoomId", "CaseControlNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodianAppointments_EvidenceRoomId_UserId_EffectiveFrom",
                table: "CustodianAppointments",
                columns: new[] { "EvidenceRoomId", "UserId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodianAppointments_UserId",
                table: "CustodianAppointments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_CustodianAppointments_OneOpenPerType",
                table: "CustodianAppointments",
                columns: new[] { "EvidenceRoomId", "AppointmentType" },
                unique: true,
                filter: "EffectiveTo IS NULL AND SupersededByAppointmentId IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustodianDutyAssumptions_AlternateAppointmentId",
                table: "CustodianDutyAssumptions",
                column: "AlternateAppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodianDutyAssumptions_AlternateUserId_EvidenceRoomId",
                table: "CustodianDutyAssumptions",
                columns: new[] { "AlternateUserId", "EvidenceRoomId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodianDutyAssumptions_PrimaryAppointmentId",
                table: "CustodianDutyAssumptions",
                column: "PrimaryAppointmentId");

            migrationBuilder.CreateIndex(
                name: "UX_CustodianDutyAssumptions_OneOpenPerRoom",
                table: "CustodianDutyAssumptions",
                column: "EvidenceRoomId",
                unique: true,
                filter: "PrimaryResumedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyParties_UserId",
                table: "CustodyParties",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceItems_SerialNumber",
                table: "EvidenceItems",
                column: "SerialNumber");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceItems_VoucherId_ItemNumber",
                table: "EvidenceItems",
                columns: new[] { "VoucherId", "ItemNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceRoomNumberingPolicies_EvidenceRoomId_EffectiveFrom",
                table: "EvidenceRoomNumberingPolicies",
                columns: new[] { "EvidenceRoomId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceRooms_Name",
                table: "EvidenceRooms",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceVouchers_CaseId",
                table: "EvidenceVouchers",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceVouchers_EvidenceRoomId_TemporaryIdentifier",
                table: "EvidenceVouchers",
                columns: new[] { "EvidenceRoomId", "TemporaryIdentifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemEvents_CorrectionReference",
                table: "ItemEvents",
                columns: new[] { "ReferenceKind", "CorrectedReferenceId" },
                filter: "[CorrectedReferenceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ItemEvents_CorrectsEventId",
                table: "ItemEvents",
                column: "CorrectsEventId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemEvents_ItemChronology",
                table: "ItemEvents",
                columns: new[] { "EvidenceItemId", "OccurredAtUtc", "SequenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemEvents_ReceivedByPartyId",
                table: "ItemEvents",
                column: "ReceivedByPartyId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemEvents_ReleasedByPartyId",
                table: "ItemEvents",
                column: "ReleasedByPartyId");

            migrationBuilder.CreateIndex(
                name: "UX_ItemEvents_ItemSequence",
                table: "ItemEvents",
                columns: new[] { "EvidenceItemId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfficialDocumentNumberAssignments_NumberingPolicyId",
                table: "OfficialDocumentNumberAssignments",
                column: "NumberingPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_OfficialDocumentNumberAssignments_SupersedesAssignmentId",
                table: "OfficialDocumentNumberAssignments",
                column: "SupersedesAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_OfficialDocumentNumberAssignments_VoucherId",
                table: "OfficialDocumentNumberAssignments",
                column: "VoucherId");

            migrationBuilder.CreateIndex(
                name: "UX_DocumentNumber_NeverReusedPerRoomPerYear",
                table: "OfficialDocumentNumberAssignments",
                columns: new[] { "EvidenceRoomId", "CalendarYear", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryCustodianTransitions_EvidenceRoomId_EffectiveFrom",
                table: "PrimaryCustodianTransitions",
                columns: new[] { "EvidenceRoomId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryCustodianTransitions_IncomingPrimaryAppointmentId",
                table: "PrimaryCustodianTransitions",
                column: "IncomingPrimaryAppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryCustodianTransitions_OutgoingPrimaryAppointmentId",
                table: "PrimaryCustodianTransitions",
                column: "OutgoingPrimaryAppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_EvidenceRoomId",
                table: "RoleAssignments",
                column: "EvidenceRoomId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_RoleId",
                table: "RoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_UserId_EvidenceRoomId",
                table: "RoleAssignments",
                columns: new[] { "UserId", "EvidenceRoomId" });

            migrationBuilder.CreateIndex(
                name: "UX_RoleAssignments_OneOpenPerUserRoleRoom",
                table: "RoleAssignments",
                columns: new[] { "UserId", "RoleId", "EvidenceRoomId" },
                unique: true,
                filter: "EffectiveTo IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageLocations_EvidenceRoomId_ParentId_Name",
                table: "StorageLocations",
                columns: new[] { "EvidenceRoomId", "ParentId", "Name" },
                unique: true,
                filter: "[ParentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StorageLocations_ParentId",
                table: "StorageLocations",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_TemporaryIdentifierCounters_EvidenceRoomId_Date",
                table: "TemporaryIdentifierCounters",
                columns: new[] { "EvidenceRoomId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ActiveDirectorySid",
                table: "Users",
                column: "ActiveDirectorySid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserPrincipalName",
                table: "Users",
                column: "UserPrincipalName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VoucherReviewActions_ActorUserId",
                table: "VoucherReviewActions",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_VoucherReviewActions_VoucherId_OccurredAtUtc",
                table: "VoucherReviewActions",
                columns: new[] { "VoucherId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "CustodianDutyAssumptions");

            migrationBuilder.DropTable(
                name: "ItemEvents");

            migrationBuilder.DropTable(
                name: "OfficialDocumentNumberAssignments");

            migrationBuilder.DropTable(
                name: "PrimaryCustodianTransitions");

            migrationBuilder.DropTable(
                name: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "StorageLocations");

            migrationBuilder.DropTable(
                name: "SystemConfigurations");

            migrationBuilder.DropTable(
                name: "TemporaryIdentifierCounters");

            migrationBuilder.DropTable(
                name: "VoucherReviewActions");

            migrationBuilder.DropTable(
                name: "CustodyParties");

            migrationBuilder.DropTable(
                name: "EvidenceItems");

            migrationBuilder.DropTable(
                name: "EvidenceRoomNumberingPolicies");

            migrationBuilder.DropTable(
                name: "CustodianAppointments");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "EvidenceVouchers");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Cases");

            migrationBuilder.DropTable(
                name: "EvidenceRooms");
        }
    }
}
