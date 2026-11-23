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
                                     item.ItemProvider.HasItemConformingTo("public.item"));

    public override void PerformDrop(UIDropInteraction interaction, IUIDropSession session)
    {
        var providers = session.Items.Select(item => item.ItemProvider).ToArray();
        _ = CopyProvidersAsync(providers);
    }

    private static async Task CopyProvidersAsync(NSItemProvider[] providers)
    {
        var copied = new List<string>();
        foreach (var provider in providers)
        {
            var path = await CopyProviderAsync(provider);
            if (!string.IsNullOrWhiteSpace(path)) copied.Add(path);
        }

        if (copied.Count > 0)
            MainThread.BeginInvokeOnMainThread(() => MainPage.PublishDroppedFiles(copied));
    }

    private static Task<string?> CopyProviderAsync(NSItemProvider provider)
    {
        var type = provider.RegisteredTypeIdentifiers.FirstOrDefault(id =>
            provider.HasItemConformingTo(id) && (id == "public.data" || id == "public.item" || id.StartsWith("public.")));
        type ??= "public.data";
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.LoadFileRepresentation(type, (url, error) =>
        {
            try
            {
                if (error != null || url == null) { completion.TrySetResult(null); return; }
                var source = url.Path;
                if (string.IsNullOrEmpty(source) || !File.Exists(source)) { completion.TrySetResult(null); return; }
                var ext = Path.GetExtension(source);
                var target = Path.Combine(TemporaryDropDirectory, Guid.NewGuid().ToString("N") + ext);
                File.Copy(source, target, overwrite: false);
                completion.TrySetResult(target);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }
}
#endif
