#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ReceiptPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-OptionalProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $taskProperty = $Object.PSObject.Properties[$Name]
    if ($null -eq $taskProperty) { return $null }
    return $taskProperty.Value
}

function Write-SafeFailure {
    param([string]$Part, [System.Exception]$Failure)
    $taskLeaf = $Failure
    for ($taskIndex = 0; $taskIndex -lt 16 -and $null -ne $taskLeaf.InnerException; $taskIndex++) {
        $taskLeaf = $taskLeaf.InnerException
    }
    [pscustomobject]@{ Part = $Part; Result = 'Unverified'; ExceptionType = $taskLeaf.GetType().Name;
        HResult = ('0x{0:X8}' -f $taskLeaf.HResult) } | Format-List
}

function Assert-LocalPath {
    param([string]$Value)
    if ($Value -notmatch '^[A-Za-z]:\\' -or $Value.Substring(2).Contains(':') -or
        $Value -match '[\x00-\x1F]' -or $Value.Contains('"')) {
        throw [System.ArgumentException]::new('LOCAL_PATH_REQUIRED')
    }
    $taskFull = [IO.Path]::GetFullPath($Value)
    if (-not [string]::Equals($taskFull.TrimEnd('\'), $Value.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw [System.ArgumentException]::new('CANONICAL_PATH_REQUIRED')
    }
    for ($taskCurrent = $taskFull; -not [string]::IsNullOrEmpty($taskCurrent); $taskCurrent = [IO.Path]::GetDirectoryName($taskCurrent)) {
        if (Test-Path -LiteralPath $taskCurrent) {
            $taskItem = Get-Item -LiteralPath $taskCurrent -Force
            if (($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw [System.ArgumentException]::new('REPARSE_PATH_REJECTED')
            }
        }
    }
}

function Read-BoundedJson {
    param([string]$Value)
    Assert-LocalPath $Value
    $taskItem = Get-Item -LiteralPath $Value -Force
    if ($taskItem.PSIsContainer -or $taskItem.Length -gt 2097152) {
        throw [System.ArgumentException]::new('JSON_INPUT_INVALID')
    }
    return (Get-Content -LiteralPath $Value -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Resolve-RuntimePath {
    param([string]$Root, [string]$Relative)
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Length -ge 240 -or
        $Relative.Contains('\') -or $Relative.StartsWith('/') -or $Relative -match '[\x00-\x1F<>:"|?*]') {
        throw [System.ArgumentException]::new('RUNTIME_PATH_INVALID')
    }
    $taskParts = @($Relative.Split('/'))
    foreach ($taskPart in $taskParts) {
        if ($taskPart -in @('', '.', '..', 'data', 'logs') -or $taskPart.EndsWith('.') -or $taskPart.EndsWith(' ') -or
            $taskPart -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') {
            throw [System.ArgumentException]::new('RUNTIME_PATH_INVALID')
        }
    }
    $taskName = $taskParts[-1]
    if ($taskName -match '^(appsettings|NLog\.config$)' -or $taskName -match '\.(pfx|p12|key|pem|snk)$') {
        throw [System.ArgumentException]::new('PROTECTED_FILE_REJECTED')
    }
    $taskResolved = [IO.Path]::GetFullPath((Join-Path $Root $Relative.Replace('/', '\')))
    if (-not $taskResolved.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw [System.ArgumentException]::new('RUNTIME_PATH_INVALID')
    }
    Assert-LocalPath $taskResolved
    return $taskResolved
}

function Get-RelativeRuntimePath {
    param([string]$Root, [string]$Value)
    if (-not $Value.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw [System.ArgumentException]::new('RECORDED_PATH_OUTSIDE_COMPONENT')
    }
    $taskRelative = $Value.Substring($Root.TrimEnd('\').Length + 1).Replace('\', '/')
    [void](Resolve-RuntimePath $Root $taskRelative)
    return $taskRelative
}

function Get-AclObservation {
    param([string]$Value, [string]$RecordedSddl)
    Assert-LocalPath $Value
    if (-not (Test-Path -LiteralPath $Value)) { return $null }
    $taskAcl = Get-Acl -LiteralPath $Value
    $taskSections = [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor [Security.AccessControl.AccessControlSections]::Access
    $taskActual = [Security.AccessControl.RawSecurityDescriptor]::new($taskAcl.GetSecurityDescriptorSddlForm($taskSections))
    $taskExpected = $null
    if (-not [string]::IsNullOrEmpty($RecordedSddl)) { $taskExpected = [Security.AccessControl.RawSecurityDescriptor]::new($RecordedSddl) }
    $taskDaclMatches = $null
    if ($null -ne $taskExpected -and $null -ne $taskExpected.DiscretionaryAcl -and $null -ne $taskActual.DiscretionaryAcl) {
        $taskOldBytes = [byte[]]::new($taskExpected.DiscretionaryAcl.BinaryLength)
        $taskNewBytes = [byte[]]::new($taskActual.DiscretionaryAcl.BinaryLength)
        $taskExpected.DiscretionaryAcl.GetBinaryForm($taskOldBytes, 0)
        $taskActual.DiscretionaryAcl.GetBinaryForm($taskNewBytes, 0)
        $taskDaclMatches = ([Convert]::ToBase64String($taskOldBytes) -ceq [Convert]::ToBase64String($taskNewBytes))
    }
    return [pscustomobject]@{
        OwnerSid = $(if ($null -ne $taskActual.Owner) { $taskActual.Owner.Value } else { $null })
        GroupSid = $(if ($null -ne $taskActual.Group) { $taskActual.Group.Value } else { $null })
        DaclProtected = $taskAcl.AreAccessRulesProtected
        RecordedOwnerSid = $(if ($null -ne $taskExpected -and $null -ne $taskExpected.Owner) { $taskExpected.Owner.Value } else { $null })
        RecordedGroupSid = $(if ($null -ne $taskExpected -and $null -ne $taskExpected.Group) { $taskExpected.Group.Value } else { $null })
        OwnerMatchesRecord = $(if ($null -ne $taskExpected) { [object]::Equals($taskActual.Owner, $taskExpected.Owner) } else { $null })
        GroupMatchesRecord = $(if ($null -ne $taskExpected) { [object]::Equals($taskActual.Group, $taskExpected.Group) } else { $null })
        DaclBytesMatchRecord = $taskDaclMatches
        ProtectedMatchesRecord = $(if ($null -ne $taskExpected) {
            ($taskActual.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -eq
            ($taskExpected.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected)
        } else { $null })
    }
}

function Get-ReadOnlyFlag {
    param([string]$Value)
    Assert-LocalPath $Value
    if (-not (Test-Path -LiteralPath $Value -PathType Leaf)) { return $null }
    return (((Get-Item -LiteralPath $Value -Force).Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0)
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { Write-Output 'WINDOWS_REQUIRED'; exit 1 }

try {
    $taskReceipt = Read-BoundedJson $ReceiptPath
    $taskComponent = [string](Read-OptionalProperty $taskReceipt 'component')
    $taskId = [guid]::Empty
    if ((Read-OptionalProperty $taskReceipt 'version') -ne 1 -or $taskComponent -notin @('Agent', 'Console') -or
        (Read-OptionalProperty $taskReceipt 'mode') -cne 'Update' -or
        -not [guid]::TryParse([string](Read-OptionalProperty $taskReceipt 'id'), [ref]$taskId) -or $taskId -eq [guid]::Empty -or
        [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($ReceiptPath)) -ine $taskId.ToString('N')) {
        throw [System.ArgumentException]::new('UPDATE_RECEIPT_INVALID')
    }
    [pscustomobject]@{ Part = 'Receipt'; DiagnosticVersion = '20261005.2'; Component = $taskComponent; State = Read-OptionalProperty $taskReceipt 'state';
        Code = Read-OptionalProperty $taskReceipt 'code'; FailureStage = Read-OptionalProperty $taskReceipt 'failureStage';
        ExceptionType = Read-OptionalProperty $taskReceipt 'exceptionType'; HResult = Read-OptionalProperty $taskReceipt 'hresult' } | Format-List
    $taskBefore = Read-OptionalProperty $taskReceipt 'beforeService'
    $taskMatch = [regex]::Match([string](Read-OptionalProperty $taskBefore 'binaryPath'),
        '^"(?<exe>[A-Za-z]:\\[^"]+)" --contentRoot "(?<root>[A-Za-z]:\\[^"]+)" --environment Production$')
    $taskRoot = $taskMatch.Groups['root'].Value
    if (-not $taskMatch.Success -or -not [string]::Equals($taskMatch.Groups['exe'].Value,
        (Join-Path $taskRoot ('IISManager.' + $taskComponent + '.exe')), [StringComparison]::OrdinalIgnoreCase)) {
        throw [System.ArgumentException]::new('SERVICE_PATH_UNVERIFIED')
    }
    Assert-LocalPath $taskRoot
    $taskReceiptDirectory = [IO.Path]::GetDirectoryName($ReceiptPath)
    $taskStage = Join-Path $taskReceiptDirectory 'stage'
    $taskBackup = Join-Path $taskReceiptDirectory 'runtime-backup'
    $taskOld = @{}; $taskNew = @{}
    $taskBackups = @(Read-OptionalProperty $taskReceipt 'runtimeBackups' | Where-Object { $null -ne $_ })
    if ($taskBackups.Count -lt 1 -or $taskBackups.Count -gt 4096) {
        throw [System.ArgumentException]::new('RUNTIME_RECORDS_UNVERIFIED')
    }
    foreach ($taskFile in $taskBackups) {
        $taskRelative = Get-RelativeRuntimePath $taskRoot ([string](Read-OptionalProperty $taskFile 'path'))
        if ($taskOld.ContainsKey($taskRelative) -or [string](Read-OptionalProperty $taskFile 'sha256') -notmatch '^[0-9A-Fa-f]{64}$') {
            throw [System.ArgumentException]::new('RUNTIME_RECORD_INVALID')
        }
        $taskOld[$taskRelative] = $taskFile
    }
    $taskPermissions = @(Read-OptionalProperty $taskReceipt 'permissions' | Where-Object { $null -ne $_ })
    [pscustomobject]@{ Part = 'RuntimeRecords';
        RuntimeRecordsBefore = $taskOld.Count; PermissionRecords = $taskPermissions.Count } | Format-List
}
catch { Write-SafeFailure 'Inputs' $_.Exception; exit 1 }

try {
    $taskServiceName = 'IISManager.' + $taskComponent
    $taskService = Get-CimInstance -ClassName Win32_Service -Filter ("Name = '" + $taskServiceName + "'")
    [pscustomobject]@{ Part = 'Service'; Name = $taskServiceName; Status = Read-OptionalProperty $taskService 'State';
        RecordedStateBefore = Read-OptionalProperty $taskBefore 'state'; ExitCode = Read-OptionalProperty $taskService 'ExitCode';
        ProcessId = Read-OptionalProperty $taskService 'ProcessId'; StartMode = Read-OptionalProperty $taskService 'StartMode';
        BinaryPathMatchesRecord = ([string](Read-OptionalProperty $taskService 'PathName') -ceq [string](Read-OptionalProperty $taskBefore 'binaryPath'));
        AccountMatchesRecord = ([string](Read-OptionalProperty $taskService 'StartName') -ceq [string](Read-OptionalProperty $taskBefore 'account')) } | Format-List
}
catch { Write-SafeFailure 'ServiceRead' $_.Exception }

try {
    $taskMarker = Read-BoundedJson (Join-Path $taskRoot '.iismanager-install.json')
    [pscustomobject]@{ Part = 'InstallationMarker'; Component = Read-OptionalProperty $taskMarker 'component';
        State = Read-OptionalProperty $taskMarker 'state'; GenerationMatchesReceipt = ([string](Read-OptionalProperty $taskMarker 'generation') -ieq $taskId.ToString('D')) } | Format-List
}
catch { Write-SafeFailure 'InstallationMarkerRead' $_.Exception }

# ExtractTo writes Manifest.Files only, not the embedded ZIP manifest. Observe
# staged runtime bytes directly. This is diagnostic evidence, not proof that
# the stage still matches the original, verified installer payload.
$taskStageScanComplete = $false
$taskStageProtectedSkipped = 0
try {
    Assert-LocalPath $taskStage
    if (-not (Test-Path -LiteralPath $taskStage -PathType Container)) {
        throw [System.ArgumentException]::new('STAGE_DIRECTORY_MISSING')
    }
    $taskPendingDirectories = [Collections.Generic.Stack[string]]::new()
    $taskPendingDirectories.Push($taskStage)
    $taskStageEntries = 0
    while ($taskPendingDirectories.Count -gt 0) {
        $taskDirectory = $taskPendingDirectories.Pop()
        Assert-LocalPath $taskDirectory
        foreach ($taskFile in @(Get-ChildItem -LiteralPath $taskDirectory -Force)) {
            $taskStageEntries++
            if ($taskStageEntries -gt 8192) { throw [System.ArgumentException]::new('STAGE_ENTRY_LIMIT') }
            if (($taskFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw [System.ArgumentException]::new('STAGE_REPARSE_POINT_REJECTED')
            }
            if ($taskFile.PSIsContainer) {
                if ($taskFile.Name -notin @('data', 'logs')) { $taskPendingDirectories.Push($taskFile.FullName) }
                continue
            }
            $taskRelative = $taskFile.FullName.Substring($taskStage.Length + 1).Replace('\', '/')
            if ($taskFile.Name -match '^(appsettings|NLog\.config$|payload-manifest\.json$)' -or
                $taskFile.Name -match '\.(pfx|p12|key|pem|snk)$') {
                $taskStageProtectedSkipped++
                continue
            }
            [void](Resolve-RuntimePath $taskRoot $taskRelative)
            if ($taskNew.ContainsKey($taskRelative) -or $taskNew.Count -ge 4096 -or $taskFile.Length -gt 268435456) {
                throw [System.ArgumentException]::new('STAGED_RUNTIME_INVALID')
            }
            $taskStageHash = Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256
            $taskNew[$taskRelative] = [pscustomobject]@{ path = $taskRelative; sha256 = $taskStageHash.Hash; size = $taskFile.Length }
        }
    }
    $taskStageScanComplete = $true
}
catch { Write-SafeFailure 'StagedRuntimeRead' $_.Exception }
[pscustomobject]@{ Part = 'StagedRuntime'; FilesObserved = $taskNew.Count; ScanComplete = $taskStageScanComplete;
    ProtectedFilesNotRead = $taskStageProtectedSkipped; OriginalPayloadManifestVerified = $false;
    ComparisonBasis = 'Observed staged bytes; not an authorization to copy or resume' } | Format-List

$taskRelevantParents = @{}
$taskRelevantParents[$taskRoot] = $true
foreach ($taskAccess in @($taskPermissions | Select-Object -Last 2)) {
    try {
        $taskTarget = [string](Read-OptionalProperty $taskAccess 'path')
        $taskRelative = Get-RelativeRuntimePath $taskRoot $taskTarget
        $taskParent = [IO.Path]::GetDirectoryName($taskTarget); $taskRelevantParents[$taskParent] = $true
        [pscustomobject]@{ Part = 'LastPermissionRecord'; File = $taskRelative;
            ReadBackVerified = Read-OptionalProperty $taskAccess 'readBackVerified';
            CurrentFileReadOnly = Get-ReadOnlyFlag $taskTarget;
            CurrentAcl = Get-AclObservation $taskTarget ([string](Read-OptionalProperty $taskAccess 'beforeSddl'));
            ParentAcl = Get-AclObservation $taskParent $(if ($taskOld.ContainsKey($taskRelative)) {
                [string](Read-OptionalProperty $taskOld[$taskRelative] 'parentSddl')
            } else { '' }) } | Format-List
    }
    catch { Write-SafeFailure 'LastPermissionRead' $_.Exception }
}

$taskCounts = @{ OldAndStaged = 0; OldOnly = 0; StagedOnly = 0; Neither = 0; Missing = 0; Unreadable = 0; ReadOnlyFiles = 0; BackupVerified = 0; BackupUnverified = 0 }
$taskDetails = [Collections.Generic.List[object]]::new()
foreach ($taskRelative in @(@($taskOld.Keys) + @($taskNew.Keys) | Sort-Object -Unique)) {
    try {
        $taskTarget = Resolve-RuntimePath $taskRoot $taskRelative
        $taskReadOnly = Get-ReadOnlyFlag $taskTarget
        if ($taskReadOnly -eq $true) { $taskCounts.ReadOnlyFiles++ }
        $taskMatchesOld = $false; $taskMatchesNew = $false; $taskClassification = 'Missing'
        if (Test-Path -LiteralPath $taskTarget -PathType Leaf) {
            $taskActual = Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256
            $taskMatchesOld = $taskOld.ContainsKey($taskRelative) -and $taskActual.Hash -ieq [string](Read-OptionalProperty $taskOld[$taskRelative] 'sha256')
            $taskMatchesNew = $taskNew.ContainsKey($taskRelative) -and $taskActual.Hash -ieq [string](Read-OptionalProperty $taskNew[$taskRelative] 'sha256')
            $taskClassification = $(if ($taskMatchesOld -and $taskMatchesNew) { 'OldAndStaged' } elseif ($taskMatchesOld) { 'OldOnly' }
                elseif ($taskMatchesNew) { 'StagedOnly' } else { 'Neither' })
        }
        $taskCounts[$taskClassification]++
        if (($taskClassification -in @('OldOnly', 'Missing', 'Neither') -or $taskReadOnly -eq $true) -and $taskDetails.Count -lt 16) {
            $taskDetails.Add([pscustomobject]@{ File = $taskRelative; Result = $taskClassification; ReadOnly = $taskReadOnly })
            $taskRelevantParents[[IO.Path]::GetDirectoryName($taskTarget)] = $true
        }
    }
    catch { $taskCounts.Unreadable++; Write-SafeFailure ('RuntimeRead:' + $taskRelative) $_.Exception }
    if ($taskOld.ContainsKey($taskRelative)) {
        try {
            $taskBackupFile = Resolve-RuntimePath $taskBackup $taskRelative
            if ((Test-Path -LiteralPath $taskBackupFile -PathType Leaf) -and
                (Get-FileHash -LiteralPath $taskBackupFile -Algorithm SHA256).Hash -ieq [string](Read-OptionalProperty $taskOld[$taskRelative] 'sha256')) {
                $taskCounts.BackupVerified++
            } else { $taskCounts.BackupUnverified++ }
        }
        catch { $taskCounts.BackupUnverified++; Write-SafeFailure ('BackupRead:' + $taskRelative) $_.Exception }
    }
}
[pscustomobject]@{ Part = 'RuntimeSummary'; OldAndStaged = $taskCounts.OldAndStaged; OldOnly = $taskCounts.OldOnly; StagedOnly = $taskCounts.StagedOnly;
    Neither = $taskCounts.Neither; Missing = $taskCounts.Missing; Unreadable = $taskCounts.Unreadable; ReadOnlyFiles = $taskCounts.ReadOnlyFiles;
    BackupVerified = $taskCounts.BackupVerified; BackupUnverified = $taskCounts.BackupUnverified } | Format-List
if ($taskDetails.Count -gt 0) { $taskDetails.ToArray() | Format-Table -AutoSize }

foreach ($taskParent in @($taskRelevantParents.Keys | Sort-Object)) {
    try {
        Assert-LocalPath $taskParent
        if (-not (Test-Path -LiteralPath $taskParent -PathType Container)) { continue }
        foreach ($taskTemporary in @(Get-ChildItem -LiteralPath $taskParent -Filter '*.setup-tmp' -Force | Select-Object -First 8)) {
            Assert-LocalPath $taskTemporary.FullName
            [pscustomobject]@{ Part = 'TemporaryFile'; File = $taskTemporary.FullName.Substring($taskRoot.Length).TrimStart('\');
                ReadOnly = (($taskTemporary.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0);
                Acl = Get-AclObservation $taskTemporary.FullName '' } | Format-List
        }
    }
    catch { Write-SafeFailure 'TemporaryRead' $_.Exception }
}
Write-Output 'READ_ONLY_COMPLETE; NO_FILES_SERVICES_PERMISSIONS_OR_MARKERS_CHANGED'
# Reads only fixed component service metadata, receipt, staged runtime bytes,
# runtime hashes, restricted backups, and Owner/Group/DACL. No configuration or
# credential content, SACL, raw exception text, or full ACL is printed. A DACL
# byte difference is evidence for review, not an instruction to reset permissions.
