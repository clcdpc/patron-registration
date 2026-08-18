/* Add the bounded orphan-asset cleanup access path. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.RegistrationFormAssets', 'U') IS NULL
BEGIN
    THROW 50027, 'dbo.RegistrationFormAssets must exist before migration 010 is applied.', 1;
END;

IF COL_LENGTH('dbo.RegistrationFormAssets', 'CreatedDate') IS NULL
BEGIN
    THROW 50028, 'dbo.RegistrationFormAssets.CreatedDate must exist before migration 010 is applied.', 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.RegistrationFormAssets')
      AND name = 'IX_RegistrationFormAssets_CreatedDate'
)
BEGIN
    CREATE INDEX IX_RegistrationFormAssets_CreatedDate
        ON dbo.RegistrationFormAssets (CreatedDate);
END;

COMMIT;
