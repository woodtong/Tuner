# 内部开发记录（不面向用户）

实现要点、踩坑记录与自测清单。README 只保留用户视角内容。

## 2026-10-24 · 多端适配第 3-4 步（todo 15 ④，Avalonia UI + macOS 骨架，0.1.1-eta 原位替换）

用户拍板"都要做"（Avalonia + macOS）。

- **Tuner.App.Avalonia**（Avalonia 11.2.2，单源码双目标）：`TargetFrameworks=net8.0;net8.0-windows10.0.19041.0` + 条件 ProjectReference（windows→Core.Windows，否则→Core.Linux）+ `DefineConstants;WINDOWS`。五页功能完整版（状态/调音台/分组/规则/设置）+ 双语字典迁移（WPF `Strings.*.xaml` → Avalonia axaml 只改头三行 xmlns：`https://github.com/avaloniaui` + `using:System`）+ Lang.Apply（MergedDictionaries 重载）+ 托盘 NativeMenu（Linux 不支持时 catch 降级）+ MsgBox 自制（Avalonia 无内置）+ StorageProvider 文件对话框。
- **Avalonia 坑**：Slider ValueChanged 事件参数在 `Avalonia.Controls.Primitives`；ComboBox 不可编辑（无 Text）；NativeMenuItem.Click 是 EventHandler；TextBox 即时保存用 TextInput/TextChanged；单文件发布**不内嵌 libSkiaSharp.so**（目录发布）且 `SkiaNativeAssets.Linux` 版本须 ≥ 传递依赖（NU1605）；windows 目标发布 linux RID 报 WinForms 无运行时包——**publish 必须 `-f net8.0`**。
- **验证**：Windows 目标 --smoke ✓（WASAPI 后端）；WSLg 目录发布 + `DISPLAY=:0 XDG_RUNTIME_DIR=/mnt/wslg/runtime-dir` 跑 --smoke ✓（PulseAudio 后端连接/6 秒退出/退出还原链路/配置落 ~/.config/Tuner/）。WPF 版回归 smoke ✓。
- **Tuner.Core.macOS 骨架**：CoreAudio 公开 API 设备级（默认设备/主音量/静音，unsafe 指针传参）；per-app 空实现 + docs/macOS-plan.md 蓝图（AudioDriverKit 虚拟驱动 + XPC 接 IAudioSessionMonitor，工程量 3-6 周驱动 + 1-2 周宿主，Apple 签名依赖）。**仅编译验证，真机复核点已在代码标注。**
- 提交：2f3a82f。解决方案现 7 工程（Core/Core.Windows/Core.Linux/Core.macOS/App/App.Avalonia/Console/PauseProbe/LinuxProbe——实为 9）。

## 2026-10-23/24 · 产品迭代第 6-8 批（todo 13 i18n + todo 14 第三方实测 + todo 15 多端两步，0.1.1-eta 原位替换）

用户指令：13/15 需要做、14 授权真实第三方播放器实测（插耳机不影响物理世界）；版本号不动。

- **todo14 第三方实测**：拉起本机 QQ音乐（`SMTCFeature.dll` 确有 SMTC 集成）跑 `PauseProbe test`——TryPlay=True 后真实出声（峰值 28-31%）、TryPause=True 后声音真停（峰值 4s 归零保持、状态=已暂停）。**"第三方支持有限"的担忧在最大代表性目标上不成立**，B站/网易云按需复测（工具就绪）。
- **todo13 i18n**：`Lang/Strings.{zh-CN,en-US}.xaml` 双语字典（203 键）+ `Lang.Apply`（字典置顶合并，DynamicResource 全局即时刷新）。代码动态串经 `Lang.Get`（params 格式化）。**即时刷新链**：XAML 自动；窗口标题/静音钮/分组规则 VM/托盘菜单/设置页下拉 Items 由 `LanguageChanged` 订阅重建；状态页随 250ms 刷新自然跟上。语言设置 `Settings.Language`（""跟系统）。**验证方法（视觉通道仍不可用）**：键静态审计（XAML 126 + 代码 70 引用键 vs 双字典，无缺失无差异）+ 中英两套 `--screenshot` 布局自检各 9 PASS + `--lang en-US` 实测渲染（下拉 Highest priority wins、Config file）。**坑**：ComboBoxItem 无 Text 属性用 Content；`Tabs_SelectionChanged` 按 Header 字符串匹配页签自图标改版后一直静默失效（顺手改 SelectedIndex 修复）。
- **todo15-1 Core 分层**：Tuner.Core → 纯 net8.0（引擎/配置/曲线/`IAudioSessionMonitor`/`IMediaSessionTracker`）；新工程 Tuner.Core.Windows（WASAPI/SMTC，命名空间保持 Tuner.Core.* 引用方零改动）；引擎构造接口注入（无媒体跟踪器时 NullMediaSessionTracker 兜底）。SoundSession internal setter 经 `InternalsVisibleTo` 开放。sln 挂工程用 GUID 替换脚本（配置段照抄 Core 行 + 嵌套到 src 目录）。
- **todo15-2 Linux 原型**：Tuner.Core.Linux（P/Invoke libpulse.so.0，threaded mainloop 标准模式）+ Tuner.LinuxProbe 探针（linux-x64 自包含单文件发布 → WSL 直接跑，连 `PULSE_SERVER=unix:/mnt/wslg/PulseServer`）。**libpulse 三坑（全靠运行时调试揪出，坑坑致命）**：①结构体含 ByValArray 嵌套数组的 `PtrToStructure` 回调方向段错误——全部改显式 offset 手读；②**libpulse 16.x 结构字段序与记忆不符**：pa_sink_info 是 name/**index**/description/…（index 夹中间），sink_input 的 mute@0x150/proplist@0x158/corked@0x160——用 hex dump 实证（先播放生成会话再 dump 对照 format=3/rate=44100/channels=2 定位 sample_spec）；③订阅常量 MASK_SINK_INPUT=0x4、facility=0x2，写错时**静默失效**（写 success=1、pactl 外部可见 30%，但回读永不更新）。另：写操作发起后立即 unref 会取消执行中的 op（须带 success 回调 WaitForOperation）；paplay 循环播放每次重开流（index 递增），长流验证用 60s 正弦 wav + setsid 脱离 wsl 会话（否则 wsl 命令退出进程被 SIGHUP）。
- **todo15-3 macOS 评估**：CoreAudio 公开 API 无 per-app 会话音量/峰值（只有设备级）；BackgroundMusic 等方案全靠 AudioDriverKit 虚拟设备驱动（C++ 原生 + 签名公证）或私有 API。**判定：macOS 在公开 API + 纯 .NET 范围内不可行，需原生驱动项目单独立项。**
- 提交：10032e0（i18n）→ 851c6b8（Core 分层）→ 8e702e0（Linux 原型）。Windows 侧每步回归 --smoke/--probe ✓。Mimosa 钩子会拦截 git mv/python 直写源码（改 Read→Write→git rm 流程）。

## 2026-10-23 · 产品迭代第 3-5 批（todo 7-12 + 14 探针，0.1.1-eta 三次原位替换，版本号未动）

用户指令：完成 todo 后续任务、版本号不更迭（数字与字母均不动，每批完成即打包原位替换）。

- **todo7 多规则叠加**：`Settings.RuleConflict("priority"|"deepest")` 全局开关（语义决定规则间合成方式，规则级各自选无法互相合成，故放全局）。引擎 `Better` 按语义分支：最深模式取 `TargetVolumePercent` 更低者，并列仍走 `BetterPriority`。生效期记账不变（deepest 下非胜者规则退出 `_engagedNow` 自动销账，事件按轮去重天然正确）。
- **todo8 手动接管**：`Settings.ManualTakeover` + `Tracked.UserTookOver`。`SetUserVolume` 开启时解除本轮干预（Ducked=false、Current/OriginalVolume=用户值 → `EngineControlling` 立即 false）；Tick 主循环 `UserTookOver && rule!=null → continue`（规则命中仍记账），`rule==null → 复位`。**调用顺序关键**：`WriteChannelVolume` 改为先 `SetUserVolume` 再查 `IsVolumeControlled` 决定直写——接管模式下第一次用户调节即解除控制，同一次调用内用户值直写生效；旧顺序（先查后记）会把直写判断留在"受控"上，用户要拖第二下才生效。状态页徽章复用 DuckVisibility 通道显示「手动接管（本轮不压低）」。
- **todo9 导入/导出**：`TryLoadForImport` 三层校验——可读 + **顶层键检查**（`JsonDocument` 确认 settings/groups/rules 任一存在；宽松反序列化会把任意合法 JSON 读成空配置，拿错文件导入会静默清空，这是必防项）+ 旧格式迁移由 Normalize 兜底。导入后全页签重载（`_loadingUi` 包裹 RefreshGroupColors/GroupTab/RuleTab/LoadSettingsTab）。
- **todo10 便携模式**：`ConfigStore.PortableBase/ActiveDir/ActivePath` 单一出口（exe 目录 `portable.marker` 存在即启用）；App 加载、AutoSaveNow、Console `--setvol/--log` 三处从 DefaultPath 切到 ActivePath，`App.LogPath` 同随 ActiveDir。**marker 检测每次读文件系统但只在启动/保存调用，不进引擎热路径**。首次运行不预写 config.json（与 %APPDATA% 行为一致，首次修改才落盘）。
- **todo11 更新检查**：GitHub `releases/latest` 匿名 API（必须带 User-Agent，否则 403）；版本比较只取数字位（`"v0.1.2-eta"` → 0.1.2），**字母工作版与同数字正式版判等不误报**。发布页链接只作文字展示（守零 Process.Start 纪律）。本机实测遇匿名限流 HTTP 403——属预期路径，UI 如实显示"检查失败"。
- **todo12 全局快捷键**：`HotkeyHost`：`WindowInteropHelper.EnsureHandle()`（**未 Show 的窗口也能注册**——托盘驻留必须）；`HwndSource.AddHook` 捕 WM_HOTKEY；任一组合键注册失败则全部注销并留日志。开关保存后 `AutoSaveNow` 里同步 `SetEnabled`（导入配置同样同步）。真实键击验证：托盘驻留中 `WScript.Shell.SendKeys("^%p")` 两次，引擎暂停/恢复事件各恰一次。
- **todo14 探针实测**：`TunerE2E --media`（WinRT `MediaPlayer` 循环播放，自动注册 SMTC）当音源，`PauseProbe test` 实测 TryPause 后**音频峰值连续归零、状态=已暂停、TryPlay 恢复**——SMTC 真启停对集成规范的应用可行；第三方播放器响应度待逐个复测（工具已就绪），是否立项留用户决定。
- **验证方式重要变化**：本轮视觉通道全程故障（子代理 Read 图片走"CDN 上传确认"路径而非视觉呈现，中英文文件名均如此，两个 image-viewer 实例一致）——改用**程序化布局自检**：`--screenshot` 模式内嵌 `SettingsLayoutSelfCheck`（控件可见性/顺序 Y 坐标/选中项/提示行高/窗内无溢出，结果写 settings-check.txt，9 项 PASS）。视觉通道恢复后可回归截图复核。
- 端到端仍用"临时工程跑完即删"模式（%TEMP%\tuner-e2e，引用 Tuner.Core 工程引用）：触发源=本进程 WasapiOut 循环播放，被压目标=**系统提示音会话**（`PlaySound` 系统别名预热创建，"System Sounds" 与测试进程分属两会话——同进程 WASAPI 共享会话无法自避自，这是免外部进程的双会话方案）。批次 A 验证 7/7：优先级 40% → deepest 20% → 接管即时 80% 不压回 → 解除保持 → 再触发回 20% → 关闭接管回 40% → 恢复到用户锚点 90%。
- 提交：e25186c（todo7-8）→ f345537（todo9-10）→ f1fc497（todo11-12），每批后 0.1.1-eta 四件套原位替换（dist 平铺，正式版归档目录未动）。

## 2026-10-22 · UI 响应式返工（0.1.1-eta 原位替换，字母不变）

用户反馈：小窗文字显示不全、全屏 UI 太离散（规则页为主）。子代理读图定位后修复：

- **全屏离散的根因又是 Stretch+显式宽度**：规则编辑面板的值下拉全部用 `Stretch+MaxWidth`，宽窗口时控件被**居中**在星号列里、两侧各空数百 px（"下拉漂在列中间"）。统一改为 **Left 对齐 + MaxWidth**。
- **内容限宽居中**：规则编辑面板/分组详情（MaxWidth 760）、设置页（680）内容整体居中——小窗自然铺满，宽窗口聚拢居中，消除"贴左+右侧 30% 空白"。设置页由左对齐改居中。
- **小窗截断**：提示文字与控件同行的横向 StackPanel 放不下会**硬裁行尾**（优先级提示、留空＝跟随全局）——提示一律独立成行（控件下方 Hint，可换行）；「恢复 ms」同类问题改 WrapPanel 自动换行；设备条件下拉独立一行占满（长设备名不再裁字）。
- **规则页缺 ScrollViewer**（分组/设置页都有）：最小高度 580 下编辑面板下半段整段不可见——编辑卡片外包滚动视图。
- 验证：800×580 与 1600×900 双尺寸截图 + 子代理读图复核（小窗：滚动条出现、优先级提示完整换行、恢复组换行不裁字；宽屏：内容居中、无控件间空洞、设置页对称）。
- 顺手坑：大段 XAML 重排漏一个 `}`（MC1000 报"Unexpected closing brace"位置在别的行）——grep 编译输出别只数条数，`grep -c` 数到错误但截图仍跑的是旧 exe，差点漏过。

## 2026-10-22 · 产品迭代第 2 批（todo 4-6，0.1.1-eta）

- **todo4 通信设备覆盖（最小版）**：引擎并行监控多媒体+通信两台默认设备。`AudioSessionMonitor` 增加 Role 注入（构造参数），`OnDefaultDeviceChanged` 按 Role 过滤重绑；引擎合并双监控快照、按 `GetSessionInstanceIdentifier` 去重（同一物理设备设双默认时不重复）；App 层 `AllSessions` 聚合 + `SetSessionVolume/Mute/HasPending` 按会话所属设备路由（两监控的待写队列互不相通，跨设备写会静默丢失）。调音台 MASTER 仍指多媒体设备。README 已知限制第 1 条已过时，待发版时更新。
- **todo5 规则级闪避时长**：`FadeOutMs/FadeInMs`（int?，留空跟随全局，50–10000 钳制）；Tracked 携带 ActiveFadeOut/In，恢复阶段沿用刚解除规则的覆盖值，锚点保护窗口同用规则级时长；规则页「闪避节奏」行。
- **todo6 设备条件**：`Condition("none"|"device")+DeviceCondition`（子串、忽略大小写）；RulePlan 预解析 DeviceCond，每拍与当前设备名比对（ConditionOk），不满足则不参与裁决；规则页「生效条件」行（枚举设备下拉可手输，默认设备置顶），规则句追加［仅 X］。
- **重要性能坑（本轮新知识）**：`MMDevice.FriendlyName` 是 COM 属性库查询，本机单次数十毫秒——引擎每拍读两次 DeviceName 把 50ms 节拍拖到 ~200ms，渐变速率掉到 1/4（端到端测试以"渐变斜率异常"暴露）。**监控层缓存设备名，只在重绑时读一次**。教训：凡进入引擎每拍路径的 COM 属性一律缓存。
- 可编辑 ComboBox 无 TextChanged 事件，用附加事件 `primitives:TextBoxBase.TextChanged`（需 PresentationFramework 命名空间）。
- 验证：临时端到端测试（已删）——双设备去重（4==4）✓、2500ms 慢渐变轨迹精确线性（0.5s=88%/1.1s=70%/2.6s=30%）✓、设备条件拦截高优先级规则（终值 30% 而非 60%）✓、恢复段同速率 ✓；`--selftest` 音源侧 ✓；`--smoke` ✓；截图子代理复核规则页新行渲染 ✓。
- 打包 0.1.1-eta（字母 zeta→eta），zeta 制品已按 dist 管理规则删除。

## 2026-10-22 · UI 返工续（0.1.1-zeta 第二次原位替换，字母不变）

用户复验反馈三处，全部修复：

- **导航改滑动胶囊（"轮子"）**：参考 cogent-gui `src/renderer/src/components/useSlideHighlight.ts`（滑动高亮轮子 v2：单色块跟随悬停平滑移动，离开列表回落选中项）移植到 WPF——TabControl 模板加 NavRoot/NavIndicator（单胶囊层），TabItem 模板瘦身为透明载体（不再各自画胶囊，多元素各画各的必然是闪现）。代码驱动：悬停滑到悬停项（悬停色）、离开导航/切换选中滑到选中项（选中色），位移/高度 200ms ease-out + 颜色 180ms（与 cogent 同参）；启动落位与窗口缩放走无动画校准。
- **两个 WPF 冻结坑（本轮最重要的新知识）**：①模板里声明的 Freezable（TranslateTransform/内联画刷）随模板密封被**冻结**——Storyboard 触发器动画会自动克隆目标，但**代码里直接 BeginAnimation 会抛"已密封或已冻结"**（0xC000041D 静默崩溃，stderr 无输出）；解法＝代码里换自己 new 的可变实例再驱动。②Grid 子元素默认 VerticalAlignment=Stretch，**设了显式 Height 后按居中排列**，胶囊整体下偏 ~350px——指示器类元素必须显式 VerticalAlignment=Top。定位手段：App 挂 DispatcherUnhandledException 落 app.log（此前 UI 回调异常零堆栈）＋像素级截图扫描（采样琥珀色行区间，验证胶囊落在 1/3/5 项、间距 53px 均匀）。
- **音量页"音量曲线"下拉闪**：列表项悬停从"底色 ColorAnimation"改为与导航同款的分层覆盖层+透明度动画（悬停层叠于选中层之上）；Popup 的 WPF 内置 Fade 动画与投影叠加有掉帧闪烁，改 PopupAnimation=None。
- **分组页"设为默认组"定稿（2026-10-22 用户逻辑）**：默认组有且仅有一个——勾选＝换默认（原默认组让位），**默认组不可取消**，尝试取消弹回勾选并提示"默认组有且仅有一个，不能取消。若需要更改默认组，请直接勾选对应分组"。历程教训：最初版在 Unchecked 里裸回写 IsChecked 致重入失同步（"关掉打不开"）；第二版静默弹回修复了状态但用户仍困惑"为什么勾不掉"——**被拒绝的操作要显式说明规则，不要静默还原**。另连带修复：删分组时只清了旧字段 TriggerGroupId/TargetGroupId，新选择器引用的规则没删。
- Git Bash 直接调 .NET exe 偶发 exit 127 且零输出（原因未明），用 PowerShell Start-Process -Wait -PassThru 拿真实退出码更可靠。

## 2026-10-22 · UI 柔化返工（0.1.1-zeta 原位替换，希腊字母不变）

用户反馈：①很多悬停判定"鼠标还没移动到哪里就触发"；②状态变换生硬突变（要柔和渐变不要一闪一闪）。定位出三类问题并全部修复（Theme.xaml 整体重写 + Volume.cs 滑轨槽）：

- **悬停判定收窄**：开关/复选框的悬停原挂在整行（含文字标签），改 `Trigger SourceName` 指到内部元素（开关槽 Track、勾选框 Box）——鼠标在标签文字上不再触发视觉变换。推子滑轨槽原挂在整条滑条 MouseEnter（30px 宽 × 130px 高，鼠标离推帽很远就触发），保留判定区域但改渐变后不再突兀。
- **状态突变全部柔化**：复选框打勾（透明度+描边+底色三动画）、开关圆钮填色/槽描边、输入框悬停/聚焦描边（独立描边环叠层）、下拉触发器描边环、下拉列表项悬停/选中、列表卡片选中胶囊、页签选中胶囊、按钮按下（底色层+整体缩放 0.97 双动画）、菜单高亮、诊断入口文字透明度。时长约定：进入 0.12~0.18s、退出 0.15~0.2s、按下 0.05~0.08s。
- **真 bug 修复——页签选中胶囊会被悬停退出动画淡没**：旧版悬停与选中都直写同一 Border 的 Background 色，悬停退出动画（To=Transparent）以动画优先级覆盖选中 Setter，选中页签胶囊直接消失。**教训沉淀为模板约定：多状态控件一律"分层覆盖层 + 各自透明度动画"（覆盖层画刷模板内联声明，不共享、不碰冻结资源），严禁多触发器直写/动画同一属性**。该约定已写进 Theme.xaml 头部注释。
- 按钮模板重构为四层（底板[TemplateBinding 尊重实例覆写]/悬停层/按压层/内容层），按压缩放从样式级 RenderTransform 换字（Setter 里 Freezable 冻结不可动画）改为模板内 ScaleTransform 动画。
- 验证：`--probe` 通过（推子交互无回归）；`--screenshot` 五页静态检查（音量/规则/分组页选中态、开关、复选框、卡片渲染正常，视觉模型复核）；希腊字母不变，dist 同版本原位替换。

## 2026-10-22 · 产品迭代第 1 批（docs/todo.md 第 1-3 条，MVP 后首批）

范围：①开机自启 ②闪避全局暂停 ③诊断自查渠道。第 4 条（通信设备）与 P1/P2 本批不动；配置 schema 零变更（曾预写 P1 字段后按"分批交付"要求回退）。

- **开机自启**：`AutoStart`（HKCU Run 键，命令为 `"<exe>" --minimized`，免管理员）。只认指向**当前 exe** 的条目为"已启用"——换解压目录后旧条目视为未启用，勾选即重写。`--minimized` 静默启动：不 `Show()` 主窗口、不弹气泡（冒烟等开发模式不受影响）。
- **全局暂停**：引擎 `SetSuspended(bool)`。暂停＝对全部 `EngineControlling` 会话立即 `SetVolume(OriginalVolume)` 并清 Ducked/TargetVolume；恢复＝各会话 `CurrentVolume = ReadVolume()`（丢弃暂停前基准，以真实音量重起算）。暂停期间 `Tick` 在出声判定后**提前返回**——成员/出声状态仍维护（界面照常显示）但不评估规则、不写音量；新会话"创建即初始化"因 `_sounding` 为空自然不触发。三入口同步（`SuspendedChanged` 事件驱动）：托盘菜单勾选项+悬停文字、设置页「启用闪避」开关（运行态不落盘）、顶栏琥珀「闪避已暂停」徽章（仅暂停时占位）。
- **诊断自查（定位：主体是程序与维护者，未触发不占界面）**：
  - 事件源 `Tuner.Core/Diag.cs`：600 条内存环形缓冲 + `Changed` 事件；`App.Log` 同步录入（分类按 `[引擎]`/`[监控]` 前缀归并），app.log 文件照旧全量。
  - 引擎事件：**规则生效/解除按"生效期"去重**（`_ruleEpisode` 台账，TickCount64 作轮次号；本拍命中集与台账对账，新进入发"生效"、全部脱离发"解除"，每轮各恰一次）；输出设备切换（`DeviceName` 变化即报，首个名称不报）；"配置问题"预警（`EnsurePlan` 重建时对启用规则的坏引用——目标/触发分组不存在、应用名为空——一次性报出，带规则可读标签）。
  - 界面：规则列表行内 ⚠ 提醒（应用名归一化后扫描不到任何会话＝未运行或异名；分组 Id 不在配置＝已删除；切回规则页时 `RefreshRuleTab` 重算）；设置页底部弱化「诊断 · 事件日志 ▸」+ 托盘菜单「事件日志」两处入口 → 只读面板（复制/清空）。**不引入 Process.Start**（看日志路径用文本展示，遵守仓库零外部命令纪律）。
- **验证**：`--selftest`（QQMusic 未播仅音源侧通过）＋ 临时端到端工程（已删）：压至 30% ✓ 暂停即时还原到锚点 75% ✓ 触发持续时恢复→重新压低 ✓ 停止后宽限解除 ✓ 五类事件各恰一次无刷屏 ✓ 坏引用预警 ✓；`--smoke` ✓；`--screenshot` 检查设置页（运行/启动/诊断入口渲染正常）与规则页（真实配置上"哔哩哔哩"未运行的 ⚠ 如实出现）。
- 本批踩坑：忘 `monitor.Start()` 时轮询线程不跑、设备名为空、音量读 -1（临时测试里犯的，主程序无此问题）；TextBox 只读属性是 `IsReadOnly`（XAML `ReadOnly` 直接 MC3072）；WPF+WinForms 混用 `MessageBox`/`Clipboard` 歧义再犯（using 别名指向 System.Windows）；`Diag.Changed` 可能在后台线程触发，处理器里不能读 WPF 依赖属性（用普通 bool 字段 `_diagPanelOpen` 判断再 Dispatcher 回 UI）。

## 自测清单（`--selftest` 全链路，内嵌时间线，全程自动）

1. **创建即初始化**：自测音源（进程内循环播放，会话归属 Tuner.Console）会话创建瞬间即被压到 50%（不等峰值检测，短促声音对策）✓
2. **平滑闪避**：正在播放的 QQMusic 61% → 20%（渐变全程约 1 秒，符合 1200ms 全量程速率）✓
3. **迟滞+保持**：wav 循环间隙峰值跌至 0%，音量不抽搐（低电平保持盖过间隙）✓
4. **歌曲间隙保持**：响 4s → 暂停 6s（音频流保持打开）→ 再响 4s：暂停期间闪避全程保持、无恢复事件（旧逻辑 0.25s 即恢复）✓
5. **触发停止恢复**：真正停止（流关闭）后约 1.5s 平滑恢复到原始音量（锚点不漂移）✓
6. **退出还原**：应用退出时自测会话 50% → 100% ✓

WPF 应用冒烟（`--smoke`）：启动绑定设备、枚举会话、优雅退出还原链路正常、无 COM 异常 ✓

另有开发模式参数：`--screenshot`（自动遍历页签出 PNG，`--shot-dir`/`--shot-size` 可选）、`--probe`（进程内合成滚轮/拖动，验证推子显示与刷新回写的博弈）、`--smoke`（6 秒自动退出）。

## 实现要点（踩坑记录）

- **NAudio 2.2.1 会话 API 命名**：`AudioSessionManager.Sessions`（非 AllSessions）、事件 `OnSessionCreated`、会话类型 `AudioSessionControl`、峰值 `AudioMeterInformation.MasterPeakValue`、状态枚举 `NAudio.CoreAudioApi.Interfaces.AudioSessionState`、设备通知为自实现 `IMMNotificationClient`（`OnDefaultDeviceChanged(DataFlow, Role, string)`）。
- **COM 套间**：WPF 主线程 STA，轮询线程 MTA——COM 对象必须在轮询线程上创建与释放，否则跨套间 QI 失败（E_NOINTERFACE），表现为会话数恒为 0。
- **枚举器滞后竞态**：会话创建/销毁后 `IAudioSessionEnumerator` 可能返回旧数据，`OnSessionCreated` 到达后仅做标记，由轮询线程 `RefreshSessions()` 后重读。
- **会话身份**：以 `GetSessionInstanceIdentifier` 为唯一键维护跟踪表，跨枚举保留同一跟踪对象。
- **原始音量锚点保护**：刚结束闪避、渐变未完成时保留原锚点，避免把渐变中间值误存为"原始音量"（实测会 61%→40%→30% 漂移）。
- **规则引擎**：组内任一会话"出声"即整组出声；同一目标组多条规则取优先级最高；引擎每 50ms 节拍向目标线性渐变；无差异时不写 COM（空闲零开销）。
- **三层出声判定**：触发组"在播"判定依次为——① 峰值出声（即时）；② SMTC 系统媒体会话 PlaybackStatus=="Playing"（应用自报，覆盖歌间静音/极弱段落；报告"已暂停/已停止"则**立即**解除，无宽限等待）；③ 静音宽限（仅对没有任何 SMTC 信号的应用兜底）。AUMID↔进程名用"互相包含"启发式（进程名中英文异名先经 ProcessNameAlias 折叠为规范键再比较）。会话峰值表测量应用音量**之前**的信号，被闪避的应用依然能被正确检测为出声中。
- **静音宽限**：仅对未接入 SMTC 的应用生效——音频流仍打开用长宽限（默认 8000ms），流已关闭用短宽限（默认 1500ms）。
- **设备切换**：`IMMNotificationClient.OnDefaultDeviceChanged` → 轮询线程整体重绑设备并重建会话表；连续轮询异常也会触发重绑（设备拔出）。
- **进程映射**：PID→`ProcessName` 直接匹配（归一化大小写、去 .exe）；进程已退出的会话保留显示并周期性重解析（PID 复用）。中英文异名经 `Tuner.Core/Config/ProcessNameAlias.cs` 折叠为规范键（bilibili↔哔哩哔哩、cloudmusic↔网易云音乐、qqmusic↔qq音乐、msedge↔microsoftedge），在 `ConfigStore.NormalizeProcessName` 单一入口生效——分组匹配、应用规则匹配、SMTC 匹配三处共用，新增异名应用只改这一处；界面显示与配置保存保留原始名（2026-10-19 教训：B 站客户端进程名是中文"哔哩哔哩"，配置写英文名 bilibili 会静默永不匹配）。规则页应用下拉为纯扫描制（只列当前真实会话，配置手输名不混入），失配有即时提示；分组页保留自由输入框。
- **无外部命令执行**：自测音源为进程内 NAudio 播放（LoopStream 循环包装），仓库中不存在任何 `Process.Start`/命令行拼接。

## 架构备忘

- 主窗口代码按职责拆分：`MainWindow.xaml.cs`（装配/刷新调度）+ `MainWindow.{Status,Volume,Groups,Rules,Settings,DevHarness}.cs`。
- 推子交互层为"单一数据源"架构：`VolumeDisplay`(DP) 是推子显示唯一来源，所有移动经 `ApplyFader`（动画可打断，AnimSeq 作废过期 Completed）；拖动走 Track 原生 + 每帧镜像。改动此处务必跑 `--probe`。
- 配置持久化：原子写入（tmp + `File.Replace` + `.bak`）；加载端主文件损坏自动回退 `.bak` 再回退默认。
- 版本号单一来源：`src/Directory.Build.props`。
- **dist 制品管理（2026-10-22 用户定型）**：按版本号归档。正式版永久收在 `dist/Tuner-v<数字版本>/` 内（App 与 Console 各"目录 + zip"四件套，保留 `-win-x64` 命名，目录内＝exe + README.md，如 `dist/Tuner-v0.1.1/`）；当前字母工作版四件套平铺 dist 根、只留最新字母批次（推进即删旧字母、同版本重打包原位替换）；新正式版（如 v0.1.2）照抄该结构新建归档目录，旧正式版目录不动。dist/ 不入库（git 忽略）。
