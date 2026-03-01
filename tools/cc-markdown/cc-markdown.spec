# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec file for cc-markdown."""

import os
from pathlib import Path

block_cipher = None

# Get the spec file directory
spec_path = Path(SPECPATH)
src_dir = spec_path / 'src'
themes_dir = src_dir / 'themes'

# Collect theme CSS files
theme_files = []
for css_file in themes_dir.glob('*.css'):
    theme_files.append((str(css_file), 'src/themes'))

a = Analysis(
    [str(spec_path / 'main.py')],
    pathex=[SPECPATH, str(spec_path / 'src')],
    binaries=[],
    datas=theme_files,
    hiddenimports=[
        'typer',
        'rich',
        'rich.console',
        'rich.table',
        'markdown_it',
        'mdit_py_plugins',
        'mdit_py_plugins.tasklists',
        'mdit_py_plugins.footnote',
        'linkify_it',
        'pygments',
        'docx',
        'docx.shared',
        'docx.enum.text',
        'docx.enum.style',
        'bs4',
        'cli',
        'parser',
        'html_generator',
        'pdf_converter',
        'word_converter',
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
    name='cc-markdown',
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
