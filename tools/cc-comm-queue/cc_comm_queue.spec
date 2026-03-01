# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec for cc_comm_queue."""

import os
import sys
from PyInstaller.utils.hooks import collect_data_files

block_cipher = None

# SPECPATH is defined by PyInstaller and points to the directory containing the spec file
spec_dir = SPECPATH

# Add src to the path for module discovery
src_path = os.path.join(spec_dir, 'src')

# Collect source files to be bundled (as data that we'll import at runtime)
src_files = []
src_dir_full = os.path.join(spec_dir, 'src')
for f in os.listdir(src_dir_full):
    if f.endswith('.py') and f not in ['cli.py', '__main__.py']:
        src_files.append((os.path.join(src_dir_full, f), '.'))

# Collect Rich Unicode data files
rich_unicode_data = collect_data_files('rich._unicode_data', include_py_files=True)

# Runtime hook path
runtime_hook_path = os.path.join(spec_dir, 'pyi_rth_paths.py')

a = Analysis(
    [os.path.join(src_dir_full, 'cli.py')],
    pathex=[src_path, spec_dir],
    binaries=[],
    datas=src_files + rich_unicode_data,
    hiddenimports=[
        'typer',
        'typer.core',
        'typer.main',
        'rich',
        'rich.console',
        'rich.table',
        'pydantic',
        'pydantic.deprecated.decorator',
        'pydantic_core',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[runtime_hook_path],
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
    name='cc_comm_queue',
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
