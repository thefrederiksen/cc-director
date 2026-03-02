# -*- mode: python ; coding: utf-8 -*-
#
# NOTE: crawl4ai depends on Playwright for browser automation.
# The browsers themselves are NOT bundled - run 'playwright install chromium'
# on the target system after deployment.

from PyInstaller.utils.hooks import collect_data_files, collect_submodules

a = Analysis(
    ['src\\cli.py'],
    pathex=[],
    binaries=[],
    datas=collect_data_files('rich'),
    hiddenimports=[
        'crawl4ai',
        'crawl4ai.async_webcrawler',
        'crawl4ai.async_configs',
        'crawl4ai.extraction_strategy',
        'crawl4ai.chunking_strategy',
        'crawl4ai.markdown_generation_strategy',
        'crawl4ai.content_scraping_strategy',
        'crawl4ai.browser_manager',
        'crawl4ai.models',
        'crawl4ai.utils',
        'playwright',
        'playwright.async_api',
        'playwright._impl',
        'patchright',
        'asyncio',
        'aiohttp',
        'bs4',
        'lxml',
        'typer',
        'rich',
        'rich.console',
        'rich.table',
        'rich.progress',
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
    name='cc-crawl4ai',
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
