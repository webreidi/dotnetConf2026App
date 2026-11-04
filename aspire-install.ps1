#!/usr/bin/env pwsh

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(HelpMessage = "Directory to install the CLI")]
    [string]$InstallPath = "",

    [Parameter(HelpMessage = "Version of the Aspire CLI to download")]
    [string]$Version = "",

    [Parameter(HelpMessage = "Quality to download")]
    [ValidateSet("", "release", "staging", "dev")]
    [string]$Quality = "",

    [Parameter(HelpMessage = "Operating system")]
    [ValidateSet("", "win", "linux", "linux-musl", "osx")]
    [string]$OS = "",

    [Parameter(HelpMessage = "Architecture")]
    [ValidateSet("", "x64", "arm64")]
    [string]$Architecture = "",

    [Parameter(HelpMessage = "Keep downloaded archive files and temporary directory after installation")]
    [switch]$KeepArchive,

    [Parameter(HelpMessage = "Install VS Code extension along with the CLI")]
    [switch]$InstallExtension,

    [Parameter(HelpMessage = "Install extension to VS Code Insiders instead of VS Code")]
    [switch]$UseInsiders,

    [Parameter(HelpMessage = "Do not add the install path to PATH environment variable (useful for portable installs)")]
    [switch]$SkipPath,

    [Parameter(HelpMessage = "Show help message")]
    [switch]$Help
)

# Global constants
$Script:UserAgent = "get-aspire-cli.ps1/1.0"
$Script:IsModernPowerShell = $PSVersionTable.PSVersion.Major -ge 6 -and $PSVersionTable.PSEdition -eq "Core"
$Script:ArchiveDownloadTimeoutSec = 600
$Script:ChecksumDownloadTimeoutSec = 120
$Script:ExtensionArtifactName = "aspire-vscode.vsix.zip"

# Configuration constants
$Script:Config = @{
    DefaultQuality = "release"
    MinimumPowerShellVersion = 4
    SupportedQualities = @("release", "staging", "dev")
    SupportedOperatingSystems = @("win", "linux", "linux-musl", "osx")
    SupportedArchitectures = @("x64", "arm64")
    BaseUrls = @{
        "dev" = "https://aka.ms/dotnet/9/aspire/daily"
        "staging" = "https://aka.ms/dotnet/9/aspire/rc/daily"
        "release" = "https://aka.ms/dotnet/9/aspire/ga/daily"
        "versioned" = "https://ci.dot.net/public/aspire"
        "versioned-checksums" = "https://ci.dot.net/public-checksums/aspire"
        "github-releases" = "https://github.com/microsoft/aspire/releases/download"
    }
}

# True if the script is executed from a file (pwsh -File … or .\get-aspire-cli.ps1)
# False if the body is piped / dot‑sourced / iex’d into the current session.
$InvokedFromFile = -not [string]::IsNullOrEmpty($PSCommandPath)

# Ensure minimum PowerShell version
if ($PSVersionTable.PSVersion.Major -lt $Script:Config.MinimumPowerShellVersion) {
    Write-Message "Error: This script requires PowerShell $($Script:Config.MinimumPowerShellVersion).0 or later. Current version: $($PSVersionTable.PSVersion)" -Level Error
    if ($InvokedFromFile) {
        exit 1
    }
    else {
        return 1
    }
}

# Consolidated output function with fallback for platforms that don't support Write-Host
function Write-Message {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Message,

        [Parameter()]
        [ValidateSet("Verbose", "Info", "Success", "Warning", "Error")]
        [string]$Level = "Info"
    )

    $hasWriteHost = Get-Command Write-Host -ErrorAction SilentlyContinue

    switch ($Level) {
        "Verbose" {
            if ($VerbosePreference -ne "SilentlyContinue") {
                Write-Verbose $Message
            }
        }
        "Info" {
            if ($hasWriteHost) {
                Write-Host $Message -ForegroundColor White
            } else {
                Write-Output $Message
            }
        }
        "Success" {
            if ($hasWriteHost) {
                Write-Host $Message -ForegroundColor Green
            } else {
                Write-Output "SUCCESS: $Message"
            }
        }
        "Warning" {
            Write-Warning $Message
        }
        "Error" {
            Write-Error $Message
        }
    }
}

if ($Help) {
    Write-Message @"
Aspire CLI Download Script

DESCRIPTION:
    Downloads and installs the Aspire CLI for the current platform from the specified version and quality.
    Automatically updates the current session's PATH environment variable and supports GitHub Actions.

    Running with `-Quality release` download the latest release version of the Aspire CLI for your platform and architecture.
    Running with `-Quality staging` will download the latest staging version, or the release version if no staging is available.
    Running with `-Quality dev` will download the latest dev build from `main`.

    The default quality is '$($Script:Config.DefaultQuality)'.

    Pass a specific version to get CLI for that version.

PARAMETERS:
    -InstallPath <string>       Directory to install the CLI (default: %USERPROFILE%\.aspire\bin on Windows, `$HOME/.aspire/bin on Unix)
    -Quality <string>           Quality to download (default: $($Script:Config.DefaultQuality))
    -Version <string>           Version of the Aspire CLI to download (default: unset)
    -OS <string>                Operating system (default: auto-detect)
    -Architecture <string>      Architecture (default: auto-detect)
    -InstallExtension           Install VS Code extension along with the CLI
    -UseInsiders                Install extension to VS Code Insiders instead of VS Code (requires -InstallExtension)
    -SkipPath                   Do not add the install path to PATH environment variable (useful for portable installs)
    -KeepArchive                Keep downloaded archive files and temporary directory after installation
    -Help                       Show this help message

ENVIRONMENT:
    The script automatically updates the PATH environment variable for the current session.

    Windows: The script will also add the installation path to the user's persistent PATH
    environment variable and to the session PATH, making the aspire CLI available in the existing and new terminal sessions.

    GitHub Actions Support:
    When running in GitHub Actions (GITHUB_ACTIONS=true), the script will automatically
    append the installation path to the GITHUB_PATH file to make the CLI available in
    subsequent workflow steps.

EXAMPLES:
    .\get-aspire-cli.ps1
    .\get-aspire-cli.ps1 -InstallPath "C:\tools\aspire"
    .\get-aspire-cli.ps1 -Quality "staging"
    .\get-aspire-cli.ps1 -Version "9.5.0-preview.1.25366.3"
    .\get-aspire-cli.ps1 -OS "linux" -Architecture "x64"
    .\get-aspire-cli.ps1 -InstallExtension
    .\get-aspire-cli.ps1 -InstallExtension -UseInsiders
    .\get-aspire-cli.ps1 -SkipPath
    .\get-aspire-cli.ps1 -KeepArchive
    .\get-aspire-cli.ps1 -WhatIf
    .\get-aspire-cli.ps1 -Help

    # Piped execution
    iex "& { `$(irm https://aka.ms/aspire/get/install.ps1) }"
    iex "& { `$(irm https://aka.ms/aspire/get/install.ps1) } -Quality staging"
"@
    if ($InvokedFromFile) { exit 0 } else { return }
}

# Helper function for PowerShell version-specific operations
function Invoke-WithPowerShellVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$ModernAction,

        [Parameter(Mandatory = $true)]
        [scriptblock]$LegacyAction
    )

    if ($Script:IsModernPowerShell) {
        & $ModernAction
    } else {
        & $LegacyAction
    }
}

# Function to detect OS
function Get-OperatingSystem {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    try {
        return Invoke-WithPowerShellVersion -ModernAction {
            if ($IsWindows) {
                return "win"
            }
            elseif ($IsLinux) {
                try {
                    $lddOutput = & ldd --version 2>&1 | Out-String
                    return if ($lddOutput -match "musl") { "linux-musl" } else { "linux" }
                }
                catch { return "linux" }
            }
            elseif ($IsMacOS) {
                return "osx"
            }
            else {
                return "unsupported"
            }
        } -LegacyAction {
            # PowerShell 5.1 and earlier - more reliable Windows detection
            if ($env:OS -eq "Windows_NT" -or [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
                return "win"
            }

            $platform = [System.Environment]::OSVersion.Platform
            switch ($platform) {
                { $_ -in @([System.PlatformID]::Unix, 4, 6) } { return "linux" }
                { $_ -in @([System.PlatformID]::MacOSX, 128) } { return "osx" }
                default { return "unsupported" }
            }
        }
    }
    catch {
        Write-Message "Failed to detect operating system: $($_.Exception.Message)" -Level Warning
        return "unsupported"
    }
}

# Enhanced function for cross-platform architecture detection
function Get-MachineArchitecture {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    Write-Message "Detecting machine architecture" -Level Verbose

    try {
        # On Windows PowerShell, use environment variables
        if (-not $Script:IsModernPowerShell -or $IsWindows) {
            # On PS x86, PROCESSOR_ARCHITECTURE reports x86 even on x64 systems.
            # To get the correct architecture, we need to use PROCESSOR_ARCHITEW6432.
            # PS x64 doesn't define this, so we fall back to PROCESSOR_ARCHITECTURE.
            # Possible values: amd64, x64, x86, arm64, arm
            if ( $null -ne $ENV:PROCESSOR_ARCHITEW6432 ) {
                return $ENV:PROCESSOR_ARCHITEW6432
            }

            try {
                $osInfo = Get-CimInstance -ClassName CIM_OperatingSystem -ErrorAction Stop
                if ($osInfo.OSArchitecture -like "ARM*") {
                    if ([Environment]::Is64BitOperatingSystem) {
                        return "arm64"
                    }
                    return "arm"
                }
            }
            catch {
                Write-Message "Failed to get CIM instance: $($_.Exception.Message)" -Level Verbose
            }

            if ( $null -ne $ENV:PROCESSOR_ARCHITECTURE ) {
                return $ENV:PROCESSOR_ARCHITECTURE
            }
        }

        # For PowerShell 6+ on Unix systems, use .NET runtime information
        if ($Script:IsModernPowerShell) {
            try {
                $runtimeArch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
                switch ($runtimeArch) {
                    "X64" { return "x64" }
                    "Arm64" { return "arm64" }
                    default {
                        Write-Message "Unknown runtime architecture: $runtimeArch" -Level Verbose
                        # Fall back to uname if available
                        if (Get-Command uname -ErrorAction SilentlyContinue) {
                            $unameArch = & uname -m
                            switch ($unameArch) {
                                { @("x86_64", "amd64") -contains $_ } { return "x64" }
                                { @("aarch64", "arm64") -contains $_ } { return "arm64" }
                                default {
                                    Write-Message "Unknown uname architecture: $unameArch" -Level Verbose
                                    return "x64"  # Default fallback
                                }
                            }
                        }
                        return "x64"  # Default fallback
                    }
                }
            }
            catch {
                Write-Message "Failed to get runtime architecture: $($_.Exception.Message)" -Level Warning
                return "x64"  # Default fallback
            }
        }

        # Final fallback for older PowerShell versions
        return "x64"
    }
    catch {
        Write-Message "Architecture detection failed: $($_.Exception.Message)" -Level Warning
        return "x64"  # Safe fallback
    }
}

# Convert architecture to CLI architecture format
function Get-CLIArchitectureFromArchitecture {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Architecture
    )

    Write-Message "Converting architecture: $Architecture" -Level Verbose

    if ($Architecture -eq "<auto>") {
        $Architecture = Get-MachineArchitecture
    }

    $normalizedArch = $Architecture.ToLowerInvariant()
    switch ($normalizedArch) {
        { @("amd64", "x64") -contains $_ } {
            return "x64"
        }
        { $_ -eq "arm64" } {
            return "arm64"
        }
        default {
            throw "Architecture '$Architecture' not supported. If you think this is a bug, report it at https://github.com/microsoft/aspire/issues"
        }
    }
}

function Get-ContentTypeFromUri {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter()]
        [int]$TimeoutSec = 60,

        [Parameter()]
        [int]$OperationTimeoutSec = 30,

        [Parameter()]
        [int]$MaxRetries = 5
    )

    try {
        Write-Message "Making HEAD request to get content type for: $Uri" -Level Verbose
        $headResponse = Invoke-SecureWebRequest -Uri $Uri -Method "Head" -TimeoutSec $TimeoutSec -OperationTimeoutSec $OperationTimeoutSec -MaxRetries $MaxRetries

        # Extract Content-Type from response headers
        $headers = $headResponse.Headers
        if ($headers) {
            # Try common case variations and use case-insensitive lookup
            $contentTypeKey = $headers.Keys | Where-Object { $_ -ieq "Content-Type" } | Select-Object -First 1
            if ($contentTypeKey) {
                $value = $headers[$contentTypeKey]
                if ($value -is [array]) {
                    return $value -join ", "
                }
                else {
                    return $value
                }
            }
        }
        return ""
    }
    catch {
        Write-Message "Failed to get content type from URI: $($_.Exception.Message)" -Level Verbose
        return "Unable to determine ($($_.Exception.Message))"
    }
}

# Enhanced web request function with security and reliability improvements
function Invoke-SecureWebRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter()]
        [string]$OutFile,

        [Parameter()]
        [string]$Method = "Get",

        [Parameter()]
        [int]$TimeoutSec = 60,

        [Parameter()]
        [int]$OperationTimeoutSec = 30,

        [Parameter()]
        [int]$MaxRetries = 5
    )

    # Configure TLS for PowerShell 5
    if (-not $Script:IsModernPowerShell) {
        try {
            # Set TLS 1.2 and attempt TLS 1.3 if available
            $protocols = [Net.SecurityProtocolType]::Tls12
            try {
                $protocols = $protocols -bor [Net.SecurityProtocolType]::Tls13
            }
            catch {
                Write-Message "TLS 1.3 not available, using TLS 1.2 only" -Level Verbose
            }
            [Net.ServicePointManager]::SecurityProtocol = $protocols
        }
        catch {
            Write-Message "Failed to configure TLS settings: $($_.Exception.Message)" -Level Warning
        }
    }

    # Build base request parameters
    $requestParams = @{
        Uri = $Uri
        Method = $Method
        MaximumRedirection = 10
        TimeoutSec = $TimeoutSec
        UserAgent = $Script:UserAgent
        UseBasicParsing = $true
    }

    if ($Method -eq "Get" -and $OutFile) {
        $requestParams.OutFile = $OutFile
    }

    # Add modern PowerShell parameters with graceful fallback
    if ($Script:IsModernPowerShell) {
        $modernParams = @{
            "SslProtocol" = @("Tls12", "Tls13")
            "OperationTimeoutSeconds" = $OperationTimeoutSec
            "MaximumRetryCount" = $MaxRetries
        }

        foreach ($paramName in $modernParams.Keys) {
            try {
                $requestParams[$paramName] = $modernParams[$paramName]
            }
            catch {
                Write-Message "$paramName parameter not available: $($_.Exception.Message)" -Level Verbose
            }
        }
    }

    try {
        return Invoke-WebRequest @requestParams
    }
    catch {
        throw $_.Exception
    }
}

# Builds a compact display string for download messages without exposing the full URL.
function Get-DownloadDescriptor {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter()]
        [string]$Source
    )

    try {
        $downloadUri = [System.Uri]$Uri
        $fileName = [System.IO.Path]::GetFileName($downloadUri.AbsolutePath)

        if ([string]::IsNullOrWhiteSpace($fileName)) {
            return $Uri
        }

        if ([string]::IsNullOrWhiteSpace($Source)) {
            return $fileName
        }

        return "$fileName from $Source"
    }
    catch {
        return $Uri
    }
}

# Enhanced file download wrapper with validation
function Invoke-FileDownload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [string]$OutputPath,

        [Parameter()]
        [int]$TimeoutSec = 60,

        [Parameter()]
        [int]$OperationTimeoutSec = 30,

        [Parameter()]
        [int]$MaxRetries = 5
    )

    # Validate content type via HEAD request
    Write-Message "Validating content type for $Uri" -Level Verbose
    $contentType = Get-ContentTypeFromUri -Uri $Uri -TimeoutSec 60 -OperationTimeoutSec $OperationTimeoutSec -MaxRetries $MaxRetries
    Write-Message "Detected content type: $contentType" -Level Verbose

    if ($contentType -and $contentType.ToLowerInvariant().StartsWith("text/html")) {
        throw "Server returned HTML content instead of expected file. Make sure the URL is correct: $Uri"
    }

    try {
        Write-Message "Downloading $Uri to $OutputPath" -Level Verbose
        Invoke-SecureWebRequest -Uri $Uri -OutFile $OutputPath -TimeoutSec $TimeoutSec -OperationTimeoutSec $OperationTimeoutSec -MaxRetries $MaxRetries
        Write-Message "Successfully downloaded file to: $OutputPath" -Level Verbose
    }
    catch {
        throw "Failed to download $Uri - $($_.Exception.Message)"
    }
}

# Function to check VS Code CLI dependency
function Test-VSCodeCLIDependency {
    [CmdletBinding()]
    param(
        [Parameter()]
        [switch]$UseInsiders
    )

    $vscodeCmd = if ($UseInsiders) { "code-insiders" } else { "code" }
    $vscodeName = if ($UseInsiders) { "VS Code Insiders" } else { "VS Code" }

    if (-not (Get-Command $vscodeCmd -ErrorAction SilentlyContinue)) {
        Write-Message "$vscodeName CLI ($vscodeCmd) not found in PATH" -Level Warning
        return $false
    }

    Write-Message "$vscodeName CLI ($vscodeCmd) found" -Level Verbose
    return $true
}

# Enhanced checksum validation with proper error handling
function Test-FileChecksum {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchiveFile,

        [Parameter(Mandatory = $true)]
        [string]$ChecksumFile
    )

    # Check if Get-FileHash cmdlet is available
    if (-not (Get-Command Get-FileHash -ErrorAction SilentlyContinue)) {
        throw "Get-FileHash cmdlet not found. Please use PowerShell 4.0 or later to validate checksums."
    }

    $expectedChecksum = (Get-Content $ChecksumFile -Raw -ErrorAction Stop).Trim().ToLowerInvariant()
    $actualChecksum = (Get-FileHash -Path $ArchiveFile -Algorithm SHA512 -ErrorAction Stop).Hash.ToLowerInvariant()

    # Compare checksums
    if ($expectedChecksum -ne $actualChecksum) {
        $displayChecksum = if ($expectedChecksum.Length -gt 128) { $expectedChecksum.Substring(0, 128) + "..." } else { $expectedChecksum }
        throw "Checksum validation failed for $ArchiveFile with checksum from $ChecksumFile !`nExpected: $displayChecksum`nActual:   $actualChecksum"
    }
}

# Function to get the CLI executable path for a given OS
function Get-CliExecutablePath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,

        [Parameter(Mandatory = $true)]
        [string]$OS
    )

    $exeName = if ($OS -eq "win") { "aspire.exe" } else { "aspire" }
    return Join-Path $DestinationPath $exeName
}

# Function to back up an existing CLI executable before overwriting it.
# This matches self-update semantics by deleting stale *.old.* backups first.
# On Windows, a running process can still block the rename.
function Backup-ExistingCliExecutable {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetExePath
    )

    if (Test-Path $TargetExePath) {
        $unixTimestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $backupPath = "$TargetExePath.old.$unixTimestamp"

        if ($PSCmdlet.ShouldProcess($TargetExePath, "Backup to $backupPath")) {
            Write-Message "Backing up existing CLI: $TargetExePath -> $backupPath" -Level Verbose

            Remove-OldCliBackupFiles -TargetExePath $TargetExePath

            # Rename existing executable to .old.[timestamp]
            try {
                Move-Item -Path $TargetExePath -Destination $backupPath -Force -ErrorAction Stop
            }
            catch {
                throw "Failed to back up existing CLI at '$TargetExePath'. The file may be in use by another process. Please close any running Aspire CLI instances and try again. Error: $($_.Exception.Message)"
            }
            return $backupPath
        }
    }

    return $null
}

# Function to restore CLI executable from backup if installation fails
function Restore-CliExecutableFromBackup {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BackupPath,

        [Parameter(Mandatory = $true)]
        [string]$TargetExePath
    )

    if ($PSCmdlet.ShouldProcess($BackupPath, "Restore to $TargetExePath")) {
        Write-Message "Restoring CLI from backup: $BackupPath -> $TargetExePath" -Level Warning

        if (Test-Path $TargetExePath) {
            Remove-Item -Path $TargetExePath -Force -ErrorAction SilentlyContinue
        }

        Move-Item -Path $BackupPath -Destination $TargetExePath -Force -ErrorAction Stop
    }
}

# Function to clean up old backup files (aspire.exe.old.* or aspire.old.*)
function Remove-OldCliBackupFiles {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetExePath
    )

    $directory = Split-Path -Parent $TargetExePath
    if ([string]::IsNullOrEmpty($directory)) {
        return
    }

    $exeName = Split-Path -Leaf $TargetExePath
    $searchPattern = "$exeName.old.*"

    $oldBackupFiles = Get-ChildItem -Path $directory -Filter $searchPattern -ErrorAction SilentlyContinue
    foreach ($backupFile in $oldBackupFiles) {
        if ($PSCmdlet.ShouldProcess($backupFile.FullName, "Delete old backup")) {
            try {
                Remove-Item -Path $backupFile.FullName -Force -ErrorAction Stop
                Write-Message "Deleted old backup file: $($backupFile.FullName)" -Level Verbose
            }
            catch {
                Write-Message "Failed to delete old backup file: $($backupFile.FullName) - $($_.Exception.Message)" -Level Verbose
            }
        }
    }
}

function Expand-AspireCliArchive {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [string]$ArchiveFile,
        [string]$DestinationPath,
        [string]$OS
    )

    Write-Message "Unpacking archive to: $DestinationPath" -Level Verbose

    # Get the target executable path using shared function
    $targetExePath = Get-CliExecutablePath -DestinationPath $DestinationPath -OS $OS
    $backupPath = $null

    try {
        # Create destination directory if it doesn't exist
        if (-not (Test-Path $DestinationPath)) {
            Write-Message "Creating destination directory: $DestinationPath" -Level Verbose
            New-Item -ItemType Directory -Path $DestinationPath -Force -ErrorAction Stop | Out-Null
        }
        else {
            # Back up the existing executable before extraction.
            # On Windows, this can still fail if the file is locked by a running process.
            $backupPath = Backup-ExistingCliExecutable -TargetExePath $targetExePath
        }

        if ($OS -eq "win") {
            # Use Expand-Archive for ZIP files on Windows
            if (-not (Get-Command Expand-Archive -ErrorAction SilentlyContinue)) {
                throw "Expand-Archive cmdlet not found. Please use PowerShell 5.0 or later to extract ZIP files."
            }

            Expand-Archive -Path $ArchiveFile -DestinationPath $DestinationPath -Force -ErrorAction Stop
        }
        else {
            # Use tar for tar.gz files on Unix systems
            if (-not (Get-Command tar -ErrorAction SilentlyContinue)) {
                throw "tar command not found. Please install tar to extract tar.gz files."
            }

            $currentLocation = Get-Location
            try {
                Set-Location $DestinationPath
                $tarOutput = & tar -xzf $ArchiveFile 2>&1
                if ($LASTEXITCODE -ne 0) {
                    $tarMessage = ($tarOutput | ForEach-Object { $_.ToString() } | Out-String).Trim()
                    if ([string]::IsNullOrWhiteSpace($tarMessage)) {
                        throw "tar command failed with exit code $LASTEXITCODE"
                    }

                    throw "tar command failed with exit code $LASTEXITCODE`: $tarMessage"
                }
            }
            finally {
                Set-Location $currentLocation
            }
        }

        # Clean up old backup files on successful extraction
        if (Test-Path $targetExePath) {
            Remove-OldCliBackupFiles -TargetExePath $targetExePath
        }

        Write-Message "Successfully unpacked archive" -Level Verbose
    }
    catch {
        $unpackErrorMessage = $_.Exception.Message

        # If anything goes wrong and we have a backup, restore it
        if ($backupPath -and (Test-Path $backupPath)) {
            try {
                Restore-CliExecutableFromBackup -BackupPath $backupPath -TargetExePath $targetExePath
            }
            catch {
                throw "Failed to unpack archive: $unpackErrorMessage. Restore from backup also failed: $($_.Exception.Message)"
            }
        }

        throw "Failed to unpack archive: $unpackErrorMessage"
    }
}

# Function to map quality to channel name
function ConvertTo-ChannelName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Quality
    )

    switch ($Quality.ToLowerInvariant()) {
        "release" { return "stable" }
        "staging" { return "staging" }
        "dev" { return "daily" }
        default { return $Quality }
    }
}

# Simplified installation path determination
function Get-InstallPath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter()]
        [string]$InstallPath
    )

    if (-not [string]::IsNullOrWhiteSpace($InstallPath)) {
        # Validate that the path is not just whitespace and can be created
        try {
            $resolvedPath = [System.IO.Path]::GetFullPath($InstallPath)
            return $resolvedPath
        }
        catch {
            throw "Invalid installation path: $InstallPath - $($_.Exception.Message)"
        }
    }

    # Get home directory cross-platform
    $homeDirectory = Invoke-WithPowerShellVersion -ModernAction {
        if ($env:HOME) {
            $env:HOME
        } elseif ($IsWindows -and $env:USERPROFILE) {
            $env:USERPROFILE
        } elseif ($env:USERPROFILE) {
            $env:USERPROFILE
        } else {
            $null
        }
    } -LegacyAction {
        if ($env:USERPROFILE) {
            $env:USERPROFILE
        } elseif ($env:HOME) {
            $env:HOME
        } else {
            $null
        }
    }

    if ([string]::IsNullOrWhiteSpace($homeDirectory)) {
        throw "Unable to determine user home directory. Please specify -InstallPath parameter."
    }

    $defaultPath = Join-Path (Join-Path $homeDirectory ".aspire") "bin"
    return [System.IO.Path]::GetFullPath($defaultPath)
}

# Simplified PATH environment update
function Update-PathEnvironment {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$InstallPath,

        [Parameter(Mandatory = $true)]
        [ValidateSet("win", "linux", "linux-musl", "osx")]
        [string]$TargetOS
    )

    $pathSeparator = [System.IO.Path]::PathSeparator

    # Update current session PATH
    $currentPathArray = $env:PATH.Split($pathSeparator, [StringSplitOptions]::RemoveEmptyEntries)
    if ($currentPathArray -notcontains $InstallPath) {
        if ($PSCmdlet.ShouldProcess("PATH environment variable", "Add $InstallPath to current session")) {
            $env:PATH = (@($InstallPath) + $currentPathArray) -join $pathSeparator
            Write-Message "Added $InstallPath to PATH for current session" -Level Info
        }
    }

    # Update persistent PATH for Windows
    if ($TargetOS -eq "win") {
        try {
            $userPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::User)
            if (-not $userPath) { $userPath = "" }
            $userPathArray = if ($userPath) { $userPath.Split($pathSeparator, [StringSplitOptions]::RemoveEmptyEntries) } else { @() }
            if ($userPathArray -notcontains $InstallPath) {
                if ($PSCmdlet.ShouldProcess("User PATH environment variable", "Add $InstallPath")) {
                    $newUserPath = (@($InstallPath) + $userPathArray) -join $pathSeparator
                    [Environment]::SetEnvironmentVariable("PATH", $newUserPath, [EnvironmentVariableTarget]::User)
                    Write-Message "Added $InstallPath to user PATH environment variable" -Level Info
                }
            }

            Write-Message "" -Level Info
            Write-Message "The aspire cli is now available for use in this and new sessions." -Level Success
        }
        catch {
            Write-Message "Failed to update persistent PATH environment variable: $($_.Exception.Message)" -Level Warning
            Write-Message "You may need to manually add $InstallPath to your PATH environment variable" -Level Info
        }
    }

    # GitHub Actions support
    if ($env:GITHUB_ACTIONS -eq "true" -and $env:GITHUB_PATH) {
        try {
            if ($PSCmdlet.ShouldProcess("GITHUB_PATH environment variable", "Add $InstallPath to GITHUB_PATH")) {
                Add-Content -Path $env:GITHUB_PATH -Value $InstallPath
                Write-Message "Added $InstallPath to GITHUB_PATH for GitHub Actions" -Level Success
            }
        }
        catch {
            Write-Message "Failed to update GITHUB_PATH: $($_.Exception.Message)" -Level Warning
        }
    }
}

# Function to download VS Code extension
function Get-AspireExtension {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TempDir,

        [Parameter()]
        [string]$Version,

        [Parameter()]
        [string]$Quality
    )

    Write-Message "Downloading Aspire VS Code extension" -Level Info

    $extensionUrl = Get-AspireExtensionUrl -Version $Version -Quality $Quality
    $extensionSource = $null
    if ([string]::IsNullOrWhiteSpace($Version) -and -not [string]::IsNullOrWhiteSpace($Quality)) {
        $extensionSource = "the $(ConvertTo-ChannelName -Quality $Quality) channel"
    }
    $extensionDescriptor = Get-DownloadDescriptor -Uri $extensionUrl -Source $extensionSource
    $extensionArchive = Join-Path $TempDir $Script:ExtensionArtifactName

    try {
        if ($PSCmdlet.ShouldProcess($extensionArchive, "Download extension ($extensionDescriptor)")) {
            Write-Message "Downloading $extensionDescriptor" -Level Info
            Invoke-FileDownload -Uri $extensionUrl -OutputPath $extensionArchive -TimeoutSec $Script:ArchiveDownloadTimeoutSec
            Write-Message "Successfully downloaded extension archive" -Level Verbose
        }

        return $extensionArchive
    }
    catch {
        Write-Message "Failed to download extension: $($_.Exception.Message)" -Level Error
        throw
    }
}

# Function to install VS Code extension
function Install-AspireExtension {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExtensionArchive,

        [Parameter()]
        [switch]$UseInsiders
    )

    $vscodeCmd = if ($UseInsiders) { "code-insiders" } else { "code" }
    $vscodeName = if ($UseInsiders) { "VS Code Insiders" } else { "VS Code" }

    Write-Message "Installing Aspire extension to $vscodeName" -Level Info

    # Extract the zip to get the VSIX file
    $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-extension-$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))"

    try {
        if ($PSCmdlet.ShouldProcess($extractDir, "Extract extension archive")) {
            # Expand the zip archive
            if ($Script:IsModernPowerShell) {
                Expand-Archive -Path $ExtensionArchive -DestinationPath $extractDir -Force -ErrorAction Stop
            } else {
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                [System.IO.Compression.ZipFile]::ExtractToDirectory($ExtensionArchive, $extractDir)
            }

            Write-Message "Extracted extension archive" -Level Verbose
        }

        # Find the VSIX file
        $vsixFile = Get-ChildItem -Path $extractDir -Filter "*.vsix" | Select-Object -First 1

        if (-not $vsixFile) {
            throw "No VSIX file found in extension archive"
        }

        Write-Message "Found VSIX file: $($vsixFile.Name)" -Level Verbose

        # Install the extension
        if ($PSCmdlet.ShouldProcess($vsixFile.FullName, "Install extension using $vscodeCmd")) {
            $installArgs = @("--install-extension", $vsixFile.FullName, "--force")
            Write-Message "Running: $vscodeCmd $($installArgs -join ' ')" -Level Verbose

            $output = & $vscodeCmd $installArgs 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Extension installation failed with exit code $LASTEXITCODE`: $output"
            }

            Write-Message "Successfully installed Aspire extension to $vscodeName" -Level Success
        }
    }
    finally {
        # Clean up extraction directory
        if (Test-Path $extractDir) {
            try {
                Remove-Item -Path $extractDir -Recurse -Force -ErrorAction SilentlyContinue
                Write-Message "Cleaned up extraction directory" -Level Verbose
            }
            catch {
                Write-Message "Failed to clean up extraction directory: $($_.Exception.Message)" -Level Warning
            }
        }
    }
}

# Function to construct VS Code extension URL
function Get-AspireExtensionUrl {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter()]
        [string]$Version,

        [Parameter()]
        [string]$Quality
    )

    $extension = "vsix.zip"

    if ([string]::IsNullOrWhiteSpace($Version)) {
        # Validate quality against supported values
        if ($Quality -notin $Script:Config.SupportedQualities) {
            throw "Unsupported quality '$Quality'. Supported values are: $($Script:Config.SupportedQualities -join ", ")."
        }

        $baseUrl = $Script:Config.BaseUrls[$Quality]
        if (-not $baseUrl) {
            throw "No base URL configured for quality: $Quality"
        }

        return "$baseUrl/aspire-vscode.$extension"
    }
    else {
        # Version-based URL
        $baseUrl = $Script:Config.BaseUrls["versioned"]
        return "$baseUrl/$Version/aspire-vscode-$Version.$extension"
    }
}

function Test-StableVersion {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    return $Version -match '^v?\d+\.\d+\.\d+$'
}

function ConvertTo-StableVersion {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ($Version.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Version.Substring(1)
    }

    return $Version
}

# Enhanced URL construction function with configuration-based URLs
function Get-AspireCliUrl {
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter()]
        [string]$Version,

        [Parameter()]
        [string]$Quality,

        [Parameter(Mandatory = $true)]
        [string]$RuntimeIdentifier,

        [Parameter(Mandatory = $true)]
        [string]$Extension
    )

    if ([string]::IsNullOrWhiteSpace($Version)) {
        # Validate quality against supported values
        if ($Quality -notin $Script:Config.SupportedQualities) {
            throw "Unsupported quality '$Quality'. Supported values are: $($Script:Config.SupportedQualities -join ", ")."
        }

        # When version is not set use aka.ms URLs based on quality
        $baseUrl = $Script:Config.BaseUrls[$Quality]
        if (-not $baseUrl) {
            throw "No base URL configured for quality: $Quality"
        }

        $archiveFilename = "aspire-cli-$RuntimeIdentifier.$Extension"
        $checksumFilename = "aspire-cli-$RuntimeIdentifier.$Extension.sha512"

        return [PSCustomObject]@{
            ArchiveUrl = "$baseUrl/$archiveFilename"
            ArchiveFilename = $archiveFilename
            ChecksumUrl = "$baseUrl/$checksumFilename"
            ChecksumFilename = $checksumFilename
        }
    }
    else {
        if (Test-StableVersion -Version $Version) {
            $stableVersion = ConvertTo-StableVersion -Version $Version
            $archiveFilename = "aspire-cli-$RuntimeIdentifier-$stableVersion.$Extension"
            $checksumFilename = "$archiveFilename.sha512"
            $baseUrl = $Script:Config.BaseUrls["github-releases"]

            return [PSCustomObject]@{
                ArchiveUrl = "$baseUrl/v$stableVersion/$archiveFilename"
                ArchiveFilename = $archiveFilename
                ChecksumUrl = "$baseUrl/v$stableVersion/$checksumFilename"
                ChecksumFilename = $checksumFilename
            }
        }

        $archiveFilename = "aspire-cli-$RuntimeIdentifier-$Version.$Extension"
        $checksumFilename = "$archiveFilename.sha512"

        return [PSCustomObject]@{
            ArchiveUrl = "$($Script:Config.BaseUrls["versioned"])/$Version/$archiveFilename"
            ArchiveFilename = $archiveFilename
            ChecksumUrl = "$($Script:Config.BaseUrls["versioned-checksums"])/$Version/$checksumFilename"
            ChecksumFilename = $checksumFilename
        }
    }
}

# Function to download and install the Aspire CLI
function Install-AspireCli {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallPath,
        [string]$Version,
        [string]$Quality,
        [string]$OS,
        [string]$Architecture
    )

    # Create a temporary directory for downloads with conflict resolution
    $tempBaseName = "aspire-cli-download-$([System.Guid]::NewGuid().ToString("N").Substring(0, 8))"
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) $tempBaseName

    # Handle potential conflicts
    $attempt = 1
    while (Test-Path $tempDir) {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "$tempBaseName-$attempt"
        $attempt++
        if ($attempt -gt 10) {
            throw "Unable to create temporary directory after 10 attempts"
        }
    }

    if ($PSCmdlet.ShouldProcess($InstallPath, "Create temporary directory")) {
        Write-Message "Creating temporary directory: $tempDir" -Level Verbose
        try {
            New-Item -ItemType Directory -Path $tempDir -Force -ErrorAction Stop | Out-Null
        }
        catch {
            throw "Failed to create temporary directory: $tempDir - $($_.Exception.Message)"
        }
    }

    try {
        # Determine OS and architecture (either detected or user-specified)
        $targetOS = if ([string]::IsNullOrWhiteSpace($OS)) { Get-OperatingSystem } else { $OS }

        # Check for unsupported OS
        if ($targetOS -eq "unsupported") {
            throw "Unsupported operating system. Current platform: $([System.Environment]::OSVersion.Platform)"
        }

        $targetArch = if ([string]::IsNullOrWhiteSpace($Architecture)) { Get-CLIArchitectureFromArchitecture "<auto>" } else { Get-CLIArchitectureFromArchitecture $Architecture }

        # Construct the runtime identifier and URLs
        $runtimeIdentifier = "$targetOS-$targetArch"
        $extension = if ($targetOS -eq "win") { "zip" } else { "tar.gz" }
        $urls = Get-AspireCliUrl -Version $Version -Quality $Quality -RuntimeIdentifier $runtimeIdentifier -Extension $extension
        $downloadSource = $null
        if ([string]::IsNullOrWhiteSpace($Version) -and -not [string]::IsNullOrWhiteSpace($Quality)) {
            $downloadSource = "the $(ConvertTo-ChannelName -Quality $Quality) channel"
        }
        $archiveDescriptor = Get-DownloadDescriptor -Uri $urls.ArchiveUrl -Source $downloadSource
        $checksumDescriptor = Get-DownloadDescriptor -Uri $urls.ChecksumUrl -Source $downloadSource

        $archivePath = Join-Path $tempDir $urls.ArchiveFilename
        $checksumPath = Join-Path $tempDir $urls.ChecksumFilename

        if ($PSCmdlet.ShouldProcess($archivePath, "Download CLI archive ($archiveDescriptor)")) {
            # Download the Aspire CLI archive
            Write-Message "Downloading $archiveDescriptor" -Level Info
            Invoke-FileDownload -Uri $urls.ArchiveUrl -TimeoutSec $Script:ArchiveDownloadTimeoutSec -OutputPath $archivePath
        }

        if ($PSCmdlet.ShouldProcess($checksumPath, "Download CLI archive checksum ($checksumDescriptor)")) {
            # Download and test the checksum
            Invoke-FileDownload -Uri $urls.ChecksumUrl -TimeoutSec $Script:ChecksumDownloadTimeoutSec -OutputPath $checksumPath
            Test-FileChecksum -ArchiveFile $archivePath -ChecksumFile $checksumPath

            Write-Message "Successfully downloaded and validated: $($urls.ArchiveFilename)" -Level Verbose
        }

        # Determine CLI path (needed for config operations)
        $cliExe = if ($targetOS -eq "win") { "aspire.exe" } else { "aspire" }
        $cliPath = Join-Path $InstallPath $cliExe

        if ($PSCmdlet.ShouldProcess($InstallPath, "Install CLI")) {
            # Unpack the archive
            Expand-AspireCliArchive -ArchiveFile $archivePath -DestinationPath $InstallPath -OS $targetOS

            Write-Message "Aspire CLI successfully installed to: $cliPath" -Level Success
        }

        # Write the script-route install-source sidecar next to the binary.
        # Under -WhatIf, print the target path and skip the write.
        # Authorship contract: docs/specs/install-routes.md.
        $sidecarPath = Join-Path $InstallPath '.aspire-install.json'
        if ($PSCmdlet.ShouldProcess($sidecarPath, "Write route sidecar")) {
            [System.IO.Directory]::CreateDirectory($InstallPath) | Out-Null
            [System.IO.File]::WriteAllText($sidecarPath, "{""source"":""script""}`n")
        }
        else {
            Write-Host "What if: Route sidecar would be written to: $sidecarPath"
        }

        # Download and install VS Code extension if requested
        if ($InstallExtension) {
            Write-Message "" -Level Info
            Write-Message "Installing VS Code extension" -Level Info

            if (Test-VSCodeCLIDependency -UseInsiders:$UseInsiders) {
                try {
                    $extensionArchive = Get-AspireExtension -TempDir $tempDir -Version $Version -Quality $Quality
                    Install-AspireExtension -ExtensionArchive $extensionArchive -UseInsiders:$UseInsiders
                }
                catch {
                    Write-Message "Failed to install VS Code extension: $($_.Exception.Message)" -Level Warning
                    Write-Message "The CLI was installed successfully, but the extension installation failed" -Level Warning
                }
            }
            else {
                Write-Message "Cannot install extension: VS Code CLI not found in PATH" -Level Warning
                Write-Message "Please ensure VS Code is installed and available in PATH" -Level Info
            }
        }

        # Return the target OS for the caller to use
        return $targetOS
    }
    finally {
        # Clean up temporary directory and downloaded files
        if (Test-Path $tempDir -ErrorAction SilentlyContinue) {
            if (-not $KeepArchive) {
                try {
                    Write-Message "Cleaning up temporary files..." -Level Verbose
                    Remove-Item $tempDir -Recurse -Force -ErrorAction Stop
                }
                catch {
                    Write-Message "Failed to clean up temporary directory: $tempDir - $($_.Exception.Message)" -Level Warning
                }
            }
            else {
                Write-Message "Archive files kept in: $tempDir" -Level Info
            }
        }
    }
}

# Main function with enhanced error handling and validation
function Start-AspireCliInstallation {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([int])]
    param()

    try {
        # Validate that both Version and Quality are not provided
        if (-not [string]::IsNullOrWhiteSpace($Version) -and -not [string]::IsNullOrWhiteSpace($Quality)) {
            throw "Cannot specify both -Version and -Quality. Use -Version for a specific version or -Quality for a quality level."
        }

        # Set default quality if not specified and no version is provided
        if ([string]::IsNullOrWhiteSpace($Version) -and [string]::IsNullOrWhiteSpace($Quality)) {
            $Quality = $Script:Config.DefaultQuality
        }

        # Additional parameter validation
        if (-not [string]::IsNullOrWhiteSpace($OS) -and $OS -notin $Script:Config.SupportedOperatingSystems) {
            throw "Unsupported OS '$OS'. Supported values are: $($Script:Config.SupportedOperatingSystems -join ', ')"
        }

        if (-not [string]::IsNullOrWhiteSpace($Architecture) -and $Architecture -notin $Script:Config.SupportedArchitectures) {
            throw "Unsupported Architecture $Architecture. Supported values are: $($Script:Config.SupportedArchitectures -join ", ")"
        }

        # Validate extension installation is only allowed with dev quality
        if ($InstallExtension -and $Quality -ne "dev") {
            throw "Extension installation is only supported with -Quality dev. Current quality: $Quality"
        }

        # Determine the installation path
        $resolvedInstallPath = Get-InstallPath -InstallPath $InstallPath

        # Ensure the installation directory exists
        if (-not (Test-Path $resolvedInstallPath)) {
            Write-Message "Creating installation directory: $resolvedInstallPath" -Level Info
            if ($PSCmdlet.ShouldProcess($resolvedInstallPath, "Create installation directory")) {
                try {
                    New-Item -ItemType Directory -Path $resolvedInstallPath -Force -ErrorAction Stop | Out-Null
                }
                catch {
                    throw "Failed to create installation directory: $resolvedInstallPath - $($_.Exception.Message)"
                }
            }
        }

        # Download and install the Aspire CLI
        $targetOS = Install-AspireCli -InstallPath $resolvedInstallPath -Version $Version -Quality $Quality -OS $OS -Architecture $Architecture

        # Update PATH environment variables unless -SkipPath is specified
        if ($SkipPath) {
            Write-Message "Skipping PATH configuration due to -SkipPath flag" -Level Info
        } else {
            Update-PathEnvironment -InstallPath $resolvedInstallPath -TargetOS $targetOS
        }
    }
    catch {
        # Display clean error message without stack trace
        Write-Message "Error: $($_.Exception.Message)" -Level Error
        if ($InvokedFromFile) {
            exit 1
        } else {
            return 1
        }
    }
}

# Run main function and handle exit code
try {
    # Ensure we're not in strict mode which can cause issues in PowerShell 5.1
    if (-not $Script:IsModernPowerShell) {
        Set-StrictMode -Off
    }

    Start-AspireCliInstallation
    $exitCode = 0
}
catch {
    # Display clean error message without stack trace
    Write-Message "Error: $($_.Exception.Message)" -Level Error
    $exitCode = 1
}

if ($InvokedFromFile) {
    exit $exitCode
}
else {
    if ($exitCode -ne 0) {
        return $exitCode
    }
}

# SIG # Begin signature block
# MIInYAYJKoZIhvcNAQcCoIInUTCCJ00CAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCBQpkPb9LfmwJsv
# ccFjZR1GHRIXLQF2Fbuffjk8ZLma7KCCDLgwggXzMIID26ADAgECAhMzAAABx5qh
# 7twn4vi3AAAAAAHHMA0GCSqGSIb3DQEBCwUAMFcxCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKDAmBgNVBAMTH01pY3Jvc29mdCBD
# b2RlIFNpZ25pbmcgUENBIDIwMjQwHhcNMjYwNDE2MTg1NzM5WhcNMjcwNDE1MTg1
# NzM5WjBjMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UE
# BxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMQ0wCwYD
# VQQDEwQuTkVUMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAwHrWAGb7
# Mikb7Od1FUpCwqEwOSb3eL6xfCnU4cn4Qaeq/USe2UzFDdeCWFanFLzfIzD5UGb8
# gBO0wivb/YshMScEGBjz4QD5ILGfUbRwGVHRaGUS9Czj0MaTiJSgBsDIQONOG+iQ
# dLpqm6+rBWK4/5wVyL1JLjHprjmH66OuXmrLvtGSBikGFqm+A8szXO+0+wLOHW3u
# PYr/TmvgkkAmpYg9n4+NK9ckspb3kQfe/E+G192GbqwQrjGlwowIbuqwv90KqEZ4
# eT4hCDFN5zBnJZszJjfTk+JScZxzvbzzAc2vfondnxzkFYVam3KhxBp9NeOIyKXs
# 5LwpiaGAdiDrGQIDAQABo4IBqjCCAaYwDgYDVR0PAQH/BAQDAgeAMB8GA1UdJQQY
# MBYGCisGAQQBgjdMCAEGCCsGAQUFBwMDMB0GA1UdDgQWBBSACbR5/9Pq7K0bJtON
# 2DwMVf8vnzBUBgNVHREETTBLpEkwRzEtMCsGA1UECxMkTWljcm9zb2Z0IElyZWxh
# bmQgT3BlcmF0aW9ucyBMaW1pdGVkMRYwFAYDVQQFEw00NjQyMjMrNTA3NjA2MB8G
# A1UdIwQYMBaAFH9ZP1Qh2q1P7wXl5qPXLQaUEggxMGAGA1UdHwRZMFcwVaBToFGG
# T2h0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01pY3Jvc29mdCUy
# MENvZGUlMjBTaWduaW5nJTIwUENBJTIwMjAyNC5jcmwwbQYIKwYBBQUHAQEEYTBf
# MF0GCCsGAQUFBzAChlFodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2Nl
# cnRzL01pY3Jvc29mdCUyMENvZGUlMjBTaWduaW5nJTIwUENBJTIwMjAyNC5jcnQw
# DAYDVR0TAQH/BAIwADANBgkqhkiG9w0BAQsFAAOCAgEAibMS3+Aa35AADc0aVFlS
# /6ShNQtbFIVP+iwfVjFtUoWBJhT10D0GkUPOjWUX4iFVOCOPAdeFoDzs/hhPO2Xd
# FB+uWusQPsXTTwMeIn4b/uedPGRvvL181FNPD0Bz+EZNaRAFblVq6OaBZB+VOhOk
# wo2WpVsYWD5RVkrGL6TkyGxQuMq3kr+6UfltW6+detmZ4XddEa5KxB7ZiOk1hpge
# rvXeK3B03DJirumulDhHfGJQHDpI45ofZTIN2HJCKxmXxXfAU2Fja3rJmRALIYb4
# ZkXj1Dy9zWX5OX6lxFryX7i7dGrXxBQMl6dbhSnnL9r6YtPLEzylhFCk8OpVflT0
# 1N8tuBlYrFCaQ37SakngJGkCeIiIkkLhqDtaOTL4CyCKTzFv/XrCqLDvbEN08KTt
# M3SD8bg+WbLIM/6F1KPOWrkmJAra5t8SECqhEPW2YorhHdCogWUtmw+MTzsBeVng
# 8kfO60fZic2xRDjmgMMJ/ePq9+yUYvTUzdcCCQNeGyTI5fFvW/uh/QlHwbMK+wiQ
# 14xCZ5dzkVnCLvFcbpR42HVnhIDeomj4f4i1X90yCPEJY+Vh/OCHprYJddoSu5m+
# cunAP5wK5nFiEZggl+uxagKHgZ8C2S8bdnR8eB5+0H3RvJeuhBu6mqiEJSmlu6D1
# 1/7Y+M5o9aTSg++7/gfMVpkwgga9MIIEpaADAgECAhMzAAAAOTu2Nxm/Bh1nAAAA
# AAA5MA0GCSqGSIb3DQEBDAUAMIGIMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2Fz
# aGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENv
# cnBvcmF0aW9uMTIwMAYDVQQDEylNaWNyb3NvZnQgUm9vdCBDZXJ0aWZpY2F0ZSBB
# dXRob3JpdHkgMjAxMTAeFw0yNDA4MDgyMDU0MThaFw0zNjAzMjIyMjEzMDRaMFcx
# CzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKDAm
# BgNVBAMTH01pY3Jvc29mdCBDb2RlIFNpZ25pbmcgUENBIDIwMjQwggIiMA0GCSqG
# SIb3DQEBAQUAA4ICDwAwggIKAoICAQDYAZwe4zjHqpUWBzWtuub+CGPXx/EyoXph
# 3zyDXtYKS2ld3YYN9uFsB9Oi3B26Z7AbpAgzYra8qNHbUvxFuiP8hC/2y0mPISqW
# 30LlrrAT6/ams2HA8Qlv6p42+SbCNbPGzToN21QE70FS+LXH9N2k8nLM/EHgnTNJ
# f8h0TmyfUKmszNa+lTxDieyy/rhBG+98OkArobPPWtbr9c3qzmDJ7J3kUcAm6clt
# dSHIIFNHESgw6taY1ScyGyBevqIl120XjrIHiPM7tRckHytH1ZGsmvEplR0P7Tn9
# t5meFvZNEYttkFvad1IEguTlA5LSscXAphi+rVy3zhklhyCFeGK0yU0+jzbcuURK
# IxybmRwK5BfVZx0xEVqE4wM3yN5D/uW+GpVHYYAGe7bTrtW1Z13x2qj2Jdqz7NtI
# 4tNyzlVrIf62nYBNe3rOYS/repVdHlR61YbLLETlibs9jFzAre4sO5RTxvS1yho7
# JqJ59oKLRnRyLhIOSZyTCVZosXeS0ZZJoGEWSs4cUgsMqBiKtD4WgO2PlT3LeaQh
# 5Io3CCA5tJ5ZCvtCsnqaJXKhptE/xmEETIRyZRjjplUKKd+sFFVGJJVMvvrw1nhI
# BKOLO4cTepiG39jEiEP4iHzGYCcQuvaLpDFFwqzgt0pBP8SJIKX5dtjDNYrZGd+Z
# zV5DKJVNZQIDAQABo4IBTjCCAUowDgYDVR0PAQH/BAQDAgGGMBAGCSsGAQQBgjcV
# AQQDAgEAMB0GA1UdDgQWBBR/WT9UIdqtT+8F5eaj1y0GlBIIMTAZBgkrBgEEAYI3
# FAIEDB4KAFMAdQBiAEMAQTAPBgNVHRMBAf8EBTADAQH/MB8GA1UdIwQYMBaAFHIt
# OgIxkEO5FAVO4eqnxzHRI4k0MFoGA1UdHwRTMFEwT6BNoEuGSWh0dHA6Ly9jcmwu
# bWljcm9zb2Z0LmNvbS9wa2kvY3JsL3Byb2R1Y3RzL01pY1Jvb0NlckF1dDIwMTFf
# MjAxMV8wM18yMi5jcmwwXgYIKwYBBQUHAQEEUjBQME4GCCsGAQUFBzAChkJodHRw
# Oi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpL2NlcnRzL01pY1Jvb0NlckF1dDIwMTFf
# MjAxMV8wM18yMi5jcnQwDQYJKoZIhvcNAQEMBQADggIBABSUHzgoT+6J5+nyyDCq
# 0pTdVmCsAxYAHXcpjlDtxazPHewf1v4kOg8V7A5+w+VuMDMGHi8rLXBKn5I8+DVE
# UYGs8jLuckc0IeC6owOLUrU3CYdaKRMaO55+T7jwWJ27tPkx0rlR03tFU0z1YYpc
# v6Yhaw6N2sUPT+AvjpecnrftoE33pCAkucUvnGH0iL4J9CZLFQVTGFSOUBbv6oZy
# 4bBBRFMxvH779IY4JDvpZKVfbcuhpDeL3Z3e8mukOmkfct+GojNapsWsQYujlJ8j
# Zen5Lrp/3YkxZ2Ay06aTpK/5oOVknwog1TDQsbY+MDyguTph5tQ0CLfzDaJG2x91
# BrBT9UG87C6HLkqiwrx9PSKN3wz05rHEfWO+RuKl+0U1/AHQT6NCOjhKI39/c7hW
# bdKjh5uuWFkBOvXGTNrnhNTAdOXTTYByvYExO8yryv34PAdqo1vPDE/1heVebr2R
# ramvRUi9kWswKwPqwz7n+iRmM+B6YDGRweEurM1kimAb9FYrAs38YHlPnarl1vW3
# dGrmJTgefAz3DmCnXN0nveIPsS+KXBIWweeCToAJMGE7v/XS3h9qQ6niWQAAVQ1k
# UAml3zuS4MisCgi2F6YoK2WAo1EgXK/lXvDxVjIVU0JdL+KvCfwFJkDeVuJ9dNXG
# Ni+AOxk0BtYd9hxwL30BElj9MYIZ/jCCGfoCAQEwbjBXMQswCQYDVQQGEwJVUzEe
# MBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSgwJgYDVQQDEx9NaWNyb3Nv
# ZnQgQ29kZSBTaWduaW5nIFBDQSAyMDI0AhMzAAABx5qh7twn4vi3AAAAAAHHMA0G
# CWCGSAFlAwQCAQUAoIGuMBkGCSqGSIb3DQEJAzEMBgorBgEEAYI3AgEEMBwGCisG
# AQQBgjcCAQsxDjAMBgorBgEEAYI3AgEVMC8GCSqGSIb3DQEJBDEiBCA/KPf7OPCt
# wN4GJQ3nKyyisRxP9PXEBgAFXLhSNk+v8DBCBgorBgEEAYI3AgEMMTQwMqAUgBIA
# TQBpAGMAcgBvAHMAbwBmAHShGoAYaHR0cDovL3d3dy5taWNyb3NvZnQuY29tMA0G
# CSqGSIb3DQEBAQUABIIBAFovbHb8tPyQrMFY8lhd5Gz96jMBfKSgspM5jEBTmp3E
# dItahDgQ+JqA+7wsqIwDPD/hUvD8CBcHvmqWwJwu3Ra5P3ngZoXVwWhpA9DokqJK
# 4JhXimd3k/dfCcXz7jUzxXSJcekEYgXQjtrnR+IbOYR0945rkxBEGTaA0lcgBQkZ
# 6IGLl45ZJA8M8HMWO5Gn+VSofrlEJtlem+TmhF4ST9NVYmhLygQ3L6NklO2i+KXW
# GUbA+VWPC1j1I93GF6ibptyg2Hd/WYGz3ctcQs1b53JLJWj70LeHpuZCanbsuKmH
# Rx+NlRANK+4L6r9gR+xFHYQVH4M7hHK2JCxSZhA6fyihghewMIIXrAYKKwYBBAGC
# NwMDATGCF5wwgheYBgkqhkiG9w0BBwKggheJMIIXhQIBAzEPMA0GCWCGSAFlAwQC
# AQUAMIIBWgYLKoZIhvcNAQkQAQSgggFJBIIBRTCCAUECAQEGCisGAQQBhFkKAwEw
# MTANBglghkgBZQMEAgEFAAQgyc0KwfpfOINEHIzMGWP76zkbEzyq6R+b9377lP8R
# JA4CBmoQk+q8PBgTMjAyNjA1MjcxNjQyMzkuMTI3WjAEgAIB9KCB2aSB1jCB0zEL
# MAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1v
# bmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEtMCsGA1UECxMkTWlj
# cm9zb2Z0IElyZWxhbmQgT3BlcmF0aW9ucyBMaW1pdGVkMScwJQYDVQQLEx5uU2hp
# ZWxkIFRTUyBFU046MkQxQS0wNUUwLUQ5NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBU
# aW1lLVN0YW1wIFNlcnZpY2WgghH+MIIHKDCCBRCgAwIBAgITMwAAAhLRCAY8yhhP
# qgABAAACEjANBgkqhkiG9w0BAQsFADB8MQswCQYDVQQGEwJVUzETMBEGA1UECBMK
# V2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0
# IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQgVGltZS1TdGFtcCBQQ0Eg
# MjAxMDAeFw0yNTA4MTQxODQ4MTVaFw0yNjExMTMxODQ4MTVaMIHTMQswCQYDVQQG
# EwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwG
# A1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMS0wKwYDVQQLEyRNaWNyb3NvZnQg
# SXJlbGFuZCBPcGVyYXRpb25zIExpbWl0ZWQxJzAlBgNVBAsTHm5TaGllbGQgVFNT
# IEVTTjoyRDFBLTA1RTAtRDk0NzElMCMGA1UEAxMcTWljcm9zb2Z0IFRpbWUtU3Rh
# bXAgU2VydmljZTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAK9M06A5
# KVkLbGXpEtHFdKrg3SkkxpKL7wWmR9DgCBItDtwnDu+yYl/HOBKavbomx1WWdVvy
# 6LnxNe9r5EVzvkGDbVlqKxAidgHGUNdG6QJbZIWTYl5VSfC90M4SoK165jJJtifv
# 4PNVNtyT3DuMFxxH7aJ098KXf//d+q45sMTJuzZG7MoGyX/uAFQTDa+GjD0IQXe+
# qHdCjAelq78hBjjlNPPdzhbn0uRA3suJ+OFoGsSRNFZ79/zjr7jsOSqdSj6o42Cf
# i/csC7gTLXA3y6TUDv4dXhRKDK24hDOu0znzZV38Ww8+DJbGLy4qMYHdl83stUMa
# 1dfoviclQyTIknvYCjrs6YkEBRNfQ1D4LIncoy081xIUlSwZUaK9HglX+4AukX5P
# DWN6ztrIIDi+/b1ORbgyk4f7CDrXFB3hwuNowRgfrX3SgtSjgUflJTfWjs4PJqVD
# SNhYKkL4q1T/aaW3jFH76dsAPb6Mk4kVrw4MwsaPMZSdZ7HGExyEK5pBfY/wmtCA
# 5rfH7zp+uJ55SThlGGWBzAtZdMbYJGExNRElKqpGsCpO4qm8XZy8snEvnUfs2sT7
# nTAy60Bc/JYH8vaG5NA/Bwtnc4VqWDZ+YXZKDxM4AqkFDqfL90I/7HeGp8rqXuSq
# ApIwATj98oUkOFvWfg6yZ4TP627Fwu6E79unAgMBAAGjggFJMIIBRTAdBgNVHQ4E
# FgQUUgW5UsW+XGCmDsL3f1X6Fzn7t94wHwYDVR0jBBgwFoAUn6cVXQBeYl2D9OXS
# ZacbUzUZ6XIwXwYDVR0fBFgwVjBUoFKgUIZOaHR0cDovL3d3dy5taWNyb3NvZnQu
# Y29tL3BraW9wcy9jcmwvTWljcm9zb2Z0JTIwVGltZS1TdGFtcCUyMFBDQSUyMDIw
# MTAoMSkuY3JsMGwGCCsGAQUFBwEBBGAwXjBcBggrBgEFBQcwAoZQaHR0cDovL3d3
# dy5taWNyb3NvZnQuY29tL3BraW9wcy9jZXJ0cy9NaWNyb3NvZnQlMjBUaW1lLVN0
# YW1wJTIwUENBJTIwMjAxMCgxKS5jcnQwDAYDVR0TAQH/BAIwADAWBgNVHSUBAf8E
# DDAKBggrBgEFBQcDCDAOBgNVHQ8BAf8EBAMCB4AwDQYJKoZIhvcNAQELBQADggIB
# AF50gJJl+/UXO5cUtcDqM1ye1dKLuQ57VXDiaZSJA47xDfASEKYeFPoEji2blUXj
# /8/WHVIlJHD8C9TzNuOf5BF9RHoKcTXPBwwOoSgh4NACzRHLsZxjpYCi6JF31Hq9
# Q+YYlvDwATPUfp3orXk4D2mkZSRbxk2L6LXNLqhohEuYTEIS/ETRYaUvUXCFh7Z0
# BhJ63TlLwgIbOBXlYmsHJi0yr/tfO9HPzHkx4tEA3Xkfu/1oOKoZCdpNYhByXZmH
# /KyFdDUQWXHUU03R3nt+Ulz+Z3jQnwJIwyLmcnEbneo8zywjS4jWMxlwbMMycoI+
# BHkjkU+8DL2hbinkQF6Fwt8MGL4CLCNc59wxmOtuWPsmUovFDUjR7q4t+mb/WvkO
# LycA6WCt+ktEwdqX+8S4oh99p5O2Cu90YPfFun2diDbs2M2exoYL3335I+BFF4NR
# NBH32NaRKpG6Q0z+4fwwarc6D17MsNjFIfu8r1nKtgRmUrnGugmOl+IqDLnOT1qb
# JrzjpYuwETQOwG/JCnQnNoDQy2nIJbMHmRHPf1UAeoZbP2+ipN9p5MLhxMSpWnqE
# laygeVPcZadfnPCf+xiY+EcOwIkPLXKflpn8g/CsV9kJSmw4uElI54Jb+Ote0fPm
# v3A1icmjLfNu/Vp+39sjHnTe5HxiEOUmY+ukXYXZWTqvMIIHcTCCBVmgAwIBAgIT
# MwAAABXF52ueAptJmQAAAAAAFTANBgkqhkiG9w0BAQsFADCBiDELMAkGA1UEBhMC
# VVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNV
# BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFJv
# b3QgQ2VydGlmaWNhdGUgQXV0aG9yaXR5IDIwMTAwHhcNMjEwOTMwMTgyMjI1WhcN
# MzAwOTMwMTgzMjI1WjB8MQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3Rv
# bjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0
# aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQgVGltZS1TdGFtcCBQQ0EgMjAxMDCCAiIw
# DQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAOThpkzntHIhC3miy9ckeb0O1YLT
# /e6cBwfSqWxOdcjKNVf2AX9sSuDivbk+F2Az/1xPx2b3lVNxWuJ+Slr+uDZnhUYj
# DLWNE893MsAQGOhgfWpSg0S3po5GawcU88V29YZQ3MFEyHFcUTE3oAo4bo3t1w/Y
# JlN8OWECesSq/XJprx2rrPY2vjUmZNqYO7oaezOtgFt+jBAcnVL+tuhiJdxqD89d
# 9P6OU8/W7IVWTe/dvI2k45GPsjksUZzpcGkNyjYtcI4xyDUoveO0hyTD4MmPfrVU
# j9z6BVWYbWg7mka97aSueik3rMvrg0XnRm7KMtXAhjBcTyziYrLNueKNiOSWrAFK
# u75xqRdbZ2De+JKRHh09/SDPc31BmkZ1zcRfNN0Sidb9pSB9fvzZnkXftnIv231f
# gLrbqn427DZM9ituqBJR6L8FA6PRc6ZNN3SUHDSCD/AQ8rdHGO2n6Jl8P0zbr17C
# 89XYcz1DTsEzOUyOArxCaC4Q6oRRRuLRvWoYWmEBc8pnol7XKHYC4jMYctenIPDC
# +hIK12NvDMk2ZItboKaDIV1fMHSRlJTYuVD5C4lh8zYGNRiER9vcG9H9stQcxWv2
# XFJRXRLbJbqvUAV6bMURHXLvjflSxIUXk8A8FdsaN8cIFRg/eKtFtvUeh17aj54W
# cmnGrnu3tz5q4i6tAgMBAAGjggHdMIIB2TASBgkrBgEEAYI3FQEEBQIDAQABMCMG
# CSsGAQQBgjcVAgQWBBQqp1L+ZMSavoKRPEY1Kc8Q/y8E7jAdBgNVHQ4EFgQUn6cV
# XQBeYl2D9OXSZacbUzUZ6XIwXAYDVR0gBFUwUzBRBgwrBgEEAYI3TIN9AQEwQTA/
# BggrBgEFBQcCARYzaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9Eb2Nz
# L1JlcG9zaXRvcnkuaHRtMBMGA1UdJQQMMAoGCCsGAQUFBwMIMBkGCSsGAQQBgjcU
# AgQMHgoAUwB1AGIAQwBBMAsGA1UdDwQEAwIBhjAPBgNVHRMBAf8EBTADAQH/MB8G
# A1UdIwQYMBaAFNX2VsuP6KJcYmjRPZSQW9fOmhjEMFYGA1UdHwRPME0wS6BJoEeG
# RWh0dHA6Ly9jcmwubWljcm9zb2Z0LmNvbS9wa2kvY3JsL3Byb2R1Y3RzL01pY1Jv
# b0NlckF1dF8yMDEwLTA2LTIzLmNybDBaBggrBgEFBQcBAQROMEwwSgYIKwYBBQUH
# MAKGPmh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2kvY2VydHMvTWljUm9vQ2Vy
# QXV0XzIwMTAtMDYtMjMuY3J0MA0GCSqGSIb3DQEBCwUAA4ICAQCdVX38Kq3hLB9n
# ATEkW+Geckv8qW/qXBS2Pk5HZHixBpOXPTEztTnXwnE2P9pkbHzQdTltuw8x5MKP
# +2zRoZQYIu7pZmc6U03dmLq2HnjYNi6cqYJWAAOwBb6J6Gngugnue99qb74py27Y
# P0h1AdkY3m2CDPVtI1TkeFN1JFe53Z/zjj3G82jfZfakVqr3lbYoVSfQJL1AoL8Z
# thISEV09J+BAljis9/kpicO8F7BUhUKz/AyeixmJ5/ALaoHCgRlCGVJ1ijbCHcNh
# cy4sa3tuPywJeBTpkbKpW99Jo3QMvOyRgNI95ko+ZjtPu4b6MhrZlvSP9pEB9s7G
# dP32THJvEKt1MMU0sHrYUP4KWN1APMdUbZ1jdEgssU5HLcEUBHG/ZPkkvnNtyo4J
# vbMBV0lUZNlz138eW0QBjloZkWsNn6Qo3GcZKCS6OEuabvshVGtqRRFHqfG3rsjo
# iV5PndLQTHa1V1QJsWkBRH58oWFsc/4Ku+xBZj1p/cvBQUl+fpO+y/g75LcVv7TO
# PqUxUYS8vwLBgqJ7Fx0ViY1w/ue10CgaiQuPNtq6TPmb/wrpNPgkNWcr4A245oyZ
# 1uEi6vAnQj0llOZ0dFtq0Z4+7X6gMTN9vMvpe784cETRkPHIqzqKOghif9lwY1NN
# je6CbaUFEMFxBmoQtB1VM1izoXBm8qGCA1kwggJBAgEBMIIBAaGB2aSB1jCB0zEL
# MAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1v
# bmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEtMCsGA1UECxMkTWlj
# cm9zb2Z0IElyZWxhbmQgT3BlcmF0aW9ucyBMaW1pdGVkMScwJQYDVQQLEx5uU2hp
# ZWxkIFRTUyBFU046MkQxQS0wNUUwLUQ5NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBU
# aW1lLVN0YW1wIFNlcnZpY2WiIwoBATAHBgUrDgMCGgMVAOVRwa+IdNBDe41HUN90
# hPqm5P/AoIGDMIGApH4wfDELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0
# b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3Jh
# dGlvbjEmMCQGA1UEAxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIwMTAwDQYJ
# KoZIhvcNAQELBQACBQDtwQC2MCIYDzIwMjYwNTI3MDUzMzQyWhgPMjAyNjA1Mjgw
# NTMzNDJaMHcwPQYKKwYBBAGEWQoEATEvMC0wCgIFAO3BALYCAQAwCgIBAAICCZcC
# Af8wBwIBAAICE/cwCgIFAO3CUjYCAQAwNgYKKwYBBAGEWQoEAjEoMCYwDAYKKwYB
# BAGEWQoDAqAKMAgCAQACAwehIKEKMAgCAQACAwGGoDANBgkqhkiG9w0BAQsFAAOC
# AQEAYGm+s7y5dqBhkJnA0XzgHTz46oVsRmhSyD2c9DwqLDMdX1mx3YoREIIQ4fzj
# dxZZkKh/CMo+N+2oAkJLfktND6E5HxsuwxdBxNTdb7YkxVpGHpDbFIXEONK9hLx2
# kKry1IDNStF2eHhxg2YhnWjuGRUvtYMJ03js4O/WXyAWIRQp2HWyJHRYu6gDhpk6
# gaTnZcGBpgmF+Ov5q3qk2HD79VaJK9f2XmQgaNg/qKO7yGttM1mMmQy3uMiaIhdF
# e558iFQDv8WfdTCMtcc4sep9W2i9g24uBhk9f6IyjB2EFEq3lr6p4PsiLtPPA0u+
# uTbtfD3Kh7wD8Ai1zNb1EAHBLzGCBA0wggQJAgEBMIGTMHwxCzAJBgNVBAYTAlVT
# MRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQK
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29mdCBUaW1l
# LVN0YW1wIFBDQSAyMDEwAhMzAAACEtEIBjzKGE+qAAEAAAISMA0GCWCGSAFlAwQC
# AQUAoIIBSjAaBgkqhkiG9w0BCQMxDQYLKoZIhvcNAQkQAQQwLwYJKoZIhvcNAQkE
# MSIEIBagI4woHs9ubxEE5SkgPpOWjsjeSHLvoPXHcl0F+6TtMIH6BgsqhkiG9w0B
# CRACLzGB6jCB5zCB5DCBvQQgc/l+Rrzu1p4JJx+AWXmFPgcwS9ScuaCfMFZUzSMX
# 2IMwgZgwgYCkfjB8MQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQ
# MA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9u
# MSYwJAYDVQQDEx1NaWNyb3NvZnQgVGltZS1TdGFtcCBQQ0EgMjAxMAITMwAAAhLR
# CAY8yhhPqgABAAACEjAiBCAM4H53Myd8KlBCaj4i8iuuS4lDBrCcDl8NWTSq1KKz
# 0DANBgkqhkiG9w0BAQsFAASCAgA6pxxWQXEugxQZMSG4JZFW4rLbehpDjwKdSaJp
# Zepo44nay2orKb1G5Ob10Yr8dRIPQtDryRIsryaqsGq3oGkD3y6uXY0z8oq/ZpXt
# DRaUUwoeo14QdZb79GoR82aICPrcK21UfnVnBqHhLFN+MrvGeQ7UNVSbvUKe+OQl
# xTAISGFZAipBjg9s5Dkk17gIBdVX2G1BtUHAOFdvLkgObtgNyJD7r6sTN/khsUtF
# DeNohc2VsmZRFME4jS1KjYRIw6mvjj/d8t3gAmb16VRY+qkg2sQ70Wc5VtYrhvvI
# HYgCI5kTR2fA6zHNXH0/8slpfwID2ZeKVBSWePnyC9obG6SlINXxnnajo26X+Z/c
# NQG+ABxDf5mrMHBSNX7d0bYKmtGCDm2fWaUPZuEL2Vwv2MUZj4XT8xoSdx5GU93r
# hbgaq2tZxWBuo386nGT6EgV24RFKGhEvvlP/Fgrsvl8Fg2D07+UeECtTeekRIRZU
# KBCClYOcqwzkwhbYl5BMUsMlyzDW0OqNFdl2b5kG+lfVKt85FCr6pndRfp6fuoPS
# FYDYaOWf8JbI91mGGFcV6LUmVORVkNgvaAhE64YotpOzKL6P9fuJfIAOmg6XNW85
# yti0tQXJRY9+7pcZoH0fsej6gT3kJ5WTRVs848II/JTW3qcbippta4x9ft7h2T6E
# aF7ISg==
# SIG # End signature block
