# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec file for cc-pdf."""

import os
from pathlib import Path

block_cipher = None

# Get the spec file directory
spec_path = Path(SPECPATH)

# Get cc_shared path
cc_shared_path = os.path.abspath('../cc_shared')

a = Analysis(
    [str(spec_path / 'main.py')],
    pathex=[SPECPATH, str(spec_path / 'src'), cc_shared_path],
    binaries=[],
    datas=[
        (cc_shared_path + '/*.py', 'cc_shared'),
        (cc_shared_path + '/providers/*.py', 'cc_shared/providers'),
    ],
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
        'bs4',
        'pymupdf',
        'fitz',
        'cli',
        'html_generator',
        'pdf_converter',
        'md_converter',
        'cc_shared',
        'cc_shared.themes',
        'cc_shared.css_themes',
        'cc_shared.markdown_parser',
        'cc_shared.config',
        'cc_shared.image_extractor',
        'cc_shared.providers',
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
    name='cc-pdf',
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
