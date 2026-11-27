#if IOS
using System;
using System.IO;
using Foundation;
using Microsoft.Maui.Storage;

namespace AssetStudioMobile;

/// <summary>
/// Persists an iOS security-scoped bookmark for the folder the user dropped from Files.
/// The scope is held only while an export is copied into that folder.
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
