/*
  Incremental deployment for installations that already ran 001.
  Existing links cannot be assigned a trustworthy branch, so they are revoked before the column becomes required.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.RegistrationSettingPreviewLinks', 'OperationalBranchId') IS NULL
BEGIN
    UPDATE dbo.RegistrationSettingPreviewLinks
    SET RevokedAtUtc = COALESCE(RevokedAtUtc, SYSUTCDATETIME()),
        RevokedBy = COALESCE(RevokedBy, '002-preview-operational-branch.sql');

    ALTER TABLE dbo.RegistrationSettingPreviewLinks
        ADD OperationalBranchId int NULL;

    /* Revoked legacy rows use a sentinel that is always rejected by application branch validation. */
    UPDATE dbo.RegistrationSettingPreviewLinks
    SET OperationalBranchId = -2147483648
    WHERE OperationalBranchId IS NULL;

    ALTER TABLE dbo.RegistrationSettingPreviewLinks
        ALTER COLUMN OperationalBranchId int NOT NULL;
END;

COMMIT;
