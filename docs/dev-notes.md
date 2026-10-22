# 内部开发记录（不面向用户）

实现要点、踩坑记录与自测清单。README 只保留用户视角内容。

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
