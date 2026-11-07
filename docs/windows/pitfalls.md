# Windows 端踩坑与排错

按主题归类，详细时间线见 [../dev-notes.md](../dev-notes.md)。

## 音频会话层（NAudio 2.2.1）

- 命名要对：`AudioSessionManager.Sessions`（不是 `AllSessions`）、事件 `OnSessionCreated`、会话类型 `AudioSessionControl`、峰值 `AudioMeterInformation.MasterPeakValue`、状态枚举 `NAudio.CoreAudioApi.Interfaces.AudioSessionState`；设备通知自实现 `IMMNotificationClient`。
- **COM 套间**：WPF 主线程是 STA，轮询线程是 MTA——COM 对象必须在轮询线程上创建和释放，跨套间 QI 会失败（`E_NOINTERFACE`），表现为会话数恒为 0。
- **枚举器滞后竞态**：会话创建/销毁后 `IAudioSessionEnumerator` 可能返回旧数据；`OnSessionCreated` 只做标记，由轮询线程 `RefreshSessions()` 后重读。
- **会话身份**：以 `GetSessionInstanceIdentifier` 为唯一键维护跟踪表，跨枚举保留同一跟踪对象。
- **原始音量锚点**：刚结束闪避、渐变未完成时保留原锚点，否则会把渐变中间值误存为"原始音量"（实测 61%→40%→30% 漂移）。
- **设备切换**：`IMMNotificationClient.OnDefaultDeviceChanged` → 轮询线程整体重绑设备并重建会话表；连续轮询异常也会触发重绑（设备拔出）。

## 出声判定（三层，顺序不可换）

1. 峰值出声（即时）；
2. SMTC `PlaybackStatus == "Playing"`（应用自报，覆盖歌间静音/极弱段落；报告已暂停/已停止则**立即**解除，无宽限）；
3. 静音宽限（只对没有任何 SMTC 信号的应用兜底；流仍打开用长宽限 8000ms，流已关闭用短宽限 1500ms）。

AUMID↔进程名用"互相包含"启发式；中英文异名先经 `ProcessNameAlias` 折叠（bilibili↔哔哩哔哩、cloudmusic↔网易云音乐、qqmusic↔qq音乐、msedge↔microsoftedge），在 `ConfigStore.NormalizeProcessName` 单一入口生效，分组/应用规则/SMTC 三处共用——新增异名只改这一处。界面显示与配置保存保留原始名。

## 退出与驻留

- 现象：点托盘退出无反应、进程变僵尸。根因链：息屏触发的音频设备电源切换 → 引擎线程持 `_gate` 卡在 COM 音量调用 → `Dispose` 的 `Join(2000)` 超时后永久等待锁 → UI 线程冻结。
- 修复：引擎停止限时取锁 + **双端 8 秒退出看门狗**（`Task.Delay` → `Environment.Exit(0)`）。任何一环再被音频服务挂起，最多 8 秒硬退。
- 关窗 = 最小化到托盘；托盘右键退出时还原全部被改过的音量。

## 动效与主题

- 动效开关关掉再打开不恢复：根因是主题字典（`Theme.xaml` / `Theme.NoAnim.xaml`）热切换时 Storyboard 处于冻结/使用状态，直接换字典抛异常。现在的做法是"字典热切换 + 导航胶囊取件加固"，改这里必须跑 `--animtest`。
- `--smoke` 只验进程存活，**不验画面**；界面改动要用 `--screenshot` 出图或 `--animtest`/`--probe` 验证。
- 设置页「检查更新」只比较版本号数字位（`MainWindow.Settings.cs`），dev 提交不改 `Directory.Build.props` 的版本号，所以会显示"已是最新"，不能据此判断代码是否最新。

## 崩溃排查

```powershell
# 应用错误事件（含 faulting module 与异常代码）
Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000} -MaxEvents 20 |
  Where-Object { $_.Message -like '*Tuner.exe*' }

# 崩溃转储（WER 自动生成）
Get-ChildItem "$env:LOCALAPPDATA\CrashDumps" -Filter 'Tuner*.dmp'

# 运行日志
Get-Content "$env:APPDATA\Tuner\app.log" -Tail 50
```

- 托管异常：`AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` 会写 `app.log`（含完整堆栈）。
- 原生崩溃（如 `coreclr.dll` 的 `0xc0000005`）不会走托管异常路径，`app.log` 可能停在崩溃前最后一行——此时看事件查看器 + dump。
- 纪律：仓库**零外部命令执行**（不存在 `Process.Start`/命令行拼接），日志路径只以文字展示，不拉起进程。

## 调试小技巧

- 全局快捷键真实键击验证：托盘驻留中用 `WScript.Shell.SendKeys("^%p")`，可在日志里看到暂停/恢复各一次。
- 自动保存是 800ms 防抖 + 原子写入；改配置文件后可直接重启验证加载与 `.bak` 回退。
