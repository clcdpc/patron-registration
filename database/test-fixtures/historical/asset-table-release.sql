/*
   Immutable fixture for migrations 001-005 and 010: assets, upload scope,
   and the asset cleanup access path exist; the revision/generation and
   durable asset-reference-lock releases have not yet run.
*/
create table dbo.RegistrationFormCodeMetadata
(
    OrganizationId int not null,
    FormCode nvarchar(64) not null,
    DisplayName nvarchar(200) not null,
    Description nvarchar(2000) null,
    CreatedAtUtc datetime2(7) not null constraint DF_RFCode_Created default sysutcdatetime(),
    CreatedBy nvarchar(256) not null,
    ModifiedAtUtc datetime2(7) not null constraint DF_RFCode_Modified default sysutcdatetime(),
    ModifiedBy nvarchar(256) not null,
    constraint PK_RegistrationFormCodeMetadata primary key (OrganizationId, FormCode),
    constraint CK_RFCode_NotBlank check (len(FormCode) > 0)
);

create table dbo.RegistrationSettingScopeVersions
(
    OrganizationId int not null,
    FormCode nvarchar(64) not null constraint DF_RSSV_Code default '',
    Version bigint not null constraint DF_RSSV_Version default 0,
    ModifiedAtUtc datetime2(7) not null constraint DF_RSSV_Modified default sysutcdatetime(),
    constraint PK_RegistrationSettingScopeVersions primary key (OrganizationId, FormCode)
);

create table dbo.RegistrationSettingDrafts
(
    DraftId bigint identity(1,1) not null constraint PK_RegistrationSettingDrafts primary key,
    OrganizationId int not null,
    FormCode nvarchar(64) not null constraint DF_RSD_Code default '',
    BaselineVersion bigint not null,
    Status varchar(16) not null,
    CreatedAtUtc datetime2(7) not null constraint DF_RSD_Created default sysutcdatetime(),
    CreatedBy nvarchar(256) not null,
    ModifiedAtUtc datetime2(7) not null constraint DF_RSD_Modified default sysutcdatetime(),
    ModifiedBy nvarchar(256) not null,
    CommittedAtUtc datetime2(7) null,
    CommittedBy nvarchar(256) null,
    DiscardedAtUtc datetime2(7) null,
    DiscardedBy nvarchar(256) null,
    constraint CK_RSD_Status check (Status in ('Active', 'Committed', 'Discarded', 'Invalidated'))
);

create unique index UX_RSD_ActiveScope
    on dbo.RegistrationSettingDrafts (OrganizationId, FormCode)
    where Status = 'Active';

create table dbo.RegistrationSettingDraftChanges
(
    DraftChangeId bigint identity(1,1) not null constraint PK_RegistrationSettingDraftChanges primary key,
    DraftId bigint not null,
    SettingKey nvarchar(200) not null,
    Operation varchar(20) not null,
    Value nvarchar(max) null,
    ModifiedAtUtc datetime2(7) not null constraint DF_RSDC_Modified default sysutcdatetime(),
    ModifiedBy nvarchar(256) not null,
    constraint FK_RSDC_Draft foreign key (DraftId)
        references dbo.RegistrationSettingDrafts (DraftId)
        on delete cascade,
    constraint UQ_RSDC_Key unique (DraftId, SettingKey),
    constraint CK_RSDC_Operation check (Operation in ('Upsert', 'RemoveOverride')),
    constraint CK_RSDC_Value check
    (
        (Operation = 'Upsert' and Value is not null)
        or (Operation = 'RemoveOverride' and Value is null)
    )
);

create table dbo.RegistrationSettingPreviewLinks
(
    PreviewLinkId bigint identity(1,1) not null constraint PK_RegistrationSettingPreviewLinks primary key,
    DraftId bigint not null,
    TokenHash binary(32) not null,
    OperationalBranchId int not null,
    AllowLiveSubmission bit not null constraint DF_RSPL_Live default 0,
    CreatedAtUtc datetime2(7) not null constraint DF_RSPL_Created default sysutcdatetime(),
    CreatedBy nvarchar(256) not null,
    ModifiedAtUtc datetime2(7) not null constraint DF_RSPL_Modified default sysutcdatetime(),
    ModifiedBy nvarchar(256) not null,
    RevokedAtUtc datetime2(7) null,
    RevokedBy nvarchar(256) null,
    ExpiresAtUtc datetime2(7) null,
    constraint FK_RSPL_Draft foreign key (DraftId)
        references dbo.RegistrationSettingDrafts (DraftId)
        on delete cascade,
    constraint UQ_RSPL_Token unique (TokenHash)
);

create table dbo.RegistrationSettingAuditEvents
(
    AuditEventId bigint identity(1,1) not null constraint PK_RegistrationSettingAuditEvents primary key,
    TimestampUtc datetime2(7) not null constraint DF_RSAE_Time default sysutcdatetime(),
    EventType nvarchar(80) not null,
    ActorId nvarchar(128) null,
    ActorName nvarchar(256) null,
    ActorOrganizationId int null,
    TargetOrganizationId int not null,
    TargetLibraryId int null,
    FormCode nvarchar(64) not null constraint DF_RSAE_Code default '',
    SettingKey nvarchar(200) null,
    PreviousValue nvarchar(max) null,
    NewValue nvarchar(max) null,
    IsSensitive bit not null constraint DF_RSAE_Secret default 0,
    DraftId bigint null,
    PreviewLinkId bigint null,
    CorrelationId nvarchar(128) null,
    IpAddress nvarchar(64) null,
    Succeeded bit not null,
    FailureReason nvarchar(1000) null,
    MetadataJson nvarchar(max) null,
    constraint CK_RSAE_Json check (MetadataJson is null or isjson(MetadataJson) = 1)
);

create index IX_RSAE_LibraryTime
    on dbo.RegistrationSettingAuditEvents (TargetLibraryId, TimestampUtc desc)
    include (EventType, TargetOrganizationId, FormCode);

create index IX_RSAE_ScopeFilter
    on dbo.RegistrationSettingAuditEvents
    (TargetOrganizationId, FormCode, EventType, TimestampUtc desc);

create table dbo.RegistrationSettingsCacheGeneration
(
    Id tinyint not null constraint PK_RegistrationSettingsCacheGeneration primary key,
    Generation bigint not null,
    ModifiedAtUtc datetime2(7) not null
);

insert dbo.RegistrationSettingsCacheGeneration (Id, Generation, ModifiedAtUtc)
values (1, 0, sysutcdatetime());

create table dbo.RegistrationFormAssets
(
    AssetId int identity(1,1) not null
        constraint PK_RegistrationFormAssets primary key,
    FileName nvarchar(255) not null,
    ContentType varchar(100) not null,
    Content varbinary(max) not null,
    ContentHash varchar(64) not null,
    CreatedDate datetime2(7) not null
        constraint DF_RegistrationFormAssets_CreatedDate default sysutcdatetime(),
    ModifiedDate datetime2(7) not null
        constraint DF_RegistrationFormAssets_ModifiedDate default sysutcdatetime(),
    UploadOrganizationId int null,
    UploadFormCode nvarchar(64) null,
    constraint CK_RegistrationFormAssets_FileName_NotBlank
        check (len(ltrim(rtrim(FileName))) > 0),
    constraint CK_RegistrationFormAssets_ContentType_NotBlank
        check (len(ltrim(rtrim(ContentType))) > 0),
    constraint CK_RegistrationFormAssets_Content_NotEmpty
        check (datalength(Content) > 0),
    constraint CK_RegistrationFormAssets_ContentHash_Sha256
        check (len(ContentHash) = 64)
);

create index IX_RegistrationFormAssets_UploadScope
    on dbo.RegistrationFormAssets (UploadOrganizationId, UploadFormCode);

create index IX_RegistrationFormAssets_CreatedDate
    on dbo.RegistrationFormAssets (CreatedDate);
