# 首期监控功能验证

日期：2026-10-25（Asia/Shanghai）。范围为服务器总览、站点/子应用健康检查、证书预警及已有页面导航接入。

## 已完成验证

| 验证 | 结果与范围 |
|---|---|
| Windows 目标构建 | Agent、Console、Contracts、Core 及传输测试项目以 `win-x64`、`net10.0`、`Release` 构建成功，0 警告、0 错误；未执行 publish |
| Console 监控后端 | 40/40 自测通过，覆盖模块失败/畸形响应隔离、缓存、证书边界、配置版本、重复目标、落盘、调度、并发编辑、损坏与漂移及无效成功结果 |
| Agent 健康探测 | 43/43 自测通过，使用注入传输和合成 IIS 拓扑，覆盖当前绑定/应用归属、本机地址与 IP 地址族、非运行站点、超时、取消、模拟、跳转与并发限制 |
| 前端 | Node 测试入口 164/164 通过，含新增总览 17 项、工作台/操作记录/认证相关回归及密码测试入口；密码脚本内部另报告 13/13 |
| 本地 HTTP 联调 | 12/12 通过：实际 Console 进程与本机 Node 假 Agent 通信，验证在线/部分失败/离线、证书和服务过滤、CSRF 403、配置保存和独立读回、启用/检查/停用/删除、冲突及 Production 未登录 401/302 |
| 浏览器 | 检查默认桌面与 390×844 窄屏；核对健康检查弹窗默认停用、全局域名搜索，以及从结果跳回指定服务器站点工作台。窄屏表格在各自容器内横向滚动 |

构建时关闭共享编译进程，避免本机共享编译器的输出访问限制；未修改文件权限或安全设置。示例命令在项目根目录执行：

```sh
dotnet build tests/IISManager.Transport.SelfTests/IISManager.Transport.SelfTests.csproj -c Release --no-restore -p:UseSharedCompilation=false
dotnet tests/IISManager.Transport.SelfTests/bin/Release/net10.0/IISManager.Transport.SelfTests.dll --monitoring-only
dotnet tests/IISManager.Transport.SelfTests/bin/Release/net10.0/IISManager.Transport.SelfTests.dll --agent-health-only
dotnet build tests/IISManager.Transport.SelfTests/IISManager.Transport.SelfTests.csproj -c Release -r win-x64 --no-restore -p:UseSharedCompilation=false
node --test tests/IISManager.Console.Frontend.SelfTests.cjs tests/IISManager.Console.History.SelfTests.cjs tests/IISManager.Console.Authentication.SelfTests.cjs tests/IISManager.Console.Overview.SelfTests.cjs tests/frontend/password-change.test.cjs
node tests/monitoring-http-smoke.cjs --dotnet /Users/pengwei/.dotnet/dotnet
```

以上命令复用已有且覆盖 Windows RID 的恢复结果；新环境需先按项目说明恢复匹配依赖。HTTP 脚本原生直连 loopback，使用独立内容根目录、合成配置和凭据，不加载现场配置。生产认证测试只验证未登录拒绝，不代表已完成真实用户登录验收。

## 页面证据

以下截图全部来自本地合成数据，不是现场服务器状态。

- [桌面总览](ui-evidence/monitoring/overview-desktop.png)
- [桌面完整页面](ui-evidence/monitoring/overview-full-desktop.png)
- [健康检查编辑](ui-evidence/monitoring/health-editor-desktop.png)
- [窄屏完整页面](ui-evidence/monitoring/overview-mobile.png)

## 验证边界与清理

开发环境为 macOS arm64。本次没有在 Windows/IIS 执行真实业务 GET、验证实际 Host/SNI 路由或证书信任链，也未覆盖用户现场的业务依赖；这些需在指定 Windows 环境使用专用只读检查地址验收。HTTP 脚本中的 Agent 及其返回的业务检查结果均为合成数据。

本次预览启动命令为 `node tests/monitoring-http-smoke.cjs --dotnet /Users/pengwei/.dotnet/dotnet --preview --preview-port 5197`。已结束本次 Node 主进程及两个 Console 子进程，关闭本机假 Agent 监听器，删除隔离临时目录与一次性日志，恢复浏览器视口并关闭验证标签。保留源码、自测脚本、说明和截图。未生成部署包，未部署或修改生产配置。
