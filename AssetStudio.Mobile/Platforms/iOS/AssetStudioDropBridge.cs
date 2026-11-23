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
/// Accepts files dragged from Files.app (and other document providers) onto
/// the app's visible page. The provider's temporary URL is copied immediately
/// because iOS may revoke it after the drop callback completes.
/// </summary>
internal sealed class AssetStudioDropBridge : UIDropInteractionDelegate
{
    private static readonly List<AssetStudioDropBridge> RetainedDelegates = new();
    private static readonly string TemporaryDropDirectory = Path.Combine(FileSystem.CacheDirectory, "AssetStudioDrops");

    public static void Attach(UIView view)
    {
        if (view.Interactions.OfType<UIDropInteraction>().Any()) return;
        Directory.CreateDirectory(TemporaryDropDirectory);
        var dropDelegate = new AssetStudioDropBridge();
        RetainedDelegates.Add(dropDelegate);
        view.AddInteraction(new UIDropInteraction(dropDelegate));
    }

    public override bool CanHandleSession(UIDropInteraction interaction, IUIDropSession session)
        => session.Items.Any(item => item.ItemProvider.HasItemConformingTo("public.data") ||
                                     item.ItemProvider.HasItemConformingTo("public.item") ||
                                     item.ItemProvider.HasItemConformingTo("public.file-url") ||
                                     item.ItemProvider.HasItemConformingTo("public.url"));

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
        var providers = session.Items.Select(item => item.ItemProvider).ToArray();
        _ = CopyProvidersAsync(providers);
    }

    internal static void CleanupTemporaryCopy(string path)
    {
        var root = Path.GetFullPath(TemporaryDropDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.Ordinal) || !File.Exists(fullPath))
            return;

        File.Delete(fullPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null && !string.Equals(
                directory,
                Path.GetFullPath(TemporaryDropDirectory),
                StringComparison.Ordinal)
            && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private static async Task CopyProvidersAsync(NSItemProvider[] providers)
    {
        var copied = new List<string>();
        var failures = new List<string>();
        foreach (var provider in providers)
        {
            try
            {
                copied.Add(await CopyProviderAsync(provider));
            }
            catch (Exception exception)
            {
                failures.Add(exception.Message);
            }
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
        var type = provider.RegisteredTypeIdentifiers.FirstOrDefault(id =>
            provider.HasItemConformingTo(id) &&
            (id == "public.data" || id == "public.item" || id == "public.file-url" ||
             id == "public.url" || id.StartsWith("public.", StringComparison.Ordinal)));
        type ??= "public.data";
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.LoadFileRepresentation(type, (url, error) =>
        {
            try
            {
                if (error != null)
                    throw new IOException($"iOS could not read the dropped file: {error.LocalizedDescription}");
                if (url == null || !url.IsFileUrl)
                    throw new IOException("iOS did not provide a local file for the dropped item.");

                var hasScopedAccess = url.StartAccessingSecurityScopedResource();
                try
                {
                    var source = url.Path;
                    if (string.IsNullOrEmpty(source) || !File.Exists(source))
                        throw new FileNotFoundException("The temporary file supplied by iOS is no longer available.");

                    var targetDirectory = Path.Combine(TemporaryDropDirectory, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(targetDirectory);
                    var target = Path.Combine(targetDirectory, Path.GetFileName(source));
                    File.Copy(source, target, overwrite: false);
                    completion.TrySetResult(target);
                }
                finally
                {
                    if (hasScopedAccess)
                        url.StopAccessingSecurityScopedResource();
                }
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }
}
#endif
