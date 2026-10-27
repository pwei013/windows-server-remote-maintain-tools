# 站点双子表 v2 与安装器启动修复验证

日期：2026-10-10（Asia/Shanghai）。交付见 [v2 包](../outputs/MKY服务器远程管理工具-站点双子表-v2-20261010-win-x64/README.md)，目标 Windows Server x64 / win-x64 / Release / 自包含；服务 net10.0，安装器 net10.0-windows，运行时 10.0.12，开发机 macOS arm64、SDK 10.0.401。

包内新增空子表隐藏、根应用去重、无子项展开入口及零数量摘要隐藏，菜单/键盘顺序为站点、池、Windows 服务。73 项前端自测及实际合成预览已于前一实现任务通过，本轮源码输入未变化，不为打包重复运行；见 [UI 验收](../design-qa.md)。

## 原故障与代码修复

用户提供的 Windows Server 2019 .NET Runtime 事件显示 DirectoryNotFoundException，调用链为 ThemingScope.CreateActivationContext → Application.EnableVisualStyles → ApplicationConfiguration.Initialize → Program.Main：当前用户会话临时目录不存在，主题文件无法创建。旧代码中的初始化在 try 之前，因此未进入安全异常提示。0xe0434352 为 CLR 异常代码，具体根因由这条 .NET 事件而非 KERNELBASE.dll 名称确定。

SetupStartupEnvironment 在界面初始化前创建实际临时路径，以随机、CreateNew / DeleteOnClose 的文件探针写入及 Flush 验证访问，关闭时清理探针，保留既有文件；初始化已移入 try，失败退出码为 1，界面只输出固定阶段、状态码和 SafeFailureSummary 的类型/HResult。未修改全局 TEMP/TMP、目录 ACL、账号或服务配置。Setup 的 FileVersion 为 1.0.1.0，InformationalVersion 为 1.0.1；服务版本和依赖还原输入保持原值。

新增 3 项真实文件系统用例：先重现缺失会话目录的写入失败，准备后验证可写且探针移除；已存在目录的文件保留；文件占用目录路径及相对路径安全拒绝。安装器自测 48/48 通过，测试环境自动清理。该证据为跨平台 IO 与安全契约验证，不是 Windows WinForms 实机启动验证。

参考：[Path.GetTempPath 不检查目录存在/访问的官方说明](https://learn.microsoft.com/en-us/dotnet/api/system.io.path.gettemppath?view=net-10.0)、[CLR 异常代码](https://learn.microsoft.com/en-us/shows/inside/e0434352)。现场原因及操作员最小核对见包内启动故障说明。

## 发布、基线与累计范围

Console 使用原 scripts/publish-installer-component.py 和部署包 Skill，net10.0 / win-x64 / Release / SelfContained=true / --no-restore 新发布成功，0 警告、0 错误，C#及项目输入和 SDK 静态资源指纹保持稳定。Agent 业务源码无变化，从上一已核验 EXE 的真实 PE 资源提取原 ZIP 并重新核验完整集合，复用既有 SDK 产物，未声称重发 Agent。

原 20261007 两端本地目录当前不存在，因此实际基线为上一 v1 组合包。按历史包规则只读忽略 Finder 添加的元数据，不改旧包；其 root/component SHA、真实 EXE、PE 资源边界、ZIP 精确文件集合、大小和每项哈希通过。Console 先由原 Skill 对 v1 产出 10 文件差异，再将 v1 累计 31 路径全部并入，运行文件均从本轮 SDK publish 取得，配套 371 文件与 v1 逐项一致，删除 0；避免仅交付小差异而遗漏站点契约。原 20261007 累计基础由 v1 的已核验清单继承，未重建或伪造旧包。

| 组件 | 增量 | 配套 | 完整运行集合 | 载荷中的安全模板 |
|---|---:|---:|---:|---:|
| Agent | 6 | 340 | 346 | 2 |
| Console | 31 | 371 | 402 | 2 |

两端各 17 项原生 x64、各 3 组项目 DLL/PDB、Core/Contracts 四文件一致性通过；38 个 br/gz 资源逐项解压与原始资源一致。Agent 16、Console 24 个 legacy 项保持原数组，Console legacy JSON 字节片段与前版相同，4 个默认模板无现场凭据。

## EXE 与最终文件核验

Setup 使用原 SDK Windows x64 自包含单文件发布，不裁剪、嵌入原生库、启用压缩；新 ZIP 由实际发布程序集内 PE 资源独立核对。真实 PayloadArchive 提取 348 / 404 文件、两端内嵌 ZIP 的大小及 SHA256 比对均 2/2 通过。EXE x64 PE32+ GUI、管理员 UAC、255 bundle 条目、NETCore/WindowsDesktop 10.0.12 及匹配 SDK singlefilehost .text/.pdata 均通过，文件版本独立读回为 1.0.1.0。

EXE 150,034,087 字节，SHA256 `AACA2A754341284F67D51E5F2FAADDD1BB9AEAD5EB65235D1D333202FB20325B`；Agent ZIP 49,030,436 字节、原字节保留；Console ZIP 49,072,504 字节。完整证据在包内 INSTALLER / PACKAGE / BASELINE JSON 及各组件 BUILD 文件。

最终未压缩目录共 62 文件、151,685,546 字节。说明、元数据、许可证与运行文件冻结后先生成两个组件清单，再生成 61 项 root 清单（另有清单自身），独立核对精确文件集合及逐文件哈希通过；缺失、多余、哈希差异、大小写冲突及 macOS 元数据均为零。包内不含校验脚本、配置保护工具或临时编译证据项目，历史包保留。

未在目标 Windows Server 2019 执行新版 WinForms 启动、安装/更新、真实 IIS/SCM/ACL、身份、防火墙或业务健康验证；未传输、部署或修改现场配置。操作员先在原故障机器打开新版向导确认启动，再按归属和基线条件更新；原 unknown/partial 现场先只读核实，不自动重放或回滚。
