# IISManagerTool 人工部署说明

本说明的包结构与手工部署命令对应历史包 `IISManagerTool-20261003-win-x64/`，该包使用 HTTPS，不能通过只改配置获得新 HTTP 模式。当前 `IISManagerTool-Setup-20261004-intranet-win-x64/` EXE 新装默认无需证书、无需管理来源输入的 HTTP 自动内网模式，推荐按 [安装说明](installer-guide.md) 操作。`20261004-win-x64` 安装包保留为 HTTP 加精确来源白名单历史版本。已安装服务更新保留原认证与来源模式；相关边界见本说明末尾。

本包用于 Windows Server x64，目标为 `net10.0`、`win-x64`、`Release`、框架依赖模式。没有已核验的服务器部署基线，因此两组件携带完整应用运行文件集合。本说明供操作员人工新装、更新和回退使用；没有安装服务、复制生产程序、配置生产证书或启停生产服务的自动脚本。

控制台集中部署一套；每台被管理的 IIS 服务器部署一套 Agent。两者均为独立 Windows 服务，直接使用 Kestrel HTTPS，默认端口分别为 7443、7444。它们不托管在 IIS 应用池中，不能将本包当作业务 IIS 站点覆盖包。

## 包结构与复制范围

完整交付目录为 `IISManagerTool-20261003-win-x64/`：

```text
IISManagerTool-20261003-win-x64/
  Agent/
    overlay/                         Agent 的全部运行文件
    ...                              组件发布证据与包元数据
  Console/
    overlay/                         Console 的全部运行文件与 wwwroot
    ...                              组件发布证据与包元数据
  config-templates/
    Agent/
      appsettings.json
      appsettings.Local.json.example
      NLog.config
    Console/
      appsettings.json
      appsettings.Local.json.example
      NLog.config
  deployment-guide.md
  windows-operations.md
  api.md
  validation.md
  package-validation.md
  console-preview.jpg
```

先将**完整交付目录**传到目标服务器上业务站点和服务程序目录之外的受限运维目录，保留上述结构及说明、证据。随后仅将目标组件 `overlay/` 内的**全部运行文件**按相对路径复制到该组件的独立服务目录。不能只复制入口 DLL，也不能将整个 `Agent/`、`Console/` 或包根覆盖进服务目录。文件传输与复制后的正确性由人工部署操作员负责；本包不携带部署包校验脚本。

首次部署还需从对应 `config-templates/` 复制 `appsettings.json`、`NLog.config`，并将 `appsettings.Local.json.example` 另存为服务目录中的 `appsettings.Local.json`，填写现场值。更新时保留现场配置和日志规则，人工比较/合并需要的字段；模板不能直接覆盖已有 `appsettings*.json` 或 `NLog.config`。

配置、状态与日志不属于 `overlay/` 覆盖范围。保留现场证书、私钥及独立密钥库；不要将这些材料或真实秘密放入交付目录。本次无配置加密迁移，包中不包含敏感信息保护工具；证书和权限由操作员已有或另行准备的工具处理。

## 首装前准备

1. 确认目标 Windows Server、IIS 版本及 x64 架构在支持范围内。应用池创建/修改要求 IIS 8.5 或更新版本的 `idleTimeoutAction` 属性。系统与旧服务器 ESU 条件见 [Windows 运行与验收](windows-operations.md)。
2. 安装受支持的 ASP.NET Core 10.0.x x64 运行时，使 `Microsoft.NETCore.App 10.0.x` 和 `Microsoft.AspNetCore.App 10.0.x` 均可用。框架依赖包不内置这些运行时，服务器无需安装 SDK。Console 主机本身不需要 IIS；Agent 主机须具备 IIS/WAS 及相应配置管理、Windows 原生管理组件。Agent 使用本机 IIS 管理 API，无需额外启用 WMSVC。随包 `Microsoft.Web.Administration` 来自项目的 NuGet 发布依赖，不得拿系统目录中的 .NET Framework 版本 DLL 替换。
3. 确认两个组件实际运行身份，并由操作员准备“作为服务登录”权限。Agent 身份须能管理本机 IIS 配置、读取建站点检查所需资源；Console 身份须能读取配置与证书，并写入自己的状态/日志。权限根据实际资源配置，不套用全目录全控模板，不因一次权限错误改用默认高权限身份。
4. 准备服务器证书与控制台客户端证书：证书存于各自机器 `LocalMachine\My`，有效且带所需私钥；服务端证书须有 serverAuth EKU，客户端证书须有 clientAuth EKU。Agent 将客户端指纹加入白名单，Console 固定核对 Agent 服务器证书指纹、主机名、链和撤销状态。授予实际服务身份必要的私钥读取权限，信任链和撤销访问依实际证书及网络验证，不硬编码外部网络端口，不关闭验证。
5. 准备管理网络连通：管理客户端访问 Console HTTPS 7443，Console 访问每台 Agent HTTPS 7444。操作员按实际来源和目标配置防火墙。Console 没有登录、账号或用户权限，所有获准访问入口的客户端具有完整操作能力；同时填写精确主机/IP 白名单及来源 IP/CIDR 白名单，不使用 `*`。
6. 准备 Agent 建站点允许根目录及已有物理目录，赋予关联池实际身份必要读取权限。工具不创建目录、不分配目录 ACL、不导入站点证书。站点 HTTPS 证书也由操作员提前准备，完整 TLS 握手及私钥可用性须实机验收。
7. 为 Agent 与 Console 各自准备不同的受限状态目录，建议使用服务程序目录之外的绝对路径，并给予各自身份必要读写权限。不得让两个进程共享同一账本目录。NLog 默认日志位于各自服务程序目录下 `logs/`，该目录须可写；更新与回退均保留日志与状态。

检查运行时可在管理员 Windows PowerShell 5.1 中使用已确认的 x64 `dotnet.exe`，不输出应用配置：

```powershell
$taskDotnet = '<服务器已安装的 x64 dotnet.exe 绝对路径>'
& $taskDotnet --list-runtimes
```

## 填写生产配置

模板中 `<...>` 都是占位值，必须替换，不能原样启动。两个组件均以 `Production` 环境运行；Agent 设置 `Mode=Windows`，Console 设置 `AllowDevelopmentHttp=false`，所有生产 Agent URL 使用 HTTPS，`Simulation=false`。不要复制开发配置，不要把 Development 模拟模式用于生产。

| 组件 | 必须按现场填写的主要配置 |
|---|---|
| Agent | `ServerCertificateThumbprint`、`AllowedClientThumbprints`、`AllowedSiteRoots`、绝对 `StateDirectory`、实际 `Port`/`ReservedPorts` |
| Console | 实际 `ListenAddress`、`Port`、精确 `AllowedHosts`、`AllowedClientNetworks`、`ServerCertificateThumbprint`、`AgentClientCertificateThumbprint`、绝对 `TaskDirectory`、`Servers` |
| Console 每台服务器 | 唯一 `Id`、`Name`、HTTPS 根地址 `AgentUrl`、预期 Agent `ServerCertificateThumbprint`、`Simulation=false` |

Console 顶层 `AllowedHosts` 也须与 `Console:AllowedHosts` 同步；数组只填写主机名/IP、不带端口，浏览器 Host 端口须等于 `Console:Port`。Agent URL 必须是根地址，例如 `https://<实际 Agent 域名>:7444/`，不能带账号、密码、额外路径、查询或片段；证书名称须匹配该域名。

两端从内容根读取 `appsettings.json`、环境配置及可选 `appsettings.Local.json`；随后环境变量和命令行具有更高优先级。检查服务启动参数与已有环境配置是否覆盖现场 JSON，不打印完整环境变量或配置。配置在启动时读取，服务器清单、证书、目录策略及日志配置变化后需要重启相应服务。

Windows 服务的工作目录与应用目录不能混为一谈。.NET 10 默认宿主对 System32 工作目录有内容根处理，但本说明的服务命令仍显式传入 `--contentRoot` 和 `--environment Production`。这样 JSON、`wwwroot` 及相对状态路径明确以实际服务目录为根；NLog `${basedir}` 则取应用目录。该做法依据当前源码及 [.NET 10 宿主内容根实现](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Microsoft.Extensions.Hosting/src/HostingHostBuilderExtensions.cs)，不依赖执行 PowerShell 时所在目录。

## 首次安装 Windows 服务

先完成运行文件复制、生产配置、证书和权限准备，再注册服务。下列命令由操作员在目标服务器的**管理员、64 位 Windows PowerShell 5.1** 中逐步执行；不会由本项目自动执行。每段重新填写路径，不依赖之前窗口中的变量。

源码中的服务名称固定如下，注册时使用对应名称；每台机器同一组件只注册一套：

| 组件 | 服务名称 | 运行入口 |
|---|---|---|
| Agent | `IISManager.Agent` | `IISManager.Agent.exe` |
| Console | `IISManager.Console` | `IISManager.Console.exe` |

下面以 Agent 为例。`<Agent 服务目录绝对路径>` 是复制完 Agent `overlay/` 并准备配置后的目录；`<域或计算机名\已确认服务账号>` 是操作员已准备的实际账号。Console 安装时同时将服务名称、入口文件及服务目录改为上表对应值。示例适用于带密码的本地/域服务账号，密码通过安全提示输入，不写进命令或文件；gMSA 等无密码身份使用操作员既有管理方式配置。

```powershell
$taskServiceName = 'IISManager.Agent'
$taskInstallDirectory = '<Agent 服务目录绝对路径>'
$taskServiceAccount = '<域或计算机名\已确认服务账号>'
$taskExecutable = Join-Path $taskInstallDirectory 'IISManager.Agent.exe'
if (-not (Test-Path -LiteralPath $taskExecutable -PathType Leaf)) { throw 'SERVICE_EXECUTABLE_NOT_FOUND' }
if (Get-Service -Name $taskServiceName -ErrorAction SilentlyContinue) { throw 'SERVICE_ALREADY_EXISTS_USE_UPDATE_STEPS' }
$taskCredential = Get-Credential -UserName $taskServiceAccount -Message '输入已确认的服务运行账号凭据'
if ($null -eq $taskCredential) { throw 'SERVICE_CREDENTIAL_REQUIRED' }
$taskBinaryPath = '"{0}" --contentRoot "{1}" --environment Production' -f $taskExecutable, $taskInstallDirectory
$taskServiceParameters = @{
    Name = $taskServiceName
    BinaryPathName = $taskBinaryPath
    DisplayName = $taskServiceName
    Description = 'IISManagerTool 独立管理服务'
    StartupType = 'Manual'
    Credential = $taskCredential
}
New-Service @taskServiceParameters -ErrorAction Stop
```

可执行文件和内容根均保留双引号，适用于含空格路径。这里先注册为手动启动，验收成功后再设置自动启动。`New-Service` 及其账号/路径参数属于 [Windows PowerShell 5.1 正式命令](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/main/reference/5.1/Microsoft.PowerShell.Management/New-Service.md)；这些示例未在目标 Windows PowerShell 5.1 服务环境执行验收。

安装后使用该组件的固定名称检查实际服务身份并启动，Agent 验收完成后再启动连接它的 Console：

```powershell
$taskServiceName = 'IISManager.Agent' # Console 主机改为 IISManager.Console
Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $taskServiceName) | Select-Object Name, State, StartName
Start-Service -Name $taskServiceName -ErrorAction Stop
Get-Service -Name $taskServiceName
```

`Running` 只代表服务进程状态，不能视为证书、IIS 身份、账本和业务全部通过。若启动失败，保留配置及日志，检查安全错误码、实际账号、运行时、内容根、证书用途/信任/撤销和权限；不要关闭证书校验、执行策略或安全机制。

## 更新已安装组件

更新不重复 `New-Service`，不改变既有服务账号。操作员先只读记录原服务身份、启动类型与入口参数；确认当前运行配置、数据目录及备份位置，暂停该控制台入口的新提交，并等待或核实 `queued`/`running` 任务。不得通过清空账本让任务消失。

1. 将完整新包传入站点外运维目录并保留结构，人工确认目标组件与文件传输正确。
2. 停止本次要更新的实际管理服务，确认它已停止。这里停止的是 IISManagerTool 服务，不是业务应用池，也不要求全站停机。若两组件都更新，先停止 Console 入口，再停止相应 Agent；等待既有任务结束或记录未知结果。
3. 在受限、服务目录之外的位置备份旧程序、现场 `appsettings*.json`、`NLog.config`、日志和账本；账本位于外部绝对目录时单独备份。保留目录权限和实际服务注册信息，使用操作员已验证的备份方式。备份结果与文件集合由人工确认，不将一条复制命令的成功当作完整备份验收。
4. 仅将新包中该组件 `overlay/` 内的全部运行文件按相对路径覆盖到对应独立服务目录，包含依赖及 Console 的 `wwwroot`。保留现场配置、`data/`、外部状态目录、`logs/`、证书与密钥库，不删除无关文件，不复制配置模板覆盖现场。
5. 人工比较配置模板，仅在必要时合并新字段和 NLog 规则；本版无数据库迁移、配置加密迁移或业务协议密钥轮换。程序文件完整性由操作员确认，未确认前保持服务停止。
6. 启动 Agent 并完成最小只读验收，随后启动 Console 并验收入口；符合验收后恢复管理访问。配置变化须随相应服务重启生效。

手动停止并等待该服务的示例：

```powershell
$taskServiceName = 'IISManager.Agent' # 仅填写本次更新组件的固定名称
Stop-Service -Name $taskServiceName -ErrorAction Stop
$taskService = Get-Service -Name $taskServiceName -ErrorAction Stop
$taskService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(60))
Get-Service -Name $taskServiceName
```

超时或无法确认停止时不要继续覆盖文件，不按进程名称批量结束 `dotnet`。备份、覆盖、配置比较及启动都应作为操作员分开的步骤完成，不执行未经确认的自动部署或回滚。

服务确认停止后，可在同类管理员 PowerShell 5.1 窗口逐步执行以下文件备份示例。备份位置必须位于服务目录之外且事先限制访问；Console 同时替换名称和安装目录。此命令只备份，不覆盖服务、不启动服务：

```powershell
$taskServiceName = 'IISManager.Agent'
$taskInstallDirectory = '<当前 Agent 服务目录绝对路径>'
$taskBackupDirectory = '<本次新建的受限备份目录绝对路径，不能位于服务目录内>'
if ((Get-Service -Name $taskServiceName -ErrorAction Stop).Status -ne 'Stopped') { throw 'SERVICE_NOT_STOPPED' }
if (-not (Test-Path -LiteralPath $taskInstallDirectory -PathType Container)) { throw 'SERVICE_DIRECTORY_NOT_FOUND' }
if (Test-Path -LiteralPath $taskBackupDirectory) { throw 'BACKUP_DIRECTORY_ALREADY_EXISTS' }
& robocopy.exe $taskInstallDirectory $taskBackupDirectory /E /COPY:DATS /DCOPY:DAT /R:1 /W:1 /XJ /NFL /NDL /NP /NJH /NJS
if ($LASTEXITCODE -ge 8) { throw 'BACKUP_COPY_FAILED' }
```

该示例复制内容、属性、时间及 DACL，排除联接；它不证明所有者、组或审计 SACL 已完整备份。参数和退出码依据 [Robocopy 正式说明](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy)。所需权限记录由操作员已有备份工具完成并核对，不能将未记录的权限声称为已恢复。外部绝对状态目录须在服务停止后单独备份，备份正确性仍由人工确认；回退时保留当前账本，不用旧账本覆盖。

## 启动后的验收

通过真实服务身份和管理网络进行以下最小验收，详细用例见 [Windows 运行与验收](windows-operations.md)：

1. 确认两个组件运行于 Production，实际身份与准备权限一致，状态与日志路径正确；Agent 为 Windows 模式。
2. 在允许的管理客户端打开 `https://<实际 Console 运维域名>:7443/`，通过页面查看各 Agent 信息、快照、池配置和证书。使用真实客户端完成 mTLS、证书名称/链/撤销验收；错误客户端证书与非白名单入口应被拒绝。
3. 在专用非生产资源上验证预检查、池局部更新、闲置动作和站点证书/绑定/目录检查；默认创建的池与站点应停止。完成站点真实 TLS 握手和关联应用读取验收，不以管理员读配置通过代替实际身份验收。
4. 停止一个业务测试池后管理入口仍可使用。查询原编号的历史任务，检查两端 NLog 关联和账本；重启出现的 `unknown`/`partial` 只能只读核实，不自动重放。
5. 对照 [API 契约](api.md) 验收去重、过期、指纹冲突及任务状态语义。页面的“已只读核实，保留记录并继续管理”只解除当前浏览器操作锁，不修改任务结果；`queued`/`running` 不能这样解锁。

本轮 Windows SDK publish、运行依赖、入口 PE 架构、DLL/PDB 配对及包范围检查见 [本轮发布检查记录](package-validation.md)。首版开发阶段的 23 项自测与本机模拟联调证据见 [开发验证记录](validation.md)，该文件中的“当时未 publish”属于前次验证范围；本次打包复用已通过且输入未变的用例。页面外观见 [控制台预览](console-preview.jpg)。这些结果不代表已完成 Windows 服务安装、真实 IIS 写入、ACL、证书私钥、HTTP.sys 或生产业务验收。

人工验收通过后，可按运维约定将相应服务设为自动启动：

```powershell
$taskServiceName = 'IISManager.Agent' # Console 主机改为 IISManager.Console
Set-Service -Name $taskServiceName -StartupType Automatic -ErrorAction Stop
Get-Service -Name $taskServiceName
```

## 回退

出现问题时先停止受影响的管理服务，并保留本轮日志、当前账本、配置和现状证据。程序回退由操作员从已确认的旧版本备份恢复运行文件集合；新版本新增文件若影响旧版，按准确差异人工处理，不用全目录删除或通配清理。保留当前现场配置、证书、日志与任务状态，不能用旧备份账本覆盖当前账本，也不能清空去重记录。

若确需回退某项配置，由操作员核对该字段与旧版兼容性、原权限及实际身份后单独处理，不自动替换整个配置或恢复所有父目录 ACL。原服务账号、入口、Production 环境及内容根保持已确认配置。

恢复旧程序后，先只读核实现场 IIS 状态和原操作编号，再启动服务并执行最小验收。`unknown`、`partial` 或查询不到记录均不能证明此前操作未发生；回退程序不会撤销已执行的 IIS 配置或应用池动作，也不能自动重发这些操作。

## 已安装服务的认证与来源模式

当前 EXE 更新保留已安装服务的认证模式、来源模式、令牌及现场配置。旧 HTTPS 配置缺少 `TransportMode` 时按 `Https` 兼容，缺少 `SourceAccessMode` 时按 `Allowlist` 兼容；更新不会改成 HTTP，也不会把原来源限制自动放宽为全部内网。

安装器会比对完成归属记录与实际配置中的模式，直接修改模式会造成归属漂移并阻止后续更新。当前版本不提供已安装服务的模式转换功能，也不应修改归属记录绕过检查。已安装服务需要变更认证或来源模式时，应另行制定维护方案，并在目标 Windows 测试环境验证转换和后续更新；不按本说明直接修改现场模式。停服务、受限备份、保留配置权限和账本、两端一致及读回验收仍是该维护方案的必要条件。

尚未安装服务时，直接使用新版本向导的默认 HTTP 和自动内网访问即可，无需填写管理来源 IP/CIDR 或进行旧服务的配置迁移。普通 VPN 使用私有地址或私有网关 NAT 后的实际来源时直接适用；公网来源地址不自动允许。
