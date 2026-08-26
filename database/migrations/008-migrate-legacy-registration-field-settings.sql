/* Migrate the remaining legacy registration-field labels and requiredness settings. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.RegistrationFormSettings', 'U') IS NULL
BEGIN
    THROW 50016, 'dbo.RegistrationFormSettings must exist before migration 008 is applied.', 1;
END;

IF OBJECT_ID('dbo.RegistrationFormSettingTypes', 'U') IS NULL
BEGIN
    THROW 50017, 'dbo.RegistrationFormSettingTypes must exist before migration 008 is applied.', 1;
END;

IF OBJECT_ID('dbo.RegistrationSettingDraftChanges', 'U') IS NULL
BEGIN
    THROW 50018, 'dbo.RegistrationSettingDraftChanges must exist before migration 008 is applied.', 1;
END;

IF OBJECT_ID('dbo.RegistrationSettingDrafts', 'U') IS NULL
BEGIN
    THROW 50019, 'dbo.RegistrationSettingDrafts must exist before migration 008 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationFormSettings', 'OrganizationID') IS NULL
   OR COL_LENGTH('dbo.RegistrationFormSettings', 'Setting') IS NULL
   OR COL_LENGTH('dbo.RegistrationFormSettings', 'FormCode') IS NULL
   OR COL_LENGTH('dbo.RegistrationFormSettings', 'Value') IS NULL
BEGIN
    THROW 50020, 'dbo.RegistrationFormSettings must have OrganizationID, Setting, FormCode, and Value before migration 008 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationFormSettingTypes', 'Setting') IS NULL
BEGIN
    THROW 50021, 'dbo.RegistrationFormSettingTypes.Setting must exist before migration 008 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationSettingDraftChanges', 'DraftId') IS NULL
   OR COL_LENGTH('dbo.RegistrationSettingDraftChanges', 'SettingKey') IS NULL
   OR COL_LENGTH('dbo.RegistrationSettingDraftChanges', 'Operation') IS NULL
   OR COL_LENGTH('dbo.RegistrationSettingDraftChanges', 'Value') IS NULL
BEGIN
    THROW 50022, 'dbo.RegistrationSettingDraftChanges must have DraftId, SettingKey, Operation, and Value before migration 008 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationSettingDrafts', 'DraftId') IS NULL
   OR COL_LENGTH('dbo.RegistrationSettingDrafts', 'Status') IS NULL
BEGIN
    THROW 50023, 'dbo.RegistrationSettingDrafts must have DraftId and Status before migration 008 is applied.', 1;
END;

DECLARE @SettingMap TABLE
(
    LegacyKey nvarchar(200) NOT NULL PRIMARY KEY,
    ReplacementKey nvarchar(200) NOT NULL UNIQUE
);

INSERT @SettingMap (LegacyKey, ReplacementKey)
VALUES
    ('legal_name_checkbox_label', 'label.UseLegalName'),
    ('ecard_checkbox_label', 'label.IsECard'),
    ('mailing_list_checkbox_label', 'label.AddToMailingList'),
    ('require_preferred_pickup_location', 'require.RequestPickupBranchID');

/* The settings table is foreign-key constrained to this allowlist. */
INSERT dbo.RegistrationFormSettingTypes (Setting)
SELECT map.ReplacementKey
FROM @SettingMap AS map
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.RegistrationFormSettingTypes AS settingType
    WHERE settingType.Setting = map.ReplacementKey
);

/* Transform only explicitly owned rows; effective inherited values are never materialized. */
INSERT dbo.RegistrationFormSettings (OrganizationID, Setting, FormCode, Value)
SELECT legacy.OrganizationID, map.ReplacementKey, legacy.FormCode, legacy.Value
FROM dbo.RegistrationFormSettings AS legacy
INNER JOIN @SettingMap AS map
    ON map.LegacyKey = legacy.Setting
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.RegistrationFormSettings AS replacement
    WHERE replacement.OrganizationID = legacy.OrganizationID
      AND replacement.FormCode = legacy.FormCode
      AND replacement.Setting = map.ReplacementKey
);

/*
   Active shared drafts must not be able to recreate a retired live key. If a
   draft already contains the replacement mutation, it wins. Otherwise update
   the mutation in place so its operation, value, and audit metadata remain
   unchanged. Historical committed, discarded, and invalidated drafts remain
   untouched.
*/
UPDATE draftChange
SET SettingKey = map.ReplacementKey
FROM dbo.RegistrationSettingDraftChanges AS draftChange
INNER JOIN dbo.RegistrationSettingDrafts AS draft
    ON draft.DraftId = draftChange.DraftId
INNER JOIN @SettingMap AS map
    ON map.LegacyKey = draftChange.SettingKey
WHERE draft.Status = 'Active'
  AND NOT EXISTS
(
    SELECT 1
    FROM dbo.RegistrationSettingDraftChanges AS replacementChange
    WHERE replacementChange.DraftId = draftChange.DraftId
      AND replacementChange.SettingKey = map.ReplacementKey
);

DELETE draftChange
FROM dbo.RegistrationSettingDraftChanges AS draftChange
INNER JOIN dbo.RegistrationSettingDrafts AS draft
    ON draft.DraftId = draftChange.DraftId
INNER JOIN @SettingMap AS map
    ON map.LegacyKey = draftChange.SettingKey
WHERE draft.Status = 'Active';

/* Delete referencing live rows before removing the retired allowlist entries. */
DELETE FROM dbo.RegistrationFormSettings
WHERE Setting IN (SELECT LegacyKey FROM @SettingMap);

DELETE FROM dbo.RegistrationFormSettingTypes
WHERE Setting IN (SELECT LegacyKey FROM @SettingMap);

COMMIT;
