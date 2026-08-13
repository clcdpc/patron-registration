/* Remove the retired external registration header-image setting. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.RegistrationFormSettings', 'U') IS NULL
BEGIN
    THROW 50008, 'dbo.RegistrationFormSettings must exist before migration 007 is applied.', 1;
END;

IF OBJECT_ID('dbo.RegistrationFormSettingTypes', 'U') IS NULL
BEGIN
    THROW 50009, 'dbo.RegistrationFormSettingTypes must exist before migration 007 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationFormSettings', 'Setting') IS NULL
BEGIN
    THROW 50010, 'dbo.RegistrationFormSettings.Setting must exist before migration 007 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationFormSettingTypes', 'Setting') IS NULL
BEGIN
    THROW 50011, 'dbo.RegistrationFormSettingTypes.Setting must exist before migration 007 is applied.', 1;
END;

IF OBJECT_ID('dbo.RegistrationSettingDraftChanges', 'U') IS NULL
BEGIN
    THROW 50012, 'dbo.RegistrationSettingDraftChanges must exist before migration 007 is applied.', 1;
END;

IF OBJECT_ID('dbo.RegistrationSettingDrafts', 'U') IS NULL
BEGIN
    THROW 50013, 'dbo.RegistrationSettingDrafts must exist before migration 007 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationSettingDraftChanges', 'DraftId') IS NULL
   OR COL_LENGTH('dbo.RegistrationSettingDraftChanges', 'SettingKey') IS NULL
BEGIN
    THROW 50014, 'dbo.RegistrationSettingDraftChanges must have DraftId and SettingKey before migration 007 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationSettingDrafts', 'DraftId') IS NULL
   OR COL_LENGTH('dbo.RegistrationSettingDrafts', 'Status') IS NULL
BEGIN
    THROW 50015, 'dbo.RegistrationSettingDrafts must have DraftId and Status before migration 007 is applied.', 1;
END;

/*
   Remove retired mutations from live shared drafts so they cannot make a
   post-migration draft fail catalog validation. Historical committed,
   discarded, and invalidated draft data is intentionally preserved.
*/
DELETE draftChange
FROM dbo.RegistrationSettingDraftChanges AS draftChange
INNER JOIN dbo.RegistrationSettingDrafts AS draft
    ON draft.DraftId = draftChange.DraftId
WHERE draftChange.SettingKey = 'header_image_url'
  AND draft.Status = 'Active';

/* Delete referencing settings before deleting the allowlist row. */
DELETE FROM dbo.RegistrationFormSettings
WHERE Setting = 'header_image_url';

DELETE FROM dbo.RegistrationFormSettingTypes
WHERE Setting = 'header_image_url';

COMMIT;
