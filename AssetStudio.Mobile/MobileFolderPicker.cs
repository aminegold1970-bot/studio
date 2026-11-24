namespace AssetStudioMobile;

internal static class MobileFolderPicker
{
    public static Task<string?> PickAsync(AssetDocumentLibrary library)
    {
#if IOS || MACCATALYST
        return AssetStudioAppleFolderPicker.PickAsync(library);
#elif ANDROID
        return AssetStudioAndroidFolderPicker.PickAsync(library);
#elif WINDOWS
        return PickWindowsFolderAsync(library);
#else
        throw new PlatformNotSupportedException("Folder selection is not supported on this platform.");
#endif
    }

#if IOS || MACCATALYST
    private static class AssetStudioAppleFolderPicker
    {
        private static PickerDelegate? _activeDelegate;

        public static Task<string?> PickAsync(AssetDocumentLibrary library)
        {
            ArgumentNullException.ThrowIfNull(library);
            var completion = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var presenter = GetTopViewController();
                    if (presenter == null)
                        throw new InvalidOperationException("Could not find the active Apple view controller.");

                    var picker = new UIKit.UIDocumentPickerViewController(
                        new[] { "public.folder" },
                        UIKit.UIDocumentPickerMode.Open);
                    picker.AllowsMultipleSelection = false;
                    var pickerDelegate = new PickerDelegate(completion, library);
                    _activeDelegate = pickerDelegate;
                    picker.Delegate = pickerDelegate;
                    presenter.PresentViewController(picker, true, null);
                }
                catch (Exception exception)
                {
                    _activeDelegate = null;
                    completion.TrySetException(exception);
                }
            });

            return completion.Task;
        }

        private static UIKit.UIViewController? GetTopViewController()
        {
            var root = UIKit.UIApplication.SharedApplication.ConnectedScenes
                .OfType<UIKit.UIWindowScene>()
                .SelectMany(scene => scene.Windows)
                .FirstOrDefault(window => window.IsKeyWindow)?
                .RootViewController;

            while (root?.PresentedViewController != null)
                root = root.PresentedViewController;
            if (root is UIKit.UINavigationController navigation && navigation.VisibleViewController != null)
                root = navigation.VisibleViewController;
            if (root is UIKit.UITabBarController tabs && tabs.SelectedViewController != null)
                root = tabs.SelectedViewController;
            return root;
        }

        private sealed class PickerDelegate : UIKit.UIDocumentPickerDelegate
        {
            private readonly TaskCompletionSource<string?> _completion;
            private readonly AssetDocumentLibrary _library;
            private bool _finished;

            public PickerDelegate(
                TaskCompletionSource<string?> completion,
                AssetDocumentLibrary library)
            {
                _completion = completion;
                _library = library;
            }

            public override async void DidPickDocument(
                UIKit.UIDocumentPickerViewController controller,
                Foundation.NSUrl[] urls)
            {
                if (_finished)
                    return;
                _finished = true;
                try
                {
                    var url = urls?.FirstOrDefault()
                        ?? throw new IOException("The folder picker returned no folder.");
                    var hasAccess = url.StartAccessingSecurityScopedResource();
                    try
                    {
                        var source = url.Path;
                        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
                            throw new IOException($"iOS could not access the selected folder: {url.LastPathComponent}");

                        _completion.TrySetResult(await _library.ImportFolderAsync(source));
                    }
                    finally
                    {
                        if (hasAccess)
                            url.StopAccessingSecurityScopedResource();
                    }
                }
                catch (Exception exception)
                {
                    _completion.TrySetException(exception);
                }
                finally
                {
                    controller.DismissViewController(true, null);
                    _activeDelegate = null;
                }
            }

            public override void WasCancelled(UIKit.UIDocumentPickerViewController controller)
            {
                if (_finished)
                    return;
                _finished = true;
                controller.DismissViewController(true, null);
                _completion.TrySetResult(null);
                _activeDelegate = null;
            }
        }
    }
#endif

#if WINDOWS
    private static async Task<string?> PickWindowsFolderAsync(AssetDocumentLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var mauiWindow = Application.Current?.Windows.FirstOrDefault()
            ?? throw new InvalidOperationException("Could not find the active app window.");
        var nativeWindow = mauiWindow.Handler?.PlatformView as Microsoft.UI.Xaml.Window
            ?? throw new InvalidOperationException("The active app window is not a Windows window.");

        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow));
        var folder = await picker.PickSingleFolderAsync();
        return folder == null ? null : await library.ImportFolderAsync(folder.Path);
    }
#endif
}
