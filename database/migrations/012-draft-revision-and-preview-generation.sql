/* Add optimistic draft revisions and bind live preview links to the settings generation. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.RegistrationSettingDrafts', 'U') IS NULL
    THROW 50031, 'dbo.RegistrationSettingDrafts must exist before migration 012 is applied.', 1;

IF OBJECT_ID('dbo.RegistrationSettingPreviewLinks', 'U') IS NULL
    THROW 50032, 'dbo.RegistrationSettingPreviewLinks must exist before migration 012 is applied.', 1;

IF OBJECT_ID('dbo.RegistrationSettingsCacheGeneration', 'U') IS NULL
    THROW 50033, 'dbo.RegistrationSettingsCacheGeneration must exist before migration 012 is applied.', 1;

IF COL_LENGTH('dbo.RegistrationSettingDrafts', 'Revision') IS NULL
BEGIN
    ALTER TABLE dbo.RegistrationSettingDrafts
        ADD Revision bigint NOT NULL CONSTRAINT DF_RSD_Revision DEFAULT 0;
END;

IF COL_LENGTH('dbo.RegistrationSettingPreviewLinks', 'LiveSettingsGeneration') IS NULL
BEGIN
    ALTER TABLE dbo.RegistrationSettingPreviewLinks
        ADD LiveSettingsGeneration bigint NULL;
END;

UPDATE p
SET LiveSettingsGeneration = g.Generation
FROM dbo.RegistrationSettingPreviewLinks p
CROSS JOIN dbo.RegistrationSettingsCacheGeneration g
WHERE p.AllowLiveSubmission = 1
  AND p.LiveSettingsGeneration IS NULL;

COMMIT;
