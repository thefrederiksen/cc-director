# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec file for cc-image."""

import os
from pathlib import Path

block_cipher = None

# Get the spec file directory
spec_path = Path(SPECPATH)
cc_shared_path = os.path.abspath('../cc_shared')
venv_site_packages = os.path.join(SPECPATH, 'venv', 'Lib', 'site-packages')

a = Analysis(
    [str(spec_path / 'main.py')],
    pathex=[SPECPATH, str(spec_path / 'src'), cc_shared_path],
    binaries=[],
    datas=[
        (cc_shared_path + '/*.py', 'cc_shared'),
        (cc_shared_path + '/providers/*.py', 'cc_shared/providers'),
        (os.path.join(venv_site_packages, 'rich', '_unicode_data', '*.py'), 'rich/_unicode_data'),
    ],
    hiddenimports=[
        'typer',
        'rich',
        'rich.console',
        'rich.table',
        'rich.panel',
        'rich.text',
        'rich._unicode_data',
        'PIL',
        'PIL.Image',
        'requests',
        'openai',
        'tzdata',
        'cli',
        'generation',
        'vision',
        'manipulation',
        'cc_shared',
        'cc_shared.llm',
        'cc_shared.config',
        'cc_shared.providers',
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
    name='cc-image',
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
