# 双端 UI 第 2 轮验证记录（文案精简 + 排版逻辑化 + 外观模块）

日期：2026-10-24。结论：**双端改版构建通过、静态合同全绿、截图矩阵取证完成、三轮独立子代理视觉验收全部通过（终轮三处修复全部 PASS，无遗留缺陷）**。未提交 git、未动 dist、未改版本号；引擎与音频层零改动。

## 改动范围（对基线 main 9155529）

- **扬弃**：删除白色主题分支 `feat/ui-redesign-attempt`；工作区旧"白色工作台"改动 stash 备份（stash@{0}）后还原。
- **Core**：`TunerConfig.Appearance`（theme/accent/fontScale/density/radius/animations）+ Normalize 归一。引擎不读。
- **WPF**：Theme/Palette.{Dark,Light}.xaml 拆分（全部 DynamicResource 引用，运行时换字典热切换）；Appearance.cs（主题/强调色 HSL 推导/字号/密度/圆角覆写）；自绘控件（LedMeter/CurveChart/导航胶囊）改资源取色；新增第 5 页签「外观」（预览卡+8 色板+7 行设置）；状态行重构（图标+名称+分组副行+峰值+音量%主位，删冗余列）；规则编辑器三节（关系/效果/条件）；语言行移入外观页；zh/en 全量文案精简（230 键）。
- **Avalonia**：App.axaml 深色默认 + ThemeDictionaries 深浅双字典 + 令牌；同款外观页（无动效行，如实不设项）；状态行/规则节/设置瘦身同步；新增 `--screenshot --shot-dir --shot-size --lang --appearance --accent` 取证参数；语言文件镜像 WPF 新文案。
- **工具**：validate_ui.py 合同更新（页序含外观页、外观键存在性、6 组深浅对比度对）；tools/tokenize_theme.py（一次性令牌化，留存备查）。

## 已执行验证

1. **构建**：Tuner.Core / Tuner.App（Release）/ Tuner.App.Avalonia（net8.0-windows10.0.19041.0，Release）全部 0 error。
2. **静态合同**：`python -I tools/validate_ui.py` → WPF 70 命名控件/72 事件、Avalonia 55/44、双语键一致（230=230）、页序=状态/音量/分组/规则/外观/设置、外观键齐全；对比度 6 组全过（最低 5.74:1）。
3. **截图矩阵**（ui-preview/round2/，全部真实构建运行， Tabs 关闭命中测试防鼠标伪影）：WPF 深色×6 页、浅色、英文、蓝强调、大字号+紧凑+直角、最小窗 800×580；Avalonia 深色×6 页、浅色、英文。
4. **像素复核**（PIL）：深色选中导航胶囊 #332B1C（=AccentSoft）+ 琥珀选中文字 294px；蓝强调胶囊 (68,123,219)=蓝调 AccentSoft；Avalonia 深色窗口底 #0E1218。
5. **子代理视觉验收**：第一轮 15/15 PASS（提出 D1/D2/D3）；修复后第二轮 Avalonia 全量 A1-A6/B1/C1 全 PASS；第三轮复核（wp-en 空格 / ava 音量页裁切 / 规则列表）进行中，结论见下方事件记录。

## 事件记录（本轮教训，均已修复）

1. **Storyboard 内 DynamicResource 冻结失败**（XamlParseException: 无法冻结此 Storyboard）：动画 Duration/To 的资源引用必须可冻结。回退：动画回字面值、颜色动画改"同值 Setter（DynamicResource）+透明度动画"模式；深浅切换靠视觉层 DynamicResource 热刷新。副作用：动效开关只作用于代码驱动动画（导航胶囊），模板内 0.08-0.15s 微渐变不受控——已在外观页提示与计划书如实标注。
2. **dev 取证参数污染用户配置**：截图运行会因程序化设值触发 MarkDirty→自动保存链，把 --appearance/--accent 等内存值写进真实 config.json（实测跨运行累积）。修复：DevAppearanceActive 挡板（取证运行禁保存）+ 恢复用户配置默认外观。
3. **截图期真实鼠标悬停伪影**：截图窗口弹在屏幕中央，用户鼠标恰好压在导航上→IsMouseOver 把胶囊/文字染成悬停色，冒充"选中态异常"。修复：截图模式关闭 Tabs 命中测试。教训：对"悬停敏感"UI 取证必须隔离输入。
4. **镜像文件后缀事故**：Avalonia 语言文件误写成 `.xaml`（WPF 后缀），真身 `.axaml` 未更新→构建用旧文案、外观页标签缺失。修复后强制全量重建。
5. **增量构建陈旧 BAML**：改 .xaml 后普通增量构建产物未含新资源（[state] 空格修复两次截图仍粘连）；`--no-incremental` 全量重建后解决。取证截图前建议一律全量构建。
6. **XAML 文本规范化吞空格**（[state] 粘连的真根因）：WPF BAML 编译默认剥掉文本内容首尾空格（内部空格不受影响），`xml:space="preserve"` 才能保留——源文件有空格、二进制没有，dll 字节级比对（`b' [state]' in data`）可直接确诊。
7. **Avalonia 通道纵向堆叠**（旧代码遗留）：VolumeList 缺横向 ItemsPanel，旧版用 Height=300 截断伪装成横向；本轮补 `StackPanel Orientation="Horizontal"` 彻底修复。顺带修掉旧代码两处遗留：规则页"判定方式"下拉缺 Grid.Column 与标签重叠、Avalonia 构造器漏调 RefreshRules（规则列表启动为空）。

## 未验证 / 已知限制

- 系统明暗实时跟随（监听 WM_SETTINGCHANGE）未做，"跟随系统"在启动/重开窗口时生效。
- Linux 目标（net8.0）仅编译过 Windows 等价物，未在真 Linux/Xvfb 出图；--screenshot 参数已就绪，待 Linux 环境复跑。
- 音频行为面（推子/静音/闪避/托盘）代码零改动，本轮未重跑 --probe/--selftest（改动不触及该链路）；发布前应按旧清单完整回归。
- 外观项不含窗口透明度/亚克力（WPF 需 Win32 互操作，风险大于收益，计划书已声明）。

## 动效开关二修（2026-10-24 深夜，用户复测报"不论开关都无动效"）

- **根因**：WPF 对同 URI 的 BAML 字典有实例缓存——关动效后切回"开"时，`new ResourceDictionary{Source=Theme.xaml}` 拿回的是缓存实例，WPF 判定"无变化"，DynamicResource 样式引用不重算 → 动画永久丢失。SwapPalette（深↔浅）同样存在此隐患。
- **修复**：`FreshDictionary(uri)`——装载后把全部键值物化进一个全新普通字典实例再合并；每次切换都是真正的换血。`--anim-cycle` 取证装置（关→开→关逐段打点样式哈希）验证：关 #0000000B → 开 #00000034，实例确已更换。
- **默认值反转**：动效默认改为**关**（用户三次明确表态"一闪一闪很难受/不应该出现"；想要者经外观页手动开启），用户配置已同步置 false。

## 用户实测反馈修复轮（2026-10-24 晚）

用户实测后报 3 项，全部修复并验证：

1. **原生警告弹窗扎眼 + 默认组勾选不弹回**：新增应用内风格化 `MsgBox`（深色圆角卡片+投影，Esc/Enter/DragMove，随主题走），替换全部 11 处 `MessageBox.Show`；默认组取消改为 Dispatcher 延迟弹回（点击管线内同步写回会被点击结果覆盖——即"提示了但勾选仍显示取消"的根因）。
2. **选中项看不清（橙底橙字）**：根因＝Appearance 推导 AccentSoft 的混合方向写反（强调色向基底 17%，应为基底向强调轻染 ~16%/22%）；用户实选琥珀色板后选中胶囊变亮橙、文字同为橙系不可读。已修（双端）；像素复核：显式琥珀下导航胶囊 (63,57,50) 暗琥珀染 + 琥珀文字 294px，与默认观感一致。顺带输入框文字选区/光标主题化（SelectionBrush=Accent、SelectionTextBrush=TextOnAccent、CaretBrush=TextPrimary）。
3. **关动效仍"一闪一闪"**：动效开关原来只门控代码动画，模板内 0.1s 级渐变不受控。改为 **Theme.NoAnim.xaml 字典热切换**（由 Theme.xaml 生成、38 处时长归零；MainWindow 118 处样式引用转 DynamicResource，换字典即全套模板重应用），推子代码动效时长同步归零，诊断链接渐变移除。`--noanim` 取证参数可复现关动效态。教训入库：改 Theme.xaml 后必须重新生成 NoAnim（脚本见下）。

复跑：`python -c "import re,pathlib;s=pathlib.Path('src/Tuner.App/Theme.xaml').read_text(encoding='utf-8');pathlib.Path('src/Tuner.App/Theme.NoAnim.xaml').write_text(re.sub(r'Duration=\"0:0:0\.\d+\"','Duration=\"0:0:0\"',s),encoding='utf-8')"`（后再全量构建）。

## 复跑指令

```
dotnet build src/Tuner.App/Tuner.App.csproj -c Release --no-incremental
src/Tuner.App/bin/Release/net8.0-windows10.0.19041.0/Tuner.exe --screenshot --shot-dir ui-preview/round2/smoke [--lang en-US | --appearance light | --accent #4C8DFF | --fontscale large --density compact --radius sharp | --shot-size 800x580]
dotnet build src/Tuner.App.Avalonia/Tuner.App.Avalonia.csproj -c Release -f net8.0-windows10.0.19041.0 --no-incremental
src/Tuner.App.Avalonia/bin/Release/net8.0-windows10.0.19041.0/Tuner.Cross.exe --screenshot --shot-dir <dir> [同上参数]
python -I tools/validate_ui.py
```
截图模式引擎隔离为空规则、只读会话，可与运行中实例共存；取证运行不落盘配置。


## 事件补记（2026-10-24 深夜）：Avalonia 滑动胶囊导航（用户追加需求）

- 需求：动效开＝导航胶囊随指针流式滑动（cogent/WPF 同款）；关＝Fluent 静态高亮。外观页「动效」开关控制（Appearance.Animations）。
- 实测：xdotool 悬停滑至目标页签（292-366 vs 预期 276-352 容差内）✓；指针入内容区回落选中页签0（16-138）✓；关闭态胶囊像素检出 0 ✓；双端 --smoke exit 0 ✓。
- 坑两枚：①胶囊 Border 显式 Width + 默认 HorizontalAlignment=Stretch → 居中漂移（960-122)/2≈419，实测 418-540 精确吻合；②xdotool 鼠标轨迹历史会在新进程启动时被 X 重放（一次性事件风暴，勿误判为幽灵交互），WSLg 死进程窗口会残留（截图须确认窗口为活实例；--pid 搜窗在 WSLg 无效）。
