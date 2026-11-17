"""Regenerate embedded toolbar atlases from Microsoft's MIT-licensed Fluent SVGs.

Design-time tool only: Python 3 and Edge/Chrome; normal builds need neither.
SVG originals and the pinned upstream revision are kept beside the PNGs.
"""
import concurrent.futures
import json
import math
import os
from pathlib import Path
import re
import subprocess
import time
from urllib.parse import quote
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / 'res' / 'icons'
WORK = ROOT / 'bin' / 'fluent-assets'
SIZES = [12, 14, 16, 18, 20, 24, 26, 28, 30, 32, 36, 40, 42, 48, 56, 64, 72, 80, 96, 128]
ICONS = {
    'select': 'Cursor', 'arrow': 'Arrow Up Right', 'line': 'Line',
    'shape': 'Shapes', 'callout': 'Comment', 'text': 'Text',
    'step': 'Number Circle 1', 'stamp': 'Star', 'pen': 'Pen',
    'highlighter': 'Highlight', 'fill': 'Paint Bucket', 'blur': 'Blur',
    'magnify': 'Zoom In', 'spotlight': 'Flashlight', 'eraser': 'Eraser',
    'crop': 'Crop', 'cutout': 'Cut', 'camera': 'Camera', 'video': 'Video',
    'region': 'Select Object', 'window': 'Window', 'fullscreen': 'Full Screen Maximize',
    'scroll': 'Document One Page', 'freehand': 'Lasso', 'fixed': 'Ruler',
    'repeat': 'Arrow Repeat All', 'new': 'Add', 'open': 'Folder Open',
    'save': 'Save', 'copy': 'Copy', 'paste': 'Clipboard Paste',
    'undo': 'Arrow Undo', 'redo': 'Arrow Redo', 'zoomin': 'Zoom In',
    'zoomout': 'Zoom Out', 'fit': 'Arrow Fit', 'actual': 'Ratio One To One',
    'settings': 'Settings', 'library': 'Library', 'print': 'Print',
    'email': 'Mail', 'folder': 'Folder Open', 'rotate': 'Arrow Rotate Clockwise',
    'flip': 'Flip Horizontal', 'resize': 'Resize Image', 'canvas': 'Resize',
    'border': 'Border Outside', 'shadow': 'Image Shadow', 'torn': 'Document Landscape Split',
    'adjust': 'Options', 'gray': 'Color', 'watermark': 'Text Effects',
    'trash': 'Delete', 'share': 'Share', 'close': 'Dismiss', 'help': 'Question Circle',
    'check': 'Checkmark', 'delay': 'Timer', 'layers': 'Layer Diagonal',
    'bold': 'Text Bold', 'italic': 'Text Italic', 'underline': 'Text Underline',
    'textcolor': 'Text Color', 'font': 'Text Font', 'alignl': 'Text Align Left',
    'alignc': 'Text Align Center', 'alignr': 'Text Align Right', 'tray': 'Subtract',
}

def read_url(url):
    return urlopen(Request(url, headers={'User-Agent': 'RXCapture-Fluent-Assets'}), timeout=30).read()

DEST.mkdir(parents=True, exist_ok=True)
WORK.mkdir(parents=True, exist_ok=True)
revision_file = DEST / 'fluent-source.json'
if revision_file.exists():
    revision = json.loads(revision_file.read_text())['revision']
else:
    revision = json.loads(read_url('https://api.github.com/repos/microsoft/fluentui-system-icons/commits/main'))['sha']
base = 'https://raw.githubusercontent.com/microsoft/fluentui-system-icons/' + revision + '/'
index = read_url(base + 'icons_regular.md').decode()
available = {name: [int(s) for s in re.findall(r'_(\d+)_regular', row)]
             for name, row in re.findall(r'^\|([^|]+)\|(.*)$', index, re.M)}
missing = sorted(set(ICONS.values()) - available.keys())
if missing:
    raise SystemExit('Unknown Fluent names: ' + ', '.join(missing))

def download(item):
    key, title = item
    folder = DEST / 'svg' / key
    folder.mkdir(parents=True, exist_ok=True)
    for nominal in sorted(set(min(available[title], key=lambda s: abs(s - wanted)) for wanted in (16, 20, 24, 32))):
        target = folder / (str(nominal) + '.svg')
        if not target.exists():
            slug = title.lower().replace(' ', '_')
            target.write_bytes(read_url(base + 'assets/' + quote(title) + '/SVG/ic_fluent_' + slug + '_' + str(nominal) + '_regular.svg'))
    return key

with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
    for key in pool.map(download, ICONS.items()):
        print('Prepared', key, flush=True)
(DEST / 'LICENSE-Fluent.txt').write_bytes(read_url(base + 'LICENSE'))
revision_file.write_text(json.dumps({'repository': 'https://github.com/microsoft/fluentui-system-icons',
                                   'revision': revision, 'icons': ICONS}, indent=2) + '\n', encoding='utf-8')
(DEST / 'fluent-names.txt').write_text('\n'.join(ICONS) + '\n', encoding='utf-8')

browser = next((p for p in [Path(os.environ.get('ProgramFiles(x86)', '')) / 'Microsoft/Edge/Application/msedge.exe',
                           Path(os.environ.get('ProgramFiles', '')) / 'Google/Chrome/Application/chrome.exe'] if p.is_file()), None)
if browser is None:
    raise SystemExit('Edge or Chrome is required to regenerate icons.')

for size in SIZES:
    cols, rows = 8, math.ceil(len(ICONS) / 8)
    width, height = cols * size, rows * size
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">']
    for i, key in enumerate(ICONS):
        files = list((DEST / 'svg' / key).glob('*.svg'))
        # Keep size-specific optical drawings for small controls and large actions.
        nominal = min(files, key=lambda p: abs(int(p.stem) - min(size, 32)))
        source = nominal.read_text(encoding='utf-8')
        root = ET.fromstring(source)
        viewbox = root.attrib.get('viewBox', '0 0 ' + nominal.stem + ' ' + nominal.stem)
        inner = re.search(r'<svg\b[^>]*>(.*)</svg>', source, re.S).group(1)
        inner = re.sub(r'fill="(?!none)[^"]*"', 'fill="#FFFFFF"', inner)
        parts.append(f'<svg x="{(i % cols)*size}" y="{(i // cols)*size}" width="{size}" height="{size}" viewBox="{viewbox}" fill="#FFFFFF">{inner}</svg>')
    parts.append('</svg>')
    sheet = WORK / ('fluent-' + str(size) + '.svg')
    output = DEST / ('fluent-' + str(size) + '.png')
    sheet.write_text('\n'.join(parts), encoding='utf-8')
    args = [str(browser), '--headless', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
            '--default-background-color=00000000', '--force-device-scale-factor=1',
            '--user-data-dir=' + str(WORK / 'browser-profile'), '--screenshot=' + str(output),
            f'--window-size={width},{height}', sheet.as_uri()]
    started = time.time()
    result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=45,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if result.returncode or not output.exists() or output.stat().st_mtime < started - 2:
        raise SystemExit('Atlas rendering failed at ' + str(size) + ' px: ' + result.stderr.decode(errors='replace')[-600:])
    print('Rendered', output.name, flush=True)
