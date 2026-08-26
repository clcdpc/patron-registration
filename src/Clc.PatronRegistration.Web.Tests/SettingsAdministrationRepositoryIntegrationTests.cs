using System.Data;
using Clc.Melissa;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Clc.Rest;
using Clc.Rest.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;

#nullable enable

namespace Clc.PatronRegistration.Tests;

internal enum DatabaseCleanupMode { None, BestEffort, Strict }

internal static class DatabaseCleanupModeSelector
{
    internal static DatabaseCleanupMode Select(string? configured, string? unavailableReason) =>
        string.IsNullOrWhiteSpace(configured)
            ? DatabaseCleanupMode.None
            : unavailableReason is not null
                ? DatabaseCleanupMode.BestEffort
                : DatabaseCleanupMode.Strict;
}

[TestClass]
[DoNotParallelize]
public sealed class SettingsAdministrationRepositoryIntegrationTests
{
    private const string ConnectionVariable = "PATRON_REGISTRATION_TEST_SQL_CONNECTION_STRING";
    private static string? databaseName;
    private static string? databaseConnectionString;
    private static string? unavailableReason;
    private static bool sqlIntegrationConfigured;
    private static bool databaseCreated;
    private static bool schemaReady;
    private static TestContext? classContext;
    private SettingsAdministrationRepository repository = null!;
    private RegistrationFormAssetRepository assetRepository = null!;
    private MutableTimeProvider clock = null!;

    private static readonly SettingDefinition First = new("test.first", "First", "Test value", SettingValueType.ShortString);
    private static readonly SettingDefinition Second = new("test.second", "Second", "Test value", SettingValueType.ShortString);
    private static readonly SettingDefinition Html = new("registration_form_header", "Registration header", "HTML value", SettingValueType.ShortString,
        IsHtmlExecutionContext: true);
    private static readonly SettingDefinition Secret = new("test.secret", "Secret", "Test secret", SettingValueType.ShortString, IsSensitive: true);
    private static readonly SettingDefinition RetiredHeaderImageUrl = new("header_image_url", "Retired header image URL", "Retired setting", SettingValueType.Uri);
    private static readonly IReadOnlyDictionary<string, SettingDefinition> Catalog =
        new[] { First, Second, Html, Secret }.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

    // Represents the setting-type rows from the old database state before the
    // header-image migrations. This is an intentionally explicit fixture
    // contract, not a projection of SettingCatalog: migration 007 must remove
    // header_image_url, and the compatibility test below must detect a newly
    // administrable ordinary key omitted from the database allowlist.
    private static readonly string[] ExistingSettingTypeKeys =
    [
        "header_image_url", "css_file", "warning_text", "custom_form_footer_html", "registration_text", "registration_form_header",
        "show_dl", "hide_gender", "enable_age_warning", "age_warning_text", "enable_age_block", "age_block_text", "hide_ereceipt", "na_gender_text",
        "normalize_to_uppercase", "dl_format", "enable_legal_name_checkbox", "drivers_license_button_text",
        "drivers_license_prompt_text", "agreement_confirm_button_text", "agreement_cancel_button_text", "school_info_field_legend",
        "school_info_format", "responsible_person_disclaimer", "display_responsible_person_field", "phone_number_format",
        "enable_patron_branch_select_option", "display_preferred_pickup_location", "teacher_patron_code_id", "student_patron_code_id",
        "patron_code_id", "expiration_date", "expiration_date_years", "hide_branch_select_if_only_one_option", "disable_branch",
        "display_ecard_checkbox", "ecard_patron_code_id", "ecard_registration_text", "ecard_barcode_prefix", "force_ecard_remotely",
        "display_mailing_list_checkbox", "mailing_list_description_html", "mailing_list_record_set_id", "display_sms_notice_information",
        "sms_notice_information_html", "use_legal_name_on_notices", "ecard_welcome_email_template_text",
        "ecard_welcome_email_template_html", "welcome_email_template_text", "welcome_email_template_html", "welcome_email_from_name",
        "welcome_email_subject", "welcome_email_from_address", "ecard_welcome_email_subject", "postmark_api_key",
        "bypass_dupe_check", "duplicate_patron_message_html", "perform_papi_duplicate_bypass", "use_first_name_for_duplicate_workaround",
        "block_out_of_state_registrations", "update_patron_record_with_melissa_address", "melissa_data_api_key",
        "valid_address_registration_text", "valid_address_plus_name_registration_text", "out_of_state_block_message",
        "valid_address_patron_code_id", "valid_address_plus_name_patron_code_id", "valid_address_record_set_id",
        "valid_address_plus_name_record_set_id", "invalid_address_record_set_id", "registration_logon_user_id",
        "add_to_record_set_id", "post_registration_note_text", "show_dl_ips", "reset_form", "kiosk_registration_text",
        "kiosk_registration_header", "reset_seconds"
    ];

    private static readonly string[] IntegrationTestSettingTypeKeys =
    [
        "test.first", "test.second", "test.secret"
    ];

    [ClassInitialize]
    public static void CreateDatabase(TestContext context)
    {
        classContext = context;
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            sqlIntegrationConfigured = false;
            unavailableReason = $"SQL-backed repository tests require {ConnectionVariable}. The connection must permit CREATE DATABASE.";
            return;
        }

        sqlIntegrationConfigured = true;

        databaseName = $"PatronRegistrationTests_{Guid.NewGuid():N}";
        try
        {
            var adminBuilder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", ConnectTimeout = 10 };
            using var connection = new SqlConnection(adminBuilder.ConnectionString);
            connection.Open();
            Execute(connection, $"CREATE DATABASE [{databaseName}]");
            databaseCreated = true;
        }
        catch (SqlException exception)
        {
            TryDropDatabase(configured);
            unavailableReason = $"SQL-backed repository tests could not create a temporary database using {ConnectionVariable}: " +
                $"{exception.Message} The configured login must be able to create and drop a temporary database.";
            return;
        }

        try
        {
            var databaseBuilder = new SqlConnectionStringBuilder(configured) { InitialCatalog = databaseName, ConnectTimeout = 10 };
            var candidateConnectionString = databaseBuilder.ConnectionString;
            using var database = new SqlConnection(candidateConnectionString);
            database.Open();
            DeployExistingRegistrationSettingsSchema(database);
            foreach (var file in NumberedMigrationPaths())
            {
                Execute(database, File.ReadAllText(file), 30);
            }
            // Exercise the incremental migrations' repeatability during fixture deployment.
            Execute(database, File.ReadAllText(MigrationPath("006-register-header-image-asset-setting.sql")), 30);
            Execute(database, File.ReadAllText(MigrationPath("007-remove-legacy-header-image-url.sql")), 30);
            Execute(database, File.ReadAllText(MigrationPath("008-migrate-legacy-registration-field-settings.sql")), 30);
            Execute(database, File.ReadAllText(MigrationPath("009-register-setting-catalog-keys.sql")), 30);
            Execute(database, File.ReadAllText(MigrationPath("010-registration-form-asset-cleanup.sql")), 30);
            Execute(database, File.ReadAllText(MigrationPath("011-registration-form-asset-reference-lock.sql")), 30);
            Execute(database, File.ReadAllText(MigrationPath("012-draft-revision-and-preview-generation.sql")), 30);
            databaseConnectionString = candidateConnectionString;
            schemaReady = true;
        }
        catch
        {
            TryDropDatabase(configured);
            throw;
        }
    }

    [ClassCleanup]
    public static void DeleteDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        switch (DatabaseCleanupModeSelector.Select(configured, unavailableReason))
        {
            case DatabaseCleanupMode.None:
                return;
            case DatabaseCleanupMode.BestEffort:
                TryDropDatabase(configured!);
                return;
            case DatabaseCleanupMode.Strict:
                DropDatabaseCore(configured!);
                return;
            default:
                throw new InvalidOperationException("Unknown SQL integration database cleanup mode.");
        }
    }

    [TestInitialize]
    public void ResetDatabase()
    {
        if (unavailableReason is not null)
        {
            if (!sqlIntegrationConfigured) Assert.Inconclusive(unavailableReason);
            Assert.Fail(unavailableReason);
        }
        Assert.IsTrue(databaseCreated && schemaReady && databaseConnectionString is not null,
            "The SQL integration fixture attempted setup but did not finish deploying the schema.");
        clock = new MutableTimeProvider(new DateTimeOffset(2030, 4, 5, 6, 7, 8, TimeSpan.Zero));
        repository = new SettingsAdministrationRepository(databaseConnectionString!, clock);
        assetRepository = new RegistrationFormAssetRepository(databaseConnectionString!);
        using var connection = Open();
        Execute(connection, @"delete dbo.RegistrationFormCodeMetadata;
delete dbo.RegistrationFormSettings;
delete dbo.RegistrationFormAssets;
delete dbo.RegistrationSettingAuditEvents;
delete dbo.RegistrationSettingPreviewLinks;
delete dbo.RegistrationSettingDraftChanges;
delete dbo.RegistrationSettingDrafts;
delete dbo.RegistrationSettingScopeVersions;
update dbo.RegistrationSettingsCacheGeneration set Generation=0,ModifiedAtUtc=SYSUTCDATETIME() where Id=1;");
    }

    [TestMethod]
    public void Fixture_DeploysRequiredSettingsAdministrationSchema()
    {
        var requiredObjects = new[]
        {
            "dbo.RegistrationSettingScopeVersions",
            "dbo.RegistrationSettingDrafts",
            "dbo.RegistrationSettingDraftChanges",
            "dbo.RegistrationSettingAuditEvents",
            "dbo.RegistrationFormAssets",
            "dbo.RegistrationFormAssetReferenceLocks",
            "dbo.RegistrationFormSettingTypes",
            "dbo.RegistrationFormSettings"
        };
        foreach (var requiredObject in requiredObjects)
        {
            Assert.AreEqual(1, Scalar<int>($"select case when object_id('{requiredObject}', 'U') is null then 0 else 1 end"),
                $"Required schema object {requiredObject} was not deployed.");
        }
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingDrafts') and name='UX_RSD_ActiveScope' and is_unique=1 and has_filter=1"));
        Assert.AreEqual(2, Scalar<int>("select count(*) from sys.columns where object_id=object_id('dbo.RegistrationFormAssets') and name in ('UploadOrganizationId', 'UploadFormCode')"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationFormAssets') and name='IX_RegistrationFormAssets_UploadScope'"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationFormAssets') and name='IX_RegistrationFormAssets_CreatedDate'"));
        Assert.AreEqual(1, Scalar<int>("select case when object_id('dbo.RegistrationFormAssetReferenceLocks','U') is null then 0 else 1 end"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationFormAssetReferenceLocks where LockId=1"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.columns where object_id=object_id('dbo.RegistrationSettingDrafts') and name='Revision' and is_nullable=0"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.columns where object_id=object_id('dbo.RegistrationSettingPreviewLinks') and name='LiveSettingsGeneration' and is_nullable=1"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationFormSettingTypes where Setting='header_image_asset_id'"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettingTypes where Setting='header_image_url'"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where Setting='header_image_url'"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.foreign_keys where parent_object_id=object_id('dbo.RegistrationFormSettings') and name='FK_Registration_Form_Settings_Registration_Form_Setting_Types' and is_disabled=0"));
    }

    [TestMethod]
    public void ConvergenceScript_IsRepeatableAndRegistersTheCompleteCatalog()
    {
        var convergence = File.ReadAllText(Path.Combine(RepositoryRoot(), "database", "settings-administration.sql"));
        using (var connection = Open())
        {
            Execute(connection, convergence, 30);
            Execute(connection, convergence, 30);
        }

        var expected = new SettingCatalog().All.Select(setting => setting.Key).ToArray();
        var actual = Query("select Setting from dbo.RegistrationFormSettingTypes", null,
            reader => reader.GetString(0));
        foreach (var key in expected)
        {
            Assert.AreEqual(1, actual.Count(actualKey => actualKey.Equals(key, StringComparison.OrdinalIgnoreCase)), key);
        }
        foreach (var compatibilityOnlyKey in new[]
        {
            "school_info_format", "hide_gender", "na_gender_text", "kiosk_registration_header"
        })
        {
            Assert.AreEqual(1, actual.Count(actualKey => actualKey.Equals(compatibilityOnlyKey, StringComparison.OrdinalIgnoreCase)),
                compatibilityOnlyKey);
        }
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.foreign_keys where parent_object_id=object_id('dbo.RegistrationFormSettings') and name='FK_Registration_Form_Settings_Registration_Form_Setting_Types' and is_disabled=0"));
    }

    [TestMethod]
    public void ConvergenceScript_RevokesChangedDraftLinksAndAdvancesRevisionOnce()
    {
        var convergence = File.ReadAllText(Path.Combine(RepositoryRoot(), "database", "settings-administration.sql"));
        using (var connection = Open())
        {
            SeedLegacyHeaderImageSetting(connection);
            SeedLegacyRegistrationFieldSetting(connection, 101, "legal_name_checkbox_label", "convergence", "legacy label");
        }

        var activeDraftId = SeedDraftAtScope(101, "convergence", "Active");
        SeedRawDraftMutation(activeDraftId, "header_image_url", "Upsert", "https://example.test/convergence.png");
        SeedRawDraftMutation(activeDraftId, "legal_name_checkbox_label", "Upsert", "legacy draft label");
        var safeHash = Enumerable.Repeat((byte)64, 32).ToArray();
        var liveHash = Enumerable.Repeat((byte)65, 32).ToArray();
        var safeLinkId = SeedPreviewLink(activeDraftId, safeHash, clock.GetUtcNow().UtcDateTime.AddHours(1));
        var liveLinkId = SeedPreviewLink(activeDraftId, liveHash, clock.GetUtcNow().UtcDateTime.AddHours(1),
            allowLiveSubmission: true, liveSettingsGeneration: 0);

        var unrelatedDraftId = SeedDraftAtScope(202, "convergence-unrelated", "Active");
        SeedRawDraftMutation(unrelatedDraftId, "registration_text", "Upsert", "unrelated");
        var unrelatedHash = Enumerable.Repeat((byte)66, 32).ToArray();
        var unrelatedLinkId = SeedPreviewLink(unrelatedDraftId, unrelatedHash, clock.GetUtcNow().UtcDateTime.AddHours(1));

        // Simulate a database that has the draft/link tables from the earlier
        // migrations but has not installed the Revision column from 012 yet.
        DropDraftRevisionColumn();
        using (var connection = Open())
        {
            Execute(connection, convergence, 30);
            Assert.AreEqual(1L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + activeDraftId));
            Assert.IsNotNull(repository.GetPreviewLink(safeLinkId)!.RevokedAtUtc);
            Assert.IsNotNull(repository.GetPreviewLink(liveLinkId)!.RevokedAtUtc);
            Assert.IsNull(repository.ResolvePreviewContext(safeHash));
            Assert.IsNull(repository.ResolvePreviewContext(liveHash));
            Assert.IsNull(repository.GetPreviewLink(unrelatedLinkId)!.RevokedAtUtc);
            Assert.AreEqual(0L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + unrelatedDraftId));

            Execute(connection, convergence, 30);
            Assert.AreEqual(1L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + activeDraftId));
            Assert.IsNull(repository.GetPreviewLink(unrelatedLinkId)!.RevokedAtUtc);
        }
    }

    [TestMethod]
    public void BackendOnlySchoolInfoFormatValue_SurvivesCatalogMigrationsUnchanged()
    {
        const string originalValue = "uapl-backend-value";
        using (var connection = Open())
        {
            Execute(connection, "delete dbo.RegistrationFormSettings where OrganizationID=101 and Setting='school_info_format' and FormCode='form';");
            Execute(connection,
                "insert dbo.RegistrationFormSettings(OrganizationID, Setting, FormCode, Value) values(101, 'school_info_format', 'form', @value);",
                parameters: command => command.Parameters.AddWithValue("@value", originalValue));

            Execute(connection, File.ReadAllText(MigrationPath("009-register-setting-catalog-keys.sql")), 30);
            Execute(connection, File.ReadAllText(MigrationPath("009-register-setting-catalog-keys.sql")), 30);
            Execute(connection, File.ReadAllText(Path.Combine(RepositoryRoot(), "database", "settings-administration.sql")), 30);
            Execute(connection, File.ReadAllText(Path.Combine(RepositoryRoot(), "database", "settings-administration.sql")), 30);
        }

        Assert.AreEqual(originalValue, ReadSettingValue(101, "form", "school_info_format"));
    }

    [TestMethod]
    public void BackendOnlyKioskRegistrationHeaderValueAndTypeSurviveCatalogMigrationsUnchanged()
    {
        const string originalValue = "legacy kiosk header";
        using (var connection = Open())
        {
            Execute(connection, "delete dbo.RegistrationFormSettings where OrganizationID=101 and Setting='kiosk_registration_header' and FormCode='form';");
            Execute(connection,
                "insert dbo.RegistrationFormSettings(OrganizationID, Setting, FormCode, Value) values(101, 'kiosk_registration_header', 'form', @value);",
                parameters: command => command.Parameters.AddWithValue("@value", originalValue));

            foreach (var file in new[]
            {
                "007-remove-legacy-header-image-url.sql",
                "008-migrate-legacy-registration-field-settings.sql",
                "009-register-setting-catalog-keys.sql",
                "settings-administration.sql"
            })
            {
                var scriptPath = file.Equals("settings-administration.sql", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(RepositoryRoot(), "database", file)
                    : MigrationPath(file);
                var script = File.ReadAllText(scriptPath);
                Execute(connection, script, 30);
                Execute(connection, script, 30);
            }
        }

        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationFormSettingTypes where Setting='kiosk_registration_header'"));
        Assert.AreEqual(originalValue, ReadSettingValue(101, "form", "kiosk_registration_header"));
    }

    [TestMethod]
    public void Migration006_IsIdempotentAndRegistersExactlyOneHeaderImageSettingType()
    {
        var migration = File.ReadAllText(MigrationPath("006-register-header-image-asset-setting.sql"));
        using (var connection = Open())
        {
            Execute(connection, migration, 30);
        }

        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationFormSettingTypes where Setting='header_image_asset_id'"));
    }

    [TestMethod]
    public void Migration007_RemovesLegacyHeaderImageUrlRowsAndIsIdempotent()
    {
        var migration = File.ReadAllText(MigrationPath("007-remove-legacy-header-image-url.sql"));
        using (var connection = Open())
        {
            SeedLegacyHeaderImageSetting(connection);
            Execute(connection, migration, 30);
            Execute(connection, migration, 30);
        }

        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettingTypes where Setting='header_image_url'"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where Setting='header_image_url'"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationFormSettingTypes where Setting='header_image_asset_id'"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.foreign_keys where parent_object_id=object_id('dbo.RegistrationFormSettings') and name='FK_Registration_Form_Settings_Registration_Form_Setting_Types' and is_disabled=0"));
    }

    [TestMethod]
    public void Migration007_RevokesSafeAndLiveLinksOnlyForChangedActiveDraftsAndAdvancesRevisionOnce()
    {
        var migration = File.ReadAllText(Path.Combine(RepositoryRoot(), "database", "007-remove-legacy-header-image-url.sql"));
        using (var connection = Open())
            SeedLegacyHeaderImageSetting(connection);

        var activeDraftId = SeedDraftAtScope(101, "migration-007", "Active");
        SeedRawDraftMutation(activeDraftId, "header_image_url", "Upsert", "https://example.test/draft.png");
        var safeHash = Enumerable.Repeat((byte)57, 32).ToArray();
        var liveHash = Enumerable.Repeat((byte)58, 32).ToArray();
        var safeLinkId = SeedPreviewLink(activeDraftId, safeHash, clock.GetUtcNow().UtcDateTime.AddHours(1));
        var liveLinkId = SeedPreviewLink(activeDraftId, liveHash, clock.GetUtcNow().UtcDateTime.AddHours(1),
            allowLiveSubmission: true, liveSettingsGeneration: 0);

        var unrelatedDraftId = SeedDraftAtScope(202, "migration-007-unrelated", "Active");
        SeedRawDraftMutation(unrelatedDraftId, "registration_text", "Upsert", "unrelated");
        var unrelatedHash = Enumerable.Repeat((byte)59, 32).ToArray();
        var unrelatedLinkId = SeedPreviewLink(unrelatedDraftId, unrelatedHash, clock.GetUtcNow().UtcDateTime.AddHours(1));

        using (var connection = Open())
        {
            Execute(connection, migration, 30);
            Assert.AreEqual(1L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + activeDraftId));
            Assert.IsNotNull(repository.GetPreviewLink(safeLinkId)!.RevokedAtUtc);
            Assert.IsNotNull(repository.GetPreviewLink(liveLinkId)!.RevokedAtUtc);
            Assert.IsNull(repository.ResolvePreviewContext(safeHash));
            Assert.IsNull(repository.ResolvePreviewContext(liveHash));
            Assert.IsNull(repository.GetPreviewLink(unrelatedLinkId)!.RevokedAtUtc);
            Assert.AreEqual(0L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + unrelatedDraftId));

            Execute(connection, migration, 30);
            Assert.AreEqual(1L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + activeDraftId));
            Assert.IsNull(repository.GetPreviewLink(unrelatedLinkId)!.RevokedAtUtc);
        }
    }

    [TestMethod]
    public void Migration007_RemovesRetiredKeyFromActiveDraftButPreservesValidAndHistoricalChanges()
    {
        var migration = File.ReadAllText(MigrationPath("007-remove-legacy-header-image-url.sql"));
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var ordinary = catalog["registration_text"];

        using (var connection = Open())
            SeedLegacyHeaderImageSetting(connection);

        SeedVersion(0);
        var activeDraft = repository.SaveToSharedDraft(101, "form", 0, null,
            [Upsert(ordinary, "ordinary draft value")], catalog, Audit());
        SeedRawDraftChange(activeDraft.DraftId, RetiredHeaderImageUrl.Key, "https://example.test/draft-only.png");
        var committedDraft = SeedDraft(0, "Committed", RetiredHeaderImageUrl, "https://example.test/committed.png");
        var discardedDraft = SeedDraft(0, "Discarded", RetiredHeaderImageUrl, "https://example.test/discarded.png");
        var invalidatedDraft = SeedDraft(0, "Invalidated", RetiredHeaderImageUrl, "https://example.test/invalidated.png");

        using (var connection = Open())
        {
            Execute(connection, migration, 30);
            Assert.AreEqual(activeDraft.DraftRevision + 1, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + activeDraft.DraftId));
            Execute(connection, migration, 30);
            Assert.AreEqual(activeDraft.DraftRevision + 1, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + activeDraft.DraftId));
        }

        var remainingActiveDraft = repository.GetDraft(activeDraft.DraftId);
        Assert.IsNotNull(remainingActiveDraft);
        Assert.AreEqual(DraftStatus.Active, remainingActiveDraft!.Status);
        Assert.AreEqual(1, remainingActiveDraft.Changes.Count);
        CollectionAssert.AreEquivalent(new[] { "registration_text|Upsert|ordinary draft value" }, ReadChanges(activeDraft.DraftId).ToArray());
        CollectionAssert.AreEquivalent(new[] { "header_image_url|Upsert|https://example.test/committed.png" }, ReadChanges(committedDraft).ToArray());
        CollectionAssert.AreEquivalent(new[] { "header_image_url|Upsert|https://example.test/discarded.png" }, ReadChanges(discardedDraft).ToArray());
        CollectionAssert.AreEquivalent(new[] { "header_image_url|Upsert|https://example.test/invalidated.png" }, ReadChanges(invalidatedDraft).ToArray());

        repository.CommitDraft(activeDraft.DraftId, catalog, true, Audit(), activeDraft.DraftRevision + 1);

        Assert.AreEqual(DraftStatus.Committed, repository.GetDraft(activeDraft.DraftId)!.Status);
        var persisted = QuerySingle("select Value from dbo.RegistrationFormSettings where OrganizationID=101 and FormCode='form' and Setting='registration_text'",
            null, reader => reader.GetString(0));
        Assert.AreEqual("ordinary draft value", persisted);
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingDraftChanges where SettingKey='header_image_url' and DraftId in (select DraftId from dbo.RegistrationSettingDrafts where Status='Active')"));
    }

    [TestMethod]
    public void Migration007_RemovesOnlyRetiredKeyFromHeaderOnlyActiveDraftAndEmptyDraftCanCommit()
    {
        var migration = File.ReadAllText(MigrationPath("007-remove-legacy-header-image-url.sql"));
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);

        using (var connection = Open())
            SeedLegacyHeaderImageSetting(connection);

        var activeDraft = SeedDraft(0, "Active", RetiredHeaderImageUrl, "https://example.test/only-draft.png");

        using (var connection = Open())
        {
            Execute(connection, migration, 30);
            Assert.AreEqual(1L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + activeDraft));
            Execute(connection, migration, 30);
            Assert.AreEqual(1L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + activeDraft));
        }

        var remainingActiveDraft = repository.GetDraft(activeDraft);
        Assert.IsNotNull(remainingActiveDraft);
        Assert.AreEqual(DraftStatus.Active, remainingActiveDraft!.Status);
        Assert.AreEqual(0, remainingActiveDraft.Changes.Count);
        Assert.AreEqual(0, ReadChanges(activeDraft).Count);

        repository.CommitDraft(activeDraft, catalog, true, Audit(), 1);

        Assert.AreEqual(DraftStatus.Committed, repository.GetDraft(activeDraft)!.Status);
        Assert.AreEqual(1L, repository.GetVersion(101, "form"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where Setting='header_image_url'"));
    }

    [TestMethod]
    public void Migration008_MigratesOwnedRowsDraftsAndSettingTypesIdempotently()
    {
        var migration = File.ReadAllText(MigrationPath("008-migrate-legacy-registration-field-settings.sql"));
        using (var connection = Open())
        {
            SeedLegacyRegistrationFieldSetting(connection, 3, "legal_name_checkbox_label", "branch-form", "Branch legal name");
            SeedLegacyRegistrationFieldSetting(connection, 3, "ecard_checkbox_label", string.Empty, "Branch e-card");
            SeedLegacyRegistrationFieldSetting(connection, 2, "mailing_list_checkbox_label", "library-form", "Library mailing list");
            SeedLegacyRegistrationFieldSetting(connection, 1, "require_preferred_pickup_location", "system-form", "true");
            SeedLegacyRegistrationFieldSetting(connection, 2, "ecard_checkbox_label", string.Empty, "Library e-card");
            SeedLegacyRegistrationFieldSetting(connection, 1, "mailing_list_checkbox_label", string.Empty, "System mailing list");
            SeedLegacyRegistrationFieldSetting(connection, 3, "require_preferred_pickup_location", "branch-require", "true");
            SeedLegacyRegistrationFieldSetting(connection, 3, "legal_name_checkbox_label", "conflict", "legacy value");
            SeedLegacyRegistrationFieldSetting(connection, 3, "label.UseLegalName", "conflict", "replacement value");
        }

        var activeDraftId = SeedDraftAtScope(101, "form", "Active");
        SeedRawDraftMutation(activeDraftId, "legal_name_checkbox_label", "Upsert", "draft legal name");
        SeedRawDraftMutation(activeDraftId, "ecard_checkbox_label", "RemoveOverride", null);
        SeedRawDraftMutation(activeDraftId, "mailing_list_checkbox_label", "Upsert", "legacy draft mailing list");
        SeedRawDraftMutation(activeDraftId, "label.AddToMailingList", "Upsert", "replacement draft mailing list");
        SeedRawDraftMutation(activeDraftId, "require_preferred_pickup_location", "Upsert", "true");
        var safeHash = Enumerable.Repeat((byte)61, 32).ToArray();
        var liveHash = Enumerable.Repeat((byte)62, 32).ToArray();
        var safeLinkId = SeedPreviewLink(activeDraftId, safeHash, clock.GetUtcNow().UtcDateTime.AddHours(1));
        var liveLinkId = SeedPreviewLink(activeDraftId, liveHash, clock.GetUtcNow().UtcDateTime.AddHours(1),
            allowLiveSubmission: true, liveSettingsGeneration: 0);

        var unrelatedDraftId = SeedDraftAtScope(202, "unrelated-008", "Active");
        SeedRawDraftMutation(unrelatedDraftId, "registration_text", "Upsert", "unrelated draft");
        var unrelatedHash = Enumerable.Repeat((byte)63, 32).ToArray();
        var unrelatedLinkId = SeedPreviewLink(unrelatedDraftId, unrelatedHash, clock.GetUtcNow().UtcDateTime.AddHours(1));

        var committedDraftId = SeedDraftAtScope(101, "historical", "Committed");
        SeedRawDraftMutation(committedDraftId, "legal_name_checkbox_label", "Upsert", "historical legal name");

        using (var connection = Open())
        {
            Execute(connection, migration, 30);
            Assert.AreEqual(1L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + activeDraftId));
            Assert.IsNotNull(repository.GetPreviewLink(safeLinkId)!.RevokedAtUtc);
            Assert.IsNotNull(repository.GetPreviewLink(liveLinkId)!.RevokedAtUtc);
            Assert.IsNull(repository.ResolvePreviewContext(safeHash));
            Assert.IsNull(repository.ResolvePreviewContext(liveHash));
            Assert.IsNull(repository.GetPreviewLink(unrelatedLinkId)!.RevokedAtUtc);
            Assert.AreEqual(0L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + unrelatedDraftId));
            Execute(connection, migration, 30);
            Assert.AreEqual(1L, Scalar<long>("select Revision from dbo.RegistrationSettingDrafts where DraftId=" + activeDraftId));
        }

        foreach (var key in new[]
        {
            "legal_name_checkbox_label", "ecard_checkbox_label", "mailing_list_checkbox_label",
            "require_preferred_pickup_location"
        })
        {
            Assert.AreEqual(0, Scalar<int>($"select count(*) from dbo.RegistrationFormSettingTypes where Setting='{key}'"), key);
            Assert.AreEqual(0, Scalar<int>($"select count(*) from dbo.RegistrationFormSettings where Setting='{key}'"), key);
            Assert.AreEqual(0, Scalar<int>($"select count(*) from dbo.RegistrationSettingDraftChanges where SettingKey='{key}' and DraftId in (select DraftId from dbo.RegistrationSettingDrafts where Status='Active')"), key);
        }

        foreach (var key in new[]
        {
            "label.UseLegalName", "label.IsECard", "label.AddToMailingList", "require.RequestPickupBranchID"
        })
        {
            Assert.AreEqual(1, Scalar<int>($"select count(*) from dbo.RegistrationFormSettingTypes where Setting='{key}'"), key);
        }

        Assert.AreEqual("Branch legal name", ReadSettingValue(3, "branch-form", "label.UseLegalName"));
        Assert.AreEqual("Branch e-card", ReadSettingValue(3, string.Empty, "label.IsECard"));
        Assert.AreEqual("Library mailing list", ReadSettingValue(2, "library-form", "label.AddToMailingList"));
        Assert.AreEqual("true", ReadSettingValue(1, "system-form", "require.RequestPickupBranchID"));
        Assert.AreEqual("Library e-card", ReadSettingValue(2, string.Empty, "label.IsECard"));
        Assert.AreEqual("System mailing list", ReadSettingValue(1, string.Empty, "label.AddToMailingList"));
        Assert.AreEqual("true", ReadSettingValue(3, "branch-require", "require.RequestPickupBranchID"));
        Assert.AreEqual("replacement value", ReadSettingValue(3, "conflict", "label.UseLegalName"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where Setting='label.UseLegalName' and OrganizationID=2 and FormCode='conflict'"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where Setting='label.UseLegalName' and OrganizationID=1 and FormCode='branch-form'"));

        CollectionAssert.AreEquivalent(
            new[]
            {
                "label.AddToMailingList|Upsert|replacement draft mailing list",
                "label.UseLegalName|Upsert|draft legal name",
                "require.RequestPickupBranchID|Upsert|true",
                "label.IsECard|RemoveOverride|"
            },
            ReadChanges(activeDraftId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "legal_name_checkbox_label|Upsert|historical legal name" },
            ReadChanges(committedDraftId).ToArray());
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingDraftChanges where DraftId in (select DraftId from dbo.RegistrationSettingDrafts where Status='Active') and SettingKey in ('legal_name_checkbox_label','ecard_checkbox_label','mailing_list_checkbox_label','require_preferred_pickup_location')"));
    }

    [DataTestMethod]
    [DataRow("007-remove-legacy-header-image-url.sql", "header_image_url", "migration-007-no-revision")]
    [DataRow("008-migrate-legacy-registration-field-settings.sql", "legal_name_checkbox_label", "migration-008-no-revision")]
    public void LegacyDraftMigrations_DoNotRequireRevisionColumn(string migrationFile, string legacyKey, string formCode)
    {
        var migration = File.ReadAllText(Path.Combine(RepositoryRoot(), "database", migrationFile));
        using (var connection = Open())
        {
            if (legacyKey == "header_image_url")
                SeedLegacyHeaderImageSetting(connection);
            else
                SeedLegacyRegistrationFieldSetting(connection, 101, legacyKey, formCode, "legacy value");
        }

        var draftId = SeedDraftAtScope(101, formCode, "Active");
        SeedRawDraftMutation(draftId, legacyKey, "Upsert", "legacy draft value");
        var hash = Enumerable.Repeat((byte)(67 + legacyKey.Length), 32).ToArray();
        var linkId = SeedPreviewLink(draftId, hash, clock.GetUtcNow().UtcDateTime.AddHours(1),
            allowLiveSubmission: true, liveSettingsGeneration: 0);

        try
        {
            DropDraftRevisionColumn();
            using var connection = Open();
            Execute(connection, migration, 30);
            Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks where PreviewLinkId=" + linkId + " and RevokedAtUtc is not null"));
            Assert.AreEqual(0, Scalar<int>("select count(*) from sys.columns where object_id=object_id('dbo.RegistrationSettingDrafts') and name='Revision'"));
        }
        finally
        {
            RestoreDraftRevisionColumn();
        }
    }

    [TestMethod]
    public void Migration012_RevokesLegacyLiveLinksInsteadOfBackfillingGeneration()
    {
        var migration = File.ReadAllText(Path.Combine(RepositoryRoot(), "database", "012-draft-revision-and-preview-generation.sql"));
        var draftId = SeedActiveDraft(0, First, "draft");
        var liveHash = Enumerable.Repeat((byte)69, 32).ToArray();
        var safeHash = Enumerable.Repeat((byte)70, 32).ToArray();
        var liveLinkId = SeedPreviewLink(draftId, liveHash, clock.GetUtcNow().UtcDateTime.AddHours(1),
            allowLiveSubmission: true, liveSettingsGeneration: null);
        var safeLinkId = SeedPreviewLink(draftId, safeHash, clock.GetUtcNow().UtcDateTime.AddHours(1));
        var historicalDraftId = SeedDraft(0, "Committed", First, "historical");
        var historicalHash = Enumerable.Repeat((byte)71, 32).ToArray();
        var historicalLinkId = SeedPreviewLink(historicalDraftId, historicalHash, clock.GetUtcNow().UtcDateTime.AddHours(1),
            allowLiveSubmission: true, liveSettingsGeneration: null);

        using (var connection = Open())
        {
            Execute(connection, migration, 30);
            Assert.IsNotNull(repository.GetPreviewLink(liveLinkId)!.RevokedAtUtc);
            Assert.IsNull(repository.GetPreviewLink(liveLinkId)!.LiveSettingsGeneration);
            Assert.IsNull(repository.ResolvePreviewContext(liveHash));
            Assert.IsNull(repository.GetPreviewLink(safeLinkId)!.RevokedAtUtc);
            Assert.IsNotNull(repository.ResolvePreviewContext(safeHash));
            Assert.IsNull(repository.GetPreviewLink(historicalLinkId)!.RevokedAtUtc);
            Assert.IsNull(repository.GetPreviewLink(historicalLinkId)!.LiveSettingsGeneration);

            Execute(connection, migration, 30);
            Assert.IsNotNull(repository.GetPreviewLink(liveLinkId)!.RevokedAtUtc);
            Assert.IsNull(repository.GetPreviewLink(liveLinkId)!.LiveSettingsGeneration);
            Assert.IsNull(repository.GetPreviewLink(historicalLinkId)!.RevokedAtUtc);
        }
    }

    [TestMethod]
    public void DeployedSettingTypesContainEveryOrdinaryCatalogKeyExactlyOnce()
    {
        var expected = new SettingCatalog().All
            .Where(setting => setting.Group == SettingGroup.Ordinary)
            .Select(setting => setting.Key)
            .ToArray();
        var actual = Query("select Setting from dbo.RegistrationFormSettingTypes", null,
            reader => reader.GetString(0));

        Assert.AreEqual(expected.Length, expected.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "The ordinary catalog contains duplicate keys.");
        foreach (var key in expected)
        {
            Assert.AreEqual(1, actual.Count(actualKey => actualKey.Equals(key, StringComparison.OrdinalIgnoreCase)),
                $"Setting-type registration is missing or duplicated for {key}.");
        }
    }

    [TestMethod]
    public void DeployedSettingTypesContainEveryPersistableCatalogKeyExactlyOnce()
    {
        var expected = new SettingCatalog().All.Select(setting => setting.Key).ToArray();
        var actual = Query("select Setting from dbo.RegistrationFormSettingTypes", null,
            reader => reader.GetString(0));

        Assert.AreEqual(expected.Length, expected.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "The persistable catalog contains duplicate keys.");
        foreach (var key in expected)
        {
            Assert.AreEqual(1, actual.Count(actualKey => actualKey.Equals(key, StringComparison.OrdinalIgnoreCase)),
                $"Setting-type registration is missing or duplicated for {key}.");
        }
        CollectionAssert.DoesNotContain(actual, "header_image_url");
        CollectionAssert.DoesNotContain(actual, "legal_name_checkbox_label");
        CollectionAssert.DoesNotContain(actual, "ecard_checkbox_label");
        CollectionAssert.DoesNotContain(actual, "mailing_list_checkbox_label");
        CollectionAssert.DoesNotContain(actual, "require_preferred_pickup_location");
    }

    [TestMethod]
    public void DirectSave_PersistsGeneratedCatalogKeysWithForeignKeyEnabled()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        SeedVersion(0);

        repository.DirectSave(101, "dynamic", 0,
            [
                new SettingMutation("label.NameFirst", DraftOperation.Upsert, "Given name"),
                new SettingMutation("require.PhoneVoice1", DraftOperation.Upsert, "true"),
                new SettingMutation("alert.NameFirst", DraftOperation.Upsert, "Enter a first name.")
            ], catalog, Audit());

        Assert.AreEqual("Given name", ReadSettingValue(101, "dynamic", "label.NameFirst"));
        Assert.AreEqual("true", ReadSettingValue(101, "dynamic", "require.PhoneVoice1"));
        Assert.AreEqual("Enter a first name.", ReadSettingValue(101, "dynamic", "alert.NameFirst"));
        Assert.AreEqual(1L, repository.GetVersion(101, "dynamic"));
    }

    [TestMethod]
    public void DirectSave_AndSharedDraftStripExecutableHtmlBeforeStorage()
    {
        const string malicious = "<p><strong>Keep formatting</strong></p><script>alert(1)</script>" +
            "<img src=\"https://example.test/logo.png\" onerror=\"alert(2)\"><a href=\"javascript:alert(3)\">bad</a>";
        SeedVersion(0);

        repository.DirectSave(101, "form", 0, [Upsert(Html, malicious)], Catalog, Audit());
        var direct = ReadSettingValue(101, "form", Html.Key)!;
        AssertSafeHtml(direct);

        var draft = repository.SaveToSharedDraft(101, "draft-form", 0, null,
            [Upsert(Html, malicious)], Catalog, Audit());
        var staged = ReadChanges(draft.DraftId).Single(change => change.StartsWith($"{Html.Key}|", StringComparison.Ordinal));
        AssertSafeHtml(staged.Split('|', 3)[2]);
        Assert.AreEqual(1L, repository.GetDraft(draft.DraftId)!.Revision);
    }

    [TestMethod]
    public void LivePreview_IsInvalidatedByDirectSaveAtBranchLibraryAndSystemScopes()
    {
        SeedVersion(0);
        var draftId = SeedActiveDraft(0, First, "draft");

        foreach (var (scope, value) in new[] { (101, "branch"), (2, "library"), (1, "system") })
        {
            var hash = Enumerable.Repeat((byte)scope, 32).ToArray();
            var linkId = CreatePreviewLinkAtCurrentRevision(draftId, hash, true, 101, 1, Catalog, true, Audit());
            var issued = repository.GetPreviewLink(linkId)!;
            Assert.AreEqual(repository.GetCacheGeneration(), issued.LiveSettingsGeneration);
            Assert.IsNotNull(repository.ResolvePreviewContext(hash));

            repository.DirectSave(scope, "form", 0, [Upsert(Html, value)], Catalog, Audit());

            Assert.IsNull(repository.ResolvePreviewContext(hash), $"scope {scope} did not invalidate the live link");
            Assert.IsFalse(repository.IsLivePreviewCurrent(linkId, issued.LiveSettingsGeneration!.Value));
        }
    }

    [TestMethod]
    public void ResolvePreviewContext_ReturnsTheAuthoritativeGenerationForSafeAndLiveLinks()
    {
        SeedVersion(0);
        var draftId = SeedActiveDraft(0, First, "draft");

        var safeHash = Enumerable.Repeat((byte)78, 32).ToArray();
        var safeLinkId = CreatePreviewLinkAtCurrentRevision(draftId, safeHash, false, 101, 1, Catalog, true, Audit());
        var safe = repository.ResolvePreviewContext(safeHash);
        Assert.IsNotNull(safe);
        Assert.AreEqual(repository.GetCacheGeneration(), safe!.CacheGeneration);
        Assert.IsNull(repository.GetPreviewLink(safeLinkId)!.LiveSettingsGeneration);

        var liveHash = Enumerable.Repeat((byte)79, 32).ToArray();
        var liveLinkId = CreatePreviewLinkAtCurrentRevision(draftId, liveHash, true, 101, 1, Catalog, true, Audit());
        var liveLink = repository.GetPreviewLink(liveLinkId)!;
        var live = repository.ResolvePreviewContext(liveHash);
        Assert.IsNotNull(live);
        Assert.AreEqual(liveLink.LiveSettingsGeneration, live!.CacheGeneration);
    }

    [TestMethod]
    public async Task LivePreviewAdmission_ReleasesPublicationLocksBeforeRegistrationAndRejectsStaleAdmission()
    {
        SeedVersion(0);
        var draftId = SeedActiveDraft(0, First, "draft");
        var hash = Enumerable.Repeat((byte)77, 32).ToArray();
        var linkId = CreatePreviewLinkAtCurrentRevision(draftId, hash, true, 101, 1, Catalog, true, Audit());
        var issued = repository.GetPreviewLink(linkId)!;
        var admission = repository.TryAdmitLivePreviewSubmission(linkId, issued.LiveSettingsGeneration!.Value);
        Assert.IsNotNull(admission);

        var generationUpdateAttempted = CompletionSource();
        SettingsAdministrationRepository.BeforeLiveSettingsGenerationIncrementForTesting = () =>
            generationUpdateAttempted.TrySetResult(true);
        try
        {
            var save = Task.Run(() =>
            {
                try
                {
                    repository.DirectSave(101, "form", 0, [Upsert(Html, "published")], Catalog, Audit());
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });

            await save.WaitAsync(ConcurrencyTestTimeout);
            await generationUpdateAttempted.Task.WaitAsync(ConcurrencyTestTimeout);
            Assert.IsNull(save.Result, save.Result?.ToString());
            Assert.AreEqual(issued.LiveSettingsGeneration.Value + 1, repository.GetCacheGeneration());
            Assert.IsFalse(repository.IsLivePreviewCurrent(linkId, issued.LiveSettingsGeneration.Value));

            using var staleAdmission = repository.TryAdmitLivePreviewSubmission(
                linkId, issued.LiveSettingsGeneration.Value);
            Assert.IsNull(staleAdmission, "A preview whose generation was invalidated must not enter patron creation.");
        }
        finally
        {
            admission.Dispose();
            SettingsAdministrationRepository.BeforeLiveSettingsGenerationIncrementForTesting = null;
        }
    }

    [TestMethod]
    public async Task LivePreviewAdmission_AllowsIndependentAdmissionsAndPublicationWhileRegistrationIsInFlight()
    {
        var firstDraftId = SeedDraftAtScope(101, "preview-one", "Active");
        var secondDraftId = SeedDraftAtScope(101, "preview-two", "Active");
        var firstLinkId = CreatePreviewLinkAtCurrentRevision(firstDraftId,
            Enumerable.Repeat((byte)80, 32).ToArray(), true, 101, 1, Catalog, true, Audit());
        var secondLinkId = CreatePreviewLinkAtCurrentRevision(secondDraftId,
            Enumerable.Repeat((byte)81, 32).ToArray(), true, 101, 1, Catalog, true, Audit());
        var generation = repository.GetCacheGeneration();
        Assert.AreEqual(generation, repository.GetPreviewLink(firstLinkId)!.LiveSettingsGeneration);
        Assert.AreEqual(generation, repository.GetPreviewLink(secondLinkId)!.LiveSettingsGeneration);

        var firstAdmission = repository.TryAdmitLivePreviewSubmission(firstLinkId, generation);
        Assert.IsNotNull(firstAdmission);
        ILivePreviewSubmissionAdmission? secondAdmission = null;
        var secondAdmissionTask = Task.Run(() =>
            repository.TryAdmitLivePreviewSubmission(secondLinkId, generation));
        Task<Exception?>? publication = null;
        var generationUpdateAttempted = CompletionSource();
        try
        {
            secondAdmission = await secondAdmissionTask.WaitAsync(ConcurrencyTestTimeout);
            Assert.IsNotNull(secondAdmission,
                "An independent preview at the same generation should be admitted while the first registration is in flight.");

            SettingsAdministrationRepository.BeforeLiveSettingsGenerationIncrementForTesting = () =>
                generationUpdateAttempted.TrySetResult(true);
            publication = Task.Run(() =>
            {
                try
                {
                    repository.DirectSave(202, "publication", 0,
                        [Upsert(First, "published")], Catalog, Audit());
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });

            await publication.WaitAsync(ConcurrencyTestTimeout);
            await generationUpdateAttempted.Task.WaitAsync(ConcurrencyTestTimeout);
            Assert.IsNull(publication.Result, publication.Result?.ToString());
            Assert.AreEqual(generation + 1, repository.GetCacheGeneration());

            using var staleFirstAdmission = repository.TryAdmitLivePreviewSubmission(firstLinkId, generation);
            using var staleSecondAdmission = repository.TryAdmitLivePreviewSubmission(secondLinkId, generation);
            Assert.IsNull(staleFirstAdmission, "The first old-generation preview must fail admission after publication.");
            Assert.IsNull(staleSecondAdmission, "The second old-generation preview must fail admission after publication.");
        }
        finally
        {
            firstAdmission.Dispose();
            SettingsAdministrationRepository.BeforeLiveSettingsGenerationIncrementForTesting = null;
            if (!secondAdmissionTask.IsCompleted)
            {
                try { secondAdmission = await secondAdmissionTask.WaitAsync(ConcurrencyTestTimeout); }
                catch { /* Preserve the original test failure. */ }
            }
            secondAdmission?.Dispose();
            if (publication is not null)
            {
                try { await publication.WaitAsync(ConcurrencyTestTimeout); }
                catch { /* Preserve the original test failure. */ }
            }
        }
    }

    [TestMethod]
    public async Task LivePreviewAdmission_ReleasesDraftLocksBeforeRegistrationAndRejectsRevokedAdmission()
    {
        SeedVersion(0);
        var draftId = SeedActiveDraft(0, Html, "draft");
        var hash = Enumerable.Repeat((byte)78, 32).ToArray();
        var linkId = CreatePreviewLinkAtCurrentRevision(draftId, hash, true, 101, 1, Catalog, true, Audit());
        var issued = repository.GetPreviewLink(linkId)!;
        var admission = repository.TryAdmitLivePreviewSubmission(linkId, issued.LiveSettingsGeneration!.Value);
        Assert.IsNotNull(admission);

        var commitAttempted = CompletionSource();
        RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = operation =>
        {
            if (operation == nameof(SettingsAdministrationRepository.CommitDraft))
            {
                commitAttempted.TrySetResult(true);
            }
        };
        Task<Exception?>? commit = null;
        try
        {
            commit = Task.Run(() =>
            {
                try
                {
                    repository.CommitDraft(draftId, Catalog, true, Audit(), 0);
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });

            await commit.WaitAsync(ConcurrencyTestTimeout);
            await commitAttempted.Task.WaitAsync(ConcurrencyTestTimeout);
            Assert.IsNull(commit.Result, commit.Result?.ToString());

            using var staleAdmission = repository.TryAdmitLivePreviewSubmission(
                linkId, issued.LiveSettingsGeneration.Value);
            Assert.IsNull(staleAdmission, "A committed draft must not admit its old live-preview link.");
        }
        finally
        {
            admission.Dispose();
            RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = null;
            RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = null;
            if (commit is not null)
            {
                try { await commit.WaitAsync(ConcurrencyTestTimeout); }
                catch { /* Preserve the original test failure. */ }
            }
        }
    }

    [TestMethod]
    public void LivePreviewAdmission_StaleGenerationIsRejectedBeforeRegistrationSideEffects()
    {
        SeedVersion(0);
        var draftId = SeedActiveDraft(0, First, "draft");
        var linkId = CreatePreviewLinkAtCurrentRevision(
            draftId, Enumerable.Repeat((byte)82, 32).ToArray(), true, 101, 1, Catalog, true, Audit());
        var issued = repository.GetPreviewLink(linkId)!;
        var context = CreatePreviewRequestContext(linkId, draftId, issued.LiveSettingsGeneration!.Value);

        repository.DirectSave(202, "publication", 0, [Upsert(First, "published")], Catalog, Audit());

        var (controller, papi, melissa, email) = CreatePreviewSubmissionController(context);
        var result = controller.Submit("ignored", new Registration(context.Settings));

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
        papi.VerifyNoOtherCalls();
        melissa.VerifyNoOtherCalls();
        email.VerifyNoOtherCalls();
    }

    [DataTestMethod]
    [DataRow("expired")]
    [DataRow("revoked")]
    [DataRow("invalidated")]
    public void LivePreviewAdmission_InvalidLinkIsRejectedBeforeRegistrationSideEffects(string invalidation)
    {
        SeedVersion(0);
        var draftId = SeedActiveDraft(0, First, "draft");
        var linkId = CreatePreviewLinkAtCurrentRevision(
            draftId, Enumerable.Repeat((byte)(83 + invalidation.Length), 32).ToArray(), true, 101, 1, Catalog, true, Audit());
        var issued = repository.GetPreviewLink(linkId)!;
        var context = CreatePreviewRequestContext(linkId, draftId, issued.LiveSettingsGeneration!.Value);

        switch (invalidation)
        {
            case "expired":
                clock.Advance(TimeSpan.FromHours(2));
                break;
            case "revoked":
                repository.RevokePreviewLink(linkId, Catalog, true, Audit());
                break;
            case "invalidated":
                repository.DirectSave(202, "publication", 0, [Upsert(First, "published")], Catalog, Audit());
                break;
            default:
                Assert.Fail($"Unknown invalidation: {invalidation}");
                break;
        }

        var (controller, papi, melissa, email) = CreatePreviewSubmissionController(context);
        var result = controller.Submit("ignored", new Registration(context.Settings));

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
        papi.VerifyNoOtherCalls();
        melissa.VerifyNoOtherCalls();
        email.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task LivePreviewAdmission_AllowsPublicationWhileRegistrationWorkflowIsPaused()
    {
        SeedVersion(0);
        var draftId = SeedActiveDraft(0, First, "draft");
        var linkId = CreatePreviewLinkAtCurrentRevision(
            draftId, Enumerable.Repeat((byte)84, 32).ToArray(), true, 101, 1, Catalog, true, Audit());
        var issued = repository.GetPreviewLink(linkId)!;
        var context = CreatePreviewRequestContext(linkId, draftId, issued.LiveSettingsGeneration!.Value);

        var db = new Mock<IDbHelper>();
        db.Setup(service => service.CheckPatronIsDuplicate(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(false);
        db.Setup(service => service.AddRegistrationHistoryEntry(It.IsAny<RegistrationHistoryEntry>())).Returns(true);

        var registrationStarted = CompletionSource();
        var releaseRegistration = CompletionSource();
        var papi = new Mock<IPapiClient>();
        var response = new RestResponse<PatronRegistrationCreateResult>
        {
            Data = new PatronRegistrationCreateResult
            {
                PatronID = 123,
                Barcode = "2000000000123",
                PAPIErrorCode = 0
            }
        };
        papi.Setup(service => service.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
            .Callback(() =>
            {
                registrationStarted.TrySetResult(true);
                releaseRegistration.Task.GetAwaiter().GetResult();
            })
            .Returns(response);
        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(service => service.PersonatorRequest(It.IsAny<Clc.Melissa.Models.PersonatorRequestRecord>()))
            .Returns(new RestResponse<Clc.Melissa.Models.PersonatorResponse>
            {
                Data = new Clc.Melissa.Models.PersonatorResponse
                {
                    Records =
                    [
                        new Clc.Melissa.Models.Record
                        {
                            Results = "AS01",
                            AddressLine1 = "1 Main St",
                            AddressLine2 = string.Empty,
                            City = "Columbus",
                            State = "OH",
                            PostalCode = "43215"
                        }
                    ]
                }
            });
        var email = new Mock<IEmailSender>();
        var controller = new PreviewController(
            repository,
            new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = context },
            new TestCache(),
            db.Object,
            papi.Object,
            melissa.Object,
            email.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var registration = new Registration(context.Settings)
        {
            NameFirst = "Jane",
            NameLast = "Doe",
            Birthdate = new DateTime(2000, 1, 1),
            DeliveryOptionId = 1,
            PatronBranchID = 101,
            State = "OH",
            StreetOne = "1 Main St",
            City = "Columbus",
            PostalCode = "43215"
        };

        var previousGlobal = DbHelper.Global;
        DbHelper.Global = db.Object;
        Task<IActionResult>? submission = null;
        Task<Exception?>? publication = null;
        try
        {
            submission = Task.Run(() => controller.Submit("ignored", registration));
            await registrationStarted.Task.WaitAsync(ConcurrencyTestTimeout);

            publication = Task.Run(() =>
            {
                try
                {
                    repository.DirectSave(202, "publication", 0, [Upsert(First, "published")], Catalog, Audit());
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
            await publication.WaitAsync(ConcurrencyTestTimeout);
            Assert.IsNull(publication.Result, publication.Result?.ToString());
            Assert.AreEqual(issued.LiveSettingsGeneration.Value + 1, repository.GetCacheGeneration());

            releaseRegistration.TrySetResult(true);
            var result = await submission.WaitAsync(ConcurrencyTestTimeout);

            Assert.IsInstanceOfType<JsonResult>(result);
            papi.Verify(service => service.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Once);
        }
        finally
        {
            releaseRegistration.TrySetResult(true);
            if (submission is not null)
            {
                try { await submission.WaitAsync(ConcurrencyTestTimeout); }
                catch { /* Preserve the original assertion or workflow failure. */ }
            }
            if (publication is not null)
            {
                try { await publication.WaitAsync(ConcurrencyTestTimeout); }
                catch { /* Preserve the original assertion or publication failure. */ }
            }
            DbHelper.Global = previousGlobal;
        }
    }

    private static void AssertSafeHtml(string value)
    {
        var lower = value.ToLowerInvariant();
        StringAssert.Contains(value, "<strong>Keep formatting</strong>");
        Assert.IsFalse(lower.Contains("<script", StringComparison.Ordinal));
        Assert.IsFalse(lower.Contains("onerror", StringComparison.Ordinal));
        Assert.IsFalse(lower.Contains("javascript:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CommitDraft_PublishesGeneratedCatalogKeysWithForeignKeyEnabled()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        SeedVersion(0);

        var draft = repository.SaveToSharedDraft(102, "dynamic", 0, null,
            [
                new SettingMutation("label.NameFirst", DraftOperation.Upsert, "Draft first name"),
                new SettingMutation("require.PhoneVoice1", DraftOperation.Upsert, "false"),
                new SettingMutation("alert.NameFirst", DraftOperation.Upsert, "Draft validation message")
            ], catalog, Audit());
        repository.CommitDraft(draft.DraftId, catalog, true, Audit(), draft.DraftRevision);

        Assert.AreEqual("Draft first name", ReadSettingValue(102, "dynamic", "label.NameFirst"));
        Assert.AreEqual("false", ReadSettingValue(102, "dynamic", "require.PhoneVoice1"));
        Assert.AreEqual("Draft validation message", ReadSettingValue(102, "dynamic", "alert.NameFirst"));
        Assert.AreEqual(DraftStatus.Committed, repository.GetDraft(draft.DraftId)!.Status);
    }

    [TestMethod]
    public void DirectSave_PersistsHeaderImageAssetIdWithForeignKeyAndAuditIntact()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var content = TestImageData.Create("image/png");
        var asset = assetRepository.Create("header.png", "image/png", content, 101, "form");
        SeedVersion(0);

        repository.DirectSave(101, "form", 0,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, asset.AssetId.ToString())], catalog, Audit());

        var row = QuerySingle("select Setting,Value,OrganizationID,FormCode from dbo.RegistrationFormSettings where Setting='header_image_asset_id'",
            null, reader => new { Setting = reader.GetString(0), Value = reader.GetString(1), OrganizationId = reader.GetInt32(2), FormCode = reader.GetString(3) });
        Assert.AreEqual("header_image_asset_id", row.Setting);
        Assert.AreEqual(asset.AssetId.ToString(), row.Value);
        Assert.AreEqual(101, row.OrganizationId);
        Assert.AreEqual("form", row.FormCode);
        Assert.AreEqual(1L, repository.GetVersion(101, "form"));
        Assert.AreEqual(1L, repository.GetCacheGeneration());
        AssertAuditCount("OverrideCreated", 1);
        AssertAuditCount("DirectSave", 1);
    }

    [TestMethod]
    public void CommitDraft_PublishesHeaderImageAssetIdWithForeignKeyEnabled()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var content = TestImageData.Create("image/jpeg");
        var asset = assetRepository.Create("header.jpg", "image/jpeg", content, 101, "form");
        SeedVersion(0);

        var draft = repository.SaveToSharedDraft(101, "form", 0, null,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, asset.AssetId.ToString())], catalog, Audit());
        repository.CommitDraft(draft.DraftId, catalog, true, Audit(), draft.DraftRevision);

        var row = QuerySingle("select Value,OrganizationID,FormCode from dbo.RegistrationFormSettings where Setting='header_image_asset_id'",
            null, reader => new { Value = reader.GetString(0), OrganizationId = reader.GetInt32(1), FormCode = reader.GetString(2) });
        Assert.AreEqual(asset.AssetId.ToString(), row.Value);
        Assert.AreEqual(101, row.OrganizationId);
        Assert.AreEqual("form", row.FormCode);
        Assert.AreEqual(1L, repository.GetVersion(101, "form"));
        Assert.AreEqual(DraftStatus.Committed, repository.GetDraft(draft.DraftId)!.Status);
        AssertAuditCount("DraftCommitted", 1);
        AssertNoDanglingLiveImageReferences();
    }

    [TestMethod]
    public async Task DirectSaveAndCleanup_Race_SaveWinsAfterRequestAuthorization()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var asset = assetRepository.Create("race-save.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var now = clock.GetUtcNow().UtcDateTime;
        SetAssetCreatedDate(asset.AssetId, now.AddDays(-2));
        SeedVersion(0);
        AssertRequestLevelAssetAuthorizationSucceeded(asset);

        var saveGateHeld = CompletionSource();
        var cleanupAttempted = CompletionSource();
        var allowSave = CompletionSource();
        RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = operation =>
        {
            if (operation == nameof(RegistrationFormAssetRepository.DeleteOrphanedAssets))
            {
                cleanupAttempted.TrySetResult(true);
            }
        };
        RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = operation =>
        {
            if (operation == nameof(SettingsAdministrationRepository.DirectSave))
            {
                saveGateHeld.TrySetResult(true);
                if (!allowSave.Task.Wait(ConcurrencyTestTimeout))
                {
                    throw new TimeoutException("The deterministic save-wins gate was not released.");
                }
            }
        };

        try
        {
            var save = Task.Run(() =>
            {
                try
                {
                    repository.DirectSave(101, "form", 0,
                        [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, asset.AssetId.ToString())], catalog, Audit());
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
            await saveGateHeld.Task.WaitAsync(ConcurrencyTestTimeout);

            var cleanup = Task.Run(() => assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));
            await cleanupAttempted.Task.WaitAsync(ConcurrencyTestTimeout);
            allowSave.TrySetResult(true);

            await Task.WhenAll(save, cleanup).WaitAsync(ConcurrencyTestTimeout);
            Assert.IsNull(save.Result);
            Assert.AreEqual(0, cleanup.Result);
            Assert.IsTrue(assetRepository.Exists(asset.AssetId));
            Assert.AreEqual(asset.AssetId.ToString(), ReadSettingValue(101, "form", "header_image_asset_id"));
            AssertNoDanglingLiveImageReferences();
        }
        finally
        {
            allowSave.TrySetResult(true);
            RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = null;
            RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = null;
        }
    }

    [TestMethod]
    public async Task DirectSaveAndCleanup_Race_CleanupWinsAndSaveDoesNotPersistDanglingReference()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var asset = assetRepository.Create("race-cleanup.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var now = clock.GetUtcNow().UtcDateTime;
        SetAssetCreatedDate(asset.AssetId, now.AddDays(-2));
        SeedVersion(0);
        AssertRequestLevelAssetAuthorizationSucceeded(asset);

        var cleanupGateHeld = CompletionSource();
        var saveAttempted = CompletionSource();
        var allowCleanup = CompletionSource();
        RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = operation =>
        {
            if (operation == nameof(SettingsAdministrationRepository.DirectSave))
            {
                saveAttempted.TrySetResult(true);
            }
        };
        RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = operation =>
        {
            if (operation == nameof(RegistrationFormAssetRepository.DeleteOrphanedAssets))
            {
                cleanupGateHeld.TrySetResult(true);
                if (!allowCleanup.Task.Wait(ConcurrencyTestTimeout))
                {
                    throw new TimeoutException("The deterministic cleanup-wins gate was not released.");
                }
            }
        };

        try
        {
            var cleanup = Task.Run(() => assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));
            await cleanupGateHeld.Task.WaitAsync(ConcurrencyTestTimeout);
            var save = Task.Run(() =>
            {
                try
                {
                    repository.DirectSave(101, "form", 0,
                        [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, asset.AssetId.ToString())], catalog, Audit());
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
            await saveAttempted.Task.WaitAsync(ConcurrencyTestTimeout);
            allowCleanup.TrySetResult(true);

            await Task.WhenAll(cleanup, save).WaitAsync(ConcurrencyTestTimeout);
            Assert.AreEqual(1, cleanup.Result);
            Assert.IsInstanceOfType<InvalidOperationException>(save.Result);
            Assert.IsFalse(assetRepository.Exists(asset.AssetId));
            Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where Setting='header_image_asset_id'"));
            AssertNoDanglingLiveImageReferences();
        }
        finally
        {
            allowCleanup.TrySetResult(true);
            RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = null;
            RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = null;
        }
    }

    [TestMethod]
    public async Task SaveToSharedDraftAndCleanup_Race_SaveWinsForFirstImageReference()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var asset = assetRepository.Create("draft-race-save.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var now = clock.GetUtcNow().UtcDateTime;
        SetAssetCreatedDate(asset.AssetId, now.AddDays(-2));
        SeedVersion(0);
        AssertRequestLevelAssetAuthorizationSucceeded(asset);

        var saveGateHeld = CompletionSource();
        var cleanupAttempted = CompletionSource();
        var allowSave = CompletionSource();
        RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = operation =>
        {
            if (operation == nameof(RegistrationFormAssetRepository.DeleteOrphanedAssets))
            {
                cleanupAttempted.TrySetResult(true);
            }
        };
        RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = operation =>
        {
            if (operation == nameof(SettingsAdministrationRepository.SaveToSharedDraft))
            {
                saveGateHeld.TrySetResult(true);
                if (!allowSave.Task.Wait(ConcurrencyTestTimeout))
                {
                    throw new TimeoutException("The deterministic draft save-wins gate was not released.");
                }
            }
        };

        try
        {
            var save = Task.Run(() =>
            {
                try
                {
                    return (SaveToDraftResult?)repository.SaveToSharedDraft(101, "form", 0, null,
                        [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, asset.AssetId.ToString())], catalog, Audit());
                }
                catch
                {
                    return (SaveToDraftResult?)null;
                }
            });
            await saveGateHeld.Task.WaitAsync(ConcurrencyTestTimeout);
            var cleanup = Task.Run(() => assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));
            await cleanupAttempted.Task.WaitAsync(ConcurrencyTestTimeout);
            allowSave.TrySetResult(true);

            await Task.WhenAll(save, cleanup).WaitAsync(ConcurrencyTestTimeout);
            Assert.IsNotNull(save.Result);
            Assert.AreEqual(0, cleanup.Result);
            Assert.IsTrue(assetRepository.Exists(asset.AssetId));
            Assert.AreEqual(asset.AssetId.ToString(), ReadActiveDraftImageValue(save.Result!.DraftId));
            AssertNoDanglingActiveDraftImageReferences();
        }
        finally
        {
            allowSave.TrySetResult(true);
            RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = null;
            RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = null;
        }
    }

    [TestMethod]
    public async Task SaveToSharedDraftAndCleanup_Race_CleanupWinsWithoutCreatingFirstImageDraftReference()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var asset = assetRepository.Create("draft-race-cleanup.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var now = clock.GetUtcNow().UtcDateTime;
        SetAssetCreatedDate(asset.AssetId, now.AddDays(-2));
        SeedVersion(0);
        AssertRequestLevelAssetAuthorizationSucceeded(asset);

        var cleanupGateHeld = CompletionSource();
        var saveAttempted = CompletionSource();
        var allowCleanup = CompletionSource();
        RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = operation =>
        {
            if (operation == nameof(SettingsAdministrationRepository.SaveToSharedDraft))
            {
                saveAttempted.TrySetResult(true);
            }
        };
        RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = operation =>
        {
            if (operation == nameof(RegistrationFormAssetRepository.DeleteOrphanedAssets))
            {
                cleanupGateHeld.TrySetResult(true);
                if (!allowCleanup.Task.Wait(ConcurrencyTestTimeout))
                {
                    throw new TimeoutException("The deterministic cleanup-wins gate was not released.");
                }
            }
        };

        try
        {
            var cleanup = Task.Run(() => assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));
            await cleanupGateHeld.Task.WaitAsync(ConcurrencyTestTimeout);
            var save = Task.Run(() =>
            {
                try
                {
                    repository.SaveToSharedDraft(101, "form", 0, null,
                        [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, asset.AssetId.ToString())], catalog, Audit());
                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
            await saveAttempted.Task.WaitAsync(ConcurrencyTestTimeout);
            allowCleanup.TrySetResult(true);

            await Task.WhenAll(cleanup, save).WaitAsync(ConcurrencyTestTimeout);
            Assert.AreEqual(1, cleanup.Result);
            Assert.IsInstanceOfType<InvalidOperationException>(save.Result);
            Assert.AreEqual(0, CountActiveDrafts());
            AssertNoDanglingActiveDraftImageReferences();
        }
        finally
        {
            allowCleanup.TrySetResult(true);
            RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = null;
            RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = null;
        }
    }

    [TestMethod]
    public async Task SaveToSharedDraftAndCleanup_Race_ReplacementKeepsNewActiveDraftReference()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var first = assetRepository.Create("draft-first.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var replacement = assetRepository.Create("draft-replacement.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var now = clock.GetUtcNow().UtcDateTime;
        SetAssetCreatedDate(first.AssetId, now.AddDays(-2));
        SetAssetCreatedDate(replacement.AssetId, now.AddDays(-2));
        SeedVersion(0);
        var draft = repository.SaveToSharedDraft(101, "form", 0, null,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, first.AssetId.ToString())], catalog, Audit());
        AssertRequestLevelAssetAuthorizationSucceeded(replacement);

        var saveGateHeld = CompletionSource();
        var cleanupAttempted = CompletionSource();
        var allowSave = CompletionSource();
        RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = operation =>
        {
            if (operation == nameof(RegistrationFormAssetRepository.DeleteOrphanedAssets))
            {
                cleanupAttempted.TrySetResult(true);
            }
        };
        RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = operation =>
        {
            if (operation == nameof(SettingsAdministrationRepository.SaveToSharedDraft))
            {
                saveGateHeld.TrySetResult(true);
                if (!allowSave.Task.Wait(ConcurrencyTestTimeout))
                {
                    throw new TimeoutException("The deterministic replacement save-wins gate was not released.");
                }
            }
        };

        try
        {
            var save = Task.Run(() => repository.SaveToSharedDraft(101, "form", 0, draft.DraftId,
                [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, replacement.AssetId.ToString())], catalog, Audit(), draft.DraftRevision));
            await saveGateHeld.Task.WaitAsync(ConcurrencyTestTimeout);
            var cleanup = Task.Run(() => assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));
            await cleanupAttempted.Task.WaitAsync(ConcurrencyTestTimeout);
            allowSave.TrySetResult(true);

            await Task.WhenAll(save, cleanup).WaitAsync(ConcurrencyTestTimeout);
            Assert.AreEqual(1, cleanup.Result);
            Assert.IsTrue(assetRepository.Exists(replacement.AssetId));
            Assert.IsFalse(assetRepository.Exists(first.AssetId));
            Assert.AreEqual(replacement.AssetId.ToString(), ReadActiveDraftImageValue(draft.DraftId));
            AssertNoDanglingActiveDraftImageReferences();
        }
        finally
        {
            allowSave.TrySetResult(true);
            RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = null;
            RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = null;
        }
    }

    [TestMethod]
    public async Task CommitDraftAndCleanup_Race_PublishedLiveReferenceKeepsAsset()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var asset = assetRepository.Create("commit-race.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var now = clock.GetUtcNow().UtcDateTime;
        SetAssetCreatedDate(asset.AssetId, now.AddDays(-2));
        SeedVersion(0);
        var draft = repository.SaveToSharedDraft(101, "form", 0, null,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, asset.AssetId.ToString())], catalog, Audit());
        Assert.AreEqual(0, assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));

        var commitGateHeld = CompletionSource();
        var cleanupAttempted = CompletionSource();
        var allowCommit = CompletionSource();
        RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = operation =>
        {
            if (operation == nameof(RegistrationFormAssetRepository.DeleteOrphanedAssets))
            {
                cleanupAttempted.TrySetResult(true);
            }
        };
        RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = operation =>
        {
            if (operation == nameof(SettingsAdministrationRepository.CommitDraft))
            {
                commitGateHeld.TrySetResult(true);
                if (!allowCommit.Task.Wait(ConcurrencyTestTimeout))
                {
                    throw new TimeoutException("The deterministic commit gate was not released.");
                }
            }
        };

        try
        {
            var commit = Task.Run(() =>
            {
                repository.CommitDraft(draft.DraftId, catalog, true, Audit(), draft.DraftRevision);
                return true;
            });
            await commitGateHeld.Task.WaitAsync(ConcurrencyTestTimeout);
            var cleanup = Task.Run(() => assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));
            await cleanupAttempted.Task.WaitAsync(ConcurrencyTestTimeout);
            allowCommit.TrySetResult(true);

            await Task.WhenAll(commit, cleanup).WaitAsync(ConcurrencyTestTimeout);
            Assert.IsTrue(commit.Result);
            Assert.AreEqual(0, cleanup.Result);
            Assert.IsTrue(assetRepository.Exists(asset.AssetId));
            Assert.AreEqual(asset.AssetId.ToString(), ReadSettingValue(101, "form", "header_image_asset_id"));
            Assert.AreEqual(DraftStatus.Committed, repository.GetDraft(draft.DraftId)!.Status);
            AssertNoDanglingLiveImageReferences();
        }
        finally
        {
            allowCommit.TrySetResult(true);
            RegistrationFormAssetReferenceCoordinator.BeforeAcquireForTesting = null;
            RegistrationFormAssetReferenceCoordinator.AfterAcquireForTesting = null;
        }
    }

    [TestMethod]
    public void AssetRepository_StoresAndRetrievesContentMetadataAndSha256Hash()
    {
        var content = TestImageData.Create("image/png");
        var created = assetRepository.Create("..\\uploads\\header.png", "IMAGE/PNG", content, 101, "form");

        Assert.AreEqual("header.png", created.FileName);
        Assert.AreEqual("image/png", created.ContentType);
        CollectionAssert.AreEqual(content, created.Content);
        Assert.AreEqual(RegistrationFormAssetUploadValidation.ComputeContentHash(content), created.ContentHash);

        var metadata = assetRepository.GetMetadata(created.AssetId);
        Assert.IsNotNull(metadata);
        Assert.AreEqual(created.AssetId, metadata.AssetId);
        Assert.AreEqual(created.ContentHash, metadata.ContentHash);
        Assert.AreEqual(101, metadata.UploadOrganizationId);
        Assert.AreEqual("form", metadata.UploadFormCode);
        Assert.IsTrue(assetRepository.Exists(created.AssetId));

        var loaded = assetRepository.Get(created.AssetId);
        Assert.IsNotNull(loaded);
        CollectionAssert.AreEqual(content, loaded.Content);
        Assert.AreEqual(created.ContentType, loaded.ContentType);
    }

    [TestMethod]
    public void AssetRepository_ReturnsNoResultForMissingAsset()
    {
        Assert.IsFalse(assetRepository.Exists(987654321));
        Assert.IsNull(assetRepository.Get(987654321));
        Assert.IsNull(assetRepository.GetMetadata(987654321));
    }

    [TestMethod]
    public void AssetCleanup_RespectsGracePeriodAndProcessesOnlyTheRequestedBatch()
    {
        var oldAssets = Enumerable.Range(0, 3)
            .Select(index => assetRepository.Create($"old-{index}.png", "image/png", TestImageData.Create("image/png"), 101, "form"))
            .ToArray();
        var fresh = assetRepository.Create("fresh.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var asset in oldAssets)
        {
            SetAssetCreatedDate(asset.AssetId, now.AddDays(-2));
        }
        SetAssetCreatedDate(fresh.AssetId, now.AddHours(-1));

        var firstDeleted = assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 2);

        Assert.AreEqual(2, firstDeleted);
        Assert.AreEqual(1, oldAssets.Count(asset => assetRepository.Exists(asset.AssetId)));
        Assert.IsTrue(assetRepository.Exists(fresh.AssetId));
        Assert.AreEqual(1, assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 2));
        Assert.IsFalse(oldAssets.Any(asset => assetRepository.Exists(asset.AssetId)));
        Assert.IsTrue(assetRepository.Exists(fresh.AssetId));
    }

    [TestMethod]
    public void AssetCleanup_DoesNotDeleteAnUploadDuringItsGraceWindowThenDeletesIt()
    {
        var asset = assetRepository.Create("abandoned.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var now = clock.GetUtcNow().UtcDateTime;
        SetAssetCreatedDate(asset.AssetId, now.AddHours(-1));

        Assert.AreEqual(0, assetRepository.DeleteOrphanedAssets(now.AddHours(-2), 100));
        Assert.IsTrue(assetRepository.Exists(asset.AssetId));
        Assert.AreEqual(1, assetRepository.DeleteOrphanedAssets(now, 100));
        Assert.IsFalse(assetRepository.Exists(asset.AssetId));
    }

    [TestMethod]
    public void AssetCleanup_DeletesReplacedAssetButRetainsReplacement()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var first = assetRepository.Create("first.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var replacement = assetRepository.Create("replacement.png", "image/png", TestImageData.Create("image/png"), 101, "form");
        var now = clock.GetUtcNow().UtcDateTime;
        SetAssetCreatedDate(first.AssetId, now.AddDays(-2));
        SetAssetCreatedDate(replacement.AssetId, now.AddHours(-1));

        repository.DirectSave(101, "form", 0,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, first.AssetId.ToString())], catalog, Audit());
        repository.DirectSave(101, "form", 1,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, replacement.AssetId.ToString())], catalog, Audit());

        Assert.AreEqual(1, assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));
        Assert.IsFalse(assetRepository.Exists(first.AssetId));
        Assert.IsTrue(assetRepository.Exists(replacement.AssetId));
    }

    [TestMethod]
    public void AssetCleanup_ProtectsReferencesAcrossScopesAndActiveDraftsUntilFinalRemoval()
    {
        var catalog = new SettingCatalog().All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var live = assetRepository.Create("live.png", "image/png", TestImageData.Create("image/png"), 101, "live");
        var anotherScope = assetRepository.Create("another-scope.png", "image/png", TestImageData.Create("image/png"), 999, "other");
        var draftOnly = assetRepository.Create("draft-only.png", "image/png", TestImageData.Create("image/png"), 777, "draft");
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var asset in new[] { live, anotherScope, draftOnly })
        {
            SetAssetCreatedDate(asset.AssetId, now.AddDays(-2));
        }

        repository.DirectSave(101, "live", 0,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, live.AssetId.ToString())], catalog, Audit());
        repository.DirectSave(101, "other", 0,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, anotherScope.AssetId.ToString())], catalog, Audit());
        var draft = repository.SaveToSharedDraft(102, "draft", 0, null,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, draftOnly.AssetId.ToString())], catalog, Audit());

        Assert.AreEqual(0, assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));
        Assert.IsTrue(assetRepository.Exists(live.AssetId));
        Assert.IsTrue(assetRepository.Exists(anotherScope.AssetId));
        Assert.IsTrue(assetRepository.Exists(draftOnly.AssetId));

        repository.DirectSave(101, "live", 1,
            [new SettingMutation("header_image_asset_id", DraftOperation.RemoveOverride, null)], catalog, Audit());
        repository.DirectSave(101, "other", 1,
            [new SettingMutation("header_image_asset_id", DraftOperation.RemoveOverride, null)], catalog, Audit());
        Assert.AreEqual(2, assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));
        Assert.IsFalse(assetRepository.Exists(live.AssetId));
        Assert.IsFalse(assetRepository.Exists(anotherScope.AssetId));
        Assert.IsTrue(assetRepository.Exists(draftOnly.AssetId));

        repository.DiscardDraft(draft.DraftId, catalog, true, Audit(), draft.DraftRevision);
        Assert.AreEqual(1, assetRepository.DeleteOrphanedAssets(now.AddDays(-1), 100));
        Assert.IsFalse(assetRepository.Exists(draftOnly.AssetId));
    }

    [TestMethod]
    public void SaveToSharedDraft_NoActiveDraft_CreatesDraftAndPersistsAllChangesAtomically()
    {
        SeedVersion(17);
        var result = repository.SaveToSharedDraft(101, "form", 17, null,
            [Upsert(First, "one"), Upsert(Second, "two")], Catalog, Audit());

        Assert.IsTrue(result.DraftCreated);
        Assert.AreEqual(1, CountActiveDrafts());
        var draft = ReadDraft(result.DraftId);
        Assert.AreEqual(17L, draft.BaselineVersion);
        Assert.AreEqual("integration-admin", draft.ModifiedBy);
        Assert.IsNotNull(draft.ModifiedAtUtc);
        CollectionAssert.AreEquivalent(new[] { "test.first|Upsert|one", "test.second|Upsert|two" }, ReadChanges(result.DraftId).ToArray());
        AssertAuditCount("DraftCreated", 1);
        AssertAuditCount("DraftEdited", 1);
        Assert.AreEqual("{\"changeCount\":2}", ReadAudits().Single(row => row.EventType == "DraftEdited").MetadataJson);
    }

    [TestMethod]
    public void SaveToSharedDraft_PostInsertMutationFailure_RollsBackDraftChangesAndAudits()
    {
        SeedVersion(9);
        var missing = new SettingMutation("missing.key", DraftOperation.Upsert, "bad");
        Assert.ThrowsException<InvalidOperationException>(() => repository.SaveToSharedDraft(101, "form", 9, null,
            [Upsert(First, "written-before-failure"), missing], Catalog, Audit()));

        AssertNoDraftChangesOrSuccessAudits();
        Assert.AreEqual(9L, ReadScopeVersion());
        var retry = repository.SaveToSharedDraft(101, "form", 9, null, [Upsert(First, "retry")], Catalog, Audit());
        Assert.IsTrue(retry.DraftCreated);
        Assert.AreEqual(1, CountActiveDrafts());
    }

    [TestMethod]
    public void SaveToSharedDraft_CurrentDraftRevisionAllowsIntentionalMerge()
    {
        SeedVersion(3);
        var first = repository.SaveToSharedDraft(101, "form", 3, null, [Upsert(First, "existing")], Catalog, Audit());
        var beforeEdited = ReadAudits().Count(row => row.EventType == "DraftEdited");

        var result = repository.SaveToSharedDraft(101, "form", 3, first.DraftId,
            [Upsert(Second, "new")], Catalog, Audit(), first.DraftRevision);

        Assert.AreEqual(first.DraftId, result.DraftId);
        Assert.IsFalse(result.DraftCreated);
        Assert.AreEqual(1, CountActiveDrafts());
        CollectionAssert.AreEquivalent(new[] { "test.first|Upsert|existing", "test.second|Upsert|new" }, ReadChanges(result.DraftId).ToArray());
        AssertAuditCount("DraftCreated", 1);
        Assert.AreEqual(beforeEdited + 1, ReadAudits().Count(row => row.EventType == "DraftEdited"));
        Assert.AreEqual(first.DraftRevision + 1, result.DraftRevision);
    }

    [TestMethod]
    public void SaveToSharedDraft_StaleSecondAdministratorCannotOverwriteSameKey()
    {
        SeedVersion(0);
        var initial = repository.SaveToSharedDraft(101, "form", 0, null,
            [Upsert(First, "initial")], Catalog, Audit());
        var administratorARevision = repository.GetDraft(initial.DraftId)!.Revision;
        var administratorBRevision = administratorARevision;

        var administratorA = repository.SaveToSharedDraft(101, "form", 0, initial.DraftId,
            [Upsert(First, "administrator A")], Catalog, Audit(), administratorARevision);

        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", 0, initial.DraftId,
            [Upsert(First, "stale administrator B")], Catalog, Audit(), administratorBRevision));
        Assert.AreEqual("administrator A", ReadChanges(initial.DraftId).Single().Split('|', 3)[2]);
        Assert.AreEqual(administratorA.DraftRevision, repository.GetDraft(initial.DraftId)!.Revision);

        var merged = repository.SaveToSharedDraft(101, "form", 0, initial.DraftId,
            [Upsert(Second, "administrator B current edit")], Catalog, Audit(), administratorA.DraftRevision);
        Assert.AreEqual(administratorA.DraftRevision + 1, merged.DraftRevision);
        CollectionAssert.AreEquivalent(
            new[] { "test.first|Upsert|administrator A", "test.second|Upsert|administrator B current edit" },
            ReadChanges(initial.DraftId).ToArray());
    }

    [TestMethod]
    public void RemoveDraftChange_RequiresTheCurrentDraftRevision()
    {
        SeedVersion(0);
        var initial = repository.SaveToSharedDraft(101, "form", 0, null,
            [Upsert(First, "initial")], Catalog, Audit());
        var currentRevision = initial.DraftRevision;
        var changed = repository.SaveToSharedDraft(101, "form", 0, initial.DraftId,
            [Upsert(Second, "another edit")], Catalog, Audit(), currentRevision);

        Assert.ThrowsException<DBConcurrencyException>(() => repository.RemoveDraftChange(
            initial.DraftId, First.Key, Catalog, true, Audit(), currentRevision));
        Assert.IsTrue(ReadChanges(initial.DraftId).Any(change => change.StartsWith("test.first|", StringComparison.Ordinal)));

        repository.RemoveDraftChange(initial.DraftId, First.Key, Catalog, true, Audit(), changed.DraftRevision);
        Assert.IsFalse(ReadChanges(initial.DraftId).Any(change => change.StartsWith("test.first|", StringComparison.Ordinal)));
        Assert.AreEqual(changed.DraftRevision + 1, repository.GetDraft(initial.DraftId)!.Revision);
    }

    [TestMethod]
    public void CommitDraft_SucceedsWhenExpectedRevisionIsCurrent()
    {
        SeedVersion(0);
        var draft = repository.SaveToSharedDraft(101, "form", 0, null,
            [Upsert(First, "current")], Catalog, Audit());

        repository.CommitDraft(draft.DraftId, Catalog, true, Audit(), draft.DraftRevision);

        Assert.AreEqual(DraftStatus.Committed, repository.GetDraft(draft.DraftId)!.Status);
        Assert.AreEqual("current", ReadSettingValue(101, "form", First.Key));
        Assert.AreEqual(1L, repository.GetVersion(101, "form"));
        AssertAuditCount("DraftCommitted", 1);
    }

    [TestMethod]
    public void CommitDraft_StaleRevisionPublishesNothingAndLeavesNewerDraftActive()
    {
        SeedVersion(0);
        var initial = repository.SaveToSharedDraft(101, "form", 0, null,
            [Upsert(First, "initial")], Catalog, Audit());
        var newer = repository.SaveToSharedDraft(101, "form", 0, initial.DraftId,
            [Upsert(Second, "newer")], Catalog, Audit(), initial.DraftRevision);

        Assert.ThrowsException<DBConcurrencyException>(() => repository.CommitDraft(
            initial.DraftId, Catalog, true, Audit(), initial.DraftRevision));

        var remaining = repository.GetDraft(initial.DraftId)!;
        Assert.AreEqual(DraftStatus.Active, remaining.Status);
        Assert.AreEqual(newer.DraftRevision, remaining.Revision);
        CollectionAssert.AreEquivalent(
            new[] { "test.first|Upsert|initial", "test.second|Upsert|newer" },
            ReadChanges(initial.DraftId).ToArray());
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where OrganizationID=101 and FormCode='form'"));
        Assert.AreEqual(0L, repository.GetVersion(101, "form"));
        AssertAuditCount("DraftCommitted", 0);
    }

    [TestMethod]
    public void DiscardDraft_SucceedsWhenExpectedRevisionIsCurrent()
    {
        SeedVersion(0);
        var draft = repository.SaveToSharedDraft(101, "form", 0, null,
            [Upsert(First, "current")], Catalog, Audit());

        repository.DiscardDraft(draft.DraftId, Catalog, true, Audit(), draft.DraftRevision);

        Assert.AreEqual(DraftStatus.Discarded, repository.GetDraft(draft.DraftId)!.Status);
        Assert.AreEqual(draft.DraftRevision + 1, repository.GetDraft(draft.DraftId)!.Revision);
        CollectionAssert.AreEquivalent(new[] { "test.first|Upsert|current" }, ReadChanges(draft.DraftId).ToArray());
        Assert.AreEqual(0L, repository.GetVersion(101, "form"));
        AssertAuditCount("DraftDiscarded", 1);
    }

    [TestMethod]
    public void DiscardDraft_StaleRevisionLeavesNewerDraftAndChangesActive()
    {
        SeedVersion(0);
        var initial = repository.SaveToSharedDraft(101, "form", 0, null,
            [Upsert(First, "initial")], Catalog, Audit());
        var newer = repository.SaveToSharedDraft(101, "form", 0, initial.DraftId,
            [Upsert(Second, "newer")], Catalog, Audit(), initial.DraftRevision);

        Assert.ThrowsException<DBConcurrencyException>(() => repository.DiscardDraft(
            initial.DraftId, Catalog, true, Audit(), initial.DraftRevision));

        var remaining = repository.GetDraft(initial.DraftId)!;
        Assert.AreEqual(DraftStatus.Active, remaining.Status);
        Assert.AreEqual(newer.DraftRevision, remaining.Revision);
        CollectionAssert.AreEquivalent(
            new[] { "test.first|Upsert|initial", "test.second|Upsert|newer" },
            ReadChanges(initial.DraftId).ToArray());
        AssertAuditCount("DraftDiscarded", 0);
    }

    [TestMethod]
    public void CommitAndDiscardDraft_RequireAnExpectedRevision()
    {
        SeedVersion(0);
        var draft = repository.SaveToSharedDraft(101, "form", 0, null,
            [Upsert(First, "current")], Catalog, Audit());

        Assert.ThrowsException<DBConcurrencyException>(() => repository.CommitDraft(
            draft.DraftId, Catalog, true, Audit(), null));
        Assert.AreEqual(DraftStatus.Active, repository.GetDraft(draft.DraftId)!.Status);

        Assert.ThrowsException<DBConcurrencyException>(() => repository.DiscardDraft(
            draft.DraftId, Catalog, true, Audit(), null));
        Assert.AreEqual(DraftStatus.Active, repository.GetDraft(draft.DraftId)!.Status);
        CollectionAssert.AreEquivalent(new[] { "test.first|Upsert|current" }, ReadChanges(draft.DraftId).ToArray());
        AssertAuditCount("DraftCommitted", 0);
        AssertAuditCount("DraftDiscarded", 0);
    }

    [TestMethod]
    public void SaveToSharedDraft_StaleVersion_RollsBackEverything()
    {
        SeedVersion(6);
        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", 5, null,
            [Upsert(First, "stale")], Catalog, Audit()));
        AssertNoDraftChangesOrSuccessAudits();
        Assert.AreEqual(6L, ReadScopeVersion());
    }

    [TestMethod]
    public void SaveToSharedDraft_ActiveDraftWithDifferentBaseline_IsUnchanged()
    {
        SeedVersion(6);
        var draftId = SeedActiveDraft(6, First, "existing");
        var auditCount = ReadAudits().Count;

        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", 5, draftId,
            [Upsert(Second, "stale")], Catalog, Audit(), 0));

        Assert.AreEqual(1, CountActiveDrafts());
        CollectionAssert.AreEqual(new[] { "test.first|Upsert|existing" }, ReadChanges(draftId).ToArray());
        Assert.AreEqual(auditCount, ReadAudits().Count);
    }

    [TestMethod]
    public void SaveToSharedDraft_ExistingDraft_UpdatesSubmittedKeyAndPreservesOtherChanges()
    {
        SeedVersion(1);
        var first = repository.SaveToSharedDraft(101, "form", 1, null,
            [Upsert(First, "old"), Upsert(Second, "untouched")], Catalog, Audit());

        var result = repository.SaveToSharedDraft(101, "form", 1, first.DraftId,
            [new SettingMutation(First.Key, DraftOperation.RemoveOverride, null)], Catalog, Audit(), first.DraftRevision);

        Assert.AreEqual(first.DraftId, result.DraftId);
        Assert.IsFalse(result.DraftCreated);
        CollectionAssert.AreEquivalent(new[] { "test.first|RemoveOverride|", "test.second|Upsert|untouched" }, ReadChanges(result.DraftId).ToArray());
        Assert.AreEqual(1, ReadChanges(result.DraftId).Count(row => row.StartsWith("test.first|", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SaveToSharedDraft_WrongExpectedDraftId_ThrowsAndWritesNothing()
    {
        SeedVersion(1);
        var existing = repository.SaveToSharedDraft(101, "form", 1, null, [Upsert(First, "existing")], Catalog, Audit());
        var auditCount = ReadAudits().Count;

        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", 1, existing.DraftId + 100,
            [Upsert(Second, "must-not-write")], Catalog, Audit()));

        Assert.AreEqual(1, CountActiveDrafts());
        CollectionAssert.AreEqual(new[] { "test.first|Upsert|existing" }, ReadChanges(existing.DraftId).ToArray());
        Assert.AreEqual(auditCount, ReadAudits().Count);
    }

    [TestMethod]
    public void SaveToSharedDraft_ExpectedDraftIdButNoActiveDraft_ThrowsAndDoesNotCreateReplacement()
    {
        SeedVersion(1);
        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", 1, 42,
            [Upsert(First, "must-not-write")], Catalog, Audit()));
        AssertNoDraftChangesOrSuccessAudits();
    }

    [TestMethod]
    public async Task SaveToSharedDraft_ConcurrentFirstSaves_ProduceOneActiveDraft()
    {
        SeedVersion(1);
        using var barrier = new Barrier(2);
        Task<(SaveToDraftResult? Result, Exception? Error)> Start(SettingMutation mutation) => Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            try
            {
                return ((SaveToDraftResult?)new SettingsAdministrationRepository(databaseConnectionString!)
                    .SaveToSharedDraft(101, "form", 1, null, [mutation], Catalog, Audit()), (Exception?)null);
            }
            catch (Exception exception) { return ((SaveToDraftResult?)null, (Exception?)exception); }
        });

        var tasks = new[] { Start(Upsert(First, "one")), Start(Upsert(Second, "two")) };
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(40));
        var outcomes = tasks.Select(task => task.Result).ToList();
        var errors = outcomes.Where(item => item.Error is not null).Select(item => item.Error!).ToList();
        Assert.IsTrue(errors.All(error => error is DBConcurrencyException || error is SqlException { Number: 1205 or 2601 or 2627 }),
            $"Unexpected concurrent-save error: {errors.FirstOrDefault()}");
        var successes = outcomes.Where(item => item.Result is not null).Select(item => item.Result!).ToList();
        Assert.IsTrue(successes.Count is 1 or 2);
        Assert.AreEqual(1, CountActiveDrafts());
        Assert.AreEqual(1, successes.Count(item => item.DraftCreated));
        if (successes.Count == 2)
        {
            Assert.AreEqual(successes[0].DraftId, successes[1].DraftId);
            CollectionAssert.AreEquivalent(new[] { "test.first|Upsert|one", "test.second|Upsert|two" }, ReadChanges(successes[0].DraftId).ToArray());
        }
        else Assert.AreEqual(1, ReadChanges(successes[0].DraftId).Count);
        AssertAuditCount("DraftCreated", 1);
    }

    [TestMethod]
    public void SaveToSharedDraft_SensitiveValueIsStoredButNotLeakedToAudit()
    {
        SeedVersion(1);
        const string secret = "super-secret-value";
        var result = repository.SaveToSharedDraft(101, "form", 1, null, [Upsert(Secret, secret)], Catalog, Audit());

        StringAssert.Contains(ReadChanges(result.DraftId).Single(), secret);
        var audits = ReadAudits();
        Assert.IsFalse(audits.Any(row => (row.MetadataJson ?? "").Contains(secret, StringComparison.Ordinal) ||
            (row.FailureReason ?? "").Contains(secret, StringComparison.Ordinal) ||
            (row.PreviousValue ?? "").Contains(secret, StringComparison.Ordinal) ||
            (row.NewValue ?? "").Contains(secret, StringComparison.Ordinal)));
        // DraftCreated and DraftEdited are draft-level events and deliberately do not classify an individual mutation.
        Assert.IsTrue(audits.All(row => !row.IsSensitive));
    }

    [TestMethod]
    public void SaveToSharedDraft_OrdinaryEditRevokesLivePreviewFromOlderRevision()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var hash = Enumerable.Repeat((byte)31, 32).ToArray();
        var linkId = CreatePreviewLinkAtCurrentRevision(draftId, hash, true, 101, 1, Catalog, true, Audit());
        Assert.IsNotNull(repository.ResolvePreviewContext(hash));

        repository.SaveToSharedDraft(101, "form", 1, draftId,
            [Upsert(Second, "ordinary edit")], Catalog, Audit(), 0);

        Assert.IsNull(repository.ResolvePreviewContext(hash));
        Assert.IsNotNull(repository.GetPreviewLink(linkId)!.RevokedAtUtc);
        Assert.AreEqual("ordinary edit", ReadChanges(draftId).Single(change => change.StartsWith("test.second|", StringComparison.Ordinal)).Split('|')[2]);
    }

    [TestMethod]
    public void RemoveDraftChange_RevokesLivePreviewFromOlderRevision()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var hash = Enumerable.Repeat((byte)32, 32).ToArray();
        var linkId = CreatePreviewLinkAtCurrentRevision(draftId, hash, true, 101, 1, Catalog, true, Audit());
        Assert.IsNotNull(repository.ResolvePreviewContext(hash));

        repository.RemoveDraftChange(draftId, First.Key, Catalog, true, Audit(), 0);

        Assert.IsNull(repository.ResolvePreviewContext(hash));
        Assert.IsNotNull(repository.GetPreviewLink(linkId)!.RevokedAtUtc);
        Assert.AreEqual(0, ReadChanges(draftId).Count);
    }

    [TestMethod]
    public void SaveFormCode_StaleModifiedAtRejectsSecondMetadataEdit()
    {
        var originalModifiedAtUtc = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        SeedFormCodeMetadata("kids", "Original", originalModifiedAtUtc);
        var firstEdit = new FormCodeMetadata(101, "kids", "First edit", "Description", originalModifiedAtUtc,
            "first", originalModifiedAtUtc, "first");

        repository.SaveFormCode(firstEdit, false, Audit(), originalModifiedAtUtc);
        Assert.AreEqual("First edit", ReadFormCodeMetadata("kids").DisplayName);

        var staleEdit = firstEdit with { DisplayName = "Stale edit" };
        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveFormCode(staleEdit, false, Audit(), originalModifiedAtUtc));

        Assert.AreEqual("First edit", ReadFormCodeMetadata("kids").DisplayName);
        AssertAuditCount("ConcurrencyConflict", 1);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(24)]
    [DataRow(168)]
    public void CreatePreviewLink_BoundedLifetimePersistsExactExpiration(int lifetimeHours)
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var linkId = CreatePreviewLinkAtCurrentRevision(draftId, new byte[32], false, 101, lifetimeHours, Catalog, true, Audit());

        Assert.AreEqual(clock.GetUtcNow().UtcDateTime.AddHours(lifetimeHours), ReadPreviewExpiration(linkId));
        AssertAuditCount("PreviewLinkCreated", 1);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreatePreviewLink_RejectsStaleDraftRevisionBeforeWritingLinkOrSuccessAudit(bool allowLiveSubmission)
    {
        SeedVersion(1);
        var created = repository.SaveToSharedDraft(101, "form", 1, null,
            [Upsert(First, "revision N")], Catalog, Audit());
        var loaded = repository.GetDraft(created.DraftId)!;
        var expectedRevision = loaded.Revision;

        var newer = repository.SaveToSharedDraft(101, "form", 1, created.DraftId,
            [Upsert(Second, "revision N+1")], Catalog, Audit(), expectedRevision);
        Assert.AreEqual(expectedRevision + 1, newer.DraftRevision);

        Assert.ThrowsException<DBConcurrencyException>(() => repository.CreatePreviewLink(created.DraftId,
            Enumerable.Repeat((byte)(allowLiveSubmission ? 91 : 90), 32).ToArray(), allowLiveSubmission,
            101, 1, Catalog, true, Audit(), expectedRevision));

        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingAuditEvents where EventType='PreviewLinkCreated' and Succeeded=1"));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreatePreviewLink_RejectsMissingDraftRevisionInsteadOfUsingCurrent(bool allowLiveSubmission)
    {
        SeedVersion(1);
        var created = repository.SaveToSharedDraft(101, "form", 1, null,
            [Upsert(First, "current revision")], Catalog, Audit());
        var current = repository.GetDraft(created.DraftId)!;

        Assert.ThrowsException<DBConcurrencyException>(() => repository.CreatePreviewLink(created.DraftId,
            Enumerable.Repeat((byte)(allowLiveSubmission ? 93 : 92), 32).ToArray(), allowLiveSubmission,
            101, 1, Catalog, true, Audit(), null));

        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingAuditEvents where EventType='PreviewLinkCreated' and Succeeded=1"));
        Assert.AreEqual(current.Revision, repository.GetDraft(created.DraftId)!.Revision);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreatePreviewLink_SucceedsWithCurrentDraftRevision(bool allowLiveSubmission)
    {
        SeedVersion(1);
        var created = repository.SaveToSharedDraft(101, "form", 1, null,
            [Upsert(First, "current revision")], Catalog, Audit());
        var current = repository.GetDraft(created.DraftId)!;
        var linkId = repository.CreatePreviewLink(created.DraftId,
            Enumerable.Repeat((byte)(allowLiveSubmission ? 95 : 94), 32).ToArray(), allowLiveSubmission,
            101, 1, Catalog, true, Audit(), current.Revision);

        var link = repository.GetPreviewLink(linkId)!;
        Assert.AreEqual(allowLiveSubmission, link.AllowLiveSubmission);
        Assert.AreEqual(allowLiveSubmission ? repository.GetCacheGeneration() : null, link.LiveSettingsGeneration);
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingAuditEvents where EventType='PreviewLinkCreated' and Succeeded=1"));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(169)]
    public void CreatePreviewLink_InvalidLifetimeWritesNothing(int lifetimeHours)
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => CreatePreviewLinkAtCurrentRevision(draftId,
            new byte[32], false, 101, lifetimeHours, Catalog, true, Audit()));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks"));
        AssertAuditCount("PreviewLinkCreated", 0);
    }

    [TestMethod]
    public void ResolvePreviewContext_UsesTrustedClockAtExpirationBoundary()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var hash = Enumerable.Repeat((byte)7, 32).ToArray();
        CreatePreviewLinkAtCurrentRevision(draftId, hash, false, 101, 1, Catalog, true, Audit());

        clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        Assert.IsNotNull(repository.ResolvePreviewContext(hash));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNull(repository.ResolvePreviewContext(hash));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNull(repository.ResolvePreviewContext(hash));
    }

    [TestMethod]
    public void ResolveAndReplacePreviewContext_RejectLegacyNullExpiration()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var hash = Enumerable.Repeat((byte)8, 32).ToArray();
        using (var connection = Open())
            Execute(connection, @"insert dbo.RegistrationSettingPreviewLinks(DraftId,TokenHash,AllowLiveSubmission,OperationalBranchId,CreatedBy,ModifiedBy,ExpiresAtUtc)
values(@draftId,@hash,0,101,'legacy','legacy',null)", parameters: command =>
            {
                command.Parameters.AddWithValue("@draftId", draftId);
                command.Parameters.AddWithValue("@hash", hash);
            });
        var linkId = Scalar<long>("select PreviewLinkId from dbo.RegistrationSettingPreviewLinks");

        Assert.IsNull(repository.ResolvePreviewContext(hash));
        Assert.ThrowsException<DBConcurrencyException>(() => repository.ReplacePreviewLinkMode(linkId, new byte[32], true, Catalog, true, Audit()));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks"));
    }

    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void ReplacePreviewLinkMode_PreservesExactExpiration(bool originalLive, bool replacementLive)
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        clock.SetUtcNow(DateTimeOffset.UtcNow);
        var originalId = CreatePreviewLinkAtCurrentRevision(draftId, Enumerable.Repeat((byte)3, 32).ToArray(),
            originalLive, 101, 24, Catalog, true, Audit());
        var expectedExpiration = repository.GetPreviewLink(originalId)!.ExpiresAtUtc;

        var replacementId = repository.ReplacePreviewLinkMode(originalId, Enumerable.Repeat((byte)4, 32).ToArray(),
            replacementLive, Catalog, true, Audit());

        Assert.IsNotNull(replacementId);
        Assert.AreEqual(expectedExpiration, repository.GetPreviewLink(replacementId.Value)!.ExpiresAtUtc);
        Assert.IsNotNull(repository.GetPreviewLink(originalId)!.RevokedAtUtc);
    }

    [TestMethod]
    public void ReplacePreviewLinkMode_ExpiredLinkWritesNothing()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var originalId = CreatePreviewLinkAtCurrentRevision(draftId, Enumerable.Repeat((byte)5, 32).ToArray(),
            false, 101, 1, Catalog, true, Audit());
        var original = repository.GetPreviewLink(originalId)!;
        var auditCount = Scalar<int>("select count(*) from dbo.RegistrationSettingAuditEvents where EventType='PreviewLinkModeReplaced' and Succeeded=1");
        clock.Advance(TimeSpan.FromHours(2));

        Assert.ThrowsException<DBConcurrencyException>(() => repository.ReplacePreviewLinkMode(originalId,
            Enumerable.Repeat((byte)6, 32).ToArray(), true, Catalog, true, Audit()));

        var unchanged = repository.GetPreviewLink(originalId)!;
        Assert.IsNull(unchanged.RevokedAtUtc);
        Assert.AreEqual(original.ExpiresAtUtc, unchanged.ExpiresAtUtc);
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks"));
        Assert.AreEqual(auditCount, Scalar<int>("select count(*) from dbo.RegistrationSettingAuditEvents where EventType='PreviewLinkModeReplaced' and Succeeded=1"));
    }

    [TestMethod]
    public void RestorePreviewLink_ExpiredLinkPreservesIdentityAndMakesOriginalTokenResolvable()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var hash = Enumerable.Repeat((byte)11, 32).ToArray();
        var linkId = CreatePreviewLinkAtCurrentRevision(draftId, hash, true, 101, 1, Catalog, true, Audit());
        var previousExpiration = repository.GetPreviewLink(linkId)!.ExpiresAtUtc!.Value;
        clock.Advance(TimeSpan.FromHours(2));

        repository.RestorePreviewLink(linkId, 24, Catalog, true, Audit());

        var restored = repository.GetPreviewLink(linkId)!;
        Assert.AreEqual(linkId, restored.PreviewLinkId);
        Assert.AreEqual(draftId, restored.DraftId);
        CollectionAssert.AreEqual(hash, restored.TokenHash);
        Assert.IsTrue(restored.AllowLiveSubmission);
        Assert.AreEqual(101, restored.OperationalBranchId);
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime.AddHours(24), restored.ExpiresAtUtc);
        Assert.AreEqual(linkId, repository.ResolvePreviewContext(hash)!.Link.PreviewLinkId);
        var audit = ReadAudits().Single(row => row.EventType == "PreviewLinkRestored");
        using var metadata = JsonDocument.Parse(audit.MetadataJson!);
        Assert.AreEqual(previousExpiration, metadata.RootElement.GetProperty("previousExpiresAtUtc").GetDateTime());
        Assert.AreEqual(restored.ExpiresAtUtc, metadata.RootElement.GetProperty("newExpiresAtUtc").GetDateTime());
    }

    [TestMethod]
    public void RestorePreviewLink_NullExpirationIsEligibleAndAuditedAsJsonNull()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var hash = Enumerable.Repeat((byte)12, 32).ToArray();
        var linkId = SeedPreviewLink(draftId, hash, expiration: null);

        repository.RestorePreviewLink(linkId, 6, Catalog, true, Audit());

        Assert.AreEqual(clock.GetUtcNow().UtcDateTime.AddHours(6), repository.GetPreviewLink(linkId)!.ExpiresAtUtc);
        using var metadata = JsonDocument.Parse(ReadAudits().Single(row => row.EventType == "PreviewLinkRestored").MetadataJson!);
        Assert.AreEqual(JsonValueKind.Null, metadata.RootElement.GetProperty("previousExpiresAtUtc").ValueKind);
    }

    [TestMethod]
    public void RestorePreviewLink_ActiveRevokedAndInactiveDraftsAreRejectedWithoutSuccessAudit()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var activeId = CreatePreviewLinkAtCurrentRevision(draftId, Enumerable.Repeat((byte)13, 32).ToArray(), false, 101, 2, Catalog, true, Audit());
        Assert.ThrowsException<DBConcurrencyException>(() => repository.RestorePreviewLink(activeId, 24, Catalog, true, Audit()));
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime.AddHours(2), repository.GetPreviewLink(activeId)!.ExpiresAtUtc);

        var revokedId = SeedPreviewLink(draftId, Enumerable.Repeat((byte)14, 32).ToArray(), clock.GetUtcNow().UtcDateTime.AddHours(-1), revoked: true);
        Assert.ThrowsException<DBConcurrencyException>(() => repository.RestorePreviewLink(revokedId, 24, Catalog, true, Audit()));

        foreach (var (status, hashByte) in new[] { ("Committed", (byte)22), ("Discarded", (byte)23) })
        {
            var inactiveDraft = SeedDraft(1, status, First, status);
            var linkId = SeedPreviewLink(inactiveDraft, Enumerable.Repeat(hashByte, 32).ToArray(), clock.GetUtcNow().UtcDateTime.AddHours(-1));
            Assert.ThrowsException<DBConcurrencyException>(() => repository.RestorePreviewLink(linkId, 24, Catalog, true, Audit()));
            Assert.AreEqual(status, repository.GetPreviewLink(linkId)!.DraftStatus);
        }
        AssertAuditCount("PreviewLinkRestored", 0);
    }

    [TestMethod]
    public void RestrictedDraft_RequiresGlobalAdministratorForRestoreAndDelete()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, Secret, "secret");
        var restoreId = SeedPreviewLink(draftId, Enumerable.Repeat((byte)15, 32).ToArray(), clock.GetUtcNow().UtcDateTime.AddHours(-1));
        var deleteId = SeedPreviewLink(draftId, Enumerable.Repeat((byte)16, 32).ToArray(), clock.GetUtcNow().UtcDateTime.AddHours(-1));

        Assert.ThrowsException<UnauthorizedAccessException>(() => repository.RestorePreviewLink(restoreId, 24, Catalog, false, Audit()));
        Assert.ThrowsException<UnauthorizedAccessException>(() => repository.DeletePreviewLink(deleteId, Catalog, false, Audit()));
        repository.RestorePreviewLink(restoreId, 24, Catalog, true, Audit());
        repository.DeletePreviewLink(deleteId, Catalog, true, Audit());

        Assert.IsNotNull(repository.GetPreviewLink(restoreId));
        Assert.IsNull(repository.GetPreviewLink(deleteId));
        AssertAuditCount("PreviewLinkRestored", 1);
        AssertAuditCount("PreviewLinkDeleted", 1);
    }

    [TestMethod]
    public void DeletePreviewLink_ExpiredAndRevokedLinksAreRemovedWithStateMetadata()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var expiredId = SeedPreviewLink(draftId, Enumerable.Repeat((byte)17, 32).ToArray(), clock.GetUtcNow().UtcDateTime.AddHours(-1));
        var revokedId = SeedPreviewLink(draftId, Enumerable.Repeat((byte)18, 32).ToArray(), clock.GetUtcNow().UtcDateTime.AddHours(1), revoked: true);

        repository.DeletePreviewLink(expiredId, Catalog, true, Audit());
        repository.DeletePreviewLink(revokedId, Catalog, true, Audit());

        Assert.IsNull(repository.GetPreviewLink(expiredId));
        Assert.IsNull(repository.GetPreviewLink(revokedId));
        Assert.IsFalse(repository.GetPreviewLinks(draftId).Any(link => link.PreviewLinkId is var id && (id == expiredId || id == revokedId)));
        var metadata = ReadAudits().Where(row => row.EventType == "PreviewLinkDeleted")
            .Select(row => JsonDocument.Parse(row.MetadataJson!).RootElement.Clone()).ToList();
        Assert.IsTrue(metadata.Any(value => value.GetProperty("expired").GetBoolean() && !value.GetProperty("revoked").GetBoolean()));
        Assert.IsTrue(metadata.Any(value => value.GetProperty("revoked").GetBoolean()));
    }

    [TestMethod]
    public void DeletePreviewLink_ActiveLinkIsRejectedWithoutDeletingOrAuditing()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var linkId = SeedPreviewLink(draftId, Enumerable.Repeat((byte)19, 32).ToArray(), clock.GetUtcNow().UtcDateTime.AddHours(1));

        Assert.ThrowsException<DBConcurrencyException>(() => repository.DeletePreviewLink(linkId, Catalog, true, Audit()));

        Assert.IsNotNull(repository.GetPreviewLink(linkId));
        AssertAuditCount("PreviewLinkDeleted", 0);
    }

    [TestMethod]
    public async Task ConcurrentRestoreAndDelete_ProducesOneCompleteOutcomeWithoutPartialWrites()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var linkId = SeedPreviewLink(draftId, Enumerable.Repeat((byte)20, 32).ToArray(), clock.GetUtcNow().UtcDateTime.AddHours(-1));
        using var barrier = new Barrier(2);
        Task<Exception?> Run(Action<SettingsAdministrationRepository> operation) => Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            try
            {
                operation(new SettingsAdministrationRepository(databaseConnectionString!, clock));
                return null;
            }
            catch (Exception exception) { return exception; }
        });

        var restore = Run(repo => repo.RestorePreviewLink(linkId, 24, Catalog, true, Audit()));
        var delete = Run(repo => repo.DeletePreviewLink(linkId, Catalog, true, Audit()));
        await Task.WhenAll(restore, delete).WaitAsync(TimeSpan.FromSeconds(40));

        var errors = new[] { restore.Result, delete.Result }.Where(error => error is not null).ToList();
        Assert.IsTrue(errors.All(error => error is DBConcurrencyException or SqlException { Number: 1205 }),
            $"Unexpected concurrency error: {errors.FirstOrDefault()}");
        Assert.AreEqual(1, errors.Count);
        var link = repository.GetPreviewLink(linkId);
        Assert.AreEqual(link is null ? 1 : 0, ReadAudits().Count(row => row.EventType == "PreviewLinkDeleted"));
        Assert.AreEqual(link is null ? 0 : 1, ReadAudits().Count(row => row.EventType == "PreviewLinkRestored"));
    }

    [TestMethod]
    public async Task ConcurrentRestoreAndDraftDiscard_HasControlledOutcomeAndNoPartialRestore()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var linkId = SeedPreviewLink(draftId, Enumerable.Repeat((byte)21, 32).ToArray(), clock.GetUtcNow().UtcDateTime.AddHours(-1));
        using var barrier = new Barrier(2);
        Exception? restoreError = null;
        Exception? discardError = null;
        var restore = Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            try { new SettingsAdministrationRepository(databaseConnectionString!, clock).RestorePreviewLink(linkId, 24, Catalog, true, Audit()); }
            catch (Exception exception) { restoreError = exception; }
        });
        var discard = Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            try { new SettingsAdministrationRepository(databaseConnectionString!, clock).DiscardDraft(draftId, Catalog, true, Audit(), 0); }
            catch (Exception exception) { discardError = exception; }
        });
        await Task.WhenAll(restore, discard).WaitAsync(TimeSpan.FromSeconds(40));

        Assert.IsTrue(new[] { restoreError, discardError }.Where(error => error is not null)
            .All(error => error is DBConcurrencyException or SqlException { Number: 1205 }));
        var restoredAudits = ReadAudits().Count(row => row.EventType == "PreviewLinkRestored");
        Assert.IsTrue(restoredAudits is 0 or 1);
        if (repository.GetDraft(draftId)!.Status != DraftStatus.Active)
            Assert.IsNull(repository.ResolvePreviewContext(Enumerable.Repeat((byte)21, 32).ToArray()));
    }

    private static readonly TimeSpan ConcurrencyTestTimeout = TimeSpan.FromSeconds(20);

    private static TaskCompletionSource<bool> CompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void AssertRequestLevelAssetAuthorizationSucceeded(RegistrationFormAsset asset)
    {
        var authorization = new RegistrationFormAssetAuthorization(
            assetRepository,
            new TestCache(),
            Options.Create(new SettingsAdministrationOptions { SystemOrganizationId = 1 }));
        Assert.IsNotNull(authorization.GetAuthorizedMetadata(
            asset.AssetId,
            asset.UploadOrganizationId!.Value,
            asset.UploadFormCode ?? string.Empty));
    }

    private string ReadActiveDraftImageValue(long draftId) => QuerySingle(
        """
        select c.Value
        from dbo.RegistrationSettingDraftChanges c
        join dbo.RegistrationSettingDrafts d on d.DraftId=c.DraftId
        where c.DraftId=@draftId and d.Status='Active'
          and c.SettingKey='header_image_asset_id' and c.Operation='Upsert'
        """,
        command => command.Parameters.AddWithValue("@draftId", draftId),
        reader => reader.GetString(0));

    private void AssertNoDanglingLiveImageReferences()
    {
        Assert.AreEqual(0, Scalar<int>("""
            select count(*)
            from dbo.RegistrationFormSettings s
            left join dbo.RegistrationFormAssets a
              on a.AssetId=TRY_CONVERT(int,s.Value)
            where s.Setting='header_image_asset_id' and a.AssetId is null;
            """));
    }

    private void AssertNoDanglingActiveDraftImageReferences()
    {
        Assert.AreEqual(0, Scalar<int>("""
            select count(*)
            from dbo.RegistrationSettingDraftChanges c
            join dbo.RegistrationSettingDrafts d on d.DraftId=c.DraftId
            left join dbo.RegistrationFormAssets a
              on a.AssetId=TRY_CONVERT(int,c.Value)
            where d.Status='Active'
              and c.SettingKey='header_image_asset_id'
              and c.Operation='Upsert'
              and a.AssetId is null;
            """));
    }

    private void SeedVersion(long version)
    {
        using var connection = Open();
        Execute(connection,
            "insert dbo.RegistrationSettingScopeVersions(OrganizationId,FormCode,Version) values(101,'form',@version)",
            parameters: command => command.Parameters.AddWithValue("@version", version));
    }

    private static void SeedLegacyHeaderImageSetting(SqlConnection connection)
    {
        Execute(connection, "insert dbo.RegistrationFormSettingTypes(Setting) values('header_image_url');");
        Execute(connection, "insert dbo.RegistrationFormSettings(OrganizationID,Setting,FormCode,Value) values(101,'header_image_url','form','https://example.test/legacy.png');");
    }

    private static void SeedLegacyRegistrationFieldSetting(SqlConnection connection, int organizationId, string settingKey, string formCode, string value)
    {
        Execute(connection, @"
if not exists (select 1 from dbo.RegistrationFormSettingTypes where Setting=@settingKey)
    insert dbo.RegistrationFormSettingTypes(Setting) values(@settingKey);
insert dbo.RegistrationFormSettings(OrganizationID,Setting,FormCode,Value)
values(@organizationId,@settingKey,@formCode,@value);", parameters: command =>
        {
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@settingKey", settingKey);
            command.Parameters.AddWithValue("@formCode", formCode);
            command.Parameters.AddWithValue("@value", value);
        });
    }

    private void SeedRawDraftChange(long draftId, string settingKey, string value)
    {
        SeedRawDraftMutation(draftId, settingKey, "Upsert", value);
    }

    private void SeedRawDraftMutation(long draftId, string settingKey, string operation, string? value)
    {
        using var connection = Open();
        Execute(connection, @"insert dbo.RegistrationSettingDraftChanges(DraftId,SettingKey,Operation,Value,ModifiedBy)
values(@draftId,@settingKey,@operation,@value,'migration-test')", parameters: command =>
        {
            command.Parameters.AddWithValue("@draftId", draftId);
            command.Parameters.AddWithValue("@settingKey", settingKey);
            command.Parameters.AddWithValue("@operation", operation);
            command.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);
        });
    }

    private long SeedDraftAtScope(int organizationId, string formCode, string status)
    {
        using var connection = Open();
        using var command = Command(connection, @"insert dbo.RegistrationSettingDrafts(OrganizationId,FormCode,BaselineVersion,Status,CreatedBy,ModifiedBy)
output inserted.DraftId values(@organizationId,@formCode,0,@status,'other','other')");
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.Parameters.AddWithValue("@formCode", formCode);
        command.Parameters.AddWithValue("@status", status);
        return (long)command.ExecuteScalar()!;
    }

    private long SeedActiveDraft(long baselineVersion, SettingDefinition definition, string value)
        => SeedDraft(baselineVersion, "Active", definition, value);

    private long SeedDraft(long baselineVersion, string status, SettingDefinition definition, string value)
    {
        using var connection = Open();
        using var command = Command(connection, @"insert dbo.RegistrationSettingDrafts(OrganizationId,FormCode,BaselineVersion,Status,CreatedBy,ModifiedBy)
output inserted.DraftId values(101,'form',@baseline,@status,'other','other')");
        command.Parameters.AddWithValue("@baseline", baselineVersion);
        command.Parameters.AddWithValue("@status", status);
        var draftId = (long)command.ExecuteScalar()!;
        Execute(connection, @"insert dbo.RegistrationSettingDraftChanges(DraftId,SettingKey,Operation,Value,ModifiedBy)
values(@draftId,@key,'Upsert',@value,'other')", parameters: mutation =>
        {
            mutation.Parameters.AddWithValue("@draftId", draftId);
            mutation.Parameters.AddWithValue("@key", definition.Key);
            mutation.Parameters.AddWithValue("@value", value);
        });
        return draftId;
    }
    private long SeedPreviewLink(long draftId, byte[] hash, DateTime? expiration, bool revoked = false,
        bool allowLiveSubmission = false, long? liveSettingsGeneration = null)
    {
        using var connection = Open();
        using var command = Command(connection, @"insert dbo.RegistrationSettingPreviewLinks(
DraftId,TokenHash,AllowLiveSubmission,OperationalBranchId,LiveSettingsGeneration,CreatedBy,ModifiedBy,ExpiresAtUtc,RevokedAtUtc,RevokedBy)
output inserted.PreviewLinkId values(@draftId,@hash,@allowLiveSubmission,101,@liveSettingsGeneration,'other','other',@expiration,@revokedAt,@revokedBy)");
        command.Parameters.AddWithValue("@draftId", draftId);
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@allowLiveSubmission", allowLiveSubmission);
        command.Parameters.AddWithValue("@liveSettingsGeneration", (object?)liveSettingsGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue("@expiration", (object?)expiration ?? DBNull.Value);
        command.Parameters.AddWithValue("@revokedAt", revoked ? clock.GetUtcNow().UtcDateTime : DBNull.Value);
        command.Parameters.AddWithValue("@revokedBy", revoked ? "other" : DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    private void DropDraftRevisionColumn()
    {
        using var connection = Open();
        Execute(connection, @"
if col_length('dbo.RegistrationSettingDrafts', 'Revision') is not null
begin
    if exists
    (
        select 1
        from sys.default_constraints
        where parent_object_id = object_id('dbo.RegistrationSettingDrafts')
          and name = 'DF_RSD_Revision'
    )
        alter table dbo.RegistrationSettingDrafts drop constraint DF_RSD_Revision;
    alter table dbo.RegistrationSettingDrafts drop column Revision;
end");
    }

    private void RestoreDraftRevisionColumn()
    {
        using var connection = Open();
        Execute(connection, @"
if col_length('dbo.RegistrationSettingDrafts', 'Revision') is null
    alter table dbo.RegistrationSettingDrafts
        add Revision bigint not null constraint DF_RSD_Revision default 0 with values;");
    }

    private long CreatePreviewLinkAtCurrentRevision(long draftId, byte[] tokenHash, bool allowLiveSubmission,
        int operationalBranchId, int lifetimeHours, IReadOnlyDictionary<string, SettingDefinition> catalog,
        bool canManageSensitive, AuditContext audit)
    {
        var expectedDraftRevision = repository.GetDraft(draftId)?.Revision;
        Assert.IsNotNull(expectedDraftRevision);
        return repository.CreatePreviewLink(draftId, tokenHash, allowLiveSubmission, operationalBranchId,
            lifetimeHours, catalog, canManageSensitive, audit, expectedDraftRevision);
    }

    private PreviewRequestContext CreatePreviewRequestContext(long linkId, long draftId, long generation)
    {
        var link = repository.GetPreviewLink(linkId)!;
        var draft = repository.GetDraft(draftId)!;
        var cache = new TestCache { Generation = generation };
        cache.OrganizationCache = cache.OrganizationCache
            .Append(new OrganizationsGetRow
            {
                OrganizationID = link.OperationalBranchId,
                Name = "Integration branch",
                OrganizationCodeID = 3,
                ParentOrganizationID = 2
            })
            .ToList();
        return new PreviewRequestContext(
            link,
            draft,
            new PreviewSettingProvider(draft, link.OperationalBranchId, cache, 1));
    }

    private (PreviewController Controller, Mock<IPapiClient> Papi, Mock<IMelissaRestClient> Melissa, Mock<IEmailSender> Email)
        CreatePreviewSubmissionController(PreviewRequestContext context)
    {
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var controller = new PreviewController(
            repository,
            new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = context },
            new TestCache(),
            Mock.Of<IDbHelper>(),
            papi.Object,
            melissa.Object,
            email.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, papi, melissa, email);
    }

    private void SeedFormCodeMetadata(string formCode, string displayName, DateTime modifiedAtUtc)
    {
        using var connection = Open();
        Execute(connection, @"insert dbo.RegistrationFormCodeMetadata(
OrganizationId,FormCode,DisplayName,Description,CreatedAtUtc,CreatedBy,ModifiedAtUtc,ModifiedBy)
values(101,@formCode,@displayName,'seed',@modifiedAtUtc,'seed',@modifiedAtUtc,'seed')", parameters: command =>
        {
            command.Parameters.AddWithValue("@formCode", formCode);
            command.Parameters.AddWithValue("@displayName", displayName);
            command.Parameters.AddWithValue("@modifiedAtUtc", modifiedAtUtc);
        });
    }
    private FormCodeMetadata ReadFormCodeMetadata(string formCode) => QuerySingle(
        "select OrganizationId,FormCode,DisplayName,Description,CreatedAtUtc,CreatedBy,ModifiedAtUtc,ModifiedBy from dbo.RegistrationFormCodeMetadata where OrganizationId=101 and FormCode=@formCode",
        command => command.Parameters.AddWithValue("@formCode", formCode),
        reader => new FormCodeMetadata(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), Text(reader, 3),
            reader.GetDateTime(4), reader.GetString(5), reader.GetDateTime(6), reader.GetString(7)));
    private long ReadScopeVersion() => Scalar<long>("select Version from dbo.RegistrationSettingScopeVersions where OrganizationId=101 and FormCode='form'");
    private int CountActiveDrafts() => Scalar<int>("select count(*) from dbo.RegistrationSettingDrafts where OrganizationId=101 and FormCode='form' and Status='Active'");
    private DraftState ReadDraft(long draftId) => QuerySingle("select BaselineVersion,ModifiedAtUtc,ModifiedBy from dbo.RegistrationSettingDrafts where DraftId=@id",
        command => command.Parameters.AddWithValue("@id", draftId), reader => new DraftState(reader.GetInt64(0), reader.GetDateTime(1), reader.GetString(2)));
    private List<string> ReadChanges(long draftId) => Query("select SettingKey,Operation,Value from dbo.RegistrationSettingDraftChanges where DraftId=@id order by SettingKey",
        command => command.Parameters.AddWithValue("@id", draftId), reader => $"{reader.GetString(0)}|{reader.GetString(1)}|{(reader.IsDBNull(2) ? "" : reader.GetString(2))}");
    private string ReadSettingValue(int organizationId, string formCode, string settingKey) => QuerySingle(
        "select Value from dbo.RegistrationFormSettings where OrganizationID=@organizationId and FormCode=@formCode and Setting=@settingKey",
        command =>
        {
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@formCode", formCode);
            command.Parameters.AddWithValue("@settingKey", settingKey);
        }, reader => reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
    private List<AuditState> ReadAudits() => Query("select EventType,MetadataJson,FailureReason,PreviousValue,NewValue,IsSensitive from dbo.RegistrationSettingAuditEvents order by AuditEventId", null,
        reader => new AuditState(reader.GetString(0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4), reader.GetBoolean(5)));
    private DateTime ReadPreviewExpiration(long previewLinkId) => QuerySingle("select ExpiresAtUtc from dbo.RegistrationSettingPreviewLinks where PreviewLinkId=@id",
        command => command.Parameters.AddWithValue("@id", previewLinkId), reader => reader.GetDateTime(0));
    private void AssertAuditCount(string eventType, int expected) => Assert.AreEqual(expected, ReadAudits().Count(row => row.EventType == eventType));
    private void AssertNoDraftChangesOrSuccessAudits()
    {
        Assert.AreEqual(0, CountActiveDrafts());
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingDraftChanges"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingAuditEvents where Succeeded=1 and EventType in ('DraftCreated','DraftEdited')"));
    }

    private static SettingMutation Upsert(SettingDefinition definition, string value) => new(definition.Key, DraftOperation.Upsert, value);
    private static AuditContext Audit() => new("integration", "integration-admin", 101, 101, 101, "form", "integration-test", "127.0.0.1");

    private static void DeployExistingRegistrationSettingsSchema(SqlConnection connection)
    {
        // These tables belong to the pre-existing clcdb schema. The test fixture
        // creates the smallest faithful version so the migration and repository
        // exercise the production FK rather than a mocked write path.
        Execute(connection, @"
if object_id('dbo.RegistrationFormSettingTypes','U') is null
begin
    create table dbo.RegistrationFormSettingTypes
    (
        Setting nvarchar(200) not null
            constraint PK_Registration_Form_Setting_Types primary key
    );
end;
if object_id('dbo.RegistrationFormSettings','U') is null
begin
    create table dbo.RegistrationFormSettings
    (
        OrganizationID int not null,
        Setting nvarchar(200) not null,
        FormCode nvarchar(64) not null
            constraint DF_RegistrationFormSettings_FormCode default '',
        Value nvarchar(max) null,
        constraint PK_RegistrationFormSettings primary key (OrganizationID,Setting,FormCode),
        constraint FK_Registration_Form_Settings_Registration_Form_Setting_Types
            foreign key (Setting) references dbo.RegistrationFormSettingTypes(Setting)
    );
end;");

        foreach (var key in ExistingSettingTypeKeys)
        {
            Execute(connection,
                "if not exists (select 1 from dbo.RegistrationFormSettingTypes where Setting=@key) insert dbo.RegistrationFormSettingTypes(Setting) values(@key);",
                parameters: command => command.Parameters.AddWithValue("@key", key));
        }

        foreach (var key in IntegrationTestSettingTypeKeys)
        {
            Execute(connection,
                "if not exists (select 1 from dbo.RegistrationFormSettingTypes where Setting=@key) insert dbo.RegistrationFormSettingTypes(Setting) values(@key);",
                parameters: command => command.Parameters.AddWithValue("@key", key));
        }
    }

    private void SetAssetCreatedDate(int assetId, DateTime createdDateUtc)
    {
        using var connection = Open();
        Execute(connection,
            "update dbo.RegistrationFormAssets set CreatedDate=@createdDateUtc,ModifiedDate=@createdDateUtc where AssetId=@assetId",
            parameters: command =>
            {
                command.Parameters.AddWithValue("@createdDateUtc", createdDateUtc);
                command.Parameters.AddWithValue("@assetId", assetId);
            });
    }

    private SqlConnection Open() { var connection = new SqlConnection(databaseConnectionString!); connection.Open(); return connection; }
    private T Scalar<T>(string sql) { using var connection = Open(); using var command = Command(connection, sql); return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T)); }
    private T QuerySingle<T>(string sql, Action<SqlCommand>? parameters, Func<SqlDataReader, T> map) => Query(sql, parameters, map).Single();
    private List<T> Query<T>(string sql, Action<SqlCommand>? parameters, Func<SqlDataReader, T> map)
    {
        using var connection = Open(); using var command = Command(connection, sql); parameters?.Invoke(command);
        using var reader = command.ExecuteReader(); var rows = new List<T>(); while (reader.Read()) rows.Add(map(reader)); return rows;
    }
    private static string? Text(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static SqlCommand Command(SqlConnection connection, string sql) => new(sql, connection) { CommandTimeout = 15 };
    private static void Execute(SqlConnection connection, string sql, int timeout = 15, Action<SqlCommand>? parameters = null)
    {
        using var command = new SqlCommand(sql, connection) { CommandTimeout = timeout };
        parameters?.Invoke(command);
        command.ExecuteNonQuery();
    }
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static string MigrationDirectory() => Path.Combine(RepositoryRoot(), "database", "migrations");
    private static string MigrationPath(string fileName) => Path.Combine(MigrationDirectory(), fileName);
    private static IReadOnlyList<string> NumberedMigrationPaths() => Directory
        .EnumerateFiles(MigrationDirectory(), "*.sql")
        .OrderBy(path => int.Parse(Path.GetFileName(path).Split('-', 2)[0]))
        .ToArray();
    private static void DropDatabaseCore(string configured)
    {
        if (databaseName is null) return;
        var nameToDrop = databaseName;
        try
        {
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", ConnectTimeout = 10 };
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            Execute(connection, $"""
                if db_id('{nameToDrop}') is not null
                begin
                    alter database [{nameToDrop}] set single_user with rollback immediate;
                    drop database [{nameToDrop}];
                end
                """, 30);
            databaseCreated = false;
            schemaReady = false;
            databaseConnectionString = null;
            databaseName = null;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not drop temporary SQL integration database {nameToDrop}.", exception);
        }
    }

    private static void TryDropDatabase(string configured)
    {
        try
        {
            DropDatabaseCore(configured);
        }
        catch (Exception exception)
        {
            classContext?.WriteLine($"Best-effort cleanup failed for temporary SQL integration database {databaseName}: {exception.Message}");
        }
    }
    private sealed record DraftState(long BaselineVersion, DateTime ModifiedAtUtc, string ModifiedBy);
    private sealed record AuditState(string EventType, string? MetadataJson, string? FailureReason, string? PreviousValue, string? NewValue, bool IsSensitive);
}

[TestClass]
public sealed class DatabaseCleanupModeSelectorTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Select_NoConfiguredConnectionSkipsCleanup(string? configured) =>
        Assert.AreEqual(DatabaseCleanupMode.None, DatabaseCleanupModeSelector.Select(configured, "unavailable"));

    [TestMethod]
    public void Select_UnavailableInfrastructureUsesBestEffortCleanup() =>
        Assert.AreEqual(DatabaseCleanupMode.BestEffort,
            DatabaseCleanupModeSelector.Select("Server=test", "SQL Server unavailable"));

    [TestMethod]
    public void Select_SuccessfulFixtureUsesStrictCleanup() =>
        Assert.AreEqual(DatabaseCleanupMode.Strict,
            DatabaseCleanupModeSelector.Select("Server=test", null));
}
