# FREB 请求失败跟踪只读诊断

本功能读取 IIS 已经生成的 Failed Request Tracing（FREB）记录，显示失败模块、通知阶段、HTTP 状态及有限的流水线事件。它不会启用跟踪、修改站点或跟踪规则、请求业务 URL、清理日志，也不会把原始 XML 下载到浏览器。Simulation 明确返回 `simulation`，不生成虚构的请求结果。

## 使用前提和人工开启

在 Windows Server 的角色服务中安装 IIS「跟踪 / Tracing」，再由管理员在 IIS 管理器选择需要诊断的站点，打开「失败请求跟踪设置」，选择受限的本机固定磁盘日志目录并启用。按照当前故障配置失败请求跟踪规则，例如仅捕获需要调查的 HTTP 状态或耗时阈值。规则和保留数量由管理员根据服务器情况设置；诊断页不会代为修改。开启后需要有真实请求符合规则，才会产生记录；空列表不能证明站点正常。

IIS 的 `traceFailedRequestsLogging.enabled` 默认关闭，默认目录为 `%SystemDrive%\inetpub\logs\FailedReqLogFiles`。Agent 从 IIS 当前有效配置取得每个站点的目录，只读取其 `W3SVC<siteId>` 目录中形如 `fr000001.xml` 的文件。它不接受客户端指定的路径。配置目录必须为固定本机磁盘，不支持 UNC、网络盘、ADS、重解析目录或链接文件。参见 [Microsoft 跟踪日志配置](https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/sites/site/tracefailedrequestslogging) 与 [Microsoft FREB 故障诊断示例](https://learn.microsoft.com/en-us/troubleshoot/developer/webapps/iis/health-diagnostic-performance/troubleshoot-php-with-failed-request-tracing)。

Agent 需要读取当前 IIS 配置和已配置目录的权限；本功能不会写 ACL 或提权。出现 `FREB_ACCESS_DENIED` 时由管理员核实运行身份与现有权限。正在写入的 XML 会被安全拒绝，稍后完成后可再次读取。

## HTTP 与数据契约

Agent：`GET /agent/v1/diagnostics/freb`；Console：`GET /api/v1/servers/{serverId}/diagnostics/freb`。使用现有 Agent 身份校验、Console 登录和对应服务器读取权限。旧 Agent 没有该路由时 Console 返回 501 / `FREB_UNSUPPORTED`，不会改用其他路径或执行写操作。

`FrebDiagnosticsSnapshot` 包含 `collectedAt`、`mode`、`cacheSeconds`、`state`、可空 `errorCode` 和 `sites`。站点对象包含 `siteId`、`siteName`、`state`、可空 `errorCode`、`truncated` 和 `traces`。

每个 trace 包含 opaque `traceId`、`startedAt`、可空 `status` / `subStatus` / `win32Status` / `timeTakenMilliseconds`、固定枚举文本 `method` / `failureModule` / `failureNotification`、`truncated` 和 `events`。每个 event 包含 `sequence`、可空时间及相对毫秒数、固定枚举文本 `eventName` / `module` / `notification`，以及可空数值状态。缺失或非法数值保留为空；不会由成功读取文件推断请求成功。

`traceId` 由每个读取器实例保留的随机 HMAC key、站点与内部文件身份和内容摘要生成。同一进程内相同未改动记录及缓存期间保持稳定，不暴露文件名、Activity ID、URL 或磁盘路径；进程重启后可以变化。

| state | 含义 |
| --- | --- |
| `available` | 读取完成；可能没有近 24 小时记录 |
| `partial` | 有可用站点或记录，同时存在截断、拒绝或不可用项 |
| `unavailable` | 本次无法取得可用诊断记录，详见安全错误码 |
| `disabled` | 已安装跟踪组件，但相关站点未启用 FREB |
| `simulation` | 模拟模式，不读取或伪造真实 FREB |

常见错误码：`FREB_DISABLED`、`FREB_ROLE_MISSING`、`FREB_WINDOWS_REQUIRED`、`FREB_CONFIGURATION_UNAVAILABLE`、`FREB_ACCESS_DENIED`、`FREB_TIMEOUT`、`FREB_PATH_REJECTED`、`FREB_LOG_DIRECTORY_NOT_FOUND`、`FREB_FILE_REJECTED`、`FREB_FILE_CHANGED`、`FREB_FILE_TOO_LARGE`、`FREB_LOG_UNAVAILABLE`、`FREB_XML_INVALID`、`FREB_XML_LIMIT_REACHED`、`FREB_SCOPE_MISMATCH`、`FREB_SITE_LIMIT`、`FREB_SCAN_LIMIT`、`FREB_TRACE_LIMIT`、`FREB_BYTE_BUDGET_REACHED`、`FREB_TRACE_TRUNCATED`。`FREB_NO_RECENT_TRACES` 表示没有可显示的近期记录，与“健康”没有等价关系。异常正文及内部路径不会返回。

## 隐私投影与资源上限

原始 FREB 可含 URL、query、请求/响应头、请求体、用户名、IP、物理路径及其他敏感内容。本功能不返回这些内容，也不提供原文件下载。日志中的字符串只经过逐字段白名单映射，未知模块、事件或通知统一为 `OTHER`；缺失失败模块为 `NONE`。请求头/实体缓冲事件完全不显示；其余未知事件只可能呈现固定 `OTHER` 标记与合法数值。站点名称来自 IIS 配置，不来自日志。

XML 解析禁止 DTD、外部实体和解析器网络访问，不执行日志样式表。只接受 Microsoft FREB `failedRequest` 根以及 `System` / `EventData` / `RenderingInfo` 结构；校验根和事件中的站点编号。XML 内容的请求开始时间限制为采集时刻之前的 24 小时窗口；文件修改时间只用于预筛选。事件时间仅在同一合法窗口内显示。

| 范围 | 上限 |
| --- | --- |
| 聚合缓存 | 10 秒；同一服务最多一个采集任务 |
| 一次采集 | 默认 8 秒、32 个站点、64 个打开文件、8 MiB 读取预算、64 条 trace、1024 个返回事件 |
| 每站点 | 顶层最多检查 128 个文件名、返回 12 条 trace |
| 每文件 | 2 MiB、XML 深度 32、50000 个节点、4096 个扫描事件 |
| 每 trace | 最多 96 个返回事件；保留开始部分、尾部和最近失败事件 |

达到限制会返回截断或明确错误码，不隐式扩大扫描。超时调用保留唯一尚未结束的采集任务，后续刷新不会累积后台任务。客户端取消仅取消等待，不额外启动采集。Windows 对完整目录链和文件使用只读共享句柄，拒绝重解析点、非固定盘、硬链接及并行写入；读完再次核对文件身份、大小与修改时间。Unix 合成测试通过 `openat` / `O_NOFOLLOW` 固定目录句柄读取，并拒绝硬链接。

## 依据与验证边界

字段结构依据 Microsoft 的 [HTTP_TRACE_EVENT 结构与 XML 示例](https://learn.microsoft.com/en-us/previous-versions/iis/smooth-streaming-client/http-trace-event-structure)、[重写规则跟踪事件](https://learn.microsoft.com/en-us/iis/extensions/url-rewrite-module/using-failed-request-tracing-to-trace-rewrite-rules) 和 [IIS 模块说明](https://learn.microsoft.com/en-us/iis/get-started/introduction-to-iis/iis-modules-overview)。解析器兼容官方示例出现的 HTTP/HTTPS Event namespace，以及 `freb:Description Data="Notification"`。

专项测试只使用合成 XML、隔离临时目录和注入配置源，不连接真实 IIS 或业务网络。Windows 原生共享模式、IIS 组件安装状态和真实服务器日志兼容性仍需在获授权 Windows 验证环境确认；macOS 构建和合成用例不能代替这一验证。
