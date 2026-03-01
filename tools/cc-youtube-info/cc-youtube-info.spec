# -*- mode: python ; coding: utf-8 -*-


a = Analysis(
    ['src\\cli.py'],
    pathex=[],
    binaries=[],
    datas=[],
    hiddenimports=[
        'yt_dlp',
        'yt_dlp.extractor',
        'yt_dlp.extractor.youtube',
        'yt_dlp.downloader',
        'yt_dlp.postprocessor',
        'youtube_transcript_api',
        'youtube_transcript_api._api',
        'youtube_transcript_api._errors',
        'youtube_transcript_api._transcripts',
        'typer',
        'rich',
        'rich.console',
        'rich.table',
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
    name='cc-youtube-info',
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
