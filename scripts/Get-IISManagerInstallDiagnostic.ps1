#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReceiptPath,
    [switch]$ProbeRuleProperties
)

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
    param([string]$Operation, [System.Exception]$Failure)
    $taskLeaf = $Failure
    for ($taskIndex = 0; $taskIndex -lt 16 -and $null -ne $taskLeaf.InnerException; $taskIndex++) {
        $taskLeaf = $taskLeaf.InnerException
    }
    [pscustomobject]@{
        Part = $Operation
        Result = 'Unverified'
        ExceptionType = $taskLeaf.GetType().Name
        HResult = ('0x{0:X8}' -f $taskLeaf.HResult)
    } | Format-List
}

function Release-DiagnosticComObject {
    param($Value)
    try {
        if ($null -ne $Value -and [Runtime.InteropServices.Marshal]::IsComObject($Value)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value)
        }
    }
    catch { Write-SafeFailure 'ComRelease' $_.Exception }
}

function Test-UnregisteredRuleProperties {
    param($Receipt, [string]$Component, [guid]$InstallationId)
    $taskProbeRule = $null
    $taskOperation = 'ProbeInputs'
    $taskFailureStep = ''
    try {
        $taskRecorded = Read-OptionalProperty $Receipt 'afterService'
        $taskBinary = [string](Read-OptionalProperty $taskRecorded 'binaryPath')
        $taskMatch = [regex]::Match($taskBinary,
            '^"(?<exe>[A-Za-z]:\\[^"]+)" --contentRoot "(?<root>[A-Za-z]:\\[^"]+)" --environment Production$')
        if (-not $taskMatch.Success) { throw [System.ArgumentException]::new('SERVICE_PATH_UNVERIFIED') }
        $taskProgram = $taskMatch.Groups['exe'].Value
        $taskRoot = $taskMatch.Groups['root'].Value
        $taskExpectedProgram = Join-Path $taskRoot ('IISManager.' + $Component + '.exe')
        if (-not [string]::Equals($taskProgram, $taskExpectedProgram, [StringComparison]::OrdinalIgnoreCase)) {
            throw [System.ArgumentException]::new('SERVICE_PATH_UNVERIFIED')
        }
        $taskLocalPath = Join-Path $taskRoot 'appsettings.Local.json'
        $taskLocalFile = Get-Item -LiteralPath $taskLocalPath
        if ($taskLocalFile.PSIsContainer -or $taskLocalFile.Length -gt 2097152) { throw [System.ArgumentException]::new('CONFIG_INVALID') }
        # Only the port and source-mode fields are selected. Credentials are never output.
        $taskLocal = Get-Content -LiteralPath $taskLocalPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $taskSection = Read-OptionalProperty $taskLocal $Component
        $taskPort = 0
        if ((Read-OptionalProperty $taskSection 'SourceAccessMode') -cne 'PrivateNetwork' -or
            -not [int]::TryParse([string](Read-OptionalProperty $taskSection 'Port'), [ref]$taskPort) -or
            $taskPort -lt 1024 -or $taskPort -gt 65535) { throw [System.ArgumentException]::new('PROBE_POLICY_UNVERIFIED') }
        $taskNetworks = '10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,127.0.0.0/8,169.254.0.0/16,100.64.0.0/10,::1/128,fc00::/7,fe80::/10'
        $taskOperation = 'Create.UnregisteredRule'
        $taskProbeRule = New-Object -ComObject HNetCfg.FWRule
        # Same property order as the installer, on a fresh object never added to Rules.
        $taskSettings = @(
            @('Name', ('IISManager.Setup.' + $Component + '.' + $InstallationId.ToString('D'))),
            @('Description', 'IISManager installer: restricted management access'),
            @('Grouping', 'IISManager'),
            @('ServiceName', ('IISManager.' + $Component)),
            @('ApplicationName', $taskProgram),
            @('Protocol', 6),
            @('LocalPorts', $taskPort.ToString([Globalization.CultureInfo]::InvariantCulture)),
            @('RemoteAddresses', $taskNetworks),
            @('Direction', 1),
            @('Profiles', 3),
            @('Action', 1),
            @('EdgeTraversal', $false),
            @('Enabled', $true)
        )
        foreach ($taskSetting in $taskSettings) {
            $taskPropertyName = [string]$taskSetting[0]
            $taskOperation = 'Set.' + $taskPropertyName
            [void]$taskProbeRule.GetType().InvokeMember($taskPropertyName,
                [Reflection.BindingFlags]::SetProperty, $null, $taskProbeRule, [object[]]@($taskSetting[1]))
            Write-Output ('PASS ' + $taskOperation)
        }
        Write-Output 'PASS UNREGISTERED_RULE_PROPERTIES_ONLY; NO_RULE_ADDED'
    }
    catch {
        $taskFailureStep = $taskOperation
        Write-SafeFailure $taskOperation $_.Exception
    }
    finally { Release-DiagnosticComObject $taskProbeRule }

    if ($taskFailureStep -notin @('Set.RemoteAddresses', 'Set.ServiceName')) { return }
    $taskWithoutV6Loopback = $taskNetworks.Replace(',::1/128', '')
    $taskWithoutLoopbacks = $taskWithoutV6Loopback.Replace(',127.0.0.0/8', '')
    $taskVariants = @(
        @('WithoutIPv6Loopback', $taskWithoutV6Loopback, $false),
        @('WithoutAnyLoopback', $taskWithoutLoopbacks, $false),
        @('IPv6LoopbackOnly', '::1/128', $false),
        @('IPv4LoopbackOnly', '127.0.0.0/8', $false),
        @('OfficialOrderFullNetworks', $taskNetworks, $true),
        @('OfficialOrderWithoutLoopbacks', $taskWithoutLoopbacks, $true)
    )
    foreach ($taskVariant in $taskVariants) {
        $taskVariantRule = $null
        $taskVariantStep = 'Create.UnregisteredRule'
        try {
            $taskVariantSettings = @($taskSettings | ForEach-Object { ,@($_[0], $_[1]) })
            $taskVariantSettings[7][1] = $taskVariant[1]
            if ($taskVariant[2]) {
                $taskSavedSetting = $taskVariantSettings[3]
                $taskVariantSettings[3] = $taskVariantSettings[4]
                $taskVariantSettings[4] = $taskSavedSetting
            }
            $taskVariantRule = New-Object -ComObject HNetCfg.FWRule
            foreach ($taskSetting in $taskVariantSettings) {
                $taskVariantStep = 'Set.' + [string]$taskSetting[0]
                [void]$taskVariantRule.GetType().InvokeMember([string]$taskSetting[0],
                    [Reflection.BindingFlags]::SetProperty, $null, $taskVariantRule, [object[]]@($taskSetting[1]))
            }
            Write-Output ('PASS ' + [string]$taskVariant[0] + '; NO_RULE_ADDED')
        }
        catch { Write-SafeFailure ([string]$taskVariant[0] + '/' + $taskVariantStep) $_.Exception }
        finally { Release-DiagnosticComObject $taskVariantRule }
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    Write-Output 'WINDOWS_REQUIRED'
    exit 1
}

try {
    $taskFile = Get-Item -LiteralPath $ReceiptPath
    if ($taskFile.PSIsContainer -or $taskFile.Length -gt 2097152) { throw [System.ArgumentException]::new('RECEIPT_INVALID') }
    $taskReceipt = Get-Content -LiteralPath $ReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $taskComponent = Read-OptionalProperty $taskReceipt 'component'
    $taskId = [guid]::Empty
    if ((Read-OptionalProperty $taskReceipt 'version') -ne 1 -or $taskComponent -notin @('Agent', 'Console') -or
        -not [guid]::TryParse([string](Read-OptionalProperty $taskReceipt 'id'), [ref]$taskId) -or $taskId -eq [guid]::Empty) {
        throw [System.ArgumentException]::new('RECEIPT_INVALID')
    }
    $taskIntent = Read-OptionalProperty $taskReceipt 'firewall'
    $taskRecordedService = Read-OptionalProperty $taskReceipt 'afterService'
    [pscustomobject]@{
        Part = 'Receipt'
        Component = $taskComponent
        Mode = Read-OptionalProperty $taskReceipt 'mode'
        State = Read-OptionalProperty $taskReceipt 'state'
        Code = Read-OptionalProperty $taskReceipt 'code'
        ExceptionType = Read-OptionalProperty $taskReceipt 'exceptionType'
        HResult = Read-OptionalProperty $taskReceipt 'hresult'
        FirewallWriteIntentRecorded = ($null -ne $taskIntent)
        ServiceRecordedAfterFailure = ($null -ne $taskRecordedService)
        RecordedServiceState = Read-OptionalProperty $taskRecordedService 'state'
        PermissionRecords = @(Read-OptionalProperty $taskReceipt 'permissions' | Where-Object { $null -ne $_ }).Count
    } | Format-List
}
catch {
    Write-SafeFailure 'ReceiptRead' $_.Exception
    exit 1
}

foreach ($taskServiceName in @(('IISManager.' + $taskComponent), 'MpsSvc', 'BFE')) {
    try {
        $taskService = Get-Service -Name $taskServiceName -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Part = 'Service'
            Name = $taskServiceName
            Status = $(if ($null -eq $taskService) { 'AbsentOrUnreadable' } else { [string]$taskService.Status })
        } | Format-List
    }
    catch { Write-SafeFailure 'ServiceRead' $_.Exception }
}

$taskPolicy = $null
$taskRules = $null
try {
    $taskPolicy = New-Object -ComObject HNetCfg.FwPolicy2
    $taskRules = $taskPolicy.Rules
    $taskRuleName = 'IISManager.Setup.' + $taskComponent + '.' + $taskId.ToString('D')
    $taskFound = $false
    foreach ($taskRule in $taskRules) {
        try {
            if ([string]$taskRule.Name -ceq $taskRuleName) { $taskFound = $true }
        }
        finally {
            Release-DiagnosticComObject $taskRule
        }
    }
    [pscustomobject]@{
        Part = 'FirewallRead'
        ExactInstallationRulePresent = $taskFound
        LocalPolicyModifyState = $taskPolicy.LocalPolicyModifyState
    } | Format-List
}
catch { Write-SafeFailure 'FirewallRead' $_.Exception }
finally {
    foreach ($taskComObject in @($taskRules, $taskPolicy)) {
        Release-DiagnosticComObject $taskComObject
    }
}

if ($ProbeRuleProperties) {
    Test-UnregisteredRuleProperties $taskReceipt $taskComponent $taskId
}

# The optional probe selects port/source-mode from local configuration and tests
# setters on a fresh, unregistered COM object. It never adds/removes rules,
# starts/stops services, writes files, or prints credentials. Do not retry installation.
