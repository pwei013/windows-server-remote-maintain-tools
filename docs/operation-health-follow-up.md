# IIS 操作后的业务健康检查

新版本 Console 为新提交的 IIS 生命周期操作记录健康检查跟进标记。应用池启动、停止、回收，以及站点启动、停止、重启属于跟进范围；创建、配置变更和 Windows 服务操作不属于该范围。历史任务没有 `healthFollowUp` 字段时保持原样，不补跑检查。

IIS 操作结果和业务健康结果分别保留：IIS 返回 `succeeded` 才能执行本批健康检查。业务返回不健康、不可用或未知，不会将原 IIS 操作改写为失败。停止站点成功后出现 `HEALTH_SITE_STOPPED` 属于检查的实际结果，不能据此认定停止失败。

## 范围与执行边界

- 站点操作仅选择同服务器、同站点名下已启用的检查，包括该站点中各应用自己的检查。
- 应用池操作优先使用原 Agent 操作记录 `before/after.applications` 中的应用身份（站点名加应用路径）；缺少有效应用清单时，使用当前 `site-topology` 中的应用池关联。共享池会覆盖关联的多个站点或应用，不扩展到整个服务器。
- 无法确认影响范围时明确记录跳过或未知；没有匹配的已启用检查时记录跳过。不会探测未启用检查、其他服务器或不相关应用。
- 每批最多检查 20 项，保留实际 `matchedChecks`；匹配超过上限时带 `HEALTH_CHECK_LIMIT_REACHED`，页面应提示结果未覆盖其余匹配项。范围读取和探测共用一批 60 秒截止时间。
- 后台最多同时处理两项跟进任务。已有健康检查服务为手动、定时和跟进探测共用四路限制，且同一个 check 的并发请求仍返回 busy。busy 在本批记录跳过，不排队重试该批。
- 跟进使用检查计划时的 revision，实际开始前再核对启用状态与 revision。探测期间修改检查后，不能将修改后或旧缓存的结果冒用为原检查结果。

原有健康检查定时任务保持独立。跟进批次完成、跳过或未知，不会停用正常定时检查；之后定时产生的结果也不会重写原批次的关联回执。

## 持久化与未知结果

`ConsoleTaskRecord.CreateIntent` 在新任务写入意向时创建 `pending` 元数据；跟进记录与原 Console task 一起存储。派发前的 IIS `unknown`、无 Agent 回执或暂时读不到操作，不代表失败，后台仅按原 operationId 做只读状态查询，有界等待至确认结果或 10 分钟截止。Agent 明确返回 `failed/expired/partial/unknown` 时不执行健康探测。

首次健康探测之前，先将计划和 `running` 状态写入账本并独立读回。每项结果也独立持久化；更新时先持有该 operationId 的 `store.Lock`，读取最新 task，只合并健康字段，不覆盖并发更新的历史持久化回执。

Console 在 `running` 期间停止或异常退出后，重启将该批标记为 `unknown / HEALTH_BATCH_INTERRUPTED`，保留已记录项，其余项标记未知，**不自动重试探测**。`completed/skipped/unknown` 都是批次终态，刷新、原编号查询、重复 Observe 或重启均不会再次触发该批，也不会重派发 IIS 写请求。存储不可可靠读写时停止相关更新；先只读核对账本，不能通过重提 IIS 操作来补健康结果。

跟进工作独立于浏览器与当前登录会话，由后台读取已持久化的待处理任务索引。启动已有账本校验时同时建立索引，定时处理不反复扫描全部历史文件。浏览器 GET 原编号只读取当前结果并观察已有标记，不发送健康检查 POST 或业务写请求。

## 返回字段

原任务新增可空的 `healthFollowUp`：

| 字段 | 含义 |
| --- | --- |
| `version` | 当前为 1 |
| `state` | `pending/running/completed/skipped/unknown`；前两者可以继续 GET 轮询 |
| `requestedAt/startedAt/completedAt` | 创建、实际开始和终态时间；未到阶段时可空 |
| `scopeSource` | `site/agentRecord/topology` 或空 |
| `errorCode` | 批次安全状态码，可空；不含异常正文 |
| `matchedChecks` | 实际匹配数量，可能大于 results 数量 |
| `results` | 最多 20 条检查回执 |

每条结果只有 `checkId/checkRevision/siteName/applicationPath/state/statusCode/elapsedMilliseconds/checkedAt/errorCode`。其中 state 为 `pending/healthy/unhealthy/unavailable/simulation/unsupported/skipped/unknown`，状态码、耗时、时间和错误码可空。不会保存业务响应正文、URL、连接 Token 或平台凭据。Agent 返回的错误码还需经过显式白名单，未识别代码统一映射 `HEALTH_CHECK_UNAVAILABLE`。

批次码包括：`HEALTH_OPERATION_NOT_SUCCEEDED`、`HEALTH_OPERATION_UNCONFIRMED`、`HEALTH_OPERATION_WAIT_TIMEOUT`、`HEALTH_OPERATION_STATUS_UNAVAILABLE`、`HEALTH_OPERATION_STATUS_INVALID`、`HEALTH_SCOPE_UNRESOLVED`、`HEALTH_SCOPE_UNAVAILABLE`、`HEALTH_CONFIGURATION_UNAVAILABLE`、`HEALTH_NO_MATCHING_ENABLED_CHECKS`、`HEALTH_CHECK_LIMIT_REACHED`、`HEALTH_BATCH_TIMEOUT`、`HEALTH_BATCH_INTERRUPTED`。单项本地码包括检查被修改、停用、删除、busy、结果 revision 改变、无法核对或取消；实际业务探测保留已有安全健康码，例如 `HEALTH_STATUS_MISMATCH` 和 `HEALTH_SITE_STOPPED`。

## 接线和验证

注册单例 `OperationHealthFollowUpService` 和后台 `OperationHealthFollowUpWorker`。服务依赖现有 `ConsoleTaskStore`、`HealthMonitoringService` 与 `IMonitoringAgentClient`；可注入 TimeProvider 用于合成测试。提交保存 Agent 结果后、按原编号 GET 结果后，可调用 `followUp.Observe(task)` 加入缓存，该方法不进行网络或文件写入。即使没有浏览器 Observe，后台也会从 task store 索引发现已标记任务。

状态与拓扑通过现有 AgentClient 安全 GET 读取；业务探测继续使用现有健康检查通道，保留绑定、应用路径、管理端口、TLS 和响应内容限制，不新增业务 HTTP 直连或操作派发逻辑。

专项入口为 `OperationHealthFollowUpSelfTests.RunAsync()`，Transport.SelfTests 的命令选择器为 `--operation-health-only`。测试全部使用合成账本、模拟 Agent 状态/拓扑和模拟业务探测，不连接真实服务器。覆盖成功与非成功、历史不补跑、启用与范围筛选、共享池、读取失败隔离、并发幂等、原编号后台跟进、重启未知、revision/busy 边界、保留历史回执、上限与取消。Windows/IIS 和真实业务健康检查仍需在获授权的目标环境另行验收。
