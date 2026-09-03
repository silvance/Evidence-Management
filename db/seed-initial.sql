/*
    EMC initial seed.

    Run ONCE against a freshly migrated database, after reviewing and editing the values marked
    with <-- EDIT. Administration screens for users, roles, storage locations and system
    configuration are designed but not built in the first vertical slice, so this script is the
    supported way to bring a new installation up.

    Every value here is deployment configuration, not evidence data. Nothing in this script
    creates, alters or deletes accountability records.
*/

SET XACT_ABORT ON;
BEGIN TRANSACTION;

/* ---------------------------------------------------------------------------------------------
   1. System configuration.

   AuthoritativeMode 0 = Companion, NumberingMode 0 = ManualTranscription.

   DO NOT change these to 1 without a recorded Army G-2X approval reference. AR 195-5 para 2-5c
   requires prior approval before a CI organization uses a stand-alone automated evidence
   ledger/accountability system.

   AccreditedClassificationLevel is shown in the banner on every page. See open decision DEC-06
   in docs/open-policy-decisions.md: this must be settled with the organization's security
   manager BEFORE the system holds real data.
--------------------------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.SystemConfigurations)
BEGIN
    INSERT INTO dbo.SystemConfigurations
        (OrganizationName, AuthoritativeMode, NumberingMode,
         AutomatedSystemApprovalReference, AutomatedSystemApprovalDate,
         AccreditedClassificationLevel, LocalSuspenseReviewThresholdDays, ConcurrencyStamp)
    VALUES
        (N'902d MI Group',              -- <-- EDIT: your organization
         0, 0, NULL, NULL,
         N'UNCLASSIFIED',               -- <-- EDIT: the accredited level (DEC-06)

         /* A LOCAL management threshold, not a regulatory deadline. AR 195-5 gives no numeric
            limit for any temporary-release category: para 2-7a requires "reasonable and adequate
            contact" and paras 2-7b / 3-1a(4) require that release not be for "an excessive
            period" (SUSP-004). */
         60,
         NEWID());
END;

/* ---------------------------------------------------------------------------------------------
   2. Roles. Names must match Emc.Domain.Identity.EmcRoles exactly.
--------------------------------------------------------------------------------------------- */
MERGE dbo.Roles AS target
USING (VALUES
    (N'Agent',                          N'Prepares DA Forms 4137 for evidence they acquired (AR 195-5 2-3b).'),
    (N'PrimaryEvidenceCustodian',       N'Appointed primary evidence custodian (AR 195-5 1-4g(1), 1-4h).'),
    (N'AlternateEvidenceCustodian',     N'Appointed alternate evidence custodian (AR 195-5 1-4i).'),
    (N'CommanderOrSac',                 N'Commander or SAC: inspections, attestations, approvals (AR 195-5 1-4g(3), 3-1b(2)).'),
    (N'InspectorOrInventoryParticipant',N'Assigned inspection or inventory participant (AR 195-5 3-1, 3-2).'),
    (N'ApplicationAdministrator',       N'Administers the application. Holds NO evidence-accountability authority.')
) AS source (Name, Description)
ON target.Name = source.Name
WHEN NOT MATCHED THEN
    INSERT (Name, Description) VALUES (source.Name, source.Description);

/* ---------------------------------------------------------------------------------------------
   3. Evidence room.

   The document-number series, custodian appointments, inventories and access scoping are all per
   evidence room (AR 195-5 2-4c, 2-7g, 1-4g(1), 3-1). See open decision DEC-03.
--------------------------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.EvidenceRooms)
BEGIN
    INSERT INTO dbo.EvidenceRooms (Name, OrganizationOrUnit, TimeZoneId, IsActive, ConcurrencyStamp)
    VALUES (N'902d MI Group Evidence Room',   -- <-- EDIT
            N'902d MI Group',                 -- <-- EDIT

            /* The DA Form 4137 and the evidence ledger record LOCAL time (para 2-5b,
               "03 SEP 26 09:15"), so the room's zone drives display. */
            N'Eastern Standard Time',         -- <-- EDIT
            1, NEWID());
END;

/* ---------------------------------------------------------------------------------------------
   4. Users.

   EMC stores NO passwords. Authentication is Windows Authentication, and the Active Directory
   object SID is the key (IAM-003). Obtain it with, for example:

       PowerShell:  (Get-ADUser -Identity <samaccountname>).SID.Value
       or:          whoami /user

   Add one row per person who needs access.
--------------------------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.Users)
BEGIN
    INSERT INTO dbo.Users
        (ActiveDirectorySid, UserPrincipalName, DisplayName, RankOrGrade, OrganizationOrUnit,
         IsActive, ConcurrencyStamp)
    VALUES
        (N'S-1-5-21-0000000000-0000000000-0000000000-0000',  -- <-- EDIT: real AD object SID
         N'alice.baker@army.mil',                            -- <-- EDIT
         N'BAKER, ALICE C.',                                 -- <-- EDIT
         N'SA',                                              -- <-- EDIT
         N'902d MI Group',                                   -- <-- EDIT
         1, NEWID());
END;

/* ---------------------------------------------------------------------------------------------
   5. Role assignments.

   OPERATIONAL ROLES ARE SCOPED TO AN EVIDENCE ROOM (IAM-016). A grant in one room confers
   nothing in another, and holding no grant in a room means the user cannot even READ its records
   (IAM-017). Only ApplicationAdministrator may be granted globally, because it carries no
   authority over evidence at all.

   GrantedByUserId records who made the grant. A grant where GrantedByUserId equals UserId is a
   self-grant, which the application flags (IAM-010). For this bootstrap row that is unavoidable
   and expected; subsequent grants should be made by an identified administrator.
--------------------------------------------------------------------------------------------- */
DECLARE @UserId INT = (SELECT TOP 1 Id FROM dbo.Users ORDER BY Id);
DECLARE @RoomId INT = (SELECT TOP 1 Id FROM dbo.EvidenceRooms ORDER BY Id);

INSERT INTO dbo.RoleAssignments
    (UserId, RoleId, EvidenceRoomId, EffectiveFrom, EffectiveTo, GrantedByUserId, GrantedAtUtc)
SELECT @UserId, r.Id, @RoomId, SYSDATETIMEOFFSET(), NULL, @UserId, SYSUTCDATETIME()
FROM dbo.Roles r
WHERE r.Name IN (N'Agent', N'PrimaryEvidenceCustodian')  -- <-- EDIT: roles for this person
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RoleAssignments ra
      WHERE ra.UserId = @UserId AND ra.RoleId = r.Id AND ra.EvidenceRoomId = @RoomId
        AND ra.EffectiveTo IS NULL);

/* ---------------------------------------------------------------------------------------------
   6. Custodian appointment.

   AR 195-5 para 1-4g(1): commanders "appoint, in writing, one primary and one alternate evidence
   custodian." The role alone confers NO evidence-room authority in EMC - an active appointment
   is required (IAM-005, invariant I-11).

   PersonnelCategory decides WHICH AR 195-5 para 1-7a rule the eligibility attestation is made
   under, and the two CI rules genuinely differ:

     1 = MilitaryCi  para 1-7a(1)(c) - must be a credentialed CI agent; CI agents in a
                     probationary program will not be appointed.
     2 = Civilian    para 1-7a(2)(c) - may be appointed depending on the needs and requirements
                     of the unit and at the discretion of the commander.

   Note what 1-7a(2)(c) does NOT say: unlike the USACIDC and Military Police civilian paragraphs,
   it states no job-series list and no background-investigation requirement for CI units. Do not
   import those restrictions.

   EMC cannot verify either rule, so the applicable one is attested and retained with the
   category that determined it.

   Only insert this row for a person who genuinely holds written appointment orders.
--------------------------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.CustodianAppointments WHERE EvidenceRoomId = @RoomId)
BEGIN
    INSERT INTO dbo.CustodianAppointments
        (EvidenceRoomId, UserId, AppointmentType, PersonnelCategory, EffectiveFrom, EffectiveTo,
         AppointmentOrderReference, AppointingAuthority, EligibilityAttested,
         SupersedesAppointmentId, SupersededByAppointmentId,
         RecordedByUserId, RecordedAtUtc, Notes, ConcurrencyStamp)
    VALUES
        (@RoomId, @UserId,
         1,                                  -- AppointmentType: 1 = Primary, 2 = Alternate
         1,                                  -- <-- EDIT PersonnelCategory: 1 = MilitaryCi, 2 = Civilian
         SYSDATETIMEOFFSET(),
         NULL,                               -- open-ended; ended when the appointment ends
         N'ORDERS 2026-114, 902d MI Group',  -- <-- EDIT: the written appointment document
         N'Commander, 902d MI Group',        -- <-- EDIT
         1,                                  -- eligibility attested (para 1-7a(1)(c))
         NULL, NULL,
         @UserId, SYSUTCDATETIME(), NULL, NEWID());
END;

/* ---------------------------------------------------------------------------------------------
   7. Storage locations.

   Hierarchical: shelves and containers, then bins. Kinds mirror the storage concepts AR 195-5
   names - see StorageLocationKind (4-1 evidence room, 4-1d depository, 4-3 temporary facility,
   2-6f impound lot, 2-13 long-term container).

   A temporary release is NOT a storage location; it is a custody state (LOC-005).
--------------------------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.StorageLocations WHERE EvidenceRoomId = @RoomId)
BEGIN
    INSERT INTO dbo.StorageLocations (EvidenceRoomId, Name, Kind, ParentId, IsActive, ConcurrencyStamp)
    VALUES (@RoomId, N'Shelf B', 7, NULL, 1, NEWID());          -- 7 = Shelf

    DECLARE @ShelfId INT = SCOPE_IDENTITY();

    INSERT INTO dbo.StorageLocations (EvidenceRoomId, Name, Kind, ParentId, IsActive, ConcurrencyStamp)
    VALUES
        (@RoomId, N'Bin 14', 8, @ShelfId, 1, NEWID()),          -- 8 = Bin
        (@RoomId, N'Bin 19', 8, @ShelfId, 1, NEWID()),
        (@RoomId, N'High-Value Safe / Drawer 2', 6, NULL, 1, NEWID());  -- 6 = HighValueContainer
END;

COMMIT TRANSACTION;
GO

PRINT 'EMC seed complete. Review docs/open-policy-decisions.md before recording real evidence.';
