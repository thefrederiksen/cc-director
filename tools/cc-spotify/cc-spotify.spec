# -*- mode: python ; coding: utf-8 -*-

import os
from pathlib import Path
from PyInstaller.utils.hooks import collect_data_files, collect_submodules

spec_path = Path(SPECPATH)
tools_dir = spec_path.parent
cc_storage_path = str(tools_dir / 'cc_storage')

# Collect cc_storage .py files as data
cc_storage_files = []
for f in os.listdir(cc_storage_path):
    if f.endswith('.py'):
        cc_storage_files.append((os.path.join(cc_storage_path, f), 'cc_storage'))

a = Analysis(
    ['src\\__main__.py'],
    pathex=[str(spec_path), str(spec_path / 'src'), cc_storage_path],
    binaries=[],
    datas=collect_data_files('rich') + cc_storage_files,
    hiddenimports=[
        'typer',
        'rich',
        'httpx',
        'pydantic',
        'cc_storage',
        'cc_storage.storage',
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
    name='cc-spotify',
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
