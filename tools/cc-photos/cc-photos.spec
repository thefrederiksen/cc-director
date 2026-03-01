# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec file for cc-photos."""

import sys
import os
from pathlib import Path

block_cipher = None

# Get paths
src_path = Path('src')
cc_shared_path = os.path.abspath('../cc_shared')
cc-vault_path = os.path.abspath('../cc-vault/src')

a = Analysis(
    ['main.py'],
    pathex=[str(src_path), cc_shared_path, cc-vault_path],
    binaries=[],
    datas=[
        (cc_shared_path + '/*.py', 'cc_shared'),
        (cc_shared_path + '/providers/*.py', 'cc_shared/providers'),
        (cc-vault_path + '/*.py', 'cc-vault/src'),
    ],
    hiddenimports=[
        'cc_shared.providers',
        'PIL',
        'PIL.Image',
        'PIL.ExifTags',
        'pillow_heif',
        'typer',
        'rich',
        'rich.console',
        'rich.table',
        'rich.progress',
        'openai',
        'cc_shared',
        'cc_shared.config',
        'cc_shared.llm',
        'cc-vault',
        'cc-vault.src',
        'cc-vault.src.db',
        'cc-vault.src.config',
        'cc-vault.src.vectors',
        'cli',
        'database',
        'scanner',
        'duplicates',
        'analyzer',
        'hasher',
        'metadata',
        'screenshot',
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
    name='cc-photos',
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
