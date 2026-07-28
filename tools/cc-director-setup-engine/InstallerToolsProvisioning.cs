namespace CcDirector.Setup.Engine;

/// <summary>
/// The installer's Python-tools provisioning step, extracted from the (Avalonia/WPF) wizard install
/// runners so the "snappy install" decision lives in ONE pinnable place away from the untestable
/// wizard bootstrap - the same seam discipline as <c>GatewayRefresh</c>.
///
/// In the snappy-install design the INSTALLER does NOT provision the shared-venv cc-* tools bundle:
/// downloading the ~334 MB bundle, building the venv, and offline pip-installing ~20 wheels is the
/// dominant install time ("3-8 min"). Instead the app provisions the bundle FROM NOTHING on first
/// launch via the startup reconcile (<see cref="ToolReconciler.ReconcileAsync"/>), so the install
/// places only the Director + Launcher and returns fast.
///
/// The real provisioner (<see cref="PythonToolsInstaller.InstallAsync"/>, wrapped by each wizard) is
/// passed in so a test can prove the installer NEVER invokes it.
/// </summary>
public static class InstallerToolsProvisioning
{
    /// <summary>
    /// Run the installer's tools step. Returns the number of cc-* tools the installer provisioned.
    /// By design this is 0 and <paramref name="provisionTools"/> is never invoked - the app provisions
    /// the tools on first launch. The real provisioner is accepted (and deliberately not called) so the
    /// removal is pinnable at the exact production line.
    /// </summary>
    public static Task<int> ProvisionDuringInstallAsync(
        Func<CancellationToken, Task<int>> provisionTools,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provisionTools);

        // SNAPPY INSTALL: the installer does NOT provision the shared-venv cc-* tools bundle. The app
        // provisions it from nothing on first launch (ToolReconciler startup reconcile), so the multi-
        // minute bundle download never runs during install. Reverting this to
        //     return provisionTools(ct);
        // re-introduces tool provisioning into the installer -> InstallerToolsProvisioningTests reds.
        return Task.FromResult(0);
    }
}
