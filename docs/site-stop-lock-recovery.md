# IIS 站点启停误报与管理锁定修复

日期：2026-10-11（Asia/Shanghai）。本次完成源码调整及本地验证，尚未部署到服务器。

## 已确认的现场现象

提交记录的动作是 `site.stop`，目标为站点，`applicationPoolName=null`。独立读回为 `after.state=Stopped`，操作员随后确认 IIS 中该站点已停止；应用程序池仍处于 Started。

当前后端分别调用站点和应用程序池的停止方法。停止站点不会显式停止应用池，IIS 的 [Site.Stop](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.administration.site.stop?view=iis-dotnet) 和 [ApplicationPool.Stop](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.administration.applicationpool.stop?view=iis-dotnet) 是两个独立入口。名称相同不代表操作对象相同。

`partial` 使 `hasPendingTask()` 持续成立，控制台因此禁用站点、应用池和服务的变更按钮。原页面已有人工核实后继续管理的入口，但位于表格下方，表格附近没有解释按钮为何变灰，解除后也未主动刷新快照。

## 已定位的启停误报根因

IIS 的 RSCA 扩展将运行时 `state` 加入站点和应用池的 `Attributes`。旧 `AttributesHash` 不加区分地遍历全部属性，导致 SiteConfigurationEvidence 的“配置指纹”包含实际运行状态。配置保持原样时，成功停止使 Started→Stopped、成功启动使 Stopped→Started，隐藏指纹仍必然不同；ReadBack 因此把正常状态变化当成配置漂移并返回 partial。仅比对公开 SiteConfig 的测试无法覆盖这个分支。

依据为 [IIS 官方 RSCA 原始说明](https://blogs.iis.net/ksingla/working-with-rsca-using-configuration-apis/)及本地 Microsoft.Web.Administration 11.1.0 DLL 的 IL：Site.State、ApplicationPool.State 读取属性索引器 `state`；ConfigurationElement.Attributes 来自原生 AppHostElement.Properties，集合完整包装这些属性；ConfigurationAttribute.Value 重新读取原生值。

另有两个读回问题：旧循环仅在 Starting/Stopping 等待，首次读取到旧稳定态便过早结束；旧页面在 partial/unknown 后不刷新列表，因此启动操作后的表格可能仍显示停止前或提交前的状态。

## 现有版本的现场恢复

1. 在操作结果中保留原编号，并核对 IIS 的实际站点、应用池状态及相关配置。
2. 点击“已只读核实，保留记录并继续管理”，解除当前浏览器的管理锁定。
3. 点击“刷新快照”。若需恢复网站访问，在站点页面使用“启动”；若需停止应用程序池，在应用程序池页面使用“停止”，各自重新预检查后提交。

这一步只解除浏览器锁定，原记录仍保持 partial，不会重发旧请求。queued/running 仍须等待或按原编号查询。

## 本次源码调整

- 仅在 Site、ApplicationPool 的配置属性哈希中排除 RSCA `state`，公开状态仍独立验收，执行前预检查仍能识别运行状态变化。SSL 绑定动态配置、目录、关联池和其他配置证据继续校验。
- 读回按目标状态等待，最多 10 秒、间隔 200ms，使用单调时钟。重新启动每轮先核对 ID 和配置，确认停止后再单次启动；延迟、超时或漂移不触发动作重放。
- 站点操作的成功、部分完成、未知终态均触发独立快照刷新，涵盖立即返回、后续轮询和响应丢失场景；保留原结果及人工核实锁。
- 所有资源页上方显示锁定任务、动作、编号、状态及核实入口，禁用按钮的提示指向该区域。
- 两处核实入口复用同一恢复函数。确认后保留历史和表单草稿、清除旧预检查并读取最新快照；读取失败时不保留可操作的旧 IIS 快照。
- 应用池和站点表头、按钮提示及站点操作弹窗明确操作对象。
- 站点配置指纹变化返回 `site.configurationChanged`；公开字段或目标状态不符继续返回 `iis.readbackMismatch`。两类结果仍为 partial，并保留独立读回及禁止自动重放的规则。

## 验证与未确认项

前端自测 101/101，包括成功、部分完成、未知结果在立即返回和轮询后刷新真实状态且保留原账本与锁。站点专项自测 15/15，覆盖 RSCA 的五种状态不改变配置指纹、真实配置与 SSL 扩展变化仍改变指纹、旧稳定态到目标态的等待、超时保留实读状态、重启等待中配置漂移及停止/启动均最多一次。

先将原属性哈希流程提取为可测纯函数：新增 RSCA 状态回归在旧算法下失败，11/12 通过；过滤 state 后，该回归及新等待用例全部通过。新增列表刷新回归在旧页面中 1/6 通过，修复后 6/6 通过。这些证据复现了两处问题，不只是验证新代码的返回值。

隔离 HTTP 9/9，使用现有 Console→Simulation Agent 测试，验证创建、启停、重启、重复编号、记录查询、参数门禁和旧指纹拒绝。目标仅为随机回环端口，客户端明确 UseProxy=false；测试进程 1998/1999 和临时账本、日志目录已清理，没有连接或操作现场 IIS。

Agent 与 Console 均使用 .NET SDK 10.0.401 / net10.0 / Release / win-x64 / 显式框架依赖模式进行编译验证，零警告、零错误。测试运行于 macOS arm64，没有打开真实 IIS 管理句柄，编译输出不作为服务器部署包。

上述源码、DLL 与先失败后通过的回归已定位正常启停被判配置变化的原因。启动截图中的旧列表不能替代当次 Agent.after 或现场读回，真实 Windows 上的动作、权限和业务健康仍需人工验收。已经持久化的历史 partial/unknown 不会被修改为 succeeded；更新程序后，按原编号核实并保留记录，再重新预检查新的现场操作。

可重复运行专项检查：

```sh
dotnet build tests/IISManager.SelfTests/IISManager.SelfTests.csproj -c Release --no-restore
dotnet tests/IISManager.SelfTests/bin/Release/net10.0/IISManager.SelfTests.dll --site-lifecycle-only
node --test tests/IISManager.Console.Frontend.SelfTests.cjs
```

Windows/IIS 实机启停、权限、配置证据和业务恢复由操作员现场验收。本次没有生成新部署包或执行现场操作。
