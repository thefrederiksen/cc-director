# Issue #1730 — setup wizard blank white window on the update path (Windows 11)

## The defect

On a Windows 11 machine that already had DevThrottle installed, launching
`devthrottle-setup-win-x64.exe` (the WPF setup wizard) opened a window titled **"DevThrottle
Update"** whose entire client area was **blank white** — sidebar, Welcome step, and the Next/Back
buttons never painted. Only the OS-drawn title bar and Close button appeared. The setup log ran
clean (`isUpdate=True`, latest version fetched, Welcome step built) and the Windows event log held
no exception, so the logic ran but nothing composited.

## Root cause

The Windows release asset is the **WPF** wizard (`tools/cc-director-setup`), not the Avalonia one.
WPF composites through Direct3D 9Ex handed to the Desktop Window Manager. On this machine that path
never presented a single frame, leaving DWM showing its cleared (white) surface — hence a white
client area with a working title bar. The Director, which uses Avalonia's separate GPU path, painted
perfectly on the same desktop at the same time, confirming the fault is WPF-specific, not the GPU or
the display.

## The fix

Force WPF software rendering at process startup, before the first window is created
(`tools/cc-director-setup/App.xaml.cs`):

```csharp
RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
```

The wizard is short-lived and visually simple, so software rendering costs nothing perceptible and
guarantees it paints on any GPU or driver.

## Proof (this machine, SORENLAPTOP, Windows 11, update path)

Both captures are direct `PrintWindow` (`PW_RENDERFULLCONTENT`) renders of the wizard's own window
handle, built from source with the release publish settings (self-contained, single-file, win-x64).

| File | What it shows |
|------|----------------|
| `screen-03-welcome.png` | Original field capture from #1712 — full desktop, blank white content. |
| `screen-06-printwindow.png` | Original `PrintWindow` render — proves the blank is real, not a capture artifact. |
| `proof-before-blank-from-source.png` | **Before the fix**, reproduced from a source build on this machine — the same blank white window. |
| `proof-after-fixed-welcome.png` | **After the fix** — the Welcome step paints fully: sidebar, step rail, DT logo, "Update DevThrottle", version text, install-type card, and the Next button. |

Only the `RenderMode.SoftwareOnly` line differs between the before and after builds. No update was
applied during capture — the wizard was launched, observed at step 1, and closed.
