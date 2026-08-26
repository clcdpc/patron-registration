/* Add optimistic draft revisions and generation-bound live-preview admission. */
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

/*
   A NULL LiveSettingsGeneration identifies a live link issued before the
   generation-bound admission contract existed. Do not bind those links to
   the current generation: migration 007 or 008 may already have transformed
   their active draft and older copies of those migrations left no durable
   marker by which this migration could distinguish the changed draft. A
   blanket revocation is therefore required to avoid silently legitimizing an
   old bearer token. Links issued after this migration receive the generation
   in the repository transaction; rerunning this migration does not touch
   already-revoked rows.
*/
UPDATE p
SET RevokedAtUtc = SYSUTCDATETIME(),
    RevokedBy = COALESCE(RevokedBy, '012-draft-revision-and-preview-generation.sql'),
    ModifiedAtUtc = SYSUTCDATETIME(),
    ModifiedBy = '012-draft-revision-and-preview-generation.sql'
FROM dbo.RegistrationSettingPreviewLinks AS p
INNER JOIN dbo.RegistrationSettingDrafts AS d
    ON d.DraftId = p.DraftId
WHERE p.AllowLiveSubmission = 1
  AND p.LiveSettingsGeneration IS NULL
  AND p.RevokedAtUtc IS NULL
  AND d.Status = 'Active';

COMMIT;
