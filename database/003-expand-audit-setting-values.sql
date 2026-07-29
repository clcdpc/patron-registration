/* Expand non-sensitive audit display values to match the catalog's supported value length. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.RegistrationSettingAuditEvents')
      AND name = 'PreviousValue'
      AND max_length <> -1
)
BEGIN
    ALTER TABLE dbo.RegistrationSettingAuditEvents ALTER COLUMN PreviousValue nvarchar(max) NULL;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.RegistrationSettingAuditEvents')
      AND name = 'NewValue'
      AND max_length <> -1
)
BEGIN
    ALTER TABLE dbo.RegistrationSettingAuditEvents ALTER COLUMN NewValue nvarchar(max) NULL;
END;

COMMIT;
