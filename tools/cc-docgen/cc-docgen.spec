# -*- mode: python ; coding: utf-8 -*-

from pathlib import Path

spec_path = Path(SPECPATH)

a = Analysis(
    ['main.py'],
    pathex=[str(spec_path)],
    binaries=[],
    datas=[],
    hiddenimports=[
        'cli',
        'generator',
        'schema',
        'diagrams',
        'diagrams.c4',
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
    name='cc-docgen',
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
