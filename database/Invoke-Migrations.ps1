[CmdletBinding()]
param(
    [string]$ConnectionString,
    [string]$ConnectionStringFile,
    [ValidatePattern('^[A-Za-z0-9_]+$')]
    [string]$DatabaseName = 'clcdb',
    [string]$MigrationsPath = (Join-Path $PSScriptRoot 'migrations'),
    [switch]$Baseline,
    [ValidateRange(1, 2147483647)]
    [int]$BaselineThrough = 12,
    [ValidateRange(1, 86400)]
    [int]$LockTimeoutSeconds = 600,
    [ValidateRange(1, 86400)]
    [int]$CommandTimeoutSeconds = 1800,
    [string]$AppliedBy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$historyTableName = 'dbo.PatronRegistrationMigrations'
$applicationLockResource = 'Clc.PatronRegistration.DatabaseMigrations'
$migrationFilePattern = '^(?<id>[0-9]{3,})-(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\.sql$'

function ConvertTo-Hex {
    param([byte[]]$Bytes)

    if ($null -eq $Bytes) {
        return ''
    }

    return (($Bytes | ForEach-Object { $_.ToString('x2', [Globalization.CultureInfo]::InvariantCulture) }) -join '')
}

function Test-ByteArrayEqual {
    param(
        [byte[]]$Left,
        [byte[]]$Right
    )

    if ($null -eq $Left -or $null -eq $Right -or $Left.Length -ne $Right.Length) {
        return $false
    }

    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) {
            return $false
        }
    }

    return $true
}

function Add-SqlParameter {
    param(
        [Parameter(Mandatory)]$Command,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][System.Data.SqlDbType]$Type,
        [Parameter(Mandatory)]$Value,
        [int]$Size = 0
    )

    if ($Size -gt 0) {
        $parameter = $Command.Parameters.Add($Name, $Type, $Size)
    }
    else {
        $parameter = $Command.Parameters.Add($Name, $Type)
    }

    if ($null -eq $Value) {
        $parameter.Value = [DBNull]::Value
    }
    else {
        $parameter.Value = $Value
    }
}

function Invoke-SqlNonQuery {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$CommandText,
        $Transaction,
        [object[]]$Parameters = @(),
        [int]$CommandTimeout = $CommandTimeoutSeconds
    )

    $command = $Connection.CreateCommand()
    $command.CommandText = $CommandText
    $command.CommandTimeout = $CommandTimeout
    if ($null -ne $Transaction) {
        $command.Transaction = $Transaction
    }

    try {
        foreach ($parameterDefinition in $Parameters) {
            Add-SqlParameter -Command $command `
                -Name $parameterDefinition.Name `
                -Type $parameterDefinition.Type `
                -Value $parameterDefinition.Value `
                -Size $parameterDefinition.Size
        }

        $null = $command.ExecuteNonQuery()
    }
    finally {
        $command.Dispose()
    }
}

function Invoke-SqlScalar {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$CommandText,
        $Transaction,
        [object[]]$Parameters = @(),
        [int]$CommandTimeout = $CommandTimeoutSeconds
    )

    $command = $Connection.CreateCommand()
    $command.CommandText = $CommandText
    $command.CommandTimeout = $CommandTimeout
    if ($null -ne $Transaction) {
        $command.Transaction = $Transaction
    }

    try {
        foreach ($parameterDefinition in $Parameters) {
            Add-SqlParameter -Command $command `
                -Name $parameterDefinition.Name `
                -Type $parameterDefinition.Type `
                -Value $parameterDefinition.Value `
                -Size $parameterDefinition.Size
        }

        return $command.ExecuteScalar()
    }
    finally {
        $command.Dispose()
    }
}

function Invoke-SqlRows {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$CommandText,
        $Transaction,
        [object[]]$Parameters = @(),
        [int]$CommandTimeout = $CommandTimeoutSeconds
    )

    $command = $Connection.CreateCommand()
    $command.CommandText = $CommandText
    $command.CommandTimeout = $CommandTimeout
    if ($null -ne $Transaction) {
        $command.Transaction = $Transaction
    }

    try {
        foreach ($parameterDefinition in $Parameters) {
            Add-SqlParameter -Command $command `
                -Name $parameterDefinition.Name `
                -Type $parameterDefinition.Type `
                -Value $parameterDefinition.Value `
                -Size $parameterDefinition.Size
        }

        $reader = $command.ExecuteReader()
        try {
            $rows = [System.Collections.Generic.List[object]]::new()
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($ordinal = 0; $ordinal -lt $reader.FieldCount; $ordinal++) {
                    $value = $reader.GetValue($ordinal)
                    if ($value -is [DBNull]) {
                        $value = $null
                    }
                    $row[$reader.GetName($ordinal)] = $value
                }
                $null = $rows.Add([pscustomobject]$row)
            }
            return $rows.ToArray()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $command.Dispose()
    }
}

function Get-ConnectionStringValue {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString) -and
        -not [string]::IsNullOrWhiteSpace($ConnectionStringFile)) {
        throw 'Specify only one of -ConnectionString or -ConnectionStringFile.'
    }

    if (-not [string]::IsNullOrWhiteSpace($ConnectionStringFile)) {
        if (-not [IO.File]::Exists($ConnectionStringFile)) {
            throw "Connection string file was not found: $ConnectionStringFile"
        }
        $ConnectionString = [IO.File]::ReadAllText($ConnectionStringFile).Trim()
    }
    elseif ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        $ConnectionString = $env:PATRON_REGISTRATION_SQL_CONNECTION_STRING
    }

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw 'Provide a connection string with -ConnectionString, -ConnectionStringFile, or PATRON_REGISTRATION_SQL_CONNECTION_STRING.'
    }

    return $ConnectionString
}

function New-DatabaseConnection {
    param([Parameter(Mandatory)][string]$RawConnectionString)

    try {
        Add-Type -AssemblyName System.Data.SqlClient -ErrorAction Stop
    }
    catch {
        try {
            Add-Type -AssemblyName System.Data -ErrorAction Stop
        }
        catch {
            throw 'The PowerShell SQL Server client (System.Data.SqlClient) is not available.'
        }
    }

    try {
        $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($RawConnectionString)
        $builder['Initial Catalog'] = $DatabaseName
        return [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    }
    catch {
        throw 'The supplied SQL Server connection string is invalid.'
    }
}

function Get-MigrationFiles {
    param([Parameter(Mandatory)][string]$Path)

    if (-not [IO.Directory]::Exists($Path)) {
        throw "Migration directory was not found: $Path"
    }

    $files = @(Get-ChildItem -LiteralPath $Path -File -Filter '*.sql')
    if ($files.Count -eq 0) {
        throw "No SQL migration files were found in $Path."
    }

    $malformed = @($files | Where-Object { $_.Name -notmatch $migrationFilePattern })
    if ($malformed.Count -gt 0) {
        $names = $malformed.Name -join ', '
        throw "Malformed migration filename(s) in ${Path}: $names. Expected NNN-name.sql (for example, 013-add-setting.sql)."
    }

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $migrations = foreach ($file in $files) {
            $matches = [regex]::Match($file.Name, $migrationFilePattern)
            $idText = $matches.Groups['id'].Value

            if ($idText.Length -gt 3 -and $idText.StartsWith('0', [StringComparison]::Ordinal)) {
                throw "Malformed migration filename '$($file.Name)': numeric prefix '$idText' is not canonical."
            }

            $id = 0
            if (-not [int]::TryParse($idText, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$id) -or $id -le 0) {
                throw "Malformed migration filename '$($file.Name)': migration ID must be a positive integer."
            }

            $bytes = [IO.File]::ReadAllBytes($file.FullName)
            [pscustomobject]@{
                Id = $id
                IdText = $idText
                Name = $file.Name
                Path = $file.FullName
                Bytes = $bytes
                Checksum = $sha256.ComputeHash($bytes)
                SqlText = [IO.File]::ReadAllText($file.FullName)
            }
        }
    }
    finally {
        $sha256.Dispose()
    }

    $duplicates = @($migrations | Group-Object -Property Id | Where-Object { $_.Count -gt 1 })
    if ($duplicates.Count -gt 0) {
        $details = $duplicates | ForEach-Object {
            "ID $($_.Name): $((($_.Group | Sort-Object Name).Name) -join ', ')"
        }
        throw "Duplicate migration IDs were found: $($details -join '; ')."
    }

    return @($migrations | Sort-Object -Property Id)
}

function Ensure-HistoryTable {
    param([Parameter(Mandatory)]$Connection)

    Invoke-SqlNonQuery -Connection $Connection -CommandText @"
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.PatronRegistrationMigrations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PatronRegistrationMigrations
    (
        MigrationId int NOT NULL
            CONSTRAINT PK_PatronRegistrationMigrations PRIMARY KEY CLUSTERED,
        Name nvarchar(260) NOT NULL
            CONSTRAINT UQ_PatronRegistrationMigrations_Name UNIQUE,
        [Checksum] varbinary(32) NOT NULL,
        AppliedAtUtc datetime2(7) NOT NULL
            CONSTRAINT DF_PatronRegistrationMigrations_AppliedAtUtc DEFAULT SYSUTCDATETIME(),
        AppliedBy nvarchar(256) NOT NULL,
        CONSTRAINT CK_PatronRegistrationMigrations_Checksum CHECK (DATALENGTH([Checksum]) = 32)
    );
END;
"@

    $columns = @(Invoke-SqlRows -Connection $Connection -CommandText @"
SELECT name, max_length, is_nullable
FROM sys.columns
WHERE object_id = OBJECT_ID(N'dbo.PatronRegistrationMigrations', N'U');
"@)

    $requiredColumns = @{
        MigrationId = @{ MaxLength = 4; Nullable = 0 }
        Name = @{ MaxLength = 520; Nullable = 0 }
        Checksum = @{ MaxLength = 32; Nullable = 0 }
        AppliedAtUtc = @{ MaxLength = 8; Nullable = 0 }
        AppliedBy = @{ MaxLength = 512; Nullable = 0 }
    }

    foreach ($columnName in $requiredColumns.Keys) {
        $column = $columns | Where-Object { $_.name -eq $columnName } | Select-Object -First 1
        if ($null -eq $column) {
            throw "$historyTableName exists but is missing required column '$columnName'."
        }

        $requirements = $requiredColumns[$columnName]
        if ([int]$column.max_length -ne $requirements.MaxLength -or [int]$column.is_nullable -ne $requirements.Nullable) {
            throw "$historyTableName column '$columnName' has an incompatible definition."
        }
    }
}

function Get-HistoryRows {
    param([Parameter(Mandatory)]$Connection)

    return @(Invoke-SqlRows -Connection $Connection -CommandText @"
SELECT MigrationId, Name, [Checksum], AppliedAtUtc, AppliedBy
FROM dbo.PatronRegistrationMigrations
ORDER BY MigrationId;
"@)
}

function Acquire-ApplicationLock {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds
    )

    $result = Invoke-SqlScalar -Connection $Connection -CommandText @"
DECLARE @result int;
EXEC @result = sys.sp_getapplock
    @Resource = N'Clc.PatronRegistration.DatabaseMigrations',
    @LockMode = N'Exclusive',
    @LockOwner = N'Session',
    @LockTimeout = @TimeoutMilliseconds;
SELECT @result;
"@ -Parameters @(
        @{ Name = '@TimeoutMilliseconds'; Type = [System.Data.SqlDbType]::Int; Value = $TimeoutMilliseconds; Size = 0 }
    )

    $numericResult = [int]$result
    if ($numericResult -lt 0) {
        throw "Could not acquire the migration application lock (SQL Server result $numericResult)."
    }
}

function Release-ApplicationLock {
    param([Parameter(Mandatory)]$Connection)

    $result = Invoke-SqlScalar -Connection $Connection -CommandText @"
DECLARE @result int;
EXEC @result = sys.sp_releaseapplock
    @Resource = N'Clc.PatronRegistration.DatabaseMigrations',
    @LockOwner = N'Session';
SELECT @result;
"@

    if ([int]$result -lt 0) {
        throw "Could not release the migration application lock (SQL Server result $result)."
    }
}

function Assert-HistoryIntegrity {
    param(
        [Parameter(Mandatory)][object[]]$Migrations,
        [object[]]$HistoryRows = @()
    )

    $byId = @{}
    foreach ($migration in $Migrations) {
        $byId[[int]$migration.Id] = $migration
    }

    foreach ($history in $HistoryRows) {
        $id = [int]$history.MigrationId
        if (-not $byId.ContainsKey($id)) {
            throw "Applied migration $id ('$($history.Name)') is missing from the repository migration directory. Applied migration files cannot be deleted or renamed."
        }
    }

    foreach ($migration in $Migrations) {
        $history = $HistoryRows | Where-Object { [int]$_.MigrationId -eq [int]$migration.Id } | Select-Object -First 1
        if ($null -eq $history) {
            continue
        }

        if ($history.Name -cne $migration.Name) {
            throw "Migration identity changed for ID $($migration.IdText). Repository filename '$($migration.Name)' does not match the applied filename '$($history.Name)'. Applied migration files are immutable."
        }

        $storedChecksum = ConvertTo-Hex -Bytes ([byte[]]$history.Checksum)
        $currentChecksum = ConvertTo-Hex -Bytes ([byte[]]$migration.Checksum)
        if (-not (Test-ByteArrayEqual -Left ([byte[]]$history.Checksum) -Right ([byte[]]$migration.Checksum))) {
            throw "Migration integrity check failed for ID $($migration.IdText), filename '$($migration.Name)'. Stored checksum: $storedChecksum. Current checksum: $currentChecksum. Applied migration files are immutable; create a new migration for corrections."
        }
    }
}

function Insert-HistoryRow {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)]$Migration,
        [Parameter(Mandatory)][string]$Actor
    )

    Invoke-SqlNonQuery -Connection $Connection -Transaction $Transaction -CommandText @"
INSERT dbo.PatronRegistrationMigrations (MigrationId, Name, [Checksum], AppliedBy)
VALUES (@MigrationId, @Name, @Checksum, @AppliedBy);
"@ -Parameters @(
        @{ Name = '@MigrationId'; Type = [System.Data.SqlDbType]::Int; Value = [int]$Migration.Id; Size = 0 },
        @{ Name = '@Name'; Type = [System.Data.SqlDbType]::NVarChar; Value = $Migration.Name; Size = 260 },
        @{ Name = '@Checksum'; Type = [System.Data.SqlDbType]::VarBinary; Value = [byte[]]$Migration.Checksum; Size = 32 },
        @{ Name = '@AppliedBy'; Type = [System.Data.SqlDbType]::NVarChar; Value = $Actor; Size = 256 }
    )
}

function Get-CatalogKeys {
    param([Parameter(Mandatory)]$CatalogMigration)

    $sectionMatch = [regex]::Match(
        $CatalogMigration.SqlText,
        '/\* BEGIN SETTING_CATALOG_ALLOWLIST \*/(?<body>.*?)/\* END SETTING_CATALOG_ALLOWLIST \*/',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $sectionMatch.Success) {
        throw "Could not find the SettingCatalog invariant section in $($CatalogMigration.Name)."
    }

    $keyMatches = [regex]::Matches($sectionMatch.Groups['body'].Value, "\('(?<key>[^']+)'\)")
    $keys = @($keyMatches | ForEach-Object { $_.Groups['key'].Value })
    if ($keys.Count -eq 0) {
        throw "No SettingCatalog keys were found in $($CatalogMigration.Name)."
    }

    return $keys
}

function Test-BaselineInvariants {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)]$Transaction,
        [Parameter(Mandatory)][object[]]$Migrations
    )

    $failures = [System.Collections.Generic.List[string]]::new()
    $tableExists = @{}

    function Confirm-BaselineTable {
        param([string]$TableName, [string]$MigrationId)

        $exists = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT CASE WHEN OBJECT_ID(@TableName, N'U') IS NULL THEN 0 ELSE 1 END;
"@ -Parameters @(
            @{ Name = '@TableName'; Type = [System.Data.SqlDbType]::NVarChar; Value = "dbo.$TableName"; Size = 261 }
        )) -eq 1
        $tableExists[$TableName] = $exists
        if (-not $exists) {
            $null = $failures.Add("$MigrationId requires dbo.$TableName to exist.")
        }
        return $exists
    }

    function Confirm-BaselineColumn {
        param(
            [string]$TableName,
            [string]$ColumnName,
            [string]$MigrationId,
            [Nullable[int]]$MaxLength,
            [Nullable[bool]]$Nullable
        )

        if (-not $tableExists[$TableName]) {
            return
        }

        $predicate = '1 = 1'
        if ($null -ne $MaxLength) {
            $predicate += ' AND max_length = @MaxLength'
        }
        if ($null -ne $Nullable) {
            $predicate += ' AND is_nullable = @IsNullable'
        }

        $parameters = @(
            @{ Name = '@TableName'; Type = [System.Data.SqlDbType]::NVarChar; Value = "dbo.$TableName"; Size = 261 },
            @{ Name = '@ColumnName'; Type = [System.Data.SqlDbType]::NVarChar; Value = $ColumnName; Size = 128 }
        )
        if ($null -ne $MaxLength) {
            $parameters += @{ Name = '@MaxLength'; Type = [System.Data.SqlDbType]::SmallInt; Value = [int16]$MaxLength; Size = 0 }
        }
        if ($null -ne $Nullable) {
            $parameters += @{ Name = '@IsNullable'; Type = [System.Data.SqlDbType]::Bit; Value = [bool]$Nullable; Size = 0 }
        }

        $present = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT COUNT(*)
FROM sys.columns
WHERE object_id = OBJECT_ID(@TableName, N'U')
  AND name = @ColumnName
  AND $predicate;
"@ -Parameters $parameters) -eq 1
        if (-not $present) {
            $description = "dbo.$TableName.$ColumnName"
            if ($null -ne $MaxLength) {
                $description += " with max_length $MaxLength"
            }
            if ($null -ne $Nullable) {
                $description += if ($Nullable) { ' nullable' } else { ' NOT NULL' }
            }
            $null = $failures.Add("$MigrationId requires $description.")
        }
    }

    function Confirm-BaselineIndex {
        param(
            [string]$TableName,
            [string]$IndexName,
            [string]$MigrationId,
            [bool]$Unique = $false,
            [bool]$Filtered = $false
        )

        if (-not $tableExists[$TableName]) {
            return
        }

        $present = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT COUNT(*)
FROM sys.indexes
WHERE object_id = OBJECT_ID(@TableName, N'U')
  AND name = @IndexName
  AND (@Unique = 0 OR is_unique = 1)
  AND (@Filtered = 0 OR has_filter = 1);
"@ -Parameters @(
            @{ Name = '@TableName'; Type = [System.Data.SqlDbType]::NVarChar; Value = "dbo.$TableName"; Size = 261 },
            @{ Name = '@IndexName'; Type = [System.Data.SqlDbType]::NVarChar; Value = $IndexName; Size = 128 },
            @{ Name = '@Unique'; Type = [System.Data.SqlDbType]::Bit; Value = $Unique; Size = 0 },
            @{ Name = '@Filtered'; Type = [System.Data.SqlDbType]::Bit; Value = $Filtered; Size = 0 }
        )) -eq 1
        if (-not $present) {
            $null = $failures.Add("$MigrationId requires index dbo.$TableName.$IndexName.")
        }
    }

    $requiredTables = @(
        @{ Name = 'RegistrationFormCodeMetadata'; Migration = '001' },
        @{ Name = 'RegistrationSettingScopeVersions'; Migration = '001' },
        @{ Name = 'RegistrationSettingDrafts'; Migration = '001' },
        @{ Name = 'RegistrationSettingDraftChanges'; Migration = '001' },
        @{ Name = 'RegistrationSettingPreviewLinks'; Migration = '001' },
        @{ Name = 'RegistrationSettingAuditEvents'; Migration = '001' },
        @{ Name = 'RegistrationSettingsCacheGeneration'; Migration = '001' },
        @{ Name = 'RegistrationFormAssets'; Migration = '004' },
        @{ Name = 'RegistrationFormAssetReferenceLocks'; Migration = '011' },
        @{ Name = 'RegistrationFormSettingTypes'; Migration = '006' },
        @{ Name = 'RegistrationFormSettings'; Migration = '006' }
    )
    foreach ($requiredTable in $requiredTables) {
        $null = Confirm-BaselineTable -TableName $requiredTable.Name -MigrationId $requiredTable.Migration
    }

    Confirm-BaselineColumn -TableName 'RegistrationSettingPreviewLinks' -ColumnName 'OperationalBranchId' -MigrationId '002' -Nullable $false
    Confirm-BaselineColumn -TableName 'RegistrationSettingAuditEvents' -ColumnName 'PreviousValue' -MigrationId '003' -MaxLength -1
    Confirm-BaselineColumn -TableName 'RegistrationSettingAuditEvents' -ColumnName 'NewValue' -MigrationId '003' -MaxLength -1
    Confirm-BaselineColumn -TableName 'RegistrationFormAssets' -ColumnName 'AssetId' -MigrationId '004'
    Confirm-BaselineColumn -TableName 'RegistrationFormAssets' -ColumnName 'FileName' -MigrationId '004'
    Confirm-BaselineColumn -TableName 'RegistrationFormAssets' -ColumnName 'Content' -MigrationId '004'
    Confirm-BaselineColumn -TableName 'RegistrationFormAssets' -ColumnName 'ContentHash' -MigrationId '004'
    Confirm-BaselineColumn -TableName 'RegistrationFormAssets' -ColumnName 'UploadOrganizationId' -MigrationId '005'
    Confirm-BaselineColumn -TableName 'RegistrationFormAssets' -ColumnName 'UploadFormCode' -MigrationId '005'
    Confirm-BaselineColumn -TableName 'RegistrationFormSettingTypes' -ColumnName 'Setting' -MigrationId '006'
    Confirm-BaselineColumn -TableName 'RegistrationFormSettings' -ColumnName 'Setting' -MigrationId '007'
    Confirm-BaselineColumn -TableName 'RegistrationSettingDraftChanges' -ColumnName 'DraftId' -MigrationId '007'
    Confirm-BaselineColumn -TableName 'RegistrationSettingDraftChanges' -ColumnName 'SettingKey' -MigrationId '007'
    Confirm-BaselineColumn -TableName 'RegistrationSettingDrafts' -ColumnName 'DraftId' -MigrationId '007'
    Confirm-BaselineColumn -TableName 'RegistrationSettingDrafts' -ColumnName 'Status' -MigrationId '007'
    Confirm-BaselineColumn -TableName 'RegistrationFormSettings' -ColumnName 'OrganizationID' -MigrationId '008'
    Confirm-BaselineColumn -TableName 'RegistrationFormSettings' -ColumnName 'FormCode' -MigrationId '008'
    Confirm-BaselineColumn -TableName 'RegistrationFormSettings' -ColumnName 'Value' -MigrationId '008'
    Confirm-BaselineColumn -TableName 'RegistrationSettingDraftChanges' -ColumnName 'Operation' -MigrationId '008'
    Confirm-BaselineColumn -TableName 'RegistrationSettingDraftChanges' -ColumnName 'Value' -MigrationId '008'
    Confirm-BaselineColumn -TableName 'RegistrationSettingDrafts' -ColumnName 'Revision' -MigrationId '012' -Nullable $false
    Confirm-BaselineColumn -TableName 'RegistrationSettingPreviewLinks' -ColumnName 'AllowLiveSubmission' -MigrationId '001' -Nullable $false
    Confirm-BaselineColumn -TableName 'RegistrationSettingPreviewLinks' -ColumnName 'LiveSettingsGeneration' -MigrationId '012' -Nullable $true
    Confirm-BaselineColumn -TableName 'RegistrationFormAssets' -ColumnName 'CreatedDate' -MigrationId '010'

    Confirm-BaselineIndex -TableName 'RegistrationSettingDrafts' -IndexName 'UX_RSD_ActiveScope' -MigrationId '001' -Unique $true -Filtered $true
    Confirm-BaselineIndex -TableName 'RegistrationSettingAuditEvents' -IndexName 'IX_RSAE_LibraryTime' -MigrationId '001'
    Confirm-BaselineIndex -TableName 'RegistrationSettingAuditEvents' -IndexName 'IX_RSAE_ScopeFilter' -MigrationId '001'
    Confirm-BaselineIndex -TableName 'RegistrationFormAssets' -IndexName 'IX_RegistrationFormAssets_UploadScope' -MigrationId '005'
    Confirm-BaselineIndex -TableName 'RegistrationFormAssets' -IndexName 'IX_RegistrationFormAssets_CreatedDate' -MigrationId '010'

    if ($tableExists['RegistrationSettingsCacheGeneration']) {
        $cacheGenerationRow = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT COUNT(*) FROM dbo.RegistrationSettingsCacheGeneration WHERE Id = 1 AND Generation >= 0;
"@)
        if ($cacheGenerationRow -ne 1) {
            $null = $failures.Add('001 requires dbo.RegistrationSettingsCacheGeneration to contain its singleton Id = 1 row.')
        }
    }

    if ($tableExists['RegistrationFormAssetReferenceLocks']) {
        $assetLockRow = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT COUNT(*) FROM dbo.RegistrationFormAssetReferenceLocks WHERE LockId = 1;
"@)
        if ($assetLockRow -ne 1) {
            $null = $failures.Add('011 requires dbo.RegistrationFormAssetReferenceLocks to contain its singleton LockId = 1 row.')
        }
    }

    if ($tableExists['RegistrationSettingPreviewLinks']) {
        $unboundLivePreviewCount = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT COUNT(*)
FROM dbo.RegistrationSettingPreviewLinks
WHERE AllowLiveSubmission = 1
  AND LiveSettingsGeneration IS NULL;
"@)
        if ($unboundLivePreviewCount -ne 0) {
            $null = $failures.Add('012 requires every existing live preview link to be bound to the current settings-cache generation.')
        }
    }

    $catalogMigration = $Migrations | Where-Object { [int]$_.Id -eq 9 } | Select-Object -First 1
    if ($null -eq $catalogMigration) {
        $null = $failures.Add('009 migration file is required to derive the SettingCatalog baseline invariant.')
    }
    elseif ($tableExists['RegistrationFormSettingTypes']) {
        $catalogKeys = Get-CatalogKeys -CatalogMigration $catalogMigration
        $actualKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($row in @(Invoke-SqlRows -Connection $Connection -Transaction $Transaction -CommandText 'SELECT Setting FROM dbo.RegistrationFormSettingTypes;')) {
            $null = $actualKeys.Add([string]$row.Setting)
        }
        foreach ($catalogKey in $catalogKeys) {
            if (-not $actualKeys.Contains($catalogKey)) {
                $null = $failures.Add("009 requires SettingCatalog key '$catalogKey' in dbo.RegistrationFormSettingTypes.")
            }
        }
        if (-not $actualKeys.Contains('header_image_asset_id')) {
            $null = $failures.Add("006 requires SettingCatalog key 'header_image_asset_id'.")
        }
    }

    $legacyKeys = @(
        'header_image_url',
        'legal_name_checkbox_label',
        'ecard_checkbox_label',
        'mailing_list_checkbox_label',
        'require_preferred_pickup_location'
    )
    $legacySqlValues = ($legacyKeys | ForEach-Object { "N'$_'" }) -join ', '
    if ($tableExists['RegistrationFormSettingTypes']) {
        $legacyTypeCount = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText "SELECT COUNT(*) FROM dbo.RegistrationFormSettingTypes WHERE Setting IN ($legacySqlValues);")
        if ($legacyTypeCount -ne 0) {
            $null = $failures.Add('007-008 require retired setting-type rows to be absent from dbo.RegistrationFormSettingTypes.')
        }
    }
    if ($tableExists['RegistrationFormSettings']) {
        $legacySettingCount = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText "SELECT COUNT(*) FROM dbo.RegistrationFormSettings WHERE Setting IN ($legacySqlValues);")
        if ($legacySettingCount -ne 0) {
            $null = $failures.Add('007-008 require retired setting rows to be absent from dbo.RegistrationFormSettings.')
        }
    }
    if ($tableExists['RegistrationSettingDraftChanges'] -and $tableExists['RegistrationSettingDrafts']) {
        $legacyDraftCount = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText "
SELECT COUNT(*)
FROM dbo.RegistrationSettingDraftChanges AS c
INNER JOIN dbo.RegistrationSettingDrafts AS d ON d.DraftId = c.DraftId
WHERE d.Status = 'Active' AND c.SettingKey IN ($legacySqlValues);")
        if ($legacyDraftCount -ne 0) {
            $null = $failures.Add('007-008 require retired setting mutations to be absent from active drafts.')
        }
    }

    return $failures.ToArray()
}

function Invoke-Baseline {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][object[]]$Migrations,
        [object[]]$HistoryRows = @(),
        [Parameter(Mandatory)]$Actor
    )

    if ($BaselineThrough -ne 12) {
        throw 'Baseline adoption is defined only for the existing 001 through 012 migration set.'
    }

    if ($HistoryRows.Count -gt 0) {
        throw "Baseline refused because $historyTableName already contains $($HistoryRows.Count) row(s). Use normal migration execution to complete a partially recorded history instead."
    }

    $baselineMigrations = @($Migrations | Where-Object { [int]$_.Id -le $BaselineThrough } | Sort-Object Id)
    $expectedIds = 1..$BaselineThrough
    $actualIds = @($baselineMigrations | ForEach-Object { [int]$_.Id })
    $missingIds = @($expectedIds | Where-Object { $_ -notin $actualIds })
    if ($missingIds.Count -gt 0) {
        throw "Baseline refused because migration file(s) are missing for ID(s): $($missingIds -join ', ')."
    }

    Write-Output "Baselining migrations 001 through $('{0:D3}' -f $BaselineThrough) after validating the existing schema and data..."

    $transaction = $Connection.BeginTransaction([System.Data.IsolationLevel]::Serializable)
    try {
        $failures = @(Test-BaselineInvariants -Connection $Connection -Transaction $transaction -Migrations $Migrations)
        if ($failures.Count -gt 0) {
            $details = $failures | ForEach-Object { " - $_" }
            throw "Baseline refused. Required invariants are missing or incompatible:`n$($details -join "`n")"
        }

        foreach ($migration in $baselineMigrations) {
            Insert-HistoryRow -Connection $Connection -Transaction $transaction -Migration $migration -Actor $Actor
            Write-Output "$($migration.IdText) baselined"
        }

        $transaction.Commit()
    }
    catch {
        try { $transaction.Rollback() } catch { }
        throw
    }
    finally {
        $transaction.Dispose()
    }
}

function Invoke-Migrations {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][object[]]$Migrations,
        [Parameter(Mandatory)][string]$Actor
    )

    Ensure-HistoryTable -Connection $Connection
    $historyRows = @(Get-HistoryRows -Connection $Connection)
    Assert-HistoryIntegrity -Migrations $Migrations -HistoryRows $historyRows

    if ($Baseline) {
        Invoke-Baseline -Connection $Connection -Migrations $Migrations -HistoryRows $historyRows -Actor $Actor
        Write-Output "Database current at migration $('{0:D3}' -f $BaselineThrough)"
        return
    }

    foreach ($migration in $Migrations) {
        $history = $historyRows | Where-Object { [int]$_.MigrationId -eq [int]$migration.Id } | Select-Object -First 1
        if ($null -ne $history) {
            Write-Output "$($migration.IdText) already applied"
            continue
        }

        Write-Output "$($migration.IdText) applying..."
        $transaction = $null
        try {
            $transaction = $Connection.BeginTransaction([System.Data.IsolationLevel]::ReadCommitted)
            Invoke-SqlNonQuery -Connection $Connection -Transaction $transaction -CommandText 'SET XACT_ABORT ON;'
            Invoke-SqlNonQuery -Connection $Connection -Transaction $transaction -CommandText $migration.SqlText

            $transactionState = [int](Invoke-SqlScalar -Connection $Connection -Transaction $transaction -CommandText 'SELECT CASE WHEN @@TRANCOUNT = 1 AND XACT_STATE() = 1 THEN 1 ELSE 0 END;')
            if ($transactionState -ne 1) {
                throw "Migration $($migration.IdText) changed the runner-owned outer transaction. Existing migration transaction wrappers must be nested BEGIN/COMMIT pairs; a migration must not commit or roll back the runner transaction."
            }

            Insert-HistoryRow -Connection $Connection -Transaction $transaction -Migration $migration -Actor $Actor
            $transaction.Commit()
            Write-Output "$($migration.IdText) applied"
        }
        catch {
            if ($null -ne $transaction) {
                try { $transaction.Rollback() } catch { }
            }
            throw "Migration $($migration.IdText) '$($migration.Name)' failed: $($_.Exception.Message)"
        }
        finally {
            if ($null -ne $transaction) {
                $transaction.Dispose()
            }
        }
    }

    $latest = $Migrations | Sort-Object Id | Select-Object -Last 1
    Write-Output "Database current at migration $($latest.IdText)"
}

$connection = $null
$lockAcquired = $false
$exitCode = 0

try {
    $resolvedMigrationsPath = [IO.Path]::GetFullPath($MigrationsPath)
    $migrations = Get-MigrationFiles -Path $resolvedMigrationsPath
    $rawConnectionString = Get-ConnectionStringValue
    if ([string]::IsNullOrWhiteSpace($AppliedBy)) {
        $AppliedBy = [Environment]::UserName
    }
    if ([string]::IsNullOrWhiteSpace($AppliedBy)) {
        $AppliedBy = 'migration-runner'
    }
    if ($AppliedBy.Length -gt 256) {
        throw '-AppliedBy must be 256 characters or fewer.'
    }

    $connection = New-DatabaseConnection -RawConnectionString $rawConnectionString
    $connection.Open()
    Acquire-ApplicationLock -Connection $connection -TimeoutMilliseconds ($LockTimeoutSeconds * 1000)
    $lockAcquired = $true
    Invoke-Migrations -Connection $connection -Migrations $migrations -Actor $AppliedBy
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    $exitCode = 1
}
finally {
    if ($lockAcquired -and $null -ne $connection -and $connection.State -eq [System.Data.ConnectionState]::Open) {
        try {
            Release-ApplicationLock -Connection $connection
        }
        catch {
            [Console]::Error.WriteLine("Warning: migration application lock release failed; closing the connection will release the session lock. $($_.Exception.Message)")
            $exitCode = 1
        }
    }

    if ($null -ne $connection) {
        $connection.Dispose()
    }
}

exit $exitCode
