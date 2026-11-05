# Tuner 双端 UI 长程迭代（第 2 轮：文案精简 + 排版逻辑化 + 外观模块）

更新：2026-10-24。用户决策驱动的整轮重做：弃用一切白色主题方案，以 main 已提交的暗色 UI 为基线，做三件事——①文案精简（信息直观）②排版逻辑化重整 ③新增「外观」模块（主流显示风格全量可调）。本文件是工作纲领与验收依据（含预期现象清单），配套验证记录在 docs/ui-validation.md。

## 0. 决策记录（扬弃与基线）

- `feat/ui-redesign-attempt` 分支（侧边导航+现代化设计，ba93dec）经查为白色系（#FAFBFC/#FFFFFF/#EFF6FF），用户判定"太丑、交互不如现有"——**分支已删除**。
- 工作区当时的未提交改动是另一轮"明亮白色工作台"重设计（#FFFFFF/#EDF1F3，即上一版计划书）——同属白色系，**已 stash 备份后还原**（stash@{0}: "backup: 白色主题UI重设计…"，可恢复）。
- 基线 = main 9155529（暗色：窗口底 #0E1218、卡片 #171C24、琥珀强调 #E8A33D）。**保留暗色为默认身份**，浅色仅作为外观模块的一个可选项。
- Avalonia 端现状是浅色 Fluent（RequestedThemeVariant="Light"）——与 WPF 不统一，本轮统一为默认深色。
- 版本纪律：不动版本号、不动 dist、不提交、不发布（除非用户另行指示）。

## 1. 风格调研结论（2026-10-24）

外观设置项清单（对齐主流桌面/工具类应用）：
- **必选项**（Windows 11 / macOS / iOS / Android / Material 均为标准项）：主题模式（深色/浅色/跟随系统）、强调色。
- **主流第二梯队**（Telegram/Discord/Slack/VS Code/Windows 11）：字号档位、界面密度（舒适/紧凑）、圆角风格、动效开关、界面语言。
- 以上 7 项即本模块全集。窗口透明度/亚克力不在本轮（WPF 需 Win32 DWM 互操作，风险大于收益，明确不做并记录）。

文案规范（NN/g 微文案研究 + 设计系统内容规范）：
- 标签 ≤5 词；可见提示 ≤1 句（<20 字为佳）；超过的细节全部移入 Tooltip。
- 信息前置：数值和结论放前面，解释放后面或悬浮。
- 让数据当主角：监控页以数字/状态为主，装饰性说明文字行数趋零。

来源：[Windows 个性化颜色](https://support.microsoft.com/en-us/windows/experience/personalization/personalize-your-colors-in-windows)、[Apple HIG Dark Mode](https://developer.apple.com/design/human-interface-guidelines/dark-mode)、[Material Dark Theme](https://m2.material.io/design/color/dark-theme.html)、[NN/g UX Copy Sizes](https://www.nngroup.com/articles/ux-copy-sizes)、[Microcopy Guide](https://uxdesign.cc/microcopy-an-essential-guide-to-becoming-a-more-literate-designer-2472d02b4eb8)。

## 2. 设计规范（Design Tokens，双端同名同义）

调色板拆分为独立字典（深/浅两套，键名一致），样式与页面一律 DynamicResource 引用——运行时换字典即全界面热切换（与现有语言热切换同机制）。

| Token | Dark（默认，现值保留） | Light（新增） |
|---|---|---|
| PageBg 窗口底 | #0E1218 | #EEF1F5 |
| CardBg 卡片 | #171C24 | #FAFBFD |
| ItemBg 面上抬升 | #1F2530 | #F4F6F9 |
| ItemHover/HoverSoft | #262E3B | #E9EDF3 |
| CardBorder | #242B37 | #DFE4EB |
| FieldBorder(/Hover) | #374050 / #4A5468 | #C3CBD6 / #8FA0B4 |
| TextPrimary/Secondary/Faint | #E8ECF4 / #98A2B3 / #7E889D | #1E2530 / #57626F / #8A94A2 |
| Accent 默认（琥珀） | #E8A33D（hover #F0B258、pressed #B87F1F、soft #332B1C、文字用 #F0B258） | 同基色（文字用 #8A5D10、soft #F3E7CE） |
| 语义色 | Green #34D399、Amber #F5B942、Danger #F87171（保持现值） | 同基色（浅底微调文字变体） |

- 强调色 8 预设：琥珀 #E8A33D（默认）、蓝 #4C8DFF、绿 #34D399、紫 #A78BFA、玫红 #FB7185、青 #22D3EE、橙 #FB923C、灰蓝 #94A3B8。hover/pressed/soft/文字变体由 HSL 数学推导，两主题分别合成。
- 字号档（键 Fz.Micro/Small/Base/Title/Page/Brand/Num）：小 10.5/11.5/12.5/13.5/17/17/13，标准（现值）11/12/13/14/18/18/14，大 11.5/12.5/14/15/19/19/15。
- 密度（键 M.Page/M.Card/M.Row/M.Item）：舒适（现值）与紧凑（约 -25% 纵向）。
- 圆角（键 Rad.Chip/Rad.Control/Rad.Card）：直角 3/5/6，柔和（现值）5/7/9，圆润 7/9/12。
- 动效时长（键 Dur.Hover/Dur.Color/Dur.Slide）：开＝现值 0.15/0.18/0.20s，关＝0（模板动画照跑但时长归零＝瞬时；导航胶囊滑动的代码路径另读全局开关）。
- 自绘控件（LedMeter/CurveChart/导航胶囊/状态保存字）改为从资源取色，杜绝代码内写死主题色（当前 ColorsFromTheme 字面值一并清除）。

## 3. 外观模块规格

- **位置**：第 5 个页签「外观」（状态/音量/分组/规则/**外观**/设置），页序进 validate_ui.py 合同。
- **结构**：顶部「实时预览」卡（主按钮/普通按钮/开关/滑条/下拉/文字层级/LED 样例，全部真实控件，改动即时可见）＋设置行。
- **可调项**（7 项全量）：主题（深色/浅色/跟随系统）、强调色（8 色板，当前色描环）、字号（小/标准/大）、密度（舒适/紧凑）、圆角（直角/柔和/圆润）、动效（开/关）、界面语言（自设置页移入；配置键仍为 settings.language，无迁移成本）。
- **持久化**：TunerConfig 新增 `Appearance`（theme="dark"|"light"|"system"，accent=""|hex，fontScale/density/radius 三选一字符串，animations bool）。ConfigStore.Normalize 归一非法值。改动走既有 800ms 防抖自动保存。
- **生效时机**：启动时在 MainWindow 创建前 Apply（同语言装载位置）；页内改动即时 Apply（DynamicResource 热切换，不重建窗口）。
- **跟随系统**：Windows 读 HKCU Themes\Personalize\AppsUseLightTheme；Avalonia 用 RequestedThemeVariant=Default 交给系统。（系统明暗变化时的实时跟随监听本轮不做，重启/重开窗口生效，记录在案。）
- **双端一致性**：Avalonia 用 ThemeDictionaries（Dark/Light 两字典）+ RequestedThemeVariant；WPF 端动效开关作用于代码驱动动画（导航胶囊滑动/颜色过渡），模板内 0.08–0.15s 悬停微渐变不受控（Storyboard 冻结校验禁止资源引用，详见验证记录事件 1），外观页提示已如实说明。
- **更新（2026-10-24 深夜）**：用户明确要求 Avalonia 端也做滑动胶囊导航＋动效开关，原"Avalonia 端不设项"判定作废。已实现：NavHost 覆盖层 + NavIndicator 胶囊（悬停随指针滑、离开导航/入内容区回落选中项、220ms CubicEaseOut 平移过渡；关＝胶囊隐藏回落 Fluent 静态高亮），外观页增设「动效」开关行（同一 Appearance.Animations 配置）。实测坑：胶囊 Border 设显式 Width 后必须 HorizontalAlignment=Left（默认 Stretch 变居中，整体漂移 ~420px——WPF 版当年同款坑的 Avalonia 复刻）。验证：悬停滑至目标页签（292-366 vs 预期 276-352）✓ 离开回落选中 ✓ 关闭态胶囊像素 0 ✓ Windows/Linux 双端 smoke ✓。
- **开发取证参数**：`--appearance dark|light --accent #HEX --fontscale small|large --density compact --radius sharp`（WPF/Avalonia 同参，截图模式用，不落盘）。

## 4. 排版重整（逐页，双端同思路；控件名/事件合同不变）

- **状态页**：行内信息降为 4 个视觉组——图标、应用名（副行＝分组名）、峰值 LED＋VOL% 等宽数字（Fz.Num）、状态徽章。删去通道号小字与第二根音量 LED（与百分比数字重复，此前已被用户与评审指出信息混淆）。统计条保留 3 项数值。
- **音量页**：保留横向调音台结构（MASTER＋应用通道同高并排、下方曲线卡）；标题与提示精简，长说明移入 Tooltip。
- **分组页**：保留左列表右编辑；编辑区维持"名称/默认 → 成员进程 → 添加 → 删除"动线，文案精简，"? 说明泡"收敛为 3 行。
- **规则页**：编辑器按逻辑分三节（细分隔线，不再嵌套卡片）：关系（触发/目标）→ 效果（目标音量/优先级/节奏）→ 条件（设备/判定方式/启用）＋底部删除。长提示全部转 Tooltip。
- **设置页**：移除语言行（已并入外观）；其余分区（运行/出声判定/音量渐变/性能/启动/配置/关于）保留，每行可见提示 ≤1 行。
- **导航**：保留顶部页签＋滑动胶囊（用户已认可的手感），页签图标不变，外观页图标 E790（调色板）。

## 5. 实施顺序（长程任务分解）

1. Core：AppearanceSettings + TunerConfig.Appearance + Normalize。
2. WPF：Theme/Palette.Dark|Light.xaml 拆分；Theme.xaml 与 MainWindow.xaml 调色板引用转 DynamicResource；新增 Fz/M/Rad/Dur 键；AppearanceManager；App 启动接线；自绘控件取色改造。
3. WPF：外观页（第 5 页签）＋五页排版重整＋双语资源精简改写。
4. Avalonia：App.axaml ThemeDictionaries/深色默认；MainWindow.axaml 同步重整＋外观页；MainWindow.axaml.cs 外观逻辑＋--screenshot 截图模式＋--lang 参数。
5. validate_ui.py 合同更新（页序 + 双端外观键存在性 + 深浅两组对比度对）。
6. 构建双端（Release）→ 截图矩阵取证 → image-viewer 子代理对照预期现象验收 → 修复 → 记录。

## 6. 预期现象（验收清单——评审子代理逐条对照）

截图矩阵（全部真实构建运行截图，存 ui-preview/round2/）：WPF 深色 zh（默认 920×866、最小 800×580）、WPF 浅色 zh、WPF 深色 en-US、WPF 强调色蓝 zh、WPF 字号大＋紧凑 zh、WPF 圆角直角 zh；Avalonia(win) 深色 zh 五页、Avalonia 浅色 zh 外观页。

逐条预期：
1. **导航**：6 个页签顺序＝状态/音量/分组/规则/外观/设置，每签有图标＋文字；滑动胶囊停在当前选中页。
2. **状态页（深色 zh）**：顶栏 Tuner 品牌＋设备按钮（绿点＋设备名）＋主电平表；统计条三项"会话/出声中/闪避中"数值为 SemiBold 数字；会话行＝图标→应用名→分组副行→峰值 LED→VOL%（等宽右对齐）→徽章；页面内说明文字 ≤1 行；空态一行短句。
3. **音量页**：MASTER 通道琥珀描边、与应用通道同宽并排；通道内 名称/竖电平/竖推子/百分比/静音 自上而下对齐；曲线卡在下方，提示 ≤1 行。
4. **分组页**：左列分组卡（名称＋计数），底部"＋新建分组"主按钮；右编辑卡四段动线清晰；进程 chips 流式排列；删除按钮右下。
5. **规则页**：左规则卡＝自然语句＋启用开关；右编辑器三节（关系/效果/条件）以细分隔线分组，每节小标题；无超过 1 行的可见提示。
6. **外观页**：预览卡含主按钮/普通按钮/开关/滑条/下拉/两级文字样例；下方 7 行设置：主题下拉、8 圆色板（当前色有描边环）、字号/密度/圆角下拉、动效开关、语言下拉；行间距均匀。
7. **浅色切换**：同一张截图内窗口整体浅色（含下拉、滚动条、滑轨、输入框边框），无深色残块；文字对比清晰（正文对比 ≥7:1）。
8. **强调色切换（蓝）**：主按钮底、MASTER 描边、页签选中胶囊、色板选中环、开关选中色全部变蓝；语义色（绿/琥珀/红）不跟随变化。
9. **字号大＋紧凑**：页签/按钮/正文全部变大；卡片内边距行距变小；两页无文字截断、无重叠、无横向滚动条（宽度 920 下）。
10. **圆角直角**：按钮/卡片/输入框圆角目视接近直角（半径 3–6px）。
11. **en-US**：全部标签英文、无中文残留、无单词截断（状态/规则/外观/设置四页抽查）。
12. **重启保留**：改主题+强调色后（自动保存提示出现）结束进程，重开首帧即为目标主题（第二轮截图验证）。
13. **合同检查**：`python -I tools/validate_ui.py` PASS（页序合同含外观页、双语键一致、资源键齐全、深浅两组对比度 ≥4.5:1）。
14. **构建**：WPF 与 Avalonia(windows 目标) Release 构建 0 error；行为面（推子/静音/规则/托盘）代码零改动——git diff 不含 Tuner.Core 引擎与两平台音频层。

不做的（明确出界）：窗口亚克力/透明、系统明暗实时监听、Avalonia FluentTheme 内建动效的全局开关、引擎与音频层任何改动、版本号与 dist。

## 7. 验证能力

dotnet SDK 8.0.418 可用（本轮已确认）。WPF 用自带 `--screenshot --shot-dir --shot-size --lang`；Avalonia 本轮补同参截图模式。截图模式引擎隔离为空规则、只读会话，可与运行中实例共存，不动用户音频。看图验收走 image-viewer 子代理（多图一批）。
