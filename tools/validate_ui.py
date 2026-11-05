"""Static UI contracts only; this is NOT a XAML compiler or native UI test.
Usage: python -I tools/validate_ui.py [--baseline PATH]
Baseline layout: wpf-before/MainWindow.xaml, avalonia-before/MainWindow.axaml.
"""
from pathlib import Path
import argparse
import collections
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'
EVENTS = {'Click','Loaded','SelectionChanged','ValueChanged','Checked','Unchecked','PreviewMouseLeftButtonUp','PreviewKeyDown',
          'IsCheckedChanged','LostFocus','KeyDown','TextChanged','TextInput',
          'MouseEnter','MouseLeave','PreviewMouseWheel','MouseLeftButtonDown',
          'Expanded','Collapsed','CurveEdited'}

def local(tag):
    return tag.split('}')[-1]

def contract(tree):
    names = collections.Counter(e.attrib[X+'Name'] for e in tree.iter() if X+'Name' in e.attrib)
    events = collections.Counter((local(e.tag), k, v) for e in tree.iter()
                                 for k,v in e.attrib.items() if k in EVENTS or k.endswith('.TextChanged'))
    named_events = {(e.attrib[X+'Name'], k): v for e in tree.iter() if X+'Name' in e.attrib
                    for k,v in e.attrib.items() if k in EVENTS}
    return names, events, named_events

def check(app, extension, baseline):
    folder = ROOT/'src'/app
    files = list(folder.glob('*.'+extension)) + list((folder/'Lang').glob('*.'+extension)) + list((folder/'Theme').glob('*.'+extension))
    trees = [ET.parse(f) for f in files]
    keys = {e.attrib[X+'Key'] for t in trees for e in t.iter() if X+'Key' in e.attrib}
    for f,t in zip(files,trees):
        for e in t.iter():
            for v in e.attrib.values():
                for key in re.findall(r'\{(?:Static|Dynamic)Resource\s+([^{}]+)\}',v):
                    assert key in keys, f'{f.name}: missing resource {key}'
        for grid in t.iter():
            if local(grid.tag) != 'Grid': continue
            for axis in ['Row','Column']:
                prop = axis+'Definitions'
                if prop in grid.attrib:
                    count = len(grid.attrib[prop].split(','))
                else:
                    defs = next((c for c in grid if local(c.tag)=='Grid.'+prop),None)
                    count = len(defs) if defs is not None else 1
                for child in grid:
                    assert int(child.get('Grid.'+axis,0)) < count, f'{f.name}: out-of-range Grid.{axis}'
    window = ET.parse(folder/('MainWindow.'+extension))
    now = contract(window)
    assert all(n==1 for n in now[0].values()), 'duplicate named controls'
    cs = '\n'.join(p.read_text(encoding='utf-8-sig') for p in folder.glob('*.cs'))
    for _,_,handler in now[1]:
        assert re.search(r'\b'+re.escape(handler)+r'\s*\(',cs), f'missing handler {handler}'
    langs = [ET.parse(folder/'Lang'/('Strings.'+lang+'.'+extension)) for lang in ['zh-CN','en-US']]
    assert {e.get(X+'Key') for e in langs[0].getroot()} == {e.get(X+'Key') for e in langs[1].getroot()}, 'language key mismatch'
    zh_keys = {e.get(X+'Key') for e in langs[0].getroot()}
    need = {'S.Tab.Appearance','S.Ap.Theme','S.Ap.Accent','S.Ap.FontScale','S.Ap.Density','S.Ap.Radius','S.Rule.SecRelation'}
    assert need <= zh_keys, f'{app}: appearance keys missing {need - zh_keys}'
    tabs = [e for e in window.iter() if local(e.tag)=='TabItem']
    tab_keys = []
    for tab in tabs:
        header = next(c for c in tab if local(c.tag)=='TabItem.Header')
        tab_keys.extend(re.findall(r'S.Tab.\w+',ET.tostring(header,encoding='unicode')))
    assert tab_keys == ['S.Tab.Status','S.Tab.Volume','S.Tab.Groups','S.Tab.Rules','S.Tab.Appearance','S.Tab.Settings'],tab_keys
    if baseline:
        old = contract(ET.parse(baseline))
        assert old == now, f'{app}: named control or event contract changed'
    print(f'PASS {app}: XML, resources, grid indices, {len(now[0])} names, {sum(now[1].values())} events, bilingual keys, page order'+(', baseline contracts' if baseline else ''))

def contrast(a,b):
    def lum(c):
        v=[int(c[i:i+2],16)/255 for i in (1,3,5)]
        v=[x/12.92 if x<=.04045 else ((x+.055)/1.055)**2.4 for x in v]
        return sum(x*w for x,w in zip(v,[.2126,.7152,.0722]))
    lo,hi=sorted([lum(a),lum(b)])
    return (hi+.05)/(lo+.05)

if __name__ == '__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--baseline',type=Path)
    args=p.parse_args()
    for app,ext,sub in [('Tuner.App','xaml','wpf-before'),('Tuner.App.Avalonia','axaml','avalonia-before')]:
        check(app,ext,args.baseline/sub/('MainWindow.'+ext) if args.baseline else None)
    for fg,bg in [('#98A2B3','#1F2530'),('#E8ECF4','#171C24'),('#241A05','#E8A33D'),('#F8A0A0','#331B1E'),('#1E2530','#FAFBFD'),('#57626F','#F4F6F9')]:
        ratio=contrast(fg,bg)
        assert ratio>=4.5,(fg,bg,ratio)
        print(f'PASS palette contrast {fg}/{bg}: {ratio:.2f}:1')
    print('NOT VERIFIED: XAML compilation, native layout, DPI, focus navigation, audio behavior.')
