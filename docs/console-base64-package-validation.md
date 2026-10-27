# Console Base64 公钥增量包验证

日期：2026-10-11。交付目录 [Console 增量包](../outputs/MKY服务器远程管理工具-Console公钥Base64-20261011-win-x64/README.md)。目标 Windows Server x64 / win-x64 / net10.0 / Release / 自包含，沿用 10.0.12 运行时。

基线为已核验的 1.0.3.0 站点启停修复包 Console 组件：14 个 overlay、394 个 required，完整运行集合 408 个。本次用户明确要求手工覆盖，未重新生成安装器，也不改写现场安装归属。

复用项目 publish-installer-component.py 和部署包 Skill，SDK 10.0.401 明确目标及 SelfContained=true、--no-restore 发布成功，零警告、零错误；10 个源码/构建输入及 21 个静态输入发布期间与组包时均未漂移。自动比较仅 IISManager.Console.dll/PDB 需要更新，配套 406 文件未变，无运行文件删除、新依赖或静态资源更新。

每端已有依赖保留，17 个 x64 原生文件及 Console/Core/Contracts 三组 DLL/PDB 匹配通过。包内提供默认关闭、空公钥值的 PublicKeyBase64 节点模板，说明要求合并进实际 appsettings.Local.json；现有配置不纳入 overlay。保留六份相关 .NET、ASP.NET Core 与 Fluent 许可/NOTICE。

复用上一轮站点标注专项 29/29，覆盖 SPKI/PKCS#1 Base64 配置、加密与签名、私钥/路径/无效数据拒绝、分页及独立读回。业务输入没有后续变化，不因打包重复测试。

最终包 16 文件、465,801 字节；文件、配置模板和说明冻结后生成 SHA256 清单 15 项。独立读回的缺失、多余、哈希不符、大小写冲突、链接、Mac 元数据及不允许脚本均为零，Skill.verify_package 通过。没有安装器、部署包校验脚本、配置保护工具、证据编译项目或真实调用值。

用户通过停止实际 Console 管理服务、备份、覆盖两个运行文件、迁移配置字段、恢复服务的方式更新。说明明确保留账本、配置、身份和权限，并记录已有安装器归属在手工覆盖后保留旧哈希、未来可能拒绝安装器更新的影响；不删除或改写归属记录绕过校验。

未执行远程传输、部署或真实平台数据写入，Windows Server 启动、平台连接及业务保存由人工验收。暂存发布文件在验证后清理，保留交付包和原有构建输出。
