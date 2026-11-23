#if IOS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui.Storage;
using UIKit;

namespace AssetStudioMobile;

/// <summary>Native iOS document picker used for importing arbitrary Unity files.</summary>
internal static class AssetStudioNativeFilePicker
{
    private static PickerDelegate? _activeDelegate;

    public static Task<List<FileResult>> PickAsync(bool allowMultiple)
    {
        var completion = new TaskCompletionSource<List<FileResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var presenter = GetTopViewController();
                if (presenter == null)
                    throw new InvalidOperationException("Could not find the active iOS view controller.");

                var picker = new UIDocumentPickerViewController(
                    new[] { "public.data", "public.item" }, UIDocumentPickerMode.Import);
                picker.AllowsMultipleSelection = allowMultiple;

                var pickerDelegate = new PickerDelegate(completion);
                _activeDelegate = pickerDelegate;
                picker.Delegate = pickerDelegate;
                presenter.PresentViewController(picker, true, null);
            }
            catch (Exception ex)
            {
                _activeDelegate = null;
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    private static UIViewController? GetTopViewController()
    {
        var root = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(window => window.IsKeyWindow)?
            .RootViewController;

        while (root?.PresentedViewController != null)
            root = root.PresentedViewController;
        if (root is UINavigationController navigation && navigation.VisibleViewController != null)
            root = navigation.VisibleViewController;
        if (root is UITabBarController tabs && tabs.SelectedViewController != null)
            root = tabs.SelectedViewController;
        return root;
    }

    private sealed class PickerDelegate : UIDocumentPickerDelegate
    {
        private readonly TaskCompletionSource<List<FileResult>> _completion;
        private bool _finished;

        public PickerDelegate(TaskCompletionSource<List<FileResult>> completion)
            => _completion = completion;

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls)
        {
            if (_finished) return;
            _finished = true;
            try
            {
                var copied = new List<FileResult>();
                var directory = Path.Combine(FileSystem.CacheDirectory, "AssetStudioPickerImports");
                Directory.CreateDirectory(directory);

                foreach (var url in urls ?? Array.Empty<NSUrl>())
                {
                    var hasAccess = url.StartAccessingSecurityScopedResource();
                    try
                    {
                        var source = url.Path;
                        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                            throw new IOException($"iOS could not access the selected file: {url.LastPathComponent}");

                        var destination = Path.Combine(directory, $"{Guid.NewGuid():N}_{Path.GetFileName(source)}");
                        File.Copy(source, destination, overwrite: false);
                        copied.Add(new FileResult(destination));
                    }
                    finally
                    {
                        if (hasAccess) url.StopAccessingSecurityScopedResource();
                    }
                }

                _completion.TrySetResult(copied);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
            finally
            {
                controller.DismissViewController(true, null);
                _activeDelegate = null;
            }
        }

        public override void WasCancelled(UIDocumentPickerViewController controller)
        {
            if (_finished) return;
            _finished = true;
            controller.DismissViewController(true, null);
            _completion.TrySetResult(new List<FileResult>());
            _activeDelegate = null;
        }
    }
}
#endif
