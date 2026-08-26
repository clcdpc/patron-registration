/* Add the durable database lock used to coordinate asset references and orphan cleanup. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.RegistrationFormAssets', 'U') IS NULL
BEGIN
    THROW 50029, 'dbo.RegistrationFormAssets must exist before migration 011 is applied.', 1;
END;

IF OBJECT_ID('dbo.RegistrationSettingDrafts', 'U') IS NULL
BEGIN
    THROW 50030, 'dbo.RegistrationSettingDrafts must exist before migration 011 is applied.', 1;
END;

IF OBJECT_ID('dbo.RegistrationFormAssetReferenceLocks', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.RegistrationFormAssetReferenceLocks
    (
        LockId tinyint NOT NULL
            CONSTRAINT PK_RegistrationFormAssetReferenceLocks PRIMARY KEY,
        CONSTRAINT CK_RegistrationFormAssetReferenceLocks_Singleton CHECK (LockId = 1)
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM dbo.RegistrationFormAssetReferenceLocks
    WHERE LockId = 1
)
BEGIN
    INSERT dbo.RegistrationFormAssetReferenceLocks (LockId)
    VALUES (1);
END;

COMMIT;
