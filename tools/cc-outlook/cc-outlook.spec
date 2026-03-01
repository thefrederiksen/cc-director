# -*- mode: python ; coding: utf-8 -*-

import os

src_dir = 'src'
cc_shared_path = os.path.abspath('../cc_shared')
cc_storage_path = os.path.abspath('../cc_storage')

a = Analysis(
    ['main.py'],
    pathex=['.', 'src', '../cc_shared', '../cc_storage'],
    binaries=[],
    datas=[
        (cc_shared_path + '/*.py', 'cc_shared'),
        (cc_shared_path + '/providers/*.py', 'cc_shared/providers'),
        (cc_storage_path + '/*.py', 'cc_storage'),
    ],
    hiddenimports=['cli', 'auth', 'outlook_api', 'utils', 'cc_shared', 'cc_shared.config', 'cc_shared.llm', 'cc_shared.providers', 'cc_storage', 'rich._unicode_data', 'rich._unicode_data.unicode17-0-0'],
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
    name='cc-outlook',
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
