/*
	Patron-registration settings administration desired-state deployment.

	This is the single authoritative deployment for the settings-administration
	feature. It updates a supported existing database to the current required
	schema and data state without reconstructing its deployment history.
*/
set nocount on
set xact_abort on
set quoted_identifier on

declare @deployment_transaction_started bit = 0
declare @deployment_phase nvarchar(200) = N'prerequisite validation'

begin try
/* 1. Prerequisite validation */
if @@trancount <> 0
	raiserror('settings-administration.sql must be executed without an existing transaction. Commit or roll back the caller transaction before rerunning deployment.', 16, 1)

if object_id('dbo.RegistrationFormSettingTypes', 'U') is null
	raiserror('Required shared prerequisite table dbo.RegistrationFormSettingTypes is missing. Restore or create the shared registration setting-type table before rerunning deployment.', 16, 1)

if object_id('dbo.RegistrationFormSettings', 'U') is null
	raiserror('Required application table dbo.RegistrationFormSettings is missing. Restore the patron-registration settings table before rerunning deployment.', 16, 1)

declare @shared_column_requirements table
(
	ObjectName sysname not null,
	ColumnName sysname not null,
	ExpectedDefinition nvarchar(100) not null,
	ExpectedSystemTypeId tinyint not null,
	ExpectedMaxLength smallint not null,
	ExpectedNullable bit not null,
	primary key (ObjectName, ColumnName)
)

insert into @shared_column_requirements
(
	ObjectName,
	ColumnName,
	ExpectedDefinition,
	ExpectedSystemTypeId,
	ExpectedMaxLength,
	ExpectedNullable
)
values
	(N'dbo.RegistrationFormSettingTypes', N'Setting', N'nvarchar(200) NOT NULL', 231, 400, 0),
	(N'dbo.RegistrationFormSettings', N'OrganizationID', N'int NOT NULL', 56, 4, 0),
	(N'dbo.RegistrationFormSettings', N'Setting', N'nvarchar(200) NOT NULL', 231, 400, 0),
	(N'dbo.RegistrationFormSettings', N'FormCode', N'nvarchar(64) NOT NULL', 231, 128, 0)

declare @shared_object sysname
declare @shared_column sysname
declare @expected_shared_definition nvarchar(100)

select top (1)
	@shared_object = requirement.ObjectName,
	@shared_column = requirement.ColumnName,
	@expected_shared_definition = requirement.ExpectedDefinition
from @shared_column_requirements requirement
where not exists
(
	select 1
	from sys.columns actual_column
	where actual_column.object_id = object_id(requirement.ObjectName, 'U')
		and actual_column.name = requirement.ColumnName
)
order by requirement.ObjectName, requirement.ColumnName

if @shared_column is not null
	raiserror('Required database column %s.%s is missing. Expected %s. Correct the registration schema before rerunning deployment.', 16, 1, @shared_object, @shared_column, @expected_shared_definition)

if col_length('dbo.RegistrationFormSettings', 'Value') is null
	raiserror('Required application column dbo.RegistrationFormSettings.Value is missing. Expected nvarchar(max) NOT NULL. Restore the column before rerunning deployment.', 16, 1)

declare @actual_shared_definition nvarchar(100)

select top (1)
	@shared_object = requirement.ObjectName,
	@shared_column = requirement.ColumnName,
	@expected_shared_definition = requirement.ExpectedDefinition,
	@actual_shared_definition = concat
	(
		type_name(actual_column.system_type_id),
		case
			when actual_column.system_type_id in (231, 239) then concat('(', case when actual_column.max_length = -1 then 'max' else convert(varchar(10), actual_column.max_length / 2) end, ')')
			when actual_column.system_type_id in (165, 167, 173, 175) then concat('(', case when actual_column.max_length = -1 then 'max' else convert(varchar(10), actual_column.max_length) end, ')')
			else ''
		end,
		case when actual_column.is_nullable = 1 then ' NULL' else ' NOT NULL' end
	)
from @shared_column_requirements requirement
inner join sys.columns actual_column
	on actual_column.object_id = object_id(requirement.ObjectName, 'U')
	and actual_column.name = requirement.ColumnName
where actual_column.system_type_id <> requirement.ExpectedSystemTypeId
	or actual_column.max_length <> requirement.ExpectedMaxLength
	or actual_column.is_nullable <> requirement.ExpectedNullable
order by requirement.ObjectName, requirement.ColumnName

if @actual_shared_definition is not null
	raiserror('Database column %s.%s has an incompatible definition. Expected %s; found %s. Correct the registration schema before rerunning deployment.', 16, 1, @shared_object, @shared_column, @expected_shared_definition, @actual_shared_definition)

declare @registration_settings_value_definition nvarchar(100)
select @registration_settings_value_definition = concat
(
	type_name(actual_column.system_type_id),
	case
		when actual_column.system_type_id in (231, 239) then concat('(', case when actual_column.max_length = -1 then 'max' else convert(varchar(10), actual_column.max_length / 2) end, ')')
		when actual_column.system_type_id in (165, 167, 173, 175) then concat('(', case when actual_column.max_length = -1 then 'max' else convert(varchar(10), actual_column.max_length) end, ')')
		else ''
	end,
	case when actual_column.is_nullable = 1 then ' NULL' else ' NOT NULL' end
)
from sys.columns actual_column
where actual_column.object_id = object_id('dbo.RegistrationFormSettings')
	and actual_column.name = 'Value'

if exists
(
	select 1
	from sys.columns
	where object_id = object_id('dbo.RegistrationFormSettings')
		and name = 'Value'
		and system_type_id <> 231
)
	raiserror('Application column dbo.RegistrationFormSettings.Value must use the nvarchar data type so it can be converged to nvarchar(max) NOT NULL. Found %s.', 16, 1, @registration_settings_value_definition)

if not exists
(
	select 1
	from sys.indexes unique_index
	where unique_index.object_id = object_id('dbo.RegistrationFormSettings')
		and unique_index.is_unique = 1
		and unique_index.is_disabled = 0
		and unique_index.is_hypothetical = 0
		and unique_index.has_filter = 0
		and unique_index.filter_definition is null
		and 3 =
		(
			select count(*)
			from sys.index_columns key_column
			inner join sys.columns column_object
				on column_object.object_id = key_column.object_id
				and column_object.column_id = key_column.column_id
			where key_column.object_id = unique_index.object_id
				and key_column.index_id = unique_index.index_id
				and key_column.key_ordinal > 0
				and column_object.name in ('OrganizationID', 'Setting', 'FormCode')
		)
		and 3 =
		(
			select count(*)
			from sys.index_columns key_column
			where key_column.object_id = unique_index.object_id
				and key_column.index_id = unique_index.index_id
				and key_column.key_ordinal > 0
		)
)
	raiserror('dbo.RegistrationFormSettings requires an enabled, unfiltered unique key containing exactly OrganizationID, Setting, and FormCode; column order may vary. No qualifying key was found. Restore that uniqueness guarantee before rerunning deployment.', 16, 1)

declare @setting_fk_name sysname
declare @setting_fk_disabled int
declare @setting_fk_untrusted int

select top (1)
	@setting_fk_name = fk.name,
	@setting_fk_disabled = fk.is_disabled,
	@setting_fk_untrusted = fk.is_not_trusted
from sys.foreign_keys fk
inner join sys.foreign_key_columns fkc on fkc.constraint_object_id = fk.object_id
where fk.parent_object_id = object_id('dbo.RegistrationFormSettings')
	and fk.referenced_object_id = object_id('dbo.RegistrationFormSettingTypes')
	and fkc.parent_column_id = columnproperty(object_id('dbo.RegistrationFormSettings'), 'Setting', 'ColumnId')
	and fkc.referenced_column_id = columnproperty(object_id('dbo.RegistrationFormSettingTypes'), 'Setting', 'ColumnId')
order by case when fk.is_disabled = 0 and fk.is_not_trusted = 0 then 0 else 1 end, fk.name

if @setting_fk_name is null
	raiserror('dbo.RegistrationFormSettings.Setting must reference dbo.RegistrationFormSettingTypes.Setting through a foreign key. No matching foreign key was found. Restore the relationship before rerunning deployment.', 16, 1)

if @setting_fk_disabled = 1 or @setting_fk_untrusted = 1
	raiserror('Foreign key %s connects dbo.RegistrationFormSettings.Setting to dbo.RegistrationFormSettingTypes.Setting but is not usable: is_disabled=%d, is_not_trusted=%d. Enable and trust the constraint after correcting any invalid data, then rerun deployment.', 16, 1, @setting_fk_name, @setting_fk_disabled, @setting_fk_untrusted)

	/* 2. Deployment lock and transaction */
	set @deployment_phase = N'acquiring the database convergence lock'
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
		raiserror('Could not acquire the patron-registration database convergence application lock. sp_getapplock returned %d. No deployment changes were made; verify that another deployment is not still running and retry.', 16, 1, @application_lock_result)


	/* 3. Creation of missing current objects */
	set @deployment_phase = N'creating missing current database objects'
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

	if not exists
	(
		select 1
		from sys.indexes
		where object_id = object_id('dbo.RegistrationSettingDrafts')
			and name = 'UX_RSD_ActiveScope'
	)
	begin
		create unique index UX_RSD_ActiveScope
		on dbo.RegistrationSettingDrafts (OrganizationId, FormCode)
		where Status = 'Active'
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

	if object_id('dbo.RegistrationSettingsCacheGeneration', 'U') is null
	begin
		create table dbo.RegistrationSettingsCacheGeneration
		(
			Id tinyint not null constraint PK_RegistrationSettingsCacheGeneration primary key,
			Generation bigint not null,
			ModifiedAtUtc datetime2(7) not null
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

	if object_id('dbo.RegistrationFormAssetReferenceLocks', 'U') is null
	begin
		create table dbo.RegistrationFormAssetReferenceLocks
		(
			LockId tinyint not null
				constraint PK_RegistrationFormAssetReferenceLocks primary key,
			constraint CK_RegistrationFormAssetReferenceLocks_Singleton check (LockId = 1)
		)
	end


	/* 4. Additive and widening upgrades required by the current application */
	set @deployment_phase = N'applying additive and widening schema upgrades'

	if exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormSettings')
			and name = 'Value'
			and (max_length <> -1 or is_nullable <> 0)
	)
	begin
		declare @null_setting_value_count int =
		(
			select count(*)
			from dbo.RegistrationFormSettings
			where Value is null
		)

		if @null_setting_value_count > 0
		begin
			declare @null_setting_value_detail nvarchar(1000)
			select top (1) @null_setting_value_detail = concat
			(
				'OrganizationID=', OrganizationID,
				', FormCode="', FormCode,
				'", Setting="', Setting, '"'
			)
			from dbo.RegistrationFormSettings
			where Value is null
			order by OrganizationID, FormCode, Setting

			raiserror('Cannot converge dbo.RegistrationFormSettings.Value to nvarchar(max) NOT NULL because %d row(s) currently contain NULL. First offending row: %s. Decide whether those rows should be removed or assigned an explicit value, correct the data, and rerun deployment.', 16, 1, @null_setting_value_count, @null_setting_value_detail)
		end

		alter table dbo.RegistrationFormSettings
			alter column Value nvarchar(max) not null
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
	if col_length('dbo.RegistrationSettingPreviewLinks', 'LiveSettingsGeneration') is null
	begin
		alter table dbo.RegistrationSettingPreviewLinks
			add LiveSettingsGeneration bigint null
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

	if col_length('dbo.RegistrationSettingDrafts', 'Revision') is null
	begin
		alter table dbo.RegistrationSettingDrafts
			add Revision bigint not null constraint DF_RSD_Revision default 0
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



	/* 5. Required data transformations and current singleton state */
	set @deployment_phase = N'applying required data transformations and singleton state'
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

	/*
	   A NULL LiveSettingsGeneration has no durable evidence of the generation
	   under which an old live bearer token was issued. Revoke those links while
	   they are attached to an active draft; links for inactive drafts remain
	   existing records, and safe-preview links are not affected by the live
	   generation contract. A generation-bound link is never rewritten here.
	*/
	/*
	   LiveSettingsGeneration may be introduced earlier in this same batch.
	   Execute this statement dynamically so SQL Server resolves the upgraded
	   column after the ALTER TABLE has committed to the transaction.
	*/
	exec
	(
		N'update preview_link
		set RevokedAtUtc = sysutcdatetime(),
			RevokedBy = coalesce(RevokedBy, ''settings-administration.sql''),
			ModifiedAtUtc = sysutcdatetime(),
			ModifiedBy = ''settings-administration.sql''
		from dbo.RegistrationSettingPreviewLinks preview_link
		inner join dbo.RegistrationSettingDrafts draft
			on draft.DraftId = preview_link.DraftId
		where preview_link.AllowLiveSubmission = 1
			and preview_link.LiveSettingsGeneration is null
			and preview_link.RevokedAtUtc is null
			and draft.Status = ''Active'''
	)



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

	declare @setting_map table
	(
		LegacyKey nvarchar(200) not null primary key,
		ReplacementKey nvarchar(200) not null unique
	)

	declare @required_setting_types table
	(
		Setting nvarchar(200) not null primary key
	)

	insert into @setting_map
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

	insert #SettingsAdministrationChangedDrafts (DraftId)
	select distinct draft_change.DraftId
	from dbo.RegistrationSettingDraftChanges draft_change
	inner join dbo.RegistrationSettingDrafts draft
		on draft.DraftId = draft_change.DraftId
	where draft.Status = 'Active'
		and
		(
			draft_change.SettingKey = 'header_image_url'
			or draft_change.SettingKey in
			(
				select LegacyKey
				from @setting_map
			)
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
	inner join @setting_map as map
	on map.LegacyKey = legacy.Setting
	where not exists
	(
		select 1
		from dbo.RegistrationFormSettings as replacement
		where replacement.OrganizationID = legacy.OrganizationID
			and replacement.FormCode = legacy.FormCode
			and replacement.Setting = map.ReplacementKey
	)

	update draft_change
	set SettingKey = map.ReplacementKey
	from dbo.RegistrationSettingDraftChanges as draft_change
	inner join dbo.RegistrationSettingDrafts as draft
	on draft.DraftId = draft_change.DraftId
	inner join @setting_map as map
	on map.LegacyKey = draft_change.SettingKey
	where draft.Status = 'Active'
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
	inner join @setting_map as map
	on map.LegacyKey = draft_change.SettingKey
	where draft.Status = 'Active'

	delete draft_change
	from dbo.RegistrationSettingDraftChanges as draft_change
	inner join dbo.RegistrationSettingDrafts as draft
	on draft.DraftId = draft_change.DraftId
	where draft_change.SettingKey = 'header_image_url'
		and draft.Status = 'Active'

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

	/* Revision may have been added earlier in this batch. */
	exec
	(
		N'update draft
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
			from @setting_map
		)

	delete from dbo.RegistrationFormSettingTypes
	where Setting = 'header_image_url'
		or Setting in
		(
			select LegacyKey
			from @setting_map
		)

	/* 6. Focused final application invariants */
	set @deployment_phase = N'validating final application invariants'

	if not exists
	(
		select 1
		from sys.columns
		where object_id = object_id('dbo.RegistrationFormSettings')
			and name = 'Value'
			and system_type_id = 231
			and max_length = -1
			and is_nullable = 0
	)
		raiserror('dbo.RegistrationFormSettings.Value must be nvarchar(max) NOT NULL after deployment.', 16, 1)

	declare @cache_generation_row_count int = (select count(*) from dbo.RegistrationSettingsCacheGeneration)
	if @cache_generation_row_count <> 1
	begin
		declare @cache_generation_ids nvarchar(1000) = coalesce
		(
			(select string_agg(convert(nvarchar(max), Id), N', ') from dbo.RegistrationSettingsCacheGeneration),
			N'<none>'
		)
		raiserror('dbo.RegistrationSettingsCacheGeneration must contain exactly one singleton row with Id=1. Found %d row(s); IDs present: %s.', 16, 1, @cache_generation_row_count, @cache_generation_ids)
	end

	declare @asset_lock_row_count int = (select count(*) from dbo.RegistrationFormAssetReferenceLocks)
	if @asset_lock_row_count <> 1
	begin
		declare @asset_lock_ids nvarchar(1000) = coalesce
		(
			(select string_agg(convert(nvarchar(max), LockId), N', ') from dbo.RegistrationFormAssetReferenceLocks),
			N'<none>'
		)
		raiserror('dbo.RegistrationFormAssetReferenceLocks must contain exactly one singleton row with LockId=1. Found %d row(s); LockId values present: %s.', 16, 1, @asset_lock_row_count, @asset_lock_ids)
	end

	declare @invalid_version_detail nvarchar(1000)
	exec sys.sp_executesql
		N'
			select top (1) @detail = problem.Detail
			from
			(
				select concat(''RegistrationSettingScopeVersions OrganizationId='', OrganizationId,
					'', FormCode="'', FormCode, ''", Version='', Version) as Detail, 1 as SortOrder
				from dbo.RegistrationSettingScopeVersions
				where Version < 0
				union all
				select concat(''RegistrationSettingDrafts DraftId='', DraftId,
					'', BaselineVersion='', BaselineVersion, '', Revision='', Revision) as Detail, 2 as SortOrder
				from dbo.RegistrationSettingDrafts
				where BaselineVersion < 0 or Revision < 0
			) problem
			order by problem.SortOrder, problem.Detail',
		N'@detail nvarchar(1000) output',
		@detail = @invalid_version_detail output

	if @invalid_version_detail is not null
		raiserror('Settings administration versions must be non-negative after deployment. First offending row: %s.', 16, 1, @invalid_version_detail)

	declare @invalid_live_preview_detail nvarchar(1000)
	exec sys.sp_executesql
		N'
			select top (1) @detail = concat
			(
				''PreviewLinkId='', preview_link.PreviewLinkId,
				'', DraftId='', preview_link.DraftId,
				'', LiveSettingsGeneration='', coalesce(convert(nvarchar(30), preview_link.LiveSettingsGeneration), ''<NULL>''),
				'', current cache Generation='', generation.Generation
			)
			from dbo.RegistrationSettingPreviewLinks preview_link
			inner join dbo.RegistrationSettingDrafts draft on draft.DraftId = preview_link.DraftId
			cross join dbo.RegistrationSettingsCacheGeneration generation
			where preview_link.AllowLiveSubmission = 1
				and preview_link.RevokedAtUtc is null
				and draft.Status = ''Active''
				and (preview_link.LiveSettingsGeneration is null or preview_link.LiveSettingsGeneration > generation.Generation)
			order by preview_link.PreviewLinkId',
		N'@detail nvarchar(1000) output',
		@detail = @invalid_live_preview_detail output

	if @invalid_live_preview_detail is not null
		raiserror('An active live-preview link has an invalid settings generation. %s. Revoke or correct the link before rerunning deployment.', 16, 1, @invalid_live_preview_detail)

	declare @invalid_branch_preview_detail nvarchar(1000)
	exec sys.sp_executesql
		N'
			select top (1) @detail = concat
			(
				''PreviewLinkId='', PreviewLinkId,
				'', DraftId='', DraftId,
				'', OperationalBranchId='', OperationalBranchId
			)
			from dbo.RegistrationSettingPreviewLinks
			where RevokedAtUtc is null and OperationalBranchId = -2147483648
			order by PreviewLinkId',
		N'@detail nvarchar(1000) output',
		@detail = @invalid_branch_preview_detail output

	if @invalid_branch_preview_detail is not null
		raiserror('An unrevoked preview link still has the unknown operational-branch sentinel. %s. Revoke the link or assign its actual operational branch before rerunning deployment.', 16, 1, @invalid_branch_preview_detail)

	declare @missing_setting_types nvarchar(1800)
	select @missing_setting_types = string_agg(convert(nvarchar(max), missing.Setting), N', ')
	from
	(
		select required.Setting
		from @required_setting_types required
		where not exists
		(
			select 1 from dbo.RegistrationFormSettingTypes existing where existing.Setting = required.Setting
		)
	) missing

	if @missing_setting_types is not null
		raiserror('Required registration setting types are missing after deployment: %s.', 16, 1, @missing_setting_types)

	declare @retired_setting_count int
	declare @retired_setting_detail nvarchar(1000)
	select @retired_setting_count = count(*)
	from dbo.RegistrationFormSettings
	where Setting = 'header_image_url' or Setting in (select LegacyKey from @setting_map)

	if @retired_setting_count > 0
	begin
		select top (1) @retired_setting_detail = concat
		(
			'OrganizationID=', OrganizationID,
			', FormCode="', FormCode,
			'", Setting="', Setting, '"'
		)
		from dbo.RegistrationFormSettings
		where Setting = 'header_image_url' or Setting in (select LegacyKey from @setting_map)
		order by OrganizationID, FormCode, Setting

		raiserror('%d retired live setting row(s) remain in dbo.RegistrationFormSettings after deployment. First offending row: %s.', 16, 1, @retired_setting_count, @retired_setting_detail)
	end

	declare @retired_draft_change_count int
	declare @retired_draft_change_detail nvarchar(1000)
	select @retired_draft_change_count = count(*)
	from dbo.RegistrationSettingDraftChanges draft_change
	inner join dbo.RegistrationSettingDrafts draft on draft.DraftId = draft_change.DraftId
	where draft.Status = 'Active'
		and (draft_change.SettingKey = 'header_image_url' or draft_change.SettingKey in (select LegacyKey from @setting_map))

	if @retired_draft_change_count > 0
	begin
		select top (1) @retired_draft_change_detail = concat
		(
			'DraftId=', draft_change.DraftId,
			', SettingKey="', draft_change.SettingKey, '"'
		)
		from dbo.RegistrationSettingDraftChanges draft_change
		inner join dbo.RegistrationSettingDrafts draft on draft.DraftId = draft_change.DraftId
		where draft.Status = 'Active'
			and (draft_change.SettingKey = 'header_image_url' or draft_change.SettingKey in (select LegacyKey from @setting_map))
		order by draft_change.DraftId, draft_change.SettingKey

		raiserror('%d retired setting change(s) remain in active drafts after deployment. First offending row: %s.', 16, 1, @retired_draft_change_count, @retired_draft_change_detail)
	end

	drop table #SettingsAdministrationChangedDrafts
	set @deployment_phase = N'committing the database convergence transaction'
	commit transaction
end try
begin catch
	declare @error_number int = error_number()
	declare @error_line int = error_line()
	declare @error_message nvarchar(2048) = error_message()

	if @deployment_transaction_started = 1 and xact_state() <> 0
		rollback transaction;

	declare @diagnostic_message nvarchar(2048) = concat
	(
		'Database convergence failed during "', @deployment_phase,
		'". SQL Server error ', @error_number,
		case when @error_line is null then '' else concat(' at line ', @error_line) end,
		': ', @error_message
	)

	;throw 51000, @diagnostic_message, 1
end catch