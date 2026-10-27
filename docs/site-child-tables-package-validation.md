# 站点启停与双子表组合包验证

完成日期：2026-10-10。目标为 Windows Server x64，Agent / Console 使用 net10.0、win-x64、Release、自包含，运行时 10.0.12；图形安装器项目为 net10.0-windows、Windows x64 自包含单文件。开发机为 macOS arm64，SDK 10.0.401。

交付目录：[组合更新包](../outputs/MKY服务器远程管理工具-站点启停与双子表-20261010-win-x64/README.md)。包括站点启动、停止、重新启动、方案 2 两张行内子表及此前未打包的 Agent 创建服务缺项诊断；默认策略及现场配置无变更。

## 发布与对比

复用项目 `scripts/publish-installer-component.py` 和部署包 Skill 的 publisher / package 模块。两端 SDK publish 均显式指定 Release、win-x64、SelfContained=true，匹配的恢复输入已存在，使用 --no-restore；本轮发布零警告、零错误。Console 静态资源来自 SDK publish，原始资源与源码一致，发布过程输入指纹保持稳定。

| 组件 | 已核验本地基线 | 增量 | 配套 | 完整运行文件 |
|---|---|---:|---:|---:|
| Agent | IISManagerTool-Agent-WindowsServices-20261007-win-x64 | 6 | 340 | 346 |
| Console | MKY服务器远程管理工具-Console-20261007-win-x64 | 31 | 371 | 402 |

历史包 SHA 清单、JSON 运行文件清单及真实 EXE 载荷已独立核验。实际归档内运行文件与历史 PUBLISH / REQUIRED 联合集合一致；本地包不表示现场已安装该版。首版手工目录当前缺失，本轮从已核验历史实际 ZIP 精确保留 Agent 16 项、Console 24 项旧运行文件兼容清单；未编造缺失基线，也未改变历史包。

Agent 更新三个项目的 DLL/PDB 共 6 文件。Console 更新三个项目的 DLL/PDB、SDK 静态端点清单、页面与脚本、样式、两个展开图标及对应 br/gz 资源，共 31 文件。所有配套文件与发布及本地基线的字节数、SHA256 一致。两端各 17 项 Windows 原生架构检查通过，三个项目 DLL/PDB 各自配对通过，Core / Contracts 在两端一致。38 个压缩静态资源解压后均与发布原文件逐字节一致。

## 完整载荷与安装器

安装器沿用项目已验证的载荷格式和安全默认模板规则；完整 Runtime 文件来自上述 SDK 输出，更新不生成现场配置。新载荷含 Agent 348 项、Console 404 项，其中每端各 2 项空凭据默认模板，仅供新装使用。

| 项目 | 本轮结果 |
|---|---|
| 安装器边界自测 | 45/45 通过 |
| 实际 PayloadArchive 提取、路径、大小、SHA256 核验 | 2/2 通过，共 752 文件 |
| 实际安装程序集的两个内嵌资源比对 | 2/2 通过，大小及 SHA256 均与新 ZIP 一致 |
| PE、bundle、UAC 与 SDK host 核验 | Windows x64 PE32+ GUI，requireAdministrator；255 条 bundle，NETCore / WindowsDesktop 10.0.12，匹配 SDK host 比对通过 |

最终 EXE 为 150,033,322 字节，SHA256 `4F255F2B42D80ECE3F00FCEEA0669D1E154F812E7257033E2B9F1064BD50C5E5`。两端新载荷分别为 49,030,436 / 49,072,198 字节；具体 SHA256、原生架构和 PDB 配对数据保存在包内 JSON 元数据。

## 最终交付核验与边界

交付为未压缩组合目录，一个公共 EXE 加两端增量；完整 EXE 供维护安装归属的更新，手工增量仅适用于无归属且匹配基线的安装。运行配置、日志、账本、证书及真实秘密不在 overlay 内。许可证随包保留；不交付校验脚本、保护工具或临时编译证据项目。

最终说明、JSON、运行文件及许可证全部冻结后，先生成两端 SHA256 清单，再生成覆盖整个组合目录的清单，随后独立读回精确路径集合与每个文件哈希。核验覆盖隐藏文件、重复路径、大小写冲突及 macOS 元数据；分别检查缺失、多余和哈希差异。传输与现场文件正确性由人工操作员确认。

最终实际交付共 61 个文件、151,600,554 字节；根清单 60 项，另有根清单本身。Agent / Console 子清单分别覆盖 10 / 35 项文件，另含各自清单本身；所有读取与哈希通过，缺失、多余、哈希不符、路径冲突、重复项、macOS 元数据和校验脚本均为零。Core / Contracts 的 DLL/PDB 在两端完全一致。

功能实现阶段的核心、前端、操作历史及本机 HTTP 模拟结果继续有效，本轮没有重新运行不受组包影响的业务用例；详情见 [站点启停验证](site-lifecycle-validation.md) 和 [设计验收](../design-qa.md)。本轮新增的是 Windows 目标发布、实际新载荷、安装器和包文件核验。

未在目标 Windows 环境执行 WinForms 安装或更新、真实 IIS/SCM/ACL、运行身份、目录访问、防火墙及业务健康验证；未传输、部署或修改现场配置。使用说明明确先 Agent 后 Console，未知结果先只读核实，产生新 site.* 账本后回退须先评估兼容性，不删除或替换账本来绕过去重。
