# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec file for cc-excel."""

import os
from pathlib import Path

block_cipher = None

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
        'xlsxwriter',
        'xlsxwriter.workbook',
        'xlsxwriter.worksheet',
        'xlsxwriter.format',
        'xlsxwriter.chart',
        'xlsxwriter.chartsheet',
        'openpyxl',
        'markdown_it',
        'cli',
        'models',
        'type_inference',
        'xlsx_generator',
        'chart_builder',
        'spec_models',
        'spec_parser',
        'spec_generator',
        'md_converter',
        'parsers',
        'parsers.csv_parser',
        'parsers.json_parser',
        'parsers.markdown_parser',
        'themes',
        'cc_shared',
        'cc_shared.themes',
        'cc_shared.config',
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
    name='cc-excel',
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
