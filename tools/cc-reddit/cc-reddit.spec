# -*- mode: python ; coding: utf-8 -*-

import os
from PyInstaller.utils.hooks import collect_data_files, collect_submodules

cc_storage_path = os.path.abspath('../cc_storage')

a = Analysis(
    ['src\\__main__.py'],
    pathex=['.', '../cc_storage'],
    binaries=[],
    datas=collect_data_files('rich') + [(cc_storage_path + '/*.py', 'cc_storage')],
    hiddenimports=[
        'typer',
        'rich',
        'httpx',
        'pydantic',
        'cc_storage',
    ] + collect_submodules('rich'),
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='cc-reddit',
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
)
