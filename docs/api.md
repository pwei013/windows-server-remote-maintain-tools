# 首版 API 契约

协议版本为 `1.0`。接口使用 UTF-8 JSON、camelCase 属性名。对象只接受已定义字段；局部修改通过省略字段保留原值，不接受 `null` 代替省略。所有时间使用带时区的 ISO 8601 值，建议使用 UTC。

## 路由

| 用途 | 浏览器 → 控制台 | 控制台 → Agent |
|---|---|---|
| 取得浏览器 CSRF 会话令牌 | `GET /api/v1/session` | — |
| 配置中的服务器 | `GET /api/v1/servers` | — |
| 连接信息 | `GET /api/v1/servers/{id}/info` | `GET /agent/v1/info` |
| 站点、应用池、关联应用 | `GET /api/v1/servers/{id}/snapshot` | `GET /agent/v1/snapshot` |
| 站点及应用别名与标签 | `GET /api/v1/servers/{id}/site-annotations` | —，由 Console 查询零代码 |
| 保存站点及应用别名与标签 | `POST /api/v1/servers/{id}/site-annotations` | —，由 Console 保存并读回零代码 |
| 站点、应用程序和虚拟目录详情 | `GET /api/v1/servers/{id}/site-topology` | `GET /agent/v1/site-topology` |
| 单个应用池可编辑配置 | `GET /api/v1/servers/{id}/application-pools/{name}` | `GET /agent/v1/application-pools/{name}` |
| 可用 HTTPS 证书元数据 | `GET /api/v1/servers/{id}/certificates` | `GET /agent/v1/certificates` |
| Windows 服务列表及管理能力 | `GET /api/v1/servers/{id}/windows-services` | `GET /agent/v1/windows-services` |
| 单个 Windows 服务详情 | `GET /api/v1/servers/{id}/windows-services/{name}` | `GET /agent/v1/windows-services/{name}` |
| 操作预检查 | `POST /api/v1/servers/{id}/operations/validate` | `POST /agent/v1/operations/validate` |
| 提交操作 | `POST /api/v1/servers/{id}/operations` | `POST /agent/v1/operations` |
| 查询原操作 | `GET /api/v1/servers/{id}/operations/{operationId}` | `GET /agent/v1/operations/{operationId}` |
| 操作记录条件查询与分页 | `GET /api/v1/operations` | — |
| 多服务器总览与证书预警 | `GET /api/v1/overview` | 汇总现有 info/snapshot/site-topology/certificates/windows-services 读接口 |
| 健康检查配置与最近结果 | `GET /api/v1/health-checks` | — |
| 保存健康检查配置 | `POST /api/v1/health-checks/save` | `POST /agent/v1/health-check/validate`，仅验证目标 |
| 删除健康检查配置 | `POST /api/v1/health-checks/{id}/delete` | — |
| 手动运行已启用的健康检查 | `POST /api/v1/health-checks/{id}/run` | `POST /agent/v1/health-check` |

路径中的名称必须进行 URL 编码。控制台服务器编号来源于受限配置；浏览器不能指定任意 Agent URL。单个 Agent 只管理本机，不接受服务器编号。

服务器清单返回 `id`、`name`、`mode` 和 `hostName`；`hostName` 仅为已配置 Agent 地址的主机部分，不包含端口或认证信息。站点绑定没有主机名且监听通配 IP 时，页面使用该主机构造网站地址，并采用绑定的协议和端口；真实 Agent 配置为回环地址且 Console 从远程访问时改用其机器名。仅监听回环地址或主机名为 localhost 的真实站点标注“仅服务器本机访问”，仅在 Console 和 Agent 均使用回环地址访问时提供打开链接。应用程序池的关联站点查看包含根应用及子应用，站点筛选按快照中的关联应用匹配。

控制台 POST 请求须携带与其同源的 `Origin`、会话 Cookie 和对应 `X-IISManager-CSRF` 请求头；Cookie 和令牌由 `/api/v1/session` 返回。浏览器页面自动处理这些字段。该会话仅用于防止跨站请求，不是账号登录；脚本客户端也须满足同源/CSRF 校验。

## 站点及应用别名与标签

GET 返回 `{serverId, entries}`；每个 entry 为 `{siteId, siteName, applicationPath, alias, tags, version}`。`applicationPath=""` 表示站点，非空表示站点下的子应用路径（如 `/api`）；旧响应缺少该字段时按站点处理。只返回当前配置服务器与现有 IIS 站点、应用身份匹配的标注；没有记录时显示原名称或路径、空别名与空标签。`version` 是不透明的 64 位十六进制并发指纹。该存储独立于 Agent，配置失败不让已读取的 IIS 快照失效。

POST 必须显式传入全部五个字段：`siteId`（正整数）、`siteName`（原 IIS 名称）、`alias`（字符串）、`tags`（字符串数组）、`expectedVersion`（上次读到的版本，无记录时为 `""`）。不接受缺省字段、null 或额外属性，清空别名或标签也须显式传空值。别名 trim 后最多 100 个 UTF-16 字符；标签最多 20 个、每个最多 40 个字符，trim、去空并忽略大小写去重，拒绝控制字符。别名不修改 IIS 名称，站点启停仍使用原名称。

保存子应用时额外传 `applicationPath`；省略或空字符串仍保存站点。该字段不接受 null、根路径 `/`、相对路径、空路径段、`.` / `..`、反斜杠、查询或片段字符、控制字符，最长 2048 个字符，且须与最新 Agent 拓扑中的原路径精确匹配。站点根应用沿用站点标注。子应用别名和标签使用同样限制，不改变 IIS 路径、应用池或绑定。列表关键词和标签筛选包含子应用标注，命中时自动展开所属站点的应用子表。

服务器编号来自配置，浏览器不能指定平台地址、调用凭据、记录主键或唯一键。站点继续使用服务器编号、站点编号和原名称生成 64 字符 SHA256 唯一键；子应用使用该站点键拼接完整四元组（含原应用路径）的 SHA256，共 128 字符，复用现有六个零代码字段。保存前重新取得 IIS 快照，子应用额外检查最新拓扑，再读取平台记录并核对 `expectedVersion`；单个 Console 内按服务器串行保存。新增或修改只尝试一次，随后独立查询并核对完整目标和值，成功返回最新 entry。首次显式保存空值也创建可回读版本的记录，已有相同内容不重复写入。

`400 SITE_ANNOTATION_INVALID` 表示值不合法；`409 SITE_TARGET_CHANGED` 表示 IIS 身份变化；`409 SITE_ANNOTATION_CONFLICT` 表示版本变化；`503 SITE_ANNOTATIONS_NOT_CONFIGURED` 表示连接未启用；`503 SITE_ANNOTATIONS_UNAVAILABLE` 表示调用或平台数据无法可靠读取；`502 SITE_ANNOTATION_OUTCOME_UNKNOWN` 表示写入后尚未读回确认。页面成功响应后再独立 GET；冲突及未知结果保留草稿并提供只读核对入口，不自动重放 POST。不返回远端异常正文或凭据。

零代码接口字段及空值配置见 [站点标注接入](site-annotations-zero-code.md)。平台更新接口不提供条件写入；单 Console 的串行与版本检查不构成跨 Console、平台直接编辑的原子比较更新。

## 应用与虚拟目录详情

`site-topology` 是独立只读接口，沿用 Agent 的来源、Token 或证书校验。返回 `collectedAt`、`sites` 和 `applicationPools`。每个站点返回 `id`、`name`、`state`、`bindings` 和 `applications`；每个应用返回 `path`、`applicationPoolName`、`physicalPath`、`enabledProtocols` 和 `virtualDirectories`；每个虚拟目录仅返回 `path`、`physicalPath`。不返回目录访问账号、密码或其他原始配置属性。

应用路径相对站点根路径；虚拟目录路径相对所属应用。各应用的根虚拟目录 `/` 包含在数据中，应用 `physicalPath` 来自此根映射。页面的应用数包含根应用，附加虚拟目录数排除每个应用的 `/` 映射；虚拟目录使用所属应用的池，不是独立应用。地址预览显示所属应用，并将应用路径、虚拟目录路径按段编码后接到站点绑定地址。IIS 应用启用协议中的 `http` 同时允许 HTTP 与 HTTPS，未启用时不生成这两类地址。

当前站点页通过站点行的展开按钮显示两张行内子表：应用程序表列出应用路径、应用池、物理目录和访问地址；附加虚拟目录表列出虚拟路径、所属应用、继承应用池、物理目录和完整地址。同名虚拟路径按所属应用分别展示，不合并为站点根路径。应用池筛选同时约束两张子表，站点摘要和子表标题显示当前筛选范围内的子项数量。刷新保留当前服务器的展开状态，切换服务器清空展开记录，旧服务器的行回调不能影响新服务器。加载、升级提示、读取失败和最新拓扑缺少站点均显示实际状态与只读刷新入口，不当作空目录。展开和收起不提交操作；应用池详情入口保留原有跨站点关联查看。

站点页关键词忽略大小写及首尾空白，同时匹配应用路径、应用别名/标签、应用池、应用物理目录、虚拟目录路径及物理目录，也匹配所属应用与虚拟目录拼接的完整虚拟路径（如 `/api/assets`）。子项命中后自动展开所属站点；仅命中子项时，子表只保留匹配的应用/虚拟目录，应用命中时保留其所属目录作为上下文。站点自身信息命中时仍可展开查看全部子项。关键词与应用池条件约束同一批子项，避免在其他池的应用中命中却展示无关结果；标题及站点摘要显示当前子表数量。搜索展开后可手动收起，新关键词重新展开对应结果；清空关键词恢复完整子项。该行为使用已取得的拓扑，不提交管理操作或标注写入。

绑定配置可折叠查看 IP、端口、主机名、证书指纹和 SNI 等现有信息。有效地址通过完整 URL 表示协议，非网页绑定保留协议标签；不修改绑定数据或接口字段。

此接口不扩展原 `snapshot`、`info` 或操作结果模型，也不改变已有预检指纹输入。拓扑中的池指纹为空，仅用于展示，不能用于提交操作。Windows 读取同一个 `ServerManager` 下的站点、应用、目录和池；Simulation 只展示实际存在的模拟站点根应用，不伪造子应用或目录。

旧 Agent 对新路由返回 404 时，Console 返回 501 和 `SITE_TOPOLOGY_UNSUPPORTED`，页面提示升级 Agent。鉴权拒绝、网络故障或无效响应仍报告相应错误，不转换为空集合；未知服务器仍为 `SERVER_NOT_FOUND`。应用与目录查询单独加载，读取失败不使已成功取得的原快照失效。完整层级展示需要新版 Agent 和 Console，更新 Agent 时旧 Console 仍可使用原接口。

## Windows 服务查询与兼容

服务列表是独立只读接口，不扩展原 `info`、`snapshot` 响应。返回 `collectedAt`、`services`、`canCreate`、`creationUnavailableReason`、`allowedAccounts`。服务对象包含 `name`、`displayName`、`description`、`state`、`startType`、`accountName`、`executablePath`、`processId`、`dependencies`、`dependentServices`、`managed`、`canStart`、`canStop`、`canRestart`、`canUpdateStartType`、`unavailableReason`。可执行路径只返回 EXE 部分，不返回已有完整命令行、密码或其他秘密。操作能力是当前查询结果，提交时仍需重新预检查。

状态包括 `Stopped`、`Running`、`Paused`、`StartPending`、`StopPending`、`ContinuePending`、`PausePending`、`Unknown`；过渡或无法核实的状态不允许新写入。名称是 SCM 服务名，不是可重复的显示名称。服务详情路径须 URL 编码。

Console 页面列表、计数及搜索只展示 `managed=true` 的服务；API 保留完整服务快照供依赖选择与核验。展示范围不依赖某个启停按钮是否可用，因此已禁用或过渡中的可管理服务仍显示。没有可管理项时页面提示检查服务范围配置及保护规则。

旧 Agent 不提供服务列表路由时，Console 返回 501 `WINDOWS_SERVICES_UNSUPPORTED` 并提示升级；网络、认证和反序列化错误仍保留真实错误。页面确认服务能力后才能发送新增动作，服务列表失败不影响已有 IIS 查询。原 IIS 请求中的 `serviceName` 和 IIS 预检查中的 `affectedServices` 为 null 时省略，保持旧 JSON 及请求哈希。服务请求使用独立 `serviceName`，影响范围使用独立 `affectedServices`，不混入 `affectedApplications`。

需要同时升级 Agent 与 Console 才能使用服务功能。新服务请求进入任一端账本后，旧版程序可能因未知字段或动作拒绝读取账本，不能无条件降级；不可删除记录规避这个限制或清除去重历史。

## 连接认证

新安装默认 `TransportMode=HttpToken`、`SourceAccessMode=PrivateNetwork`：浏览器访问 Console 的 HTTP 地址，Console 后端连接每台 Agent 的 HTTP 根地址，并在请求头 `X-IISManager-Token` 中附加该 Agent 的 64 位十六进制随机访问令牌。每台 Agent 使用不同令牌；Console 和 Agent 根据实际 TCP 来源自动允许内网地址，无需设置管理来源 IP/CIDR。来源检查不信任 `X-Forwarded-For` 等代理头，公网来源地址不会自动允许。缺少或错误令牌、未获准的来源均不能调用 Agent；不能通过来源 IP 或浏览器 CSRF 令牌替代 Agent 访问令牌。

Agent 令牌只保存在受限服务配置中，由 Console 后端读取；服务器清单和浏览器响应不会返回它。访问令牌不写入 URL、查询参数、NLog、安装摘要或回执。HTTP Agent 模式仅用于受信任内网。当前 Console 的页面及管理 API 另要求应用用户登录，登录用户沿用既有管理能力；浏览器只持有不透明 HttpOnly Cookie，上游应用用户 Token 由后端携带。认证接口、HTTPS 配置和有效期规则见[应用用户认证说明](app-user-authentication.md)。

自动内网范围为 `10.0.0.0/8`、`172.16.0.0/12`、`192.168.0.0/16`、`127.0.0.0/8`、`169.254.0.0/16`、`100.64.0.0/10`、`::1/128`、`fc00::/7`、`fe80::/10`。旧配置缺少 `SourceAccessMode` 时仍为 `Allowlist`，读取原来源白名单；安装器更新保留原来源模式，不自动放宽。

HTTPS 双向证书模式仍可选，配置值为 `Https`：Console 附加配置的客户端证书，并验证 Agent 的证书名称、信任链、撤销状态和固定指纹；Agent 验证并限定 Console 客户端证书指纹。两种模式均保留 Console Host、来源 IP、同源/CSRF 校验及原有操作语义。HTTPS 没有失效后自动转为 HTTP 的回退。

## 操作请求

预检查和提交使用同一结构。预检查时省略 `expectedFingerprint`；提交时填入本次预检查的 `fingerprint`。操作有效期不能超过 Agent 当前时间后 15 分钟，执行开始时也必须未过期。

| 字段 | 类型 | 约定 |
|---|---|---|
| `operationId` | GUID | 提交前生成；全链路及超时查询保持相同编号 |
| `type` | string | 下表已定义动作之一 |
| `target` | object | 对应动作的唯一目标名称 |
| `parameters` | object | 动作参数；启停、回收必须为 `{}` |
| `expectedFingerprint` | string，预检查可省略 | 预检查返回的 64 位十六进制指纹 |
| `expiresAt` | datetime | 允许开始执行的截止时间 |

| 动作 | 目标字段 | 参数 |
|---|---|---|
| `applicationPool.start` | `applicationPoolName` | `{}` |
| `applicationPool.stop` | `applicationPoolName` | `{}` |
| `applicationPool.recycle` | `applicationPoolName` | `{}`，池须已启动 |
| `applicationPool.create` | `applicationPoolName` | 应用池字段，可省略采用默认值 |
| `applicationPool.update` | `applicationPoolName` | 至少一个可编辑应用池字段 |
| `site.create` | `siteName` | 物理目录、现有池及绑定 |
| `site.start` | `siteName` | `{}`，站点须为稳定的 Started 或 Stopped |
| `site.stop` | `siteName` | `{}`，站点须为稳定的 Started 或 Stopped |
| `site.restart` | `siteName` | `{}`，站点须为 Started，先停止再启动 |
| `windowsService.start` | `serviceName` | `{}`；不修改已禁用服务的启动类型 |
| `windowsService.stop` | `serviceName` | `{}`；不级联停止依赖服务 |
| `windowsService.restart` | `serviceName` | `{}`；服务须稳定运行，先停止再启动 |
| `windowsService.create` | `serviceName` | 下表服务创建字段 |
| `windowsService.updateStartType` | `serviceName` | 仅 `startType`；不立即启停服务 |

IIS 名称长度为 1–64 个字符，Windows 服务名为 1–128 个字符；不允许首尾空白、控制字符、路径分隔符、结尾句点或 `:*?"<>|`。首版不支持改名、删除、自定义应用池账号或任意 IIS 配置写入。

### 应用池参数

| 字段 | 取值 | 创建默认值 |
|---|---|---|
| `managedRuntimeVersion` | `""`（无托管代码）、`v2.0`、`v4.0` | `""` |
| `managedPipelineMode` | `Integrated`、`Classic` | `Integrated` |
| `enable32BitAppOnWin64` | boolean | `false` |
| `autoStart` | boolean | `false` |
| `startMode` | `OnDemand`、`AlwaysRunning` | `OnDemand` |
| `idleTimeoutMinutes` | integer，0–43200；0 禁用闲置超时 | `20` |
| `idleTimeoutAction` | `Terminate`、`Suspend` | `Terminate` |

创建池时固定 `ApplicationPoolIdentity`，按 `autoStart` 选项处理：`true` 由 IIS 自动启动并须读回 `Started`，`false`（包括省略时的默认值）须读回 `Stopped`；创建后不再强制停止，也不额外调用启动动作。预检查返回对应的目标状态与提示，运行状态尚未就绪时只做限时只读等待。修改已有池只覆盖明确提交的字段，保留未提交的配置、关联应用和当前运行状态。修改 CLR、管道、32 位支持、启动模式、闲置超时或闲置动作前必须单独停止应用池；编辑已有池的 `autoStart` 配置不等同于立即启动或停止。闲置超时为 0 时仍保存动作配置。

下面的预检查将修改闲置动作为挂起，且保留其余字段。执行前需要池已停止；时间占位值须换成真实截止时间。

```json
{
  "operationId": "46dba725-9b15-4a98-a485-c188b7470968",
  "type": "applicationPool.update",
  "target": { "applicationPoolName": "DemoPool" },
  "parameters": { "idleTimeoutAction": "Suspend" },
  "expiresAt": "<当前 UTC 时间后不超过 15 分钟>"
}
```

### 站点运行操作

`site.start`、`site.stop`、`site.restart` 只提供 `target.siteName`，不能混入应用池或服务目标，`parameters` 必须是空对象。启停不修改 `serverAutoStart`、绑定、应用池关联、应用和虚拟目录配置，不显式启停或回收应用池。启动已启动的站点、停止已停止的站点在接口上为幂等操作；页面按快照禁用这些重复动作。Starting、Stopping、Unknown 等不稳定状态须先只读核对；已停止站点不能重新启动，应使用单独启动动作。

预检查列出所选站点的全部应用（含根应用和使用其他池的子应用），停止或重启的影响包括这些应用下的虚拟目录。启动/停止使用 IIS 的 [Site.Start](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.administration.site.start?view=iis-dotnet) / [Site.Stop](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.administration.site.stop?view=iis-dotnet)。重新启动先停止，使用新 `ServerManager` 读回确认同一站点 ID 已停止且包含应用、虚拟目录和绑定在内的配置未漂移，再用新的管理句柄复核并启动。停止等待上限为 10 秒；未确认停止、目标被替换或配置变化时不继续启动。

IIS RSCA 将动态 `state` 暴露在站点和应用池的 `Attributes` 中，配置属性指纹只对这两类对象排除此运行状态；状态仍参与执行前预检查，并在动作后单独核对。证书库、DS Mapper 等 SSL 绑定动态配置仍保留在证据中。独立读回每 200ms 等待本次目标状态，首次仍为旧稳定态时继续只读等待，最多 10 秒，不重复执行动作。[IIS RSCA 说明](https://blogs.iis.net/ksingla/working-with-rsca-using-configuration-apis/)

变更后的最终状态和完整站点配置须独立读回；停止后启动失败可能返回 `partial` 或 `unknown` 并保持站点停止，必须保留原操作编号、只读核对，禁止自动重试或回滚。Started 仅表示 IIS 站点运行状态，不保证应用池或业务健康。所有操作沿用指纹、有效期、共享串行队列、持久化和去重规则，操作记录支持三个新动作的筛选。

站点启停后的配置证据指纹变化返回 `site.configurationChanged`；目标状态或公开配置字段未匹配预检查方案返回 `iis.readbackMismatch`，两者均保留 `partial`。因此 `after.state=Stopped` 不能单独证明全部配置检查通过，错误不会自动触发回滚或重复停止。

此功能需要更新 Agent 与 Console。旧 Agent 的预检查会拒绝不支持的新动作；原查询 JSON 形状保持不变。含新 `site.*` 动作的账本会被旧版程序视为无效，不可无条件降级，不能通过删除账本规避验证或去重。Windows/IIS 实机验收要求见 [站点启停验证](site-lifecycle-validation.md)。

### 站点创建参数

| 字段 | 类型 | 约定 |
|---|---|---|
| `physicalPath` | string | 已存在的本机绝对目录，须处于 `Agent:AllowedSiteRoots` 白名单内 |
| `applicationPoolName` | string | 已存在应用池 |
| `bindings` | array | 1–16 个绑定，不得重叠或占用管理保留端口 |

| 绑定字段 | 约定 |
|---|---|
| `protocol` | `http` 或 `https` |
| `ipAddress` | `*` 或有效 IP，默认 `*` |
| `port` | 1–65535，默认 80 |
| `hostName` | ASCII DNS 主机名；HTTPS 必填 |
| `certificateThumbprint` | HTTPS 使用 LocalMachine/My 内现有证书的 SHA1 指纹 |
| `useSni` | boolean；此版 SNI 只接受通配 IP |

HTTP 绑定不接受证书或 SNI。HTTPS 会检查证书存在、有效期、私钥元数据及名称匹配；存在 EKU 扩展时须允许服务器认证或 Any EKU。该预检查不证明私钥实际可用，也不检查站点证书完整信任链和在线撤销状态。共享 IP/端口的证书与 SNI 模式须兼容。Windows 适配器还会检查实际目录、应用池身份读取能力、HTTP.sys TLS 记录、现有 IIS 绑定及 IPv4/IPv6 TCP 监听占用；这些原生检查和实际站点启动仍须 Windows 现场验收。站点初始停止且 `serverAutoStart=false`。本动作不创建目录、不修改目录 ACL、不导入证书。

### Windows 服务参数

| 创建字段 | 约定及默认值 |
|---|---|
| `displayName` | 展示名称，空值默认服务名，最长 256 个字符 |
| `description` | 服务说明，不包含秘密 |
| `executablePath` | 服务器本机已存在的 Windows 服务 EXE 绝对路径，须在 Agent 允许目录内 |
| `arguments` | 非秘密启动参数数组，默认 `[]`；拒绝识别到的密码、令牌、连接字符串等秘密参数 |
| `startType` | `Automatic`、`AutomaticDelayed`、`Manual`、`Disabled`，默认 `Manual` |
| `accountName` | Agent 允许的无需密码身份，默认 `VirtualAccount`，对应 `NT SERVICE\<服务名>`；默认还允许 `LocalService` |
| `dependencies` | 已存在的服务名数组，默认 `[]`；不创建所依赖服务 |

服务目标一次只能设置 `serviceName`，不能混入站点或应用池目标。创建必须满足 Agent 的服务名范围、`AllowCreate`、可执行目录及身份策略。仅注册独立进程 Windows 服务，不把普通 EXE、脚本或驱动转换成服务。不复制程序、不创建目录/账号、不更改 ACL、不授予服务登录特权，不接收登录密码。启动参数会进入受限操作账本，不能包含任何秘密；已有服务的完整命令行不会作为快照、差异或日志返回。

创建后保持停止，即使选择自动或延迟自动启动也不会立即启动。`updateStartType` 只接受 `{ "startType": "Manual" }` 等一个字段，保留服务身份、程序、参数、说明及依赖，也保留当时运行/停止状态。禁用服务先明确调整启动类型，再单独预检并启动；不能用 `start` 隐式启用。

Agent 与 Console 固定受保护；额外 `ProtectedNames` 及受保护服务依赖链也拒绝管理动作。停止/重启发现运行中的依赖者时拒绝，不级联停止；启动预检展示可能影响的依赖服务。过渡状态、新指纹冲突或权限无法核实时拒绝写入。重启已停止而后启动失败时保留 `partial`；动作可能发生却无法独立读回时保留 `unknown`，不自动回滚、重放或换编号重试。服务状态读回成功不证明业务健康。

## 预检查结果

`ValidationResult` 包含 `valid`、`fingerprint`、`affectedApplications`、`changes`、`warnings`、`errors`，以及可选的 `before`、`proposed`。服务操作另外返回 `affectedServices`，IIS 操作省略该字段。差异项包含 `field`、`before`、`after`；错误项包含 `code`、`message`。

预检查不会修改 IIS 或 Windows 服务，也不会锁定配置。提交与实际执行均重新校验；指纹覆盖目标及关联配置、适配器检查证据。不能把单池查询返回的指纹或较早预检查指纹用于其他动作。

## 受理、状态与恢复

Agent 完成校验并保存接单记录后返回 `202 Accepted`；返回任务记录与查询地址。Agent 返回 `OperationRecord`；控制台返回 `ConsoleTaskRecord`，包括 `serverId`、原请求、请求哈希、来源 IP、受理/更新时间、`state`、`error` 和可选的 `agentRecord`。控制台的外层 `state` 用于展示，`agentRecord` 保存核实到的 Agent 原记录。HTTP 200 或 202 不表示 IIS 或服务动作成功。

| 状态 | 含义 |
|---|---|
| `queued` | 已保存，等待串行执行 |
| `running` | 已保存开始执行状态 |
| `succeeded` | 按动作对应读回条件确认成功 |
| `failed` | 校验拒绝或已确认失败 |
| `partial` | 配置/动作部分完成，需核实 |
| `unknown` | 结果无法确认或 Agent 重启发现未完成记录 |
| `expired` | 开始执行前超时，未调用适配器执行 |

任务记录包含原请求、请求哈希、状态、受理/开始/结束时间、执行前后快照、脱敏错误与来源 IP。两端使用同一操作编号记录 NLog，来源 IP 由控制台取得，通过认证后的 Agent 请求传递。

同编号、同参数（包括 JSON 属性重排后的等价对象）返回已有记录；同编号不同参数返回 409。截止时间也是请求内容，重试时不要更新它。每台 Agent 在同一队列中统一串行执行 IIS 与 Windows 服务写入，排队容量为 256；队列满时任务会成为 `failed`，错误为 `QUEUE_FULL`。

提交断线或超时后查询原编号，不自动提交新编号。控制台先保存发送意图，无法确认 Agent 受理时保留 `unknown`；只读查询可以更新记录，不能自动重发。Agent 重启把原 `queued`、`running` 记录标为 `unknown`。核实 IIS 或 Windows 服务实际状态、配置和日志后，由操作员决定后续操作。检测到存储不可可靠读写或记录损坏时阻止新的写操作；首次启动会创建状态目录，不能自动识别重启前整份账本已丢失，查询不到原记录仍须人工核实。不得删除记录绕过去重或恢复检查。

页面允许操作员在 `unknown`/`partial` 结果已完成独立只读核实后继续管理。该操作只解除当前浏览器的操作锁，不更改原结果，也不自动重发；`queued`/`running` 仍须等待或查询原任务。

工作台顶部及操作结果中均提供“已只读核实，保留记录并继续管理”。确认后保留原编号、重新读取快照并清除旧预检查；快照失败时 IIS 操作继续禁用。锁定原因在站点、应用池及服务页面均可见。现场恢复与验证范围见 [站点停止后的管理锁定恢复](site-stop-lock-recovery.md)。

站点启动、停止、重启的 `succeeded`、`partial`、`unknown` 结果都会触发当前服务器快照的独立读取，避免列表停留在提交前状态。刷新不改写原操作状态，partial/unknown 的管理锁仍需操作员核实后解除。

回收成功表示 IIS 接受动作且池仍为 Started，业务健康仍须单独检查。正在执行的动作不会仅因浏览器断线而中途取消。

## 操作记录条件查询

`GET /api/v1/operations` 读取控制台操作账本中的最近持久化状态，不联系 Agent、不更新账本、不提交或重放操作。结果可能早于 Agent 当前状态；核实未完成操作时使用上表的“查询原操作”接口，该接口可向 Agent 查询并更新控制台记录。

| 查询参数 | 约定 |
|---|---|
| `serverId` | 可省略；1–64 位英文字母、数字、下划线或连字符，精确匹配。允许格式合法的未知或已从配置移除的服务器编号，已有历史仍可查询 |
| `type` | 可省略；“操作请求”中的已定义动作之一，精确匹配 |
| `state` | 可省略；`queued`、`running`、`succeeded`、`failed`、`partial`、`unknown` 或 `expired` |
| `search` | 可省略；最长 128 个字符，禁止控制字符，去除首尾空白后对目标名称或操作编号进行不区分大小写的包含匹配 |
| `from` | 可省略；受理时间 `acceptedAt` 的下界，包含该时刻 |
| `to` | 可省略；受理时间 `acceptedAt` 的上界，包含该时刻，须不早于 `from` |
| `page` | 正整数，不超过 2147483647，默认 `1` |
| `pageSize` | 仅允许 `10`、`20` 或 `50`，默认 `20` |

筛选参数省略或为空表示不限；分页参数显式为空属于非法请求。`from`、`to` 使用包含时间及 `Z` 或时区偏移的 ISO 8601 值，按实际时刻比较，不能仅传日期或没有时区的本地时间。查询上海某日可使用该日 `00:00:00+08:00` 至 `23:59:59.9999999+08:00`，URL 中的 `+` 须编码为 `%2B`；也可使用等价的 UTC 时间。各筛选条件同时生效，时间范围筛选受理时间而非更新时间。

返回 `{items,total,page,pageSize}`，`total` 为筛选后、分页前的记录数。按 `acceptedAt` 降序排列，同一受理时间按 `operationId` 升序排列。超出最后一页或没有匹配记录时 `items` 为空，仍返回对应 `total` 和请求的分页值。

`items` 严格使用摘要字段：`operationId`、`serverId`、`type`、`target`、`state`、`acceptedAt`、`updatedAt`、`errorCode`。`target` 为站点名、应用池名或服务名字符串，`errorCode` 可为 `null`。摘要不包含请求参数、请求哈希、来源 IP、错误正文、Agent 原始结果、物理路径或认证信息。

示例（合成数据）：

```http
GET /api/v1/operations?serverId=simulation&state=succeeded&from=2026-10-06T00%3A00%3A00%2B08%3A00&page=1&pageSize=20
```

```json
{
  "items": [
    {
      "operationId": "20261006-0000-0000-0000-000000000001",
      "serverId": "simulation",
      "type": "applicationPool.start",
      "target": "DemoPool",
      "state": "succeeded",
      "acceptedAt": "2026-10-06T08:05:00+08:00",
      "updatedAt": "2026-10-06T08:05:12+08:00",
      "errorCode": null
    }
  ],
  "total": 1,
  "page": 1,
  "pageSize": 20
}
```

未知参数、重复参数、非法编号/类型/状态、过长搜索、无效时间、起止反转或非法分页返回 `400` 和 `OPERATION_QUERY_INVALID`。账本损坏、内容不合法或读取失败时返回 `503` 和 `CONSOLE_LEDGER_UNHEALTHY`，不会跳过坏记录返回部分结果，也不输出异常正文。服务器编号格式合法但无历史时返回正常空结果。

## 服务器总览与健康检查

使用方式及文件恢复边界见 [监控使用说明](monitoring.md)。这些新增路由使用现有 Console 用户会话；POST 使用已有同源、Cookie 与 `X-IISManager-CSRF` 校验。Agent 路由继续使用现有独立通信认证，不接收浏览器指定的 Agent 地址。

### 总览响应

`GET /api/v1/overview` 返回 `{collectedAt,servers,healthChecks,healthError}`。它只聚合 Agent 管理读接口和 Console 已保存的检查结果，不访问业务检查 URL。资源默认缓存 15 秒，可由 `Console:Monitoring:OverviewCacheSeconds` 配置为 5–120 秒。

`servers` 各项字段为：

| 字段 | 约定 |
|---|---|
| `id,name,hostName,mode` | 配置服务器标识、名称、Agent 地址主机部分及 `Windows`/`Simulation` 模式；不含连接秘密 |
| `group,environment` | 可选项目与环境配置，缺省空字符串 |
| `connectionState` | `online` 全部读取成功；`partial` 部分读取失败；`offline` 全部模块均未取得数据 |
| `collectedAt` | 此服务器的聚合时间 |
| `info,snapshot,topology,services` | 原有对应契约或 `null`；`services.services` 仅保留 `managed=true` 项 |
| `certificates` | 证书聚合与有效期预警数组 |
| `errors` | `{module,code}` 数组；模块为 `info`、`snapshot`、`site-topology`、`certificates`、`windows-services`，错误仅使用安全码 |

模块独立读取，一项失败不清空其它成功结果。协议不兼容时记录 `AGENT_PROTOCOL_UNSUPPORTED` 并停止使用该服务器其它数据。证书项为 `{thumbprint,subject,friendlyName,notBefore,notAfter,hasPrivateKey,daysRemaining,severity,bindings}`；绑定项为 `{siteName,hostName,port,protocol}`。绑定引用证书不存在时 `severity=missing`，有效期与剩余天数为 `null`。证书读取失败属于模块错误，不推断为缺失。

`severity` 为 `missing`、`notYetValid`、`expired`、`critical`、`warning`、`notice` 或 `normal`。按实际剩余时间分级：已到期为 `expired`；有效且不超过 7 天为 `critical`；大于 7 天且不超过 15 天为 `warning`；大于 15 天且不超过 30 天为 `notice`；大于 30 天为 `normal`；开始时间在未来优先为 `notYetValid`。7／15／30 天边界均包含，`daysRemaining` 为剩余天数向上取整，缺私钥单独通过 `hasPrivateKey` 表达。

健康存储无法读取时，总览仍返回 `200`，`healthError` 为 `{code,message}`、`healthChecks=[]`，不能将此空数组解释为没有已配置检查。正常时 `healthError=null`。

### 健康检查配置与最近结果

`GET /api/v1/health-checks` 直接返回 `HealthCheckView[]`。各 view 字段为 `{id,serverId,siteName,applicationPath,url,expectedStatusCode,timeoutSeconds,intervalSeconds,enabled,revision,result,lastSuccessAt,nextCheckAt}`。结果为空表示尚无本配置的检查结果；时间字段可为 `null`。`result` 契约见下节。

`POST /api/v1/health-checks/save` 使用完整配置，不是局部 PATCH。新增省略 `id`、`revision`；编辑使用最近返回的两个值。合成示例：

```json
{
  "serverId": "server-demo",
  "siteName": "DemoSite",
  "applicationPath": "/api",
  "url": "https://example.invalid/api/health",
  "expectedStatusCode": 200,
  "timeoutSeconds": 5,
  "intervalSeconds": 60,
  "enabled": false
}
```

`serverId` 来自现有服务器配置；站点名称最长 64 字符，应用路径最长 512 字符且以 `/` 开头，根应用使用 `/`；URL 最长 2048 字符。`expectedStatusCode` 为 200–299 的具体期望值，默认 200；`timeoutSeconds` 为 1–15，默认 5；`intervalSeconds` 为 30–3600，默认 60；`enabled` 默认 `false`。最多 200 项，同一服务器/站点/应用已有配置时返回冲突。

保存请求调用 Agent 的 `/health-check/validate`，不访问业务 URL；已有配置仅停用且目标未变时可不联系 Agent。成功返回最新 `HealthCheckView`，含新的 `revision`。该版本为不透明 32 位十六进制字符串，检查 `id` 为 GUID 的 `D` 格式。启用后由后台调度，无需浏览器在线；后台每 5 秒检查到期项，完成后按配置间隔设置下次时间。

`POST /api/v1/health-checks/{id}/delete` 请求体为 `{ "revision": "<最近返回的版本>" }`，成功返回 `{deleted:true}`。正在运行的检查不能删除。删除只作用于检查配置。

`POST /api/v1/health-checks/{id}/run` 无业务参数，浏览器可发送 `{}`；立即运行最新已启用配置，返回含本次结果的 `HealthCheckView`。停用检查拒绝运行。同一检查不能并发运行；进行中的旧配置结果不会覆盖后续编辑或停用。

| HTTP 状态 | Console 配置/运行错误码 |
|---|---|
| 400 | `HEALTH_CONFIG_INVALID`、`HEALTH_LIMIT_REACHED`、`HEALTH_TARGET_REJECTED` |
| 404 | `HEALTH_CHECK_NOT_FOUND` |
| 409 | `HEALTH_CONFIG_CONFLICT`、`HEALTH_TARGET_DUPLICATED`、`HEALTH_CHECK_BUSY`、`HEALTH_CHECK_DISABLED` |
| 501 | `HEALTH_AGENT_UPGRADE_REQUIRED`，保存时 Agent 不支持新能力 |
| 503 | `HEALTH_AGENT_UNAVAILABLE`、`HEALTH_STORE_CHANGED`、`HEALTH_STORE_UNVERIFIED`、`HEALTH_STORE_UNAVAILABLE` |

文件默认保存在 Console 内容根目录的 `data/monitoring/health-checks.json`，可通过 `Console:Monitoring:DataDirectory` 指定目录；不改变原操作账本。配置版本冲突应刷新核对；外部文件漂移或存储未知/损坏时先只读核对文件和权限后重启，不自动重放保存、删除或恢复请求。

### Agent 只读业务探测

`POST /agent/v1/health-check/validate` 与 `/agent/v1/health-check` 使用相同 `HealthProbeRequest`：`{siteName,applicationPath,url,expectedStatusCode,timeoutSeconds}`，不含 Console 的检查编号、周期和版本。验证接口成功返回 `{valid:true}`，不发送业务 HTTP 请求，允许目标站点处于停止状态。

实际探测每次重新读取本机 IIS 拓扑，URL 须匹配当前站点的协议、端口、主机及最具体应用路径，并限定本机绑定地址、IP 地址族和管理保留端口。站点非 `Started` 时不发送 HTTP。仅发送 GET，禁用代理、Cookie 和跳转，不附加用户或 Agent 凭据，不读取或返回响应正文；HTTPS 维持正常证书验证。拒绝 URL 凭据、查询参数、片段以及不明确的路径/编码。检查 URL 必须是业务已提供的专用只读端点。

探测结果为 `{state,checkedAt,statusCode,elapsedMilliseconds,errorCode}`。`statusCode`、`elapsedMilliseconds` 和 `errorCode` 可为 `null`。`healthy` 表示实际 HTTP 状态与期望值相等，`unhealthy` 表示已响应但不符，`unavailable` 表示无法检查，`simulation` 表示没有真实业务请求的模拟结果。Console 对旧 Agent 的探测增加 `unsupported`，并使用 `HEALTH_AGENT_UPGRADE_REQUIRED`；只影响健康检查能力，不禁用原有管理接口。只有真实 `healthy` 结果更新 `lastSuccessAt`。

非运行站点分别返回 `HEALTH_SITE_STOPPED` 或 `HEALTH_TARGET_NOT_RUNNING`；其它安全结果码包括 `HEALTH_STATUS_MISMATCH`、`HEALTH_TIMEOUT`、`HEALTH_CONNECTION_FAILED`、`HEALTH_TLS_ERROR`、`HEALTH_BUSY`、`HEALTH_RESPONSE_INVALID`、`HEALTH_SIMULATION`，以及目标重新验证的 `HEALTH_SITE_NOT_FOUND`、`HEALTH_APPLICATION_NOT_FOUND`、`HEALTH_APPLICATION_PATH_MISMATCH`、`HEALTH_BINDING_MISMATCH`、`HEALTH_BINDING_AMBIGUOUS`、`HEALTH_MANAGEMENT_PORT_DENIED`、`HEALTH_TOPOLOGY_UNAVAILABLE`、`HEALTH_LOCAL_ADDRESS_UNAVAILABLE`。此类已形成探测结果的失败使用 `200` 和结果字段表达，不代表业务成功。

直接 Agent 请求的格式、URL、路径参数错误为 `400`（`HEALTH_REQUEST_INVALID`、`HEALTH_URL_INVALID`、`HEALTH_PATH_INVALID`）；仅验证接口另可能返回 `429 HEALTH_BUSY`、`504 HEALTH_TIMEOUT`、`503` 目标读取错误及 `400` 目标校验错误。Console 保存时会将 Agent 校验拒绝转换为上述 Console 安全错误，不原样透传异常正文。

## HTTP 错误

Agent 参数/协议错误返回 400；不存在的查询对象返回 404；同编号不同参数或提交时指纹变化返回 409；过期请求返回 410；提交时业务校验拒绝返回 422；任务存储未就绪或失效返回 503。预检查的业务校验结果返回 `200`，通过 `valid=false` 和 `errors` 表达拒绝。错误体使用 `ApiError`，不返回未经审查的异常正文。

HTTP 内网模式的 Agent 来源或访问令牌校验失败返回 403，不返回配置内容。HTTPS 在握手或证书认证阶段拒绝未获准连接。

控制台对 Agent 网络、超时与异常响应使用脱敏状态码，例如 `AGENT_UNREACHABLE`、`AGENT_TIMEOUT`、`AGENT_CONFLICT`。Agent 的错误正文不会原样转发，因此浏览器应通过预检查 `errors` 和任务 `error` 查看可操作原因。


## 集中优化新增接口（2026-10-25）

所有 Console API 沿用会话、来源限制、POST CSRF；新增按服务器查看/read、启停/operate、配置/configure 权限，管理路由还要求 administrator。全局汇总与历史在分页/计数前过滤当前用户范围。

- `GET /api/v1/overview/progress`：按台返回 pending/refreshing/isStale 与采集结果，附 isRefreshing/completedServers/totalServers；不提交业务请求。
- `GET /api/v1/servers/{id}/overview/refresh`：共享且去重的单台只读重采集。
- `GET /api/v1/servers/{id}/diagnostics`：转发 Agent `GET /agent/v1/diagnostics`，旧 Agent 返回 501 DIAGNOSTICS_UNSUPPORTED。结构与采样边界见 [诊断说明](diagnostics.md)。
- `GET /api/v1/servers/{id}/configuration-baseline`：返回 baseline、currentFingerprint、currentConfiguration、differences、differenceCount、matches。
- `POST /api/v1/servers/{id}/configuration-baseline`：配置权限，接收 `{label,revision,currentFingerprint}`；首次 revision 为 null；重读当前拓扑并校验版本，保存后返回基线回执。409 BASELINE_REVISION_CONFLICT / BASELINE_CONFIGURATION_CHANGED，400 BASELINE_TOO_LARGE；存储损坏/漂移返回安全 503。只更新本工具基线，不回滚 IIS。
- `GET /api/v1/operations/export`：沿用历史筛选，最多 5,000 条安全摘要 CSV；不导出请求正文或操作参数。
- `GET /api/v1/permissions` 与 `/api/v1/administration/*`：见 [接入与权限契约](administration.md)。

已有操作 validate/submit 路由新增 site.update、application.create/update、virtualDirectory.create/update；target 增加可选 applicationPath/virtualDirectoryPath，null 时不序列化以保留旧请求哈希。停止条件、字段与 TLS 边界见 [维护契约](site-maintenance.md)。

已有健康检查禁止更改 serverId，返回 409 HEALTH_SERVER_IMMUTABLE；需要跨服务器重建时分别取得旧/新服务器权限。


## 操作后业务健康验证与 FREB（2026-10-27）

新提交的应用池 start/stop/recycle、站点 start/stop/restart 意向可带服务端生成的 `healthFollowUp`。仅原任务确认 succeeded 后后台处理已启用且属于影响范围的检查；历史 null 不补跑，状态未知/失败不执行本批检查。原操作提交请求不增加任何客户端健康参数；不能由请求伪造跟进结果。

`GET /api/v1/servers/{id}/operations/{operationId}` 沿用查看权限，返回原记录及可空 `healthFollowUp`；pending/running 可按原编号继续 GET，读取不重放操作或健康批次。后台独立于浏览器运行，结果及重启中断记录详见 [操作后健康契约](operation-health-follow-up.md)。IIS state 不因健康结果异常改变。

`GET /api/v1/servers/{id}/diagnostics/freb` 沿用服务器 read 权限，转发 Agent `GET /agent/v1/diagnostics/freb`。不接受日志路径参数，不写 IIS。旧 Agent 返回 501 FREB_UNSUPPORTED；正常返回 `{collectedAt,mode,cacheSeconds,state,errorCode,sites}`，state 为 available/partial/unavailable/disabled/simulation。站点、trace、事件字段、脱敏和预算见 [FREB 契约](freb-diagnostics.md)。返回不含原始 URL、headers、body、XML 或磁盘路径。
