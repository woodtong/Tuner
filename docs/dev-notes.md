# 内部开发记录（不面向用户）

实现要点、踩坑记录与自测清单。README 只保留用户视角内容。

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
- **三层出声判定**：触发组"在播"判定依次为——① 峰值出声（即时）；② SMTC 系统媒体会话 PlaybackStatus=="Playing"（应用自报，覆盖歌间静音/极弱段落；报告"已暂停/已停止"则**立即**解除，无宽限等待）；③ 静音宽限（仅对没有任何 SMTC 信号的应用兜底）。AUMID↔进程名用"互相包含 + 别名表"启发式（哔哩哔哩↔bilibili、cloudmusic、msedge 等），可在 `Tuner.Core/Media/MediaSessionTracker.cs` 扩展。会话峰值表测量应用音量**之前**的信号，被闪避的应用依然能被正确检测为出声中。
- **静音宽限**：仅对未接入 SMTC 的应用生效——音频流仍打开用长宽限（默认 8000ms），流已关闭用短宽限（默认 1500ms）。
- **设备切换**：`IMMNotificationClient.OnDefaultDeviceChanged` → 轮询线程整体重绑设备并重建会话表；连续轮询异常也会触发重绑（设备拔出）。
- **进程映射**：PID→`ProcessName` 直接匹配（归一化大小写、去 .exe）；进程已退出的会话保留显示并周期性重解析（PID 复用）。
- **无外部命令执行**：自测音源为进程内 NAudio 播放（LoopStream 循环包装），仓库中不存在任何 `Process.Start`/命令行拼接。

## 架构备忘

- 主窗口代码按职责拆分：`MainWindow.xaml.cs`（装配/刷新调度）+ `MainWindow.{Status,Volume,Groups,Rules,Settings,DevHarness}.cs`。
- 推子交互层为"单一数据源"架构：`VolumeDisplay`(DP) 是推子显示唯一来源，所有移动经 `ApplyFader`（动画可打断，AnimSeq 作废过期 Completed）；拖动走 Track 原生 + 每帧镜像。改动此处务必跑 `--probe`。
- 配置持久化：原子写入（tmp + `File.Replace` + `.bak`）；加载端主文件损坏自动回退 `.bak` 再回退默认。
- 版本号单一来源：`src/Directory.Build.props`。
