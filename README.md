# Tuner — Windows 应用分组音频闪避工具

当某个分组的音频应用出声时，自动把其他分组应用的音量平滑降低（闪避 / ducking）；对方停止发声后平滑恢复。
纯用户态实现（WASAPI，经 NAudio 访问），免驱动、免管理员权限。

## 实施进度

- [x] **步骤 1 控制台原型**：枚举全部音频会话，实时打印进程名与峰值电平
- [x] **步骤 2 闪避核心**：阈值迟滞判定 + 规则引擎 + 平滑渐变 + 原音量保护/退出还原（含自动化验收 `--selftest`）
- [x] **步骤 3 应用化**：WPF 托盘常驻 + 配置界面（分组/规则/设置/实时状态）
- [x] **步骤 4 打磨**（主要项）：JSON 持久化、会话竞态处理、默认设备切换跟随、退出还原、单实例
- [ ] 步骤 4 剩余：长时间运行稳定性观察、子进程归属启发式（如有需要）

## 版本

版本策略：数字版本保持 **0.1**，预发布标签按希腊字母序列推进（alpha → beta → gamma …），仅在用户明确指示时递增数字版本。

| 版本 | 内容 |
|---|---|
| v0.1-alpha (2026-10-06) | 控制台原型：会话识别 + 峰值检测 |
| v0.1-beta (2026-10-07) | 完整闪避引擎 + WPF 托盘应用 |
| **v0.1-epsilon (2026-10-07)** | UI 全面重构 + 静音宽限 + **三层出声判定**：SMTC 系统媒体播放状态为权威信号（应用自报"播放中"→保持，"暂停/停止"→立即恢复，无长等待），峰值迟滞次之，宽限仅对未接入 SMTC 的应用兜底；真启停可行性验证（PauseProbe） |

## 目录结构

```
Tuner.sln
config/test-ducking.json     自动化验收用测试配置
src/
  Tuner.Core/                核心库（UI 无关）
    Audio/AudioSessionMonitor.cs   会话枚举/事件感知/峰值轮询/设备切换重绑/音量控制句柄
    Audio/SoundSession.cs          会话快照模型
    Audio/ISessionVolumeControl.cs 引擎写音量的接口
    Config/TunerConfig.cs          分组/规则/引擎设置模型
    Config/ConfigStore.cs          JSON 读写（%APPDATA%\Tuner\config.json）
    Ducking/DuckingEngine.cs       闪避引擎（迟滞判定/规则优先级/渐变/还原）
  Tuner.Console/             控制台诊断工具（实时表格/日志/自测/setvol）
  Tuner.App/                 WPF 托盘应用（程序集名 Tuner.exe）
```

## 运行

环境：Windows 10/11。开发需 .NET 8 SDK；发布的 zip 自带运行时，解压即用。

```bash
dotnet build -c Release

# 托盘应用（主程序）
dotnet run --project src/Tuner.App -c Release
#   启动后在托盘右键：打开主界面 / 退出（还原音量）；关闭窗口 = 最小化到托盘

# 控制台诊断（实时表格）
dotnet run --project src/Tuner.Console -c Release
# 日志模式 / 自定义轮询
dotnet run --project src/Tuner.Console -c Release -- --log --interval 50

# 闪避全链路自动化验收（需要 QQMusic 正在播放，会短暂压低音乐音量）
dotnet run --project src/Tuner.Console -c Release -- --selftest

# 调试：直接设置某进程所有会话的音量
dotnet run --project src/Tuner.Console -c Release -- --setvol qqmusic 61
```

配置文件：`%APPDATA%\Tuner\config.json`（界面"应用并保存"后写入；应用启动时自动加载）。

### 使用步骤

1. 托盘双击/右键打开主界面 →「分组」页：新建分组，把进程名加进去（可从"运行中的应用"直接选，即预注册）；
   默认组承接所有未匹配应用；填 `system sounds` 可匹配系统提示音。
2. 「规则」页：新增规则——当【触发组】出声 → 【目标组】音量渐变到 N%，可配优先级。
3. 「设置」页：进入/退出阈值（默认 5%/2%）、低电平保持时长（防断续声音抽搐）、渐变时长（默认 500ms）。
4. 「应用并保存」立即生效并持久化；托盘右键退出时还原全部被修改的音量。

## 步骤 2/3 验证记录（2026-10-06~07）

`--selftest` 全链路（内嵌时间线，全程自动）：

1. **创建即初始化**：自测音源（进程内循环播放，会话归属 Tuner.Console）会话创建瞬间即被压到 50%（不等峰值检测，短促声音对策）✓
2. **平滑闪避**：正在播放的 QQMusic 61% → 20%（渐变全程约 1 秒，符合 1200ms 全量程速率）✓
3. **迟滞+保持**：wav 循环间隙峰值跌至 0%，音量不抽搐（低电平保持盖过间隙）✓
4. **歌曲间隙保持（v0.1-delta 新增）**：响 4s → **暂停 6s（音频流保持打开）** → 再响 4s：暂停期间闪避全程保持、无恢复事件（旧逻辑 0.25s 即恢复）✓
5. **触发停止恢复**：真正停止（流关闭）后约 1.5s 平滑恢复到原始音量（锚点不漂移）✓
6. **退出还原**：应用退出时自测会话 50% → 100% ✓

WPF 应用冒烟：启动绑定设备、枚举 6 个会话、优雅退出还原链路正常、无 COM 异常 ✓

## 实现要点（踩坑记录）

- **NAudio 2.2.1 会话 API 命名**：`AudioSessionManager.Sessions`（非 AllSessions）、事件 `OnSessionCreated`、会话类型 `AudioSessionControl`、峰值 `AudioMeterInformation.MasterPeakValue`、状态枚举 `NAudio.CoreAudioApi.Interfaces.AudioSessionState`、设备通知为自实现 `IMMNotificationClient`（`OnDefaultDeviceChanged(DataFlow, Role, string)`）。
- **COM 套间**：WPF 主线程 STA，轮询线程 MTA——COM 对象必须在轮询线程上创建与释放，否则跨套间 QI 失败（E_NOINTERFACE），表现为会话数恒为 0。
- **枚举器滞后竞态**：会话创建/销毁后 `IAudioSessionEnumerator` 可能返回旧数据，`OnSessionCreated` 到达后仅做标记，由轮询线程 `RefreshSessions()` 后重读。
- **会话身份**：以 `GetSessionInstanceIdentifier` 为唯一键维护跟踪表，跨枚举保留同一跟踪对象。
- **原始音量锚点保护**：刚结束闪避、渐变未完成时保留原锚点，避免把渐变中间值误存为"原始音量"（实测会 61%→40%→30% 漂移）。
- **规则引擎**：组内任一会话"出声"即整组出声；同一目标组多条规则取优先级最高；引擎每 50ms 节拍向目标线性渐变；无差异时不写 COM（空闲零开销）。
- **三层出声判定（v0.1-epsilon）**：触发组"在播"判定依次为——① 峰值出声（即时）；② SMTC 系统媒体会话 PlaybackStatus=="Playing"（应用自报，覆盖歌间静音/极弱段落；报告"已暂停/已停止"则**立即**解除，无宽限等待）；③ 静音宽限（仅对没有任何 SMTC 信号的应用兜底）。AUMID↔进程名用"互相包含 + 别名表"启发式（哔哩哔哩↔bilibili、cloudmusic、msedge 等），可在 `Tuner.Core/Media/MediaSessionTracker.cs` 扩展。会话峰值表测量应用音量**之前**的信号，被闪避的应用依然能被正确检测为出声中。
- **静音宽限**：仅对未接入 SMTC 的应用生效——音频流仍打开用长宽限（默认 8000ms），流已关闭用短宽限（默认 1500ms），可在设置页调整。
- **设备切换**：`IMMNotificationClient.OnDefaultDeviceChanged` → 轮询线程整体重绑设备并重建会话表；连续轮询异常也会触发重绑（设备拔出）。
- **进程映射**：PID→`ProcessName` 直接匹配（归一化大小写、去 .exe）；进程已退出的会话保留显示并周期性重解析（PID 复用）。
- **无外部命令执行**：自测音源为进程内 NAudio 播放（LoopStream 循环包装），仓库中不存在任何 `Process.Start`/命令行拼接。

## 已知限制

- 仅监控**默认播放设备（Multimedia 角色）**；"默认通信设备"独立的场景（通话走另一耳机）暂不覆盖。
- 浏览器多标签共享一个会话（系统限制）；独占模式/ASIO 检测不到（不做适配）。
- 从未打开过音频流的应用系统层面不可见——用界面"从运行中的应用添加（预注册）"兜底。
- 闪避期间用户手动调整被闪避应用的音量会被渐变覆盖；恢复完成后用户的调整不受影响。
- 若某应用系统音量本身很低，其峰值可能达不到进入阈值，可调低阈值。

## 打包发布

```bash
dotnet publish src/Tuner.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
  -o dist/Tuner-v0.1-beta-win-x64
```

产物：`Tuner.exe`（约 69MB，自带 .NET 8 桌面运行时，免安装）+ README。版本号在 `Tuner.App.csproj` 的 `<Version>`。
