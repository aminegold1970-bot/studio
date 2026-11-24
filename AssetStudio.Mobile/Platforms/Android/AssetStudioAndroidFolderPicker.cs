#if ANDROID
using Android.Content;
using Android.Provider;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace AssetStudioMobile;

internal static class AssetStudioAndroidFolderPicker
{
    public static async Task<string?> PickAsync(AssetDocumentLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var treeUri = await MainActivity.PickFolderUriAsync();
        if (treeUri == null)
            return null;

        var resolver = Platform.AppContext.ContentResolver
            ?? throw new InvalidOperationException("Android did not provide a content resolver.");
        var staging = Path.Combine(FileSystem.CacheDirectory, $"AssetStudioFolder_{Guid.NewGuid():N}");
        return await Task.Run(async () =>
        {
            Directory.CreateDirectory(staging);
            try
            {
                var rootDocumentId = DocumentsContract.GetTreeDocumentId(treeUri);
                if (string.IsNullOrWhiteSpace(rootDocumentId))
                    throw new InvalidDataException("The selected Android folder has no document identifier.");

                var rootUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, rootDocumentId);
                var rootName = ReadDisplayName(resolver, rootUri);
                var rootPath = Path.Combine(staging, SafeSegment(rootName));
                Directory.CreateDirectory(rootPath);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                await CopyChildrenAsync(resolver, treeUri, rootDocumentId, rootPath, visited);
                return await library.ImportFolderAsync(rootPath);
            }
            finally
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
        });
    }

    private static async Task CopyChildrenAsync(
        ContentResolver resolver,
        Android.Net.Uri treeUri,
        string directoryId,
        string destination,
        HashSet<string> visited)
    {
        if (!visited.Add(directoryId))
            throw new InvalidDataException("The selected Android folder contains a directory cycle.");

        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, directoryId);
        var projection = new[]
        {
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType
        };
        using var cursor = resolver.Query(childrenUri, projection, null, null, null)
            ?? throw new IOException("Android could not list the selected folder.");

        var idColumn = cursor.GetColumnIndexOrThrow(DocumentsContract.Document.ColumnDocumentId);
        var nameColumn = cursor.GetColumnIndexOrThrow(DocumentsContract.Document.ColumnDisplayName);
        var mimeColumn = cursor.GetColumnIndexOrThrow(DocumentsContract.Document.ColumnMimeType);
        while (cursor.MoveToNext())
        {
            var documentId = cursor.GetString(idColumn);
            var displayName = cursor.GetString(nameColumn);
            var mimeType = cursor.GetString(mimeColumn);
            if (string.IsNullOrWhiteSpace(documentId))
                throw new InvalidDataException("Android returned a folder entry without a document identifier.");

            var entryPath = Path.Combine(destination, SafeSegment(displayName));
            if (string.Equals(mimeType, DocumentsContract.Document.MimeTypeDir, StringComparison.Ordinal))
            {
                Directory.CreateDirectory(entryPath);
                await CopyChildrenAsync(resolver, treeUri, documentId, entryPath, visited);
                continue;
            }

            var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId);
            using var input = resolver.OpenInputStream(documentUri)
                ?? throw new IOException($"Android could not open '{displayName}'.");
            await using var output = new FileStream(
                entryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output);
        }
    }

    private static string ReadDisplayName(ContentResolver resolver, Android.Net.Uri documentUri)
    {
        using var cursor = resolver.Query(
            documentUri,
            new[] { DocumentsContract.Document.ColumnDisplayName },
            null,
            null,
            null) ?? throw new IOException("Android could not read the selected folder name.");
        if (!cursor.MoveToFirst())
            throw new IOException("Android returned no details for the selected folder.");

        return SafeSegment(cursor.GetString(0));
    }

    private static string SafeSegment(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "UnityAssetFolder" : value;
        var safe = string.Concat(name.Select(character =>
            character is '/' or '\\' or ':' || Path.GetInvalidFileNameChars().Contains(character)
                ? '_'
                : character));
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
            throw new InvalidDataException("Android returned an invalid folder entry name.");
        return safe;
    }
}
#endif
