# -*- coding: utf-8 -*-
import io

p = 'README.md'
src = io.open(p, encoding='utf-8').read()
old = '| **v0.1-eta (2026-10-07)** | 导航改为左侧栏切换 |'
new = '''| v0.1-eta (2026-10-07) | 导航改为左侧栏切换 |
| **v0.1 (当前)** | 通道列对齐修复；音量页改竖向通道条（独立应用通道 block，预留扩展区）；设置页精简（SMTC 为主信号，峰值/宽限兜底参数移入配置文件）。版本命名经用户指示定版为 **v0.1** |'''
assert old in src
src = src.replace(old, new)
io.open(p, 'w', encoding='utf-8', newline='').write(src)

p2 = r'C:\Users\lenovo\.zcode\cli\memories\projects\tuner-998a53186478c3a9\memory\tuner-versioning-policy.md'
src2 = io.open(p2, encoding='utf-8').read()
src2 = src2.replace(
    'description: Tuner 版本号策略——数字版本保持 0.1，预发布标签按希腊字母序列推进，仅用户明确指示时才递增数字',
    'description: Tuner 版本命名——用户已定版为 v0.1（希腊字母序列停用），后续命名听用户指示')
src2 = src2.replace(
    '用户指示（2026-10-07）：Tuner 的发布版本号**保持 0.1**，预发布标签使用希腊字母序列（alpha → beta → gamma → delta → epsilon → zeta …）。',
    '用户指示（2026-10-07）：**版本号定版为 v0.1**（csproj Version=0.1，包名 Tuner-v0.1-win-x64.zip）。希腊字母序列（alpha…theta）已停用；后续版本如何命名（v0.2 / 1.0 / 日期等）等用户明确指示。')
src2 = src2.replace(
    '**How to apply:** 每次打包/发布新产物时，除非用户明确给出新版本号，否则保持 `<Version>0.1.0-<希腊字母></Version>` 只推进标签（已用：alpha、beta、gamma、delta、epsilon、zeta、eta、theta，下一个是 iota）。包/zip 命名形如 `Tuner-v0.1-beta-win-x64.zip`。项目背景见 [[tuner-project-roadmap]]。',
    '**How to apply:** 打包时保持 Version=0.1 与 `Tuner-v0.1-win-x64.zip` 命名，不要自行推进版本号；用户提出新命名时再改。项目背景见 [[tuner-project-roadmap]]。')
io.open(p2, 'w', encoding='utf-8', newline='').write(src2)

p3 = r'C:\Users\lenovo\.zcode\cli\memories\projects\tuner-998a53186478c3a9\memory\tuner-project-roadmap.md'
src3 = io.open(p3, encoding='utf-8').read()
add = '''- 2026-10-07：对齐修复（通道列固定列宽）、音量页改竖向通道条（独立 block + 竖 LED 表/竖推子，LedMeter 加 Orientation）、设置页精简（SMTC 为主信号，峰值/宽限兜底参数移出界面留配置文件）。用户指示**版本定版 v0.1**（希腊字母停用，见 [[tuner-versioning-policy]]）并打包。
'''
marker = '- 2026-10-07：git 仓库初始化'
assert marker in src3
src3 = src3.replace(marker, add + marker, 1)
io.open(p3, 'w', encoding='utf-8', newline='').write(src3)
print('docs done')
