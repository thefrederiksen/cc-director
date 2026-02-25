# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec file for cc_director_service

import sys
from pathlib import Path

block_cipher = None

# Get the scheduler directory
scheduler_dir = Path(SPECPATH)

a = Analysis(
    ['main.py'],
    pathex=[str(scheduler_dir)],
    binaries=[],
    datas=[
        # Include gateway templates and static files
        ('cc_director/gateway/templates', 'cc_director/gateway/templates'),
        ('cc_director/gateway/static', 'cc_director/gateway/static'),
    ],
    hiddenimports=[
        # Core modules
        'cc_director',
        'cc_director.service',
        'cc_director.scheduler',
        'cc_director.config',
        'cc_director.database',
        'cc_director.executor',
        'cc_director.cron',
        # Dispatcher modules
        'cc_director.dispatcher',
        'cc_director.dispatcher.config',
        'cc_director.dispatcher.dispatcher',
        'cc_director.dispatcher.email_sender',
        'cc_director.dispatcher.watcher',
        # Gateway modules
        'cc_director.gateway',
        'cc_director.gateway.app',
        'cc_director.gateway.routes',
        'cc_director.gateway.routes.jobs',
        'cc_director.gateway.routes.runs',
        'cc_director.gateway.routes.system',
        'cc_director.gateway.routes.websocket',
        # Dependencies
        'click',
        'croniter',
        'fastapi',
        'uvicorn',
        'uvicorn.logging',
        'uvicorn.loops',
        'uvicorn.loops.auto',
        'uvicorn.protocols',
        'uvicorn.protocols.http',
        'uvicorn.protocols.http.auto',
        'uvicorn.protocols.websockets',
        'uvicorn.protocols.websockets.auto',
        'uvicorn.lifespan',
        'uvicorn.lifespan.on',
        'starlette',
        'starlette.routing',
        'starlette.middleware',
        'jinja2',
        'watchdog',
        'watchdog.observers',
        'watchdog.events',
        'websockets',
        'asyncio',
        'sqlite3',
        'json',
        'logging',
        'threading',
        'concurrent.futures',
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
    name='cc_director_service',
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
