/*
  Patron-registration settings administration schema.
  Run manually against clcdb with CREATE TABLE and CREATE INDEX permission.
  The application never executes this script at startup.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.RegistrationFormCodeMetadata') IS NULL
BEGIN
    CREATE TABLE dbo.RegistrationFormCodeMetadata
    (
        OrganizationId int NOT NULL,
        FormCode nvarchar(64) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Description nvarchar(2000) NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_RFCode_Created DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NOT NULL,
        ModifiedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_RFCode_Modified DEFAULT SYSUTCDATETIME(),
        ModifiedBy nvarchar(256) NOT NULL,
        CONSTRAINT PK_RegistrationFormCodeMetadata PRIMARY KEY (OrganizationId, FormCode),
        CONSTRAINT CK_RFCode_NotBlank CHECK (LEN(FormCode) > 0)
    );
END;

IF OBJECT_ID('dbo.RegistrationSettingScopeVersions') IS NULL
BEGIN
    CREATE TABLE dbo.RegistrationSettingScopeVersions
    (
        OrganizationId int NOT NULL,
        FormCode nvarchar(64) NOT NULL CONSTRAINT DF_RSSV_Code DEFAULT '',
        Version bigint NOT NULL CONSTRAINT DF_RSSV_Version DEFAULT 0,
        ModifiedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_RSSV_Modified DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_RegistrationSettingScopeVersions PRIMARY KEY (OrganizationId, FormCode)
    );
END;

IF OBJECT_ID('dbo.RegistrationSettingDrafts') IS NULL
BEGIN
    CREATE TABLE dbo.RegistrationSettingDrafts
    (
        DraftId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RegistrationSettingDrafts PRIMARY KEY,
        OrganizationId int NOT NULL,
        FormCode nvarchar(64) NOT NULL CONSTRAINT DF_RSD_Code DEFAULT '',
        BaselineVersion bigint NOT NULL,
        Revision bigint NOT NULL CONSTRAINT DF_RSD_Revision DEFAULT 0,
        Status varchar(16) NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_RSD_Created DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NOT NULL,
        ModifiedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_RSD_Modified DEFAULT SYSUTCDATETIME(),
        ModifiedBy nvarchar(256) NOT NULL,
        CommittedAtUtc datetime2(7) NULL,
        CommittedBy nvarchar(256) NULL,
        DiscardedAtUtc datetime2(7) NULL,
        DiscardedBy nvarchar(256) NULL,
        CONSTRAINT CK_RSD_Status CHECK (Status IN ('Active', 'Committed', 'Discarded', 'Invalidated'))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_RSD_ActiveScope')
BEGIN
    CREATE UNIQUE INDEX UX_RSD_ActiveScope
        ON dbo.RegistrationSettingDrafts (OrganizationId, FormCode)
        WHERE Status = 'Active';
END;

IF OBJECT_ID('dbo.RegistrationSettingDraftChanges') IS NULL
BEGIN
    CREATE TABLE dbo.RegistrationSettingDraftChanges
    (
        DraftChangeId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RegistrationSettingDraftChanges PRIMARY KEY,
        DraftId bigint NOT NULL,
        SettingKey nvarchar(200) NOT NULL,
        Operation varchar(20) NOT NULL,
        Value nvarchar(max) NULL,
        ModifiedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_RSDC_Modified DEFAULT SYSUTCDATETIME(),
        ModifiedBy nvarchar(256) NOT NULL,
        CONSTRAINT FK_RSDC_Draft FOREIGN KEY (DraftId) REFERENCES dbo.RegistrationSettingDrafts (DraftId) ON DELETE CASCADE,
        CONSTRAINT UQ_RSDC_Key UNIQUE (DraftId, SettingKey),
        CONSTRAINT CK_RSDC_Operation CHECK (Operation IN ('Upsert', 'RemoveOverride')),
        CONSTRAINT CK_RSDC_Value CHECK
        (
            (Operation = 'Upsert' AND Value IS NOT NULL)
            OR (Operation = 'RemoveOverride' AND Value IS NULL)
        )
    );
END;

IF OBJECT_ID('dbo.RegistrationSettingPreviewLinks') IS NULL
BEGIN
    CREATE TABLE dbo.RegistrationSettingPreviewLinks
    (
        PreviewLinkId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RegistrationSettingPreviewLinks PRIMARY KEY,
        DraftId bigint NOT NULL,
        TokenHash binary(32) NOT NULL,
        OperationalBranchId int NOT NULL,
        AllowLiveSubmission bit NOT NULL CONSTRAINT DF_RSPL_Live DEFAULT 0,
        LiveSettingsGeneration bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_RSPL_Created DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(256) NOT NULL,
        ModifiedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_RSPL_Modified DEFAULT SYSUTCDATETIME(),
        ModifiedBy nvarchar(256) NOT NULL,
        RevokedAtUtc datetime2(7) NULL,
        RevokedBy nvarchar(256) NULL,
        ExpiresAtUtc datetime2(7) NULL,
        CONSTRAINT FK_RSPL_Draft FOREIGN KEY (DraftId) REFERENCES dbo.RegistrationSettingDrafts (DraftId) ON DELETE CASCADE,
        CONSTRAINT UQ_RSPL_Token UNIQUE (TokenHash)
    );
END;

IF OBJECT_ID('dbo.RegistrationSettingAuditEvents') IS NULL
BEGIN
    CREATE TABLE dbo.RegistrationSettingAuditEvents
    (
        AuditEventId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RegistrationSettingAuditEvents PRIMARY KEY,
        TimestampUtc datetime2(7) NOT NULL CONSTRAINT DF_RSAE_Time DEFAULT SYSUTCDATETIME(),
        EventType nvarchar(80) NOT NULL,
        ActorId nvarchar(128) NULL,
        ActorName nvarchar(256) NULL,
        ActorOrganizationId int NULL,
        TargetOrganizationId int NOT NULL,
        TargetLibraryId int NULL,
        FormCode nvarchar(64) NOT NULL CONSTRAINT DF_RSAE_Code DEFAULT '',
        SettingKey nvarchar(200) NULL,
        PreviousValue nvarchar(max) NULL,
        NewValue nvarchar(max) NULL,
        IsSensitive bit NOT NULL CONSTRAINT DF_RSAE_Secret DEFAULT 0,
        DraftId bigint NULL,
        PreviewLinkId bigint NULL,
        CorrelationId nvarchar(128) NULL,
        IpAddress nvarchar(64) NULL,
        Succeeded bit NOT NULL,
        FailureReason nvarchar(1000) NULL,
        MetadataJson nvarchar(max) NULL,
        CONSTRAINT CK_RSAE_Json CHECK (MetadataJson IS NULL OR ISJSON(MetadataJson) = 1)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RSAE_LibraryTime')
BEGIN
    CREATE INDEX IX_RSAE_LibraryTime
        ON dbo.RegistrationSettingAuditEvents (TargetLibraryId, TimestampUtc DESC)
        INCLUDE (EventType, TargetOrganizationId, FormCode);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RSAE_ScopeFilter')
BEGIN
    CREATE INDEX IX_RSAE_ScopeFilter
        ON dbo.RegistrationSettingAuditEvents (TargetOrganizationId, FormCode, EventType, TimestampUtc DESC);
END;

IF OBJECT_ID('dbo.RegistrationSettingsCacheGeneration') IS NULL
BEGIN
    CREATE TABLE dbo.RegistrationSettingsCacheGeneration
    (
        Id tinyint NOT NULL CONSTRAINT PK_RegistrationSettingsCacheGeneration PRIMARY KEY,
        Generation bigint NOT NULL,
        ModifiedAtUtc datetime2(7) NOT NULL
    );

    INSERT dbo.RegistrationSettingsCacheGeneration (Id, Generation, ModifiedAtUtc)
    VALUES (1, 0, SYSUTCDATETIME());
END;

COMMIT;
