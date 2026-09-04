IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [AuditEvents] (
        [Id] int NOT NULL IDENTITY,
        [EventType] int NOT NULL,
        [ActingUserId] int NULL,
        [ActingUserName] nvarchar(256) NOT NULL,
        [AffectedRecordType] nvarchar(128) NOT NULL,
        [AffectedRecordId] nvarchar(256) NULL,
        [OccurredAtUtc] datetimeoffset NOT NULL,
        [PreviousValue] nvarchar(4000) NULL,
        [NewValue] nvarchar(4000) NULL,
        [Reason] nvarchar(2000) NULL,
        [Succeeded] bit NOT NULL,
        [SourceAddress] nvarchar(64) NULL,
        [CorrelationId] nvarchar(64) NULL,
        CONSTRAINT [PK_AuditEvents] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [EvidenceRooms] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(256) NOT NULL,
        [OrganizationOrUnit] nvarchar(256) NOT NULL,
        [TimeZoneId] nvarchar(128) NOT NULL,
        [IsActive] bit NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_EvidenceRooms] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [Roles] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(64) NOT NULL,
        [Description] nvarchar(512) NOT NULL,
        CONSTRAINT [PK_Roles] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [SystemConfigurations] (
        [Id] int NOT NULL IDENTITY,
        [OrganizationName] nvarchar(256) NOT NULL,
        [AuthoritativeMode] int NOT NULL,
        [NumberingMode] int NOT NULL,
        [AutomatedSystemApprovalReference] nvarchar(256) NULL,
        [AutomatedSystemApprovalDate] datetimeoffset NULL,
        [AccreditedClassificationLevel] nvarchar(128) NOT NULL,
        [LocalSuspenseReviewThresholdDays] int NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_SystemConfigurations] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] int NOT NULL IDENTITY,
        [ActiveDirectorySid] nvarchar(184) NOT NULL,
        [UserPrincipalName] nvarchar(256) NOT NULL,
        [DisplayName] nvarchar(256) NOT NULL,
        [RankOrGrade] nvarchar(64) NULL,
        [OrganizationOrUnit] nvarchar(256) NULL,
        [IsActive] bit NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [Cases] (
        [Id] int NOT NULL IDENTITY,
        [CaseControlNumber] nvarchar(64) NOT NULL,
        [Title] nvarchar(512) NOT NULL,
        [Synopsis] nvarchar(4000) NULL,
        [EvidenceRoomId] int NOT NULL,
        [CreatedByUserId] int NOT NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [ClassificationMarking] nvarchar(128) NOT NULL,
        [IsClosed] bit NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Cases] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Cases_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [EvidenceRoomNumberingPolicies] (
        [Id] int NOT NULL IDENTITY,
        [EvidenceRoomId] int NOT NULL,
        [EffectiveFrom] datetimeoffset NOT NULL,
        [EffectiveTo] datetimeoffset NULL,
        [Layout] int NOT NULL,
        [SequenceWidth] int NOT NULL,
        [YearWidth] int NOT NULL,
        [Separator] nvarchar(3) NOT NULL,
        [Basis] int NOT NULL,
        [AuthorityReference] nvarchar(512) NULL,
        [Notes] nvarchar(2000) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_EvidenceRoomNumberingPolicies] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_EvidenceRoomNumberingPolicies_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [PhysicalFileContainers] (
        [Id] int NOT NULL IDENTITY,
        [EvidenceRoomId] int NOT NULL,
        [Kind] int NOT NULL,
        [Form] int NOT NULL,
        [Label] nvarchar(256) NOT NULL,
        [RangeCalendarYear] int NULL,
        [RangeFromSequence] int NULL,
        [RangeToSequence] int NULL,
        [DocumentNumberRangeFrom] nvarchar(24) NULL,
        [DocumentNumberRangeTo] nvarchar(24) NULL,
        [DispositionYear] int NULL,
        [DispositionMonth] int NULL,
        [Notes] nvarchar(2000) NULL,
        [IsActive] bit NOT NULL,
        [FiledVoucherCount] int NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_PhysicalFileContainers] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PhysicalFileContainers_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [StorageLocations] (
        [Id] int NOT NULL IDENTITY,
        [EvidenceRoomId] int NOT NULL,
        [Name] nvarchar(256) NOT NULL,
        [Kind] int NOT NULL,
        [ParentId] int NULL,
        [IsActive] bit NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_StorageLocations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_StorageLocations_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_StorageLocations_StorageLocations_ParentId] FOREIGN KEY ([ParentId]) REFERENCES [StorageLocations] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [TemporaryIdentifierCounters] (
        [Id] int NOT NULL IDENTITY,
        [EvidenceRoomId] int NOT NULL,
        [Date] date NOT NULL,
        [LastOrdinal] int NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_TemporaryIdentifierCounters] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_TemporaryIdentifierCounters_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [CustodianAppointments] (
        [Id] int NOT NULL IDENTITY,
        [EvidenceRoomId] int NOT NULL,
        [UserId] int NOT NULL,
        [AppointmentType] int NOT NULL,
        [EffectiveFrom] datetimeoffset NOT NULL,
        [EffectiveTo] datetimeoffset NULL,
        [AppointmentOrderReference] nvarchar(256) NOT NULL,
        [AppointingAuthority] nvarchar(256) NOT NULL,
        [PersonnelCategory] int NOT NULL,
        [EligibilityAttested] bit NOT NULL,
        [SupersedesAppointmentId] int NULL,
        [SupersededByAppointmentId] int NULL,
        [RecordedByUserId] int NOT NULL,
        [RecordedAtUtc] datetimeoffset NOT NULL,
        [Notes] nvarchar(2000) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_CustodianAppointments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CustodianAppointments_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CustodianAppointments_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [CustodyParties] (
        [Id] int NOT NULL IDENTITY,
        [Kind] int NOT NULL,
        [DisplayName] nvarchar(512) NOT NULL,
        [UserId] int NULL,
        [TitleOrGrade] nvarchar(128) NULL,
        [OrganizationOrAgency] nvarchar(256) NULL,
        [AccountableMailNumber] nvarchar(128) NULL,
        [IdentificationVerified] bit NOT NULL,
        CONSTRAINT [PK_CustodyParties] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CustodyParties_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [RoleAssignments] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [RoleId] int NOT NULL,
        [EvidenceRoomId] int NULL,
        [EffectiveFrom] datetimeoffset NOT NULL,
        [EffectiveTo] datetimeoffset NULL,
        [GrantedByUserId] int NOT NULL,
        [GrantedAtUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_RoleAssignments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RoleAssignments_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RoleAssignments_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RoleAssignments_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [EvidenceVouchers] (
        [Id] int NOT NULL IDENTITY,
        [CaseId] int NOT NULL,
        [EvidenceRoomId] int NOT NULL,
        [TemporaryIdentifier] nvarchar(32) NOT NULL,
        [PreparedByUserId] int NOT NULL,
        [ReceivingActivity] nvarchar(256) NOT NULL,
        [ReceivingActivityLocation] nvarchar(256) NOT NULL,
        [ReceivedFrom] nvarchar(512) NOT NULL,
        [RequestingOfficeCaseNumber] nvarchar(64) NULL,
        [IsRequestForAssistance] bit NOT NULL,
        [AcquiredAtUtc] datetimeoffset NOT NULL,
        [AcquiredAtLocal] datetimeoffset NOT NULL,
        [AcquiredAtOffset] time NOT NULL,
        [CreatedByUserId] int NOT NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [ReviewStage] int NOT NULL,
        [SubmittedAtUtc] datetimeoffset NULL,
        [SubmittedByUserId] int NULL,
        [Remarks] nvarchar(4000) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_EvidenceVouchers] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_EvidenceVouchers_Cases_CaseId] FOREIGN KEY ([CaseId]) REFERENCES [Cases] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_EvidenceVouchers_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [CustodianDutyAssumptions] (
        [Id] int NOT NULL IDENTITY,
        [EvidenceRoomId] int NOT NULL,
        [PrimaryAppointmentId] int NOT NULL,
        [AlternateAppointmentId] int NOT NULL,
        [AlternateUserId] int NOT NULL,
        [PrimaryAbsenceStart] datetimeoffset NOT NULL,
        [ExpectedAbsenceEnd] datetimeoffset NULL,
        [AlternateAssumedDutiesAt] datetimeoffset NOT NULL,
        [AssumptionLedgerAttestation] nvarchar(2000) NOT NULL,
        [PrimaryResumedAt] datetimeoffset NULL,
        [ResumptionLedgerAttestation] nvarchar(2000) NULL,
        [ReasonForAbsence] nvarchar(1000) NULL,
        [RecordedByUserId] int NOT NULL,
        [RecordedAtUtc] datetimeoffset NOT NULL,
        [ResumptionRecordedByUserId] int NULL,
        [ResumptionRecordedAtUtc] datetimeoffset NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_CustodianDutyAssumptions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CustodianDutyAssumptions_CustodianAppointments_AlternateAppointmentId] FOREIGN KEY ([AlternateAppointmentId]) REFERENCES [CustodianAppointments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CustodianDutyAssumptions_CustodianAppointments_PrimaryAppointmentId] FOREIGN KEY ([PrimaryAppointmentId]) REFERENCES [CustodianAppointments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CustodianDutyAssumptions_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [PrimaryCustodianTransitions] (
        [Id] int NOT NULL IDENTITY,
        [EvidenceRoomId] int NOT NULL,
        [IncomingPrimaryAppointmentId] int NOT NULL,
        [OutgoingPrimaryAppointmentId] int NULL,
        [Reason] int NOT NULL,
        [EffectiveFrom] datetimeoffset NOT NULL,
        [JointInventoryCompletedAt] datetimeoffset NULL,
        [JointInventoryReference] nvarchar(256) NULL,
        [DiscrepanciesResolved] bit NOT NULL,
        [LedgerAttestation] nvarchar(2000) NULL,
        [RecordedByUserId] int NOT NULL,
        [RecordedAtUtc] datetimeoffset NOT NULL,
        [Notes] nvarchar(2000) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_PrimaryCustodianTransitions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PrimaryCustodianTransitions_CustodianAppointments_IncomingPrimaryAppointmentId] FOREIGN KEY ([IncomingPrimaryAppointmentId]) REFERENCES [CustodianAppointments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PrimaryCustodianTransitions_CustodianAppointments_OutgoingPrimaryAppointmentId] FOREIGN KEY ([OutgoingPrimaryAppointmentId]) REFERENCES [CustodianAppointments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PrimaryCustodianTransitions_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [EvidenceItems] (
        [Id] int NOT NULL IDENTITY,
        [VoucherId] int NOT NULL,
        [ItemNumber] int NOT NULL,
        [Description] nvarchar(4000) NOT NULL,
        [Quantity] nvarchar(256) NULL,
        [SerialNumber] nvarchar(256) NULL,
        [UniqueDeviceIdentifier] nvarchar(256) NULL,
        [IsPossibleBiohazard] bit NOT NULL,
        [IsFungible] bit NOT NULL,
        [IsSealed] bit NOT NULL,
        [SealDescription] nvarchar(1000) NULL,
        [IsCurrency] bit NOT NULL,
        [CurrencyDenominationBreakdown] nvarchar(2000) NULL,
        [CurrencyTotalAmount] decimal(18,2) NULL,
        [AccountabilityStatus] int NOT NULL,
        [LastEventSequenceNumber] int NOT NULL,
        [LastEventHash] nvarchar(64) NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_EvidenceItems] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_EvidenceItems_EvidenceVouchers_VoucherId] FOREIGN KEY ([VoucherId]) REFERENCES [EvidenceVouchers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [OfficialDocumentNumberAssignments] (
        [Id] int NOT NULL IDENTITY,
        [VoucherId] int NOT NULL,
        [EvidenceRoomId] int NOT NULL,
        [DocumentNumber] nvarchar(24) NOT NULL,
        [Sequence] int NOT NULL,
        [NumberingPolicyId] int NULL,
        [CalendarYear] int NOT NULL,
        [EnteredByUserId] int NOT NULL,
        [EnteredAtUtc] datetimeoffset NOT NULL,
        [AttestedAssignedInAuthoritativeLedger] bit NOT NULL,
        [SupersedesAssignmentId] int NULL,
        [SupersessionReason] nvarchar(1000) NULL,
        CONSTRAINT [PK_OfficialDocumentNumberAssignments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OfficialDocumentNumberAssignments_EvidenceRoomNumberingPolicies_NumberingPolicyId] FOREIGN KEY ([NumberingPolicyId]) REFERENCES [EvidenceRoomNumberingPolicies] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OfficialDocumentNumberAssignments_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OfficialDocumentNumberAssignments_EvidenceVouchers_VoucherId] FOREIGN KEY ([VoucherId]) REFERENCES [EvidenceVouchers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OfficialDocumentNumberAssignments_OfficialDocumentNumberAssignments_SupersedesAssignmentId] FOREIGN KEY ([SupersedesAssignmentId]) REFERENCES [OfficialDocumentNumberAssignments] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [PhysicalVoucherDocuments] (
        [Id] int NOT NULL IDENTITY,
        [VoucherId] int NOT NULL,
        [EvidenceRoomId] int NOT NULL,
        [OriginalDisposition] int NOT NULL,
        [RetainedPaperStatus] int NOT NULL,
        [CurrentContainerId] int NULL,
        [HomeActiveContainerId] int NULL,
        [CopyReason] int NOT NULL,
        [SuspenseCopyFiledWithOriginal] bit NOT NULL,
        [InactiveSinceUtc] datetimeoffset NULL,
        [DestructionConfirmedAtUtc] datetimeoffset NULL,
        [DestructionConfirmedByUserId] int NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_PhysicalVoucherDocuments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PhysicalVoucherDocuments_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PhysicalVoucherDocuments_EvidenceVouchers_VoucherId] FOREIGN KEY ([VoucherId]) REFERENCES [EvidenceVouchers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PhysicalVoucherDocuments_PhysicalFileContainers_CurrentContainerId] FOREIGN KEY ([CurrentContainerId]) REFERENCES [PhysicalFileContainers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PhysicalVoucherDocuments_PhysicalFileContainers_HomeActiveContainerId] FOREIGN KEY ([HomeActiveContainerId]) REFERENCES [PhysicalFileContainers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [SourceDocuments] (
        [Id] int NOT NULL IDENTITY,
        [EvidenceRoomId] int NOT NULL,
        [CaseId] int NULL,
        [VoucherId] int NULL,
        [DocumentType] int NOT NULL,
        [Provenance] int NOT NULL,
        [OriginalFilename] nvarchar(260) NOT NULL,
        [ContentLength] bigint NOT NULL,
        [Sha256] nchar(64) NOT NULL,
        [StorageKey] nvarchar(128) NOT NULL,
        [ReceivedByUserId] int NOT NULL,
        [ReceivedAtUtc] datetimeoffset NOT NULL,
        [ClassificationMarking] nvarchar(128) NOT NULL,
        [ProvenanceNotes] nvarchar(2000) NULL,
        CONSTRAINT [PK_SourceDocuments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SourceDocuments_Cases_CaseId] FOREIGN KEY ([CaseId]) REFERENCES [Cases] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SourceDocuments_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SourceDocuments_EvidenceVouchers_VoucherId] FOREIGN KEY ([VoucherId]) REFERENCES [EvidenceVouchers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SourceDocuments_Users_ReceivedByUserId] FOREIGN KEY ([ReceivedByUserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [VoucherFormRevisions] (
        [Id] int NOT NULL IDENTITY,
        [VoucherId] int NOT NULL,
        [RevisionNumber] int NOT NULL,
        [Kind] int NOT NULL,
        [SubmittedByUserId] int NOT NULL,
        [SubmittedAtUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_VoucherFormRevisions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_VoucherFormRevisions_EvidenceVouchers_VoucherId] FOREIGN KEY ([VoucherId]) REFERENCES [EvidenceVouchers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [VoucherReviewActions] (
        [Id] int NOT NULL IDENTITY,
        [VoucherId] int NOT NULL,
        [Kind] int NOT NULL,
        [ResultingStage] int NOT NULL,
        [ActorUserId] int NOT NULL,
        [OccurredAtUtc] datetimeoffset NOT NULL,
        [Narrative] nvarchar(4000) NULL,
        [PaperFormCorrectedAndInitialedAttested] bit NULL,
        CONSTRAINT [PK_VoucherReviewActions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_VoucherReviewActions_EvidenceVouchers_VoucherId] FOREIGN KEY ([VoucherId]) REFERENCES [EvidenceVouchers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_VoucherReviewActions_Users_ActorUserId] FOREIGN KEY ([ActorUserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [ItemEvents] (
        [Id] int NOT NULL IDENTITY,
        [EvidenceItemId] int NOT NULL,
        [SequenceNumber] int NOT NULL,
        [OccurredAtUtc] datetimeoffset NOT NULL,
        [OccurredAtLocal] datetimeoffset NOT NULL,
        [OccurredAtOffset] time NOT NULL,
        [RecordedAtUtc] datetimeoffset NOT NULL,
        [RecordedByUserId] int NOT NULL,
        [Notes] nvarchar(4000) NULL,
        [SourceDocumentId] int NULL,
        [PreviousEventHash] nvarchar(64) NULL,
        [EventHash] nvarchar(64) NOT NULL,
        [HashSchemaVersion] int NOT NULL,
        [EventKind] nvarchar(21) NOT NULL,
        [CorrectsEventId] int NULL,
        [FieldName] nvarchar(128) NULL,
        [OriginalValue] nvarchar(4000) NULL,
        [CorrectedValue] nvarchar(4000) NULL,
        [PreviousEffectiveValue] nvarchar(4000) NULL,
        [PreviousEffectiveReferenceId] int NULL,
        [Reason] nvarchar(2000) NULL,
        [Category] int NULL,
        [MfrReference] nvarchar(256) NULL,
        [SupervisorNotifiedUserId] int NULL,
        [SupervisorNotifiedName] nvarchar(256) NULL,
        [SupervisorNotifiedGradeOrTitle] nvarchar(64) NULL,
        [SupervisorNotifiedOrganization] nvarchar(256) NULL,
        [SupervisorNotifiedAtUtc] datetimeoffset NULL,
        [ReferenceKind] int NULL,
        [OriginalReferenceId] int NULL,
        [CorrectedReferenceId] int NULL,
        [ReleasedByPartyId] int NULL,
        [ReceivedByPartyId] int NULL,
        [PurposeOfChangeOfCustody] nvarchar(1000) NULL,
        [IsScrcni] bit NULL,
        [Destination] nvarchar(512) NULL,
        [Agency] nvarchar(256) NULL,
        [DocumentNumber] nvarchar(16) NULL,
        [PreviousDocumentNumber] nvarchar(16) NULL,
        [AttestedAssignedInAuthoritativeLedger] bit NULL,
        [Laboratory] nvarchar(256) NULL,
        [ExaminationRequestReference] nvarchar(256) NULL,
        [ExhibitNumber] nvarchar(128) NULL,
        [ExtractionDescription] nvarchar(2000) NULL,
        [ResultReference] nvarchar(256) NULL,
        [StorageLocationId] int NULL,
        [StorageLocationPath] nvarchar(1000) NULL,
        [LocationEvent_Reason] nvarchar(1000) NULL,
        [Action] int NULL,
        [PerformedByName] nvarchar(256) NULL,
        [PurposeOfBreach] nvarchar(1000) NULL,
        [SealEvent_MfrReference] nvarchar(256) NULL,
        [DirectingSupervisorName] nvarchar(256) NULL,
        [FromStatus] int NULL,
        [ToStatus] int NULL,
        [StatusEvent_Reason] nvarchar(1000) NULL,
        CONSTRAINT [PK_ItemEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ItemEvents_CustodyParties_ReceivedByPartyId] FOREIGN KEY ([ReceivedByPartyId]) REFERENCES [CustodyParties] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ItemEvents_CustodyParties_ReleasedByPartyId] FOREIGN KEY ([ReleasedByPartyId]) REFERENCES [CustodyParties] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ItemEvents_EvidenceItems_EvidenceItemId] FOREIGN KEY ([EvidenceItemId]) REFERENCES [EvidenceItems] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ItemEvents_ItemEvents_CorrectsEventId] FOREIGN KEY ([CorrectsEventId]) REFERENCES [ItemEvents] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [PhysicalVoucherDocumentEvents] (
        [Id] int NOT NULL IDENTITY,
        [DocumentId] int NOT NULL,
        [Kind] int NOT NULL,
        [ResultingOriginalDisposition] int NOT NULL,
        [ResultingRetainedPaperStatus] int NOT NULL,
        [RecordedByUserId] int NOT NULL,
        [OccurredAtUtc] datetimeoffset NOT NULL,
        [ContainerId] int NULL,
        [Narrative] nvarchar(2000) NULL,
        CONSTRAINT [PK_PhysicalVoucherDocumentEvents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PhysicalVoucherDocumentEvents_PhysicalVoucherDocuments_DocumentId] FOREIGN KEY ([DocumentId]) REFERENCES [PhysicalVoucherDocuments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PhysicalVoucherDocumentEvents_Users_RecordedByUserId] FOREIGN KEY ([RecordedByUserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [DocumentRenderJobs] (
        [Id] int NOT NULL IDENTITY,
        [SourceDocumentId] int NOT NULL,
        [EvidenceRoomId] int NOT NULL,
        [RequestedByUserId] int NOT NULL,
        [RequestedAtUtc] datetimeoffset NOT NULL,
        [Status] int NOT NULL,
        [Attempts] int NOT NULL,
        [LeasedByWorkerId] nvarchar(128) NULL,
        [LeaseExpiresUtc] datetimeoffset NULL,
        [FinishedAtUtc] datetimeoffset NULL,
        [LastFailureCategory] int NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_DocumentRenderJobs] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DocumentRenderJobs_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_DocumentRenderJobs_SourceDocuments_SourceDocumentId] FOREIGN KEY ([SourceDocumentId]) REFERENCES [SourceDocuments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_DocumentRenderJobs_Users_RequestedByUserId] FOREIGN KEY ([RequestedByUserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [VoucherFormRevisionLines] (
        [Id] int NOT NULL IDENTITY,
        [RevisionId] int NOT NULL,
        [EvidenceItemId] int NOT NULL,
        [LineNumber] int NOT NULL,
        [Description] nvarchar(4000) NOT NULL,
        [Quantity] nvarchar(256) NULL,
        [SerialNumber] nvarchar(256) NULL,
        [UniqueDeviceIdentifier] nvarchar(256) NULL,
        [IsPossibleBiohazard] bit NOT NULL,
        [IsSealed] bit NOT NULL,
        CONSTRAINT [PK_VoucherFormRevisionLines] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_VoucherFormRevisionLines_EvidenceItems_EvidenceItemId] FOREIGN KEY ([EvidenceItemId]) REFERENCES [EvidenceItems] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_VoucherFormRevisionLines_VoucherFormRevisions_RevisionId] FOREIGN KEY ([RevisionId]) REFERENCES [VoucherFormRevisions] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [DocumentRenderRuns] (
        [Id] int NOT NULL IDENTITY,
        [RenderJobId] int NOT NULL,
        [SourceDocumentId] int NOT NULL,
        [WorkerId] nvarchar(128) NOT NULL,
        [RendererVersion] nvarchar(256) NOT NULL,
        [StartedAtUtc] datetimeoffset NOT NULL,
        [CompletedAtUtc] datetimeoffset NOT NULL,
        [Outcome] int NOT NULL,
        [FailureCategory] int NOT NULL,
        [PageCount] int NULL,
        [RenderDpi] int NOT NULL,
        CONSTRAINT [PK_DocumentRenderRuns] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DocumentRenderRuns_DocumentRenderJobs_RenderJobId] FOREIGN KEY ([RenderJobId]) REFERENCES [DocumentRenderJobs] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_DocumentRenderRuns_SourceDocuments_SourceDocumentId] FOREIGN KEY ([SourceDocumentId]) REFERENCES [SourceDocuments] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [DocumentRenderPages] (
        [Id] int NOT NULL IDENTITY,
        [RenderRunId] int NOT NULL,
        [PageNumber] int NOT NULL,
        [WidthPx] int NOT NULL,
        [HeightPx] int NOT NULL,
        [StorageKey] nvarchar(128) NOT NULL,
        [Sha256] nchar(64) NOT NULL,
        [ContentLength] bigint NOT NULL,
        CONSTRAINT [PK_DocumentRenderPages] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DocumentRenderPages_DocumentRenderRuns_RenderRunId] FOREIGN KEY ([RenderRunId]) REFERENCES [DocumentRenderRuns] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [OcrJobs] (
        [Id] int NOT NULL IDENTITY,
        [SourceDocumentId] int NOT NULL,
        [RenderRunId] int NOT NULL,
        [EvidenceRoomId] int NOT NULL,
        [RequestedByUserId] int NOT NULL,
        [RequestedAtUtc] datetimeoffset NOT NULL,
        [Status] int NOT NULL,
        [Attempts] int NOT NULL,
        [LeasedByWorkerId] nvarchar(128) NULL,
        [LeaseExpiresUtc] datetimeoffset NULL,
        [FinishedAtUtc] datetimeoffset NULL,
        [LastFailureCategory] int NOT NULL,
        [ConcurrencyStamp] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_OcrJobs] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OcrJobs_DocumentRenderRuns_RenderRunId] FOREIGN KEY ([RenderRunId]) REFERENCES [DocumentRenderRuns] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OcrJobs_EvidenceRooms_EvidenceRoomId] FOREIGN KEY ([EvidenceRoomId]) REFERENCES [EvidenceRooms] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OcrJobs_SourceDocuments_SourceDocumentId] FOREIGN KEY ([SourceDocumentId]) REFERENCES [SourceDocuments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OcrJobs_Users_RequestedByUserId] FOREIGN KEY ([RequestedByUserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [OcrRuns] (
        [Id] int NOT NULL IDENTITY,
        [OcrJobId] int NOT NULL,
        [SourceDocumentId] int NOT NULL,
        [RenderRunId] int NOT NULL,
        [WorkerId] nvarchar(128) NOT NULL,
        [EngineName] nvarchar(64) NOT NULL,
        [EngineVersion] nvarchar(128) NOT NULL,
        [ModelIdentifiers] nvarchar(1024) NOT NULL,
        [PreprocessingVersion] nvarchar(256) NOT NULL,
        [TemplateId] nvarchar(64) NULL,
        [TemplateIdentified] bit NOT NULL,
        [StartedAtUtc] datetimeoffset NOT NULL,
        [CompletedAtUtc] datetimeoffset NOT NULL,
        [Outcome] int NOT NULL,
        [FailureCategory] int NOT NULL,
        [PagesProcessed] int NOT NULL,
        CONSTRAINT [PK_OcrRuns] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OcrRuns_DocumentRenderRuns_RenderRunId] FOREIGN KEY ([RenderRunId]) REFERENCES [DocumentRenderRuns] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OcrRuns_OcrJobs_OcrJobId] FOREIGN KEY ([OcrJobId]) REFERENCES [OcrJobs] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_OcrRuns_SourceDocuments_SourceDocumentId] FOREIGN KEY ([SourceDocumentId]) REFERENCES [SourceDocuments] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [ExtractedFields] (
        [Id] int NOT NULL IDENTITY,
        [OcrRunId] int NOT NULL,
        [FieldKey] nvarchar(128) NOT NULL,
        [PageNumber] int NOT NULL,
        [RawText] nvarchar(4000) NOT NULL,
        [NormalizedCandidate] nvarchar(4000) NULL,
        [Confidence] decimal(5,2) NOT NULL,
        [Band] int NOT NULL,
        [Left] int NOT NULL,
        [Top] int NOT NULL,
        [Width] int NOT NULL,
        [Height] int NOT NULL,
        [IsHighConsequence] bit NOT NULL,
        [RequiresVerification] bit NOT NULL,
        CONSTRAINT [PK_ExtractedFields] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ExtractedFields_OcrRuns_OcrRunId] FOREIGN KEY ([OcrRunId]) REFERENCES [OcrRuns] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [OcrRunPages] (
        [Id] int NOT NULL IDENTITY,
        [OcrRunId] int NOT NULL,
        [PageNumber] int NOT NULL,
        [StorageKey] nvarchar(128) NOT NULL,
        [Sha256] nchar(64) NOT NULL,
        [WidthPx] int NOT NULL,
        [HeightPx] int NOT NULL,
        [RotationAppliedDegrees] int NOT NULL,
        [DeskewAppliedDegrees] float NOT NULL,
        [Dpi] int NOT NULL,
        CONSTRAINT [PK_OcrRunPages] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_OcrRunPages_OcrRuns_OcrRunId] FOREIGN KEY ([OcrRunId]) REFERENCES [OcrRuns] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [ReconciliationFindings] (
        [Id] int NOT NULL IDENTITY,
        [OcrRunId] int NOT NULL,
        [SourceDocumentId] int NOT NULL,
        [VoucherId] int NOT NULL,
        [EvidenceItemId] int NULL,
        [Kind] int NOT NULL,
        [FieldKey] nvarchar(128) NOT NULL,
        [CompanionValue] nvarchar(4000) NULL,
        [DocumentValue] nvarchar(4000) NULL,
        [Decision] int NOT NULL,
        [Narrative] nvarchar(4000) NULL,
        [DecidedByUserId] int NOT NULL,
        [DecidedAtUtc] datetimeoffset NOT NULL,
        CONSTRAINT [PK_ReconciliationFindings] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ReconciliationFindings_EvidenceItems_EvidenceItemId] FOREIGN KEY ([EvidenceItemId]) REFERENCES [EvidenceItems] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ReconciliationFindings_EvidenceVouchers_VoucherId] FOREIGN KEY ([VoucherId]) REFERENCES [EvidenceVouchers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ReconciliationFindings_OcrRuns_OcrRunId] FOREIGN KEY ([OcrRunId]) REFERENCES [OcrRuns] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ReconciliationFindings_SourceDocuments_SourceDocumentId] FOREIGN KEY ([SourceDocumentId]) REFERENCES [SourceDocuments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ReconciliationFindings_Users_DecidedByUserId] FOREIGN KEY ([DecidedByUserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE TABLE [FieldVerifications] (
        [Id] int NOT NULL IDENTITY,
        [ExtractedFieldId] int NOT NULL,
        [VerifiedByUserId] int NOT NULL,
        [VerifiedAtUtc] datetimeoffset NOT NULL,
        [Decision] int NOT NULL,
        [EnteredValue] nvarchar(4000) NULL,
        [Note] nvarchar(2000) NULL,
        CONSTRAINT [PK_FieldVerifications] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_FieldVerifications_ExtractedFields_ExtractedFieldId] FOREIGN KEY ([ExtractedFieldId]) REFERENCES [ExtractedFields] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_FieldVerifications_Users_VerifiedByUserId] FOREIGN KEY ([VerifiedByUserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_ActingUserId_OccurredAtUtc] ON [AuditEvents] ([ActingUserId], [OccurredAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_AffectedRecordType_AffectedRecordId] ON [AuditEvents] ([AffectedRecordType], [AffectedRecordId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_OccurredAtUtc] ON [AuditEvents] ([OccurredAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Cases_EvidenceRoomId_CaseControlNumber] ON [Cases] ([EvidenceRoomId], [CaseControlNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_CustodianAppointments_EvidenceRoomId_UserId_EffectiveFrom] ON [CustodianAppointments] ([EvidenceRoomId], [UserId], [EffectiveFrom]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_CustodianAppointments_UserId] ON [CustodianAppointments] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_CustodianAppointments_OneOpenPerType] ON [CustodianAppointments] ([EvidenceRoomId], [AppointmentType]) WHERE EffectiveTo IS NULL AND SupersededByAppointmentId IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_CustodianDutyAssumptions_AlternateAppointmentId] ON [CustodianDutyAssumptions] ([AlternateAppointmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_CustodianDutyAssumptions_AlternateUserId_EvidenceRoomId] ON [CustodianDutyAssumptions] ([AlternateUserId], [EvidenceRoomId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_CustodianDutyAssumptions_PrimaryAppointmentId] ON [CustodianDutyAssumptions] ([PrimaryAppointmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_CustodianDutyAssumptions_OneOpenPerRoom] ON [CustodianDutyAssumptions] ([EvidenceRoomId]) WHERE PrimaryResumedAt IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_CustodyParties_UserId] ON [CustodyParties] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_DocumentRenderJobs_EvidenceRoomId] ON [DocumentRenderJobs] ([EvidenceRoomId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_DocumentRenderJobs_RequestedByUserId] ON [DocumentRenderJobs] ([RequestedByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_DocumentRenderJobs_Status_RequestedAtUtc] ON [DocumentRenderJobs] ([Status], [RequestedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_DocumentRenderJobs_OneOpenPerDocument] ON [DocumentRenderJobs] ([SourceDocumentId]) WHERE Status IN (1, 2)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DocumentRenderPages_RenderRunId_PageNumber] ON [DocumentRenderPages] ([RenderRunId], [PageNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DocumentRenderPages_StorageKey] ON [DocumentRenderPages] ([StorageKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_DocumentRenderRuns_RenderJobId] ON [DocumentRenderRuns] ([RenderJobId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_DocumentRenderRuns_SourceDocumentId_Outcome_CompletedAtUtc] ON [DocumentRenderRuns] ([SourceDocumentId], [Outcome], [CompletedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_EvidenceItems_SerialNumber] ON [EvidenceItems] ([SerialNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EvidenceItems_VoucherId_ItemNumber] ON [EvidenceItems] ([VoucherId], [ItemNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_EvidenceRoomNumberingPolicies_EvidenceRoomId_EffectiveFrom] ON [EvidenceRoomNumberingPolicies] ([EvidenceRoomId], [EffectiveFrom]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EvidenceRooms_Name] ON [EvidenceRooms] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_EvidenceVouchers_CaseId] ON [EvidenceVouchers] ([CaseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EvidenceVouchers_EvidenceRoomId_TemporaryIdentifier] ON [EvidenceVouchers] ([EvidenceRoomId], [TemporaryIdentifier]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_ExtractedFields_OcrRunId_PageNumber] ON [ExtractedFields] ([OcrRunId], [PageNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_FieldVerifications_ExtractedFieldId_VerifiedAtUtc] ON [FieldVerifications] ([ExtractedFieldId], [VerifiedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_FieldVerifications_VerifiedByUserId] ON [FieldVerifications] ([VerifiedByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_ItemEvents_CorrectionReference] ON [ItemEvents] ([ReferenceKind], [CorrectedReferenceId]) WHERE [CorrectedReferenceId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_ItemEvents_CorrectsEventId] ON [ItemEvents] ([CorrectsEventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_ItemEvents_ItemChronology] ON [ItemEvents] ([EvidenceItemId], [OccurredAtUtc], [SequenceNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_ItemEvents_ReceivedByPartyId] ON [ItemEvents] ([ReceivedByPartyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_ItemEvents_ReleasedByPartyId] ON [ItemEvents] ([ReleasedByPartyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [UX_ItemEvents_ItemSequence] ON [ItemEvents] ([EvidenceItemId], [SequenceNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_OcrJobs_EvidenceRoomId] ON [OcrJobs] ([EvidenceRoomId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_OcrJobs_RenderRunId] ON [OcrJobs] ([RenderRunId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_OcrJobs_RequestedByUserId] ON [OcrJobs] ([RequestedByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_OcrJobs_Status_RequestedAtUtc] ON [OcrJobs] ([Status], [RequestedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_OcrJobs_OneOpenPerDocument] ON [OcrJobs] ([SourceDocumentId]) WHERE Status IN (1, 2)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_OcrRunPages_OcrRunId_PageNumber] ON [OcrRunPages] ([OcrRunId], [PageNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_OcrRunPages_StorageKey] ON [OcrRunPages] ([StorageKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_OcrRuns_OcrJobId] ON [OcrRuns] ([OcrJobId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_OcrRuns_RenderRunId] ON [OcrRuns] ([RenderRunId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_OcrRuns_SourceDocumentId_CompletedAtUtc] ON [OcrRuns] ([SourceDocumentId], [CompletedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_OfficialDocumentNumberAssignments_NumberingPolicyId] ON [OfficialDocumentNumberAssignments] ([NumberingPolicyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_OfficialDocumentNumberAssignments_SupersedesAssignmentId] ON [OfficialDocumentNumberAssignments] ([SupersedesAssignmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_OfficialDocumentNumberAssignments_VoucherId] ON [OfficialDocumentNumberAssignments] ([VoucherId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [UX_DocumentNumber_NeverReusedPerRoomPerYear] ON [OfficialDocumentNumberAssignments] ([EvidenceRoomId], [CalendarYear], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PhysicalFileContainers_EvidenceRoomId_Kind_Label] ON [PhysicalFileContainers] ([EvidenceRoomId], [Kind], [Label]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_PhysicalFileContainers_EvidenceRoomId_RangeCalendarYear_RangeFromSequence] ON [PhysicalFileContainers] ([EvidenceRoomId], [RangeCalendarYear], [RangeFromSequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_PhysicalVoucherDocumentEvents_DocumentId_OccurredAtUtc] ON [PhysicalVoucherDocumentEvents] ([DocumentId], [OccurredAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_PhysicalVoucherDocumentEvents_RecordedByUserId] ON [PhysicalVoucherDocumentEvents] ([RecordedByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_PhysicalVoucherDocuments_CurrentContainerId] ON [PhysicalVoucherDocuments] ([CurrentContainerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_PhysicalVoucherDocuments_EvidenceRoomId] ON [PhysicalVoucherDocuments] ([EvidenceRoomId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_PhysicalVoucherDocuments_HomeActiveContainerId] ON [PhysicalVoucherDocuments] ([HomeActiveContainerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PhysicalVoucherDocuments_VoucherId] ON [PhysicalVoucherDocuments] ([VoucherId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_PrimaryCustodianTransitions_EvidenceRoomId_EffectiveFrom] ON [PrimaryCustodianTransitions] ([EvidenceRoomId], [EffectiveFrom]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_PrimaryCustodianTransitions_IncomingPrimaryAppointmentId] ON [PrimaryCustodianTransitions] ([IncomingPrimaryAppointmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_PrimaryCustodianTransitions_OutgoingPrimaryAppointmentId] ON [PrimaryCustodianTransitions] ([OutgoingPrimaryAppointmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_ReconciliationFindings_DecidedByUserId] ON [ReconciliationFindings] ([DecidedByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_ReconciliationFindings_EvidenceItemId] ON [ReconciliationFindings] ([EvidenceItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_ReconciliationFindings_OcrRunId] ON [ReconciliationFindings] ([OcrRunId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_ReconciliationFindings_SourceDocumentId_FieldKey] ON [ReconciliationFindings] ([SourceDocumentId], [FieldKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_ReconciliationFindings_VoucherId] ON [ReconciliationFindings] ([VoucherId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_RoleAssignments_EvidenceRoomId] ON [RoleAssignments] ([EvidenceRoomId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_RoleAssignments_RoleId] ON [RoleAssignments] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_RoleAssignments_UserId_EvidenceRoomId] ON [RoleAssignments] ([UserId], [EvidenceRoomId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_RoleAssignments_OneOpenPerUserRoleRoom] ON [RoleAssignments] ([UserId], [RoleId], [EvidenceRoomId]) WHERE EffectiveTo IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Roles_Name] ON [Roles] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_SourceDocuments_CaseId] ON [SourceDocuments] ([CaseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_SourceDocuments_EvidenceRoomId_Sha256] ON [SourceDocuments] ([EvidenceRoomId], [Sha256]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_SourceDocuments_ReceivedByUserId] ON [SourceDocuments] ([ReceivedByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SourceDocuments_StorageKey] ON [SourceDocuments] ([StorageKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_SourceDocuments_VoucherId] ON [SourceDocuments] ([VoucherId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_StorageLocations_EvidenceRoomId_ParentId_Name] ON [StorageLocations] ([EvidenceRoomId], [ParentId], [Name]) WHERE [ParentId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_StorageLocations_ParentId] ON [StorageLocations] ([ParentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_TemporaryIdentifierCounters_EvidenceRoomId_Date] ON [TemporaryIdentifierCounters] ([EvidenceRoomId], [Date]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_ActiveDirectorySid] ON [Users] ([ActiveDirectorySid]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_UserPrincipalName] ON [Users] ([UserPrincipalName]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_VoucherFormRevisionLines_EvidenceItemId] ON [VoucherFormRevisionLines] ([EvidenceItemId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_VoucherFormRevisionLines_RevisionId_LineNumber] ON [VoucherFormRevisionLines] ([RevisionId], [LineNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE UNIQUE INDEX [IX_VoucherFormRevisions_VoucherId_RevisionNumber] ON [VoucherFormRevisions] ([VoucherId], [RevisionNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_VoucherReviewActions_ActorUserId] ON [VoucherReviewActions] ([ActorUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    CREATE INDEX [IX_VoucherReviewActions_VoucherId_OccurredAtUtc] ON [VoucherReviewActions] ([VoucherId], [OccurredAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014809_InitialEvidenceModel'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260904014809_InitialEvidenceModel', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_ItemEvents_AppendOnly_Update
    ON dbo.ItemEvents
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50001,
            'ItemEvents is append-only and cannot be modified. AR 195-5 para 2-5b(5) requires an erroneous entry to remain readable - it is voided with a single line and initialed, never erased. Record a correction instead.',
            1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_ItemEvents_AppendOnly_Delete
    ON dbo.ItemEvents
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50002,
            'ItemEvents is append-only and cannot be deleted. AR 195-5 para 2-5b(5) prohibits erasing an entry; para 1-7c(3) requires the error and the corrective action to be documented. Record a correction instead.',
            1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_AuditEvents_AppendOnly_Update
    ON dbo.AuditEvents
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50003, 'AuditEvents is append-only and cannot be modified.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_AuditEvents_AppendOnly_Delete
    ON dbo.AuditEvents
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50004, 'AuditEvents is append-only and cannot be deleted.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_DocumentNumbers_AppendOnly_Update
    ON dbo.OfficialDocumentNumberAssignments
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50005,
            'OfficialDocumentNumberAssignments is append-only and cannot be modified. AR 195-5 para 2-7g supersedes a prior document number with a new assignment and keeps the prior one legible; it does not overwrite it.',
            1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_DocumentNumbers_AppendOnly_Delete
    ON dbo.OfficialDocumentNumberAssignments
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50006,
            'OfficialDocumentNumberAssignments is append-only and cannot be deleted.',
            1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_VoucherReviewActions_AppendOnly_Update
    ON dbo.VoucherReviewActions
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50007,
            'VoucherReviewActions is append-only and cannot be modified. The record of a custodian review under AR 195-5 para 2-3g is kept as it happened.',
            1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_VoucherReviewActions_AppendOnly_Delete
    ON dbo.VoucherReviewActions
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50008, 'VoucherReviewActions is append-only and cannot be deleted.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_VoucherFormRevisions_AppendOnly_Update
    ON dbo.VoucherFormRevisions
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50009, 'VoucherFormRevisions is append-only and cannot be modified. A submitted DA Form 4137 revision is kept as it was submitted (AR 195-5 para 2-3g).', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_VoucherFormRevisions_AppendOnly_Delete
    ON dbo.VoucherFormRevisions
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50010, 'VoucherFormRevisions is append-only and cannot be deleted.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_VoucherFormRevisionLines_AppendOnly_Update
    ON dbo.VoucherFormRevisionLines
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50011, 'VoucherFormRevisionLines is append-only and cannot be modified.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_VoucherFormRevisionLines_AppendOnly_Delete
    ON dbo.VoucherFormRevisionLines
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50012, 'VoucherFormRevisionLines is append-only and cannot be deleted.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_PhysicalVoucherDocumentEvents_AppendOnly_Update
    ON dbo.PhysicalVoucherDocumentEvents
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50013, 'PhysicalVoucherDocumentEvents is append-only and cannot be modified.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_PhysicalVoucherDocumentEvents_AppendOnly_Delete
    ON dbo.PhysicalVoucherDocumentEvents
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50014, 'PhysicalVoucherDocumentEvents is append-only and cannot be deleted.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_SourceDocuments_AppendOnly_Update
    ON dbo.SourceDocuments
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50015, 'SourceDocuments is append-only and cannot be modified. A source document is an immutable companion copy; its recorded hash is what receipt recorded.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_SourceDocuments_AppendOnly_Delete
    ON dbo.SourceDocuments
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50016, 'SourceDocuments is append-only and cannot be deleted. Digital retention is undetermined (DEC-07); nothing is destroyed.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_DocumentRenderRuns_AppendOnly_Update
    ON dbo.DocumentRenderRuns
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50017, 'DocumentRenderRuns is append-only and cannot be modified. A render attempt is a fact; a new attempt is a new row (DOC-015).', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_DocumentRenderRuns_AppendOnly_Delete
    ON dbo.DocumentRenderRuns
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50018, 'DocumentRenderRuns is append-only and cannot be deleted.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_DocumentRenderPages_AppendOnly_Update
    ON dbo.DocumentRenderPages
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50029, 'DocumentRenderPages is append-only and cannot be modified.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_DocumentRenderPages_AppendOnly_Delete
    ON dbo.DocumentRenderPages
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50030, 'DocumentRenderPages is append-only and cannot be deleted.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_OcrRuns_AppendOnly_Update
    ON dbo.OcrRuns
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50019, 'OcrRuns is append-only and cannot be modified: a run is a fact about what an engine read; re-run instead.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_OcrRuns_AppendOnly_Delete
    ON dbo.OcrRuns
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50020, 'OcrRuns is append-only and cannot be deleted: a run is a fact about what an engine read; re-run instead.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_ExtractedFields_AppendOnly_Update
    ON dbo.ExtractedFields
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50021, 'ExtractedFields is append-only and cannot be modified: the raw extraction is never edited (OCR-004); record a verification.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_ExtractedFields_AppendOnly_Delete
    ON dbo.ExtractedFields
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50022, 'ExtractedFields is append-only and cannot be deleted: the raw extraction is never edited (OCR-004); record a verification.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_FieldVerifications_AppendOnly_Update
    ON dbo.FieldVerifications
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50023, 'FieldVerifications is append-only and cannot be modified: a second look is a second row.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_FieldVerifications_AppendOnly_Delete
    ON dbo.FieldVerifications
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50024, 'FieldVerifications is append-only and cannot be deleted: a second look is a second row.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_OcrRunPages_AppendOnly_Update
    ON dbo.OcrRunPages
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50025, 'OcrRunPages is append-only and cannot be modified: it is the image the engine read.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_OcrRunPages_AppendOnly_Delete
    ON dbo.OcrRunPages
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50026, 'OcrRunPages is append-only and cannot be deleted: it is the image the engine read.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_ReconciliationFindings_AppendOnly_Update
    ON dbo.ReconciliationFindings
    INSTEAD OF UPDATE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50027, 'ReconciliationFindings is append-only and cannot be modified: a later decision is a later row.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    CREATE OR ALTER TRIGGER TR_ReconciliationFindings_AppendOnly_Delete
    ON dbo.ReconciliationFindings
    INSTEAD OF DELETE
    AS
    BEGIN
        SET NOCOUNT ON;
        THROW 50028, 'ReconciliationFindings is append-only and cannot be deleted.', 1;
    END;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904014816_AppendOnlyTriggers'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260904014816_AppendOnlyTriggers', N'10.0.11');
END;

COMMIT;
GO

