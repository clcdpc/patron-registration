/* Associate uploaded registration assets with the settings scope that created them. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.RegistrationFormAssets', 'UploadOrganizationId') IS NULL
BEGIN
    ALTER TABLE dbo.RegistrationFormAssets
        ADD UploadOrganizationId int NULL;
END;

IF COL_LENGTH('dbo.RegistrationFormAssets', 'UploadFormCode') IS NULL
BEGIN
    ALTER TABLE dbo.RegistrationFormAssets
        ADD UploadFormCode nvarchar(64) NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.RegistrationFormAssets')
      AND name = 'IX_RegistrationFormAssets_UploadScope'
)
BEGIN
    CREATE INDEX IX_RegistrationFormAssets_UploadScope
        ON dbo.RegistrationFormAssets (UploadOrganizationId, UploadFormCode);
END;

COMMIT;
