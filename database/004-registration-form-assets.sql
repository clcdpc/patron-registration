/* Store uploaded registration header images independently from RegistrationFormSettings. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.RegistrationFormAssets', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.RegistrationFormAssets
    (
        AssetId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_RegistrationFormAssets PRIMARY KEY,
        FileName nvarchar(255) NOT NULL,
        ContentType varchar(100) NOT NULL,
        Content varbinary(max) NOT NULL,
        ContentHash varchar(64) NOT NULL,
        CreatedDate datetime2(7) NOT NULL
            CONSTRAINT DF_RegistrationFormAssets_CreatedDate DEFAULT SYSUTCDATETIME(),
        ModifiedDate datetime2(7) NOT NULL
            CONSTRAINT DF_RegistrationFormAssets_ModifiedDate DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_RegistrationFormAssets_FileName_NotBlank CHECK (LEN(LTRIM(RTRIM(FileName))) > 0),
        CONSTRAINT CK_RegistrationFormAssets_ContentType_NotBlank CHECK (LEN(LTRIM(RTRIM(ContentType))) > 0),
        CONSTRAINT CK_RegistrationFormAssets_Content_NotEmpty CHECK (DATALENGTH(Content) > 0),
        CONSTRAINT CK_RegistrationFormAssets_ContentHash_Sha256 CHECK (LEN(ContentHash) = 64)
    );
END;

COMMIT;
