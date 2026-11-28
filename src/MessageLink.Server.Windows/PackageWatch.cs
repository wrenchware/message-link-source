using Windows.ApplicationModel;
using MessageLink.App.Services;

namespace MessageLink.Server.Windows;

/// <summary>
/// Watches this MSIX package's lifecycle so the server stops cleanly on uninstall or update even when no app window is open or
/// the deployment service does not close background processes on its own. Inactive outside a packaged run.
/// </summary>
internal sealed class PackageWatch : IDisposable
{
    private readonly PackageCatalog catalog;
    private readonly string fullName;
    private readonly Action<string> stop;
    private readonly Action<string> log;
    private int triggered;
    private int disposed;

    private PackageWatch(PackageCatalog catalog, string fullName, Action<string> stop, Action<string> log)
    {
        this.catalog = catalog;
        this.fullName = fullName;
        this.stop = stop;
        this.log = log;
        catalog.PackageUninstalling += OnUninstalling;
        catalog.PackageUpdating += OnUpdating;
        catalog.PackageStatusChanged += OnStatusChanged;
    }

    public static PackageWatch? Start(Action<string> stop, Action<string> log)
    {
        if (!AppPaths.IsPackaged) return null;
        try
        {
            var current = Package.Current;
            var fullName = current.Id.FullName;
            var catalog = PackageCatalog.OpenForCurrentPackage();
            var watch = new PackageWatch(catalog, fullName, stop, log);
            var status = current.Status;
            if (ShouldStopForStatus(status.DeploymentInProgress, status.Servicing))
                watch.Trigger("package_closing");
            return watch;
        }
        catch (Exception failure)
        {
            log($"server package watch unavailable type={failure.GetType().Name}");
            return null;
        }
    }

    internal static bool Matches(string currentFullName, string? eventFullName) =>
        !string.IsNullOrEmpty(eventFullName) && string.Equals(currentFullName, eventFullName, StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldStopForStatus(bool deploymentInProgress, bool servicing) => deploymentInProgress || servicing;

    private void OnUninstalling(PackageCatalog sender, PackageUninstallingEventArgs args)
    {
        try
        {
            if (Matches(fullName, args.Package?.Id?.FullName)) Trigger("package_uninstalling");
        }
        catch (Exception) { }
    }

    private void OnUpdating(PackageCatalog sender, PackageUpdatingEventArgs args)
    {
        try
        {
            if (Matches(fullName, args.SourcePackage?.Id?.FullName)) Trigger("package_updating");
        }
        catch (Exception) { }
    }

    private void OnStatusChanged(PackageCatalog sender, PackageStatusChangedEventArgs args)
    {
        try
        {
            var pkg = args.Package;
            if (pkg is not null && Matches(fullName, pkg.Id?.FullName) && ShouldStopForStatus(pkg.Status.DeploymentInProgress, pkg.Status.Servicing))
                Trigger("package_closing");
        }
        catch (Exception) { }
    }

    private void Trigger(string reason)
    {
        if (Interlocked.Exchange(ref triggered, 1) != 0) return;
        log($"server package event reason={reason}");
        stop(reason);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { catalog.PackageUninstalling -= OnUninstalling; } catch (Exception) { }
        try { catalog.PackageUpdating -= OnUpdating; } catch (Exception) { }
        try { catalog.PackageStatusChanged -= OnStatusChanged; } catch (Exception) { }
    }
}
