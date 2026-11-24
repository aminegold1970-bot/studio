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

/// <summary>
/// Native iOS drop target for files dragged from Files.app and other providers.
/// iOS provider URLs are temporary, so each file is copied before the callback ends.
/// </summary>
internal sealed class AssetStudioDropBridge : UIDropInteractionDelegate
{
    private static readonly HashSet<UIView> AttachedViews = new();
    private static readonly List<AssetStudioDropBridge> RetainedDelegates = new();
    private static readonly string TemporaryDropDirectory =
        Path.Combine(FileSystem.CacheDirectory, "AssetStudioDrops");

    public static void Attach(UIView view)
    {
        if (view == null) return;

        // A page can be re-created during navigation/rotation. Keep the delegate
        // alive and avoid adding duplicate interactions to the same native view.
        if (!AttachedViews.Add(view)) return;

        Directory.CreateDirectory(TemporaryDropDirectory);
        var dropDelegate = new AssetStudioDropBridge();
        RetainedDelegates.Add(dropDelegate);
        view.AddInteraction(new UIDropInteraction(dropDelegate));
        view.UserInteractionEnabled = true;
    }

    public override bool CanHandleSession(UIDropInteraction interaction, IUIDropSession session)
    {
        // Files.app may advertise provider-specific UTIs rather than public.file-url.
        // Accept any provider with a representation and resolve the actual file in PerformDrop.
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
        _ = CopyProvidersAsync(providers);
    }

    internal static void CleanupTemporaryCopy(string path)
    {
        try
        {
            var root = Path.GetFullPath(TemporaryDropDirectory) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.Ordinal) || !File.Exists(fullPath))
                return;

            File.Delete(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory != null &&
                !string.Equals(directory, Path.GetFullPath(TemporaryDropDirectory), StringComparison.Ordinal) &&
                Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch { /* Cleanup must not interrupt importing another dropped file. */ }
    }

    private static async Task CopyProvidersAsync(NSItemProvider[] providers)
    {
        var copied = new List<string>();
        var failures = new List<string>();

        foreach (var provider in providers)
        {
            try { copied.Add(await CopyProviderAsync(provider)); }
            catch (Exception exception) { failures.Add(exception.Message); }
        }

        if (copied.Count > 0)
            MainThread.BeginInvokeOnMainThread(() => MainPage.PublishDroppedFiles(copied));

        if (failures.Count > 0 || copied.Count == 0)
        {
            var message = failures.Count == 0
                ? "The dropped item did not provide an accessible file."
                : string.Join(Environment.NewLine, failures);
            MainThread.BeginInvokeOnMainThread(() => MainPage.PublishDropFailure(message));
        }
    }

    private static Task<string> CopyProviderAsync(NSItemProvider provider)
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
}
#endif
