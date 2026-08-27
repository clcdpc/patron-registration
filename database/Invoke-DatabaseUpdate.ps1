[CmdletBinding()]
param(
    [string]$ConnectionString,
    [string]$ConnectionStringFile,
    [ValidatePattern('^[A-Za-z0-9_]+$')]
    [string]$DatabaseName = 'clcdb',
    [ValidateRange(1, 86400)]
    [int]$CommandTimeoutSeconds = 1800
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ConnectionString {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString) -and
        -not [string]::IsNullOrWhiteSpace($ConnectionStringFile)) {
        throw 'Specify only one of -ConnectionString or -ConnectionStringFile.'
    }

    if (-not [string]::IsNullOrWhiteSpace($ConnectionStringFile)) {
        if (-not [IO.File]::Exists($ConnectionStringFile)) {
            throw "Connection string file was not found: $ConnectionStringFile"
        }

        return [IO.File]::ReadAllText($ConnectionStringFile).Trim()
    }

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        $ConnectionString = $env:PATRON_REGISTRATION_SQL_CONNECTION_STRING
    }

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw 'Provide a connection string with -ConnectionString, -ConnectionStringFile, or PATRON_REGISTRATION_SQL_CONNECTION_STRING.'
    }

    return $ConnectionString
}

$connection = $null
$exitCode = 0

try {
    try {
        Add-Type -AssemblyName System.Data.SqlClient -ErrorAction Stop
    }
    catch {
        Add-Type -AssemblyName System.Data -ErrorAction Stop
    }

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new((Resolve-ConnectionString))
    $builder['Initial Catalog'] = $DatabaseName
    $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    $connection.Open()

    $command = $connection.CreateCommand()
    try {
        $command.CommandText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'settings-administration.sql'))
        $command.CommandTimeout = $CommandTimeoutSeconds
        $null = $command.ExecuteNonQuery()
    }
    finally {
        $command.Dispose()
    }

    Write-Output 'Database update completed.'
}
catch {
    [Console]::Error.WriteLine("Database update failed: $($_.Exception.Message)")
    $exitCode = 1
}
finally {
    if ($null -ne $connection) {
        $connection.Dispose()
    }
}

exit $exitCode
