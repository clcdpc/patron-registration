/*
	Patron-registration settings administration desired-state deployment.

	This is the authoritative database deployment for the settings
	administration feature. It converges the current database state directly;
	it deliberately does not record deployment history or infer a prior
	deployment sequence.

	dbo.RegistrationFormSettings and dbo.RegistrationFormSettingTypes are
	shared clcdb prerequisites. This script validates those objects but does
	not take ownership of their schema.
*/
set nocount on
set xact_abort on

declare @deployment_transaction_started bit = 0

begin try
	if @@trancount <> 0
	begin
		raiserror('settings-administration.sql must be executed without an existing transaction.', 16, 1)
	end

	begin transaction
	set @deployment_transaction_started = 1

	declare @application_lock_result int
	exec @application_lock_result = sys.sp_getapplock
		@Resource = N'Clc.PatronRegistration.DatabaseConvergence',
		@LockMode = N'Exclusive',
		@LockOwner = N'Transaction',
		@LockTimeout = 600000,
		@DbPrincipal = N'public'

	if @application_lock_result < 0
	begin
		raiserror('Could not acquire the patron-registration database convergence application lock (sp_getapplock result %d). No deployment changes were made.', 16, 1, @application_lock_result)
	end

	if object_id('dbo.RegistrationFormSettingTypes', 'U') is null
	begin
		raiserror('dbo.RegistrationFormSettingTypes must exist before settings administration is installed.', 16, 1)
	end

	if object_id('dbo.RegistrationFormSettings', 'U') is null
	begin
		raiserror('dbo.RegistrationFormSettings must exist before settings administration is installed.', 16, 1)
	end

	if col_length('dbo.RegistrationFormSettingTypes', 'Setting') is null
	begin
		raiserror('dbo.RegistrationFormSettingTypes.Setting must exist before settings administration is installed.', 16, 1)
	end

	if col_length('dbo.RegistrationFormSettings', 'OrganizationID') is null
		or col_length('dbo.RegistrationFormSettings', 'Setting') is null
		or col_length('dbo.RegistrationFormSettings', 'FormCode') is null
		or col_length('dbo.RegistrationFormSettings', 'Value') is null
	begin
		raiserror('dbo.RegistrationFormSettings must contain OrganizationID, Setting, FormCode, and Value.', 16, 1)
	end

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormSettingTypes')
			and name = 'Setting'
			and (system_type_id <> 231 or max_length <> 400 or is_nullable <> 0)
	)
	begin
		raiserror('Shared prerequisite dbo.RegistrationFormSettingTypes.Setting must be nvarchar(200) NOT NULL.', 16, 1)
	end

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormSettings')
			and name = 'OrganizationID'
			and (system_type_id <> 56 or is_nullable <> 0)
	)
		or exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormSettings')
			and name = 'Setting'
			and (system_type_id <> 231 or max_length <> 400 or is_nullable <> 0)
		)
		or exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormSettings')
			and name = 'FormCode'
			and (system_type_id <> 231 or max_length <> 128 or is_nullable <> 0)
		)
		or exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormSettings')
			and name = 'Value'
			and (system_type_id <> 231 or max_length <> -1 or is_nullable <> 1)
		)
	begin
		raiserror('Shared prerequisite dbo.RegistrationFormSettings has an incompatible OrganizationID, Setting, FormCode, or Value definition.', 16, 1)
	end

	if not exists
	(
		select 1
		from sys.indexes i
		where i.object_id = object_id('dbo.RegistrationFormSettingTypes')
			and i.is_unique = 1
			and i.is_disabled = 0
			and
			(
				select count(*)
				from sys.index_columns ic
				where ic.object_id = i.object_id
					and ic.index_id = i.index_id
					and ic.key_ordinal > 0
			) = 1
			and
			(
				select c.name
				from sys.index_columns ic
				inner join sys.columns c
					on c.object_id = ic.object_id
					and c.column_id = ic.column_id
				where ic.object_id = i.object_id
					and ic.index_id = i.index_id
					and ic.key_ordinal = 1
			) = 'Setting'
	)
	begin
		raiserror('Shared prerequisite dbo.RegistrationFormSettingTypes.Setting must have a unique key.', 16, 1)
	end

	if not exists
	(
		select 1
		from sys.indexes i
		where i.object_id = object_id('dbo.RegistrationFormSettings')
			and i.is_unique = 1
			and i.is_disabled = 0
			and
			(
				select count(*)
				from sys.index_columns ic
				where ic.object_id = i.object_id
					and ic.index_id = i.index_id
					and ic.key_ordinal > 0
			) = 3
			and
			(
				select c.name
				from sys.index_columns ic
				inner join sys.columns c
					on c.object_id = ic.object_id
					and c.column_id = ic.column_id
				where ic.object_id = i.object_id
					and ic.index_id = i.index_id
					and ic.key_ordinal = 1
			) = 'OrganizationID'
			and
			(
				select c.name
				from sys.index_columns ic
				inner join sys.columns c
					on c.object_id = ic.object_id
					and c.column_id = ic.column_id
				where ic.object_id = i.object_id
					and ic.index_id = i.index_id
					and ic.key_ordinal = 2
			) = 'Setting'
			and
			(
				select c.name
				from sys.index_columns ic
				inner join sys.columns c
					on c.object_id = ic.object_id
					and c.column_id = ic.column_id
				where ic.object_id = i.object_id
					and ic.index_id = i.index_id
					and ic.key_ordinal = 3
			) = 'FormCode'
	)
	begin
		raiserror('Shared prerequisite dbo.RegistrationFormSettings must have a unique key on OrganizationID, Setting, and FormCode.', 16, 1)
	end

	if not exists
	(
		select 1
		from sys.foreign_keys fk
		inner join sys.foreign_key_columns fkc
			on fkc.constraint_object_id = fk.object_id
		where fk.parent_object_id = object_id('dbo.RegistrationFormSettings')
			and fk.referenced_object_id = object_id('dbo.RegistrationFormSettingTypes')
			and fk.is_disabled = 0
			and fk.is_not_trusted = 0
			and fkc.parent_column_id = columnproperty(object_id('dbo.RegistrationFormSettings'), 'Setting', 'ColumnId')
			and fkc.referenced_column_id = columnproperty(object_id('dbo.RegistrationFormSettingTypes'), 'Setting', 'ColumnId')
	)
	begin
		raiserror('Shared prerequisite dbo.RegistrationFormSettings.Setting must have a trusted foreign key to dbo.RegistrationFormSettingTypes.Setting.', 16, 1)
	end

	if object_id('dbo.RegistrationFormCodeMetadata', 'U') is null
	begin
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
		)
	end

	if object_id('dbo.RegistrationSettingScopeVersions', 'U') is null
	begin
		create table dbo.RegistrationSettingScopeVersions
		(
			OrganizationId int not null,
			FormCode nvarchar(64) not null constraint DF_RSSV_Code default '',
			Version bigint not null constraint DF_RSSV_Version default 0,
			ModifiedAtUtc datetime2(7) not null constraint DF_RSSV_Modified default sysutcdatetime(),
			constraint PK_RegistrationSettingScopeVersions primary key (OrganizationId, FormCode)
		)
	end

	if object_id('dbo.RegistrationSettingDrafts', 'U') is null
	begin
		create table dbo.RegistrationSettingDrafts
		(
			DraftId bigint identity(1,1) not null constraint PK_RegistrationSettingDrafts primary key,
			OrganizationId int not null,
			FormCode nvarchar(64) not null constraint DF_RSD_Code default '',
			BaselineVersion bigint not null,
			Revision bigint not null constraint DF_RSD_Revision default 0,
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
		)
	end

	if object_id('dbo.RegistrationSettingDraftChanges', 'U') is null
	begin
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
		)
	end

	if object_id('dbo.RegistrationSettingPreviewLinks', 'U') is null
	begin
		create table dbo.RegistrationSettingPreviewLinks
		(
			PreviewLinkId bigint identity(1,1) not null constraint PK_RegistrationSettingPreviewLinks primary key,
			DraftId bigint not null,
			TokenHash binary(32) not null,
			OperationalBranchId int not null,
			AllowLiveSubmission bit not null constraint DF_RSPL_Live default 0,
			LiveSettingsGeneration bigint null,
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
		)
	end
	if col_length('dbo.RegistrationSettingPreviewLinks', 'OperationalBranchId') is null
	begin
		update dbo.RegistrationSettingPreviewLinks
		set RevokedAtUtc = coalesce(RevokedAtUtc, sysutcdatetime()),
			RevokedBy = coalesce(RevokedBy, 'settings-administration.sql')

		alter table dbo.RegistrationSettingPreviewLinks
			add OperationalBranchId int null

		exec
		(
			'update dbo.RegistrationSettingPreviewLinks
			set OperationalBranchId = -2147483648
			where OperationalBranchId is null'
		)

		alter table dbo.RegistrationSettingPreviewLinks
			alter column OperationalBranchId int not null
	end
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and name = 'OperationalBranchId'
			and system_type_id <> 56
	)
	begin
		if exists
		(
			select 1
			from dbo.RegistrationSettingPreviewLinks
			where OperationalBranchId is not null
				and try_convert(int, OperationalBranchId) is null
		)
		begin
			raiserror('dbo.RegistrationSettingPreviewLinks contains an operational branch that cannot be converted to int safely.', 16, 1)
		end
		alter table dbo.RegistrationSettingPreviewLinks alter column OperationalBranchId int null
	end

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and name = 'OperationalBranchId'
			and is_nullable = 1
	)
	begin
		exec
		(
			'update dbo.RegistrationSettingPreviewLinks
			set RevokedAtUtc = coalesce(RevokedAtUtc, sysutcdatetime()),
				RevokedBy = coalesce(RevokedBy, ''settings-administration.sql'')
			where OperationalBranchId is null;

			update dbo.RegistrationSettingPreviewLinks
			set OperationalBranchId = -2147483648
			where OperationalBranchId is null'
		)

		alter table dbo.RegistrationSettingPreviewLinks
			alter column OperationalBranchId int not null
	end

	if col_length('dbo.RegistrationSettingPreviewLinks', 'LiveSettingsGeneration') is null
	begin
		alter table dbo.RegistrationSettingPreviewLinks
			add LiveSettingsGeneration bigint null
	end

	if object_id('dbo.RegistrationSettingAuditEvents', 'U') is null
	begin
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
		)
	end

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and name = 'PreviousValue'
			and max_length <> -1
	)
	begin
		alter table dbo.RegistrationSettingAuditEvents
			alter column PreviousValue nvarchar(max) null
	end

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and name = 'NewValue'
			and max_length <> -1
	)
	begin
		alter table dbo.RegistrationSettingAuditEvents
			alter column NewValue nvarchar(max) null
	end

	if not exists
	(
		select 1
		from sys.indexes
		where object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and name = 'IX_RSAE_LibraryTime'
	)
	begin
		create index IX_RSAE_LibraryTime
		on dbo.RegistrationSettingAuditEvents (TargetLibraryId, TimestampUtc desc)
		include (EventType, TargetOrganizationId, FormCode)
	end

	if not exists
	(
		select 1
		from sys.indexes
		where object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and name = 'IX_RSAE_ScopeFilter'
	)
	begin
		create index IX_RSAE_ScopeFilter
		on dbo.RegistrationSettingAuditEvents
		(
			TargetOrganizationId,
			FormCode,
			EventType,
			TimestampUtc desc
		)
	end

	if object_id('dbo.RegistrationSettingsCacheGeneration', 'U') is null
	begin
		create table dbo.RegistrationSettingsCacheGeneration
		(
			Id tinyint not null constraint PK_RegistrationSettingsCacheGeneration primary key,
			Generation bigint not null,
			ModifiedAtUtc datetime2(7) not null
		)
	end

	if not exists
	(
		select 1
		from dbo.RegistrationSettingsCacheGeneration
		where Id = 1
	)
	begin
		insert into dbo.RegistrationSettingsCacheGeneration
		(
			Id,
			Generation,
			ModifiedAtUtc
		)
		values
		(
			1,
			0,
			sysutcdatetime()
		)
	end

	if object_id('dbo.RegistrationFormAssets', 'U') is null
	begin
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
		)
	end
	else
	begin
		if col_length('dbo.RegistrationFormAssets', 'UploadOrganizationId') is null
		begin
			alter table dbo.RegistrationFormAssets
				add UploadOrganizationId int null
		end

		if col_length('dbo.RegistrationFormAssets', 'UploadFormCode') is null
		begin
			alter table dbo.RegistrationFormAssets
				add UploadFormCode nvarchar(64) null
		end
	end

	if not exists
	(
		select 1
		from sys.indexes
		where object_id = object_id('dbo.RegistrationFormAssets')
			and name = 'IX_RegistrationFormAssets_UploadScope'
	)
	begin
		create index IX_RegistrationFormAssets_UploadScope
		on dbo.RegistrationFormAssets
		(
			UploadOrganizationId,
			UploadFormCode
		)
	end

	if col_length('dbo.RegistrationSettingDrafts', 'Revision') is null
	begin
		exec
		(
			'alter table dbo.RegistrationSettingDrafts
			 add Revision bigint not null constraint DF_RSD_Revision default 0'
		)
	end

	if not exists
	(
		select 1
		from sys.indexes
		where object_id = object_id('dbo.RegistrationFormAssets')
			and name = 'IX_RegistrationFormAssets_CreatedDate'
	)
	begin
		create index IX_RegistrationFormAssets_CreatedDate
		on dbo.RegistrationFormAssets (CreatedDate)
	end

	if object_id('dbo.RegistrationFormAssetReferenceLocks', 'U') is null
	begin
		create table dbo.RegistrationFormAssetReferenceLocks
		(
			LockId tinyint not null
				constraint PK_RegistrationFormAssetReferenceLocks primary key,
			constraint CK_RegistrationFormAssetReferenceLocks_Singleton check (LockId = 1)
		)
	end

	if not exists
	(
		select 1
		from dbo.RegistrationFormAssetReferenceLocks
		where LockId = 1
	)
	begin
		insert into dbo.RegistrationFormAssetReferenceLocks (LockId)
		values (1)
	end

	/*
	   The tables above may have been created by an older partial deployment.
	   The known additive changes are repaired above. A missing identity or
	   business-key column cannot be invented without risking data loss, so
	   stop with a specific error rather than allowing a later statement to
	   fail ambiguously.
	*/
	if col_length('dbo.RegistrationFormCodeMetadata', 'OrganizationId') is null
		or col_length('dbo.RegistrationFormCodeMetadata', 'FormCode') is null
		or col_length('dbo.RegistrationFormCodeMetadata', 'DisplayName') is null
		or col_length('dbo.RegistrationFormCodeMetadata', 'Description') is null
		or col_length('dbo.RegistrationFormCodeMetadata', 'CreatedAtUtc') is null
		or col_length('dbo.RegistrationFormCodeMetadata', 'CreatedBy') is null
		or col_length('dbo.RegistrationFormCodeMetadata', 'ModifiedAtUtc') is null
		or col_length('dbo.RegistrationFormCodeMetadata', 'ModifiedBy') is null
	begin
		raiserror('dbo.RegistrationFormCodeMetadata is missing a required column. Restore the table from the current definition or remove the empty partial table before deployment.', 16, 1)
	end

	if col_length('dbo.RegistrationSettingScopeVersions', 'OrganizationId') is null
		or col_length('dbo.RegistrationSettingScopeVersions', 'FormCode') is null
		or col_length('dbo.RegistrationSettingScopeVersions', 'Version') is null
		or col_length('dbo.RegistrationSettingScopeVersions', 'ModifiedAtUtc') is null
	begin
		raiserror('dbo.RegistrationSettingScopeVersions is missing a required column. Restore the table from the current definition or remove the empty partial table before deployment.', 16, 1)
	end

	if col_length('dbo.RegistrationSettingDrafts', 'DraftId') is null
		or col_length('dbo.RegistrationSettingDrafts', 'OrganizationId') is null
		or col_length('dbo.RegistrationSettingDrafts', 'FormCode') is null
		or col_length('dbo.RegistrationSettingDrafts', 'BaselineVersion') is null
		or col_length('dbo.RegistrationSettingDrafts', 'Revision') is null
		or col_length('dbo.RegistrationSettingDrafts', 'Status') is null
		or col_length('dbo.RegistrationSettingDrafts', 'CreatedAtUtc') is null
		or col_length('dbo.RegistrationSettingDrafts', 'CreatedBy') is null
		or col_length('dbo.RegistrationSettingDrafts', 'ModifiedAtUtc') is null
		or col_length('dbo.RegistrationSettingDrafts', 'ModifiedBy') is null
		or col_length('dbo.RegistrationSettingDrafts', 'CommittedAtUtc') is null
		or col_length('dbo.RegistrationSettingDrafts', 'CommittedBy') is null
		or col_length('dbo.RegistrationSettingDrafts', 'DiscardedAtUtc') is null
		or col_length('dbo.RegistrationSettingDrafts', 'DiscardedBy') is null
	begin
		raiserror('dbo.RegistrationSettingDrafts is missing a required column. Restore the table from the current definition or remove the empty partial table before deployment.', 16, 1)
	end

	if col_length('dbo.RegistrationSettingDraftChanges', 'DraftChangeId') is null
		or col_length('dbo.RegistrationSettingDraftChanges', 'DraftId') is null
		or col_length('dbo.RegistrationSettingDraftChanges', 'SettingKey') is null
		or col_length('dbo.RegistrationSettingDraftChanges', 'Operation') is null
		or col_length('dbo.RegistrationSettingDraftChanges', 'Value') is null
		or col_length('dbo.RegistrationSettingDraftChanges', 'ModifiedAtUtc') is null
		or col_length('dbo.RegistrationSettingDraftChanges', 'ModifiedBy') is null
	begin
		raiserror('dbo.RegistrationSettingDraftChanges is missing a required column. Restore the table from the current definition or remove the empty partial table before deployment.', 16, 1)
	end

	if col_length('dbo.RegistrationSettingPreviewLinks', 'PreviewLinkId') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'DraftId') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'TokenHash') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'OperationalBranchId') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'AllowLiveSubmission') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'LiveSettingsGeneration') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'CreatedAtUtc') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'CreatedBy') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'ModifiedAtUtc') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'ModifiedBy') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'RevokedAtUtc') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'RevokedBy') is null
		or col_length('dbo.RegistrationSettingPreviewLinks', 'ExpiresAtUtc') is null
	begin
		raiserror('dbo.RegistrationSettingPreviewLinks is missing a required column. Restore the table from the current definition or remove the empty partial table before deployment.', 16, 1)
	end

	if col_length('dbo.RegistrationSettingAuditEvents', 'AuditEventId') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'TimestampUtc') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'EventType') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'TargetOrganizationId') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'TargetLibraryId') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'FormCode') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'SettingKey') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'PreviousValue') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'NewValue') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'IsSensitive') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'DraftId') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'PreviewLinkId') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'CorrelationId') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'IpAddress') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'Succeeded') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'FailureReason') is null
		or col_length('dbo.RegistrationSettingAuditEvents', 'MetadataJson') is null
	begin
		raiserror('dbo.RegistrationSettingAuditEvents is missing a required column. Restore the table from the current definition or remove the empty partial table before deployment.', 16, 1)
	end

	if col_length('dbo.RegistrationSettingsCacheGeneration', 'Id') is null
		or col_length('dbo.RegistrationSettingsCacheGeneration', 'Generation') is null
		or col_length('dbo.RegistrationSettingsCacheGeneration', 'ModifiedAtUtc') is null
	begin
		raiserror('dbo.RegistrationSettingsCacheGeneration is missing a required column. Restore the table from the current definition or remove the empty partial table before deployment.', 16, 1)
	end

	if col_length('dbo.RegistrationFormAssets', 'AssetId') is null
		or col_length('dbo.RegistrationFormAssets', 'FileName') is null
		or col_length('dbo.RegistrationFormAssets', 'ContentType') is null
		or col_length('dbo.RegistrationFormAssets', 'Content') is null
		or col_length('dbo.RegistrationFormAssets', 'ContentHash') is null
		or col_length('dbo.RegistrationFormAssets', 'CreatedDate') is null
		or col_length('dbo.RegistrationFormAssets', 'ModifiedDate') is null
		or col_length('dbo.RegistrationFormAssets', 'UploadOrganizationId') is null
		or col_length('dbo.RegistrationFormAssets', 'UploadFormCode') is null
	begin
		raiserror('dbo.RegistrationFormAssets is missing a required column. Restore the table from the current definition or remove the empty partial table before deployment.', 16, 1)
	end

	if col_length('dbo.RegistrationFormAssetReferenceLocks', 'LockId') is null
	begin
		raiserror('dbo.RegistrationFormAssetReferenceLocks is missing its required LockId column. Restore the table from the current definition or remove the empty partial table before deployment.', 16, 1)
	end

	exec
	(
		N'if exists
		(
			select 1
			from dbo.RegistrationSettingDrafts
			where Status = ''Active''
			group by OrganizationId, FormCode
			having count(*) > 1
		)
		begin
			raiserror(''Cannot converge dbo.RegistrationSettingDrafts because more than one active draft exists for a settings scope. Resolve duplicate active drafts before deployment.'', 16, 1)
		end'
	)

	if exists
	(
		select 1
		from sys.indexes
		where object_id = object_id('dbo.RegistrationSettingDrafts')
			and name = 'UX_RSD_ActiveScope'
			and
			(
				is_unique <> 1
				or has_filter <> 1
				or filter_definition is null
				or filter_definition not like '%Status%Active%'
				or (select count(*) from sys.index_columns ic where ic.object_id = object_id('dbo.RegistrationSettingDrafts') and ic.index_id = sys.indexes.index_id and ic.key_ordinal > 0) <> 2
				or not exists
				(
					select 1
					from sys.index_columns ic
					inner join sys.columns c
						on c.object_id = ic.object_id
						and c.column_id = ic.column_id
					where ic.object_id = object_id('dbo.RegistrationSettingDrafts')
						and ic.index_id = sys.indexes.index_id
						and ic.key_ordinal = 1
						and c.name = 'OrganizationId'
				)
				or not exists
				(
					select 1
					from sys.index_columns ic
					inner join sys.columns c
						on c.object_id = ic.object_id
						and c.column_id = ic.column_id
					where ic.object_id = object_id('dbo.RegistrationSettingDrafts')
						and ic.index_id = sys.indexes.index_id
						and ic.key_ordinal = 2
						and c.name = 'FormCode'
				)
			)
	)
	begin
		drop index UX_RSD_ActiveScope on dbo.RegistrationSettingDrafts
	end

	if not exists
	(
		select 1
		from sys.indexes
		where object_id = object_id('dbo.RegistrationSettingDrafts')
			and name = 'UX_RSD_ActiveScope'
	)
	begin
		exec
		(
			N'create unique index UX_RSD_ActiveScope
			   on dbo.RegistrationSettingDrafts (OrganizationId, FormCode)
			   where Status = ''Active'''
		)
	end

	exec
	(
		N'update p
		set LiveSettingsGeneration = g.Generation
		from dbo.RegistrationSettingPreviewLinks p
		inner join dbo.RegistrationSettingDrafts draft
		on draft.DraftId = p.DraftId
		cross join dbo.RegistrationSettingsCacheGeneration g
		where p.AllowLiveSubmission = 1
			and p.LiveSettingsGeneration is null
			and p.RevokedAtUtc is null
			and draft.Status = ''Active''
			/* Do not first bind a link that the retired-key cleanup will revoke. */
			and not exists
			(
				select 1
				from dbo.RegistrationSettingDraftChanges draft_change
				inner join dbo.RegistrationSettingDrafts draft
					on draft.DraftId = draft_change.DraftId
				where draft_change.DraftId = p.DraftId
					and draft.Status = ''Active''
					and
					(
						draft_change.SettingKey = ''header_image_url''
						or draft_change.SettingKey in
						(
							''legal_name_checkbox_label'',
							''ecard_checkbox_label'',
							''mailing_list_checkbox_label'',
							''require_preferred_pickup_location''
						)
					)
			)
		'
	)

	/* Normalize nullable values before tightening columns that have safe defaults. */
	if exists (select 1 from dbo.RegistrationFormCodeMetadata where OrganizationId is null)
	begin
		raiserror('dbo.RegistrationFormCodeMetadata contains a row without OrganizationId; the value cannot be inferred safely.', 16, 1)
	end
	if exists (select 1 from dbo.RegistrationFormCodeMetadata where FormCode is null)
	begin
		raiserror('dbo.RegistrationFormCodeMetadata contains a row without FormCode; the form code cannot be inferred safely.', 16, 1)
	end
	update dbo.RegistrationFormCodeMetadata set DisplayName = '' where DisplayName is null
	update dbo.RegistrationFormCodeMetadata set CreatedAtUtc = sysutcdatetime() where CreatedAtUtc is null
	update dbo.RegistrationFormCodeMetadata set CreatedBy = 'settings-administration.sql' where CreatedBy is null
	update dbo.RegistrationFormCodeMetadata set ModifiedAtUtc = sysutcdatetime() where ModifiedAtUtc is null
	update dbo.RegistrationFormCodeMetadata set ModifiedBy = 'settings-administration.sql' where ModifiedBy is null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormCodeMetadata')
			and name = 'OrganizationId'
			and is_nullable = 1
	)
		alter table dbo.RegistrationFormCodeMetadata alter column OrganizationId int not null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormCodeMetadata')
			and name = 'FormCode'
			and is_nullable = 1
	)
		alter table dbo.RegistrationFormCodeMetadata alter column FormCode nvarchar(64) not null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormCodeMetadata')
			and name = 'DisplayName'
			and is_nullable = 1
	)
		alter table dbo.RegistrationFormCodeMetadata alter column DisplayName nvarchar(200) not null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormCodeMetadata')
			and name = 'CreatedAtUtc'
			and is_nullable = 1
	)
		alter table dbo.RegistrationFormCodeMetadata alter column CreatedAtUtc datetime2(7) not null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormCodeMetadata')
			and name = 'CreatedBy'
			and is_nullable = 1
	)
		alter table dbo.RegistrationFormCodeMetadata alter column CreatedBy nvarchar(256) not null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormCodeMetadata')
			and name = 'ModifiedAtUtc'
			and is_nullable = 1
	)
		alter table dbo.RegistrationFormCodeMetadata alter column ModifiedAtUtc datetime2(7) not null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormCodeMetadata')
			and name = 'ModifiedBy'
			and is_nullable = 1
	)
		alter table dbo.RegistrationFormCodeMetadata alter column ModifiedBy nvarchar(256) not null

	if exists (select 1 from dbo.RegistrationSettingScopeVersions where OrganizationId is null)
	begin
		raiserror('dbo.RegistrationSettingScopeVersions contains a row without OrganizationId; the value cannot be inferred safely.', 16, 1)
	end
	if exists
	(
		select OrganizationId, coalesce(FormCode, '')
		from dbo.RegistrationSettingScopeVersions
		group by OrganizationId, coalesce(FormCode, '')
		having count(*) > 1
	)
	begin
		raiserror('dbo.RegistrationSettingScopeVersions contains duplicate settings scopes after NULL FormCode normalization; resolve them before deployment.', 16, 1)
	end
	update dbo.RegistrationSettingScopeVersions set FormCode = '' where FormCode is null
	update dbo.RegistrationSettingScopeVersions set Version = 0 where Version is null
	update dbo.RegistrationSettingScopeVersions set ModifiedAtUtc = sysutcdatetime() where ModifiedAtUtc is null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingScopeVersions')
			and name = 'OrganizationId'
			and is_nullable = 1
	)
		alter table dbo.RegistrationSettingScopeVersions alter column OrganizationId int not null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingScopeVersions')
			and name = 'FormCode'
			and is_nullable = 1
	)
		alter table dbo.RegistrationSettingScopeVersions alter column FormCode nvarchar(64) not null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingScopeVersions')
			and name = 'Version'
			and is_nullable = 1
	)
		alter table dbo.RegistrationSettingScopeVersions alter column Version bigint not null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingScopeVersions')
			and name = 'ModifiedAtUtc'
			and is_nullable = 1
	)
		alter table dbo.RegistrationSettingScopeVersions alter column ModifiedAtUtc datetime2(7) not null

	if exists (select 1 from dbo.RegistrationSettingDrafts where OrganizationId is null)
	begin
		raiserror('dbo.RegistrationSettingDrafts contains a row without OrganizationId; the value cannot be inferred safely.', 16, 1)
	end
	exec
	(
		N'if exists
		(
			select OrganizationId, coalesce(FormCode, ''''), Status
			from dbo.RegistrationSettingDrafts
			where Status = ''Active''
			group by OrganizationId, coalesce(FormCode, ''''), Status
			having count(*) > 1
		)
		begin
			raiserror(''dbo.RegistrationSettingDrafts contains duplicate active scopes after NULL FormCode normalization; resolve them before deployment.'', 16, 1)
		end'
	)
	update dbo.RegistrationSettingDrafts set FormCode = '' where FormCode is null
	update dbo.RegistrationSettingDrafts set BaselineVersion = 0 where BaselineVersion is null
	exec
	(
		'update dbo.RegistrationSettingDrafts
		 set Revision = 0
		 where Revision is null'
	)
	update dbo.RegistrationSettingDrafts set CreatedAtUtc = sysutcdatetime() where CreatedAtUtc is null
	update dbo.RegistrationSettingDrafts set CreatedBy = 'settings-administration.sql' where CreatedBy is null
	update dbo.RegistrationSettingDrafts set ModifiedAtUtc = sysutcdatetime() where ModifiedAtUtc is null
	update dbo.RegistrationSettingDrafts set ModifiedBy = 'settings-administration.sql' where ModifiedBy is null
	exec
	(
		N'if exists (select 1 from dbo.RegistrationSettingDrafts where Status is null)
		begin
			raiserror(''dbo.RegistrationSettingDrafts contains a row without Status; the state cannot be inferred safely.'', 16, 1)
		end'
	)
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingDrafts')
			and name in ('OrganizationId', 'FormCode', 'BaselineVersion', 'Revision', 'Status', 'CreatedAtUtc', 'CreatedBy', 'ModifiedAtUtc', 'ModifiedBy')
			and is_nullable = 1
	)
	begin
		alter table dbo.RegistrationSettingDrafts alter column OrganizationId int not null
		alter table dbo.RegistrationSettingDrafts alter column FormCode nvarchar(64) not null
		alter table dbo.RegistrationSettingDrafts alter column BaselineVersion bigint not null
		alter table dbo.RegistrationSettingDrafts alter column Revision bigint not null
		exec (N'alter table dbo.RegistrationSettingDrafts alter column Status varchar(16) not null')
		alter table dbo.RegistrationSettingDrafts alter column CreatedAtUtc datetime2(7) not null
		alter table dbo.RegistrationSettingDrafts alter column CreatedBy nvarchar(256) not null
		alter table dbo.RegistrationSettingDrafts alter column ModifiedAtUtc datetime2(7) not null
		alter table dbo.RegistrationSettingDrafts alter column ModifiedBy nvarchar(256) not null
	end

	if exists
	(
		select 1
		from dbo.RegistrationSettingDraftChanges
		where DraftId is null or SettingKey is null or Operation is null
	)
	begin
		raiserror('dbo.RegistrationSettingDraftChanges contains a row without a required identity, key, or operation; the row cannot be repaired safely.', 16, 1)
	end
	if exists
	(
		select DraftId, SettingKey
		from dbo.RegistrationSettingDraftChanges
		group by DraftId, SettingKey
		having count(*) > 1
	)
	begin
		raiserror('dbo.RegistrationSettingDraftChanges contains duplicate DraftId/SettingKey rows; resolve them before deployment.', 16, 1)
	end
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingDraftChanges')
			and name = 'ModifiedAtUtc'
			and is_nullable = 1
	)
	begin
		update dbo.RegistrationSettingDraftChanges set ModifiedAtUtc = sysutcdatetime() where ModifiedAtUtc is null
		alter table dbo.RegistrationSettingDraftChanges alter column ModifiedAtUtc datetime2(7) not null
	end
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingDraftChanges')
			and name = 'ModifiedBy'
			and is_nullable = 1
	)
	begin
		update dbo.RegistrationSettingDraftChanges set ModifiedBy = 'settings-administration.sql' where ModifiedBy is null
		alter table dbo.RegistrationSettingDraftChanges alter column ModifiedBy nvarchar(256) not null
	end

	if exists
	(
		select 1
		from dbo.RegistrationSettingPreviewLinks
		where DraftId is null or TokenHash is null
	)
	begin
		raiserror('dbo.RegistrationSettingPreviewLinks contains a row without a required key or token; the row cannot be repaired safely.', 16, 1)
	end
	if exists
	(
		select TokenHash
		from dbo.RegistrationSettingPreviewLinks
		group by TokenHash
		having count(*) > 1
	)
	begin
		raiserror('dbo.RegistrationSettingPreviewLinks contains duplicate token hashes; resolve them before deployment.', 16, 1)
	end
	update dbo.RegistrationSettingPreviewLinks set AllowLiveSubmission = 0 where AllowLiveSubmission is null
	update dbo.RegistrationSettingPreviewLinks set CreatedAtUtc = sysutcdatetime() where CreatedAtUtc is null
	update dbo.RegistrationSettingPreviewLinks set CreatedBy = 'settings-administration.sql' where CreatedBy is null
	update dbo.RegistrationSettingPreviewLinks set ModifiedAtUtc = sysutcdatetime() where ModifiedAtUtc is null
	update dbo.RegistrationSettingPreviewLinks set ModifiedBy = 'settings-administration.sql' where ModifiedBy is null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and name in ('DraftId', 'TokenHash', 'OperationalBranchId', 'AllowLiveSubmission', 'CreatedAtUtc', 'CreatedBy', 'ModifiedAtUtc', 'ModifiedBy')
			and is_nullable = 1
	)
	begin
		alter table dbo.RegistrationSettingPreviewLinks alter column DraftId bigint not null
		alter table dbo.RegistrationSettingPreviewLinks alter column TokenHash binary(32) not null
		alter table dbo.RegistrationSettingPreviewLinks alter column OperationalBranchId int not null
		alter table dbo.RegistrationSettingPreviewLinks alter column AllowLiveSubmission bit not null
		alter table dbo.RegistrationSettingPreviewLinks alter column CreatedAtUtc datetime2(7) not null
		alter table dbo.RegistrationSettingPreviewLinks alter column CreatedBy nvarchar(256) not null
		alter table dbo.RegistrationSettingPreviewLinks alter column ModifiedAtUtc datetime2(7) not null
		alter table dbo.RegistrationSettingPreviewLinks alter column ModifiedBy nvarchar(256) not null
	end

	if exists
	(
		select 1
		from dbo.RegistrationSettingAuditEvents
		where EventType is null or TargetOrganizationId is null or Succeeded is null
	)
	begin
		raiserror('dbo.RegistrationSettingAuditEvents contains a row without a required event, target, or success value; the row cannot be repaired safely.', 16, 1)
	end
	update dbo.RegistrationSettingAuditEvents set TimestampUtc = sysutcdatetime() where TimestampUtc is null
	update dbo.RegistrationSettingAuditEvents set FormCode = '' where FormCode is null
	update dbo.RegistrationSettingAuditEvents set IsSensitive = 0 where IsSensitive is null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and name in ('TimestampUtc', 'FormCode', 'IsSensitive')
			and is_nullable = 1
	)
	begin
		alter table dbo.RegistrationSettingAuditEvents alter column TimestampUtc datetime2(7) not null
		alter table dbo.RegistrationSettingAuditEvents alter column FormCode nvarchar(64) not null
		alter table dbo.RegistrationSettingAuditEvents alter column IsSensitive bit not null
	end

	if exists
	(
		select 1
		from dbo.RegistrationSettingsCacheGeneration
		where Id is null or Generation is null or ModifiedAtUtc is null
	)
	begin
		raiserror('dbo.RegistrationSettingsCacheGeneration contains a NULL singleton value; repair the row before deployment.', 16, 1)
	end

	if exists
	(
		select 1
		from dbo.RegistrationFormAssets
		where AssetId is null or FileName is null or ContentType is null or Content is null
			or ContentHash is null
	)
	begin
		raiserror('dbo.RegistrationFormAssets contains a row without required asset data; the row cannot be repaired safely.', 16, 1)
	end
	update dbo.RegistrationFormAssets set CreatedDate = sysutcdatetime() where CreatedDate is null
	update dbo.RegistrationFormAssets set ModifiedDate = sysutcdatetime() where ModifiedDate is null
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormAssets')
			and name in ('CreatedDate', 'ModifiedDate')
			and is_nullable = 1
	)
	begin
		alter table dbo.RegistrationFormAssets alter column CreatedDate datetime2(7) not null
		alter table dbo.RegistrationFormAssets alter column ModifiedDate datetime2(7) not null
	end

	/* Widen additive counters when an older deployment used an integer type. */
	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingScopeVersions')
			and name = 'Version'
			and system_type_id <> 127
	)
	begin
		if exists
		(
			select 1
			from dbo.RegistrationSettingScopeVersions
			where Version is not null and try_convert(bigint, Version) is null
		)
		begin
			raiserror('dbo.RegistrationSettingScopeVersions contains a Version that cannot be converted to bigint safely.', 16, 1)
		end
		alter table dbo.RegistrationSettingScopeVersions alter column Version bigint not null
	end

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingDrafts')
			and name in ('BaselineVersion', 'Revision')
			and system_type_id <> 127
	)
	begin
		declare @invalid_draft_counter bit = 0
		exec sys.sp_executesql
			N'if exists
			(
				select 1
				from dbo.RegistrationSettingDrafts
				where try_convert(bigint, BaselineVersion) is null
					or try_convert(bigint, Revision) is null
			)
				set @invalid = 1',
			N'@invalid bit output',
			@invalid = @invalid_draft_counter output

		if @invalid_draft_counter = 1
		begin
			raiserror('dbo.RegistrationSettingDrafts contains a version or revision that cannot be converted to bigint safely.', 16, 1)
		end
		alter table dbo.RegistrationSettingDrafts alter column BaselineVersion bigint not null
		exec (N'alter table dbo.RegistrationSettingDrafts alter column Revision bigint not null')
	end

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and name = 'LiveSettingsGeneration'
			and system_type_id <> 127
	)
	begin
		if exists
		(
			select 1
			from dbo.RegistrationSettingPreviewLinks
			where LiveSettingsGeneration is not null
				and try_convert(bigint, LiveSettingsGeneration) is null
		)
		begin
			raiserror('dbo.RegistrationSettingPreviewLinks contains a live-settings generation that cannot be converted to bigint safely.', 16, 1)
		end
		alter table dbo.RegistrationSettingPreviewLinks alter column LiveSettingsGeneration bigint null
	end

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingsCacheGeneration')
			and name = 'Generation'
			and system_type_id <> 127
	)
	begin
		if exists
		(
			select 1
			from dbo.RegistrationSettingsCacheGeneration
			where Generation is not null and try_convert(bigint, Generation) is null
		)
		begin
			raiserror('dbo.RegistrationSettingsCacheGeneration contains a Generation that cannot be converted to bigint safely.', 16, 1)
		end
		alter table dbo.RegistrationSettingsCacheGeneration alter column Generation bigint not null
	end

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and name = 'OperationalBranchId'
			and system_type_id <> 56
	)
	begin
		if exists
		(
			select 1
			from dbo.RegistrationSettingPreviewLinks
			where OperationalBranchId is not null
				and try_convert(int, OperationalBranchId) is null
		)
		begin
			raiserror('dbo.RegistrationSettingPreviewLinks contains an operational branch that cannot be converted to int safely.', 16, 1)
		end
		alter table dbo.RegistrationSettingPreviewLinks alter column OperationalBranchId int not null
	end

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and name in ('PreviousValue', 'NewValue')
			and (system_type_id <> 231 or max_length <> -1 or is_nullable <> 1)
	)
	begin
		alter table dbo.RegistrationSettingAuditEvents alter column PreviousValue nvarchar(max) null
		alter table dbo.RegistrationSettingAuditEvents alter column NewValue nvarchar(max) null
	end

	/* Restore defaults that older or manually-created copies may be missing. */
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationFormCodeMetadata')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationFormCodeMetadata'), 'CreatedAtUtc', 'ColumnId')
	)
		alter table dbo.RegistrationFormCodeMetadata add constraint DF_RFCode_Created default sysutcdatetime() for CreatedAtUtc
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationFormCodeMetadata')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationFormCodeMetadata'), 'ModifiedAtUtc', 'ColumnId')
	)
		alter table dbo.RegistrationFormCodeMetadata add constraint DF_RFCode_Modified default sysutcdatetime() for ModifiedAtUtc

	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingScopeVersions')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingScopeVersions'), 'FormCode', 'ColumnId')
	)
		alter table dbo.RegistrationSettingScopeVersions add constraint DF_RSSV_Code default '' for FormCode
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingScopeVersions')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingScopeVersions'), 'Version', 'ColumnId')
	)
		alter table dbo.RegistrationSettingScopeVersions add constraint DF_RSSV_Version default 0 for Version
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingScopeVersions')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingScopeVersions'), 'ModifiedAtUtc', 'ColumnId')
	)
		alter table dbo.RegistrationSettingScopeVersions add constraint DF_RSSV_Modified default sysutcdatetime() for ModifiedAtUtc

	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingDrafts')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingDrafts'), 'FormCode', 'ColumnId')
	)
		alter table dbo.RegistrationSettingDrafts add constraint DF_RSD_Code default '' for FormCode
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingDrafts')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingDrafts'), 'Revision', 'ColumnId')
	)
		alter table dbo.RegistrationSettingDrafts add constraint DF_RSD_Revision default 0 for Revision
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingDrafts')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingDrafts'), 'CreatedAtUtc', 'ColumnId')
	)
		alter table dbo.RegistrationSettingDrafts add constraint DF_RSD_Created default sysutcdatetime() for CreatedAtUtc
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingDrafts')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingDrafts'), 'ModifiedAtUtc', 'ColumnId')
	)
		alter table dbo.RegistrationSettingDrafts add constraint DF_RSD_Modified default sysutcdatetime() for ModifiedAtUtc

	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingDraftChanges')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingDraftChanges'), 'ModifiedAtUtc', 'ColumnId')
	)
		alter table dbo.RegistrationSettingDraftChanges add constraint DF_RSDC_Modified default sysutcdatetime() for ModifiedAtUtc

	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingPreviewLinks'), 'AllowLiveSubmission', 'ColumnId')
	)
		alter table dbo.RegistrationSettingPreviewLinks add constraint DF_RSPL_Live default 0 for AllowLiveSubmission
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingPreviewLinks'), 'CreatedAtUtc', 'ColumnId')
	)
		alter table dbo.RegistrationSettingPreviewLinks add constraint DF_RSPL_Created default sysutcdatetime() for CreatedAtUtc
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingPreviewLinks'), 'ModifiedAtUtc', 'ColumnId')
	)
		alter table dbo.RegistrationSettingPreviewLinks add constraint DF_RSPL_Modified default sysutcdatetime() for ModifiedAtUtc

	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingAuditEvents'), 'TimestampUtc', 'ColumnId')
	)
		alter table dbo.RegistrationSettingAuditEvents add constraint DF_RSAE_Time default sysutcdatetime() for TimestampUtc
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingAuditEvents'), 'FormCode', 'ColumnId')
	)
		alter table dbo.RegistrationSettingAuditEvents add constraint DF_RSAE_Code default '' for FormCode
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationSettingAuditEvents'), 'IsSensitive', 'ColumnId')
	)
		alter table dbo.RegistrationSettingAuditEvents add constraint DF_RSAE_Secret default 0 for IsSensitive

	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationFormAssets')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationFormAssets'), 'CreatedDate', 'ColumnId')
	)
		alter table dbo.RegistrationFormAssets add constraint DF_RegistrationFormAssets_CreatedDate default sysutcdatetime() for CreatedDate
	if not exists
	(
		select 1
		from sys.default_constraints
		where parent_object_id = object_id('dbo.RegistrationFormAssets')
			and parent_column_id = columnproperty(object_id('dbo.RegistrationFormAssets'), 'ModifiedDate', 'ColumnId')
	)
		alter table dbo.RegistrationFormAssets add constraint DF_RegistrationFormAssets_ModifiedDate default sysutcdatetime() for ModifiedDate

	/* Recreate missing patron-registration-owned keys and relationships. */
	if not exists
	(
		select 1 from sys.key_constraints
		where parent_object_id = object_id('dbo.RegistrationFormCodeMetadata') and type = 'PK'
	)
		alter table dbo.RegistrationFormCodeMetadata add constraint PK_RegistrationFormCodeMetadata primary key (OrganizationId, FormCode)
	if not exists
	(
		select 1 from sys.key_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingScopeVersions') and type = 'PK'
	)
		alter table dbo.RegistrationSettingScopeVersions add constraint PK_RegistrationSettingScopeVersions primary key (OrganizationId, FormCode)
	if not exists
	(
		select 1 from sys.key_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingDrafts') and type = 'PK'
	)
		alter table dbo.RegistrationSettingDrafts add constraint PK_RegistrationSettingDrafts primary key (DraftId)
	if not exists
	(
		select 1 from sys.key_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingDraftChanges') and type = 'PK'
	)
		alter table dbo.RegistrationSettingDraftChanges add constraint PK_RegistrationSettingDraftChanges primary key (DraftChangeId)
	if not exists
	(
		select 1 from sys.key_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingPreviewLinks') and type = 'PK'
	)
		alter table dbo.RegistrationSettingPreviewLinks add constraint PK_RegistrationSettingPreviewLinks primary key (PreviewLinkId)
	if not exists
	(
		select 1 from sys.key_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingAuditEvents') and type = 'PK'
	)
		alter table dbo.RegistrationSettingAuditEvents add constraint PK_RegistrationSettingAuditEvents primary key (AuditEventId)
	if not exists
	(
		select 1 from sys.key_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingsCacheGeneration') and type = 'PK'
	)
		alter table dbo.RegistrationSettingsCacheGeneration add constraint PK_RegistrationSettingsCacheGeneration primary key (Id)
	if not exists
	(
		select 1 from sys.key_constraints
		where parent_object_id = object_id('dbo.RegistrationFormAssets') and type = 'PK'
	)
		alter table dbo.RegistrationFormAssets add constraint PK_RegistrationFormAssets primary key (AssetId)
	if not exists
	(
		select 1 from sys.key_constraints
		where parent_object_id = object_id('dbo.RegistrationFormAssetReferenceLocks') and type = 'PK'
	)
		alter table dbo.RegistrationFormAssetReferenceLocks add constraint PK_RegistrationFormAssetReferenceLocks primary key (LockId)

	if not exists
	(
		select 1
		from sys.indexes i
		where i.object_id = object_id('dbo.RegistrationSettingDraftChanges')
			and i.is_unique = 1
			and
			(
				select count(*) from sys.index_columns ic
				where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal > 0
			) = 2
			and
			(
				select c.name from sys.index_columns ic inner join sys.columns c
					on c.object_id = ic.object_id and c.column_id = ic.column_id
				where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 1
			) = 'DraftId'
			and
			(
				select c.name from sys.index_columns ic inner join sys.columns c
					on c.object_id = ic.object_id and c.column_id = ic.column_id
				where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 2
			) = 'SettingKey'
	)
		alter table dbo.RegistrationSettingDraftChanges add constraint UQ_RSDC_Key unique (DraftId, SettingKey)

	if not exists
	(
		select 1
		from sys.indexes i
		where i.object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and i.is_unique = 1
			and
			(
				select count(*) from sys.index_columns ic
				where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal > 0
			) = 1
			and
			(
				select c.name from sys.index_columns ic inner join sys.columns c
					on c.object_id = ic.object_id and c.column_id = ic.column_id
				where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 1
			) = 'TokenHash'
	)
		alter table dbo.RegistrationSettingPreviewLinks add constraint UQ_RSPL_Token unique (TokenHash)

	if not exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id = object_id('dbo.RegistrationFormCodeMetadata') and name = 'CK_RFCode_NotBlank'
	)
		alter table dbo.RegistrationFormCodeMetadata with check add constraint CK_RFCode_NotBlank check (len(FormCode) > 0)
	if not exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingDrafts') and name = 'CK_RSD_Status'
	)
		exec (N'alter table dbo.RegistrationSettingDrafts with check add constraint CK_RSD_Status check (Status in (''Active'', ''Committed'', ''Discarded'', ''Invalidated''))')
	if not exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingDraftChanges') and name = 'CK_RSDC_Operation'
	)
		alter table dbo.RegistrationSettingDraftChanges with check add constraint CK_RSDC_Operation check (Operation in ('Upsert', 'RemoveOverride'))
	if not exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingDraftChanges') and name = 'CK_RSDC_Value'
	)
		alter table dbo.RegistrationSettingDraftChanges with check add constraint CK_RSDC_Value check
		(
			(Operation = 'Upsert' and Value is not null)
			or (Operation = 'RemoveOverride' and Value is null)
		)
	if not exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id = object_id('dbo.RegistrationSettingAuditEvents') and name = 'CK_RSAE_Json'
	)
		alter table dbo.RegistrationSettingAuditEvents with check add constraint CK_RSAE_Json check (MetadataJson is null or isjson(MetadataJson) = 1)
	if not exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id = object_id('dbo.RegistrationFormAssetReferenceLocks') and name = 'CK_RegistrationFormAssetReferenceLocks_Singleton'
	)
		alter table dbo.RegistrationFormAssetReferenceLocks with check add constraint CK_RegistrationFormAssetReferenceLocks_Singleton check (LockId = 1)
	if not exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id = object_id('dbo.RegistrationFormAssets') and name = 'CK_RegistrationFormAssets_FileName_NotBlank'
	)
		alter table dbo.RegistrationFormAssets with check add constraint CK_RegistrationFormAssets_FileName_NotBlank check (len(ltrim(rtrim(FileName))) > 0)
	if not exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id = object_id('dbo.RegistrationFormAssets') and name = 'CK_RegistrationFormAssets_ContentType_NotBlank'
	)
		alter table dbo.RegistrationFormAssets with check add constraint CK_RegistrationFormAssets_ContentType_NotBlank check (len(ltrim(rtrim(ContentType))) > 0)
	if not exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id = object_id('dbo.RegistrationFormAssets') and name = 'CK_RegistrationFormAssets_Content_NotEmpty'
	)
		alter table dbo.RegistrationFormAssets with check add constraint CK_RegistrationFormAssets_Content_NotEmpty check (datalength(Content) > 0)
	if not exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id = object_id('dbo.RegistrationFormAssets') and name = 'CK_RegistrationFormAssets_ContentHash_Sha256'
	)
		alter table dbo.RegistrationFormAssets with check add constraint CK_RegistrationFormAssets_ContentHash_Sha256 check (len(ContentHash) = 64)

	if not exists
	(
		select 1
		from sys.foreign_keys fk
		inner join sys.foreign_key_columns fkc
			on fkc.constraint_object_id = fk.object_id
		where fk.parent_object_id = object_id('dbo.RegistrationSettingDraftChanges')
			and fk.referenced_object_id = object_id('dbo.RegistrationSettingDrafts')
			and fk.delete_referential_action = 1
			and fk.is_disabled = 0
			and fk.is_not_trusted = 0
			and fkc.parent_column_id = columnproperty(object_id('dbo.RegistrationSettingDraftChanges'), 'DraftId', 'ColumnId')
			and fkc.referenced_column_id = columnproperty(object_id('dbo.RegistrationSettingDrafts'), 'DraftId', 'ColumnId')
	)
	begin
		if exists (select 1 from sys.foreign_keys where parent_object_id = object_id('dbo.RegistrationSettingDraftChanges') and name = 'FK_RSDC_Draft')
			alter table dbo.RegistrationSettingDraftChanges drop constraint FK_RSDC_Draft
		alter table dbo.RegistrationSettingDraftChanges with check add constraint FK_RSDC_Draft foreign key (DraftId)
			references dbo.RegistrationSettingDrafts (DraftId) on delete cascade
	end

	if not exists
	(
		select 1
		from sys.foreign_keys fk
		inner join sys.foreign_key_columns fkc
			on fkc.constraint_object_id = fk.object_id
		where fk.parent_object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and fk.referenced_object_id = object_id('dbo.RegistrationSettingDrafts')
			and fk.delete_referential_action = 1
			and fk.is_disabled = 0
			and fk.is_not_trusted = 0
			and fkc.parent_column_id = columnproperty(object_id('dbo.RegistrationSettingPreviewLinks'), 'DraftId', 'ColumnId')
			and fkc.referenced_column_id = columnproperty(object_id('dbo.RegistrationSettingDrafts'), 'DraftId', 'ColumnId')
	)
	begin
		if exists (select 1 from sys.foreign_keys where parent_object_id = object_id('dbo.RegistrationSettingPreviewLinks') and name = 'FK_RSPL_Draft')
			alter table dbo.RegistrationSettingPreviewLinks drop constraint FK_RSPL_Draft
		alter table dbo.RegistrationSettingPreviewLinks with check add constraint FK_RSPL_Draft foreign key (DraftId)
			references dbo.RegistrationSettingDrafts (DraftId) on delete cascade
	end

	/* Repair named access paths when a partially-updated copy left a wrong index behind. */
	if exists
	(
		select 1
		from sys.indexes i
		where i.object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and i.name = 'IX_RSAE_LibraryTime'
			and
			(
				i.is_unique <> 0
				or (select count(*) from sys.index_columns ic where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal > 0) <> 2
				or not exists (select 1 from sys.index_columns ic inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 1 and c.name = 'TargetLibraryId')
				or not exists (select 1 from sys.index_columns ic inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 2 and c.name = 'TimestampUtc')
			)
	)
		drop index IX_RSAE_LibraryTime on dbo.RegistrationSettingAuditEvents
	if not exists (select 1 from sys.indexes where object_id = object_id('dbo.RegistrationSettingAuditEvents') and name = 'IX_RSAE_LibraryTime')
		create index IX_RSAE_LibraryTime on dbo.RegistrationSettingAuditEvents (TargetLibraryId, TimestampUtc desc) include (EventType, TargetOrganizationId, FormCode)

	if exists
	(
		select 1
		from sys.indexes i
		where i.object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and i.name = 'IX_RSAE_ScopeFilter'
			and
			(
				i.is_unique <> 0
				or (select count(*) from sys.index_columns ic where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal > 0) <> 4
				or not exists (select 1 from sys.index_columns ic inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 1 and c.name = 'TargetOrganizationId')
				or not exists (select 1 from sys.index_columns ic inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 2 and c.name = 'FormCode')
				or not exists (select 1 from sys.index_columns ic inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 3 and c.name = 'EventType')
				or not exists (select 1 from sys.index_columns ic inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 4 and c.name = 'TimestampUtc')
			)
	)
		drop index IX_RSAE_ScopeFilter on dbo.RegistrationSettingAuditEvents
	if not exists (select 1 from sys.indexes where object_id = object_id('dbo.RegistrationSettingAuditEvents') and name = 'IX_RSAE_ScopeFilter')
		create index IX_RSAE_ScopeFilter on dbo.RegistrationSettingAuditEvents (TargetOrganizationId, FormCode, EventType, TimestampUtc desc)

	if exists
	(
		select 1
		from sys.indexes i
		where i.object_id = object_id('dbo.RegistrationFormAssets')
			and i.name = 'IX_RegistrationFormAssets_UploadScope'
			and
			(
				i.is_unique <> 0
				or (select count(*) from sys.index_columns ic where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal > 0) <> 2
				or not exists (select 1 from sys.index_columns ic inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 1 and c.name = 'UploadOrganizationId')
				or not exists (select 1 from sys.index_columns ic inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 2 and c.name = 'UploadFormCode')
			)
	)
		drop index IX_RegistrationFormAssets_UploadScope on dbo.RegistrationFormAssets
	if not exists (select 1 from sys.indexes where object_id = object_id('dbo.RegistrationFormAssets') and name = 'IX_RegistrationFormAssets_UploadScope')
		create index IX_RegistrationFormAssets_UploadScope on dbo.RegistrationFormAssets (UploadOrganizationId, UploadFormCode)

	if exists
	(
		select 1
		from sys.indexes i
		where i.object_id = object_id('dbo.RegistrationFormAssets')
			and i.name = 'IX_RegistrationFormAssets_CreatedDate'
			and
			(
				i.is_unique <> 0
				or (select count(*) from sys.index_columns ic where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal > 0) <> 1
				or not exists (select 1 from sys.index_columns ic inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 1 and c.name = 'CreatedDate')
			)
	)
		drop index IX_RegistrationFormAssets_CreatedDate on dbo.RegistrationFormAssets
	if not exists (select 1 from sys.indexes where object_id = object_id('dbo.RegistrationFormAssets') and name = 'IX_RegistrationFormAssets_CreatedDate')
		create index IX_RegistrationFormAssets_CreatedDate on dbo.RegistrationFormAssets (CreatedDate)

	/* Re-trust existing checks after a manual or interrupted deployment. */
	if exists (select 1 from sys.check_constraints where parent_object_id = object_id('dbo.RegistrationFormCodeMetadata') and name = 'CK_RFCode_NotBlank' and (is_disabled = 1 or is_not_trusted = 1))
		alter table dbo.RegistrationFormCodeMetadata with check check constraint CK_RFCode_NotBlank
	if exists (select 1 from sys.check_constraints where parent_object_id = object_id('dbo.RegistrationSettingDrafts') and name = 'CK_RSD_Status' and (is_disabled = 1 or is_not_trusted = 1))
		alter table dbo.RegistrationSettingDrafts with check check constraint CK_RSD_Status
	if exists (select 1 from sys.check_constraints where parent_object_id = object_id('dbo.RegistrationSettingDraftChanges') and name = 'CK_RSDC_Operation' and (is_disabled = 1 or is_not_trusted = 1))
		alter table dbo.RegistrationSettingDraftChanges with check check constraint CK_RSDC_Operation
	if exists (select 1 from sys.check_constraints where parent_object_id = object_id('dbo.RegistrationSettingDraftChanges') and name = 'CK_RSDC_Value' and (is_disabled = 1 or is_not_trusted = 1))
		alter table dbo.RegistrationSettingDraftChanges with check check constraint CK_RSDC_Value
	if exists (select 1 from sys.check_constraints where parent_object_id = object_id('dbo.RegistrationSettingAuditEvents') and name = 'CK_RSAE_Json' and (is_disabled = 1 or is_not_trusted = 1))
		alter table dbo.RegistrationSettingAuditEvents with check check constraint CK_RSAE_Json
	if exists (select 1 from sys.check_constraints where parent_object_id = object_id('dbo.RegistrationFormAssets') and name = 'CK_RegistrationFormAssets_FileName_NotBlank' and (is_disabled = 1 or is_not_trusted = 1))
		alter table dbo.RegistrationFormAssets with check check constraint CK_RegistrationFormAssets_FileName_NotBlank
	if exists (select 1 from sys.check_constraints where parent_object_id = object_id('dbo.RegistrationFormAssets') and name = 'CK_RegistrationFormAssets_ContentType_NotBlank' and (is_disabled = 1 or is_not_trusted = 1))
		alter table dbo.RegistrationFormAssets with check check constraint CK_RegistrationFormAssets_ContentType_NotBlank
	if exists (select 1 from sys.check_constraints where parent_object_id = object_id('dbo.RegistrationFormAssets') and name = 'CK_RegistrationFormAssets_Content_NotEmpty' and (is_disabled = 1 or is_not_trusted = 1))
		alter table dbo.RegistrationFormAssets with check check constraint CK_RegistrationFormAssets_Content_NotEmpty
	if exists (select 1 from sys.check_constraints where parent_object_id = object_id('dbo.RegistrationFormAssets') and name = 'CK_RegistrationFormAssets_ContentHash_Sha256' and (is_disabled = 1 or is_not_trusted = 1))
		alter table dbo.RegistrationFormAssets with check check constraint CK_RegistrationFormAssets_ContentHash_Sha256
	if exists (select 1 from sys.check_constraints where parent_object_id = object_id('dbo.RegistrationFormAssetReferenceLocks') and name = 'CK_RegistrationFormAssetReferenceLocks_Singleton' and (is_disabled = 1 or is_not_trusted = 1))
		alter table dbo.RegistrationFormAssetReferenceLocks with check check constraint CK_RegistrationFormAssetReferenceLocks_Singleton

	if object_id('tempdb..#SettingsAdministrationSettingMap') is not null
		drop table #SettingsAdministrationSettingMap

	create table #SettingsAdministrationSettingMap
	(
		LegacyKey nvarchar(200) not null primary key,
		ReplacementKey nvarchar(200) not null unique
	)

	declare @required_setting_types table
	(
		Setting nvarchar(200) not null primary key
	)

	insert into #SettingsAdministrationSettingMap
	(
		LegacyKey,
		ReplacementKey
	)
	values
		('legal_name_checkbox_label', 'label.UseLegalName'),
		('ecard_checkbox_label', 'label.IsECard'),
		('mailing_list_checkbox_label', 'label.AddToMailingList'),
		('require_preferred_pickup_location', 'require.RequestPickupBranchID')

	/*
	   Capture active drafts before the legacy-key and retired-header cleanup.
	   The primary key ensures one revision bump per draft for this convergence
	   run, even when several retired mutations are present.
	*/
	if object_id('tempdb..#SettingsAdministrationChangedDrafts') is not null
		drop table #SettingsAdministrationChangedDrafts

	create table #SettingsAdministrationChangedDrafts
	(
		DraftId bigint not null primary key
	)

	exec
	(
		N'insert #SettingsAdministrationChangedDrafts (DraftId)
		select distinct draft_change.DraftId
		from dbo.RegistrationSettingDraftChanges draft_change
		inner join dbo.RegistrationSettingDrafts draft
			on draft.DraftId = draft_change.DraftId
		where draft.Status = ''Active''
			and
			(
				draft_change.SettingKey = ''header_image_url''
				or draft_change.SettingKey in
				(
					select LegacyKey
					from #SettingsAdministrationSettingMap
				)
			)'
	)

	/* Compatibility-only setting types are intentionally omitted; catalog registration is insert-only and retains existing rows. */
	/* BEGIN SETTING_CATALOG_ALLOWLIST */
	insert into @required_setting_types
	(
		Setting
	)
	values
		('header_image_asset_id'),
		('css_file'),
		('warning_text'),
		('custom_form_footer_html'),
		('registration_text'),
		('registration_form_header'),
		('show_dl'),
        ('enable_age_warning'),
		('age_warning_text'),
		('enable_age_block'),
		('age_block_text'),
		('hide_ereceipt'),
        ('normalize_to_uppercase'),
		('dl_format'),
		('enable_legal_name_checkbox'),
		('drivers_license_button_text'),
		('drivers_license_prompt_text'),
		('agreement_confirm_button_text'),
		('agreement_cancel_button_text'),
		('school_info_field_legend'),
        ('responsible_person_disclaimer'),
		('display_responsible_person_field'),
		('phone_number_format'),
		('enable_patron_branch_select_option'),
		('display_preferred_pickup_location'),
		('teacher_patron_code_id'),
		('student_patron_code_id'),
		('patron_code_id'),
		('expiration_date'),
		('expiration_date_years'),
		('hide_branch_select_if_only_one_option'),
		('disable_branch'),
		('display_ecard_checkbox'),
		('ecard_patron_code_id'),
		('ecard_registration_text'),
		('ecard_barcode_prefix'),
		('force_ecard_remotely'),
		('display_mailing_list_checkbox'),
		('mailing_list_description_html'),
		('mailing_list_record_set_id'),
		('display_sms_notice_information'),
		('sms_notice_information_html'),
		('use_legal_name_on_notices'),
		('ecard_welcome_email_template_text'),
		('ecard_welcome_email_template_html'),
		('welcome_email_template_text'),
		('welcome_email_template_html'),
		('welcome_email_from_name'),
		('welcome_email_subject'),
		('welcome_email_from_address'),
		('ecard_welcome_email_subject'),
		('postmark_api_key'),
		('bypass_dupe_check'),
		('duplicate_patron_message_html'),
		('perform_papi_duplicate_bypass'),
		('use_first_name_for_duplicate_workaround'),
		('block_out_of_state_registrations'),
		('update_patron_record_with_melissa_address'),
		('melissa_data_api_key'),
		('valid_address_registration_text'),
		('valid_address_plus_name_registration_text'),
		('out_of_state_block_message'),
		('valid_address_patron_code_id'),
		('valid_address_plus_name_patron_code_id'),
		('valid_address_record_set_id'),
		('valid_address_plus_name_record_set_id'),
		('invalid_address_record_set_id'),
		('registration_logon_user_id'),
		('add_to_record_set_id'),
		('post_registration_note_text'),
		('show_dl_ips'),
		('reset_form'),
		('kiosk_registration_text'),
		('reset_seconds'),
		('alert.PatronBranchID'),
		('alert.NameFirst'),
		('alert.NameMiddle'),
		('alert.NameLast'),
		('alert.UseLegalName'),
		('alert.LegalNameFirst'),
		('alert.LegalNameMiddle'),
		('alert.LegalNameLast'),
		('alert.Birthdate'),
		('alert.DeliveryOptionId'),
		('alert.PhoneVoice1'),
		('alert.PhoneVoice2'),
		('alert.ReceiveEreceipts'),
		('alert.EmailAddress'),
		('alert.AltEmailAddress'),
		('alert.StreetOne'),
		('alert.StreetTwo'),
		('alert.City'),
		('alert.State'),
		('alert.PostalCode'),
		('alert.Password'),
		('alert.Password2'),
		('alert.RequestPickupBranchID'),
		('alert.User1'),
		('alert.User5'),
		('alert.DeliverCardToSchool'),
		('alert.IsStudent'),
		('alert.IsTeacher'),
		('alert.IsECard'),
		('alert.AddToMailingList'),
		('label.PatronBranchID'),
		('label.NameFirst'),
		('label.NameMiddle'),
		('label.NameLast'),
		('label.UseLegalName'),
		('label.LegalNameFirst'),
		('label.LegalNameMiddle'),
		('label.LegalNameLast'),
		('label.Birthdate'),
		('label.DeliveryOptionId'),
		('label.PhoneVoice1'),
		('label.PhoneVoice2'),
		('label.ReceiveEreceipts'),
		('label.EmailAddress'),
		('label.StreetOne'),
		('label.StreetTwo'),
		('label.City'),
		('label.State'),
		('label.User5'),
		('label.PostalCode'),
		('label.Password'),
		('label.Password2'),
		('label.RequestPickupBranchID'),
		('label.User1'),
		('label.DeliverCardToSchool'),
		('label.IsStudent'),
		('label.IsTeacher'),
		('label.IsECard'),
		('label.AddToMailingList'),
		('require.PhoneVoice1'),
		('require.EmailAddress'),
		('require.User5'),
		('require.RequestPickupBranchID')
	/* END SETTING_CATALOG_ALLOWLIST */

	insert into dbo.RegistrationFormSettingTypes
	(
		Setting
	)
	select required.Setting
	from @required_setting_types as required
	where not exists
	(
		select 1
		from dbo.RegistrationFormSettingTypes as existing
		where existing.Setting = required.Setting
	)

	insert into dbo.RegistrationFormSettings
	(
		OrganizationID,
		Setting,
		FormCode,
		Value
	)
	select
		legacy.OrganizationID,
		map.ReplacementKey,
		legacy.FormCode,
		legacy.Value
	from dbo.RegistrationFormSettings as legacy
	inner join #SettingsAdministrationSettingMap as map
	on map.LegacyKey = legacy.Setting
	where not exists
	(
		select 1
		from dbo.RegistrationFormSettings as replacement
		where replacement.OrganizationID = legacy.OrganizationID
			and replacement.FormCode = legacy.FormCode
			and replacement.Setting = map.ReplacementKey
	)

	exec
	(
		N'update draft_change
		set SettingKey = map.ReplacementKey
		from dbo.RegistrationSettingDraftChanges as draft_change
		inner join dbo.RegistrationSettingDrafts as draft
		on draft.DraftId = draft_change.DraftId
		inner join #SettingsAdministrationSettingMap as map
		on map.LegacyKey = draft_change.SettingKey
		where draft.Status = ''Active''
			and not exists
			(
				select 1
				from dbo.RegistrationSettingDraftChanges as replacement_change
				where replacement_change.DraftId = draft_change.DraftId
					and replacement_change.SettingKey = map.ReplacementKey
			)

		delete draft_change
		from dbo.RegistrationSettingDraftChanges as draft_change
		inner join dbo.RegistrationSettingDrafts as draft
		on draft.DraftId = draft_change.DraftId
		inner join #SettingsAdministrationSettingMap as map
		on map.LegacyKey = draft_change.SettingKey
		where draft.Status = ''Active''

		delete draft_change
		from dbo.RegistrationSettingDraftChanges as draft_change
		inner join dbo.RegistrationSettingDrafts as draft
		on draft.DraftId = draft_change.DraftId
		where draft_change.SettingKey = ''header_image_url''
			and draft.Status = ''Active'''
	)

	/*
	   The retired-key cleanup is an active-draft mutation. Invalidate every
	   still-active link for the changed drafts and advance each draft revision
	   once, in the same transaction. The convergence path creates Revision for
	   older schemas before reaching this point.
	*/
	update preview_link
	set RevokedAtUtc = sysutcdatetime(),
		RevokedBy = coalesce(RevokedBy, 'settings-administration.sql'),
		ModifiedAtUtc = sysutcdatetime(),
		ModifiedBy = 'settings-administration.sql'
	from dbo.RegistrationSettingPreviewLinks preview_link
	inner join #SettingsAdministrationChangedDrafts changed_draft
	on changed_draft.DraftId = preview_link.DraftId
	where preview_link.RevokedAtUtc is null

	exec
	(
		'update draft
		 set Revision = draft.Revision + 1
		 from dbo.RegistrationSettingDrafts draft
		 inner join #SettingsAdministrationChangedDrafts changed_draft
		 on changed_draft.DraftId = draft.DraftId
		 where draft.Status = ''Active'''
	)

	delete from dbo.RegistrationFormSettings
	where Setting = 'header_image_url'
		or Setting in
		(
			select LegacyKey
			from #SettingsAdministrationSettingMap
		)

	delete from dbo.RegistrationFormSettingTypes
	where Setting = 'header_image_url'
		or Setting in
		(
			select LegacyKey
			from #SettingsAdministrationSettingMap
		)

	if object_id('dbo.RegistrationFormCodeMetadata', 'U') is null
		or object_id('dbo.RegistrationSettingScopeVersions', 'U') is null
		or object_id('dbo.RegistrationSettingDrafts', 'U') is null
		or object_id('dbo.RegistrationSettingDraftChanges', 'U') is null
		or object_id('dbo.RegistrationSettingPreviewLinks', 'U') is null
		or object_id('dbo.RegistrationSettingAuditEvents', 'U') is null
		or object_id('dbo.RegistrationSettingsCacheGeneration', 'U') is null
		or object_id('dbo.RegistrationFormAssets', 'U') is null
		or object_id('dbo.RegistrationFormAssetReferenceLocks', 'U') is null
	begin
		raiserror('Settings administration deployment did not produce all required tables.', 16, 1)
	end

	if not exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingPreviewLinks')
			and name = 'OperationalBranchId'
			and is_nullable = 0
	)
	begin
		raiserror('RegistrationSettingPreviewLinks.OperationalBranchId is missing or nullable after deployment.', 16, 1)
	end

	if not exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and name = 'PreviousValue'
			and max_length = -1
			and is_nullable = 1
	)
	begin
		raiserror('RegistrationSettingAuditEvents.PreviousValue is not nvarchar(max) NULL after deployment.', 16, 1)
	end

	if not exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationSettingAuditEvents')
			and name = 'NewValue'
			and max_length = -1
			and is_nullable = 1
	)
	begin
		raiserror('RegistrationSettingAuditEvents.NewValue is not nvarchar(max) NULL after deployment.', 16, 1)
	end

	if col_length('dbo.RegistrationFormAssets', 'UploadOrganizationId') is null
		or col_length('dbo.RegistrationFormAssets', 'UploadFormCode') is null
	begin
		raiserror('RegistrationFormAssets is missing its upload-scope columns after deployment.', 16, 1)
	end

	if not exists
	(
		select 1
		from sys.indexes
		where object_id = object_id('dbo.RegistrationFormAssets')
			and name = 'IX_RegistrationFormAssets_UploadScope'
	)
	begin
		raiserror('RegistrationFormAssets upload-scope index is missing after deployment.', 16, 1)
	end

	if not exists
	(
		select 1
		from sys.indexes
		where object_id = object_id('dbo.RegistrationFormAssets')
			and name = 'IX_RegistrationFormAssets_CreatedDate'
	)
	begin
		raiserror('RegistrationFormAssets created-date index is missing after deployment.', 16, 1)
	end

	if not exists
	(
		select 1
		from dbo.RegistrationFormAssetReferenceLocks
		where LockId = 1
	)
	begin
		raiserror('RegistrationFormAssetReferenceLocks row 1 is missing after deployment.', 16, 1)
	end

	if exists
	(
		select required.Setting
		from @required_setting_types as required
		where not exists
		(
			select 1
			from dbo.RegistrationFormSettingTypes as existing
			where existing.Setting = required.Setting
		)
	)
	begin
		raiserror('One or more required registration setting types are missing after deployment.', 16, 1)
	end

	if exists
	(
		select 1
		from dbo.RegistrationFormSettings
		where Setting = 'header_image_url'
			or Setting in
			(
				select LegacyKey
			from #SettingsAdministrationSettingMap
			)
	)
	begin
		raiserror('One or more retired settings remain in RegistrationFormSettings after deployment.', 16, 1)
	end

	if exists
	(
		select 1
		from dbo.RegistrationFormSettingTypes
		where Setting = 'header_image_url'
			or Setting in
			(
				select LegacyKey
				from #SettingsAdministrationSettingMap
			)
	)
	begin
		raiserror('One or more retired settings remain in RegistrationFormSettingTypes after deployment.', 16, 1)
	end

	declare @invalid_retired_draft_invariant bit = 0
	exec sys.sp_executesql
		N'if exists
		(
			select 1
			from dbo.RegistrationSettingDraftChanges as draft_change
			inner join dbo.RegistrationSettingDrafts as draft
			on draft.DraftId = draft_change.DraftId
			where draft.Status = ''Active''
				and
				(
					draft_change.SettingKey = ''header_image_url''
					or draft_change.SettingKey in
					(
						select LegacyKey
						from #SettingsAdministrationSettingMap
					)
				)
		)
			set @invalid = 1',
		N'@invalid bit output',
		@invalid = @invalid_retired_draft_invariant output
	if @invalid_retired_draft_invariant = 1
	begin
		raiserror('One or more active drafts still contain retired settings after deployment.', 16, 1)
	end

	if not exists
	(
		select 1
		from dbo.RegistrationSettingsCacheGeneration
		where Id = 1
	)
	begin
		raiserror('RegistrationSettingsCacheGeneration row 1 is missing after deployment.', 16, 1)
	end

	if (select count(*) from dbo.RegistrationSettingsCacheGeneration) <> 1
	begin
		raiserror('RegistrationSettingsCacheGeneration must contain exactly one singleton row.', 16, 1)
	end

	if (select count(*) from dbo.RegistrationFormAssetReferenceLocks) <> 1
	begin
		raiserror('RegistrationFormAssetReferenceLocks must contain exactly one singleton row.', 16, 1)
	end

	declare @invalid_version_invariant bit = 0
	if exists
	(
		select 1
		from dbo.RegistrationSettingScopeVersions
		where Version < 0
	)
		set @invalid_version_invariant = 1
	exec sys.sp_executesql
		N'if exists
		(
			select 1
			from dbo.RegistrationSettingPreviewLinks p
			inner join dbo.RegistrationSettingDrafts d on d.DraftId = p.DraftId
			cross join dbo.RegistrationSettingsCacheGeneration g
			where p.AllowLiveSubmission = 1
				and p.RevokedAtUtc is null
				and d.Status = ''Active''
				and (p.LiveSettingsGeneration is null or p.LiveSettingsGeneration > g.Generation)
		)
			set @invalid = 1',
		N'@invalid bit output',
		@invalid = @invalid_version_invariant output
	declare @invalid_draft_version_invariant bit = 0
	exec sys.sp_executesql
		N'if exists
		(
			select 1
			from dbo.RegistrationSettingDrafts
			where BaselineVersion < 0 or Revision < 0
		)
			set @invalid = 1',
		N'@invalid bit output',
		@invalid = @invalid_draft_version_invariant output
	if @invalid_draft_version_invariant = 1
		set @invalid_version_invariant = 1

	if @invalid_version_invariant = 1
	begin
		raiserror('Settings administration version and preview-generation invariants are invalid after deployment.', 16, 1)
	end

	if exists
	(
		select 1
		from dbo.RegistrationSettingPreviewLinks
		where RevokedAtUtc is null and OperationalBranchId = -2147483648
	)
	begin
		raiserror('An unrevoked preview link has the unknown operational-branch sentinel. Resolve the link before deployment.', 16, 1)
	end

	declare @required_primary_keys table
	(
		TableName sysname not null primary key,
		KeyColumn1 sysname not null,
		KeyColumn2 sysname null,
		KeyColumnCount tinyint not null
	)
	insert into @required_primary_keys (TableName, KeyColumn1, KeyColumn2, KeyColumnCount)
	values
		('dbo.RegistrationFormCodeMetadata', 'OrganizationId', 'FormCode', 2),
		('dbo.RegistrationSettingScopeVersions', 'OrganizationId', 'FormCode', 2),
		('dbo.RegistrationSettingDrafts', 'DraftId', null, 1),
		('dbo.RegistrationSettingDraftChanges', 'DraftChangeId', null, 1),
		('dbo.RegistrationSettingPreviewLinks', 'PreviewLinkId', null, 1),
		('dbo.RegistrationSettingAuditEvents', 'AuditEventId', null, 1),
		('dbo.RegistrationSettingsCacheGeneration', 'Id', null, 1),
		('dbo.RegistrationFormAssets', 'AssetId', null, 1),
		('dbo.RegistrationFormAssetReferenceLocks', 'LockId', null, 1)

	if exists
	(
		select 1
		from @required_primary_keys as shape
		where not exists
		(
			select 1
			from sys.key_constraints pk
			inner join sys.indexes i
				on i.object_id = pk.parent_object_id
				and i.index_id = pk.unique_index_id
			where pk.parent_object_id = object_id(shape.TableName)
				and pk.type = 'PK'
				and i.is_disabled = 0
				and
				(
					select count(*)
					from sys.index_columns ic
					where ic.object_id = i.object_id
						and ic.index_id = i.index_id
						and ic.key_ordinal > 0
				) = shape.KeyColumnCount
				and exists
				(
					select 1
					from sys.index_columns ic
					inner join sys.columns c
						on c.object_id = ic.object_id
						and c.column_id = ic.column_id
					where ic.object_id = i.object_id
						and ic.index_id = i.index_id
						and ic.key_ordinal = 1
						and c.name = shape.KeyColumn1
				)
				and
				(
					shape.KeyColumn2 is null
					or exists
					(
						select 1
						from sys.index_columns ic
						inner join sys.columns c
							on c.object_id = ic.object_id
							and c.column_id = ic.column_id
						where ic.object_id = i.object_id
							and ic.index_id = i.index_id
							and ic.key_ordinal = 2
							and c.name = shape.KeyColumn2
					)
				)
		)
	)
	begin
		raiserror('One or more required settings-administration primary keys are missing or incompatible. Restore the affected key before deployment.', 16, 1)
	end

	/* Verify the structural invariants that application code relies on. */
	if not exists
	(
		select 1 from sys.indexes i
		where i.object_id = object_id('dbo.RegistrationSettingDrafts')
			and i.name = 'UX_RSD_ActiveScope' and i.is_unique = 1 and i.has_filter = 1
			and i.filter_definition like '%Status%Active%'
			and (select count(*) from sys.index_columns ic where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal > 0) = 2
			and exists
			(
				select 1
				from sys.index_columns ic
				inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
				where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 1 and c.name = 'OrganizationId'
			)
			and exists
			(
				select 1
				from sys.index_columns ic
				inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
				where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal = 2 and c.name = 'FormCode'
			)
	)
		or not exists
		(
			select 1 from sys.indexes i
			where i.object_id = object_id('dbo.RegistrationSettingDraftChanges') and i.is_unique = 1
				and (select count(*) from sys.index_columns ic where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal > 0) = 2
		)
		or not exists
		(
			select 1 from sys.indexes i
			where i.object_id = object_id('dbo.RegistrationSettingPreviewLinks') and i.is_unique = 1
				and (select count(*) from sys.index_columns ic where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.key_ordinal > 0) = 1
		)
	begin
		raiserror('One or more required settings-administration unique indexes are missing or incompatible.', 16, 1)
	end

	if not exists
	(
		select 1
		from sys.foreign_keys fk
		where fk.parent_object_id = object_id('dbo.RegistrationSettingDraftChanges')
			and fk.referenced_object_id = object_id('dbo.RegistrationSettingDrafts')
			and fk.delete_referential_action = 1
			and fk.is_disabled = 0 and fk.is_not_trusted = 0
	)
		or not exists
		(
			select 1
			from sys.foreign_keys fk
			where fk.parent_object_id = object_id('dbo.RegistrationSettingPreviewLinks')
				and fk.referenced_object_id = object_id('dbo.RegistrationSettingDrafts')
				and fk.delete_referential_action = 1
				and fk.is_disabled = 0 and fk.is_not_trusted = 0
		)
	begin
		raiserror('One or more required settings-administration draft foreign keys are missing, disabled, or untrusted.', 16, 1)
	end

	if exists
	(
		select 1
		from sys.check_constraints
		where parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'),
			object_id('dbo.RegistrationSettingDrafts'),
			object_id('dbo.RegistrationSettingDraftChanges'),
			object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets'),
			object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
			and (is_disabled = 1 or is_not_trusted = 1)
	)
	begin
		raiserror('A settings-administration check constraint is disabled or untrusted after deployment.', 16, 1)
	end

	drop table #SettingsAdministrationChangedDrafts
	drop table #SettingsAdministrationSettingMap

	commit transaction
end try
begin catch
	if @deployment_transaction_started = 1 and xact_state() <> 0
		rollback transaction;
	throw
end catch
