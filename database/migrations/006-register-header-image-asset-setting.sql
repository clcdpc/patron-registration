/* Register the database-backed header image setting in the existing setting allowlist. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.RegistrationFormSettingTypes', 'U') IS NULL
BEGIN
    THROW 50006, 'dbo.RegistrationFormSettingTypes must exist before migration 006 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationFormSettingTypes', 'Setting') IS NULL
BEGIN
    THROW 50007, 'dbo.RegistrationFormSettingTypes.Setting must exist before migration 006 is applied.', 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM dbo.RegistrationFormSettingTypes
    WHERE Setting = 'header_image_asset_id'
)
BEGIN
    /*
       RegistrationFormSettingTypes is an existing application table. The
       setting key is its persisted contract; any additional nullable/defaulted
       columns retain their established schema defaults by being omitted here.
    */
    INSERT dbo.RegistrationFormSettingTypes (Setting)
    VALUES ('header_image_asset_id');
END;

COMMIT;
