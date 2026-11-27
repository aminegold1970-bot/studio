#if IOS
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Foundation;
using UIKit;

namespace AssetStudioMobile;

internal enum AssetStudioDropTargetMode
{
    Load,
    ExportFolder
}

/// <summary>
/// Native iOS drop target for files and folders dragged from Files.app and other providers.
/// Provider URLs are temporary, so load drops are copied before the callback ends. Export
/// folder drops are persisted as security-scoped bookmarks so exports can write to the folder.
/// </summary>
internal sealed class AssetStudioDropBridge : UIDropInteractionDelegate
{
    private static readonly Dictionary<UIView, (AssetStudioDropTargetMode Mode, UIDropInteraction Interaction)> AttachedViews = new();
    private static readonly List<AssetStudioDropBridge> RetainedDelegates = new();
    private static readonly string TemporaryDropDirectory =
        Path.Combine(FileSystem.CacheDirectory, "AssetStudioDrops");

    internal static bool ExportDropTargetActive { get; set; }

    private readonly AssetStudioDropTargetMode _mode;

    private AssetStudioDropBridge(AssetStudioDropTargetMode mode) => _mode = mode;

    public static void Attach(UIView view, AssetStudioDropTargetMode mode = AssetStudioDropTargetMode.Load)
    {
        if (view == null) return;

        // A page can be re-created during navigation/rotation. Keep the delegate
        // alive and avoid adding duplicate interactions to the same native view.
        if (AttachedViews.TryGetValue(view, out var existing))
        {
            if (existing.Mode == mode)
                return;

            view.RemoveInteraction(existing.Interaction);
            AttachedViews.Remove(view);
        }

        Directory.CreateDirectory(TemporaryDropDirectory);
        var dropDelegate = new AssetStudioDropBridge(mode);
        var interaction = new UIDropInteraction(dropDelegate);
        AttachedViews[view] = (mode, interaction);
        RetainedDelegates.Add(dropDelegate);
        view.AddInteraction(interaction);
        view.UserInteractionEnabled = true;
    }

    public override bool CanHandleSession(UIDropInteraction interaction, IUIDropSession session)
    {
        if (_mode == AssetStudioDropTargetMode.Load && ExportDropTargetActive)
            return false;

        // Files.app may advertise provider-specific UTIs rather than public.file-url.
        // Accept any provider with a representation and resolve the actual item in PerformDrop.
        return session.Items.Any(item =>
            item?.ItemProvider != null &&
            item.ItemProvider.RegisteredTypeIdentifiers?.Length > 0);
    }

    public override void SessionDidEnter(UIDropInteraction interaction, IUIDropSession session)
        => MainPage.PublishDropTargetChanged(true);

    public override UIDropProposal SessionDidUpdate(UIDropInteraction interaction, IUIDropSession session)
        => new UIDropProposal(UIDropOperation.Copy);

    public override void SessionDidExit(UIDropInteraction interaction, IUIDropSession session)
        => MainPage.PublishDropTargetChanged(false);

    public override void SessionDidEnd(UIDropInteraction interaction, IUIDropSession session)
        => MainPage.PublishDropTargetChanged(false);

    public override void PerformDrop(UIDropInteraction interaction, IUIDropSession session)
    {
        MainPage.PublishDropTargetChanged(false);
        var providers = session.Items
            .Where(item => item?.ItemProvider != null)
            .Select(item => item.ItemProvider)
            .ToArray();
        _ = CopyProvidersAsync(providers, _mode);
    }

    internal static void CleanupTemporaryCopy(string path)
    {
        try
        {
            var root = Path.GetFullPath(TemporaryDropDirectory) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.Ordinal))
                return;

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, true);
                return;
            }

            if (!File.Exists(fullPath))
                return;

            File.Delete(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory != null &&
                !string.Equals(directory, Path.GetFullPath(TemporaryDropDirectory), StringComparison.Ordinal) &&
                Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch { /* Cleanup must not interrupt importing another dropped item. */ }
    }

    private static async Task CopyProvidersAsync(
        NSItemProvider[] providers,
        AssetStudioDropTargetMode mode)
    {
        var copied = new List<string>();
        var failures = new List<string>();
        var exportFolderSet = false;

        foreach (var provider in providers)
        {
            try
            {
                if (mode == AssetStudioDropTargetMode.ExportFolder)
                {
                    var bookmark = await CreateExportFolderBookmarkAsync(provider);
                    MainThread.BeginInvokeOnMainThread(() =>
                        MainPage.PublishDroppedExportFolder(bookmark.Bookmark, bookmark.Name));
                    exportFolderSet = true;
                    break;
                }

                copied.Add(await CopyProviderAsync(provider));
            }
            catch (Exception exception) { failures.Add(exception.Message); }
        }

        if (copied.Count > 0)
            MainThread.BeginInvokeOnMainThread(() => MainPage.PublishDroppedFiles(copied));

        if (failures.Count > 0 || (copied.Count == 0 && !exportFolderSet))
        {
            var message = failures.Count == 0
                ? mode == AssetStudioDropTargetMode.ExportFolder
                    ? "Drop a folder from the Files app to set the export folder."
                    : "The dropped item did not provide an accessible file or folder."
                : string.Join(Environment.NewLine, failures);
            MainThread.BeginInvokeOnMainThread(() => MainPage.PublishDropFailure(message));
        }
    }

    private static async Task<string> CopyProviderAsync(NSItemProvider provider)
    {
        var url = await ResolveFileUrlAsync(provider);
        if (url != null && TryCopyFolderUrl(url, out var folderPath))
            return folderPath;
        return await CopyFileProviderAsync(provider);
    }

    private static bool TryCopyFolderUrl(NSUrl url, out string folderPath)
    {
        folderPath = string.Empty;

        var accessed = url.StartAccessingSecurityScopedResource();
        try
        {
            var source = url.Path;
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
                return false;

            var name = GetPathLeaf(source);
            var target = Path.Combine(TemporaryDropDirectory, Guid.NewGuid().ToString("N"), name);
            CopyDirectory(source, target);
            folderPath = target;
            return true;
        }
        finally
        {
            if (accessed)
                url.StopAccessingSecurityScopedResource();
        }
    }

    private static Task<string> CopyFileProviderAsync(NSItemProvider provider)
    {
        var registered = provider.RegisteredTypeIdentifiers ?? Array.Empty<string>();
        // Prefer a file URL or a file-like representation. Avoid selecting a
        // generic public.data representation when the provider exposes a file UTI.
        var type = registered.FirstOrDefault(id => id == "public.file-url")
            ?? registered.FirstOrDefault(id => id == "public.item")
            ?? registered.FirstOrDefault(id => id == "public.content")
            ?? registered.FirstOrDefault(id => id == "public.data")
            ?? registered.FirstOrDefault(id => id == "public.url")
            ?? registered.FirstOrDefault();

        if (string.IsNullOrEmpty(type))
            throw new IOException("The dropped item has no readable file representation.");

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.LoadFileRepresentation(type, (url, error) =>
        {
            try
            {
                if (error != null)
                    throw new IOException($"iOS could not read the dropped file: {error.LocalizedDescription}");
                if (url == null || !url.IsFileUrl)
                    throw new IOException("The file provider did not return a local file.");

                var source = url.Path;
                if (string.IsNullOrEmpty(source) || !File.Exists(source))
                    throw new FileNotFoundException("The temporary file supplied by iOS is unavailable.");

                var targetDirectory = Path.Combine(TemporaryDropDirectory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(targetDirectory);
                var fileName = Path.GetFileName(source);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "DroppedAsset.bin";
                var target = Path.Combine(targetDirectory, fileName);
                File.Copy(source, target, overwrite: false);
                completion.TrySetResult(target);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private static async Task<NSUrl?> ResolveFileUrlAsync(NSItemProvider provider)
    {
        var registered = provider.RegisteredTypeIdentifiers ?? Array.Empty<string>();
        var types = new[] { "public.file-url", "public.folder", "public.url" }
            .Where(registered.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var type in types)
        {
            try
            {
                using var data = await provider.LoadDataRepresentationAsync(type);
                if (data == null)
                    continue;
                var url = NSUrl.CreateWithDataRepresentation(data, null);
                if (url?.IsFileUrl == true)
                    return url;
            }
            catch { /* Try the next URL representation. */ }
        }
        return null;
    }

    private static async Task<(string Bookmark, string Name)> CreateExportFolderBookmarkAsync(
        NSItemProvider provider)
    {
        var url = await ResolveFileUrlAsync(provider)
            ?? throw new IOException("The dropped item did not provide a folder URL.");
        var accessed = url.StartAccessingSecurityScopedResource();
        try
        {
            var path = url.Path;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                throw new IOException("Drop a folder from the Files app to set the export folder.");

            var data = url.CreateBookmarkData(
                NSUrlBookmarkCreationOptions.WithSecurityScope, null, null, out var error);
            if (error != null || data == null)
                throw new IOException(error?.LocalizedDescription ?? "iOS could not save access to the dropped folder.");

            return (Convert.ToBase64String(data.ToArray()), GetPathLeaf(path));
        }
        finally
        {
            if (accessed)
                url.StopAccessingSecurityScopedResource();
        }
    }

    private static string GetPathLeaf(string path)
    {
        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(leaf) ? "Dropped folder" : leaf;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
#endif
