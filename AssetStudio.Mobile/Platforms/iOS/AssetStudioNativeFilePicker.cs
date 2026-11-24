#if IOS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui.Storage;
using UIKit;

namespace AssetStudioMobile;

/// <summary>Native iOS document picker used for importing arbitrary Unity files.</summary>
internal static class AssetStudioNativeFilePicker
{
    private static PickerDelegate? _activeDelegate;

    public static Task<List<FileResult>> PickAsync(
        bool allowMultiple,
        IProgress<MainPage.AssetLoadProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

                var pickerDelegate = new PickerDelegate(completion, progress, cancellationToken);
                _activeDelegate = pickerDelegate;
                picker.Delegate = pickerDelegate;
                presenter.PresentViewController(picker, true, null);
                var cancellationRegistration = cancellationToken.Register(() =>
                    MainThread.BeginInvokeOnMainThread(() => pickerDelegate.Cancel(picker)));
                _ = completion.Task.ContinueWith(
                    _ => cancellationRegistration.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
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
        private readonly IProgress<MainPage.AssetLoadProgress> _progress;
        private readonly CancellationToken _cancellationToken;
        private bool _finished;
        private int _lastProgressPercent;

        public PickerDelegate(
            TaskCompletionSource<List<FileResult>> completion,
            IProgress<MainPage.AssetLoadProgress> progress,
            CancellationToken cancellationToken)
        {
            _completion = completion;
            _progress = progress;
            _cancellationToken = cancellationToken;
        }

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls)
        {
            if (_finished) return;
            _finished = true;
            var selectedUrls = urls ?? Array.Empty<NSUrl>();
            var access = new bool[selectedUrls.Length];
            try
            {
                for (var index = 0; index < selectedUrls.Length; index++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    access[index] = selectedUrls[index].StartAccessingSecurityScopedResource();
                }
            }
            catch (OperationCanceledException)
            {
                StopAccess(selectedUrls, access);
                _completion.TrySetCanceled(_cancellationToken);
                Dismiss(controller);
                return;
            }
            catch (Exception exception)
            {
                StopAccess(selectedUrls, access);
                _completion.TrySetException(exception);
                Dismiss(controller);
                return;
            }

            var selectedDocuments = selectedUrls
                .Select(url => (Path: url.Path ?? string.Empty, DisplayName: url.LastPathComponent ?? "selected file"))
                .ToArray();
            Dismiss(controller);
            _ = CopySelectedDocumentsAsync(selectedUrls, access, selectedDocuments);
        }

        public override void WasCancelled(UIDocumentPickerViewController controller)
        {
            if (_finished) return;
            _finished = true;
            Dismiss(controller);
            _completion.TrySetResult(new List<FileResult>());
        }

        public void Cancel(UIDocumentPickerViewController controller)
        {
            if (_finished)
                return;
            _finished = true;
            Dismiss(controller);
            _completion.TrySetCanceled(_cancellationToken);
        }

        private async Task CopySelectedDocumentsAsync(
            NSUrl[] urls,
            bool[] access,
            (string Path, string DisplayName)[] selectedDocuments)
        {
            var copied = new List<FileResult>();
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.Combine(FileSystem.CacheDirectory, "AssetStudioPickerImports");
                Directory.CreateDirectory(directory);
                var sources = await Task.Run(() => selectedDocuments.Select(document =>
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var source = document.Path;
                    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                        throw new IOException($"iOS could not access the selected file: {document.DisplayName}");
                    return (Path: source, DisplayName: document.DisplayName, Length: new FileInfo(source).Length);
                }).ToArray(), _cancellationToken);
                var totalBytes = sources.Sum(source => source.Length);
                long copiedBytes = 0;

                for (var index = 0; index < sources.Length; index++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var source = sources[index];
                    var destination = Path.Combine(
                        directory, $"{Guid.NewGuid():N}_{Path.GetFileName(source.Path)}");
                    var completed = false;
                    ReportProgress(1, $"Copying iPhone file {index + 1:N0} of {sources.Length:N0}…");
                    try
                    {
                        await CopyFileAsync(source.Path, destination, source.Length, totalBytes, copiedBytes);
                        copied.Add(new FileResult(destination));
                        copiedBytes += source.Length;
                        completed = true;
                    }
                    finally
                    {
                        if (!completed && File.Exists(destination))
                            File.Delete(destination);
                    }
                }

                ReportProgress(4, $"Copied {copied.Count:N0} iPhone file(s); preparing asset load…");
                _completion.TrySetResult(copied);
            }
            catch (OperationCanceledException)
            {
                _completion.TrySetCanceled(_cancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                StopAccess(urls, access);
                _activeDelegate = null;
            }
        }

        private async Task CopyFileAsync(
            string sourcePath,
            string destinationPath,
            long fileLength,
            long totalBytes,
            long copiedBytesBefore)
        {
            const int bufferSize = 81920;
            var buffer = new byte[bufferSize];
            long copiedFromFile = 0;
            using (var source = new FileStream(
                       sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var destination = new FileStream(
                       destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(
                           buffer.AsMemory(0, buffer.Length), _cancellationToken)) > 0)
                {
                    await destination.WriteAsync(
                        buffer.AsMemory(0, bytesRead), _cancellationToken);
                    copiedFromFile += bytesRead;
                    var totalCopied = copiedBytesBefore + copiedFromFile;
                    var percent = totalBytes == 0
                        ? 4
                        : 1 + (int)Math.Min(3, (long)totalCopied * 3 / totalBytes);
                    ReportProgress(percent, $"Copying iPhone files · {totalCopied:N0}/{totalBytes:N0} bytes");
                }
                await destination.FlushAsync(_cancellationToken);
            }

            if (copiedFromFile != fileLength)
                throw new IOException($"The selected file changed while being copied: {sourcePath}");
            _cancellationToken.ThrowIfCancellationRequested();
        }

        private void Dismiss(UIDocumentPickerViewController controller)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                controller.DismissViewController(true, () => { }));
            _activeDelegate = null;
        }

        private void ReportProgress(int percent, string message)
        {
            if (percent <= _lastProgressPercent)
                return;
            _lastProgressPercent = percent;
            _progress.Report(new MainPage.AssetLoadProgress(percent, message));
        }

        private static void StopAccess(NSUrl[] urls, bool[] access)
        {
            for (var index = 0; index < urls.Length; index++)
            {
                if (access[index])
                    urls[index].StopAccessingSecurityScopedResource();
            }
        }
    }
}
#endif
