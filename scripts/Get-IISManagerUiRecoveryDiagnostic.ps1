#Requires -Version 5.1
# Read-only: run from the directory containing this script in Windows PowerShell.
# .\Get-IISManagerUiRecoveryDiagnostic.ps1 -RecoveryReceiptPath 'C:\ProgramData\IISManager.Setup\receipts\<associated-id>\receipt.json'
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RecoveryReceiptPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:taskFailures = 0

function Read-Property {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $taskProperty = $Object.PSObject.Properties[$Name]
    if ($null -eq $taskProperty) { return $null }
    return $taskProperty.Value
}

function Write-SafeFailure {
    param([string]$Part, [System.Exception]$Failure)
    $script:taskFailures++
    $taskLeaf = $Failure
    for ($taskIndex = 0; $taskIndex -lt 16 -and $null -ne $taskLeaf.InnerException; $taskIndex++) {
        $taskLeaf = $taskLeaf.InnerException
    }
    [pscustomobject]@{ Part = $Part; Result = 'Unverified'; ExceptionType = $taskLeaf.GetType().Name;
        HResult = ('0x{0:X8}' -f $taskLeaf.HResult) } | Format-List
}

function Read-SafeIdentifier {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ([string]$Value -cmatch '^[A-Za-z][A-Za-z0-9_]{0,63}$') { return [string]$Value }
    return 'Unverified'
}

function Read-Id {
    param($Value)
    $taskGuid = [guid]::Empty
    if (-not [guid]::TryParse([string]$Value, [ref]$taskGuid) -or $taskGuid -eq [guid]::Empty) {
        throw [ArgumentException]::new('ID_INVALID')
    }
    return $taskGuid
}

function Test-Under {
    param([string]$Value, [string]$Root)
    return [string]::Equals($Value, $Root, [StringComparison]::OrdinalIgnoreCase) -or
        $Value.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-LocalPath {
    param([string]$Value)
    if ($Value -notmatch '^[A-Za-z]:\\' -or $Value.Length -gt 1024 -or
        $Value.Substring(2).Contains(':') -or $Value -match '[\x00-\x1F<>"|?*]') {
        throw [ArgumentException]::new('LOCAL_PATH_REQUIRED')
    }
    $taskFull = [IO.Path]::GetFullPath($Value)
    if (-not [string]::Equals($taskFull.TrimEnd('\'), $Value.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw [ArgumentException]::new('CANONICAL_PATH_REQUIRED')
    }
    for ($taskCurrent = $taskFull; -not [string]::IsNullOrEmpty($taskCurrent); $taskCurrent = [IO.Path]::GetDirectoryName($taskCurrent)) {
        if (Test-Path -LiteralPath $taskCurrent) {
            if (((Get-Item -LiteralPath $taskCurrent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw [ArgumentException]::new('REPARSE_PATH_REJECTED')
            }
        }
    }
}

function Read-BoundedJson {
    param([string]$Value)
    Assert-LocalPath $Value
    $taskItem = Get-Item -LiteralPath $Value -Force
    if ($taskItem.PSIsContainer -or $taskItem.Length -gt 4194304) {
        throw [ArgumentException]::new('JSON_INPUT_INVALID')
    }
    return (Get-Content -LiteralPath $Value -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Read-Receipt {
    param([string]$Value)
    Assert-LocalPath $Value
    $taskDirectory = [IO.Path]::GetDirectoryName($Value)
    $taskGuid = Read-Id ([IO.Path]::GetFileName($taskDirectory))
    if ([IO.Path]::GetFileName($Value) -cne 'receipt.json' -or
        [IO.Path]::GetFileName($taskDirectory) -cne $taskGuid.ToString('N') -or
        -not [string]::Equals([IO.Path]::GetDirectoryName($taskDirectory), $script:taskReceiptsRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw [ArgumentException]::new('RECEIPT_PATH_INVALID')
    }
    $taskValue = Read-BoundedJson $Value
    if ((Read-Property $taskValue 'version') -ne 1 -or (Read-Id (Read-Property $taskValue 'id')) -ne $taskGuid -or
        (Read-Property $taskValue 'component') -cne 'Console' -or (Read-Property $taskValue 'mode') -cne 'Update') {
        throw [ArgumentException]::new('RECEIPT_INVALID')
    }
    return $taskValue
}

function Write-ReceiptSummary {
    param([string]$Part, $Receipt)
    [pscustomobject]@{ Part = $Part; DiagnosticVersion = '20261005.3'; Id = Read-Id (Read-Property $Receipt 'id');
        Procedure = Read-SafeIdentifier (Read-Property $Receipt 'procedure');
        State = Read-SafeIdentifier (Read-Property $Receipt 'state'); Code = Read-SafeIdentifier (Read-Property $Receipt 'code');
        FailureStage = Read-SafeIdentifier (Read-Property $Receipt 'failureStage');
        RuntimeStep = Read-SafeIdentifier (Read-Property $Receipt 'runtimeStep');
        ExceptionType = Read-SafeIdentifier (Read-Property $Receipt 'exceptionType');
        HResult = $(if ($null -ne (Read-Property $Receipt 'hresult')) { ('0x{0:X8}' -f [int](Read-Property $Receipt 'hresult')) } else { $null }) } | Format-List
}

function Resolve-RuntimePath {
    param([string]$Root, [string]$Relative)
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Length -ge 240 -or
        $Relative.Contains('\') -or $Relative.StartsWith('/') -or $Relative -match '[\x00-\x1F<>:"|?*]') {
        throw [ArgumentException]::new('RUNTIME_PATH_INVALID')
    }
    foreach ($taskPart in $Relative.Split('/')) {
        if ($taskPart -in @('', '.', '..', 'data', 'logs') -or $taskPart.EndsWith('.') -or $taskPart.EndsWith(' ') -or
            $taskPart -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') {
            throw [ArgumentException]::new('RUNTIME_PATH_INVALID')
        }
    }
    $taskName = [IO.Path]::GetFileName($Relative)
    if ($taskName -match '^(appsettings|NLog\.config$|payload-manifest\.json$)' -or
        $taskName -match '\.(pfx|p12|key|pem|snk)$') {
        throw [ArgumentException]::new('PROTECTED_FILE_REJECTED')
    }
    $taskResolved = Join-Path $Root $Relative.Replace('/', '\')
    Assert-LocalPath $taskResolved
    if (-not (Test-Under $taskResolved $Root) -or $taskResolved -ieq $Root) {
        throw [ArgumentException]::new('RUNTIME_PATH_OUTSIDE_ROOT')
    }
    return $taskResolved
}

function Get-RuntimeRecords {
    param($Receipt)
    $taskRecords = @(Read-Property $Receipt 'runtimeBackups' | Where-Object { $null -ne $_ })
    if ($taskRecords.Count -lt 1 -or $taskRecords.Count -gt 4096) { throw [ArgumentException]::new('RUNTIME_COUNT_INVALID') }
    $taskResult = @{}
    foreach ($taskRecord in $taskRecords) {
        $taskValue = [string](Read-Property $taskRecord 'path')
        if (-not $taskValue.StartsWith($script:taskRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw [ArgumentException]::new('RUNTIME_RECORD_OUTSIDE_ROOT')
        }
        $taskRelative = $taskValue.Substring($script:taskRoot.TrimEnd('\').Length + 1).Replace('\', '/')
        [void](Resolve-RuntimePath $script:taskRoot $taskRelative)
        if ($taskResult.ContainsKey($taskRelative) -or [string](Read-Property $taskRecord 'sha256') -notmatch '^[A-Fa-f0-9]{64}$' -or
            $null -eq (Read-Property $taskRecord 'size') -or
            [long](Read-Property $taskRecord 'size') -lt 0 -or [long](Read-Property $taskRecord 'size') -gt 268435456) {
            throw [ArgumentException]::new('RUNTIME_RECORD_INVALID')
        }
        $taskResult[$taskRelative] = $taskRecord
    }
    return $taskResult
}

function Read-Descriptor {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value) -or $Value.Length -gt 65536) { throw [ArgumentException]::new('SDDL_RECORD_INVALID') }
    return [Security.AccessControl.RawSecurityDescriptor]::new($Value)
}

function Get-DaclBytes {
    param($Descriptor)
    if ($null -eq $Descriptor.DiscretionaryAcl) { return '' }
    $taskBytes = [byte[]]::new($Descriptor.DiscretionaryAcl.BinaryLength)
    $Descriptor.DiscretionaryAcl.GetBinaryForm($taskBytes, 0)
    return [Convert]::ToBase64String($taskBytes)
}

function Get-AclObservation {
    param([string]$Value, [string]$RecordedSddl)
    Assert-LocalPath $Value
    if (-not (Test-Path -LiteralPath $Value)) { throw [IO.FileNotFoundException]::new('OBJECT_MISSING') }
    $taskSections = [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor [Security.AccessControl.AccessControlSections]::Access
    $taskActual = Read-Descriptor ((Get-Acl -LiteralPath $Value).GetSecurityDescriptorSddlForm($taskSections))
    $taskExpected = Read-Descriptor $RecordedSddl
    $taskOwner = [object]::Equals($taskActual.Owner, $taskExpected.Owner)
    $taskGroup = [object]::Equals($taskActual.Group, $taskExpected.Group)
    $taskProtected = ($taskActual.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -eq
        ($taskExpected.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected)
    $taskDacl = (Get-DaclBytes $taskActual) -ceq (Get-DaclBytes $taskExpected)
    return [pscustomobject]@{ OwnerMatchesRecord = $taskOwner; GroupMatchesRecord = $taskGroup;
        ProtectedMatchesRecord = $taskProtected; DaclBytesMatchRecord = $taskDacl;
        MatchesRecord = ($taskOwner -and $taskGroup -and $taskProtected -and $taskDacl) }
}

function Write-AclDifference {
    param([string]$Label, [string]$Value, [string]$RecordedSddl)
    $taskSections = [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor [Security.AccessControl.AccessControlSections]::Access
    Assert-LocalPath $Value
    $taskActual = Read-Descriptor ((Get-Acl -LiteralPath $Value).GetSecurityDescriptorSddlForm($taskSections))
    $taskExpected = Read-Descriptor $RecordedSddl
    [pscustomobject]@{ Part = 'AclDifference'; Object = $Label;
        ActualOwner = $(if ($null -ne $taskActual.Owner) { $taskActual.Owner.Value } else { $null });
        RecordedOwner = $(if ($null -ne $taskExpected.Owner) { $taskExpected.Owner.Value } else { $null });
        ActualGroup = $(if ($null -ne $taskActual.Group) { $taskActual.Group.Value } else { $null });
        RecordedGroup = $(if ($null -ne $taskExpected.Group) { $taskExpected.Group.Value } else { $null });
        ActualProtected = (($taskActual.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -ne 0);
        RecordedProtected = (($taskExpected.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -ne 0);
        DaclBytesMatchRecord = ((Get-DaclBytes $taskActual) -ceq (Get-DaclBytes $taskExpected)) } | Format-List
    if ((Get-DaclBytes $taskActual) -cne (Get-DaclBytes $taskExpected)) {
        Write-AceSummary 'RecordedDescriptor' $taskExpected
        Write-AceSummary 'ActualDescriptor' $taskActual
    }
}

function Write-AceSummary {
    param([string]$Role, $Descriptor)
    [pscustomobject]@{ Part = 'TemporaryDescriptor'; Role = $Role;
        OwnerSid = $(if ($null -ne $Descriptor.Owner) { $Descriptor.Owner.Value } else { $null });
        GroupSid = $(if ($null -ne $Descriptor.Group) { $Descriptor.Group.Value } else { $null });
        DaclProtected = (($Descriptor.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclProtected) -ne 0);
        NullDacl = ($null -eq $Descriptor.DiscretionaryAcl);
        AclRevision = $(if ($null -ne $Descriptor.DiscretionaryAcl) { $Descriptor.DiscretionaryAcl.Revision } else { $null });
        AceCount = $(if ($null -ne $Descriptor.DiscretionaryAcl) { $Descriptor.DiscretionaryAcl.Count } else { 0 }) } | Format-List
    if ($null -eq $Descriptor.DiscretionaryAcl) { return }
    $taskRows = [Collections.Generic.List[object]]::new()
    for ($taskIndex = 0; $taskIndex -lt $Descriptor.DiscretionaryAcl.Count -and $taskIndex -lt 32; $taskIndex++) {
        $taskAce = $Descriptor.DiscretionaryAcl[$taskIndex]
        if ($taskAce -is [Security.AccessControl.CommonAce] -and -not $taskAce.IsCallback) {
            $taskRows.Add([pscustomobject]@{ Index = $taskIndex; Type = $taskAce.AceType.ToString();
                Sid = $taskAce.SecurityIdentifier.Value; Mask = ('0x{0:X8}' -f $taskAce.AccessMask); Flags = $taskAce.AceFlags.ToString() })
        } else {
            $taskRows.Add([pscustomobject]@{ Index = $taskIndex; Type = $taskAce.AceType.ToString();
                Sid = 'ComplexAceNotExpanded'; Mask = ''; Flags = $taskAce.AceFlags.ToString() })
        }
    }
    $taskRows.ToArray() | Format-Table -AutoSize
    if ($Descriptor.DiscretionaryAcl.Count -gt 32) { Write-Output 'ACE_DETAILS_TRUNCATED_AT_32' }
}

function Get-ObjectLabel {
    param([string]$Value)
    Assert-LocalPath $Value
    foreach ($taskScope in $script:taskScopes) {
        if (Test-Under $Value $taskScope.Root) {
            $taskSuffix = $Value.Substring($taskScope.Root.Length).TrimStart('\').Replace('\', '/')
            return $taskScope.Role + '/' + $taskSuffix
        }
    }
    throw [ArgumentException]::new('RECORDED_OBJECT_OUTSIDE_SCOPES')
}

function Test-FileBytes {
    param([string]$Value, $Record)
    Assert-LocalPath $Value
    if (-not (Test-Path -LiteralPath $Value -PathType Leaf)) { return $false }
    $taskItem = Get-Item -LiteralPath $Value -Force
    if ($taskItem.Length -gt 268435456) { throw [ArgumentException]::new('FILE_SIZE_LIMIT') }
    return $taskItem.Length -eq [long](Read-Property $Record 'size') -and
        (Get-FileHash -LiteralPath $Value -Algorithm SHA256).Hash -ieq [string](Read-Property $Record 'sha256')
}

function Test-BackupOwnerGroup {
    param([string]$Value)
    Assert-LocalPath $Value
    $taskSections = [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor [Security.AccessControl.AccessControlSections]::Access
    $taskAcl = Read-Descriptor ((Get-Acl -LiteralPath $Value).GetSecurityDescriptorSddlForm($taskSections))
    return $null -ne $taskAcl.Owner -and $null -ne $taskAcl.Group -and
        $taskAcl.Owner.Value -ceq 'S-1-5-32-544' -and $taskAcl.Group.Value -ceq 'S-1-5-32-544'
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { Write-Output 'WINDOWS_REQUIRED'; exit 1 }

try {
    $script:taskReceiptsRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'IISManager.Setup\receipts'
    $taskRecovery = Read-Receipt $RecoveryReceiptPath
    Write-ReceiptSummary 'RecoveryReceipt' $taskRecovery
    if ((Read-Property $taskRecovery 'procedure') -cne 'CompleteConsoleUiUpdate') {
        throw [ArgumentException]::new('RECOVERY_PROCEDURE_INVALID')
    }
    $taskRecoveryId = Read-Id (Read-Property $taskRecovery 'id')
    $taskOriginalId = Read-Id (Read-Property $taskRecovery 'recoveryOf')
    if ($taskOriginalId -eq $taskRecoveryId) { throw [ArgumentException]::new('RECOVERY_LINK_INVALID') }
    $taskOriginalDirectory = Join-Path $script:taskReceiptsRoot $taskOriginalId.ToString('N')
    $taskOriginalPath = Join-Path $taskOriginalDirectory 'receipt.json'
    $taskOriginal = Read-Receipt $taskOriginalPath
    Write-ReceiptSummary 'OriginalReceipt' $taskOriginal
    if ($null -ne (Read-Property $taskOriginal 'recoveryOf')) { throw [ArgumentException]::new('ORIGINAL_LINK_INVALID') }
    $taskGuardPath = Join-Path $taskOriginalDirectory 'console-ui-update-completion-guard.json'
    $taskGuard = Read-BoundedJson $taskGuardPath
    $taskGuardMatches = (Read-Property $taskGuard 'version') -eq 1 -and
        (Read-Property $taskGuard 'procedure') -ceq 'CompleteConsoleUiUpdate' -and
        (Read-Id (Read-Property $taskGuard 'originalReceiptId')) -eq $taskOriginalId -and
        (Read-Id (Read-Property $taskGuard 'recoveryReceiptId')) -eq $taskRecoveryId -and
        [string]::Equals([string](Read-Property $taskGuard 'receiptPath'), $RecoveryReceiptPath, [StringComparison]::OrdinalIgnoreCase)
    [pscustomobject]@{ Part = 'RecoveryLink'; OriginalReceiptId = $taskOriginalId; RecoveryReceiptId = $taskRecoveryId;
        ClaimExists = $true; ClaimMatchesBothReceipts = $taskGuardMatches; ReplayAuthorized = $false } | Format-List
    if (-not $taskGuardMatches) { throw [ArgumentException]::new('RECOVERY_GUARD_LINK_MISMATCH') }
    $taskBefore = Read-Property $taskOriginal 'beforeService'
    $taskMatch = [regex]::Match([string](Read-Property $taskBefore 'binaryPath'),
        '^"(?<exe>[A-Za-z]:\\[^"]+)" --contentRoot "(?<root>[A-Za-z]:\\[^"]+)" --environment Production$')
    $script:taskRoot = $taskMatch.Groups['root'].Value
    if (-not $taskMatch.Success -or (Read-Property $taskBefore 'name') -cne 'IISManager.Console' -or
        -not [string]::Equals($taskMatch.Groups['exe'].Value, (Join-Path $script:taskRoot 'IISManager.Console.exe'), [StringComparison]::OrdinalIgnoreCase)) {
        throw [ArgumentException]::new('CONSOLE_PATH_UNVERIFIED')
    }
    Assert-LocalPath $script:taskRoot
    $taskRecoveryBefore = Read-Property $taskRecovery 'beforeService'
    if ([string](Read-Property $taskBefore 'binaryPath') -cne [string](Read-Property $taskRecoveryBefore 'binaryPath') -or
        [string](Read-Property $taskBefore 'account') -cne [string](Read-Property $taskRecoveryBefore 'account')) {
        throw [ArgumentException]::new('SERVICE_RECORD_LINK_MISMATCH')
    }
    $taskRecoveryDirectory = [IO.Path]::GetDirectoryName($RecoveryReceiptPath)
    $taskRecoveryBackup = Join-Path $taskRecoveryDirectory 'runtime-backup'
    $taskOriginalBackup = Join-Path $taskOriginalDirectory 'runtime-backup'
    $taskRecoveryStage = Join-Path $taskRecoveryDirectory 'stage'
    $taskOldMarker = Read-BoundedJson (Join-Path $taskOriginalBackup '.iismanager-install.json')
    if ((Read-Property $taskOldMarker 'component') -cne 'Console' -or
        [string](Read-Property $taskOldMarker 'installDirectory') -ine $script:taskRoot) {
        throw [ArgumentException]::new('OLD_MARKER_INVALID')
    }
    $taskData = [string](Read-Property $taskOldMarker 'dataDirectory')
    Assert-LocalPath $taskData
    if ((Test-Under $taskData $script:taskRoot) -or (Test-Under $script:taskRoot $taskData) -or
        (Test-Under $taskData $script:taskReceiptsRoot) -or (Test-Under $script:taskReceiptsRoot $taskData)) {
        throw [ArgumentException]::new('DATA_SCOPE_OVERLAP')
    }
    $script:taskScopes = @([pscustomobject]@{ Role = 'Component'; Root = $script:taskRoot },
        [pscustomobject]@{ Role = 'OriginalReceipt'; Root = $taskOriginalDirectory },
        [pscustomobject]@{ Role = 'RecoveryReceipt'; Root = $taskRecoveryDirectory },
        [pscustomobject]@{ Role = 'Data'; Root = $taskData })
    $taskOld = Get-RuntimeRecords $taskOriginal
    $taskFrozen = Get-RuntimeRecords $taskRecovery
    $taskRuntimeFile = [string](Read-Property $taskRecovery 'runtimeFile')
    if (-not [string]::IsNullOrEmpty($taskRuntimeFile)) {
        [void](Resolve-RuntimePath $script:taskRoot $taskRuntimeFile)
        [pscustomobject]@{ Part = 'RuntimeStep'; File = $taskRuntimeFile;
            Step = Read-SafeIdentifier (Read-Property $taskRecovery 'runtimeStep') } | Format-List
    }
}
catch { Write-SafeFailure 'Inputs' $_.Exception; Write-Output 'READ_ONLY_INCOMPLETE; NO_CHANGES'; exit 1 }

try {
    $taskService = Get-CimInstance -ClassName Win32_Service -Filter "Name = 'IISManager.Console'"
    [pscustomobject]@{ Part = 'Service'; Name = 'IISManager.Console'; Status = Read-SafeIdentifier (Read-Property $taskService 'State');
        ProcessId = Read-Property $taskService 'ProcessId'; ExitCode = Read-Property $taskService 'ExitCode';
        StartMode = Read-SafeIdentifier (Read-Property $taskService 'StartMode');
        RecordedOriginalState = Read-Property $taskBefore 'state'; RecordedRecoveryAfterState = Read-Property (Read-Property $taskRecovery 'afterService') 'state';
        BinaryPathMatchesRecord = ([string](Read-Property $taskService 'PathName') -ceq [string](Read-Property $taskBefore 'binaryPath'));
        AccountMatchesRecord = ([string](Read-Property $taskService 'StartName') -ceq [string](Read-Property $taskBefore 'account')) } | Format-List
}
catch { Write-SafeFailure 'Service' $_.Exception }

foreach ($taskMarkerRole in @('Component', 'RecoveryBackup')) {
    try {
        $taskMarkerPath = $(if ($taskMarkerRole -ceq 'Component') { Join-Path $script:taskRoot '.iismanager-install.json' }
            else { Join-Path $taskRecoveryBackup '.iismanager-install.json' })
        $taskMarker = Read-BoundedJson $taskMarkerPath
        $taskGeneration = Read-Id (Read-Property $taskMarker 'generation')
        [pscustomobject]@{ Part = 'InstallationMarker'; Role = $taskMarkerRole; State = Read-SafeIdentifier (Read-Property $taskMarker 'state');
            GenerationMatchesOriginal = ($taskGeneration -eq $taskOriginalId); GenerationMatchesRecovery = ($taskGeneration -eq $taskRecoveryId);
            InstallDirectoryMatchesRecord = ([string](Read-Property $taskMarker 'installDirectory') -ieq $script:taskRoot);
            DataDirectoryMatchesOldMarker = ([string](Read-Property $taskMarker 'dataDirectory') -ieq $taskData) } | Format-List
    }
    catch { Write-SafeFailure ('InstallationMarker:' + $taskMarkerRole) $_.Exception }
}

# Recovery permissions include reviewed directories; they are not all runtime files.
foreach ($taskKey in @('permissions', 'preservedFiles')) {
    $taskChecked = 0; $taskMismatches = 0; $taskUnreadable = 0
    $taskRecords = @(Read-Property $taskRecovery $taskKey | Where-Object { $null -ne $_ })
    if ($taskRecords.Count -gt 16384) {
        Write-SafeFailure $taskKey ([ArgumentException]::new('RECORD_COUNT_LIMIT'))
        continue
    }
    foreach ($taskRecord in $taskRecords) {
        try {
            $taskPath = [string](Read-Property $taskRecord 'path')
            $taskLabel = Get-ObjectLabel $taskPath
            $taskAcl = Get-AclObservation $taskPath ([string](Read-Property $taskRecord $(if ($taskKey -ceq 'permissions') { 'afterSddl' } else { 'sddl' })))
            $taskParentAcl = $null; $taskContent = $null
            if ($taskKey -ceq 'preservedFiles') {
                $taskParentAcl = Get-AclObservation ([IO.Path]::GetDirectoryName($taskPath)) ([string](Read-Property $taskRecord 'parentSddl'))
                $taskContent = Test-FileBytes $taskPath $taskRecord
            }
            $taskChecked++
            if (-not $taskAcl.MatchesRecord -or $taskContent -eq $false -or ($null -ne $taskParentAcl -and -not $taskParentAcl.MatchesRecord)) {
                $taskMismatches++
                if ($taskMismatches -le 20) {
                    [pscustomobject]@{ Part = 'RecordedObjectMismatch'; RecordSet = $taskKey; Object = $taskLabel;
                        ContentMatchesRecord = $taskContent; Acl = $taskAcl; ParentAcl = $taskParentAcl } | Format-List
                    if ($taskMismatches -le 3) {
                        if (-not $taskAcl.MatchesRecord) {
                            Write-AclDifference $taskLabel $taskPath ([string](Read-Property $taskRecord $(if ($taskKey -ceq 'permissions') { 'afterSddl' } else { 'sddl' })))
                        }
                        if ($null -ne $taskParentAcl -and -not $taskParentAcl.MatchesRecord) {
                            Write-AclDifference ($taskLabel + ':parent') ([IO.Path]::GetDirectoryName($taskPath)) ([string](Read-Property $taskRecord 'parentSddl'))
                        }
                    }
                }
            }
        }
        catch { $taskUnreadable++; Write-SafeFailure ('RecordedObject:' + $taskKey) $_.Exception }
    }
    [pscustomobject]@{ Part = 'RecordedObjectSummary'; RecordSet = $taskKey; Records = $taskRecords.Count;
        Checked = $taskChecked; Mismatches = $taskMismatches; Unreadable = $taskUnreadable;
        MismatchDetailsTruncated = ($taskMismatches -gt 20) } | Format-List
}

# Only runtime bytes are scanned; configuration contents and SACL are never printed.
$taskStaged = @{}; $taskStageComplete = $false
try {
    Assert-LocalPath $taskRecoveryStage
    $taskStack = [Collections.Generic.Stack[string]]::new(); $taskStack.Push($taskRecoveryStage)
    $taskEntries = 0
    while ($taskStack.Count -gt 0) {
        $taskDirectory = $taskStack.Pop(); Assert-LocalPath $taskDirectory
        foreach ($taskItem in @(Get-ChildItem -LiteralPath $taskDirectory -Force)) {
            $taskEntries++
            if ($taskEntries -gt 8192) { throw [ArgumentException]::new('STAGE_ENTRY_LIMIT') }
            Assert-LocalPath $taskItem.FullName
            if ($taskItem.PSIsContainer) {
                if ($taskItem.Name -notin @('logs', 'data')) { $taskStack.Push($taskItem.FullName) }
                continue
            }
            if ($taskItem.Name -match '^(appsettings|NLog\.config$|payload-manifest\.json$)' -or $taskItem.Name -match '\.(pfx|p12|key|pem|snk)$') { continue }
            $taskRelative = $taskItem.FullName.Substring($taskRecoveryStage.Length + 1).Replace('\', '/')
            [void](Resolve-RuntimePath $script:taskRoot $taskRelative)
            if ($taskStaged.ContainsKey($taskRelative) -or $taskItem.Length -gt 268435456 -or $taskStaged.Count -ge 4096) {
                throw [ArgumentException]::new('STAGED_RUNTIME_INVALID')
            }
            $taskStaged[$taskRelative] = [pscustomobject]@{ size = $taskItem.Length; sha256 = (Get-FileHash -LiteralPath $taskItem.FullName -Algorithm SHA256).Hash }
        }
    }
    $taskStageComplete = $true
}
catch { Write-SafeFailure 'StagedRuntime' $_.Exception }
[pscustomobject]@{ Part = 'StagedRuntime'; FilesObserved = $taskStaged.Count; ScanComplete = $taskStageComplete;
    OriginalPayloadManifestVerified = $false; ComparisonBasis = 'Observed staged bytes; no authorization to resume' } | Format-List

$taskCounts = @{ OldAndStaged = 0; OldOnly = 0; StagedOnly = 0; Neither = 0; Missing = 0; Unreadable = 0;
    OriginalBackupVerified = 0; OriginalBackupUnverified = 0; RecoveryBackupVerified = 0; RecoveryBackupUnverified = 0;
    CurrentBytesMatchRecoverySnapshot = 0; CurrentBytesDifferFromRecoverySnapshot = 0; RuntimeAclMismatches = 0;
    RecoveryBackupOwnerGroupUnexpected = 0 }
foreach ($taskRelative in @(@($taskOld.Keys) + @($taskFrozen.Keys) + @($taskStaged.Keys) | Sort-Object -Unique)) {
    try {
        $taskPath = Resolve-RuntimePath $script:taskRoot $taskRelative
        $taskClass = 'Missing'
        if (Test-Path -LiteralPath $taskPath -PathType Leaf) {
            $taskOldMatch = $taskOld.ContainsKey($taskRelative) -and (Test-FileBytes $taskPath $taskOld[$taskRelative])
            $taskStagedMatch = $taskStaged.ContainsKey($taskRelative) -and (Test-FileBytes $taskPath $taskStaged[$taskRelative])
            $taskClass = $(if ($taskOldMatch -and $taskStagedMatch) { 'OldAndStaged' } elseif ($taskOldMatch) { 'OldOnly' }
                elseif ($taskStagedMatch) { 'StagedOnly' } else { 'Neither' })
        }
        $taskCounts[$taskClass]++
        if ($taskFrozen.ContainsKey($taskRelative)) {
            if (Test-FileBytes $taskPath $taskFrozen[$taskRelative]) { $taskCounts.CurrentBytesMatchRecoverySnapshot++ }
            else { $taskCounts.CurrentBytesDifferFromRecoverySnapshot++ }
            $taskAcl = Get-AclObservation $taskPath ([string](Read-Property $taskFrozen[$taskRelative] 'sddl'))
            $taskParentAcl = Get-AclObservation ([IO.Path]::GetDirectoryName($taskPath)) ([string](Read-Property $taskFrozen[$taskRelative] 'parentSddl'))
            if (-not $taskAcl.MatchesRecord -or -not $taskParentAcl.MatchesRecord) {
                $taskCounts.RuntimeAclMismatches++
                if ($taskCounts.RuntimeAclMismatches -le 20) {
                    [pscustomobject]@{ Part = 'RuntimeAclMismatch'; File = $taskRelative; Acl = $taskAcl; ParentAcl = $taskParentAcl } | Format-List
                }
            }
        }
    }
    catch { $taskCounts.Unreadable++; Write-SafeFailure 'RuntimeObservation' $_.Exception }
    foreach ($taskBackupRole in @('Original', 'Recovery')) {
        $taskRecords = $(if ($taskBackupRole -ceq 'Original') { $taskOld } else { $taskFrozen })
        $taskDirectory = $(if ($taskBackupRole -ceq 'Original') { $taskOriginalBackup } else { $taskRecoveryBackup })
        if ($taskRecords.ContainsKey($taskRelative)) {
            try {
                $taskBackupPath = Resolve-RuntimePath $taskDirectory $taskRelative
                if (Test-FileBytes $taskBackupPath $taskRecords[$taskRelative]) { $taskCounts[$taskBackupRole + 'BackupVerified']++ }
                else {
                    $taskCounts[$taskBackupRole + 'BackupUnverified']++
                    if ($taskCounts[$taskBackupRole + 'BackupUnverified'] -le 20) {
                        [pscustomobject]@{ Part = 'BackupBytesUnverified'; Role = $taskBackupRole; File = $taskRelative;
                            ExistsAsFile = (Test-Path -LiteralPath $taskBackupPath -PathType Leaf) } | Format-List
                    }
                }
                if ($taskBackupRole -ceq 'Recovery' -and (Test-Path -LiteralPath $taskBackupPath -PathType Leaf) -and
                    -not (Test-BackupOwnerGroup $taskBackupPath)) {
                    $taskCounts.RecoveryBackupOwnerGroupUnexpected++
                    if ($taskCounts.RecoveryBackupOwnerGroupUnexpected -le 20) {
                        [pscustomobject]@{ Part = 'BackupOwnerGroupUnexpected'; File = $taskRelative; ExpectedOwnerGroup = 'S-1-5-32-544' } | Format-List
                    }
                }
            }
            catch { $taskCounts[$taskBackupRole + 'BackupUnverified']++; Write-SafeFailure ($taskBackupRole + 'BackupRead') $_.Exception }
        }
    }
}
[pscustomobject]@{ Part = 'RuntimeSummary'; OriginalRecords = $taskOld.Count; RecoverySnapshots = $taskFrozen.Count;
    OldAndStaged = $taskCounts.OldAndStaged; OldOnly = $taskCounts.OldOnly; StagedOnly = $taskCounts.StagedOnly;
    Neither = $taskCounts.Neither; Missing = $taskCounts.Missing; Unreadable = $taskCounts.Unreadable;
    CurrentBytesMatchRecoverySnapshot = $taskCounts.CurrentBytesMatchRecoverySnapshot;
    CurrentBytesDifferFromRecoverySnapshot = $taskCounts.CurrentBytesDifferFromRecoverySnapshot;
    RuntimeAclMismatches = $taskCounts.RuntimeAclMismatches;
    OriginalBackupVerified = $taskCounts.OriginalBackupVerified; OriginalBackupUnverified = $taskCounts.OriginalBackupUnverified;
    RecoveryBackupVerified = $taskCounts.RecoveryBackupVerified; RecoveryBackupUnverified = $taskCounts.RecoveryBackupUnverified;
    RecoveryBackupOwnerGroupUnexpected = $taskCounts.RecoveryBackupOwnerGroupUnexpected } | Format-List

try {
    $taskPermissions = @(Read-Property $taskOriginal 'permissions' | Where-Object { $null -ne $_ })
    if ($taskPermissions.Count -lt 1 -or $taskPermissions.Count -gt 16384) { throw [ArgumentException]::new('PERMISSION_RECORD_COUNT_INVALID') }
    $taskLast = $taskPermissions[-1]
    $taskLastPath = [string](Read-Property $taskLast 'path')
    if ((Read-Property $taskLast 'readBackVerified') -ne $false -or
        [IO.Path]::GetDirectoryName($taskLastPath) -ine $script:taskRoot) { throw [ArgumentException]::new('INCOMPLETE_RECORD_INVALID') }
    $taskLastRelative = [IO.Path]::GetFileName($taskLastPath)
    if (-not $taskOld.ContainsKey($taskLastRelative)) { throw [ArgumentException]::new('INCOMPLETE_RECORD_NOT_RUNTIME') }
    $taskLastRecord = $taskOld[$taskLastRelative]
    $taskExpected = Read-Descriptor ([string](Read-Property $taskLastRecord 'sddl'))
    [pscustomobject]@{ Part = 'OriginalIncompleteReplacement'; File = $taskLastRelative; ReadBackVerified = Read-Property $taskLast 'readBackVerified' } | Format-List
    foreach ($taskTemporaryRole in @('Component', 'RecoveryBackup')) {
        $taskDirectory = $(if ($taskTemporaryRole -ceq 'Component') { $script:taskRoot } else { $taskRecoveryBackup })
        Assert-LocalPath $taskDirectory
        $taskCandidates = @(Get-ChildItem -LiteralPath $taskDirectory -Force -Filter '*.setup-tmp')
        if ($taskCandidates.Count -gt 8) { throw [ArgumentException]::new('TEMPORARY_COUNT_LIMIT') }
        [pscustomobject]@{ Part = 'TemporaryLocations'; Role = $taskTemporaryRole; Candidates = $taskCandidates.Count } | Format-List
        foreach ($taskTemporary in $taskCandidates) {
            $taskPattern = $(if ($taskTemporaryRole -ceq 'Component') { '^\.[0-9a-f]{32}\.setup-tmp$' } else { '^preserved-\.[0-9a-f]{32}\.setup-tmp$' })
            if ($taskTemporary.PSIsContainer -or $taskTemporary.Name -cnotmatch $taskPattern) { throw [ArgumentException]::new('TEMPORARY_NAME_UNVERIFIED') }
            Assert-LocalPath $taskTemporary.FullName
            $taskAcl = Get-AclObservation $taskTemporary.FullName ([string](Read-Property $taskLastRecord 'sddl'))
            $taskParentRecord = $(if ($taskTemporaryRole -ceq 'Component') { [string](Read-Property $taskLastRecord 'parentSddl') }
                else { 'O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)' })
            $taskParentAcl = Get-AclObservation $taskDirectory $taskParentRecord
            [pscustomobject]@{ Part = 'TemporaryFile'; Role = $taskTemporaryRole; File = $taskTemporary.Name;
                BytesMatchOriginalRuntime = (Test-FileBytes $taskTemporary.FullName $taskLastRecord);
                BytesMatchObservedStage = $(if ($taskStaged.ContainsKey($taskLastRelative)) { Test-FileBytes $taskTemporary.FullName $taskStaged[$taskLastRelative] } else { $null });
                ReadOnly = (($taskTemporary.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0);
                RecordedInRecoveryPreservedFiles = (@(Read-Property $taskRecovery 'preservedFiles' | Where-Object {
                    [string](Read-Property $_ 'path') -ieq $taskTemporary.FullName
                }).Count -gt 0);
                AclComparedWithOriginalRuntime = $taskAcl; ParentAcl = $taskParentAcl;
                FileComparisonBasis = 'Original runtime descriptor; exact pre-move temp snapshot was not persisted';
                ParentComparisonBasis = $(if ($taskTemporaryRole -ceq 'Component') { 'Recorded component parent' } else { 'New restricted backup; different from source parent' }) } | Format-List
            $taskSections = [Security.AccessControl.AccessControlSections]::Owner -bor
                [Security.AccessControl.AccessControlSections]::Group -bor [Security.AccessControl.AccessControlSections]::Access
            $taskActual = Read-Descriptor ((Get-Acl -LiteralPath $taskTemporary.FullName).GetSecurityDescriptorSddlForm($taskSections))
            Write-AceSummary 'OriginalRuntimeRecord' $taskExpected
            Write-AceSummary $taskTemporaryRole $taskActual
        }
    }
}
catch { Write-SafeFailure 'TemporaryObservation' $_.Exception }

[pscustomobject]@{ Part = 'EvidenceLimits'; AllFrozenFileAclSnapshotsPersisted = $false;
    Coverage = 'Receipt-recorded runtime, preserved objects and permissions; other frozen file ACLs have no persisted inspection baseline';
    DiagnosticIsRecoveryAuthorization = $false; SaclReadOrCompared = $false } | Format-List

if ($script:taskFailures -gt 0) { Write-Output ('READ_ONLY_INCOMPLETE; UNVERIFIED_PARTS=' + $script:taskFailures) }
else { Write-Output 'READ_ONLY_COMPLETE' }
Write-Output 'NO_FILES_SERVICES_PERMISSIONS_OR_MARKERS_CHANGED; NO_REPLAY_OR_ROLLBACK_AUTHORIZED'
# No transcript, output file, temporary file, privilege adjustment, native helper,
# service control, ACL write, marker write, or claim removal is performed.
