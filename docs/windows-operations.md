# Windows 运行与人工验收

本文件描述当前版本配置准备和非生产验收。目标为受 .NET 10 支持的 Windows Server、x64、`net10.0`、Release。20261003 手工包为框架依赖模式；当前 `IISManagerTool-Setup-20261004-intranet-win-x64` EXE 为自包含模式，新装默认无需证书、无需管理来源输入的 HTTP 自动内网访问。不能仅依据 IIS 功能版本判断 .NET 10 运行时兼容；目标 Windows Server 与实际 IIS 版本须分别确认。本次 macOS 自测和模拟联调不构成 Windows/IIS 实机验证。

截至 2026-10-03 核对的官方支持表包含 Windows Server 2016/2019/2022/2025 x64；Server 2012/2012 R2 仅在有效 ESU 条件下支持。集中管理机建议采用 2022/2025；旧服务器属于遗留环境兼容项，须另做实机验证并确认当前 ESU 状态。本项目未承诺已在这些版本上运行验收。[.NET 10 官方操作系统支持表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)

应用池创建/修改要求 IIS 具有 `idleTimeoutAction` 配置属性，即 IIS 8.5 或更新版本。OS 能运行 .NET 不表示其 IIS 具备全部功能；不满足属性要求时拒绝配置写入。

## 运行边界

控制台与 Agent 独立运行，不能放在会被本工具停止的业务应用池中。Windows Agent 使用 Windows 服务宿主，在目标机本地管理 IIS；控制台集中展示服务器和转发请求。实际部署、安装服务、调整证书或生产配置由操作员人工完成，本次开发不执行这些操作。

服务运行身份须能访问相关 IIS 配置及各自的受限配置、日志/任务状态目录；HTTPS 模式还须读取所选证书私钥。不要用宽泛目录全控权限作为通用解决方法；现有权限应先读回，再按真实身份和最小运行范围准备。

## 配置准备

请以两个项目各自的 `appsettings.json` 和 `appsettings.Development.json` 实际字段为模板。可另行准备不提交版本库的 `appsettings.Local.json` 覆盖现场配置，随后环境变量和命令行具有更高优先级。开发文件只是本机 Simulation 配置，不能用于生产。

| Agent 配置 | 作用 |
|---|---|
| `Agent:Mode` | Windows 实机使用 `Windows`；`Simulation` 仅允许 Development |
| `Agent:TransportMode` | 新装 `HttpToken`；可选 `Https`。旧配置缺少该字段时保持 `Https` |
| `Agent:SourceAccessMode` | 新装 `PrivateNetwork` 自动允许内网；旧配置缺少字段时按 `Allowlist` 保留原来源范围 |
| `Agent:Port` | Agent 监听端口，默认 7444；1024–65535 |
| `Agent:AccessToken` | `HttpToken` 模式使用的本台 Agent 独立 64 位十六进制随机令牌 |
| `Agent:AllowedConsoleNetworks` | `PrivateNetwork` 使用程序固定内网范围，无需填写；旧 `Allowlist` 使用原配置，HTTP 白名单不能为 `/0` |
| `Agent:ServerCertificateThumbprint` | `Https` 模式的 LocalMachine/My 服务器证书指纹 |
| `Agent:AllowedClientThumbprints` | `Https` 模式允许的控制台客户端证书指纹 |
| `Agent:AllowedSiteRoots` | 允许建站点的已有物理根目录，须填写真实本机目录 |
| `Agent:ReservedPorts` | 管理保留端口，默认 7443、7444 |
| `Agent:StateDirectory` | Agent 去重与恢复任务状态目录 |
| `Agent:WindowsServices:AllowedNames` | 允许操作的业务服务名，默认 `[]` |
| `Agent:WindowsServices:AllowedNamePrefixes` | 允许操作的业务服务名前缀，默认 `[]`；与名称列表均为空时只读 |
| `Agent:WindowsServices:ProtectedNames` | 额外禁止操作的服务名，默认 `[]`；固定保护 Agent、Console 和受保护服务的依赖链 |
| `Agent:WindowsServices:AllowCreate` | 是否允许注册新业务服务，默认 `false`；仍须通过名称、目录与身份策略 |
| `Agent:WindowsServices:AllowedExecutableRoots` | 创建服务使用的本机已有 EXE 允许根目录，默认 `[]` |
| `Agent:WindowsServices:AllowedAccounts` | 可创建身份，默认 `["VirtualAccount", "LocalService"]`；不接收密码 |
| `Agent:WindowsServices:OperationTimeoutSeconds` | 服务动作等待期限，默认 `60` 秒，范围 `5–300`；超时先查询原编号并只读核实 |

| Console 配置 | 作用 |
|---|---|
| `Console:BrowserHttps` | 设置为 true 时管理入口使用 HTTPS，兼容 TransportMode=HttpToken 及原 HTTP Agent 连接；需要已有有效服务器证书 |
| `Console:UserAuthentication:Enabled` | 缺省 true，生产不可关闭；显式关闭仅允许 Development 和回环绑定 |
| `Console:UserAuthentication:ValidationIntervalSeconds` | 只读本人复核间隔60–300秒，默认60；写操作仍在发送前复核 |
| `Console:TransportMode` | 新装 `HttpToken`；可选 `Https`。旧配置缺少该字段时保持 `Https` |
| `Console:SourceAccessMode` | 新装 `PrivateNetwork` 自动允许内网；旧配置缺少字段时按 `Allowlist` 保留原来源范围 |
| `Console:ListenAddress`、`Console:Port` | 管理入口绑定地址与端口 |
| `Console:AllowedHosts` | 允许的实际管理域名/IP，精确匹配，不允许 `*` |
| `Console:AllowedClientNetworks` | `PrivateNetwork` 使用程序固定内网范围，无需填写；旧 `Allowlist` 使用原配置 |
| `Console:ServerCertificateThumbprint` | `Https` 模式的控制台入口服务器证书 |
| `Console:AgentClientCertificateThumbprint` | `Https` 模式下连接 Agent 的客户端证书 |
| `Console:Servers` | 唯一编号、展示名称、Agent 根地址；HTTP 项填写 `AccessToken`，HTTPS 项填写 `ServerCertificateThumbprint` |
| `Console:TaskDirectory` | 控制台发送意图与任务结果目录 |
| `Console:AgentTimeoutSeconds` | Agent 请求超时，1–120 秒 |
| `Console:AllowDevelopmentHttp` | 仅 Development 且回环监听可开启 |

HTTP 内网模式的 Agent URL 使用 HTTP 根地址，例如 `http://192.168.1.20:7444/`，不带用户名、密码、额外路径、查询或片段。Agent 和 Console 对应服务器项的 `AccessToken` 必须一致；每台 Agent 使用独立令牌，保存在受限配置中。Console 后端附加 `X-IISManager-Token`，不会把令牌返回浏览器。两端检查实际 TCP 来源 IP，不信任代理来源头。HTTP 会明文传输操作和令牌，只用于受信任内网。

新装的 `PrivateNetwork` 自动允许 `10.0.0.0/8`、`172.16.0.0/12`、`192.168.0.0/16`、`127.0.0.0/8`、`169.254.0.0/16`、`100.64.0.0/10`、`::1/128`、`fc00::/7`、`fe80::/10`，不需要填写管理来源或查找 VPN IP。普通 VPN 为客户端分配这些地址，或经网关 NAT 后以这些地址连接时直接适用；公网来源地址不自动允许。旧 `Allowlist` 仍使用原管理来源 IP/CIDR 范围，安装器更新不会自动放宽。

HTTPS 模式使用 HTTPS 根地址。每个服务器配置的证书指纹用于附加固定身份核对，同时仍验证证书链、主机名、撤销和有效期。准备双方信任链、服务器用途、客户端用途以及服务身份所需的私钥读取权限。证书失败不会回退为 HTTP。不要在日志、回执、URL 或交付物中包含令牌、私钥、密码或证书导出密码。

控制台访问主机白名单仍须与实际访问方式匹配；开发主机名为 `127.0.0.1`/`localhost`。生产填写操作员实际使用的运维域名或 IP，不能用任意 Host 请求绕过边界。顶层 `AllowedHosts`（ASP.NET Core 主机过滤）也须同步这些主机名；`Console:AllowedHosts` 数组只填写主机名/IP、不带端口，HTTP Host 端口须等于 `Console:Port`。这与管理客户端的来源地址是不同设置，自动内网模式不会取消 Host 校验。

控制台没有账号权限系统，所有能从获准内网来源访问入口的客户端可执行完整功能。Host 校验及同源/CSRF 保护在两种认证模式下均保持；操作确认用于展示影响范围。这些检查不能替代真实操作人审计。

站点物理目录必须先由操作员准备，并授予关联应用池实际身份必要的读取权限。本工具新建的应用池固定 ApplicationPoolIdentity，已有池可能使用其他实际身份。工具不会自动创建目录、分配 ACL 或安装绑定证书。共享 IIS 配置开启时，创建池、修改池、创建站点会被拒绝。

## Windows 服务管理

控制台服务列表和数量只显示 Agent 标记为可管理的服务，即命中名称/前缀配置、未受保护且配置类型可核实的普通服务。尚未配置范围时列表为空；禁用、过渡或控制权限不足的已纳入范围服务仍保留，按钮按实际能力禁用并说明原因。完整服务数据继续用于依赖选择及 Agent 预检查。

源码支持本机 Windows 服务查询、启动、停止、重新启动、创建及启动类型调整。Console 通过独立服务接口识别新版 Agent 能力；旧 Agent 提示 `WINDOWS_SERVICES_UNSUPPORTED`，不将服务读取失败伪装成空列表。服务与 IIS 写入共用预检查、指纹、单编号任务账本和串行队列。本文引用的历史安装包未包含本次源码功能；本次不执行发布、打包或真实服务变更。

默认名称列表和前缀均为空，现有服务仅可查询；创建也默认关闭。操作员可在受限 `appsettings.Local.json` 的 `Agent` 节按现场业务范围准备以下示例，目录和前缀均为占位，不代表生产值；配置修改后需人工受控重启 Agent。

“创建 Windows 服务”按钮与管理已有服务分开判断：必须显式开启 `AllowCreate`，配置允许的名称/前缀、程序目录及运行账号，并确认 Agent 实际身份具备 SCM 创建权限。只设置服务名称或前缀不会开启创建。页面同时在操作处理中、快照未就绪或存在未核实任务时锁定创建；新的 Agent 提示会逐项列出缺少的创建条件。

```json
{
  "Agent": {
    "WindowsServices": {
      "AllowedNames": ["ExampleWorker"],
      "AllowedNamePrefixes": ["ExampleBusiness-"],
      "ProtectedNames": ["ExampleCriticalService"],
      "AllowCreate": true,
      "AllowedExecutableRoots": ["D:\\ExampleBusinessServices"],
      "AllowedAccounts": ["VirtualAccount", "LocalService"],
      "OperationTimeoutSeconds": 60
    }
  }
}
```

服务名与目录策略不代替实际 Windows 权限：Agent 身份须能查询 SCM 及目标服务，并具备对应的启动、停止、改配置或创建权限。权限不能证明时拒绝动作，不能以扩大全站 ACL 或赋予所有账号管理权限作为默认修复。固定管理服务 `IISManager.Agent`、`IISManager.Console` 和它们的依赖链保持受保护，业务服务即使命中允许范围也不能覆盖这个保护。额外关键服务通过 `ProtectedNames` 指定。

创建使用已在服务器准备好的独立进程服务 EXE 和已存在的依赖服务。普通 EXE 不会自动变成 Windows 服务；注册成功只证明 SCM 配置读回，是否能以该身份启动、读取业务配置并提供业务功能需另行验收。程序路径应位于允许根目录；不接受网络路径、脚本或驱动注册，不复制程序、不创建目录、不改变程序目录 ACL。

默认 `VirtualAccount` 解析为 `NT SERVICE\<新服务名>`，或选择 `LocalService`；运行身份只能使用 Agent 明确允许的无需密码类型。工具不接收登录密码、不创建账号、不调整服务登录特权。程序目录、业务配置、日志目录等访问权由操作员按真实身份提前准备。创建参数进入受限任务账本，参数和说明不能包含密码、令牌、连接字符串或私钥；现有服务只展示 EXE 路径，不展示完整命令行。

“启动”仅启动可启动服务，已禁用服务必须先独立预检并调整为手动或自动，再单独启动。启动类型支持自动、延迟自动、手动、禁用；修改启动类型保留程序、参数、身份、说明和依赖，不立即启停。新建服务保持停止，默认手动；选择自动/延迟自动只保存后续启动配置。停止或重启遇到运行中的依赖者时拒绝，不级联停止业务；过渡状态须等稳定后刷新。重启停止成功而启动未成功属于部分完成，无法读回属于结果未知，均不能自动回滚或重放。

升级需要 Agent 与 Console 配套。服务操作写入新账本后，旧版不识别新动作或 `serviceName` 字段，不能无条件降级，也不能删除账本规避恢复及去重；保留现场账本和受限备份，回退前须评估兼容方式。

## 发布目标

手工框架依赖产物应来自分别针对 Console 和 Agent 的 SDK publish，显式指定 `-c Release -r win-x64 --self-contained false`，包含各自入口、`.deps.json`、`.runtimeconfig.json`、托管与 Windows 原生依赖。该模式的服务器需安装对应 ASP.NET Core 10 运行时；EXE 安装包则使用完整自包含载荷，无需另装运行时。不要把 macOS 构建目录当作服务器产物。

首版已提供 Windows x64 完整运行文件手工部署包；由于没有已核验的服务器基线，两端均携带完整依赖。该历史包使用 HTTPS，配置和手工更新步骤见 [部署说明](deployment-guide.md)，发布范围见 [部署包验证记录](package-validation.md)。当前自包含 EXE 新装支持 HTTP 和自动内网访问，见 [安装说明](installer-guide.md)。程序文件只由操作员部署，日志、现场配置、任务状态和证书不应被覆盖或清空。安装器更新保留既有认证模式、来源模式和令牌，不执行模式转换；直接修改现场模式会造成归属漂移并阻止更新，需要转换时另行制定和验证维护方案。

## 非生产人工验收

以下验收使用专用应用池、站点、目录及需要的站点证书，记录实际 Windows/IIS 版本、服务身份、测试时间及结果：

1. 按实际认证和来源模式启动双方独立服务。自动内网模式检查内网及使用私有地址的 VPN 实际来源可连接、公网实际来源被拒绝；旧 `Allowlist` 按原范围验收。HTTP 模式检查正确 Agent 令牌可连接、缺少/错误令牌拒绝；浏览器返回和日志不泄露令牌。HTTPS 模式另检查真实双向证书、链/名称/撤销和私钥读取，缺少/错误客户端证书被拒绝。两种模式下 Console 错误 Host 和同源/CSRF 校验不通过时均须拒绝，伪造来源头不扩大访问范围。
2. 查询快照、池配置、关联应用和证书元数据。证书选择不暴露私钥；记录与 IIS 管理器读回一致。
3. 创建默认停止的测试池；启动、停止、回收分别读回。停止业务测试池后，管理控制台和 Agent 仍可访问。
4. 修改单个字段确认未提交字段保留，包括 idleTimeoutAction；0 分钟仍保留动作值。运行中的池修改 CLR、管道、32 位、启动模式及闲置参数被拒绝，单独停池后可修改。
5. 用现有物理目录、池及证书创建 HTTP/HTTPS/SNI 测试站点，确认初始停止。检查目录白名单、重名、实际读取权限、过期/无私钥/名称不匹配证书、保留端口、IIS 绑定及 HTTP.sys TLS 记录冲突均按预期拒绝。另行验证 IPv4/IPv6 TCP 监听检查会拒绝非 HTTP.sys 进程或无法对应已有 IIS 同协议绑定的 PID 4 占用。端点检查存在时间窗口，不能证明后续实际启动必然成功；站点证书预检查不覆盖完整信任链、在线撤销和实际私钥使用，应在测试站点实际 TLS 握手中验收。
6. 预检查后通过 IIS 管理器改动关联配置，提交或执行前发生指纹冲突，不产生新写入；临近截止的任务排队后过期不执行。
7. 同编号相同参数只执行一次；同编号不同参数冲突。制造提交后断线，先查询原编号，不能自动换编号重试。
8. 多个任务串行执行。执行期重启 Agent 后原未完成记录成为 unknown；核对现场与日志，确认没有自动重放。部分成功和结果未知必须由操作员核实。页面的“已核实后继续管理”仅解除当前浏览器操作锁，不修改原任务、不重放请求；queued/running 状态不能这样解锁。
9. 在隔离测试目录制造状态文件不可写/损坏，确认 Agent 拒绝新写入；恢复前保留现场证据，不能通过清空任务目录宣布修复。
10. 对照双方 NLog，检查编号、来源、目标、动作、时间、结果和差异；异常记录仅包含安全状态/异常类型/错误码，不含配置秘密。分别制定日志与状态文件保留及受限备份规则，保留期限不能破坏所需去重窗口。

### Windows 服务非生产人工验收

Windows 服务另使用专用非生产服务进行以下验收，不能将 macOS 内存模拟结果称为 Windows SCM 实机通过：

1. 默认配置只读，策略允许的服务可管理，Agent/Console、额外保护服务及其依赖链始终拒绝操作；无查询或控制权限时显示可核实的拒绝状态。
2. 对停止的服务启动，对运行服务停止、重启；与服务管理器独立读回比对。确认运行依赖者阻止停止/重启、过渡状态拒绝新请求、没有级联停止。管理通道始终可访问。
3. 已禁用且停止的服务分别改为手动、自动和延迟自动，确认仍停止，原程序/参数/身份/说明/依赖保留；另行启动及业务验收。修改运行服务启动类型时确认没有隐式停止。
4. 在允许目录使用已准备的服务 EXE、无需密码身份及已有依赖注册服务，确认初始停止和配置读回；再单独启动确认服务兼容性及实际身份的文件/业务读取能力。重名、越界/链接目录、无效程序、缺失依赖、未允许账号、秘密参数均须拒绝。
5. 预检查后用服务管理器改变目标或依赖状态，原指纹提交或执行前被拒绝。同编号重复只执行一次；制造重启停后未起、等待超时或断线，确认 partial/unknown 可追溯且无自动重放。原服务完整命令行及任何秘密不出现在列表、差异、日志或错误正文中。

每次成功状态仅代表该动作的读取验收；站点业务页面、数据库连接与应用健康应由操作员在目标身份和实际部署环境下另行验证。`unknown`、`partial` 或查询不到记录均不构成“操作未发生”的证据。
