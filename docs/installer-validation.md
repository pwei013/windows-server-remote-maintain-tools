# 独立 EXE 安装器验证记录

## 20261004 防火墙修复与 Console 收尾版本

当前交付目录为 `IISManagerTool-Setup-20261004-firewall-fix-win-x64/`。交付目标为 Windows Server x64、Release、自包含。新装默认 `TransportMode=HttpToken`、`SourceAccessMode=PrivateNetwork`，无需管理来源输入；Agent 仍验证独立令牌，Console 仍验证 Host 和同源/CSRF。旧配置缺少来源模式时保持 `Allowlist`，更新不自动放宽既有范围。

验证日期：2026-10-04（Asia/Shanghai）。本地环境为 macOS arm64、SDK 10.0.401。修复范围是安装器防火墙规则、失败诊断和旧 Console 安装的一次有界收尾；Agent/Console 业务运行程序未改变。

| 项目 | 本版结果 |
|---|---|
| 安装器 | `IISManager.Setup.exe`，149,768,016 字节 |
| 目标 | `net10.0-windows` / `win-x64` / Release / 自包含单文件，未裁剪 |
| 内置服务 | `net10.0` / `win-x64` / Release / 自包含；无需另装 .NET |
| 内置运行时 | NETCore / ASPNETCore / WindowsDesktop 10.0.12，按组件用途 |
| 安装器 SDK 发布 | 成功，无警告或错误 |
| Windows PE | x64 `0x8664`、GUI 子系统、UAC `requireAdministrator`；未 Authenticode 签名 |
| EXE SHA256 | `7005C90D458D3DFA1C021F37255712E6F5811385FFEDF8A9FFE124D7502EC805` |

## 已有 Windows 故障证据

旧自动内网版 Console 现场回执为 `PartialOrUnknown / WINDOWS_OPERATION_UNVERIFIED`，外层异常 `TargetInvocationException / 0x80131604`。Console 服务已注册但停止，未记录防火墙写入意图；MpsSvc 和 BFE 为 Running，未找到本次安装规则。

操作员在 Windows 执行只读诊断脚本，对尚未加入系统的 COM 防火墙规则逐项设置属性：

- Name、Description、Grouping、ServiceName、ApplicationName、Protocol 和 LocalPorts 均通过。
- 全部 9 个范围在 `RemoteAddresses` 返回 `COMException / 0xD000000D`；改用官方示例属性顺序仍在此失败。
- 去掉 `::1/128` 后，8 个范围全部设置通过；去掉全部回环的 7 个范围也通过。
- 单独 IPv6 回环失败，单独 IPv4 回环通过。全部试验均未调用 Rules.Add，未新增防火墙规则。

该证据确认安装器包含的 IPv6 回环子网导致属性失败，不能解释为用户填错 IP/Host/Token，也不是 MpsSvc/BFE 停止。Windows 协议规定 IPv6 防火墙子网地址不能是回环地址。[Microsoft 防火墙地址约束](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fasp/ccb501eb-6620-4b49-befd-2b59828a28c8)

修复后的程序来源策略和归属记录仍保留全部 9 个自动内网范围；仅用于 Windows 防火墙的范围排除 `::1/128`，保持 8 个范围，包含 IPv4 回环。规则仍限定精确程序、服务和端口、Domain/Private 配置文件；令牌和 Host/CSRF 策略保持原有行为。

## 本版本地验证

- 32 项安装边界自测通过；新增 3 项检查，覆盖程序来源与防火墙地址集合分离、底层 COM 安全异常摘要、异常链上限。Release 构建无警告或错误。实际 Windows COM、防火墙及收尾写入不由这些本地自测执行。
- 新的收尾入口只接受已核对的 Console HTTP/自动内网首次安装失败现场。源码检查涵盖原受限回执、完整载荷、服务停止与记录一致、当前合法生产配置、原记录权限一致、只读审阅至执行的配置及 Owner/Group/DACL 快照无漂移、随后独立重新检查、空账本（仅可保留零字节既有 `.console.lock`）、残余规则和已有收尾拒绝、一次记录、各阶段独立读回。收尾保留当前程序、配置、Token、ACL 和原失败回执，仅补齐防火墙、归属记录并启动现有服务；结果未知保留新回执，不自动重试或回滚。
- 安装器失败信息只输出白名单状态码、失败阶段、异常类型、HResult 及必要原生错误码；异常正文、StackTrace、Token 和现场配置不进入界面诊断或回执摘要。
- Agent/Console 复用 `20261004-intranet` 版已验证的完整 SDK 发布载荷；本次将 42 个运行程序及构建输入与原输入指纹独立比对，无新增或漂移。该版原有 121 项通信检查（99 项 helper/配置、22 项 Kestrel HTTP）及其证据保留在旧包，本次未重跑。这些检查使用模拟 Agent，不包含真实 IIS 写入。
- 本次对两个真实内置载荷完整解包，独立核对路径、大小与 SHA256：Agent 348 文件、Console 356 文件，共 704 文件。每包 17 个 Windows 原生发布文件均为 x64 PE；入口 DLL/PDB 配对与运行依赖来自已核验 SDK 载荷。
- Agent 内嵌 ZIP 为 48,977,536 字节、SHA256 `6C5B2DFF57D746CB84C9EA04379FA261DE94F76CCFF3C1C6A105A9896062250D`；Console 为 48,932,354 字节、SHA256 `59695D33D3956033C2C7C107CEC72D58BAB22B10733B033A202B3407CB626903`。从本版最终 EXE 解出的安装程序集读取两个资源，大小与哈希均与上述载荷一致，不依赖外置 ZIP。
- 最终 EXE 的 255 个 bundle 条目、内置框架、Windows Forms 和 UAC 校验通过；`.text` / `.pdata` 与匹配的 SDK Windows singlefilehost 一致。68 个程序、模板、项目与发布脚本输入在本次安装器 SDK 发布前后指纹一致。
- 程序、说明、许可和证据文件冻结后生成 SHA256 清单，独立核对精确文件集合与逐文件哈希；不交付校验脚本、真实令牌、现场配置或证书私钥。旧包保持原样。

## 验证边界

本版仍未完成 Windows WinForms/UAC、新装、更新、收尾、SCM、文件 ACL、防火墙 Rules.Add、CAPI/CNG、撤销访问或真实 IIS/mTLS/业务验收。已有 Windows 证据仅确认原问题及未注册规则的属性设置；不能把 8 个范围属性通过当作系统规则已持久化或 Console 已成功安装。本机交叉构建和载荷核验也不能替代这些验收。

实际服务器安装与收尾由操作员按 [安装说明](installer-guide.md) 执行。先保留原现场和受限回执，审查入口给出的只读检查结果；成功后还须验证真实服务身份、实际管理地址、Agent 只读查询及必要的专用 IIS 测试资源。任何部分或未知结果先只读核实，不重放。

## 历史包

以下历史交付目录及各自 `installer-validation.md` 保留原有文件和验证记录。其 EXE 字节数、哈希和测试次数不能用于本版：

- `IISManagerTool-Setup-20261004-intranet-win-x64/`：自动内网来源版本；防火墙含 IPv6 回环的旧实现。
- `IISManagerTool-Setup-20261004-win-x64/`：HTTP 加精确来源白名单版本。
- `IISManagerTool-Setup-20261003-win-x64/`：HTTPS 版本。
