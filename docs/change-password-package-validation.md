# 修改密码增量包验证记录

2026-10-13。交付[未压缩增量包](../outputs/MKY服务器远程管理工具-修改密码-20261013-win-x64/README.md)，更新见[人工部署说明](../outputs/MKY服务器远程管理工具-修改密码-20261013-win-x64/部署说明.md)。只更新Console，不更新Agent或安装器，没有新增配置或SQL。

项目既有publisher使用SDK10.0.401，完成 Windows Server / win-x64 / net10.0 / Release / SelfContained=true SDK publish，零警告、零错误。恢复目标已匹配、输入未变，使用no-restore。内置Microsoft.NETCore.App及Microsoft.AspNetCore.App10.0.12。11项主源码和32项源静态资源在发布和组包后未漂移，437个发布运行文件逐文件与最终运行集合一致，17项原生/入口PE为x64，Console/Core/Contracts DLL/PDB配对通过。

原HTTP兼容包不能直接作为输入：原校验清单外新增 `config/appsettings.Local.json`、`config/appsettings.json`，且 `overlay/wwwroot/login.html` 哈希不符。没有读取这两份现场配置内容、修改旧包或改写旧SHA。更早的Base64包已不在工作区。本轮只按原SHA列入的文件建立专用临时副本，登录页从当前SDK发布中取回与原SHA和运行清单完全一致的字节；副本全部62项校验、50个原overlay及387个原配套文件清单通过后作为基线。恢复证据随包保存，不能据此声称现场服务器匹配。

差异为18个文件：Console DLL/PDB、静态端点清单，以及app.css、auth-login.js、auth-session.js、index.html、operations.html各自原文件和SDK生成的br/gz。另419个文件由基线提供，完整集合437个，无运行文件删除或运行时升级。现场自定义或不同版本文件需由人工先核对，不继续混装。

说明、恢复证据和6份许可证完成后冻结包，最后生成SHA。Skill验证器和标准库独立读回均通过：30项校验覆盖31个包文件（另含SHA本身），共859,162字节；缺失、多余、哈希不符均为0，无符号链接、大小写冲突、macOS元数据或执行脚本，没有现场appsettings、秘密、证书私钥或配置保护工具。暂存发布与基线副本在验收后清理。

沿用2026-10-12的28项认证、9项前端和浏览器弹窗验收，打包未修改业务输入，未重复运行未受影响的测试。未访问真实平台、修改真实密码、启停现场服务或部署，未进行Windows实机、真实账号及SQL Server验收。
