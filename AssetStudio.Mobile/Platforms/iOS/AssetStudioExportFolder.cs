#if IOS
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using UIKit;
using UniformTypeIdentifiers;

namespace AssetStudioMobile;

/// <summary>
/// Persists an iOS security-scoped bookmark for the folder chosen in Files or dropped
/// from Files. The scope is held only while an export is copied into that folder.
/// </summary>
internal static class IOSExportFolder
{
    private const string BookmarkKey = "AssetStudio.ExportFolderBookmark";
    private const string NameKey = "AssetStudio.ExportFolderName";

    public static string? DisplayName
    {
        get
        {
            var name = Preferences.Get(NameKey, string.Empty);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
    }

    public static void Set(string bookmarkBase64, string displayName)
    {
        if (string.IsNullOrWhiteSpace(bookmarkBase64))
            throw new ArgumentException("A folder bookmark is required.", nameof(bookmarkBase64));

        Preferences.Set(BookmarkKey, bookmarkBase64);
        Preferences.Set(NameKey, displayName);
    }

    public static void Clear()
    {
        Preferences.Remove(BookmarkKey);
        Preferences.Remove(NameKey);
    }

    // iPhone does not reliably support cross-app folder drag and drop, so the
    // same Files folder picker used by Load folder can also set the destination.
    public static Task<bool> PickAsync()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var presenter = GetTopViewController();
                if (presenter == null)
                    throw new InvalidOperationException("Could not find the active iOS view controller.");

                var folderType = UTType.CreateFromIdentifier("public.folder")
                    ?? throw new InvalidOperationException("iOS could not resolve the folder content type.");
                var picker = new UIDocumentPickerViewController(new[] { folderType }, false)
                {
                    AllowsMultipleSelection = false,
                    Delegate = new PickerDelegate(completion)
                };
                presenter.PresentViewController(picker, true, null);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
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
        return root;
    }

    private sealed class PickerDelegate : UIDocumentPickerDelegate
    {
        private readonly TaskCompletionSource<bool> _completion;
        private bool _finished;

        public PickerDelegate(TaskCompletionSource<bool> completion) => _completion = completion;

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls)
        {
            if (_finished)
                return;
            _finished = true;
            try
            {
                var url = urls?.FirstOrDefault()
                    ?? throw new IOException("The folder picker returned no folder.");
                var accessed = url.StartAccessingSecurityScopedResource();
                try
                {
                    var path = url.Path;
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                        throw new IOException($"iOS could not access the selected folder: {url.LastPathComponent}");

                    var data = url.CreateBookmarkData(
                        NSUrlBookmarkCreationOptions.WithSecurityScope, null, null, out var error);
                    if (error != null || data == null)
                        throw new IOException(error?.LocalizedDescription ?? "iOS could not save access to the selected folder.");

                    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                    Set(Convert.ToBase64String(data.ToArray()),
                        string.IsNullOrWhiteSpace(name) ? "Export folder" : name);
                    _completion.TrySetResult(true);
                }
                finally
                {
                    if (accessed)
                        url.StopAccessingSecurityScopedResource();
                }
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                MainThread.BeginInvokeOnMainThread(() => controller.DismissViewController(true, null));
            }
        }

        public override void WasCancelled(UIDocumentPickerViewController controller)
        {
            if (_finished)
                return;
            _finished = true;
            MainThread.BeginInvokeOnMainThread(() => controller.DismissViewController(true, null));
            _completion.TrySetResult(false);
        }
    }

    public static ScopedFolder? Resolve()
    {
        var bookmark = Preferences.Get(BookmarkKey, string.Empty);
        if (string.IsNullOrWhiteSpace(bookmark))
            return null;

        try
        {
            using var data = NSData.FromArray(Convert.FromBase64String(bookmark));
            var url = NSUrl.FromBookmarkData(
                data,
                NSUrlBookmarkResolutionOptions.WithSecurityScope | NSUrlBookmarkResolutionOptions.WithoutUI,
                null,
                out var isStale,
                out var error);
            if (error != null || url == null || !url.IsFileUrl)
                throw new IOException(error?.LocalizedDescription ?? "The saved export folder could not be resolved.");

            var path = url.Path;
            if (isStale || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                throw new DirectoryNotFoundException("The saved export folder is no longer available.");

            var hasAccess = url.StartAccessingSecurityScopedResource();
            return new ScopedFolder(url, path, DisplayName ?? Path.GetFileName(path) ?? "Export folder", hasAccess);
        }
        catch (Exception exception)
        {
            AssetStudio.Logger.Verbose($"Export folder bookmark unavailable: {exception.Message}");
            Clear();
            return null;
        }
    }

    internal sealed class ScopedFolder : IDisposable
    {
        private readonly NSUrl? _url;
        private bool _hasAccess;

        public ScopedFolder(NSUrl url, string path, string displayName, bool hasAccess)
        {
            _url = url;
            _hasAccess = hasAccess;
            Path = path;
            DisplayName = displayName;
        }

        public string Path { get; }
        public string DisplayName { get; }

        public void Dispose()
        {
            if (_hasAccess && _url != null)
            {
                _url.StopAccessingSecurityScopedResource();
                _hasAccess = false;
            }
            _url?.Dispose();
        }
    }
}
#endif
