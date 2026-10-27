# 站点启动、停止和重新启动验证

完成日期：2026-10-10（Asia/Shanghai）。本次只修改源码、说明与验证文件，未发布、部署或生成安装/更新包。应用与虚拟目录子表格仅做 Product Design 设计预览，现有详情侧栏保持原实现。

## 本地证据

- Windows Server 目标源码构建：Agent、Console 分别使用 .NET SDK 10.0.401、`net10.0`、`win-x64`、Release、显式框架依赖构建（`--self-contained false`）；两个工程均为 0 警告、0 错误。此处为编译验证，未执行 SDK publish 或生成部署产物。
- 核心自测：47/47，包括新站点动作的目标与空参数约束、幂等启停、配置/其他站点/应用池保留、预检查只读、指纹漂移、停止读回后单次启动、替换/缺失/配置漂移/超时拒绝、失败不重试、账本持久化和重复编号去重。停止阶段的原生动作通过委托模拟验证顺序和故障边界。
- 前端和操作记录自测：73/73，包括站点目标、按钮可用状态、过期行回调、预检查与提交编号/指纹一致、待核对结果锁定及三个新动作的历史查询和标签。
- 本地 HTTP：9/9，复用现有测试进程和客户端，模拟 Console → Agent 的创建、启动、停止、重启、原编号重复提交、快照/拓扑读回、动作筛选、参数门禁及过期指纹拒绝。仅访问独立回环端口，客户端明确 `UseProxy=false`；未调用现场 IIS 或外部服务。指纹被 Agent 拒绝时，Console 仍保留原意向供只读核对，不能把 HTTP 202 当作动作已成功。
- Codex 内置浏览器：在本次独立的 17443（Console）/17444（模拟 Agent）端口依次创建模拟站点、启动、重新启动、停止，全部预检查并按原编号读回成功。桌面 1440 × 1024 下表头、状态、目录、地址及三个动作没有覆盖；默认窄屏下保留表格横向滚动。运行时视口恢复，测试页关闭；测试进程已正常结束。

实际界面证据：

- [重新启动成功的桌面截图](ui-evidence/site-lifecycle-restart-desktop.jpg)
- [停止成功的桌面截图](ui-evidence/site-lifecycle-stopped-desktop.jpg)
- [启动成功的窄屏截图](ui-evidence/site-lifecycle-running-narrow.jpg)

本机测试 DLL 为独立的无 RID 本地验证输出，不作为 Windows 服务器部署产物。上述结果不代表真实 IIS 管理权限、原生状态转换或业务功能已验收。

## 可重复命令

在项目根目录使用已确认的 .NET 10 SDK 和 Node.js 路径运行；依赖恢复输入未改变时沿用 `--no-restore`。Windows RID 必须逐工程指定，SDK 不接受解决方案级 `-r`。

```sh
dotnet build src/IISManager.Agent/IISManager.Agent.csproj -c Release -r win-x64 --self-contained false --no-restore
dotnet build src/IISManager.Console/IISManager.Console.csproj -c Release -r win-x64 --self-contained false --no-restore
dotnet build tests/IISManager.SelfTests/IISManager.SelfTests.csproj -c Release --no-restore
dotnet tests/IISManager.SelfTests/bin/Release/net10.0/IISManager.SelfTests.dll
node --test tests/IISManager.Console.Frontend.SelfTests.cjs tests/IISManager.Console.History.SelfTests.cjs
dotnet build tests/IISManager.Transport.SelfTests/IISManager.Transport.SelfTests.csproj -c Release --no-restore
dotnet tests/IISManager.Transport.SelfTests/bin/Release/net10.0/IISManager.Transport.SelfTests.dll --site-lifecycle-only "$PWD" "$(command -v dotnet)"
```

HTTP 测试复用现有 WindowsServiceTransportSelfTests 的隔离进程、认证、请求和退出清理，只执行站点专项分支；测试文件、账本和临时进程在结束时清理。本次浏览器验证复用 `scripts/run-dev.sh`，将 `Agent__StateDirectory` 和 `Console__TaskDirectory` 指向本次 `work/site-lifecycle-20261010` 目录，通过环境变量设置独立端口，未修改现场配置。脚本固定输出的 7443/7444 提示不是本次覆盖后的实际端口；实际测试使用 17443/17444。

## Windows/IIS 人工验收

更新程序后，由操作员在获准的 Windows 非生产环境选择可停机的测试站点执行：

1. 记录站点 ID、绑定、自动启动开关、根/子应用、虚拟目录及各应用池状态；先停止站点，确认根应用与子应用全部出现在影响范围中。
2. 启动、停止、重新启动各一次，逐次预检查、提交，并按原编号核对最终状态。重新启动仅适用于正在运行的站点。
3. 核对原配置、其他站点和应用池配置保持原样。应用池实际运行状态可能受 IIS 自身按需激活影响，Agent 不显式调用应用池启停或回收。
4. 在受控测试中核对停止超时、目标漂移或启动失败的 `partial`/`unknown` 记录。发生异常只读确认现场，不自动重放或回滚。
5. 运行各应用的只读业务查询；站点 Started 或按钮可用不能替代业务健康检查。

含新站点动作的账本不支持无条件降级到旧版程序。旧程序会拒绝未知动作，不能删除记录绕过保护。实际更新和现场操作由人工完成。
