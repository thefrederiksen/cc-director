# -*- mode: python ; coding: utf-8 -*-

import os
from PyInstaller.utils.hooks import collect_data_files, collect_submodules

# rich dynamically imports _unicode_data modules at runtime
rich_hiddenimports = collect_submodules('rich._unicode_data')
cc_storage_path = os.path.abspath('../cc_storage')

a = Analysis(
    ['src\\cli.py'],
    pathex=['src', '../cc_storage'],
    binaries=[],
    datas=collect_data_files('rich') + [(cc_storage_path + '/*.py', 'cc_storage')],
    hiddenimports=rich_hiddenimports + ['cc_storage'],
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
    name='cc-linkedin',
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
