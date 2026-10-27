# 只读故障诊断与资源监控

“故障诊断”页面为 `/diagnostics.html`，可使用 `?server=<已有服务器编号>` 选中服务器。浏览器读取 `GET /api/v1/servers/{id}/diagnostics`；Console 使用已有认证通道读取对应 Agent 的 `GET /agent/v1/diagnostics`。旧 Agent 不支持时提示统一升级，不影响既有管理能力。

本功能只读，不运行命令、不控制进程或服务、不清理日志、不修改 IIS，也不自动修复故障。请求使用现有登录与访问范围；导出文件是当前已取得的脱敏 JSON，不是完整日志或系统备份。

2026-10-27 新增独立的 [FREB 请求级失败跟踪区](freb-diagnostics.md)，可展开失败模块和管线事件；与下述 W3C 聚合分别读取，不自动开启生产跟踪。

## 页面与采样

- 服务器资源：CPU、物理内存、最多 32 个本机固定磁盘的总量和可用量；磁盘不可读取时显示不可用，不用零代替。
- 应用池与可管理服务进程：名称、状态、PID、CPU、工作集、专用内存、进程启动时间和运行时长，各类最多 100 项，总计最多 200 项，超过时提示截取。应用池读取 IIS 的 w3wp 关联；服务范围沿用现有 Agent 可管理服务规则。服务共享进程时会显示相同 PID。
- Windows 关键事件：最近 24 小时、限定提供程序与级别的事件采样，最多 50 条；显示时间、日志、提供程序、事件 ID、级别、记录编号及固定安全摘要。
- IIS 错误摘要：每站点聚合 4xx/5xx 的状态码、子状态、Win32 状态、次数、平均与最大耗时；没有记录的可选数值显示未知。

首次 CPU 读取没有可比较的增量，显示“待二次采样 / 未知”。服务器 CPU 使用 Windows 系统时间差；进程 CPU 按实际经过时间及逻辑处理器总量归一化，范围 0–100%。进程同时以 PID 和启动时间识别，PID 复用、应用池回收或进程替换后重新采样，不能沿用旧进程的 CPU 差值。

系统 CPU 的差值按 [GetSystemTimes 官方定义](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)扣除空闲时间。多处理器组机器上，该 API 不能直接代表所有组；本期明确返回 `DIAGNOSTICS_CPU_GROUP_UNSUPPORTED`、CPU 未知，仍提供内存和磁盘，不把单组数值当作整机 CPU。

服务端缓存 10 秒，同一时刻只保留一组采集；页面支持人工刷新或每 15 秒刷新，页面隐藏后暂停自动刷新。请求等待最多 10 秒，平台调用长时间无返回时复用该组进行中的采集，重复刷新不会无限启动本机采集任务。本期不存储资源历史曲线，也不发送外部告警。

各模块分别采集。权限不足、进程退出、模块不支持或读取失败时返回安全错误码与不可用状态，已成功取得的其它模块继续显示。刷新失败后旧快照会标注为上次数据，不能导出为当前成功快照。`Simulation` 明确标识模拟，不调用真实资源、事件或日志采集器，也不伪造真实健康结果。

## Windows 事件与隐私

事件提供程序限定为 `Microsoft-Windows-WAS`、`Microsoft-Windows-IIS-W3SVC`、`Microsoft-Windows-IIS-W3SVC-WP`、`Service Control Manager`、`Application Error`、`.NET Runtime`、`Windows Error Reporting`。采集关键、错误、警告级别；另外允许服务控制管理器的 7036 状态事件。

不使用事件的格式化消息、EventData、账号、安全标识或原始 XML 作为响应；摘要为代码内固定文字。详细原因应由操作员在服务器事件查看器按时间和事件编号核对，不能只凭通用摘要认定具体业务根因。

进程不返回命令行、环境变量、可执行参数或内存内容。页面及 JSON 导出再次按明确字段投影，不携带 Token、连接字符串、Cookie、任意原始响应字段或原始事件正文。

## IIS 本地日志边界

只从现有 IIS 站点配置的日志根目录读取对应 `W3SVC<站点编号>` 子目录，不接受浏览器提交日志路径。仅支持站点模式下启用的 W3C 文件日志；集中日志、二进制日志、日志停用或路径不可核实时显示不可用。W3C 字段按空格分隔、时间采用 UTC，依据 [IIS 日志格式定义](https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/sites/sitedefaults/logfile/)。

路径须为本机绝对路径；拒绝 UNC、设备路径、备用数据流、符号链接与重解析点。Windows 读取时持有目录链和文件的只读句柄，拒绝目录/文件在读取期间被替换到边界外。不会跟随链接查找日志，也不会修改目录权限来绕过拒绝。

采样范围有以下上限：

| 范围 | 上限 |
|---|---:|
| 每次站点数 | 32 |
| 每站点文件名枚举 | 129 个，超过时标记截取 |
| 每站点读取文件 | 最近 2 个标准 `u_ex*.log` 文件 |
| 每文件尾部 | 512 KiB，另有最多 32 KiB 字段头 |
| 全部站点总读取预算 | 8 MiB，包含字段头读取 |
| 每站点数据行 | 20,000 |
| 每站点错误码组合 | 100 |

仅统计最近 24 小时时间范围内的尾部样本，超过限制明确标注截取。大文件取头部 W3C 字段定义及尾部行，尾部内遇到新的 `#Fields:` 会切换字段定义；格式不匹配行跳过并提示部分可用。该采样不能作为完整 24 小时错误总量；高流量、字段变更、轮转或多个历史文件可能使覆盖范围不完整。

响应与导出只保留站点名称、采样范围/条数以及数字摘要，不返回请求路径、查询、客户端 IP、用户名、Cookie、日志文件物理路径或原始日志行。样本内没有 4xx/5xx 仅表示该样本未匹配，不能据此宣称业务全部正常。

## 响应契约与状态

`DiagnosticsSnapshot` 为 `{collectedAt,mode,cacheSeconds,resources,processes,events,iisErrors,modules}`，模块不可用时相应数据为 `null`。`modules` 各项为 `{module,state,errorCode,truncated}`，状态为 `available`、`partial`、`unavailable` 或 `simulation`。

资源字段为 `{cpuPercent,totalMemoryBytes,availableMemoryBytes,logicalProcessors,disks}`。磁盘为 `{name,totalBytes,availableBytes,errorCode}`；进程为 `{kind,name,state,processId,cpuPercent,workingSetBytes,privateMemoryBytes,startedAt,uptimeSeconds,errorCode}`，`kind` 为 `applicationPool` 或 `windowsService`。

事件字段为 `{timeCreated,logName,provider,eventId,level,recordId,summary}`。`iisErrors` 每站点字段为 `{siteName,state,windowStart,windowEnd,sampledFiles,sampledLines,truncated,errorCode,errors}`；其中错误组合只含 `{status,subStatus,win32Status,count,averageMilliseconds,maximumMilliseconds}`。

常见安全码包括 `DIAGNOSTICS_ACCESS_DENIED`、`DIAGNOSTICS_TIMEOUT`、`DIAGNOSTICS_NATIVE_UNAVAILABLE`、`DIAGNOSTICS_READ_UNAVAILABLE`、`DIAGNOSTICS_PROCESS_UNAVAILABLE`、`DIAGNOSTICS_PID_CHANGED`、`DIAGNOSTICS_LIMIT_REACHED`、`DIAGNOSTICS_IIS_LOG_PATH_REJECTED`、`DIAGNOSTICS_IIS_LOG_UNAVAILABLE`、`DIAGNOSTICS_IIS_FIELDS_UNAVAILABLE`、`DIAGNOSTICS_IIS_LINES_SKIPPED`。具体模块错误显示于模块状态、进程行或站点日志摘要，不输出异常正文。

## 验证边界

专项后端测试使用注入采集器、合成事件 XML 与临时 W3C 日志，覆盖 CPU 首次/增量、PID 复用、缓存、超时、模块失败、事件白名单、日志聚合、路径/链接边界和预算；前端测试使用模拟 DOM/API，覆盖脱敏投影、未知数值、读取失败、旧 Agent、服务器切换隔离及 JSON 导出。测试不连接真实服务器，不读取真实业务日志。

Windows 原生 API、IIS 管理接口、事件权限、磁盘及日志目录访问仍需在已授权的 Windows/IIS 环境进行只读验收。本地编译或合成测试通过不代表已完成该现场验收；本功能不自动部署或改变服务器权限。
