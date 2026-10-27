# Windows 服务功能本地验证

2026-10-07，在 macOS arm64、.NET SDK 10.0.401 上完成。服务操作使用 Development 内存模拟；本记录不代表真实 Windows SCM、服务运行账号或 ACL 验收。

本文件记录源码实现阶段的验证。后续已生成 Agent 与 Console 的 Windows x64 自包含升级包，发布及包检查见 [升级包验证记录](windows-services-package-validation.md)。

## 完成范围

Console 新增服务列表、筛选、详情、启动、停止、重启、创建及启动类型属性编辑。Agent 新增独立 SCM 适配器和模拟适配器，写操作沿用预检、指纹、串行队列、持久化、去重与不重放规则。启动类型支持自动、延迟自动、手动及禁用，配置修改保持当前运行状态。

服务管理默认只读；现场启用范围、创建目录和账号说明见 [Windows 服务管理](windows-operations.md#windows-服务管理)。程序与依赖须由操作员准备，创建不自动复制程序、修改目录 ACL 或启动服务。

## 验证结果

| 检查 | 结果 |
|---|---|
| Windows x64、net10.0、Release、框架依赖构建 | Agent、Console 及引用组件通过，0 警告、0 错误；未 publish 或生成部署包 |
| 本地可移植 Release 构建 | 通过，0 警告、0 错误；与 Windows RID 输出分目录 |
| 核心与服务自测 | 39/39，通过策略、路径、账号、依赖、保护、启动类型保留其他属性、指纹漂移、去重、旧 IIS JSON/哈希、敏感和超长参数入账前拒绝 |
| 前端及历史页面自测 | 66/66，通过服务能力、筛选、详情、创建参数、属性、旧 Agent 提示、真实连接错误、迟到响应隔离及服务历史回跳 |
| 新服务 HTTP 联调 | 12/12，通过 Console → Agent 的查询、创建、启动/停止/重启、禁用改自动/手动仍停止、持久化去重、历史目标、秘密参数拒绝及旧 Agent 501 |
| 原有通信与来源保护回归 | 121/121，通过 Token、来源、Host、CSRF 及旧来源策略 |
| 历史查询回归 | 13/13，通过条件组合、分页、损坏拒绝及离线 Agent 查询 |
| 本地浏览器操作 | 实际完成禁用改手动与创建模拟服务；确认保持停止、创建参数可审查，无 JavaScript 错误或窄屏页面全局横向溢出 |

截图：[桌面列表](ui-evidence/windows-services-desktop.png)、[启动类型属性](ui-evidence/windows-service-properties.png)、[窄屏列表](ui-evidence/windows-services-mobile.png)。窄屏宽表可在表格内横向滚动。

## 可重复命令

依赖恢复输入未变化，本次使用已有恢复结果。先按项目级别验证 Windows RID，再生成本地测试输出；SDK 不支持对整个解决方案直接传入单一 RID。

```sh
dotnet build tests/IISManager.Transport.SelfTests/IISManager.Transport.SelfTests.csproj -c Release -r win-x64 --self-contained false --no-restore
dotnet build IISManagerTool.slnx -c Release --no-restore
dotnet tests/IISManager.SelfTests/bin/Release/net10.0/IISManager.SelfTests.dll
dotnet tests/IISManager.Transport.SelfTests/bin/Release/net10.0/IISManager.Transport.SelfTests.dll --windows-services-only "$PWD" "$(command -v dotnet)"
dotnet tests/IISManager.Transport.SelfTests/bin/Release/net10.0/IISManager.Transport.SelfTests.dll "$PWD" "$(command -v dotnet)"
dotnet tests/IISManager.Transport.SelfTests/bin/Release/net10.0/IISManager.Transport.SelfTests.dll --operations-only "$PWD" "$(command -v dotnet)"
node --test tests/IISManager.Console.Frontend.SelfTests.cjs tests/IISManager.Console.History.SelfTests.cjs
```

HTTP 自测仅启动回环地址模拟进程和合成旧 Agent，并按本次 PID 正常停止进程，删除各自临时账本。浏览器验收使用独立临时环境和浏览器配置，不访问现场服务；本次之前已存在的开发 Agent 未停止。

## 待 Windows 隔离环境验收

按 [Windows 服务非生产人工验收](windows-operations.md#windows-服务非生产人工验收) 检查真实 SCM 权限、服务程序协议、账号读/执行权限、PE 与目录 Owner/ACL、延迟启动标志、依赖保护和操作部分完成/未知状态。源码已完成且本地模拟通过，实际 Windows 服务尚未操作；本次未部署、生成部署包或改变生产配置。
