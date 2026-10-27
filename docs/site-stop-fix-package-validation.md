# IIS 站点启停修复部署包验证

日期：2026-10-11（Asia/Shanghai）。交付目录 [修复包](../outputs/MKY服务器远程管理工具-站点启停修复-20261011-win-x64/README.md)。目标 Windows Server x64 / win-x64 / Release / 自包含；服务 net10.0，安装器 net10.0-windows，运行时 10.0.12，SDK 10.0.401，安装器文件版本 1.0.3.0。

本包包含已完成的 RSCA state 配置指纹修复、按目标状态等待独立读回、异常结果刷新快照及人工核实恢复入口。沿用本轮已通过的站点专项 15/15、前端 101/101、隔离 HTTP 9/9；业务输入未因打包改变，不重复业务测试。Setup 仅提高文件/信息版本，保留原安装及更新逻辑。

基线为本地已核验的站点别名与标签版组合包：根 69 项 SHA、70 文件，实际 EXE 中 Agent 346、Console 408 个运行文件、每端 2 个默认模板及 Legacy 16/24 项精确核对。基线只证明本地文件，未读取服务器现场；旧完整 publish 目录和最初手工包已经清理，因此从实际 EXE manifest 保留 Legacy，不把稀疏 overlay 当成原始基线。

两端均调用项目 publish-installer-component.py，复用部署包 Skill 的 SDK publisher，明确 win-x64 / Release / SelfContained=true / --no-restore。Agent 19、Console 10 个直接源码/构建输入稳定，Console 21 个静态源文件由 SDK 发布；独立核对 PDB 中 Core、Contracts、Agent、Console 的 51 个源文件/生成文档校验和，全部与当前源码或嵌入生成源一致。每端 3 组项目 DLL/PDB 配对及两端 4 个共享文件一致，每端 17 个 Windows x64 原生文件通过；42 个 br/gz 资源解压后匹配原始静态文件。

| 组件 | overlay | required | 完整运行文件 |
| --- | ---: | ---: | ---: |
| Agent | 4 | 342 | 346 |
| Console | 14 | 394 | 408 |

Agent 更新 Agent/Core DLL/PDB；Console 更新 Console/Core DLL/PDB、静态端点清单及 HTML/CSS/JS 的原文件与 br/gz 版本。SDK 差异比较没有删除运行文件、没有遗漏依赖，overlay 与 required 并集等于完整清单。手工增量仅适用于现场依赖匹配的无安装器归属服务；已有归属的服务使用新 EXE 更新，维护安装归属与后续更新兼容。

新载荷由本轮完整 SDK 发布文件生成并逐项核对。Agent ZIP 49,032,226 字节，Console ZIP 49,119,210 字节，Legacy 16/24 完整保留，安全默认模板中认证值为空。Setup Windows x64 自包含单文件 SDK publish 完成，48/48 安装器检查、实际 PayloadArchive 解包 348/410 文件及真实安装程序集内两端 ZIP 长度/SHA 比对全部通过。

交付 EXE 为 x64 PE32+ GUI、管理员 UAC、255 bundle 条目，NETCore/WindowsDesktop 10.0.12，匹配 SDK singlefilehost 的 .text/.pdata；1.0.3.0，150,082,727 字节，SHA256 `AB1090F493EFB7ECA8DFA193A7B73A1F1B5C51DA9EB271AEC37D2B56CC502C55`。两端 ZIP 已内嵌，操作员无需外部 ZIP。

沿用已核验旧包的 7 份 .NET、ASP.NET Core、WindowsDesktop 与 Fluent 依赖许可及 NOTICE，同版本文本逐项哈希匹配。全部运行文件、说明、许可和证据冻结后生成两端及根 SHA，并独立读回核验精确集合；两端 Skill.verify_package 通过。证据项目、源码和暂存日志不放在交付目录，包内没有真实配置、凭据、校验脚本或配置保护工具。最终文件数量、字节数及差异结果以末尾记录为准。

更新路径、组件顺序、现场配置及账本保留、归属漂移处理、人工业务验收和回退见包根部署说明。实际 Windows Server 安装/SCM/ACL、防火墙、IIS 状态转换及业务功能尚未实机验收，未执行传输、上线或生产配置变更。暂存发布、载荷、提取程序集和探针在包验收后清理，保留已交付文件及源码构建输出。

最终交付共 47 文件、151,564,216 字节，根 SHA 清单 46 项；缺失、多余、哈希不符、重复/大小写冲突、链接、Mac 元数据及不允许脚本均为零。许可补齐后只重新生成与核验根清单，未变化的 EXE、运行文件和业务测试不重复执行。本次暂存根目录已清理，最终包保留并独立读回确认。
