# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec file for cc-excel."""

from pathlib import Path

block_cipher = None

spec_path = Path(SPECPATH)

a = Analysis(
    [str(spec_path / 'main.py')],
    pathex=[SPECPATH, str(spec_path / 'src')],
    binaries=[],
    datas=[],
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
        'markdown_it',
        'cli',
        'models',
        'type_inference',
        'xlsx_generator',
        'chart_builder',
        'spec_models',
        'spec_parser',
        'spec_generator',
        'parsers',
        'parsers.csv_parser',
        'parsers.json_parser',
        'parsers.markdown_parser',
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
