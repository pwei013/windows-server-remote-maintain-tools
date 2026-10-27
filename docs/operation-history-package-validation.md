# 操作记录姓名与持久化增量包验证

2026-10-14。交付[Windows x64 未压缩增量包](../outputs/MKY服务器远程管理工具-操作记录姓名与持久化-20261014-win-x64/README.md)，仅更新 Console，人工更新见[部署说明](../outputs/MKY服务器远程管理工具-操作记录姓名与持久化-20261014-win-x64/部署说明.md)。Agent、安装器不更新，不提供实际部署脚本或新 SQL。

沿用既有项目 publisher，SDK10.0.401 对 net10.0 / win-x64 / Release / SelfContained=true 执行 SDK publish；恢复目标匹配，使用 no-restore，零警告、零错误。内置 Microsoft.NETCore.App、Microsoft.AspNetCore.App 10.0.12。13项发布求值源输入、32项源静态资源在发布中稳定；扩大到Console/Core/Contracts、构建定义和静态资源的61项输入，在发布与打包后逐项核对未漂移。

直接基线为“修改密码-20261013-win-x64”。原包额外多出 `.DS_Store`，标准精确校验将它报告为多余；原SHA的30项全部匹配，没有缺失、哈希不符或重复路径。按Skill既定基线合同只读排除macOS元数据后，18个旧overlay与419个required清单组成437项完整运行基线，校验通过。没有删除或改写旧包，也没有读取服务器或实际配置；419项配套依赖须由人工部署前核对现场。

本次18个覆盖文件为Console DLL/PDB、静态端点清单，以及app.css、app.js、index.html、operations.html、operations.js及各自SDK生成的br/gz变体。另419个文件沿用基线；437个完整发布运行文件与overlay/required联合集合逐文件一致，无删除、运行时升级或新增配置文件。17项原生/入口PE均为x64，Console/Core/Contracts DLL与PDB配对通过。

整包传到Console安装目录外的运维目录，仅overlay全部运行文件按相对路径覆盖Console。既有应用连接有效时默认启用持久化，无新增必填配置；可选开关和周期只提供说明中的非敏感局部合并示例。包附平台字段/接口契约和6份许可证，不含现场appsettings、NLog配置、证书私钥、用户或Agent Token、配置保护工具、包校验脚本或macOS元数据。

特别核对了降级限制：新版账本新增操作人和持久化回执字段，而原Protocol.Json使用严格UnmappedMemberHandling.Disallow；新版写入账本后旧Console不能直接读取。部署说明只允许账本完全未写入且与升级前备份精确一致时成套回退程序；否则保留最新账本/云记录并采用支持该格式的修复版本，不删除或回退账本绕过去重保护。

说明、平台契约、许可证和元数据完成后冻结文件，最后重新生成SHA256；Skill验证器和标准库独立读回分别核对精确文件集合、逐文件哈希、overlay/required互斥与完整发布集合。最终无缺失、多余、哈希不符、大小写冲突、符号链接或包脚本。暂存发布、构建证据和输入快照在验收后清理，验证证据保留在包内BUILD-VERIFICATION.json。

最终30项SHA覆盖31个包文件（另含SHA清单自身），总计1,101,072字节；缺失、多余和哈希不符均为0。5份变化静态资源与当前源码逐字节一致，5份gzip和5份Brotli解压后内容也逐字节一致。

复用2026-10-13实现阶段228项定向检查，业务源码/静态输入未改变，不因打包重复测试。没有请求真实平台业务HTTP、写业务记录、停止现场服务或执行部署；未在Windows Server实机、服务身份或真实业务环境完成保存验收。
