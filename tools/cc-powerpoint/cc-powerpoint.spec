# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec file for cc-powerpoint."""

import os
from pathlib import Path

block_cipher = None

# Get the spec file directory
spec_path = Path(SPECPATH)
src_dir = spec_path / 'src'

# python-pptx ships a default.pptx template that must be bundled
import pptx
pptx_pkg_dir = Path(pptx.__file__).parent
pptx_templates = pptx_pkg_dir / 'templates'

# Collect pptx template files
pptx_data = []
if pptx_templates.exists():
    for f in pptx_templates.glob('*'):
        pptx_data.append((str(f), 'pptx/templates'))

a = Analysis(
    [str(spec_path / 'main.py')],
    pathex=[SPECPATH, str(spec_path / 'src')],
    binaries=[],
    datas=pptx_data,
    hiddenimports=[
        'typer',
        'rich',
        'rich.console',
        'rich.table',
        'markdown_it',
        'mdit_py_plugins',
        'mdit_py_plugins.tasklists',
        'pptx',
        'pptx.util',
        'pptx.dml.color',
        'pptx.enum.text',
        'lxml',
        'lxml._elementpath',
        'lxml.etree',
        'cli',
        'parser',
        'pptx_generator',
        'themes',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name='cc-powerpoint',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=None,
)
