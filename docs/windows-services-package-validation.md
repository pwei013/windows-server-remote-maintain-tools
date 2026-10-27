# Windows 服务功能升级包验证

2026-10-07，在 macOS arm64 / .NET SDK 10.0.401 上完成本地交叉发布和包核验，未进行实际 Windows 安装、更新或生产配置变更。

## 交付

| 组件 | 包及说明 | 增量 | 完整运行文件 | 未变配套文件 |
|---|---|---:|---:|---:|
| Agent | [升级说明](../outputs/IISManagerTool-Agent-WindowsServices-20261007-win-x64/部署说明.md) | 6 | 346 | 340 |
| Console | [升级说明](../outputs/IISManagerTool-Console-WindowsServices-20261007-win-x64/部署说明.md) | 22 | 396 | 374 |

目标 Windows Server x64 / win-x64 / net10.0 / Release / 自包含，内置 .NET 与 ASP.NET Core 10.0.12。两个未压缩包各有相同的 `IISManager.Setup.exe`，内嵌新版两组件完整载荷，GUI 目标 net10.0-windows，内置 Windows Desktop 10.0.12；EXE 为 150,012,842 字节。图形更新要求服务器桌面体验，先更新 Agent 再更新 Console；EXE 成功后不再覆盖 overlay。默认保留现场配置，服务写操作需人工准备管理范围。

## 本轮验证

- 使用项目 `publish-installer-component.py` 与部署包 Skill 的 SDK publisher 完成两组件匹配 RID/自包含模式的 publish，0 警告、0 错误。配置、日志、账本和秘密未进入运行增量。
- 旧本地工作台包及 EXE 完整性核验后，从实际内嵌 ZIP 提取并逐文件验证基线；Console 基线另与旧 PUBLISH/REQUIRED 集合比对一致。没有读取生产文件，不假定现场已安装该版本。
- 两组件 `.deps.json`、运行时、原生资产和全运行文件集匹配；34 个 Windows 原生文件均 x64，6 组项目 DLL/PDB 配对通过。PDB 中 63 个可读源文档校验匹配；2 个 SDK 内存生成文档单列记录，不声称已经读到其源文件。
- Console 34 个 `.br` / `.gz` 资源解压后与原文件一致，SDK 静态发布源输入稳定。
- 45 项安装器自测通过；真实新载荷共 746 文件使用项目 `PayloadArchive` 完整解包、检查精确集合、大小及 SHA256；旧手工包兼容清单原样保留。
- 从最终 EXE 提取真实程序集，独立读取两项内嵌资源并与已验证 ZIP 的大小/哈希核对。255 个 bundle 条目、x64 PE、GUI 子系统、管理员 UAC 及 SDK 原生 host 比对通过。
- Agent 包 18 个交付文件，Console 包 34 个交付文件；冻结说明与元数据后最后生成 SHA256，独立读回缺失、多余、哈希不符均为空。overlay 和 required 的并集另与本轮本地完整 publish 逐文件比对通过。
- 前轮 251 项自动检查与实际浏览器模拟结果见 [实现阶段记录](windows-services-validation.md)，业务源码未变化，打包未重跑该组业务回归。

包内没有配置保护工具、校验脚本、证据项目、Mac 元数据、真实配置/凭据。本任务发布暂存、基线解包、临时检查工具已在最终核验后清理，原缓存和历史包保留。依赖文件及最终包证据保存在包内 JSON 中。

## 验证边界

本地发布、元数据和哈希结果不能替代 Windows/IIS、SCM、真实运行身份、ACL、服务程序协议及业务验收。实际部署由人工操作员完成，完整包传入外部运维目录；安装器管理的服务用 EXE 保持归属，符合基线的手工安装才覆盖 overlay。生产文件、数据、Token、证书、日志与账本均保留。
