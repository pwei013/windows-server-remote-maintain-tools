<!-- app-user-auth-login-qa:start -->
# 应用用户登录方案 1 验收（2026-10-11）

final result: passed

- 源图：[最新公司 Logo 版方案 1](docs/design/user-auth-login-20261011/方案1.png)，按最新三张图片的展示顺序解析用户选择。
- 实现：[桌面](docs/ui-evidence/user-auth-login-20261011/login-desktop-final.jpg)、[紧凑桌面](docs/ui-evidence/user-auth-login-20261011/login-compact.jpg)、[390px 窄屏](docs/ui-evidence/user-auth-login-20261011/login-narrow.jpg)；最终代码的本机合成预览 http://127.0.0.1:63617/login.html。
- 状态：默认空登录表单，使用公司原始 Logo；验证用业务及认证全部来自隔离内存夹具，没有连接真实平台或 IIS。

## 证据及密度归一

源图为1487×1058 PNG；实际桌面CSS视口1487×1058，测得DPR2，截图接口返回1487×1058 JPEG，按CSS像素网格比较。完整对照把两张同尺寸图片分别等比绘制为744×529，使用浏览器完整内容截图捕获，没有拉伸、修改原图或用手工图形替代图片。[完整对照](docs/ui-evidence/user-auth-login-20261011/comparison-final.jpg)为1512×844；[表单局部](docs/ui-evidence/user-auth-login-20261011/comparison-form-final.jpg)为1264×844，两边从同一像素位置裁窗620×680，可读文字、图标、输入和主按钮。比较页面只是只读图片载体，不作为产品实现。

附加视口为1280×720、390×844。紧凑桌面页面宽高和scrollWidth/scrollHeight均为1280×720，按钮bottom563.74；窄屏均为390×844，按钮bottom575.70，登录后工作台退出按钮right374、top112，无页面横向溢出。临时视口覆盖已还原。

## Findings 与修正历史

初次实际截图及同输入对照：[初始桌面](docs/ui-evidence/user-auth-login-20261011/login-desktop-initial.jpg)、[全图](docs/ui-evidence/user-auth-login-20261011/comparison-initial.jpg)、[表单](docs/ui-evidence/user-auth-login-20261011/comparison-form-initial.jpg)。

- [P1，已修正] 原始Logo位图带白色外角，放入深色左栏后形成白色方角。直接使用原图，按其圆角轮廓设置24%边角裁切；登录、工作台、历史页统一使用原图。最终全图和实际截图中白色外角消失，没有重绘Logo。
- [P2，已修正] 左栏固定内容使1058高视口多出约7px滚动。减少底部内边距，并为紧凑窗口压缩插图和间距；最终1487×1058、1280×720均没有页面溢出。
- 表单略过松与标题、按钮字重：缩小组间距、加强主标题并调整按钮字重。最终局部对照没有影响布局与使用的差异；系统字体笔画、官方Fluent图标和生成图略有不同，归为P3。

## 五项保真面

- 字体：沿用Segoe UI/PingFang SC/Microsoft YaHei，主标题48px，左欢迎文字60px，标签18px，正文20px；紧凑和窄屏降低字号。两行欢迎语、单行产品名、账户标签及显隐按钮无截断，层级与源图对应；不同系统字形和光栅化为P3。
- 间距：桌面41.55%深蓝品牌区、58.45%白表单区；表单588px，无外层卡片。输入72px、主按钮70px；主要左边缘和主体比例与源图一致。较低或较窄视口按实际可用空间调整，所有主动作可见。
- 颜色：深蓝#1d324f、白色表单、蓝色#3863d5主动作，灰蓝正文与细边框。源图的微弱背景纹理和饱和度差异为P3；反馈、聚焦及禁用状态有清楚区分。
- 图像：公司Logo使用原始385×385 PNG；服务器插图为按已选图片生成的1495×1052透明PNG，三台服务器、四窗格与代码面板完整。没有用CSS/SVG绘画替代插图。标准图标为微软官方Fluent SVG，局部笔画差异为P3。
- 文案：保留欢迎语、产品名、账号/密码、登录管理工作台及联系管理员提示。错误、过期、限流、HTTPS和配置缺失提示属于实际运行状态；没有新增注册、SSO或重置流程。

## 功能及边界

真实浏览器操作：错误密码反馈并清空输入，显隐，成功登录显示用户，查询站点标注，合成新增与修改后独立读回，历史页身份，退出后页面重定向；390px登录、工作台退出入口及退出均通过。证据：[错误](docs/ui-evidence/user-auth-login-20261011/login-error.jpg)、[登录后](docs/ui-evidence/user-auth-login-20261011/workbench-signed-in.jpg)、[保存读回](docs/ui-evidence/user-auth-login-20261011/site-annotation-saved.jpg)、[退出](docs/ui-evidence/user-auth-login-20261011/login-signed-out.jpg)、[窄屏工作台](docs/ui-evidence/user-auth-login-20261011/workbench-narrow.jpg)。浏览器error/warn日志检查为空；错误登录HTTP401属于预期业务拒绝。

认证专项20项、登录前端12项、既有工作台/历史页110项、标注29项通过，Windows x64 Release/net10.0自包含属性构建零警告、零错误。尚未验证真实账号、平台数据库、Windows证书加载或实机业务；说明见[认证接入](docs/app-user-authentication.md)。

## Implementation Checklist

- 已完成原图对应的登录页、公司Logo和插图资产、桌面及窄屏布局。
- 已完成会话隔离、受保护页面及API、登录/本人/退出、逐请求Bearer与401/429处理。
- 已完成独立读回、未知写入结果语义、安全错误、原编号保留和同源/CSRF保护。
- 已保存完整与局部同输入比较、修正后证据、功能截图及专项验证。
- 保留最终代码的本机合成预览PID69354供查看；先前合成会话和标注、临时比较服务及失败启动进程已清理。没有部署、打包或真实业务写入。

Follow-up Polish：操作系统字体笔画、标准图标及生成插图细部存在P3差异，可按后续明确反馈微调。没有未解决的P0/P1/P2问题。

final result: passed
<!-- app-user-auth-login-qa:end -->

---


<!-- site-arrow-alignment-qa:start -->
# 站点展开箭头对齐调整（2026-10-10）

- 结果：已通过。原箭头相对单元格固定 top:17px，而站点信息随行高垂直居中，绑定多或文字换行时产生错位；现改为 top:50% / translateY(-50%)，箭头随行高居中，横向位置及按钮尺寸沿用原样。
- 实际浏览器验证：父行高度 95.59375px 与展开绑定配置后的 142.1875px，箭头中心相对单元格中心偏差均为 0px；点击箭头仍正确展开子表。
- [效果截图](docs/ui-evidence/site-child-tables/implementation-arrow-centered-desktop.png)来自合成只读预览，未连接现场 IIS。原预览继续保留，没有新增服务进程。
- 本轮仅修改 CSS，使用差异和实际布局验证，未为样式改动新增或重复业务测试。本次未重新发布或打包，已交付 v2 仍为此前版本。
<!-- site-arrow-alignment-qa:end -->

---

<!-- site-child-tables-empty-qa:start -->
# 站点空子表与菜单顺序调整验收（2026-10-10）

- 依据：用户追加反馈，站点默认根应用 `/` 不作为额外应用展示，空子表隐藏；左侧站点在应用程序池上方。本次反馈覆盖原方案中的根应用行和空态展示，原验收历史继续保留在下方。
- 结果：已通过。本次仅修改 Console 前端，站点父行继续保留根应用池、物理目录和访问地址；子表仅显示非根应用与附加虚拟目录。根应用下有附加目录时只显示目录子表，没有子项时不显示展开按钮、空面板及零数量摘要。
- 数据范围：显示和数量遵循当前应用池筛选；完整拓扑数据保留根应用，用于关联池、访问地址和操作影响核对。读取失败、旧 Agent 或缺失快照仍显示真实状态与重读入口。
- 浏览器实际核对：文件中心、内部接口为只有根应用的站点，无展开入口或子表；业务门户仅列出 /api、/jobs 与各自附加目录。Web.Root 筛选后只剩父行，重置筛选恢复原展开内容；左侧顺序为站点、应用程序池、Windows 服务。浏览器 error 日志 0。
- 证据：[实际页面截图](docs/ui-evidence/site-child-tables/implementation-empty-children-hidden-desktop.png)，来自现有只读合成预览 [http://127.0.0.1:57008](http://127.0.0.1:57008)，未连接现场 IIS。预览继续保留，未新建服务进程。
- 自动验证：前端自测 73/73，覆盖根应用去重、仅根应用、根应用附加目录、空目录子表、筛选与过期回调、菜单实际顺序和键盘导航；app.js 语法检查通过。Console 的 net10.0 / win-x64 / Release / SelfContained=true 构建通过，0 警告、0 错误。
- 边界：本次没有 publish、重新打包或部署。此前组合更新包保持原文件及清单，尚不包含本次追加调整；Windows/IIS 实机行为仍需现场验收。
<!-- site-child-tables-empty-qa:end -->

---

<!-- site-child-tables-qa:start -->
# 站点双子表方案 2 实施验收（2026-10-10）

- final result: passed
- 范围：在现有 ASP.NET Core Console 中实现用户选择的第 2 个独立效果图，沿用原生 HTML/CSS/JavaScript 和 Microsoft Fluent UI System Icons；未新建框架或替换后端。
- 源图：[selected-design-2.png](docs/ui-evidence/site-child-tables/selected-design-2.png)，确认为此前第 2 个显示的 ImageGen 结果。
- 实现：[桌面截图](docs/ui-evidence/site-child-tables/implementation-final-desktop.jpg)；本地只读预览为 [http://127.0.0.1:57008](http://127.0.0.1:57008)。
- 状态：运维服务器选中，业务门户展开，3 个应用（含根应用）、2 个附加虚拟目录，另两个站点收起。数据为隔离的合成夹具，界面标识 Simulation，未连接现场 IIS。

## 视觉证据与规范化

CSS 视口 1440 × 1024，浏览器 DPR 1。源图 1487 × 1058；最终截图工具返回 1425 × 1013。源图等比缩至最终截图同一像素网格后，在[最终全图对照](docs/ui-evidence/site-child-tables/comparison-final.jpg)、[双子表局部对照](docs/ui-evidence/site-child-tables/comparison-child-tables.jpg)、[父行及操作局部对照](docs/ui-evidence/site-child-tables/comparison-parent-and-actions.jpg)中联合查看，没有仅凭分开的图片、构建或启动判断视觉通过。局部对照使用规范化后的图像裁剪；DOM 行高与坐标以下均为 CSS px。

## 比较历史与修复

1. [初次视口](docs/ui-evidence/site-child-tables/implementation-initial-viewport.jpg)和[首次联合对照](docs/ui-evidence/site-child-tables/comparison-initial.jpg)：P2，重复总览、筛选标签及冗长绑定元数据使父行起点 465px、父行约 126px、子表约 415px；后两个站点从 1006px / 1133px 开始，重要操作超出视口。状态为 blocked。
2. 第一轮修复省去站点页重复总览、保留连接与采集状态，紧凑筛选和摘要，将完整绑定参数放入可展开的“绑定配置”。[修复后视口](docs/ui-evidence/site-child-tables/implementation-revised-viewport.jpg)及[联合对照](docs/ui-evidence/site-child-tables/comparison-revised.jpg)仍显示约 89px 的父行，第三站点从 985px 开始，P2 未完全解决。
3. 增宽名称列，缩减子表上下内边距，保留 14px 数据文字；完整 URL 已标识 HTTP/HTTPS，移除有效地址旁重复协议标签，非网页绑定保留标签；去除绑定详情继承的旧全局 20px 底部内边距，并收紧应用池链接的空白。
4. 最终重新捕获并联合比较：父行约 74px，子表约 397px；第三站点从 937px 开始，全部站点操作按钮下缘为 992px，小于 1024px。表格无覆盖，重要信息和动作均可见。没有剩余 P0/P1/P2。

## 五项视觉检查

| 项目 | 结果 |
|---|---|
| 字体与层次 | 沿用 Segoe UI、PingFang SC、Microsoft YaHei；数据 14px，子表标题 15px，站点名称 16px。根应用标识、继承池列、所属应用及完整 URL 均可读；长值换行。Windows 字体回退未实机核验。 |
| 间距与布局 | 每个站点下同一浅色展开区包含两张分区子表，表头、列和行分隔与方案 2 一致；没有把目录放回独立侧栏。父行和全部站点控制在桌面视口内。 |
| 颜色与状态 | 沿用深蓝顶部、浅色侧栏、蓝色链接、绿色运行、灰色停止、浅红停止按钮及中性边框；展开区域仅一层淡色底。 |
| 资产与图标 | 保留现有品牌和 Fluent 图标，新增同库原始 16px Chevron Right/Down SVG，MIT 许可保留；无手绘 SVG、字符图标或生成占位图。源图无需要新生成的照片/插画。 |
| 文案与数据 | 两表明确区分应用与附加虚拟目录，后者显示所属应用和继承池；根映射不重复列出。所有数量依接口计算，没有复制源图父行“1 个应用”与子表 3 行的示意矛盾。 |

源图未显示的连接/采集状态和完整绑定配置仍保留，以支持运维判断；因此整体内容约比图中下移一行。原有预检查说明保留在表格之后。服务数量按夹具实际返回为 0，未为配图硬编码为 4。这些属于明确的数据和既有功能保留，不是遗漏。

## 交互及响应式验证

- 鼠标展开、收起，以及 Enter 键展开/收起均成功；焦点回到触发按钮，展开状态与受控区域明确，动态表头有 scope 和 caption，页面无重复 ID。
- 刷新保留当前站点展开，并读取新拓扑。按 Api.Pool 筛选得到 2 / 3 个站点；业务门户的子表为 1 个应用和 1 个目录，仅 /api 与其 /assets，原全站摘要仍为 3 / 2。见[筛选截图](docs/ui-evidence/site-child-tables/filter-api-pool.jpg)。
- 子表应用池按钮正确跳到 Api.Pool，池页仅显示 1 / 4 条；返回站点并重置筛选恢复完整内容。
- 展开文件中心后提示“无附加虚拟目录”，不重复根映射；两站点可独立展开。
- “绑定配置”可展开查看 IP/端口、证书和 SNI；父行的停止操作正确打开“停止站点”预检查对话框，作用目标为整个站点，不是某个目录。仅检查入口，未在只读夹具中提交写操作。
- 1280 × 900：页面宽 1265px，表格和容器均为 945px，动作最右缘 1209px，小于页面宽度，无页面横向溢出。见[1280 截图](docs/ui-evidence/site-child-tables/implementation-1280.jpg)。
- 390 × 844：页面宽 375px，表格容器 343px，宽表格 1040px 在自身容器横滚；实际滚至 scrollLeft=399px，可查看后续列，页面没有整体横向溢出。见[窄屏截图](docs/ui-evidence/site-child-tables/implementation-mobile.jpg)和[横滚截图](docs/ui-evidence/site-child-tables/mobile-scroll.jpg)。
- 浏览器 error 日志 0；未执行完整 WCAG 审计或 Windows/IIS 实机验收。

## 自动检查与边界

- 前端自测 70/70：含新增的两表分区、目录归属、同名路径保留、刷新、筛选、服务器切换与过期回调、读取异常及无 HTTP 地址状态；原有站点启停、预检查和操作锁回归通过。
- app.js 语法检查通过。Console 构建目标 Windows Server、win-x64、net10.0、Release、显式框架依赖，0 警告、0 错误。
- 原 site-topology JSON 和 Agent 后端未改动。本次无 publish、部署包或真实部署；尚未核验现场 IIS 数据及 Windows 字体。
- 临时视口已恢复；只读预览保持运行供用户核对，进程 PID 98874，回环端口 57008。没有真实操作账本或业务数据写入。

预览使用现有前端文件，由标准库 HTTP 服务返回合成只读接口；写操作被拒绝，不冒充实际 Console/Agent 执行结果。启动命令：

```sh
/Users/pengwei/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/bin/python3 /Users/pengwei/MKY/IISManagerTool/work/site-child-tables-3xifi92m/ui-preview.py
```

预览依赖的脚本保留在本任务 work 目录；源码快照等一次性验证文件清理。历史验收内容如下完整保留。
<!-- site-child-tables-qa:end -->

---

# 控制台第 3 张设计实施验收

- 日期：2026-10-06（Asia/Shanghai）。
- 工作流：Product Design，沿用现有 ASP.NET Core Console 与原生 HTML/CSS/JavaScript。
- final result: passed
- 上一版验收报告保留于 [previous-design-qa.md](docs/ui-evidence/console-v3/previous-design-qa.md)。

## 视觉依据与对照条件

用户选择本会话中第三个独立生成的效果图，依据为 [selected-design.png](docs/ui-evidence/console-v3/selected-design.png)，原图 1487 × 1058。最终实现为 [workbench-desktop.jpg](docs/ui-evidence/console-v3/workbench-desktop.jpg)，截图工具返回 1425 × 1013；浏览器 CSS 视口 1440 × 1024，DPR 1。将参考图缩放至同一像素网格，在同一输入中联合检查 [最终全图对照](docs/ui-evidence/console-v3/comparison-final.jpg)、[表头及行操作局部对照](docs/ui-evidence/console-v3/comparison-rows.jpg)、[Agent 列表局部对照](docs/ui-evidence/console-v3/comparison-sidebar.jpg)，没有设备或浏览器外框。

对照状态：第一个 Agent 选中，应用程序池页，筛选为全部，展示同名的五个池与八个站点。视觉对照使用独立、只读的回环数据夹具，没有连接真实服务器；数字按夹具快照计算为五个池、四个运行、一个停止。设计图中的总数与可见行状态为示意，不复制其不一致数字。实现额外保留服务器地址、应用池身份、自动启动、关联站点与关联应用两个入口，并明确标识模拟模式，属于现有管理功能的有意保留。

独立操作记录页为用户追加需求，没有单独指定视觉稿，沿用同一顶部菜单、Agent 列表、字体与色彩。在真实 Development / Simulation Console 上读取合成账本验收，见 [记录页桌面](docs/ui-evidence/console-v3/records-desktop.jpg) 与 [记录页窄屏](docs/ui-evidence/console-v3/records-mobile.jpg)。

## 比较历史及修复

1. 初次同屏比较见 [comparison-initial.jpg](docs/ui-evidence/console-v3/comparison-initial.jpg)。P2：应用池行高约 119px，第五行与底部说明超出视口，名称列比参考偏宽。将上下内边距由 18px 缩至 10px、名称列由 26% 缩至 22%，最终行高约 103px。修复后重新采集并联合比较，五行完整可见，说明下缘 1007px，小于视口 1024px。完整配置与关联入口使行高略高于示意稿，属于已说明的产品约束。
2. 记录页实际验收发现 P2：20 条记录使分页需长距离滚动，首次限制表格高度后分页仍在视口外。改为内部滚动、固定表头并调整最大高度。最终分页下缘约 978px，小于视口 1024px，每页选择与上一页、下一页均可见。
3. 1280px 桌面采用紧凑表格间距和 940px 最小宽度。实际表格与容器均为 945px，主操作右缘 1237px，页面宽 1265px，小于视口 1280px，无页面横向溢出。

最终联合比较没有未解决的 P0/P1/P2。修复后的证据是最终全图与局部对照，未凭构建或页面启动判断视觉通过。

## 五项视觉检查

| 项目 | 结果 |
|---|---|
| 字体及层次 | Segoe UI、PingFang SC、Microsoft YaHei；正文 14px、名称 16px、主标题 32px。标题、字段、关联入口、按钮和元数据分层明确；服务器名称及地址可换行。Windows 字体回退未实机核验。 |
| 布局及间距 | 顶部深蓝菜单、浅色常驻 Agent 列表符合所选结构；资源入口在当前 Agent 下缩进。总览、查询区与表格按同一网格对齐，主操作位置稳定。 |
| 颜色及状态 | 既有蓝色主操作、浅蓝选中、绿色运行；停止按钮浅红底、红文字。已停止状态保留项目中性灰，避免与执行失败混淆，属于有意保留。 |
| 图片及图标 | 所选界面无照片或插画；沿用现有 Microsoft Fluent UI System Icons 原始 SVG 与 MIT 许可，没有新画 SVG、字符图标或装饰性占位图。品牌保留项目原生实现。 |
| 文案及内容 | 菜单、字段、状态、核对说明为中文，模拟与真实 IIS 区别可见。数量与操作状态由接口返回，没有硬编码设计图数字。 |

## 行为及响应式验收

- 多 Agent 夹具：搜索 SERVER-B 后可直接选 B，资源入口随当前 Agent 移动；切换清空旧筛选并显示新快照。名称查询 Order 得到 1/5 个应用池。
- 垂直导航：ArrowDown 从池切到站点，同时更新标题、面板、选中态及新建入口。站点查询 OrderApi 得到 2/8 条，详情显示正确根应用与归属，仍支持查看应用池。
- 真实隔离模拟控制台：完成默认模拟池启动的预检查、确认一次提交、原编号核对成功；快照变为 Started，按钮可用状态正确，没有操作 Windows/IIS。
- 记录页：35 条有效合成记录加上述一条模拟结果，共 36 条。每页 10 条，下一页显示第 2/4 页。OrderApiPool + recycle + expired 得到 2 条，加上海 10:00–15:00 时间范围后得到 1 条。
- 详情跳回工作台仍核对原编号 20261006-0000-0000-0000-000000000021，结果 expired，没有重新提交。
- 390 × 844 窄屏，站点及记录页宽 375px，小于视口 390px。Agent 为可滚动按钮列表，筛选纵向排列，宽表格在自身容器滚动。见 [站点窄屏](docs/ui-evidence/console-v3/sites-mobile.jpg)。
- 最终浏览器 error 日志为空。HTML 无重复 ID 或缺失本地资产；site-path、target-name 为现有动态表单字段。检查了焦点、标签、ARIA 选中态、结果播报，未执行完整 WCAG 审计。

## 自动验证及边界

- 前端两组自测 50/50：包含 Agent 列表、操作锁、垂直导航、组合查询、分页、过期响应隔离、错误后重查与原编号详情。
- 后端专项自测 13/13，含真实回环 HTTP：离线 Agent 查询、已移除服务器历史、400 参数边界、503 坏账本、安全摘要与账本字节不变。
- 最终 Console 构建：Windows Server / win-x64 / net10.0 / Release / 框架依赖模式，0 警告、0 错误。没有 publish 或生成部署包，未在 Windows/IIS 实机验收。
- 历史查询逐条校验文件账本，为 O(n) 磁盘读取，期间与持久化写入协调；大量历史时需单独评估性能。列表为控制台最近保存状态，不承诺 Agent 实时状态。

## 本地预览及清理

保留真实模拟预览：[操作记录](http://127.0.0.1:7448/operations.html?page=1&pageSize=10)，顶部菜单可返回工作台。

启动命令（项目根目录）：

```sh
env IISMANAGER_DOTNET=/Users/pengwei/.dotnet/dotnet Console__Port=7448 Agent__StateDirectory=/Users/pengwei/MKY/IISManagerTool/work/ui-redesign/selected-preview/agent Console__TaskDirectory=/Users/pengwei/MKY/IISManagerTool/work/ui-redesign/selected-preview/console bash scripts/run-dev.sh
```

Agent PID 46911 / 127.0.0.1:7444，Console PID 46912 / 127.0.0.1:7448。开发脚本输出仍用默认端口文字，实际覆盖端口已按监听与浏览器核验。

回环 UI 对照服务 7447 已停止，其标签和一次性脚本已清理。后端测试进程与账本已清理。仅保留 work/ui-redesign/selected-preview 中的模拟账本及合成历史供预览，没有读取或修改现场配置，没有连接生产 Agent。用户验收结束后可结束本次开发脚本，正常停止两个模拟进程。

## 后续细节

P3：地址与完整运行配置比效果图占用更多文字，属于保留的管理信息；后续如需更紧凑可再调整。没有阻止本次交付的视觉或行为问题。

## 2026-10-06 操作记录页紧凑调整

按用户标注移除可见大标题、服务器范围文字和查询说明；保留面包屑与刷新入口。全部桌面查询条件和重置、查询按钮排在同一行，记录总数与本页条数放到底部分页栏。窄屏继续纵向排列，避免缩小输入区。

- 1280 × 720：七个输入及按钮的顶部坐标均为 200.5px，查询区实际宽度及滚动宽度均为 943px，无查询栏横向溢出；分页底部为 708.5px。
- 1440 × 900：查询区实际宽度及滚动宽度均为 1103px，分页底部为 888.5px。见 [紧凑桌面效果](docs/ui-evidence/console-v3/records-compact-desktop.jpg)。
- 390 × 844：页面宽 375px，查询区宽度及滚动宽度均为 341px；统计位于分页栏。见 [紧凑窄屏效果](docs/ui-evidence/console-v3/records-compact-mobile.jpg)。
- 浏览器验证组合条件及上海 10:00–15:00 时间范围返回 1 条，服务器切换、重置、下一页和每页 20 条均正常；7 项记录页前端自测通过。本次仅调整静态页面、样式及已移除元素的引用，没有后端改动或生成部署包。

## 2026-10-10 站点别名与多标签方案 1

source visual truth：`docs/design/site-alias-tags-20261010/方案1.png`，用户已选择方案 1。实施目标为已有 Console，保留站点操作、状态、实际 IIS 名称、路径、完整地址、子表及当前品牌。实现 URL：`http://127.0.0.1:55176/`，真实 wwwroot 配合回环合成 API；标注保存仅修改 Python 进程内存，不连接真实平台或 Agent。

源图 1487×1058。桌面 CSS 视口 1487×1058、devicePixelRatio=1；首两轮原始截图 1487×1058。后两轮浏览器导出原始截图为 1472×1047，而 DOM 视口仍为 1487×1058，保留原始文件并将最终截图归一化为 1487×1058 后才作最终同输入比较；不根据密度和滚动条差异评定视觉缺陷。第三轮未归一化的组合图不用于验收，最终图为下面的 normalized 文件。

桌面状态：3 个合成站点、ERP业务门户的编辑面板打开、原名称 Portal.Web、标签生产/财务/对外服务、第一行高亮、别名输入聚焦。源图选中生产/API却同时显示测试行，且多出空“标签”表头；已在设计说明中识别为图像示意问题。比较三行布局使用未筛选状态，功能验收另验证多选标签的真实 OR 语义；标签放在名称下，没有额外空列。预览额外的第二台 Agent 用于隔离测试，当前选择仍为运维服务器。

**比较证据**

- 最终原始浏览器截图：`docs/ui-evidence/site-annotations-20261010/editor-desktop-4.png`；归一化：`editor-desktop-normalized.png`。
- 全视图并排输入：`comparison-full-final.png`。重点区域并排输入：`comparison-table-final.png`、`comparison-editor-final.png`，均位于同一证据目录。查看同一输入中的源图与实现后进行判断。
- 1280×800 中等窗口：`editor-1280.png`；390×844 窄屏：`editor-mobile.png`。窄屏截图包含测试期间添加的 API 标签和验证别名，主要用于判断操作可用性，不作相同内容的桌面像素比较。
- 实际 .NET Console 未配置状态：`actual-console-unconfigured.png`，确认读取成功的 IIS 列表继续显示，标注编辑和标签筛选禁用，停止/重新启动等原入口仍可用。

**比较与修正历史**

1. 首轮 `editor-desktop-1.png` 与 `comparison-full-1.png`、`comparison-table-1.png`、`comparison-editor-1.png`：发现 [P2] 地址列过窄，portal/orders URL 的末尾斜杠单独换行；[P2] 工作区卡片底部与目标的整屏布局相差较大。该轮为 blocked。将地址列比例增到 26%，明确桌面卡片最小高度，并把别名与编辑入口字号调到 16/11px。
2. 第二轮 `editor-desktop-2.png` 与三个 `comparison-*-2.png`：URL 回到单行，卡片底部接近目标；发现 [P2] 字号调整后，ERP别名和编辑入口因首列不足分为两行。该轮仍为 blocked。首列调整为 30%、应用池列 15%，保留地址列 26% 和操作列 20%。
3. 第三轮 `editor-desktop-3.png`：DOM 确认别名与编辑入口同行（top 458.89/459.77px），箭头仍居中。该轮浏览器原始导出尺寸变化，未归一化组合图不能作为最终比较；继续保留原始证据。修正卡片底部冗余 margin，并将分栏阈值改为 1441px，中等窗口使用可关闭的右侧浮层，避免压缩原表格。
4. 第四轮 `editor-desktop-4.png` 经尺寸归一化，与源图放在 full/table/editor 三个最终并排输入中重新比较：地址完整、别名及编辑入口同行，三标签可读，保存/取消固定可见；中等和窄屏面板内容独立滚动。没有未解决的 P0/P1/P2。

**五个必查视觉面**

- 字体：继承既有 Segoe UI / PingFang SC / Microsoft YaHei / system-ui；源图表现为相近的中文无衬线字体，未给出可核实的具体字库。对照字形、笔重、行高和断行：别名 16px/650，原名称 13px，标签 12px/600，面板标题 18px/650、输入 14px；别名层级及原名区分清楚，聚焦轮廓可见。既有 32px 页面标题及更完整管理文字带来少量密度差异，作为 P3 保留。
- 间距：桌面分为 244px Agent 区、弹性工作区、336px 面板；名称下标签、编辑入口、44px 保存按钮与源图结构一致。卡片最低高度和列宽已修正；390px 下 drawer 宽360px、保存 bottom824px，页面 scrollWidth=390，无页面横向溢出。1280px 下使用浮层，关闭后恢复原表格可操作面积。
- 颜色：保留深蓝顶栏、浅色导航与工作区、蓝色主动作、绿色运行状态；蓝/紫/绿/橙/灰蓝标签均有浅底和边界，选中行和禁用状态清楚。与生成图存在轻微饱和度差异，属 P3，沿用现有产品色值。
- 图像与图标：界面使用现有品牌及 Fluent 系统图标。新增 Edit/Dismiss 为 Microsoft 官方原始 16px SVG，许可及来源在 assets/fluent 中保留；边缘清晰，未以生成图整体替代真实界面。此方案没有照片或额外插画需要复现。
- 文案：保留原 IIS 名称语义、独立显示标签、任一标签匹配说明及 Enter 添加；补全 100/20/40 字符数量限制和明确的未配置/失败/读回提示，生成图中的空列和不一致筛选状态按实际语义实现。站点编号、采集状态、刷新/新建入口和原操作说明作为实际管理信息保留。

**功能与可访问性证据**

实际浏览器验证：修改别名，Enter 添加 API，重复 api 去重；保存后独立 GET 确认并回到列表；按 ERP业务、财务搜索各返回 1/3；生产+测试标签匹配 3/3，再加 Stopped 变为 1/3，加 HTTP 变为 0/3；切换同名站点的另一个 Agent 后原别名、标签与草稿消失。Escape 关闭面板并将焦点还给对应编辑按钮。两行高161.64和138.43px的箭头中心偏移为0和-0.0039px。截图中的源图示意按钮与旧空表处理保持已确认语义。

浏览器 error/warn 日志检查为空。前端+记录页自测98/98通过；后端27/27及实际 Console 的同源、未配置、身份、非法/缺省字段校验通过。真实平台仅创建已授权设计元数据，没有新增业务测试记录；生产调用连接和 Windows Server 实机保存尚未验证，见接入说明。

**实施检查清单**

- 已完成别名主显示、原名称次要、名称下标签、任意新标签和多选筛选。
- 已完成右侧编辑、成功独立读回、草稿保留、未知结果仅只读核对与服务器隔离。
- 已修正地址孤行、入口换行、卡片高度和中等窗口布局；原站点目标及空子表处理保留。
- 已保存真实浏览器与源图的同输入比较证据、未配置和响应布局证据，保留历史报告。

P3 后续：生成图字重和颜色无法逐像素复原，已沿用现有产品字体和色值；较长真实别名、目录和地址按既有规则换行。当前没有阻止交付的视觉或功能问题。

final result: passed
