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

    $orderedMigrations = @($migrations | Sort-Object -Property Id)
    $expectedId = 1
    foreach ($migration in $orderedMigrations) {
        if ([int]$migration.Id -ne $expectedId) {
            $expectedIdText = '{0:D3}' -f $expectedId
            throw "Migration IDs must form a contiguous sequence starting at 001. Missing migration ID $expectedIdText before '$($migration.Name)'."
        }

        $expectedId++
    }

    return $orderedMigrations
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

function Assert-MigrationChronology {
    param(
        [Parameter(Mandatory)][object[]]$Migrations,
        [object[]]$HistoryRows = @()
    )

    if ($HistoryRows.Count -eq 0) {
        return
    }

    $highestAppliedId = ($HistoryRows | ForEach-Object { [int]$_.MigrationId } | Measure-Object -Maximum).Maximum
    $highestAppliedIdText = '{0:D3}' -f [int]$highestAppliedId
    foreach ($migration in $Migrations) {
        $isApplied = @($HistoryRows | Where-Object { [int]$_.MigrationId -eq [int]$migration.Id }).Count -gt 0
        if (-not $isApplied -and [int]$migration.Id -lt [int]$highestAppliedId) {
            throw "Migration $($migration.IdText) '$($migration.Name)' is pending below already-applied migration $highestAppliedIdText. Migrations must be applied in chronological order; restore the missing migration or add a new migration after the current history."
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
            [Parameter(Mandatory)][string]$SqlType,
            [Parameter(Mandatory)][int]$MaxLength,
            [Parameter(Mandatory)][bool]$Nullable,
            [int]$Precision = -1,
            [int]$Scale = -1,
            [bool]$Identity = $false
        )

        if (-not $tableExists.ContainsKey($TableName) -or -not $tableExists[$TableName]) {
            return
        }

        $column = @(Invoke-SqlRows -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT TOP (1)
    TYPE_NAME(c.user_type_id) AS SqlType,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    c.is_computed,
    CONVERT(decimal(38, 10), ic.seed_value) AS IdentitySeed,
    CONVERT(decimal(38, 10), ic.increment_value) AS IdentityIncrement
FROM sys.columns AS c
LEFT JOIN sys.identity_columns AS ic
    ON ic.object_id = c.object_id
   AND ic.column_id = c.column_id
WHERE c.object_id = OBJECT_ID(@TableName, N'U')
  AND c.name = @ColumnName;
"@ -Parameters @(
            @{ Name = '@TableName'; Type = [System.Data.SqlDbType]::NVarChar; Value = "dbo.$TableName"; Size = 261 },
            @{ Name = '@ColumnName'; Type = [System.Data.SqlDbType]::NVarChar; Value = $ColumnName; Size = 128 }
        )) | Select-Object -First 1

        $compatible = $null -ne $column -and
            ([string]$column.SqlType -ieq $SqlType) -and
            ([int]$column.max_length -eq $MaxLength) -and
            ([bool]$column.is_nullable -eq $Nullable) -and
            ([bool]$column.is_computed -eq $false) -and
            ([bool]$column.is_identity -eq $Identity)

        if ($compatible -and $Precision -ge 0) {
            $compatible = [int]$column.precision -eq $Precision
        }
        if ($compatible -and $Scale -ge 0) {
            $compatible = [int]$column.scale -eq $Scale
        }
        if ($compatible -and $Identity) {
            $compatible = [decimal]$column.IdentitySeed -eq 1 -and [decimal]$column.IdentityIncrement -eq 1
        }

        if (-not $compatible) {
            $nullability = if ($Nullable) { 'nullable' } else { 'NOT NULL' }
            $description = "dbo.$TableName.$ColumnName ($SqlType, max_length $MaxLength, $nullability"
            if ($Identity) {
                $description += ', IDENTITY(1,1)'
            }
            $description += ')'
            $null = $failures.Add("$MigrationId requires $description.")
        }
    }

    function Remove-OuterSqlParentheses {
        param([Parameter(Mandatory)][string]$Expression)

        $value = $Expression.Trim()
        while ($value.Length -ge 2 -and $value[0] -eq [char]40 -and $value[$value.Length - 1] -eq [char]41) {
            $depth = 0
            $insideString = $false
            $outerClose = -1
            for ($index = 0; $index -lt $value.Length; $index++) {
                $character = $value[$index]
                if ($insideString) {
                    if ($character -eq [char]39) {
                        if ($index + 1 -lt $value.Length -and $value[$index + 1] -eq [char]39) {
                            $index++
                        }
                        else {
                            $insideString = $false
                        }
                    }
                    continue
                }

                if ($character -eq [char]39) {
                    $insideString = $true
                }
                elseif ($character -eq [char]40) {
                    $depth++
                }
                elseif ($character -eq [char]41) {
                    $depth--
                    if ($depth -eq 0) {
                        $outerClose = $index
                        break
                    }
                }
            }

            if ($outerClose -ne $value.Length - 1) {
                break
            }

            $value = $value.Substring(1, $value.Length - 2).Trim()
        }

        return $value
    }

    function Normalize-SqlExpression {
        param([AllowNull()][string]$Expression)

        if ($null -eq $Expression) {
            return ''
        }

        $value = $Expression.Trim().ToLowerInvariant()
        $value = $value -replace '\[', ''
        $value = $value -replace '\]', ''

        do {
            $before = $value
            $value = Remove-OuterSqlParentheses -Expression $value
            $value = [regex]::Replace($value, '\(([+-]?\d+(?:\.\d+)?)\)', '$1')
        } while ($value -ne $before)

        foreach ($operator in @('or', 'and')) {
            $parts = @(Split-TopLevelSqlOperator -Expression $value -Operator $operator)
            if ($parts.Count -gt 1) {
                $normalizedParts = @($parts | ForEach-Object {
                    Normalize-SqlExpression -Expression $_
                } | Sort-Object)
                return ('{0}({1})' -f $operator, ($normalizedParts -join ','))
            }
        }

        return ($value -replace '\s+', '')
    }

    function Split-TopLevelSqlOperator {
        param(
            [Parameter(Mandatory)][string]$Expression,
            [Parameter(Mandatory)][ValidateSet('and', 'or')][string]$Operator
        )

        $parts = [System.Collections.Generic.List[string]]::new()
        $depth = 0
        $insideString = $false
        $start = 0
        for ($index = 0; $index -lt $Expression.Length; $index++) {
            $character = $Expression[$index]
            if ($insideString) {
                if ($character -eq [char]39) {
                    if ($index + 1 -lt $Expression.Length -and $Expression[$index + 1] -eq [char]39) {
                        $index++
                    }
                    else {
                        $insideString = $false
                    }
                }
                continue
            }

            if ($character -eq [char]39) {
                $insideString = $true
                continue
            }
            if ($character -eq [char]40) {
                $depth++
                continue
            }
            if ($character -eq [char]41) {
                $depth--
                continue
            }

            if ($depth -ne 0 -or $index + $Operator.Length -gt $Expression.Length -or
                -not $Expression.Substring($index, $Operator.Length).Equals($Operator, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $before = if ($index -eq 0) { $null } else { $Expression[$index - 1] }
            $afterIndex = $index + $Operator.Length
            $after = if ($afterIndex -ge $Expression.Length) { $null } else { $Expression[$afterIndex] }
            $beforeIsIdentifier = $null -ne $before -and ([char]::IsLetterOrDigit($before) -or $before -eq [char]95)
            $afterIsIdentifier = $null -ne $after -and ([char]::IsLetterOrDigit($after) -or $after -eq [char]95)
            if ($beforeIsIdentifier -or $afterIsIdentifier) {
                continue
            }

            $null = $parts.Add($Expression.Substring($start, $index - $start))
            $start = $afterIndex
            $index = $afterIndex - 1
        }

        if ($parts.Count -eq 0) {
            return @($Expression)
        }

        $null = $parts.Add($Expression.Substring($start))
        return $parts.ToArray()
    }

    function Test-ColumnSequence {
        param(
            [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$ActualRows,
            [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ExpectedColumns
        )

        if ($ActualRows.Count -ne $ExpectedColumns.Count) {
            return $false
        }

        for ($index = 0; $index -lt $ExpectedColumns.Count; $index++) {
            if (-not ([string]$ActualRows[$index].ColumnName -ieq $ExpectedColumns[$index])) {
                return $false
            }
        }

        return $true
    }

    function Confirm-BaselineKeyConstraint {
        param(
            [string]$TableName,
            [string]$MigrationId,
            [ValidateSet('PK', 'UQ')][string]$ConstraintType,
            [Parameter(Mandatory)][string[]]$KeyColumns,
            [string]$ExpectedIndexType = ''
        )

        if (-not $tableExists.ContainsKey($TableName) -or -not $tableExists[$TableName]) {
            return
        }

        $rows = @(Invoke-SqlRows -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT
    kc.name AS ConstraintName,
    kc.type AS ConstraintType,
    i.type_desc AS IndexType,
    i.is_disabled,
    ic.key_ordinal,
    c.name AS ColumnName
FROM sys.key_constraints AS kc
INNER JOIN sys.indexes AS i
    ON i.object_id = kc.parent_object_id
   AND i.index_id = kc.unique_index_id
INNER JOIN sys.index_columns AS ic
    ON ic.object_id = i.object_id
   AND ic.index_id = i.index_id
   AND ic.is_included_column = 0
INNER JOIN sys.columns AS c
    ON c.object_id = ic.object_id
   AND c.column_id = ic.column_id
WHERE kc.parent_object_id = OBJECT_ID(@TableName, N'U')
ORDER BY kc.name, ic.key_ordinal;
"@ -Parameters @(
            @{ Name = '@TableName'; Type = [System.Data.SqlDbType]::NVarChar; Value = "dbo.$TableName"; Size = 261 }
        ))

        $matched = $false
        foreach ($group in @($rows | Group-Object -Property ConstraintName)) {
            $constraintRows = @($group.Group | Sort-Object { [int]$_.key_ordinal })
            $first = $constraintRows | Select-Object -First 1
            if ($null -ne $first -and
                [string]$first.ConstraintType -eq $ConstraintType -and
                [int]$first.is_disabled -eq 0 -and
                ([string]::IsNullOrWhiteSpace($ExpectedIndexType) -or [string]$first.IndexType -eq $ExpectedIndexType) -and
                (Test-ColumnSequence -ActualRows $constraintRows -ExpectedColumns $KeyColumns)) {
                $matched = $true
                break
            }
        }

        if (-not $matched) {
            $kind = if ($ConstraintType -eq 'PK') { 'primary key' } else { 'unique constraint' }
            $null = $failures.Add("$MigrationId requires the $kind on dbo.$TableName ($($KeyColumns -join ', ')).")
        }
    }

    function Confirm-BaselineForeignKey {
        param(
            [string]$TableName,
            [string]$MigrationId,
            [string]$ReferencedTableName,
            [Parameter(Mandatory)][string[]]$ParentColumns,
            [Parameter(Mandatory)][string[]]$ReferencedColumns,
            [ValidateSet('NO_ACTION', 'CASCADE', 'SET_NULL', 'SET_DEFAULT')][string]$DeleteAction = 'NO_ACTION',
            [ValidateSet('NO_ACTION', 'CASCADE', 'SET_NULL', 'SET_DEFAULT')][string]$UpdateAction = 'NO_ACTION'
        )

        if (-not $tableExists.ContainsKey($TableName) -or -not $tableExists[$TableName]) {
            return
        }

        $rows = @(Invoke-SqlRows -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT
    fk.name AS ConstraintName,
    OBJECT_NAME(fk.referenced_object_id) AS ReferencedTableName,
    fk.delete_referential_action_desc AS DeleteAction,
    fk.update_referential_action_desc AS UpdateAction,
    fk.is_disabled,
    fk.is_not_trusted,
    fkc.constraint_column_id,
    pc.name AS ParentColumnName,
    rc.name AS ReferencedColumnName
FROM sys.foreign_keys AS fk
INNER JOIN sys.foreign_key_columns AS fkc
    ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.columns AS pc
    ON pc.object_id = fkc.parent_object_id
   AND pc.column_id = fkc.parent_column_id
INNER JOIN sys.columns AS rc
    ON rc.object_id = fkc.referenced_object_id
   AND rc.column_id = fkc.referenced_column_id
WHERE fk.parent_object_id = OBJECT_ID(@TableName, N'U')
ORDER BY fk.name, fkc.constraint_column_id;
"@ -Parameters @(
            @{ Name = '@TableName'; Type = [System.Data.SqlDbType]::NVarChar; Value = "dbo.$TableName"; Size = 261 }
        ))

        $matched = $false
        foreach ($group in @($rows | Group-Object -Property ConstraintName)) {
            $foreignKeyRows = @($group.Group | Sort-Object { [int]$_.constraint_column_id })
            $first = $foreignKeyRows | Select-Object -First 1
            $parentRows = @($foreignKeyRows | ForEach-Object {
                [pscustomobject]@{ ColumnName = $_.ParentColumnName }
            })
            $referencedRows = @($foreignKeyRows | ForEach-Object {
                [pscustomobject]@{ ColumnName = $_.ReferencedColumnName }
            })
            if ($null -ne $first -and
                [string]$first.ReferencedTableName -ieq $ReferencedTableName -and
                [string]$first.DeleteAction -eq $DeleteAction -and
                [string]$first.UpdateAction -eq $UpdateAction -and
                [int]$first.is_disabled -eq 0 -and
                [int]$first.is_not_trusted -eq 0 -and
                (Test-ColumnSequence -ActualRows $parentRows -ExpectedColumns $ParentColumns) -and
                (Test-ColumnSequence -ActualRows $referencedRows -ExpectedColumns $ReferencedColumns)) {
                $matched = $true
                break
            }
        }

        if (-not $matched) {
            $null = $failures.Add("$MigrationId requires a trusted foreign key from dbo.$TableName ($($ParentColumns -join ', ')) to dbo.$ReferencedTableName ($($ReferencedColumns -join ', ')) with ON DELETE $DeleteAction.")
        }
    }

    function Confirm-BaselineCheckConstraint {
        param(
            [string]$TableName,
            [string]$MigrationId,
            [Parameter(Mandatory)][string[]]$ExpectedDefinitions
        )

        if (-not $tableExists.ContainsKey($TableName) -or -not $tableExists[$TableName]) {
            return
        }

        $expected = @($ExpectedDefinitions | ForEach-Object { Normalize-SqlExpression -Expression $_ })
        $checks = @(Invoke-SqlRows -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT name AS ConstraintName, definition, is_disabled, is_not_trusted
FROM sys.check_constraints
WHERE parent_object_id = OBJECT_ID(@TableName, N'U');
"@ -Parameters @(
            @{ Name = '@TableName'; Type = [System.Data.SqlDbType]::NVarChar; Value = "dbo.$TableName"; Size = 261 }
        ))

        $matched = $false
        foreach ($check in $checks) {
            $normalized = Normalize-SqlExpression -Expression ([string]$check.definition)
            if ([int]$check.is_disabled -eq 0 -and [int]$check.is_not_trusted -eq 0 -and $normalized -in $expected) {
                $matched = $true
                break
            }
        }

        if (-not $matched) {
            $null = $failures.Add("$MigrationId requires a trusted check constraint on dbo.$TableName matching the expected rule.")
        }
    }

    function Confirm-BaselineDefault {
        param(
            [string]$TableName,
            [string]$ColumnName,
            [string]$MigrationId,
            [Parameter(Mandatory)][string[]]$ExpectedDefinitions
        )

        if (-not $tableExists.ContainsKey($TableName) -or -not $tableExists[$TableName]) {
            return
        }

        $expected = @($ExpectedDefinitions | ForEach-Object { Normalize-SqlExpression -Expression $_ })
        $defaults = @(Invoke-SqlRows -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT dc.definition
FROM sys.default_constraints AS dc
INNER JOIN sys.columns AS c
    ON c.object_id = dc.parent_object_id
   AND c.column_id = dc.parent_column_id
WHERE dc.parent_object_id = OBJECT_ID(@TableName, N'U')
  AND c.name = @ColumnName;
"@ -Parameters @(
            @{ Name = '@TableName'; Type = [System.Data.SqlDbType]::NVarChar; Value = "dbo.$TableName"; Size = 261 },
            @{ Name = '@ColumnName'; Type = [System.Data.SqlDbType]::NVarChar; Value = $ColumnName; Size = 128 }
        ))

        $matched = $false
        foreach ($default in $defaults) {
            if ((Normalize-SqlExpression -Expression ([string]$default.definition)) -in $expected) {
                $matched = $true
                break
            }
        }

        if (-not $matched) {
            $null = $failures.Add("$MigrationId requires a default for dbo.$TableName.$ColumnName matching the expected behavior.")
        }
    }

    function Confirm-BaselineIndex {
        param(
            [string]$TableName,
            [string]$IndexName,
            [string]$MigrationId,
            [Parameter(Mandatory)][string[]]$KeyColumns,
            [Parameter(Mandatory)][bool[]]$DescendingKeys,
            [string[]]$IncludedColumns = @(),
            [bool]$Unique = $false,
            [AllowNull()][object]$FilterDefinition = $null
        )

        if (-not $tableExists.ContainsKey($TableName) -or -not $tableExists[$TableName]) {
            return
        }

        $rows = @(Invoke-SqlRows -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT
    i.is_unique,
    i.has_filter,
    i.filter_definition,
    i.type_desc,
    i.is_disabled,
    i.is_hypothetical,
    i.is_primary_key,
    i.is_unique_constraint,
    ic.key_ordinal,
    ic.index_column_id,
    ic.is_included_column,
    ic.is_descending_key,
    c.name AS ColumnName
FROM sys.indexes AS i
LEFT JOIN sys.index_columns AS ic
    ON ic.object_id = i.object_id
   AND ic.index_id = i.index_id
LEFT JOIN sys.columns AS c
    ON c.object_id = ic.object_id
   AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(@TableName, N'U')
  AND i.name = @IndexName
ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id;
"@ -Parameters @(
            @{ Name = '@TableName'; Type = [System.Data.SqlDbType]::NVarChar; Value = "dbo.$TableName"; Size = 261 },
            @{ Name = '@IndexName'; Type = [System.Data.SqlDbType]::NVarChar; Value = $IndexName; Size = 128 }
        ))

        $index = $rows | Select-Object -First 1
        $matched = $null -ne $index -and
            [int]$index.is_unique -eq [int]$Unique -and
            [int]$index.has_filter -eq ([int]($null -ne $FilterDefinition)) -and
            [string]$index.type_desc -eq 'NONCLUSTERED' -and
            [int]$index.is_disabled -eq 0 -and
            [int]$index.is_hypothetical -eq 0 -and
            [int]$index.is_primary_key -eq 0 -and
            [int]$index.is_unique_constraint -eq 0

        if ($matched -and $null -ne $FilterDefinition) {
            $matched = (Normalize-SqlExpression -Expression ([string]$index.filter_definition)) -eq
                (Normalize-SqlExpression -Expression $FilterDefinition)
        }

        $keyRows = @($rows | Where-Object { [int]$_.is_included_column -eq 0 } | Sort-Object { [int]$_.key_ordinal })
        $includeRows = @($rows | Where-Object { [int]$_.is_included_column -eq 1 } | Sort-Object { [int]$_.index_column_id })
        if ($matched -and -not (Test-ColumnSequence -ActualRows $keyRows -ExpectedColumns $KeyColumns)) {
            $matched = $false
        }
        if ($matched -and $DescendingKeys.Count -ne $KeyColumns.Count) {
            $matched = $false
        }
        if ($matched) {
            for ($index = 0; $index -lt $DescendingKeys.Count; $index++) {
                if ([bool]$keyRows[$index].is_descending_key -ne $DescendingKeys[$index]) {
                    $matched = $false
                    break
                }
            }
        }
        if ($matched -and -not (Test-ColumnSequence -ActualRows $includeRows -ExpectedColumns $IncludedColumns)) {
            $matched = $false
        }

        if (-not $matched) {
            $null = $failures.Add("$MigrationId requires exact index dbo.$TableName.$IndexName key/include/filter definitions.")
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

    $requiredColumns = @(
        @{ TableName = 'RegistrationFormCodeMetadata'; ColumnName = 'OrganizationId'; MigrationId = '001'; SqlType = 'int'; MaxLength = 4; Nullable = $false },
        @{ TableName = 'RegistrationFormCodeMetadata'; ColumnName = 'FormCode'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 128; Nullable = $false },
        @{ TableName = 'RegistrationFormCodeMetadata'; ColumnName = 'DisplayName'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 400; Nullable = $false },
        @{ TableName = 'RegistrationFormCodeMetadata'; ColumnName = 'Description'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 4000; Nullable = $true },
        @{ TableName = 'RegistrationFormCodeMetadata'; ColumnName = 'CreatedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },
        @{ TableName = 'RegistrationFormCodeMetadata'; ColumnName = 'CreatedBy'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $false },
        @{ TableName = 'RegistrationFormCodeMetadata'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },
        @{ TableName = 'RegistrationFormCodeMetadata'; ColumnName = 'ModifiedBy'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $false },

        @{ TableName = 'RegistrationSettingScopeVersions'; ColumnName = 'OrganizationId'; MigrationId = '001'; SqlType = 'int'; MaxLength = 4; Nullable = $false },
        @{ TableName = 'RegistrationSettingScopeVersions'; ColumnName = 'FormCode'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 128; Nullable = $false },
        @{ TableName = 'RegistrationSettingScopeVersions'; ColumnName = 'Version'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $false },
        @{ TableName = 'RegistrationSettingScopeVersions'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },

        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'DraftId'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $false; Identity = $true },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'OrganizationId'; MigrationId = '001'; SqlType = 'int'; MaxLength = 4; Nullable = $false },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'FormCode'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 128; Nullable = $false },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'BaselineVersion'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $false },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'Revision'; MigrationId = '012'; SqlType = 'bigint'; MaxLength = 8; Nullable = $false },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'Status'; MigrationId = '001'; SqlType = 'varchar'; MaxLength = 16; Nullable = $false },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'CreatedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'CreatedBy'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $false },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'ModifiedBy'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $false },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'CommittedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $true; Scale = 7 },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'CommittedBy'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $true },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'DiscardedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $true; Scale = 7 },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'DiscardedBy'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $true },

        @{ TableName = 'RegistrationSettingDraftChanges'; ColumnName = 'DraftChangeId'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $false; Identity = $true },
        @{ TableName = 'RegistrationSettingDraftChanges'; ColumnName = 'DraftId'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $false },
        @{ TableName = 'RegistrationSettingDraftChanges'; ColumnName = 'SettingKey'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 400; Nullable = $false },
        @{ TableName = 'RegistrationSettingDraftChanges'; ColumnName = 'Operation'; MigrationId = '001'; SqlType = 'varchar'; MaxLength = 20; Nullable = $false },
        @{ TableName = 'RegistrationSettingDraftChanges'; ColumnName = 'Value'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = -1; Nullable = $true },
        @{ TableName = 'RegistrationSettingDraftChanges'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },
        @{ TableName = 'RegistrationSettingDraftChanges'; ColumnName = 'ModifiedBy'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $false },

        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'PreviewLinkId'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $false; Identity = $true },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'DraftId'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $false },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'TokenHash'; MigrationId = '001'; SqlType = 'binary'; MaxLength = 32; Nullable = $false },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'OperationalBranchId'; MigrationId = '002'; SqlType = 'int'; MaxLength = 4; Nullable = $false },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'AllowLiveSubmission'; MigrationId = '001'; SqlType = 'bit'; MaxLength = 1; Nullable = $false },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'LiveSettingsGeneration'; MigrationId = '012'; SqlType = 'bigint'; MaxLength = 8; Nullable = $true },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'CreatedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'CreatedBy'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $false },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'ModifiedBy'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $false },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'RevokedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $true; Scale = 7 },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'RevokedBy'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $true },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'ExpiresAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $true; Scale = 7 },

        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'AuditEventId'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $false; Identity = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'TimestampUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'EventType'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 160; Nullable = $false },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'ActorId'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 256; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'ActorName'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 512; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'ActorOrganizationId'; MigrationId = '001'; SqlType = 'int'; MaxLength = 4; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'TargetOrganizationId'; MigrationId = '001'; SqlType = 'int'; MaxLength = 4; Nullable = $false },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'TargetLibraryId'; MigrationId = '001'; SqlType = 'int'; MaxLength = 4; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'FormCode'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 128; Nullable = $false },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'SettingKey'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 400; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'PreviousValue'; MigrationId = '003'; SqlType = 'nvarchar'; MaxLength = -1; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'NewValue'; MigrationId = '003'; SqlType = 'nvarchar'; MaxLength = -1; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'IsSensitive'; MigrationId = '001'; SqlType = 'bit'; MaxLength = 1; Nullable = $false },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'DraftId'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'PreviewLinkId'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'CorrelationId'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 256; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'IpAddress'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 128; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'Succeeded'; MigrationId = '001'; SqlType = 'bit'; MaxLength = 1; Nullable = $false },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'FailureReason'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = 2000; Nullable = $true },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'MetadataJson'; MigrationId = '001'; SqlType = 'nvarchar'; MaxLength = -1; Nullable = $true },

        @{ TableName = 'RegistrationSettingsCacheGeneration'; ColumnName = 'Id'; MigrationId = '001'; SqlType = 'tinyint'; MaxLength = 1; Nullable = $false },
        @{ TableName = 'RegistrationSettingsCacheGeneration'; ColumnName = 'Generation'; MigrationId = '001'; SqlType = 'bigint'; MaxLength = 8; Nullable = $false },
        @{ TableName = 'RegistrationSettingsCacheGeneration'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },

        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'AssetId'; MigrationId = '004'; SqlType = 'int'; MaxLength = 4; Nullable = $false; Identity = $true },
        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'FileName'; MigrationId = '004'; SqlType = 'nvarchar'; MaxLength = 510; Nullable = $false },
        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'ContentType'; MigrationId = '004'; SqlType = 'varchar'; MaxLength = 100; Nullable = $false },
        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'Content'; MigrationId = '004'; SqlType = 'varbinary'; MaxLength = -1; Nullable = $false },
        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'ContentHash'; MigrationId = '004'; SqlType = 'varchar'; MaxLength = 64; Nullable = $false },
        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'CreatedDate'; MigrationId = '004'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },
        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'ModifiedDate'; MigrationId = '004'; SqlType = 'datetime2'; MaxLength = 8; Nullable = $false; Scale = 7 },
        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'UploadOrganizationId'; MigrationId = '005'; SqlType = 'int'; MaxLength = 4; Nullable = $true },
        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'UploadFormCode'; MigrationId = '005'; SqlType = 'nvarchar'; MaxLength = 128; Nullable = $true },

        @{ TableName = 'RegistrationFormAssetReferenceLocks'; ColumnName = 'LockId'; MigrationId = '011'; SqlType = 'tinyint'; MaxLength = 1; Nullable = $false },

        @{ TableName = 'RegistrationFormSettingTypes'; ColumnName = 'Setting'; MigrationId = '006'; SqlType = 'nvarchar'; MaxLength = 400; Nullable = $false },
        @{ TableName = 'RegistrationFormSettings'; ColumnName = 'OrganizationID'; MigrationId = '008'; SqlType = 'int'; MaxLength = 4; Nullable = $false },
        @{ TableName = 'RegistrationFormSettings'; ColumnName = 'Setting'; MigrationId = '008'; SqlType = 'nvarchar'; MaxLength = 400; Nullable = $false },
        @{ TableName = 'RegistrationFormSettings'; ColumnName = 'FormCode'; MigrationId = '008'; SqlType = 'nvarchar'; MaxLength = 128; Nullable = $false },
        @{ TableName = 'RegistrationFormSettings'; ColumnName = 'Value'; MigrationId = '008'; SqlType = 'nvarchar'; MaxLength = -1; Nullable = $true }
    )
    foreach ($requiredColumn in $requiredColumns) {
        Confirm-BaselineColumn @requiredColumn
    }

    $requiredPrimaryKeys = @(
        @{ TableName = 'RegistrationFormCodeMetadata'; MigrationId = '001'; ConstraintType = 'PK'; KeyColumns = @('OrganizationId', 'FormCode'); ExpectedIndexType = 'CLUSTERED' },
        @{ TableName = 'RegistrationSettingScopeVersions'; MigrationId = '001'; ConstraintType = 'PK'; KeyColumns = @('OrganizationId', 'FormCode'); ExpectedIndexType = 'CLUSTERED' },
        @{ TableName = 'RegistrationSettingDrafts'; MigrationId = '001'; ConstraintType = 'PK'; KeyColumns = @('DraftId'); ExpectedIndexType = 'CLUSTERED' },
        @{ TableName = 'RegistrationSettingDraftChanges'; MigrationId = '001'; ConstraintType = 'PK'; KeyColumns = @('DraftChangeId'); ExpectedIndexType = 'CLUSTERED' },
        @{ TableName = 'RegistrationSettingPreviewLinks'; MigrationId = '001'; ConstraintType = 'PK'; KeyColumns = @('PreviewLinkId'); ExpectedIndexType = 'CLUSTERED' },
        @{ TableName = 'RegistrationSettingAuditEvents'; MigrationId = '001'; ConstraintType = 'PK'; KeyColumns = @('AuditEventId'); ExpectedIndexType = 'CLUSTERED' },
        @{ TableName = 'RegistrationSettingsCacheGeneration'; MigrationId = '001'; ConstraintType = 'PK'; KeyColumns = @('Id'); ExpectedIndexType = 'CLUSTERED' },
        @{ TableName = 'RegistrationFormAssets'; MigrationId = '004'; ConstraintType = 'PK'; KeyColumns = @('AssetId'); ExpectedIndexType = 'CLUSTERED' },
        @{ TableName = 'RegistrationFormAssetReferenceLocks'; MigrationId = '011'; ConstraintType = 'PK'; KeyColumns = @('LockId'); ExpectedIndexType = 'CLUSTERED' },
        @{ TableName = 'RegistrationFormSettingTypes'; MigrationId = '006'; ConstraintType = 'PK'; KeyColumns = @('Setting'); ExpectedIndexType = 'CLUSTERED' },
        @{ TableName = 'RegistrationFormSettings'; MigrationId = '006'; ConstraintType = 'PK'; KeyColumns = @('OrganizationID', 'Setting', 'FormCode'); ExpectedIndexType = 'CLUSTERED' }
    )
    foreach ($primaryKey in $requiredPrimaryKeys) {
        Confirm-BaselineKeyConstraint @primaryKey
    }

    Confirm-BaselineKeyConstraint -TableName 'RegistrationSettingDraftChanges' -MigrationId '001' -ConstraintType UQ -KeyColumns @('DraftId', 'SettingKey') -ExpectedIndexType 'NONCLUSTERED'
    Confirm-BaselineKeyConstraint -TableName 'RegistrationSettingPreviewLinks' -MigrationId '001' -ConstraintType UQ -KeyColumns @('TokenHash') -ExpectedIndexType 'NONCLUSTERED'

    Confirm-BaselineForeignKey -TableName 'RegistrationSettingDraftChanges' -MigrationId '001' -ReferencedTableName 'RegistrationSettingDrafts' -ParentColumns @('DraftId') -ReferencedColumns @('DraftId') -DeleteAction CASCADE
    Confirm-BaselineForeignKey -TableName 'RegistrationSettingPreviewLinks' -MigrationId '001' -ReferencedTableName 'RegistrationSettingDrafts' -ParentColumns @('DraftId') -ReferencedColumns @('DraftId') -DeleteAction CASCADE
    Confirm-BaselineForeignKey -TableName 'RegistrationFormSettings' -MigrationId '006' -ReferencedTableName 'RegistrationFormSettingTypes' -ParentColumns @('Setting') -ReferencedColumns @('Setting')

    Confirm-BaselineCheckConstraint -TableName 'RegistrationFormCodeMetadata' -MigrationId '001' -ExpectedDefinitions @(
        "LEN(FormCode) > 0"
    )
    Confirm-BaselineCheckConstraint -TableName 'RegistrationSettingDrafts' -MigrationId '001' -ExpectedDefinitions @(
        "Status IN ('Active', 'Committed', 'Discarded', 'Invalidated')",
        "Status = 'Active' OR Status = 'Committed' OR Status = 'Discarded' OR Status = 'Invalidated'"
    )
    Confirm-BaselineCheckConstraint -TableName 'RegistrationSettingDraftChanges' -MigrationId '001' -ExpectedDefinitions @(
        "Operation IN ('Upsert', 'RemoveOverride')",
        "Operation = 'Upsert' OR Operation = 'RemoveOverride'"
    )
    Confirm-BaselineCheckConstraint -TableName 'RegistrationSettingDraftChanges' -MigrationId '001' -ExpectedDefinitions @(
        "(Operation = 'Upsert' AND Value IS NOT NULL) OR (Operation = 'RemoveOverride' AND Value IS NULL)",
        "Operation = 'Upsert' AND Value IS NOT NULL OR Operation = 'RemoveOverride' AND Value IS NULL",
        "((Operation = 'Upsert' AND Value IS NOT NULL) OR (Operation = 'RemoveOverride' AND Value IS NULL))"
    )
    Confirm-BaselineCheckConstraint -TableName 'RegistrationSettingAuditEvents' -MigrationId '001' -ExpectedDefinitions @(
        "MetadataJson IS NULL OR ISJSON(MetadataJson) = 1"
    )
    Confirm-BaselineCheckConstraint -TableName 'RegistrationFormAssets' -MigrationId '004' -ExpectedDefinitions @(
        "LEN(LTRIM(RTRIM(FileName))) > 0"
    )
    Confirm-BaselineCheckConstraint -TableName 'RegistrationFormAssets' -MigrationId '004' -ExpectedDefinitions @(
        "LEN(LTRIM(RTRIM(ContentType))) > 0"
    )
    Confirm-BaselineCheckConstraint -TableName 'RegistrationFormAssets' -MigrationId '004' -ExpectedDefinitions @(
        "DATALENGTH(Content) > 0"
    )
    Confirm-BaselineCheckConstraint -TableName 'RegistrationFormAssets' -MigrationId '004' -ExpectedDefinitions @(
        "LEN(ContentHash) = 64"
    )
    Confirm-BaselineCheckConstraint -TableName 'RegistrationFormAssetReferenceLocks' -MigrationId '011' -ExpectedDefinitions @(
        "LockId = 1"
    )

    $requiredDefaults = @(
        @{ TableName = 'RegistrationFormCodeMetadata'; ColumnName = 'CreatedAtUtc'; MigrationId = '001'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationFormCodeMetadata'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationSettingScopeVersions'; ColumnName = 'FormCode'; MigrationId = '001'; ExpectedDefinitions = @("''") },
        @{ TableName = 'RegistrationSettingScopeVersions'; ColumnName = 'Version'; MigrationId = '001'; ExpectedDefinitions = @('0') },
        @{ TableName = 'RegistrationSettingScopeVersions'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'FormCode'; MigrationId = '001'; ExpectedDefinitions = @("''") },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'Revision'; MigrationId = '012'; ExpectedDefinitions = @('0') },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'CreatedAtUtc'; MigrationId = '001'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationSettingDrafts'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationSettingDraftChanges'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'AllowLiveSubmission'; MigrationId = '001'; ExpectedDefinitions = @('0') },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'CreatedAtUtc'; MigrationId = '001'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationSettingPreviewLinks'; ColumnName = 'ModifiedAtUtc'; MigrationId = '001'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'TimestampUtc'; MigrationId = '001'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'FormCode'; MigrationId = '001'; ExpectedDefinitions = @("''") },
        @{ TableName = 'RegistrationSettingAuditEvents'; ColumnName = 'IsSensitive'; MigrationId = '001'; ExpectedDefinitions = @('0') },
        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'CreatedDate'; MigrationId = '004'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationFormAssets'; ColumnName = 'ModifiedDate'; MigrationId = '004'; ExpectedDefinitions = @('SYSUTCDATETIME()') },
        @{ TableName = 'RegistrationFormSettings'; ColumnName = 'FormCode'; MigrationId = '006'; ExpectedDefinitions = @("''") }
    )
    foreach ($requiredDefault in $requiredDefaults) {
        Confirm-BaselineDefault @requiredDefault
    }

    Confirm-BaselineIndex -TableName 'RegistrationSettingDrafts' -IndexName 'UX_RSD_ActiveScope' -MigrationId '001' -KeyColumns @('OrganizationId', 'FormCode') -DescendingKeys @($false, $false) -Unique $true -FilterDefinition "Status = 'Active'"
    Confirm-BaselineIndex -TableName 'RegistrationSettingAuditEvents' -IndexName 'IX_RSAE_LibraryTime' -MigrationId '001' -KeyColumns @('TargetLibraryId', 'TimestampUtc') -DescendingKeys @($false, $true) -IncludedColumns @('EventType', 'TargetOrganizationId', 'FormCode')
    Confirm-BaselineIndex -TableName 'RegistrationSettingAuditEvents' -IndexName 'IX_RSAE_ScopeFilter' -MigrationId '001' -KeyColumns @('TargetOrganizationId', 'FormCode', 'EventType', 'TimestampUtc') -DescendingKeys @($false, $false, $false, $true)
    Confirm-BaselineIndex -TableName 'RegistrationFormAssets' -IndexName 'IX_RegistrationFormAssets_UploadScope' -MigrationId '005' -KeyColumns @('UploadOrganizationId', 'UploadFormCode') -DescendingKeys @($false, $false)
    Confirm-BaselineIndex -TableName 'RegistrationFormAssets' -IndexName 'IX_RegistrationFormAssets_CreatedDate' -MigrationId '010' -KeyColumns @('CreatedDate') -DescendingKeys @($false)

    $cacheGeneration = $null
    if ($tableExists['RegistrationSettingsCacheGeneration']) {
        $cacheRows = @(Invoke-SqlRows -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT
    COUNT(*) AS TotalRows,
    COALESCE(SUM(CASE WHEN Id = 1 AND Generation >= 0 THEN 1 ELSE 0 END), 0) AS ValidRows,
    MAX(CASE WHEN Id = 1 THEN Generation END) AS CurrentGeneration
FROM dbo.RegistrationSettingsCacheGeneration;
"@)
        $cacheRow = $cacheRows | Select-Object -First 1
        if ($null -eq $cacheRow -or [int]$cacheRow.TotalRows -ne 1 -or [int]$cacheRow.ValidRows -ne 1) {
            $null = $failures.Add('001 requires dbo.RegistrationSettingsCacheGeneration to contain exactly one singleton row with Id = 1 and a non-negative Generation.')
        }
        else {
            $cacheGeneration = [int64]$cacheRow.CurrentGeneration
        }
    }

    if ($tableExists['RegistrationFormAssetReferenceLocks']) {
        $assetLockRows = @(Invoke-SqlRows -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT
    COUNT(*) AS TotalRows,
    COALESCE(SUM(CASE WHEN LockId = 1 THEN 1 ELSE 0 END), 0) AS SingletonRows
FROM dbo.RegistrationFormAssetReferenceLocks;
"@)
        $assetLockRow = $assetLockRows | Select-Object -First 1
        if ($null -eq $assetLockRow -or [int]$assetLockRow.TotalRows -ne 1 -or [int]$assetLockRow.SingletonRows -ne 1) {
            $null = $failures.Add('011 requires dbo.RegistrationFormAssetReferenceLocks to contain exactly one singleton LockId = 1 row.')
        }
    }

    if ($tableExists['RegistrationSettingPreviewLinks'] -and $null -ne $cacheGeneration) {
        $unboundLivePreviewCount = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT COUNT(*)
FROM dbo.RegistrationSettingPreviewLinks
WHERE AllowLiveSubmission = 1
  AND LiveSettingsGeneration IS NULL;
"@)
        $futureLivePreviewCount = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT COUNT(*)
FROM dbo.RegistrationSettingPreviewLinks
WHERE AllowLiveSubmission = 1
  AND LiveSettingsGeneration > @Generation;
"@ -Parameters @(
            @{ Name = '@Generation'; Type = [System.Data.SqlDbType]::BigInt; Value = $cacheGeneration; Size = 0 }
        ))
        if ($unboundLivePreviewCount -ne 0) {
            $null = $failures.Add('012 requires every existing live preview link to have a non-null settings-cache generation.')
        }
        if ($futureLivePreviewCount -ne 0) {
            $null = $failures.Add('012 requires every existing live preview link to have a settings-cache generation no greater than the current generation.')
        }
    }

    if ($tableExists['RegistrationSettingScopeVersions']) {
        $negativeVersionCount = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT COUNT(*) FROM dbo.RegistrationSettingScopeVersions WHERE Version < 0;
"@)
        if ($negativeVersionCount -ne 0) {
            $null = $failures.Add('001 requires dbo.RegistrationSettingScopeVersions.Version values to be non-negative.')
        }
    }

    if ($tableExists['RegistrationSettingDrafts']) {
        $negativeDraftStateCount = [int](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -CommandText @"
SELECT COUNT(*)
FROM dbo.RegistrationSettingDrafts
WHERE BaselineVersion < 0 OR Revision < 0;
"@)
        if ($negativeDraftStateCount -ne 0) {
            $null = $failures.Add('001 and 012 require draft BaselineVersion and Revision values to be non-negative.')
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
    Assert-MigrationChronology -Migrations $Migrations -HistoryRows $historyRows

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
