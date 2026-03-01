# -*- mode: python ; coding: utf-8 -*-

import os
src_dir = 'src'

a = Analysis(
    ['main.py'],
    pathex=['.', 'src'],
    binaries=[],
    datas=[],
    hiddenimports=[
        'cli', 'config', 'db', 'vectors', 'chunker', 'converters', 'importer', 'rag', 'utils',
        'rich._unicode_data', 'rich._unicode_data.unicode17-0-0',
        'tiktoken_ext.openai_public',
        'tiktoken_ext',
    ],
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
    name='cc-vault',
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
