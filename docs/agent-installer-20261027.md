# Agent 完整安装包核验

2026-10-27，为本次应用池创建与统一编辑修复生成完整 Agent 图形安装包，交付目录为 [Agent安装包-20261027-win-x64](/Users/pengwei/Publish/IISManagerTool/Agent安装包-20261027-win-x64/)。直接入口为 [IISManager.Setup.exe](/Users/pengwei/Publish/IISManagerTool/Agent安装包-20261027-win-x64/IISManager.Setup.exe)，使用方式见[安装说明](/Users/pengwei/Publish/IISManagerTool/Agent安装包-20261027-win-x64/安装说明.md)。

## 产物范围

- Windows Server x64 桌面环境，Agent `net10.0`、安装器 `net10.0-windows`，Release、自包含，运行时 10.0.12，SDK 10.0.401。
- 本次仅更新完整 Agent 载荷：346 个运行文件、2 个空凭据默认配置模板，16 个历史运行文件条目用于安装器已有兼容核对。新装不依赖 V2 或外置 ZIP。
- 沿用统一向导，选择 Agent。Console 载荷与 2026-10-19 安装包完全一致，437 个运行文件、2 个默认模板；不用于交付本轮 Console 前端更新，Console 仍按独立增量包说明处理。
- 安装器版本通过 publish 属性显式指定为 1.0.4.0，未改动项目源版本或安装逻辑；未包含敏感信息保护工具、额外校验脚本、外置载荷 ZIP 或现场配置。
- 交付共 11 个文件、151,458,463 字节：EXE、安装说明、构建证据、SHA256 清单及 7 个许可文件。EXE 为 151,282,862 字节，SHA256 为 `39876F114CFF6F334512827881CA733184BF4AB3DA0AD80E53850DD73F4B16A7`。

## 本地验证

1. Agent 和安装器使用 Windows x64 SDK publish 产物。Agent 发布零警告、零错误，安装器发布退出码 0；79 个构建输入文件在发布后保持一致。
2. 当前 Agent 的 346 个完整运行文件与“应用池与统一编辑-20261027-win-x64”修复版发布记录精确匹配文件集合、大小与 SHA256；3 组项目 DLL/PDB 匹配，17 个原生文件为 x64。
3. 安装器 48/48 自测通过。Agent 348 个、Console 439 个实际配置及运行文件完整解包，逐文件大小、哈希与精确集合核对通过；发布 EXE 的真实内嵌两个 ZIP 与已核验 ZIP 完全一致。
4. 发布 EXE 为 x64 GUI PE、版本 1.0.4.0，声明 `requireAdministrator`。内嵌运行时为 .NET/WindowsDesktop 10.0.12，SDK singlefilehost 的代码及展开信息匹配；未进行 Authenticode 签名。
5. 内容冻结后最后生成 SHA256 清单。复制到固定 Publish 目录后独立读取 11 个文件、10 条校验项以及真实 EXE 内嵌资源，缺失、多余、哈希不符均为空；无符号链接、大小写重名或 macOS 元数据。临时发布、解包与校验文件在交付核验后清理，旧安装包保持原样。

## 现场边界

未执行 Windows 图形启动、新装或更新、SCM、ACL、防火墙持久化、真实 IIS 和业务验收，也未复制到服务器或部署。更新保留现场配置、Token、身份、日志及最新账本；手工增量更新造成归属记录漂移时先只读核实，不删除归属或账本强行安装。完整操作步骤与回退边界以包内安装说明为准。
