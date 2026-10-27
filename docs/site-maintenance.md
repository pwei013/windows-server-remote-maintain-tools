# 站点、子应用与虚拟目录配置维护

入口为 `/maintenance.html`，也可从工作台站点行、展开的应用程序行或虚拟目录行点击“编辑”，自动定位服务器及资源。读取沿用登录会话及对应服务器的只读权限；配置维护需要该服务器的 `configure` 权限。权限接口不可用时保留只读资源展示并禁用提交，不按缺省值放行。新旧页面使用同一 CSRF、操作编号、预检查和持久化任务协议。

资源列表只保留一个“编辑”入口，应用池选择统一放在编辑页。站点编辑中的“修改应用池”对应 IIS 高级设置的根应用池，不覆盖独立子应用的池；子应用编辑修改所选应用的池。虚拟目录编辑页可分别勾选物理目录和所属应用池，两项一起修改时使用同一个操作编号、一次预检查和一次确认提交。虚拟目录没有独立应用池，改池影响其所属应用及该应用的全部目录；页面和预检查会明确这一范围。

工作台入口使用 `server`、`site`、`action`、`application`、`directory` 参数定位，不再提供独立改池按钮。旧站点/应用编辑链接的 `pool=1` 仍兼容，用于预选修改池字段。重复或不兼容的参数、已消失的目标及不可用服务器会阻止提交并提示重新选择，不静默改选列表首项。工作台正在执行或存在 unknown/partial 任务时编辑入口禁用；直接进入维护页仍须通过服务器端配置权限和执行校验。普通编辑默认不勾选任何修改项；成功后按持久化读回结果更新当前目录、继承池及编辑字段。

## 支持范围

| 动作 | target | parameters |
|---|---|---|
| `site.update` | `siteName` | 可选 `physicalPath`、`applicationPoolName`、`bindings` |
| `application.create` | `siteName`、`applicationPath` | 必填 `physicalPath`、`applicationPoolName` |
| `application.update` | `siteName`、`applicationPath` | 可选 `physicalPath`、`applicationPoolName`，至少一项 |
| `virtualDirectory.create` | `siteName`、`applicationPath`、`virtualDirectoryPath` | 必填 `physicalPath` |
| `virtualDirectory.update` | `siteName`、`applicationPath`、`virtualDirectoryPath` | 可选 `physicalPath`、`ownerApplicationPoolName`，至少一项 |

省略字段保持现值，显式 null、重复字段及未知字段均拒绝。`site.update` 的 `bindings` 是提交后的完整绑定集合；未提交这个字段时不修改任何绑定。根应用通过 `site.update` 维护，应用动作仅针对子应用；虚拟目录路径相对所属应用，不能使用 `/`，不将目录隐式转换为应用，也不跨另一子应用维护目录。

`virtualDirectory.update.ownerApplicationPoolName` 为已有池名称，明确修改 `target.applicationPath` 指向的所属应用；不接受目录级 `applicationPoolName`。只改池时省略 `physicalPath`，全部物理映射保持原样；两项都提交时，Windows 在同一配置提交中修改指定目录映射和所属应用池，完整拓扑独立读回。创建虚拟目录不允许这个字段，继续继承原有池。两端必须更新到支持该字段的版本；旧 Agent 会拒绝新参数，不能在产生此类账本后无条件降级或删除记录绕过兼容检查。

例如 `/api` 应用中的 `/assets` 对应完整路径 `/api/assets`，与根应用中的 `/assets` 为两个不同目标。应用和目录路径必须规范：以 `/` 开头，无末尾 `/`、空路径段、`.`/`..`、百分号编码、反斜杠、控制字符、查询参数或片段。重名按 IIS 不区分大小写的规则拒绝。

本期不删除或改名资源、不复制文件、不创建物理目录、不写 ACL、不修改连接凭据、不导入证书。物理目录必须已存在、属于 `Agent:AllowedSiteRoots`，且从根至目录的完整链不包含重解析点、符号链接或联接。Windows 端沿用目录句柄租约及保守身份/DACL 校验，句柄保留到配置与状态独立读回。目标目录已使用独立连接身份时拒绝维护，避免把应用池身份检查误当作真实访问证明。

从虚拟目录编辑页修改所属应用池时，还会检查该应用的根目录及其他虚拟目录在新池身份下的目录访问证明，并将目录身份与权限摘要纳入预检查指纹；相关句柄保留至读回结束。存在无法核实的独立目录连接身份时拒绝改池。

## 停止条件及影响范围

所有五类维护动作均要求目标站点已 `Stopped`，该站点原关联各应用池和方案中新关联池均已 `Stopped`。共享池的其它站点及应用会出现在预检查影响范围；操作员应先在工作台核对并单独处理停止动作。本页不隐式启动、停止或回收站点/应用池，也不因保存配置自动恢复运行。

读取快照、检查参数及显示差异不修改 IIS。Windows 共享配置模式禁止这些配置写入。配置、关联关系、状态、目录身份/ACL、证书或 HTTP.sys 证据发生漂移时，执行前校验拒绝原方案。

## HTTPS 与绑定保护

绑定保留原有协议、端口、主机名、证书有效期/私钥/主机名/EKU、保留端口、IIS 绑定冲突及本机 TCP 占用检查。只有 HTTP/HTTPS 常规绑定可由本页编辑，不重写集中证书存储、高级 SSL 标志或自定义 HTTP.sys 客户端证书/吊销策略。

改变或移除 HTTPS 绑定前，须证明相关 HTTP.sys 记录与本站旧绑定和证书匹配且由本站独占；被其它站点共享、属于外部服务或无法核实的记录拒绝覆盖。未变化的 binding 对象保持原样；同一端点换证书只更新证书哈希。执行后重新读取站点、各相关池状态、未提交配置属性、全局 TLS 记录，核对新记录、旧记录移除和未涉及记录保持一致。IIS 配置已写入但 TLS 或配置读回不符时返回 `partial`，不会自动补写、回滚或重放。协议切换时仍须通过原有监听冲突规则，不能通过关闭校验绕过占用。

## 页面和操作协议

1. 选择服务器、站点和动作，勾选需要修改的字段。未勾选字段不进入请求。
2. 点击“预检查并查看差异”，使用 `POST /api/v1/servers/{id}/operations/validate`；核对字段差异、全部受影响应用和安全提示。
3. 勾选确认后提交至 `POST /api/v1/servers/{id}/operations`。预检查和提交使用同一个操作编号，携带配置指纹和五分钟有效期。
4. 提交前在浏览器独立保留 `{serverId,operationId}` 索引；不能保存索引时禁止发送写请求。不会把请求正文、目录或认证信息写入索引。
5. 通过 `GET /api/v1/servers/{id}/operations/{operationId}` 只读查询原任务。结果区域读取 `agentRecord.before` / `agentRecord.after` 展示实际配置。排队/执行中可以轮询，未知/部分完成锁定后续写入。刷新页面会恢复原索引；查询不到或网络失败不能作为操作未发生的证据。

新操作沿用 `OperationCoordinator` 串行队列、幂等编号、持久化、有效期与执行前指纹检查。`Before`、`Proposed`、`After` 为完整 `SiteTopology`；Windows 写后使用新的 `ServerManager` 独立读回，不将 `CommitChanges` 返回等同于验收。失败、未知或部分完成均不自动重放或回滚。

新增 `OperationTarget.ApplicationPath` 和 `VirtualDirectoryPath` 在 null 时省略，以保留旧请求 JSON 和规范哈希。旧 Agent 不认识新动作，不能在旧 Agent 上使用本页执行配置维护；两端须成套更新。旧客户端继续使用原动作，但不能假定更早 Console 能读取新动作的账本，降级前需核对格式并保留最新账本，不删除数据绕过去重。

## 验证边界

专项 C# 测试通过内存 Simulation 和真实临时目录覆盖局部更新、应用/目录归属、停止条件、路径越界与重解析点、指纹冲突、幂等账本以及 Windows 纯 TLS 归属/读回比较。页面测试使用合成资源和模拟 fetch，覆盖差异确认、CSRF、原编号复用、只读权限、未知结果锁定和存储失败阻止提交。

2026-10-27 本次合并编辑的维护专项 29/29、工作台与维护页前端 150/150 通过，覆盖根/子应用归属、只改池、两项联合更新、原编号复用、未勾选字段保留、权限和未知结果锁定以及保存后的界面刷新。隔离 Development Simulation HTTP 与实际页面均完成目录路径和所属池的联合修改及池单项修改，并独立读取确认应用根路径、兄弟目录及其他应用保持原样。Agent 与 Console 的 Windows Server x64、net10.0、Release、显式框架依赖构建均为 0 警告、0 错误；本轮未生成部署包。

页面证据：[目录及应用池同页编辑](ui-evidence/pool-inline-edit-20261027/editor.png)、[保存及独立读回结果](ui-evidence/pool-inline-edit-20261027/result.png)。模拟服务、账本、配置及进程在验证后清理。

这些本地测试不构成 Windows/IIS 实机、真实应用池身份、ACL、HTTP.sys 或 HTTPS 握手验收。部署后应由操作员在授权测试站点，按既有人工更新与验收流程核对；不得用生产业务写入代替安全测试。
