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
   Capture the active drafts that this migration will actually mutate. The
   primary key makes the later revision update one-per-draft even when a draft
   contains more than one retired mutation.
*/
IF OBJECT_ID('tempdb..#Migration007ChangedDrafts') IS NOT NULL
    DROP TABLE #Migration007ChangedDrafts;

CREATE TABLE #Migration007ChangedDrafts
(
    DraftId bigint NOT NULL PRIMARY KEY
);

INSERT #Migration007ChangedDrafts (DraftId)
SELECT DISTINCT draftChange.DraftId
FROM dbo.RegistrationSettingDraftChanges AS draftChange
INNER JOIN dbo.RegistrationSettingDrafts AS draft
    ON draft.DraftId = draftChange.DraftId
WHERE draftChange.SettingKey = 'header_image_url'
  AND draft.Status = 'Active';

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

/*
   An active-draft mutation is a new preview revision. Keep this in the same
   transaction as the mutation and only touch links for drafts captured above.
   The dynamic batch keeps Revision optional for installations that have not
   run migration 012 yet.
*/
IF OBJECT_ID('dbo.RegistrationSettingPreviewLinks', 'U') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql N'
        UPDATE previewLink
        SET RevokedAtUtc = SYSUTCDATETIME(),
            RevokedBy = COALESCE(RevokedBy, ''007-remove-legacy-header-image-url.sql''),
            ModifiedAtUtc = SYSUTCDATETIME(),
            ModifiedBy = ''007-remove-legacy-header-image-url.sql''
        FROM dbo.RegistrationSettingPreviewLinks AS previewLink
        INNER JOIN #Migration007ChangedDrafts AS changedDraft
            ON changedDraft.DraftId = previewLink.DraftId
        WHERE previewLink.RevokedAtUtc IS NULL;';
END;

IF COL_LENGTH('dbo.RegistrationSettingDrafts', 'Revision') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql N'
        UPDATE draft
        SET Revision = draft.Revision + 1
        FROM dbo.RegistrationSettingDrafts AS draft
        INNER JOIN #Migration007ChangedDrafts AS changedDraft
            ON changedDraft.DraftId = draft.DraftId
        WHERE draft.Status = ''Active'';';
END;

/* Delete referencing settings before deleting the allowlist row. */
DELETE FROM dbo.RegistrationFormSettings
WHERE Setting = 'header_image_url';

DELETE FROM dbo.RegistrationFormSettingTypes
WHERE Setting = 'header_image_url';

DROP TABLE #Migration007ChangedDrafts;

COMMIT;
