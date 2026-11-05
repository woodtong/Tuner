"""One-shot mechanical tokenization for Theme.xaml / MainWindow.xaml (WPF).
- palette/color/brush StaticResource -> DynamicResource
- FontSize / CornerRadius / Duration literals -> preference tokens
- density-relevant Margin/Padding -> M.* tokens
Run from repo root: python tools/tokenize_theme.py
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

PALETTE = """
CardBgColor ItemBgColor ItemHoverColor HoverSoftColor PressedBgColor CardBorderColor
FieldBorderColor FieldBorderHoverColor FieldBgColor AccentColor AccentHoverColor
AccentPressedColor AccentSoftColor AccentFgColor TextOnAccentColor SwitchTrackColor
SwitchOnColor GreenColor TextPrimaryColor TextSecondaryColor TextFaintColor CheckOnColor
ScrollThumbColor ScrollThumbHoverColor LedUnlitColor AmberColor AmberSoftColor
AmberBadgeBorderColor AmberTextColor DangerColor DangerBorderColor FaderGrooveBgColor
FaderGrooveRimColor PanelShadowColor PanelEdgeTopColor PanelEdgeBottomColor
PageBg CardBg ItemBg ItemHover HoverSoft PressedBg CardBorder FieldBorder FieldBorderHover
FieldBg Accent AccentHover AccentPressed AccentSoft AccentFg TextOnAccent SwitchTrackBg
SwitchOnBg CheckOnBg ScrollThumbBg ScrollThumbHoverBg LedUnlit TextPrimary TextSecondary
TextFaint Green Amber AmberSoft AmberBadgeBorder AmberText Danger DangerBorder
FaderGrooveBg FaderGrooveRim PanelShadowBg
""".split()

FZ = {"9.5": "Fz.Micro", "10": "Fz.Micro", "10.5": "Fz.Micro", "11": "Fz.Micro",
      "11.5": "Fz.Small", "12": "Fz.Small", "12.5": "Fz.Base", "13": "Fz.Body",
      "13.5": "Fz.Title", "14": "Fz.Stat", "15": "Fz.Stat", "16": "Fz.Stat",
      "17": "Fz.Stat", "18": "Fz.Brand", "20": "Fz.Brand"}
RAD = {"4": "Rad.Chip", "4.5": "Rad.Chip", "5": "Rad.Chip", "6": "Rad.Control",
       "6.5": "Rad.Control", "7": "Rad.Control", "8": "Rad.Card", "9": "Rad.Card",
       "10": "Rad.Card", "11": "Rad.Card", "12": "Rad.Panel"}
DUR = {"0:0:0.05": "Dur.Press", "0:0:0.06": "Dur.Press", "0:0:0.08": "Dur.Press",
       "0:0:0.1": "Dur.Press", "0:0:0.12": "Dur.Hover", "0:0:0.14": "Dur.Hover",
       "0:0:0.15": "Dur.Hover", "0:0:0.16": "Dur.Hover", "0:0:0.18": "Dur.Hover",
       "0:0:0.2": "Dur.Hover", "0:0:0.25": "Dur.Hover"}
MARGIN_MAIN = {"20,14,20,12": "M.Page", "0,14,0,0": "M.Section", "0,16,0,0": "M.Section",
               "0,18,0,0": "M.Section", "0,12,0,0": "M.Row", "0,10,0,0": "M.Row"}
PADDING_MAIN = {"22": "M.Card", "20": "M.Card"}
PADDING_THEME = {"11,9": "M.Item"}

def convert(path, margin_map, padding_map):
    s = path.read_text(encoding="utf-8-sig")
    n = 0
    for k in PALETTE:
        s2 = s.replace("{StaticResource %s}" % k, "{DynamicResource %s}" % k)
        n += (len(s) - len(s2)) // (17 + len(k))
        s = s2
    for lit, tok in FZ.items():
        s2 = s.replace('FontSize="%s"' % lit, 'FontSize="{DynamicResource %s}"' % tok)
        n += s.count('FontSize="%s"' % lit)
        s = s2
        s2 = s.replace('TextElement.FontSize="%s"' % lit,
                       'TextElement.FontSize="{DynamicResource %s}"' % tok)
        n += s.count('TextElement.FontSize="%s"' % lit)
        s = s2
    for lit, tok in RAD.items():
        n += s.count('CornerRadius="%s"' % lit)
        s = s.replace('CornerRadius="%s"' % lit, 'CornerRadius="{DynamicResource %s}"' % tok)
    for lit, tok in DUR.items():
        n += s.count('Duration="%s"' % lit)
        s = s.replace('Duration="%s"' % lit, 'Duration="{DynamicResource %s}"' % tok)
    for lit, tok in margin_map.items():
        n += s.count('Margin="%s"' % lit)
        s = s.replace('Margin="%s"' % lit, 'Margin="{DynamicResource %s}"' % tok)
    for lit, tok in padding_map.items():
        n += s.count('Padding="%s"' % lit)
        s = s.replace('Padding="%s"' % lit, 'Padding="{DynamicResource %s}"' % tok)
    path.write_text(s, encoding="utf-8")
    print(f"{path.name}: {n} replacements")

if __name__ == "__main__":
    app = ROOT / "src/Tuner.App"
    convert(app / "Theme.xaml", {}, PADDING_THEME)
    convert(app / "MainWindow.xaml", MARGIN_MAIN, PADDING_MAIN)
