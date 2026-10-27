# MKY 服务器远程管理工具

源码仓库：[pwei013/windows-server-remote-maintain-tools](https://github.com/pwei013/windows-server-remote-maintain-tools)。包含 IIS 管理 Agent、集中管理 Console、Windows 图形安装器、测试及说明；部署目标为 Windows Server x64，主要项目使用 .NET 10，SDK 版本由 `global.json` 指定。

```sh
git clone https://github.com/pwei013/windows-server-remote-maintain-tools.git
cd windows-server-remote-maintain-tools
dotnet restore IISManagerTool.slnx
dotnet build IISManagerTool.slnx -c Release --no-restore
```

仓库不包含部署包、构建缓存、本地配置、任务数据及现场诊断资料。安装器工程还需要另行准备 `Agent.zip` / `Console.zip` 内嵌载荷；现有打包脚本依赖本机的 `create-dotnet-deployment-package` Skill 和已核验历史基线，克隆后不能直接将安装器解决方案视为完整发布输入。下方历史交付路径与截图属于本地开发记录，部分对应文件不随源码上传；连接凭据由操作员在本地配置，不能提交到仓库。

2026-10-27 已增加 Agent 服务卸载并生成 [1.0.5.0 安装与卸载 EXE](/Users/pengwei/Publish/IISManagerTool/Agent安装包-含卸载-20261027-win-x64/IISManager.Setup.exe)。首页选择 Agent 后点击“卸载 Agent（保留程序、配置与数据）…”，先只读核对再确认执行，仅停止并移除固定 Agent 服务，程序、配置、日志、账本、防火墙及受管 IIS 资源均保留。内嵌两端载荷与下面 1.0.4.0 完全一致；Windows x64、Release、自包含，61/61 自测及载荷核验通过，尚未执行 Windows 实机安装或卸载。见[卸载说明](docs/agent-uninstall.md)、[安装包说明](/Users/pengwei/Publish/IISManagerTool/Agent安装包-含卸载-20261027-win-x64/安装说明.md)和[本次交付核验](docs/agent-installer-uninstall-20261027.md)。

2026-10-27 已生成 [Agent 完整 EXE 安装包 1.0.4.0](/Users/pengwei/Publish/IISManagerTool/Agent安装包-20261027-win-x64/IISManager.Setup.exe)，包含本次应用池 AutoStart 修复及统一编辑后端，目标 Windows Server x64、net10.0、Release、自包含；新装无需增量基线。运行统一向导并选择 Agent，已有实例选“更新”；本 EXE 的 Console 载荷保持 2026-10-19 历史版，Console 新功能仍使用下述独立更新包。见[安装说明](/Users/pengwei/Publish/IISManagerTool/Agent安装包-20261027-win-x64/安装说明.md)和[安装包核验](docs/agent-installer-20261027.md)，未执行 Windows 安装或部署。

当前源码已让新建应用池遵循 AutoStart 选项，创建后不再强制停止；站点、应用程序和虚拟目录列表统一使用“编辑”入口修改配置与关联池，虚拟目录可在同页修改物理目录和所属应用池，统一预检查及保存。规则与验证见[池创建修复](docs/pool-creation-fix.md)和[配置维护说明](docs/site-maintenance.md)。2026-10-27 已生成相对下列 V2 基线的[Windows x64 未压缩增量包](/Users/pengwei/Publish/IISManagerTool/应用池与统一编辑-20261027-win-x64/)，Agent 6 个、Console 22 个覆盖文件，先更新 Agent 再更新 Console，现场配置保留；见[人工部署说明](/Users/pengwei/Publish/IISManagerTool/应用池与统一编辑-20261027-win-x64/部署说明.md)及[包核验记录](docs/inline-edit-package-20261027.md)，未部署。

2026-10-27 已补齐操作后自动业务健康验证和 FREB 请求级失败跟踪，并合并登录 V3 签名修复，生成 [Windows x64 完整累计增量包](/Users/pengwei/Publish/IISManagerTool/完整优化累计更新-20261027-v2-win-x64/)。Agent 6 个、Console 76 个覆盖文件；只需应用本次版本，先 Agent 后 Console，不需先叠加旧包或登录补丁；最终 V2 同时纳入应用池创建后状态时序修复。使用与验证见 [补齐说明](docs/completion-20261027.md)、[最终包核验](docs/completion-package-20261027.md)，未执行部署。

2026-10-25 已集中完成后续优化并生成 [Windows x64 累计增量包](/Users/pengwei/Publish/IISManagerTool/集中优化累计更新-20261025-win-x64/)，包含首期及此前 v4，无需先更新首期包。Agent 6 个、Console 70 个覆盖文件；先 Agent 后 Console，由人工统一更新。功能与验证见 [集中优化说明](docs/batch-optimization.md)、[包核验记录](docs/batch-package-validation.md)。旧首期包保留为历史版本，未执行现场部署。

2026-10-25 已生成首期监控功能的 [Windows x64 未压缩增量包](/Users/pengwei/Publish/IISManagerTool/首期监控功能-20261025-win-x64/)，包含 Agent 6 个、Console 37 个覆盖文件，累计此前 v4 修订。先 Agent 后 Console，监控目录与服务身份权限、完整传输和回退见 [部署说明](/Users/pengwei/Publish/IISManagerTool/首期监控功能-20261025-win-x64/部署说明.md)，发布与最终读回见 [包核验记录](docs/monitoring-package-validation.md)。目标 `win-x64`、`net10.0`、`Release`、自包含，未执行现场部署。

当前源码新增“服务器总览”、站点及子应用只读健康检查和证书 30／15／7 天预警。健康检查默认停用，启用后由 Console 定时调度 Agent；总览刷新只读取管理数据及已保存结果。入口、可选项目/环境分组、保存与恢复边界见[监控使用说明](docs/monitoring.md)，新增路由见[API 契约](docs/api.md#服务器总览与健康检查)。部署包见上方，本期未执行部署，真实 Windows/IIS 与业务检查地址仍需现场验收。

当前源码的操作记录已增加登录操作人姓名、姓名搜索及零代码平台保存状态；平台“操作记录”表单、列表及查询/新增/更新接口已创建并独立回读。Console 复用现有应用连接保存审计信息，保留本地账本和未知写入核对机制；旧记录不补造姓名。接入、同步及现场验证边界见[操作记录说明](docs/operation-history-zero-code.md)。2026-10-14已提供[Windows x64 未压缩增量包](outputs/MKY服务器远程管理工具-操作记录姓名与持久化-20261014-win-x64/README.md)，只更新Console、18个覆盖文件；人工更新及新版账本回退限制见[部署说明](outputs/MKY服务器远程管理工具-操作记录姓名与持久化-20261014-win-x64/部署说明.md)。未部署。

源码已增加主界面及操作记录页的“修改密码”入口，成功后退出该用户的所有会话并要求重新登录；原 HTTP 登录支持保留。2026-10-13已提供[修改密码 Windows x64 未压缩增量包](outputs/MKY服务器远程管理工具-修改密码-20261013-win-x64/README.md)，18个覆盖文件、无需新增配置，人工更新见[部署说明](outputs/MKY服务器远程管理工具-修改密码-20261013-win-x64/部署说明.md)。功能验收见[修改密码说明](docs/change-password-validation.md)，发布核验见[部署包验证记录](docs/change-password-package-validation.md)，未部署。

当前源码已按公司 Logo 版方案 1 接入应用用户登录、退出和 Token 会话保护，站点标注查询/新增/修改携带当前用户的 Bearer Token。按用户要求，登录默认允许 HTTP；BrowserHttps 可独立选择管理入口 HTTP 或 HTTPS，保留原 Agent 通信配置。配置及验证见[应用用户认证说明](docs/app-user-authentication.md)。此前[登录认证 HTTP 兼容 Windows x64 未压缩增量包](outputs/MKY服务器远程管理工具-登录认证-HTTP兼容-20261011-win-x64/README.md)包含 Console 50 个覆盖文件；配置合并、可选 HTTPS 证书及人工更新见[部署说明](outputs/MKY服务器远程管理工具-登录认证-HTTP兼容-20261011-win-x64/部署说明.md)。本轮发现旧包新增配置及登录页修改，恢复原清单基线供打包，未改动旧包或现场。

当前源码已按别名与标签方案 1 增加站点别名、多标签、组合搜索及右侧编辑面板，Console 接入零代码应用 1001 的“站点标注”查询、新增和修改接口。按已确认结构创建并发布表单、六个字段、布局和主视图；运行连接默认关闭，操作员合并 [空值配置模板](docs/config/站点标注-零代码配置模板.json) 并配置调用连接后启用。接入和验收见 [站点标注说明](docs/site-annotations-zero-code.md)。

最新 [站点别名与标签部署包](outputs/MKY服务器远程管理工具-站点别名与标签-20261011-win-x64/README.md) 已累计包含本功能、箭头居中及此前站点启停/双子表/菜单调整；目标Windows Server x64、net10.0、Release、自包含，提供Agent6/Console37文件增量及1.0.2.0 EXE，保留临时目录启动修复。已更新上一站点启停版Agent时本次只更新Console，配置人工合并，完整传输及更新见 [新版部署说明](outputs/MKY服务器远程管理工具-站点别名与标签-20261011-win-x64/部署说明.md)。下面的v2及更早包保留历史版本。

当前源码新增 IIS 站点启动、停止和重新启动，保留站点及应用池配置，接入预检查、影响范围确认、串行队列和操作记录；使用时需要同时更新 Agent 与 Console。源码验证与 Windows/IIS 人工验收边界见 [站点启停验证](docs/site-lifecycle-validation.md)。站点列表已按选定的方案 2 改为行内展开“应用程序”和“附加虚拟目录”两张子表，显示目录所属应用、继承应用池、物理目录及完整访问地址，支持收起、筛选与应用池跳转；视觉与交互验收见 [设计验收](design-qa.md)。

后续显示调整已纳入新版 v2：应用子表不再重复显示站点根应用，空子表、无子项的展开入口及零数量摘要隐藏；根应用下的附加虚拟目录仍显示。左侧菜单及键盘导航顺序为站点、应用程序池、Windows 服务。

历史 [站点双子表 v2 部署包](outputs/MKY服务器远程管理工具-站点双子表-v2-20261010-win-x64/README.md) 包含累计 Agent 6 文件、Console 31 文件增量及安装器 1.0.1.0，目标 Windows Server x64、net10.0、Release、自包含。安装器同时修复 Windows Server 2019 会话临时目录缺失导致的 WinForms 启动崩溃，启动前创建并验证目录、捕获初始化异常；原因见 [启动故障说明](outputs/MKY服务器远程管理工具-站点双子表-v2-20261010-win-x64/启动故障说明.md)。整体传输与人工更新见 [v2 部署说明](outputs/MKY服务器远程管理工具-站点双子表-v2-20261010-win-x64/部署说明.md)，发布证据见 [v2 验证记录](docs/site-child-tables-v2-package-validation.md)。已完成上一组合包的 Agent，本次业务载荷相同，仅需更新 Console；未完成旧升级时仍先 Agent 后 Console。

站点启停与双子表组合更新包位于 [Windows x64 更新包](outputs/MKY服务器远程管理工具-站点启停与双子表-20261010-win-x64/README.md)，包含 Agent 6 文件、Console 31 文件增量及内嵌新版两端完整载荷的 EXE。目标 Windows Server x64、net10.0、Release、自包含，运行时 10.0.12；先 Agent 后 Console，保留现场配置与账本。安装器归属服务用 EXE 更新，手工增量需匹配现场基线。完整传输、人工更新与回退见 [部署说明](outputs/MKY服务器远程管理工具-站点启停与双子表-20261010-win-x64/部署说明.md)，本次发布证据见 [组合包验证记录](docs/site-child-tables-package-validation.md)。下列历史包保持原版本。

Console 名称版更新包位于 [MKY 服务器远程管理工具 Console 更新包](outputs/MKY服务器远程管理工具-Console-20261007-win-x64/README.md)，包含 13 文件增量及维护归属的 EXE 入口，目标 Windows Server x64、net10.0、Release、自包含；只更新 Console，无配置变更，Agent 载荷保持上一版。更新步骤见 [部署说明](outputs/MKY服务器远程管理工具-Console-20261007-win-x64/部署说明.md)。

当前 Console 工作台、操作记录页、浏览器标题和品牌名称统一为“MKY 服务器远程管理工具”。Agent 创建服务缺项诊断已纳入最新组合更新包，上述历史 Console 名称版包的 Agent 仍保持旧载荷；创建配置及权限要求保持原样，现场启用步骤见 [Windows 服务管理](docs/windows-operations.md#windows-服务管理)。

可管理服务列表版 Console 更新包位于 [Console 更新包](outputs/IISManagerTool-Console-ManagedServices-20261007-win-x64/README.md)，包含 4 文件增量及维护安装归属的 EXE 入口，目标 Windows Server x64、net10.0、Release、自包含；本次仅更新 Console，无配置变更，Agent 无需更新。服务列表、数量和查询只显示可管理项，完整数据继续用于依赖选择。更新方式见 [部署说明](outputs/IISManagerTool-Console-ManagedServices-20261007-win-x64/部署说明.md)。

当前 Windows 服务功能升级包分别位于 [Agent 升级包](outputs/IISManagerTool-Agent-WindowsServices-20261007-win-x64/README.md) 和 [Console 升级包](outputs/IISManagerTool-Console-WindowsServices-20261007-win-x64/README.md)，包含 6 / 22 个增量运行文件与内嵌新版两端完整载荷的 EXE 更新入口。目标 Windows Server x64、net10.0、Release、自包含，运行时 10.0.12。先 Agent 后 Console，保留现场配置与账本；Agent 默认服务只读，业务管理范围按包内说明准备。本轮发布核验见 [升级包验证记录](docs/windows-services-package-validation.md)。

上一版工作台与操作记录 Console 更新包位于 `outputs/IISManagerTool-Console-Workbench-20261006-win-x64/`，包含 18 文件增量及内嵌完整 Console 的 EXE 更新入口，目标 Windows Server x64、net10.0、Release、自包含。该历史版只需更新 Console，无配置变更；完整传输、人工更新和验收步骤见 [部署说明](outputs/IISManagerTool-Console-Workbench-20261006-win-x64/部署说明.md)。

应用与虚拟目录展示版完整安装包位于 `outputs/IISManagerTool-Setup-SiteTopology-20261006-win-x64/`，包含新版 Agent 和 Console 的 Windows x64 自包含载荷，可新装或更新。已有环境先逐台更新 Agent，再更新 Console，完整步骤见 [安装说明](outputs/IISManagerTool-Setup-SiteTopology-20261006-win-x64/安装说明.md)。

前一版 Console 关联站点与绑定地址更新包位于 `outputs/IISManagerTool-Console-SiteNavigation-20261006-win-x64/`，目标 Windows Server x64、net10.0、Release、自包含。该历史包仅更新 Console，未包含本次新增的应用与虚拟目录详情；传输范围、手工增量条件和验收见 [该版部署说明](outputs/IISManagerTool-Console-SiteNavigation-20261006-win-x64/部署说明.md)。

首版提供集中式 Web 控制台与独立 Windows Agent。浏览器只访问控制台；Agent 操作本机 IIS。支持应用池启动、停止、回收、创建、局部修改，以及创建站点。应用池设置包含闲置超时分钟数和终止／挂起动作。应用池可查看关联站点（含子应用），站点的 HTTP/HTTPS 绑定地址可在新标签页打开。

当前源码增加 Windows 服务列表与详情、启动、停止、重新启动、创建及启动类型调整，复用预检查、确认、串行队列和操作记录。“启动”不会将已禁用服务改为自动或手动；调整启动类型也不会立即启动或停止服务。新建服务初始停止，默认手动启动，使用服务器已存在且支持 Windows 服务协议的 EXE。工具不复制程序、不创建账号、不自动修改目录 ACL。此功能已包含在本文开头的新版 Agent 和 Console 升级包中，历史包保持原版本。

生产默认仅查询服务：`Agent:WindowsServices:AllowedNames`、`AllowedNamePrefixes` 为空，`AllowCreate=false`、`AllowedExecutableRoots=[]`。操作员按业务范围配置服务名或前缀后才允许写入；创建还须单独允许并配置可执行目录。默认创建身份仅允许 `VirtualAccount`、`LocalService`，不接收密码；不可在启动参数中放入秘密。Agent、Console 及受保护服务的依赖链不能通过此入口被中断。配置及人工验收见 [Windows 服务管理](docs/windows-operations.md#windows-服务管理)。

当前 Windows 服务列表、计数与查询仅显示可管理项，未命中配置或受保护的服务不出现在列表；禁用、过渡或控制权限不足的已纳入范围服务仍显示，并保留原因。此展示调整已包含在 ManagedServices Console 更新包中，先前 WindowsServices 包保持原版。

Windows 服务功能的构建、模拟接口、前后端自测和浏览器操作结果见 [本地验证记录](docs/windows-services-validation.md)；后续自包含发布、实际载荷及升级包检查见 [升级包验证记录](docs/windows-services-package-validation.md)。

当前源码新增“应用与目录 / 地址预览”侧栏：按站点展示根应用、子应用与附加虚拟目录，显示实际物理目录、所属池及池状态；应用池可按关联应用查看涉及的全部站点。地址预览包含应用及虚拟目录路径，可从详情跳回对应应用池。完整详情需要更新 Agent 和 Console；旧 Agent 提示升级，新接口读取失败不影响原快照和管理入口。

当前控制台按第 3 张设计改为顶部菜单与常驻 Agent 列表，支持搜索服务器并直接点击切换；当前 Agent 下展示应用池与站点入口。操作记录通过顶部菜单进入独立页面，可按 Agent、动作、状态、目标或操作编号、提交时间范围组合查询，支持每页 10 / 20 / 50 条及分页；桌面查询条件为单行，统计显示在底部分页栏。列表查询读取控制台保存的最近状态，不联系 Agent；详情可跳回工作台按原编号核对。历史查询逐条校验文件账本，读取成本随记录数增加；大量历史需评估查询耗时。对应更新包见本文开头。

当前 Console 通过已配置应用的用户认证登录，登录后的用户沿用既有管理能力；不新增本地账号、角色、审批或数据库。入口仍限定在允许的管理网络内。历史安装器默认 HTTP 内网模式；采用新版登录源码时需准备 Console HTTPS 和应用调用连接。Console 用每台 Agent 独立的令牌或双向证书连接，浏览器不会取得 Agent 令牌。

HTTP 会明文传输操作和令牌，只用于受信任内网。新装自动允许内网实际连接来源，无需填写管理来源 IP/CIDR；Console 保留访问主机名白名单和同源/CSRF 保护，Agent 保留独立访问令牌。普通 VPN 使用内网地址或内网网关转发时也无需另填 VPN IP；公网来源地址不会自动允许，程序不信任转发来源头。

管理工作台及 API 要求登录，Token 失效时重新登录。旧安装缺少 `SourceAccessMode` 时仍保留原 `Allowlist` 来源限制。认证新增配置及合成验证边界见[接入说明](docs/app-user-authentication.md)。

## 项目

| 路径 | 用途 |
|---|---|
| `src/IISManager.Contracts` | 两端 JSON 契约、动作与状态 |
| `src/IISManager.Core` | 持久化接单、去重、串行执行、恢复 |
| `src/IISManager.Agent` | 本机 IIS、Windows 服务适配器与开发模拟适配器 |
| `src/IISManager.Console` | 页面、管理网络限制、Agent 请求转发与任务记录 |
| `src/IISManager.Setup.Core` | 安装计划、生产配置和载荷安全校验 |
| `src/IISManager.Setup` | Windows 图形安装器、服务与权限执行后端 |
| `tests/IISManager.SelfTests` | 无额外测试框架的控制台自测，不访问真实 IIS 或 Windows 服务 |
| `tests/IISManager.Setup.SelfTests` | 安装边界自测与实际载荷解包验证，不执行 Windows 安装 |

两端使用 NLog 记录操作编号、来源 IP、动作、目标和结果。Agent 的状态文件用于查询、去重与恢复；控制台也先保存请求记录，再发送至 Agent。来源 IP 只用于追溯，不能当作操作人身份。

两端配置在启动时读取，修改服务器、认证模式、令牌、证书或目录策略后需要重启对应服务；不启用配置文件热加载。开发脚本显式指定工程配置目录。

## 开发验证

使用 `global.json` 指定的 .NET 10 SDK。先恢复依赖并构建 Release，再启动开发脚本；开发模式仅监听本机回环地址。

```sh
dotnet restore IISManagerTool.slnx
dotnet build IISManagerTool.slnx -c Release --no-restore
dotnet run --project tests/IISManager.SelfTests -c Release --no-build --no-restore
bash scripts/run-dev.sh
```

开发脚本启动 `http://127.0.0.1:7443` 控制台和 `http://127.0.0.1:7444` 模拟 Agent，站点示例目录为仓库 `work/demo-sites`。按 Ctrl+C 结束脚本及其子进程。模拟数据不代表 IIS、Windows 服务、证书或物理目录验收结果。

前端回归可用 Node.js 运行 `node --test tests/IISManager.Console.Frontend.SelfTests.cjs tests/IISManager.Console.History.SelfTests.cjs`，覆盖内网 HTTP 的操作编号兼容、预检查与提交编号一致性、响应丢失后的原编号保留，以及过期证书过滤、应用池名称筛选、弹层新建与成功后自动选中、网站配置保留、应用与虚拟目录归属、应用路径地址预览、跨站点关联、旧 Agent 提示及并发读取的过期响应隔离；同时覆盖 Agent 列表切换、垂直资源导航、历史条件查询、分页、原编号跳转与过期查询结果隔离。测试使用模拟 DOM 和 API，不操作真实 IIS。操作记录后端专项验证可在项目根目录运行 `dotnet run --project tests/IISManager.Transport.SelfTests -c Release --no-build --no-restore -- --operations-only "$PWD" "$(command -v dotnet)"`，需先构建该测试项目；后两个参数显式指定仓库目录与实际 dotnet 可执行文件。

## 操作语义

预检查返回配置差异、影响范围和指纹；用户确认后携带该指纹提交同一个操作编号。Agent 持久化任务后返回受理结果，IIS 与 Windows 服务写操作在同一条队列中串行执行，执行前重新验证配置与有效期。

重复编号及等价参数返回已有任务；同编号不同参数返回冲突。提交超时后先查询原编号。Agent 重启把未完成任务标为 `unknown`，不会自动重放。`partial` 或 `unknown` 必须核实现场，不能直接换编号重试。任务存储故障会阻止新的写操作。

Windows 服务通过独立读接口识别能力，旧 Agent 返回 `WINDOWS_SERVICES_UNSUPPORTED`，原 `info`、`snapshot` 和 IIS JSON 契约保留。产生新服务操作账本后不能无条件降级到旧程序：旧版不识别新增动作及字段，不能通过删除任务记录绕过去重保护。当前测试包括内存服务模拟及契约校验；真实 SCM 权限、服务身份、依赖行为和业务健康仍需 Windows 非生产环境验收。

## Windows 运行

交付目标为受 .NET 10 支持的 Windows Server、`win-x64`、`net10.0`、`Release`。旧目标机须确认实际系统生命周期与运行时支持。Agent 与控制台独立于被管理的 IIS 应用池运行。最新组合更新包及 EXE 为自包含模式，已内置运行时；首版历史手工部署包为框架依赖模式，目标机需要相应 ASP.NET Core 运行时。实际服务身份须具备所需权限，开发构建不能替代 Windows 实机验证。

内网令牌或证书配置准备与人工验收见 [Windows 运行与验收](docs/windows-operations.md)；请求字段和错误语义见 [API 契约](docs/api.md)。本仓库不会自动部署生产站点。

首版的构建、自测和模拟操作结果见 [本地验证记录](docs/validation.md)。

首版未压缩部署包位于 `outputs/IISManagerTool-20261003-win-x64/`，包括 Agent 与 Console 完整运行文件及首装配置模板。人工新装、更新和回退步骤见 [部署说明](docs/deployment-guide.md)，本次发布检查见 [部署包验证记录](docs/package-validation.md)。

当前独立 EXE 安装包位于 `outputs/IISManagerTool-Setup-20261004-firewall-fix-win-x64/`。双击 `IISManager.Setup.exe` 后选择 Console 或 Agent；新装默认 HTTP 和自动内网访问，填写服务内网 IP 及 Agent 访问令牌，无需管理来源、证书、SAN 或证书指纹。向导准备运行文件、生成配置并注册服务，也支持保留现场认证和来源模式的更新。Agent 主机需已有 IIS/WAS。图形安装器需要 Windows Server 桌面体验；实际安装由人工完成。

本版修复旧自动内网包将 `::1/128` 写入 Windows 防火墙而失败的问题。程序来源策略仍允许 9 个内网范围，Windows 防火墙使用排除 IPv6 回环后的 8 个范围。已注册但停止、尚未写入防火墙和归属记录的特定 Console 失败现场，可通过 `--complete-console-firewall --receipt '<原回执路径>'` 先只读核对，再由操作员执行一次补齐；保留已有配置、Token、程序及 ACL。未完成或未知现场不能直接重装或重放收尾。完整步骤和验证边界见 [安装说明](docs/installer-guide.md) 和 [安装器验证记录](docs/installer-validation.md)。

`outputs/IISManagerTool-Setup-20261003-win-x64/` 保留为上一版 HTTPS 安装包；该历史包不会获得新的 HTTP 内网模式。

`outputs/IISManagerTool-Setup-20261004-win-x64/` 保留为 HTTP 加精确来源白名单的历史版本；`outputs/IISManagerTool-Setup-20261004-intranet-win-x64/` 保留为原自动内网版本。新装和上述 Console 收尾使用最新的 `firewall-fix` 安装包。

安装器使用独立的 `IISManager.Setup.slnx`，避免日常跨平台业务构建要求生成安装载荷。开发机复建说明见 [安装器构建](docs/installer-build.md)。
