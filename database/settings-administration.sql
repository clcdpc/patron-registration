/*
	Patron-registration settings administration desired-state deployment.

	This is the single authoritative deployment for the settings-administration
	feature. Supported inputs are the pre-feature prerequisite database, schema
	states produced by historical repository deployments, and the current state.
	Unexpected owned schema shapes fail instead of being guessed or repaired.
*/
set nocount on
set xact_abort on

declare @deployment_transaction_started bit = 0

begin try
/* 1. Prerequisite validation */
if @@trancount <> 0
	raiserror('settings-administration.sql must be executed without an existing transaction.', 16, 1)

if object_id('dbo.RegistrationFormSettingTypes', 'U') is null
	raiserror('dbo.RegistrationFormSettingTypes must exist before settings administration is installed.', 16, 1)

if object_id('dbo.RegistrationFormSettings', 'U') is null
	raiserror('dbo.RegistrationFormSettings must exist before settings administration is installed.', 16, 1)

if col_length('dbo.RegistrationFormSettingTypes', 'Setting') is null
	raiserror('dbo.RegistrationFormSettingTypes.Setting must exist before settings administration is installed.', 16, 1)

if col_length('dbo.RegistrationFormSettings', 'OrganizationID') is null
	or col_length('dbo.RegistrationFormSettings', 'Setting') is null
	or col_length('dbo.RegistrationFormSettings', 'FormCode') is null
	or col_length('dbo.RegistrationFormSettings', 'Value') is null
	raiserror('dbo.RegistrationFormSettings must contain OrganizationID, Setting, FormCode, and Value.', 16, 1)

if exists
(
	select 1 from sys.columns
	where object_id = object_id('dbo.RegistrationFormSettingTypes')
		and name = 'Setting'
		and (system_type_id <> 231 or max_length <> 400 or is_nullable <> 0)
)
	raiserror('Shared prerequisite dbo.RegistrationFormSettingTypes.Setting must be nvarchar(200) NOT NULL.', 16, 1)

if exists
(
	select 1 from sys.columns
	where object_id = object_id('dbo.RegistrationFormSettings')
		and
		(
			(name = 'OrganizationID' and (system_type_id <> 56 or is_nullable <> 0))
			or (name = 'Setting' and (system_type_id <> 231 or max_length <> 400 or is_nullable <> 0))
			or (name = 'FormCode' and (system_type_id <> 231 or max_length <> 128 or is_nullable <> 0))
			or (name = 'Value' and (system_type_id <> 231 or max_length <> -1 or is_nullable <> 1))
		)
)
	raiserror('Shared prerequisite dbo.RegistrationFormSettings has an incompatible OrganizationID, Setting, FormCode, or Value definition.', 16, 1)

if not exists
(
	select 1
	from sys.indexes unique_index
	inner join sys.index_columns organization_key
		on organization_key.object_id = unique_index.object_id
		and organization_key.index_id = unique_index.index_id
		and organization_key.key_ordinal = 1
	inner join sys.columns organization_column
		on organization_column.object_id = organization_key.object_id
		and organization_column.column_id = organization_key.column_id
	inner join sys.index_columns setting_key
		on setting_key.object_id = unique_index.object_id
		and setting_key.index_id = unique_index.index_id
		and setting_key.key_ordinal = 2
	inner join sys.columns setting_column
		on setting_column.object_id = setting_key.object_id
		and setting_column.column_id = setting_key.column_id
	inner join sys.index_columns form_key
		on form_key.object_id = unique_index.object_id
		and form_key.index_id = unique_index.index_id
		and form_key.key_ordinal = 3
	inner join sys.columns form_column
		on form_column.object_id = form_key.object_id
		and form_column.column_id = form_key.column_id
	where unique_index.object_id = object_id('dbo.RegistrationFormSettings')
		and unique_index.is_unique = 1
		and unique_index.is_disabled = 0
		and organization_column.name = 'OrganizationID'
		and setting_column.name = 'Setting'
		and form_column.name = 'FormCode'
		and not exists
		(
			select 1 from sys.index_columns extra_key
			where extra_key.object_id = unique_index.object_id
				and extra_key.index_id = unique_index.index_id
				and extra_key.key_ordinal > 3
		)
)
	raiserror('Shared prerequisite dbo.RegistrationFormSettings must have a unique key on OrganizationID, Setting, and FormCode.', 16, 1)

if not exists
(
	select 1
	from sys.foreign_keys fk
	inner join sys.foreign_key_columns fkc on fkc.constraint_object_id = fk.object_id
	where fk.parent_object_id = object_id('dbo.RegistrationFormSettings')
		and fk.referenced_object_id = object_id('dbo.RegistrationFormSettingTypes')
		and fk.is_disabled = 0 and fk.is_not_trusted = 0
		and fkc.parent_column_id = columnproperty(object_id('dbo.RegistrationFormSettings'), 'Setting', 'ColumnId')
		and fkc.referenced_column_id = columnproperty(object_id('dbo.RegistrationFormSettingTypes'), 'Setting', 'ColumnId')
)
	raiserror('Shared prerequisite dbo.RegistrationFormSettings.Setting must have a trusted foreign key to dbo.RegistrationFormSettingTypes.Setting.', 16, 1)

	/* 2. Deployment lock and transaction */
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
		raiserror('Could not acquire the patron-registration database convergence application lock (sp_getapplock result %d). No deployment changes were made.', 16, 1, @application_lock_result)

	declare @core_table_count int =
	(
		select count(*)
		from (values
			('RegistrationFormCodeMetadata'), ('RegistrationSettingScopeVersions'),
			('RegistrationSettingDrafts'), ('RegistrationSettingDraftChanges'),
			('RegistrationSettingPreviewLinks'), ('RegistrationSettingAuditEvents'),
			('RegistrationSettingsCacheGeneration')
		) owned(TableName)
		where object_id('dbo.' + owned.TableName, 'U') is not null
	)

	if @core_table_count not in (0, 7)
		raiserror('The settings-administration core tables are only partially present. Restore a complete historical deployment or remove the uninstalled partial schema before rerunning.', 16, 1)

	if @core_table_count = 0 and
		(object_id('dbo.RegistrationFormAssets', 'U') is not null
			or object_id('dbo.RegistrationFormAssetReferenceLocks', 'U') is not null)
		raiserror('Asset-owned tables exist without the settings-administration core schema. Restore a compatible historical deployment before rerunning.', 16, 1)

	if object_id('dbo.RegistrationFormAssetReferenceLocks', 'U') is not null
		and object_id('dbo.RegistrationFormAssets', 'U') is null
		raiserror('dbo.RegistrationFormAssetReferenceLocks exists without dbo.RegistrationFormAssets. Restore a compatible historical deployment before rerunning.', 16, 1)

	declare @owned_columns table
	(
		TableName sysname not null,
		ColumnName sysname not null,
		SystemTypeId tinyint not null,
		MaxLength smallint not null,
		IsNullable bit not null,
		IsIdentity bit not null,
		IsHistoricalOptional bit not null,
		HistoricalMaxLength smallint null,
		primary key (TableName, ColumnName)
	)

	insert @owned_columns values
		('RegistrationFormCodeMetadata','OrganizationId',56,4,0,0,0,null),
		('RegistrationFormCodeMetadata','FormCode',231,128,0,0,0,null),
		('RegistrationFormCodeMetadata','DisplayName',231,400,0,0,0,null),
		('RegistrationFormCodeMetadata','Description',231,4000,1,0,0,null),
		('RegistrationFormCodeMetadata','CreatedAtUtc',42,8,0,0,0,null),
		('RegistrationFormCodeMetadata','CreatedBy',231,512,0,0,0,null),
		('RegistrationFormCodeMetadata','ModifiedAtUtc',42,8,0,0,0,null),
		('RegistrationFormCodeMetadata','ModifiedBy',231,512,0,0,0,null),
		('RegistrationSettingScopeVersions','OrganizationId',56,4,0,0,0,null),
		('RegistrationSettingScopeVersions','FormCode',231,128,0,0,0,null),
		('RegistrationSettingScopeVersions','Version',127,8,0,0,0,null),
		('RegistrationSettingScopeVersions','ModifiedAtUtc',42,8,0,0,0,null),
		('RegistrationSettingDrafts','DraftId',127,8,0,1,0,null),
		('RegistrationSettingDrafts','OrganizationId',56,4,0,0,0,null),
		('RegistrationSettingDrafts','FormCode',231,128,0,0,0,null),
		('RegistrationSettingDrafts','BaselineVersion',127,8,0,0,0,null),
		('RegistrationSettingDrafts','Revision',127,8,0,0,1,null),
		('RegistrationSettingDrafts','Status',167,16,0,0,0,null),
		('RegistrationSettingDrafts','CreatedAtUtc',42,8,0,0,0,null),
		('RegistrationSettingDrafts','CreatedBy',231,512,0,0,0,null),
		('RegistrationSettingDrafts','ModifiedAtUtc',42,8,0,0,0,null),
		('RegistrationSettingDrafts','ModifiedBy',231,512,0,0,0,null),
		('RegistrationSettingDrafts','CommittedAtUtc',42,8,1,0,0,null),
		('RegistrationSettingDrafts','CommittedBy',231,512,1,0,0,null),
		('RegistrationSettingDrafts','DiscardedAtUtc',42,8,1,0,0,null),
		('RegistrationSettingDrafts','DiscardedBy',231,512,1,0,0,null),
		('RegistrationSettingDraftChanges','DraftChangeId',127,8,0,1,0,null),
		('RegistrationSettingDraftChanges','DraftId',127,8,0,0,0,null),
		('RegistrationSettingDraftChanges','SettingKey',231,400,0,0,0,null),
		('RegistrationSettingDraftChanges','Operation',167,20,0,0,0,null),
		('RegistrationSettingDraftChanges','Value',231,-1,1,0,0,null),
		('RegistrationSettingDraftChanges','ModifiedAtUtc',42,8,0,0,0,null),
		('RegistrationSettingDraftChanges','ModifiedBy',231,512,0,0,0,null),
		('RegistrationSettingPreviewLinks','PreviewLinkId',127,8,0,1,0,null),
		('RegistrationSettingPreviewLinks','DraftId',127,8,0,0,0,null),
		('RegistrationSettingPreviewLinks','TokenHash',173,32,0,0,0,null),
		('RegistrationSettingPreviewLinks','OperationalBranchId',56,4,0,0,1,null),
		('RegistrationSettingPreviewLinks','AllowLiveSubmission',104,1,0,0,0,null),
		('RegistrationSettingPreviewLinks','LiveSettingsGeneration',127,8,1,0,1,null),
		('RegistrationSettingPreviewLinks','CreatedAtUtc',42,8,0,0,0,null),
		('RegistrationSettingPreviewLinks','CreatedBy',231,512,0,0,0,null),
		('RegistrationSettingPreviewLinks','ModifiedAtUtc',42,8,0,0,0,null),
		('RegistrationSettingPreviewLinks','ModifiedBy',231,512,0,0,0,null),
		('RegistrationSettingPreviewLinks','RevokedAtUtc',42,8,1,0,0,null),
		('RegistrationSettingPreviewLinks','RevokedBy',231,512,1,0,0,null),
		('RegistrationSettingPreviewLinks','ExpiresAtUtc',42,8,1,0,0,null),
		('RegistrationSettingAuditEvents','AuditEventId',127,8,0,1,0,null),
		('RegistrationSettingAuditEvents','TimestampUtc',42,8,0,0,0,null),
		('RegistrationSettingAuditEvents','EventType',231,160,0,0,0,null),
		('RegistrationSettingAuditEvents','ActorId',231,256,1,0,0,null),
		('RegistrationSettingAuditEvents','ActorName',231,512,1,0,0,null),
		('RegistrationSettingAuditEvents','ActorOrganizationId',56,4,1,0,0,null),
		('RegistrationSettingAuditEvents','TargetOrganizationId',56,4,0,0,0,null),
		('RegistrationSettingAuditEvents','TargetLibraryId',56,4,1,0,0,null),
		('RegistrationSettingAuditEvents','FormCode',231,128,0,0,0,null),
		('RegistrationSettingAuditEvents','SettingKey',231,400,1,0,0,null),
		('RegistrationSettingAuditEvents','PreviousValue',231,-1,1,0,0,2000),
		('RegistrationSettingAuditEvents','NewValue',231,-1,1,0,0,2000),
		('RegistrationSettingAuditEvents','IsSensitive',104,1,0,0,0,null),
		('RegistrationSettingAuditEvents','DraftId',127,8,1,0,0,null),
		('RegistrationSettingAuditEvents','PreviewLinkId',127,8,1,0,0,null),
		('RegistrationSettingAuditEvents','CorrelationId',231,256,1,0,0,null),
		('RegistrationSettingAuditEvents','IpAddress',231,128,1,0,0,null),
		('RegistrationSettingAuditEvents','Succeeded',104,1,0,0,0,null),
		('RegistrationSettingAuditEvents','FailureReason',231,2000,1,0,0,null),
		('RegistrationSettingAuditEvents','MetadataJson',231,-1,1,0,0,null),
		('RegistrationSettingsCacheGeneration','Id',48,1,0,0,0,null),
		('RegistrationSettingsCacheGeneration','Generation',127,8,0,0,0,null),
		('RegistrationSettingsCacheGeneration','ModifiedAtUtc',42,8,0,0,0,null),
		('RegistrationFormAssets','AssetId',56,4,0,1,0,null),
		('RegistrationFormAssets','FileName',231,510,0,0,0,null),
		('RegistrationFormAssets','ContentType',167,100,0,0,0,null),
		('RegistrationFormAssets','Content',165,-1,0,0,0,null),
		('RegistrationFormAssets','ContentHash',167,64,0,0,0,null),
		('RegistrationFormAssets','CreatedDate',42,8,0,0,0,null),
		('RegistrationFormAssets','ModifiedDate',42,8,0,0,0,null),
		('RegistrationFormAssets','UploadOrganizationId',56,4,1,0,1,null),
		('RegistrationFormAssets','UploadFormCode',231,128,1,0,1,null),
		('RegistrationFormAssetReferenceLocks','LockId',48,1,0,0,0,null)

	declare @incompatible_owned_object nvarchar(300)

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.ColumnName
	from @owned_columns expected
	inner join sys.tables owned_table
		on owned_table.schema_id = schema_id('dbo')
		and owned_table.name collate database_default = expected.TableName
	left join sys.columns actual
		on actual.object_id = owned_table.object_id
		and actual.name collate database_default = expected.ColumnName
	where (actual.column_id is null and expected.IsHistoricalOptional = 0)
		or
		(
			actual.column_id is not null and
			(
				actual.system_type_id <> expected.SystemTypeId
				or actual.max_length not in (expected.MaxLength, coalesce(expected.HistoricalMaxLength, expected.MaxLength))
				or actual.is_nullable <> expected.IsNullable
				or actual.is_identity <> expected.IsIdentity
			)
		)

	if @incompatible_owned_object is not null
		raiserror('Owned schema column %s does not match a supported historical definition. Restore the schema from a repository deployment before rerunning.', 16, 1, @incompatible_owned_object)

	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + owned_table.name + '.' + actual.name
	from sys.tables owned_table
	inner join sys.columns actual on actual.object_id = owned_table.object_id
	left join @owned_columns expected
		on expected.TableName = owned_table.name collate database_default
		and expected.ColumnName = actual.name collate database_default
	where owned_table.schema_id = schema_id('dbo')
		and owned_table.name in
		(
			'RegistrationFormCodeMetadata','RegistrationSettingScopeVersions',
			'RegistrationSettingDrafts','RegistrationSettingDraftChanges',
			'RegistrationSettingPreviewLinks','RegistrationSettingAuditEvents',
			'RegistrationSettingsCacheGeneration','RegistrationFormAssets',
			'RegistrationFormAssetReferenceLocks'
		)
		and expected.ColumnName is null

	if @incompatible_owned_object is not null
		raiserror('Owned schema column %s was not produced by a supported historical deployment. Restore the repository schema before rerunning.', 16, 1, @incompatible_owned_object)

	if (col_length('dbo.RegistrationSettingDrafts', 'Revision') is null
			and col_length('dbo.RegistrationSettingPreviewLinks', 'LiveSettingsGeneration') is not null)
		or (col_length('dbo.RegistrationSettingDrafts', 'Revision') is not null
			and col_length('dbo.RegistrationSettingPreviewLinks', 'LiveSettingsGeneration') is null)
		raiserror('Revision and LiveSettingsGeneration must both be absent in the known legacy schema or both be present. Restore a supported historical shape before rerunning.', 16, 1)

	if object_id('dbo.RegistrationFormAssets', 'U') is not null
		and
		(
			(col_length('dbo.RegistrationFormAssets', 'UploadOrganizationId') is null
				and col_length('dbo.RegistrationFormAssets', 'UploadFormCode') is not null)
			or (col_length('dbo.RegistrationFormAssets', 'UploadOrganizationId') is not null
				and col_length('dbo.RegistrationFormAssets', 'UploadFormCode') is null)
		)
		raiserror('The RegistrationFormAssets upload-scope columns must both be present or both be absent. Restore a supported historical shape before rerunning.', 16, 1)

	if object_id('dbo.RegistrationSettingAuditEvents', 'U') is not null
		and columnproperty(object_id('dbo.RegistrationSettingAuditEvents'), 'PreviousValue', 'ColumnId') is not null
		and
		(
			select max_length from sys.columns
			where object_id = object_id('dbo.RegistrationSettingAuditEvents') and name = 'PreviousValue'
		) <>
		(
			select max_length from sys.columns
			where object_id = object_id('dbo.RegistrationSettingAuditEvents') and name = 'NewValue'
		)
		raiserror('PreviousValue and NewValue must both have the original audit width or both be nvarchar(max). Restore a supported historical shape before rerunning.', 16, 1)

	if object_id('dbo.RegistrationFormAssets', 'U') is not null
		and
		(
			col_length('dbo.RegistrationSettingPreviewLinks', 'OperationalBranchId') is null
			or exists
			(
				select 1 from sys.columns
				where object_id = object_id('dbo.RegistrationSettingAuditEvents')
					and name in ('PreviousValue', 'NewValue') and max_length <> -1
			)
		)
		raiserror('dbo.RegistrationFormAssets requires the earlier operational-branch and expanded-audit releases. Restore a supported historical shape before rerunning.', 16, 1)

	if object_id('dbo.RegistrationFormAssetReferenceLocks', 'U') is not null
		and not exists
		(
			select 1 from sys.indexes
			where object_id = object_id('dbo.RegistrationFormAssets')
				and name = 'IX_RegistrationFormAssets_CreatedDate'
		)
		raiserror('dbo.RegistrationFormAssetReferenceLocks requires the earlier asset-cleanup index release. Restore a supported historical shape before rerunning.', 16, 1)

	if col_length('dbo.RegistrationSettingDrafts', 'Revision') is not null
		and object_id('dbo.RegistrationFormAssetReferenceLocks', 'U') is null
		raiserror('The revision/generation release requires dbo.RegistrationFormAssetReferenceLocks from the preceding historical release.', 16, 1)

	declare @expected_constraints table
	(
		TableName sysname not null,
		ConstraintName sysname not null,
		ConstraintType char(2) not null,
		IsHistoricalOptional bit not null,
		primary key (TableName, ConstraintName)
	)

	insert @expected_constraints values
		('RegistrationFormCodeMetadata','PK_RegistrationFormCodeMetadata','PK',0),
		('RegistrationFormCodeMetadata','CK_RFCode_NotBlank','C',0),
		('RegistrationFormCodeMetadata','DF_RFCode_Created','D',0),
		('RegistrationFormCodeMetadata','DF_RFCode_Modified','D',0),
		('RegistrationSettingScopeVersions','PK_RegistrationSettingScopeVersions','PK',0),
		('RegistrationSettingScopeVersions','DF_RSSV_Code','D',0),
		('RegistrationSettingScopeVersions','DF_RSSV_Version','D',0),
		('RegistrationSettingScopeVersions','DF_RSSV_Modified','D',0),
		('RegistrationSettingDrafts','PK_RegistrationSettingDrafts','PK',0),
		('RegistrationSettingDrafts','CK_RSD_Status','C',0),
		('RegistrationSettingDrafts','DF_RSD_Code','D',0),
		('RegistrationSettingDrafts','DF_RSD_Revision','D',1),
		('RegistrationSettingDrafts','DF_RSD_Created','D',0),
		('RegistrationSettingDrafts','DF_RSD_Modified','D',0),
		('RegistrationSettingDraftChanges','PK_RegistrationSettingDraftChanges','PK',0),
		('RegistrationSettingDraftChanges','FK_RSDC_Draft','F',0),
		('RegistrationSettingDraftChanges','UQ_RSDC_Key','UQ',0),
		('RegistrationSettingDraftChanges','CK_RSDC_Operation','C',0),
		('RegistrationSettingDraftChanges','CK_RSDC_Value','C',0),
		('RegistrationSettingDraftChanges','DF_RSDC_Modified','D',0),
		('RegistrationSettingPreviewLinks','PK_RegistrationSettingPreviewLinks','PK',0),
		('RegistrationSettingPreviewLinks','FK_RSPL_Draft','F',0),
		('RegistrationSettingPreviewLinks','UQ_RSPL_Token','UQ',0),
		('RegistrationSettingPreviewLinks','DF_RSPL_Live','D',0),
		('RegistrationSettingPreviewLinks','DF_RSPL_Created','D',0),
		('RegistrationSettingPreviewLinks','DF_RSPL_Modified','D',0),
		('RegistrationSettingAuditEvents','PK_RegistrationSettingAuditEvents','PK',0),
		('RegistrationSettingAuditEvents','CK_RSAE_Json','C',0),
		('RegistrationSettingAuditEvents','DF_RSAE_Time','D',0),
		('RegistrationSettingAuditEvents','DF_RSAE_Code','D',0),
		('RegistrationSettingAuditEvents','DF_RSAE_Secret','D',0),
		('RegistrationSettingsCacheGeneration','PK_RegistrationSettingsCacheGeneration','PK',0),
		('RegistrationFormAssets','PK_RegistrationFormAssets','PK',0),
		('RegistrationFormAssets','CK_RegistrationFormAssets_FileName_NotBlank','C',0),
		('RegistrationFormAssets','CK_RegistrationFormAssets_ContentType_NotBlank','C',0),
		('RegistrationFormAssets','CK_RegistrationFormAssets_Content_NotEmpty','C',0),
		('RegistrationFormAssets','CK_RegistrationFormAssets_ContentHash_Sha256','C',0),
		('RegistrationFormAssets','DF_RegistrationFormAssets_CreatedDate','D',0),
		('RegistrationFormAssets','DF_RegistrationFormAssets_ModifiedDate','D',0),
		('RegistrationFormAssetReferenceLocks','PK_RegistrationFormAssetReferenceLocks','PK',0),
		('RegistrationFormAssetReferenceLocks','CK_RegistrationFormAssetReferenceLocks_Singleton','C',0)

	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.ConstraintName
	from @expected_constraints expected
	where object_id('dbo.' + expected.TableName, 'U') is not null
		and not (expected.IsHistoricalOptional = 1
			and col_length('dbo.RegistrationSettingDrafts', 'Revision') is null)
		and not exists
		(
			select 1 from sys.objects actual
			where actual.parent_object_id = object_id('dbo.' + expected.TableName)
				and actual.name collate database_default = expected.ConstraintName
				and actual.type collate database_default = expected.ConstraintType
		)

	if @incompatible_owned_object is not null
		raiserror('Owned constraint %s is missing or incompatible. This is not a supported historical state; restore it before rerunning.', 16, 1, @incompatible_owned_object)

	/*
	   sys.check_constraints.definition is SQL Server's rendered expression,
	   rather than the source text used by CREATE TABLE. Normalize the stable
	   rendering differences (case, brackets, whitespace, and one redundant
	   outer parenthesis pair) but compare the complete expression.
	   This keeps a same-name constraint with a weaker invariant outside the
	   supported state space.
	*/
	declare @expected_check_constraints table
	(
		TableName sysname not null,
		ConstraintName sysname not null,
		CanonicalDefinition nvarchar(1000) not null,
		IsHistoricalOptional bit not null,
		primary key (TableName, ConstraintName)
	)

	insert @expected_check_constraints values
		('RegistrationFormCodeMetadata','CK_RFCode_NotBlank','len(formcode)>(0)',0),
		('RegistrationSettingDrafts','CK_RSD_Status','status=''Invalidated''orstatus=''Discarded''orstatus=''Committed''orstatus=''Active''',0),
		('RegistrationSettingDraftChanges','CK_RSDC_Operation','operation=''RemoveOverride''oroperation=''Upsert''',0),
		('RegistrationSettingDraftChanges','CK_RSDC_Value','operation=''Upsert''andvalueisnotnulloroperation=''RemoveOverride''andvalueisnull',0),
		('RegistrationSettingAuditEvents','CK_RSAE_Json','metadatajsonisnullorisjson(metadatajson)=(1)',0),
		('RegistrationFormAssets','CK_RegistrationFormAssets_FileName_NotBlank','len(ltrim(rtrim(filename)))>(0)',0),
		('RegistrationFormAssets','CK_RegistrationFormAssets_ContentType_NotBlank','len(ltrim(rtrim(contenttype)))>(0)',0),
		('RegistrationFormAssets','CK_RegistrationFormAssets_Content_NotEmpty','datalength(content)>(0)',0),
		('RegistrationFormAssets','CK_RegistrationFormAssets_ContentHash_Sha256','len(contenthash)=(64)',0),
		('RegistrationFormAssetReferenceLocks','CK_RegistrationFormAssetReferenceLocks_Singleton','lockid=(1)',0)

	/*
	   Canonicalize only SQL text outside single-quoted literals. Removing
	   whitespace or brackets from a literal would allow a materially different
	   same-name constraint, such as Status = 'Act ive', to pass validation.
	   The character-position CTE preserves literal contents while accepting
	   SQL Server's case, bracket, whitespace, and one outer-parenthesis
	   rendering differences.
	*/
	declare @canonical_owned_definitions table
	(
		DefinitionKey nvarchar(100) not null primary key,
		CanonicalDefinition nvarchar(max) null
	)
	declare @max_definition_length int
	select @max_definition_length = isnull(max(DefinitionLength), 0)
	from
	(
		select len(check_constraint.definition) DefinitionLength
		from sys.check_constraints check_constraint
		where check_constraint.parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingDrafts'),
			object_id('dbo.RegistrationSettingDraftChanges'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets'), object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
		union all
		select len(default_constraint.definition) DefinitionLength
		from sys.default_constraints default_constraint
		where default_constraint.parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingScopeVersions'),
			object_id('dbo.RegistrationSettingDrafts'), object_id('dbo.RegistrationSettingDraftChanges'),
			object_id('dbo.RegistrationSettingPreviewLinks'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets')
		)
		union all
		select len(index_object.filter_definition) DefinitionLength
		from sys.indexes index_object
		where index_object.object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingScopeVersions'),
			object_id('dbo.RegistrationSettingDrafts'), object_id('dbo.RegistrationSettingDraftChanges'),
			object_id('dbo.RegistrationSettingPreviewLinks'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationSettingsCacheGeneration'), object_id('dbo.RegistrationFormAssets'),
			object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
			and index_object.has_filter = 1
			and index_object.filter_definition is not null
	) lengths

	;with OwnedDefinitions as
	(
		select convert(nvarchar(100), N'C:' + convert(nvarchar(20), check_constraint.object_id)) DefinitionKey,
			check_constraint.definition DefinitionText
		from sys.check_constraints check_constraint
		where check_constraint.parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingDrafts'),
			object_id('dbo.RegistrationSettingDraftChanges'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets'), object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
		union all
		select convert(nvarchar(100), N'D:' + convert(nvarchar(20), default_constraint.object_id)) DefinitionKey,
			default_constraint.definition DefinitionText
		from sys.default_constraints default_constraint
		where default_constraint.parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingScopeVersions'),
			object_id('dbo.RegistrationSettingDrafts'), object_id('dbo.RegistrationSettingDraftChanges'),
			object_id('dbo.RegistrationSettingPreviewLinks'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets')
		)
		union all
		select convert(nvarchar(100), N'I:' + convert(nvarchar(20), index_object.object_id) + N':' + convert(nvarchar(20), index_object.index_id)) DefinitionKey,
			index_object.filter_definition DefinitionText
		from sys.indexes index_object
		where index_object.object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingScopeVersions'),
			object_id('dbo.RegistrationSettingDrafts'), object_id('dbo.RegistrationSettingDraftChanges'),
			object_id('dbo.RegistrationSettingPreviewLinks'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationSettingsCacheGeneration'), object_id('dbo.RegistrationFormAssets'),
			object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
			and index_object.has_filter = 1
			and index_object.filter_definition is not null
	),
	Numbers as
	(
		select 1 Number
		union all
		select Number + 1
		from Numbers
		where Number < @max_definition_length
	),
	CanonicalDefinitions as
	(
		select source.DefinitionKey,
			(
				select case
					when substring(source.DefinitionText, Number, 1) = NCHAR(39)
						then substring(source.DefinitionText, Number, 1)
					when
						(
							len(left(source.DefinitionText, Number - 1))
							- len(replace(left(source.DefinitionText, Number - 1), NCHAR(39), N''))
						) % 2 = 1
						then substring(source.DefinitionText, Number, 1)
					when substring(source.DefinitionText, Number, 1) not in
						(N'[', N']', N' ', NCHAR(9), NCHAR(10), NCHAR(12), NCHAR(13), NCHAR(160))
						then lower(substring(source.DefinitionText, Number, 1))
					else N''
				end
				from Numbers
				where Number <= len(source.DefinitionText)
				order by Number
				for xml path(''), type
			).value('.', 'nvarchar(max)') RawCanonicalDefinition
		from OwnedDefinitions source
	)
	insert @canonical_owned_definitions (DefinitionKey, CanonicalDefinition)
	select DefinitionKey,
		case
			when left(RawCanonicalDefinition, 1) = N'('
				and right(RawCanonicalDefinition, 1) = N')'
			then substring(RawCanonicalDefinition, 2, len(RawCanonicalDefinition) - 2)
			else RawCanonicalDefinition
		end
	from CanonicalDefinitions
	option (maxrecursion 0)

	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.ConstraintName
	from @expected_check_constraints expected
	where object_id('dbo.' + expected.TableName, 'U') is not null
		and not (expected.IsHistoricalOptional = 1
			and col_length('dbo.RegistrationSettingDrafts', 'Revision') is null)
		and not exists
		(
			select 1
			from sys.check_constraints actual
			inner join @canonical_owned_definitions canonical
				on canonical.DefinitionKey = N'C:' + convert(nvarchar(20), actual.object_id)
			where actual.parent_object_id = object_id('dbo.' + expected.TableName)
				and actual.name collate database_default = expected.ConstraintName
				and actual.is_disabled = 0
				and actual.is_not_trusted = 0
				and canonical.CanonicalDefinition collate Latin1_General_100_BIN2 = expected.CanonicalDefinition collate Latin1_General_100_BIN2
		)

	if @incompatible_owned_object is not null
		raiserror('Owned check constraint %s is missing, untrusted, or has an unsupported definition. Restore it before rerunning.', 16, 1, @incompatible_owned_object)

	declare @expected_foreign_keys table
	(
		TableName sysname not null,
		ConstraintName sysname not null,
		ReferencedTableName sysname not null,
		ParentColumnName sysname not null,
		ReferencedColumnName sysname not null,
		DeleteReferentialAction tinyint not null,
		UpdateReferentialAction tinyint not null,
		IsHistoricalOptional bit not null,
		primary key (TableName, ConstraintName)
	)

	insert @expected_foreign_keys values
		('RegistrationSettingDraftChanges','FK_RSDC_Draft','RegistrationSettingDrafts','DraftId','DraftId',1,0,0),
		('RegistrationSettingPreviewLinks','FK_RSPL_Draft','RegistrationSettingDrafts','DraftId','DraftId',1,0,0)

	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.ConstraintName
	from @expected_foreign_keys expected
	where object_id('dbo.' + expected.TableName, 'U') is not null
		and not (expected.IsHistoricalOptional = 1
			and col_length('dbo.RegistrationSettingDrafts', 'Revision') is null)
		and not exists
		(
			select 1
			from sys.foreign_keys actual
			where actual.parent_object_id = object_id('dbo.' + expected.TableName)
				and actual.name collate database_default = expected.ConstraintName
				and actual.referenced_object_id = object_id('dbo.' + expected.ReferencedTableName)
				and actual.delete_referential_action = expected.DeleteReferentialAction
				and actual.update_referential_action = expected.UpdateReferentialAction
				and actual.is_disabled = 0
				and actual.is_not_trusted = 0
				and actual.is_not_for_replication = 0
				and
				(
					select count(*)
					from sys.foreign_key_columns actual_column
					where actual_column.constraint_object_id = actual.object_id
				) = 1
				and exists
				(
					select 1
					from sys.foreign_key_columns actual_column
					inner join sys.columns parent_column
						on parent_column.object_id = actual.parent_object_id
						and parent_column.column_id = actual_column.parent_column_id
					inner join sys.columns referenced_column
						on referenced_column.object_id = actual.referenced_object_id
						and referenced_column.column_id = actual_column.referenced_column_id
					where actual_column.constraint_object_id = actual.object_id
						and actual_column.constraint_column_id = 1
						and parent_column.name collate database_default = expected.ParentColumnName
						and referenced_column.name collate database_default = expected.ReferencedColumnName
				)
			)

	if @incompatible_owned_object is not null
		raiserror('Owned foreign key %s is missing, untrusted, disabled, or has an unsupported relationship definition. Restore it before rerunning.', 16, 1, @incompatible_owned_object)

	/* Defaults are part of the repository schema contract, not arbitrary app data. */
	declare @expected_defaults table
	(
		TableName sysname not null,
		ConstraintName sysname not null,
		ColumnName sysname not null,
		CanonicalDefinition nvarchar(200) not null,
		IsHistoricalOptional bit not null,
		primary key (TableName, ConstraintName)
	)

	insert @expected_defaults values
		('RegistrationFormCodeMetadata','DF_RFCode_Created','CreatedAtUtc','sysutcdatetime()',0),
		('RegistrationFormCodeMetadata','DF_RFCode_Modified','ModifiedAtUtc','sysutcdatetime()',0),
		('RegistrationSettingScopeVersions','DF_RSSV_Code','FormCode','''''',0),
		('RegistrationSettingScopeVersions','DF_RSSV_Version','Version','(0)',0),
		('RegistrationSettingScopeVersions','DF_RSSV_Modified','ModifiedAtUtc','sysutcdatetime()',0),
		('RegistrationSettingDrafts','DF_RSD_Code','FormCode','''''',0),
		('RegistrationSettingDrafts','DF_RSD_Revision','Revision','(0)',1),
		('RegistrationSettingDrafts','DF_RSD_Created','CreatedAtUtc','sysutcdatetime()',0),
		('RegistrationSettingDrafts','DF_RSD_Modified','ModifiedAtUtc','sysutcdatetime()',0),
		('RegistrationSettingDraftChanges','DF_RSDC_Modified','ModifiedAtUtc','sysutcdatetime()',0),
		('RegistrationSettingPreviewLinks','DF_RSPL_Live','AllowLiveSubmission','(0)',0),
		('RegistrationSettingPreviewLinks','DF_RSPL_Created','CreatedAtUtc','sysutcdatetime()',0),
		('RegistrationSettingPreviewLinks','DF_RSPL_Modified','ModifiedAtUtc','sysutcdatetime()',0),
		('RegistrationSettingAuditEvents','DF_RSAE_Time','TimestampUtc','sysutcdatetime()',0),
		('RegistrationSettingAuditEvents','DF_RSAE_Code','FormCode','''''',0),
		('RegistrationSettingAuditEvents','DF_RSAE_Secret','IsSensitive','(0)',0),
		('RegistrationFormAssets','DF_RegistrationFormAssets_CreatedDate','CreatedDate','sysutcdatetime()',0),
		('RegistrationFormAssets','DF_RegistrationFormAssets_ModifiedDate','ModifiedDate','sysutcdatetime()',0)

	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.ConstraintName
	from @expected_defaults expected
	where object_id('dbo.' + expected.TableName, 'U') is not null
		and not (expected.IsHistoricalOptional = 1
			and col_length('dbo.RegistrationSettingDrafts', 'Revision') is null)
		and not exists
		(
			select 1
			from sys.default_constraints actual
			inner join @canonical_owned_definitions canonical
				on canonical.DefinitionKey = N'D:' + convert(nvarchar(20), actual.object_id)
			where actual.parent_object_id = object_id('dbo.' + expected.TableName)
				and actual.name collate database_default = expected.ConstraintName
				and actual.parent_column_id = columnproperty(object_id('dbo.' + expected.TableName), expected.ColumnName, 'ColumnId')
				and canonical.CanonicalDefinition collate Latin1_General_100_BIN2 = expected.CanonicalDefinition collate Latin1_General_100_BIN2
		)

	if @incompatible_owned_object is not null
		raiserror('Owned default constraint %s is missing, bound to the wrong column, or has an unsupported definition. Restore it before rerunning.', 16, 1, @incompatible_owned_object)

	if exists
	(
		select 1 from sys.check_constraints
		where parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingDrafts'),
			object_id('dbo.RegistrationSettingDraftChanges'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets'), object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
			and (is_disabled = 1 or is_not_trusted = 1)
	)
		raiserror('A patron-registration-owned check constraint is disabled or untrusted. Re-enable and validate it before rerunning.', 16, 1)

	if exists
	(
		select 1 from sys.foreign_keys
		where parent_object_id in
			(object_id('dbo.RegistrationSettingDraftChanges'), object_id('dbo.RegistrationSettingPreviewLinks'))
			and (is_disabled = 1 or is_not_trusted = 1 or delete_referential_action <> 1)
	)
		raiserror('A patron-registration-owned draft foreign key is disabled, untrusted, or does not cascade deletes. Restore the repository definition before rerunning.', 16, 1)

	declare @expected_indexes table
	(
		TableName sysname not null,
		IndexName sysname not null,
		IsUnique bit not null,
		HasFilter bit not null,
		KeyColumns nvarchar(500) not null,
		CanonicalFilterDefinition nvarchar(500) null,
		IsHistoricalOptional bit not null,
		primary key (TableName, IndexName)
	)

	insert @expected_indexes values
		('RegistrationFormCodeMetadata','PK_RegistrationFormCodeMetadata',1,0,'OrganizationId:A,FormCode:A',null,0),
		('RegistrationSettingScopeVersions','PK_RegistrationSettingScopeVersions',1,0,'OrganizationId:A,FormCode:A',null,0),
		('RegistrationSettingDrafts','PK_RegistrationSettingDrafts',1,0,'DraftId:A',null,0),
		('RegistrationSettingDrafts','UX_RSD_ActiveScope',1,1,'OrganizationId:A,FormCode:A','status=''Active''',0),
		('RegistrationSettingDraftChanges','PK_RegistrationSettingDraftChanges',1,0,'DraftChangeId:A',null,0),
		('RegistrationSettingDraftChanges','UQ_RSDC_Key',1,0,'DraftId:A,SettingKey:A',null,0),
		('RegistrationSettingPreviewLinks','PK_RegistrationSettingPreviewLinks',1,0,'PreviewLinkId:A',null,0),
		('RegistrationSettingPreviewLinks','UQ_RSPL_Token',1,0,'TokenHash:A',null,0),
		('RegistrationSettingAuditEvents','PK_RegistrationSettingAuditEvents',1,0,'AuditEventId:A',null,0),
		('RegistrationSettingAuditEvents','IX_RSAE_LibraryTime',0,0,'TargetLibraryId:A,TimestampUtc:D',null,0),
		('RegistrationSettingAuditEvents','IX_RSAE_ScopeFilter',0,0,'TargetOrganizationId:A,FormCode:A,EventType:A,TimestampUtc:D',null,0),
		('RegistrationSettingsCacheGeneration','PK_RegistrationSettingsCacheGeneration',1,0,'Id:A',null,0),
		('RegistrationFormAssets','PK_RegistrationFormAssets',1,0,'AssetId:A',null,0),
		('RegistrationFormAssets','IX_RegistrationFormAssets_UploadScope',0,0,'UploadOrganizationId:A,UploadFormCode:A',null,1),
		('RegistrationFormAssets','IX_RegistrationFormAssets_CreatedDate',0,0,'CreatedDate:A',null,1),
		('RegistrationFormAssetReferenceLocks','PK_RegistrationFormAssetReferenceLocks',1,0,'LockId:A',null,0)

	declare @actual_indexes table
	(
		TableName sysname not null,
		IndexName sysname not null,
		IsUnique bit not null,
		HasFilter bit not null,
		CanonicalFilterDefinition nvarchar(max) null,
		IsDisabled bit not null,
		KeyColumns nvarchar(500) not null,
		primary key (TableName, IndexName)
	)

	insert @actual_indexes
	select
		table_object.name,
		index_object.name,
		index_object.is_unique,
		index_object.has_filter,
		canonical.CanonicalDefinition,
		index_object.is_disabled,
		coalesce(keys.KeyColumns, '')
	from sys.tables table_object
	inner join sys.indexes index_object
		on index_object.object_id = table_object.object_id and index_object.index_id > 0
	outer apply
	(
		select stuff
		(
			(
				select ',' + column_object.name
					+ case when index_column.is_descending_key = 1 then ':D' else ':A' end
				from sys.index_columns index_column
				inner join sys.columns column_object
					on column_object.object_id = index_column.object_id
					and column_object.column_id = index_column.column_id
				where index_column.object_id = index_object.object_id
					and index_column.index_id = index_object.index_id
					and index_column.key_ordinal > 0
				order by index_column.key_ordinal
				for xml path(''), type
			).value('.', 'nvarchar(max)'),
			1, 1, ''
			) KeyColumns
		) keys
	left join @canonical_owned_definitions canonical
		on canonical.DefinitionKey = N'I:' + convert(nvarchar(20), index_object.object_id) + N':' + convert(nvarchar(20), index_object.index_id)
	where table_object.schema_id = schema_id('dbo')

	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.IndexName
	from @expected_indexes expected
	left join @actual_indexes actual
		on actual.TableName = expected.TableName and actual.IndexName = expected.IndexName
	where object_id('dbo.' + expected.TableName, 'U') is not null
		and
		(
			(actual.IndexName is null and not
				(expected.IsHistoricalOptional = 1 and
					(expected.IndexName = 'IX_RegistrationFormAssets_CreatedDate'
						or col_length('dbo.RegistrationFormAssets', 'UploadOrganizationId') is null)))
			or
			(actual.IndexName is not null and
				(actual.IsUnique <> expected.IsUnique
					or actual.HasFilter <> expected.HasFilter
					or actual.IsDisabled <> 0
					or actual.KeyColumns <> expected.KeyColumns
					or (actual.CanonicalFilterDefinition collate Latin1_General_100_BIN2 <> expected.CanonicalFilterDefinition collate Latin1_General_100_BIN2
						or (actual.CanonicalFilterDefinition is null and expected.CanonicalFilterDefinition is not null)
						or (actual.CanonicalFilterDefinition is not null and expected.CanonicalFilterDefinition is null))))
		)

	if @incompatible_owned_object is not null
		raiserror('Owned index %s is missing or incompatible with every supported historical definition. Restore it before rerunning.', 16, 1, @incompatible_owned_object)

	/* 3. Creation of missing current objects */
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

	/* 4. Upgrades from known historical states */
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


	/* 5. Required data transformations */
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
	   historical records, and safe-preview links are not affected by the live
	   generation contract. A generation-bound link is never rewritten here.
	*/
	update preview_link
	set RevokedAtUtc = sysutcdatetime(),
		RevokedBy = coalesce(RevokedBy, 'settings-administration.sql'),
		ModifiedAtUtc = sysutcdatetime(),
		ModifiedBy = 'settings-administration.sql'
	from dbo.RegistrationSettingPreviewLinks preview_link
	inner join dbo.RegistrationSettingDrafts draft
	on draft.DraftId = preview_link.DraftId
	where preview_link.AllowLiveSubmission = 1
		and preview_link.LiveSettingsGeneration is null
		and preview_link.RevokedAtUtc is null
		and draft.Status = 'Active'



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

	update draft
	set Revision = draft.Revision + 1
	from dbo.RegistrationSettingDrafts draft
	inner join #SettingsAdministrationChangedDrafts changed_draft
	on changed_draft.DraftId = draft.DraftId
	where draft.Status = 'Active'

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

	/* Refresh rendered definitions because this run may have created legacy objects. */
	delete from @canonical_owned_definitions

	select @max_definition_length = isnull(max(DefinitionLength), 0)
	from
	(
		select len(check_constraint.definition) DefinitionLength
		from sys.check_constraints check_constraint
		where check_constraint.parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingDrafts'),
			object_id('dbo.RegistrationSettingDraftChanges'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets'), object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
		union all
		select len(default_constraint.definition) DefinitionLength
		from sys.default_constraints default_constraint
		where default_constraint.parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingScopeVersions'),
			object_id('dbo.RegistrationSettingDrafts'), object_id('dbo.RegistrationSettingDraftChanges'),
			object_id('dbo.RegistrationSettingPreviewLinks'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets')
		)
		union all
		select len(index_object.filter_definition) DefinitionLength
		from sys.indexes index_object
		where index_object.object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingScopeVersions'),
			object_id('dbo.RegistrationSettingDrafts'), object_id('dbo.RegistrationSettingDraftChanges'),
			object_id('dbo.RegistrationSettingPreviewLinks'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationSettingsCacheGeneration'), object_id('dbo.RegistrationFormAssets'),
			object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
			and index_object.has_filter = 1
			and index_object.filter_definition is not null
	) lengths

	;with OwnedDefinitions as
	(
		select convert(nvarchar(100), N'C:' + convert(nvarchar(20), check_constraint.object_id)) DefinitionKey,
			check_constraint.definition DefinitionText
		from sys.check_constraints check_constraint
		where check_constraint.parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingDrafts'),
			object_id('dbo.RegistrationSettingDraftChanges'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets'), object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
		union all
		select convert(nvarchar(100), N'D:' + convert(nvarchar(20), default_constraint.object_id)) DefinitionKey,
			default_constraint.definition DefinitionText
		from sys.default_constraints default_constraint
		where default_constraint.parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingScopeVersions'),
			object_id('dbo.RegistrationSettingDrafts'), object_id('dbo.RegistrationSettingDraftChanges'),
			object_id('dbo.RegistrationSettingPreviewLinks'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets')
		)
		union all
		select convert(nvarchar(100), N'I:' + convert(nvarchar(20), index_object.object_id) + N':' + convert(nvarchar(20), index_object.index_id)) DefinitionKey,
			index_object.filter_definition DefinitionText
		from sys.indexes index_object
		where index_object.object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingScopeVersions'),
			object_id('dbo.RegistrationSettingDrafts'), object_id('dbo.RegistrationSettingDraftChanges'),
			object_id('dbo.RegistrationSettingPreviewLinks'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationSettingsCacheGeneration'), object_id('dbo.RegistrationFormAssets'),
			object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
			and index_object.has_filter = 1
			and index_object.filter_definition is not null
	),
	Numbers as
	(
		select 1 Number
		union all
		select Number + 1
		from Numbers
		where Number < @max_definition_length
	),
	CanonicalDefinitions as
	(
		select source.DefinitionKey,
			(
				select case
					when substring(source.DefinitionText, Number, 1) = NCHAR(39)
						then substring(source.DefinitionText, Number, 1)
					when
						(
							len(left(source.DefinitionText, Number - 1))
							- len(replace(left(source.DefinitionText, Number - 1), NCHAR(39), N''))
						) % 2 = 1
						then substring(source.DefinitionText, Number, 1)
					when substring(source.DefinitionText, Number, 1) not in
						(N'[', N']', N' ', NCHAR(9), NCHAR(10), NCHAR(12), NCHAR(13), NCHAR(160))
						then lower(substring(source.DefinitionText, Number, 1))
					else N''
				end
				from Numbers
				where Number <= len(source.DefinitionText)
				order by Number
				for xml path(''), type
			).value('.', 'nvarchar(max)') RawCanonicalDefinition
		from OwnedDefinitions source
	)
	insert @canonical_owned_definitions (DefinitionKey, CanonicalDefinition)
	select DefinitionKey,
		case
			when left(RawCanonicalDefinition, 1) = N'('
				and right(RawCanonicalDefinition, 1) = N')'
			then substring(RawCanonicalDefinition, 2, len(RawCanonicalDefinition) - 2)
			else RawCanonicalDefinition
		end
	from CanonicalDefinitions
	option (maxrecursion 0)

	/* 6. Final invariant validation */
	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.ColumnName
	from @owned_columns expected
	left join sys.columns actual
		on actual.object_id = object_id('dbo.' + expected.TableName)
		and actual.name collate database_default = expected.ColumnName
	where actual.column_id is null
		or actual.system_type_id <> expected.SystemTypeId
		or actual.max_length <> expected.MaxLength
		or actual.is_nullable <> expected.IsNullable
		or actual.is_identity <> expected.IsIdentity

	if @incompatible_owned_object is not null
		raiserror('Final owned schema column invariant failed for %s.', 16, 1, @incompatible_owned_object)

	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.ConstraintName
	from @expected_constraints expected
	where not exists
	(
		select 1 from sys.objects actual
		where actual.parent_object_id = object_id('dbo.' + expected.TableName)
			and actual.name collate database_default = expected.ConstraintName
			and actual.type collate database_default = expected.ConstraintType
	)

	if @incompatible_owned_object is not null
		raiserror('Final owned constraint invariant failed for %s.', 16, 1, @incompatible_owned_object)

	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.ConstraintName
	from @expected_check_constraints expected
	where not exists
	(
		select 1
		from sys.check_constraints actual
		inner join @canonical_owned_definitions canonical
			on canonical.DefinitionKey = N'C:' + convert(nvarchar(20), actual.object_id)
		where actual.parent_object_id = object_id('dbo.' + expected.TableName)
			and actual.name collate database_default = expected.ConstraintName
			and actual.is_disabled = 0
			and actual.is_not_trusted = 0
			and canonical.CanonicalDefinition collate Latin1_General_100_BIN2 = expected.CanonicalDefinition collate Latin1_General_100_BIN2
	)

	if @incompatible_owned_object is not null
		raiserror('Final owned check constraint invariant failed for %s.', 16, 1, @incompatible_owned_object)

	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.ConstraintName
	from @expected_defaults expected
	where not exists
	(
		select 1
		from sys.default_constraints actual
		inner join @canonical_owned_definitions canonical
			on canonical.DefinitionKey = N'D:' + convert(nvarchar(20), actual.object_id)
		where actual.parent_object_id = object_id('dbo.' + expected.TableName)
			and actual.name collate database_default = expected.ConstraintName
			and actual.parent_column_id = columnproperty(object_id('dbo.' + expected.TableName), expected.ColumnName, 'ColumnId')
			and canonical.CanonicalDefinition collate Latin1_General_100_BIN2 = expected.CanonicalDefinition collate Latin1_General_100_BIN2
		)

	if @incompatible_owned_object is not null
		raiserror('Final owned default constraint invariant failed for %s.', 16, 1, @incompatible_owned_object)

	set @incompatible_owned_object = null

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.ConstraintName
	from @expected_foreign_keys expected
	where not exists
	(
		select 1
		from sys.foreign_keys actual
		where actual.parent_object_id = object_id('dbo.' + expected.TableName)
			and actual.name collate database_default = expected.ConstraintName
			and actual.referenced_object_id = object_id('dbo.' + expected.ReferencedTableName)
			and actual.delete_referential_action = expected.DeleteReferentialAction
			and actual.update_referential_action = expected.UpdateReferentialAction
			and actual.is_disabled = 0
			and actual.is_not_trusted = 0
			and actual.is_not_for_replication = 0
			and
			(
				select count(*)
				from sys.foreign_key_columns actual_column
				where actual_column.constraint_object_id = actual.object_id
			) = 1
			and exists
			(
				select 1
				from sys.foreign_key_columns actual_column
				inner join sys.columns parent_column
					on parent_column.object_id = actual.parent_object_id
					and parent_column.column_id = actual_column.parent_column_id
				inner join sys.columns referenced_column
					on referenced_column.object_id = actual.referenced_object_id
					and referenced_column.column_id = actual_column.referenced_column_id
				where actual_column.constraint_object_id = actual.object_id
					and actual_column.constraint_column_id = 1
					and parent_column.name collate database_default = expected.ParentColumnName
					and referenced_column.name collate database_default = expected.ReferencedColumnName
			)
		)

	if @incompatible_owned_object is not null
		raiserror('Final owned foreign key invariant failed for %s.', 16, 1, @incompatible_owned_object)

	/* Re-read indexes because missing current indexes may have been created above. */
	delete from @actual_indexes

	insert @actual_indexes
	select
		table_object.name,
		index_object.name,
		index_object.is_unique,
		index_object.has_filter,
		canonical.CanonicalDefinition,
		index_object.is_disabled,
		coalesce(keys.KeyColumns, '')
	from sys.tables table_object
	inner join sys.indexes index_object
		on index_object.object_id = table_object.object_id and index_object.index_id > 0
	outer apply
	(
		select stuff
		(
			(
				select ',' + column_object.name
					+ case when index_column.is_descending_key = 1 then ':D' else ':A' end
				from sys.index_columns index_column
				inner join sys.columns column_object
					on column_object.object_id = index_column.object_id
					and column_object.column_id = index_column.column_id
				where index_column.object_id = index_object.object_id
					and index_column.index_id = index_object.index_id
					and index_column.key_ordinal > 0
				order by index_column.key_ordinal
				for xml path(''), type
			).value('.', 'nvarchar(max)'),
			1, 1, ''
			) KeyColumns
		) keys
	left join @canonical_owned_definitions canonical
		on canonical.DefinitionKey = N'I:' + convert(nvarchar(20), index_object.object_id) + N':' + convert(nvarchar(20), index_object.index_id)
	where table_object.schema_id = schema_id('dbo')

	select top (1) @incompatible_owned_object = 'dbo.' + expected.TableName + '.' + expected.IndexName
	from @expected_indexes expected
	left join @actual_indexes actual
		on actual.TableName = expected.TableName and actual.IndexName = expected.IndexName
	where actual.IndexName is null
		or actual.IsUnique <> expected.IsUnique
		or actual.HasFilter <> expected.HasFilter
		or actual.IsDisabled <> 0
		or actual.KeyColumns <> expected.KeyColumns
		or (actual.CanonicalFilterDefinition collate Latin1_General_100_BIN2 <> expected.CanonicalFilterDefinition collate Latin1_General_100_BIN2
			or (actual.CanonicalFilterDefinition is null and expected.CanonicalFilterDefinition is not null)
			or (actual.CanonicalFilterDefinition is not null and expected.CanonicalFilterDefinition is null))

	if @incompatible_owned_object is not null
		raiserror('Final owned index invariant failed for %s.', 16, 1, @incompatible_owned_object)

	if exists
	(
		select 1 from sys.check_constraints
		where parent_object_id in
		(
			object_id('dbo.RegistrationFormCodeMetadata'), object_id('dbo.RegistrationSettingDrafts'),
			object_id('dbo.RegistrationSettingDraftChanges'), object_id('dbo.RegistrationSettingAuditEvents'),
			object_id('dbo.RegistrationFormAssets'), object_id('dbo.RegistrationFormAssetReferenceLocks')
		)
			and (is_disabled = 1 or is_not_trusted = 1)
	)
		raiserror('A settings-administration check constraint is disabled or untrusted after deployment.', 16, 1)

	if exists
	(
		select 1 from sys.foreign_keys
		where parent_object_id in
			(object_id('dbo.RegistrationSettingDraftChanges'), object_id('dbo.RegistrationSettingPreviewLinks'))
			and (is_disabled = 1 or is_not_trusted = 1 or delete_referential_action <> 1)
	)
		raiserror('A settings-administration draft foreign key is incompatible after deployment.', 16, 1)

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
				from @setting_map
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
				from @setting_map
			)
	)
	begin
		raiserror('One or more retired settings remain in RegistrationFormSettingTypes after deployment.', 16, 1)
	end

	if exists
	(
		select 1
		from dbo.RegistrationSettingDraftChanges as draft_change
		inner join dbo.RegistrationSettingDrafts as draft
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
	)
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
		raiserror('RegistrationSettingsCacheGeneration must contain exactly one singleton row.', 16, 1)

	if (select count(*) from dbo.RegistrationFormAssetReferenceLocks) <> 1
		raiserror('RegistrationFormAssetReferenceLocks must contain exactly one singleton row.', 16, 1)

	if exists (select 1 from dbo.RegistrationSettingScopeVersions where Version < 0)
		or exists (select 1 from dbo.RegistrationSettingDrafts where BaselineVersion < 0 or Revision < 0)
		raiserror('Settings administration versions must be non-negative after deployment.', 16, 1)

	if exists
	(
		select 1
		from dbo.RegistrationSettingPreviewLinks preview_link
		inner join dbo.RegistrationSettingDrafts draft on draft.DraftId = preview_link.DraftId
		cross join dbo.RegistrationSettingsCacheGeneration generation
		where preview_link.AllowLiveSubmission = 1
			and preview_link.RevokedAtUtc is null
			and draft.Status = 'Active'
			and
			(
				preview_link.LiveSettingsGeneration is null
				or preview_link.LiveSettingsGeneration > generation.Generation
			)
	)
		raiserror('An active live-preview link has an invalid settings generation after deployment.', 16, 1)

	if exists
	(
		select 1
		from dbo.RegistrationSettingPreviewLinks
		where RevokedAtUtc is null and OperationalBranchId = -2147483648
	)
		raiserror('An unrevoked preview link has the unknown operational-branch sentinel. Revoke or replace the link before deployment.', 16, 1)

	drop table #SettingsAdministrationChangedDrafts

	commit transaction
end try
begin catch
	if @deployment_transaction_started = 1 and xact_state() <> 0
		rollback transaction;
	throw
end catch
