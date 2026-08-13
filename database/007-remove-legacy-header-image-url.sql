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

/* Delete referencing settings before deleting the allowlist row. */
DELETE FROM dbo.RegistrationFormSettings
WHERE Setting = 'header_image_url';

DELETE FROM dbo.RegistrationFormSettingTypes
WHERE Setting = 'header_image_url';

COMMIT;
