# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec file for cc-posthog."""

import os
from pathlib import Path

block_cipher = None

# Get the spec file directory
spec_path = Path(SPECPATH)

# Get cc_shared path
cc_shared_path = os.path.abspath('../cc_shared')

a = Analysis(
    [str(spec_path / 'main.py')],
    pathex=[SPECPATH, str(spec_path / 'src'), cc_shared_path],
    binaries=[],
    datas=[
        (cc_shared_path + '/*.py', 'cc_shared'),
    ],
    hiddenimports=[
        'typer',
        'rich',
        'rich.console',
        'rich.table',
        'httpx',
        'httpx._transports',
        'httpx._transports.default',
        'httpcore',
        'h11',
        'certifi',
        'anyio',
        'anyio._backends',
        'anyio._backends._asyncio',
        'sniffio',
        'pydantic',
        'pydantic.deprecated',
        'pydantic.deprecated.decorator',
        'cli',
        'config',
        'posthog_api',
        'schema',
        'formatters',
        'time_range',
        'cc_storage',
        'cc_shared',
        'cc_shared.config',
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
    name='cc-posthog',
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
