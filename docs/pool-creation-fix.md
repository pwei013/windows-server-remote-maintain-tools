# 创建应用程序池后报“部分完成”的排查与修复

完成日期：2026-10-27（Asia/Shanghai）。本次修改源码及验证说明，未生成部署包或执行现场更新。

## 现场证据与判断

- 截图为 `applicationPool.create` 的 `partial` 结果，提示 `IIS 管理 API 返回错误`。用户刷新列表后确认目标应用池已经存在。
- 旧版 `WindowsIisBackend.ExecuteAsync` 在提交配置后立即读取新池的 `State`，状态为 `Starting` 或 `Started` 时立即调用 `Stop()`；这些操作位于原有的独立读回轮询之前。
- 该错误文案来自 `COMException` 的统一映射。`partial` 表明执行已跨过可能写入的边界，异常处理中的新 `ServerManager` 又读到了目标；不能据此认定创建未发生，也不能认定全部参数均已正确保存。
- 最符合当前证据的原因是：配置已经落地，而 WAS/RSCA 的运行时注册或状态转换尚未完成，立即读取状态或停止新池触发异常。旧结果没有保存 HRESULT 和失败阶段，因此尚不能排除提交或最终读回阶段的其他 COM 错误，不能声称已在现场复现特定错误号。

IIS 的 `AutoStart=true` 同时作用于创建时自动启动和 IIS 启动时自动启动；见 [Microsoft ApplicationPool.AutoStart](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.administration.applicationpool.autostart?view=iis-dotnet)。按用户进一步确认的规则，当前源码已移除创建后强制停止：`AutoStart=true` 须读回 Started，`false` 或省略时须读回 Stopped。先前版本中“自动启动后再停止”的规则已取消。

## 源码变更

- 预检查按所选 AutoStart 生成目标状态，并将目标状态加入差异和提示；模拟后端遵循相同语义。
- 创建提交成功后，直接以全新的 `ServerManager` 句柄独立读回配置与预期状态，默认最多等待 10 秒、间隔 200 毫秒。没有创建后的强制停止或补充启动动作。
- 只在创建后的读路径中将 `0x800710D8`、`0x80070490` 作为可等待的“对象尚不可读”错误。它们分别对应 [ERROR_OBJECT_NOT_FOUND](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--4000-5999-) 和 [ERROR_NOT_FOUND](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1000-1299-) 的 HRESULT；这些是修复覆盖的错误，不是已经取得的现场错误号。
- `Starting`、`Stopping`、`Unknown` 或暂时与所选目标不同的状态继续只读等待；目标缺失或到期未确认时保留 unknown/partial 结果，不补写、不回滚。
- 独立读回仍须核对所选状态、身份及全部公开配置，不能仅因配置已保存或等待结束就判定成功。
- 提交动作、权限错误和其他异常均不自动重试。原有轮询的四参数接口保留，其他调用方的异常行为不变；编辑已有池的 AutoStart 仍保留原运行状态。
- COM 错误保存安全的十六进制 HRESULT 和失败阶段；后备读回也失败时保留原始安全错误。异常原文不进入结果，避免泄露配置或凭据。

主要文件：`src/IISManager.Agent/Backends/WindowsIisBackend.cs`、`src/IISManager.Core/IisStatePolling.cs`。此次源代码变更涉及 Agent 与 Core；后续更新必须使用匹配构建的两者，不能单独替换一个 DLL。本轮未制作更新包。

## 本地验证

- .NET SDK 10.0.401，目标框架 net10.0。
- 自测工程 Release 本地构建：0 警告、0 错误。
- 应用池专项：13/13 通过，覆盖两种自动启动选择及省略时的默认值、预检查只读与状态差异、运行时注册延迟、持续失败到期、权限错误不重试、错误脱敏、最终配置核对及编辑保留当前状态。
- 共享状态轮询所影响的站点启停/重启专项：15/15 通过。
- Agent Windows Server x64 源码构建：`win-x64`、Release、显式框架依赖模式，0 警告、0 错误。它是交叉构建验证，未作为服务器部署产物交付。

复现命令（在项目根目录，依赖已恢复且输入未变化）：

```sh
dotnet build tests/IISManager.SelfTests/IISManager.SelfTests.csproj -c Release --no-restore
dotnet tests/IISManager.SelfTests/bin/Release/net10.0/IISManager.SelfTests.dll --pools-only
dotnet tests/IISManager.SelfTests/bin/Release/net10.0/IISManager.SelfTests.dll --site-lifecycle-only
dotnet build src/IISManager.Agent/IISManager.Agent.csproj -c Release -r win-x64 --self-contained false --no-restore
```

隔离 Development Simulation 的真实 Agent/Console HTTP 联调确认 `AutoStart=false` 最终 Stopped、`true` 最终 Started。原生 IIS 调用在 macOS 不可运行；状态异常专项使用实际轮询及 Windows 适配器的纯过滤/比较方法，不连接真实 IIS，也未创建、停止或更改现场应用池。

## 现有记录与现场核对

1. 对当前已存在的应用池，刷新快照，核对状态、名称、运行时、管道、32 位开关、AutoStart、StartMode、空闲设置与实际身份；不要重新提交创建请求，也不要删除池或账本以绕过旧记录。
2. 核对现场后，可使用页面的“已只读核实，保留记录并继续管理”解除界面锁定。旧记录继续保持原状态，不会补写成成功或自动重放。
3. 如确需调整已存在池的配置或状态，先核实关联应用与影响，再使用相应编辑/启停动作完成预检查和确认，不重放原创建请求。
4. 由操作员更新程序后，在获准的 Windows 非生产环境验证 AutoStart=false 的新池最终为 Stopped、true 的新池最终为 Started，配置与预检一致；同时检查创建后无额外停止动作。若仍失败，保留新结果中的 HRESULT、阶段及原编号用于进一步定位。

本次未验证真实 Windows/IIS 的运行时同步、服务身份权限或业务功能，未改变现有部分完成记录。
