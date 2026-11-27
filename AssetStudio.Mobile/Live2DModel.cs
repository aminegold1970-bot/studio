using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AssetStudioMobile;

/// <summary>
/// Parsing helpers for the in-app Live2D/Spine viewer. The app does not ship
/// the proprietary Cubism runtime, so this summarizes atlas and skeleton data
/// for a static preview instead of deforming meshes or playing animations.
/// </summary>
internal static class Live2DModel
{
    internal sealed record AtlasRegion(
        string Name, int X, int Y, int Width, int Height, bool Rotated);

    internal sealed record AtlasInfo(
        string PageName, int PageWidth, int PageHeight, IReadOnlyList<AtlasRegion> Regions);

    internal sealed record SkeletonInfo(
        string Kind, int BoneCount, int SlotCount, int AnimationCount,
        string BoneSummary, string SlotSummary, string AnimationSummary);

    // Repeatedly remove known container/document extensions so names such as
    // model.atlas.txt and model.skel.bytes compare as "model". Role suffixes
    // like model_atlas and model_skel are also treated as the same model.
    public static string Identity(string fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        string[] extensions = { ".txt", ".json", ".bytes", ".asset", ".atlas", ".skel", ".png", ".jpg", ".jpeg", ".tga" };
        string previous;
        do
        {
            previous = name;
            var extension = Path.GetExtension(name);
            if (!string.IsNullOrEmpty(extension)
                && extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(name);
        } while (!string.Equals(previous, name, StringComparison.Ordinal));

        foreach (var suffix in new[] { "_atlas", "_skel", "_skeleton", "_texture", "_tex", "_png" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }
        return name.Trim();
    }

    // Comparison key for model asset names. Unity imports sometimes keep role
    // suffixes or punctuation that Identity() cannot normalize, so compare the
    // alphanumeric form as well.
    public static string IdentityKey(string fileName)
    {
        var identity = Identity(fileName);
        var chars = identity.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    // Used when a bundled atlas has lost its original filename extension.
    public static bool LooksLikeAtlasText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        return lines.Any(line => line.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
            && lines.Any(line => line.StartsWith("xy:", StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildDetails(
        string textureName,
        (int Width, int Height) imageSize,
        AtlasInfo atlas,
        SkeletonInfo skeleton)
    {
        var builder = new StringBuilder();
        builder.Append("Texture: ").Append(textureName)
            .Append(" (").Append(imageSize.Width).Append('×').Append(imageSize.Height).AppendLine(")");
        builder.Append("Atlas: ").Append(atlas.PageName)
            .Append(" (").Append(atlas.PageWidth).Append('×').Append(atlas.PageHeight)
            .Append("), ").Append(atlas.Regions.Count).AppendLine(" region(s)");
        builder.Append("Skeleton: ").Append(skeleton.Kind)
            .Append(" · ").Append(skeleton.BoneCount).Append(" bone(s), ")
            .Append(skeleton.SlotCount).Append(" slot(s), ")
            .Append(skeleton.AnimationCount).AppendLine(" animation(s)");
        builder.AppendLine();
        builder.Append("Bones: ").AppendLine(skeleton.BoneSummary);
        builder.Append("Slots: ").AppendLine(skeleton.SlotSummary);
        builder.Append("Animations: ").AppendLine(skeleton.AnimationSummary);
        builder.AppendLine();
        builder.AppendLine("Atlas regions:");
        foreach (var region in atlas.Regions.Take(60))
        {
            builder.Append("• ").Append(region.Name)
                .Append(" @ ").Append(region.X).Append(',').Append(region.Y)
                .Append(' ').Append(region.Width).Append('×').Append(region.Height)
                .AppendLine(region.Rotated ? " (rotated)" : string.Empty);
        }
        if (atlas.Regions.Count > 60)
            builder.Append("… and ").Append(atlas.Regions.Count - 60).AppendLine(" more region(s).");
        return builder.ToString();
    }

    public static AtlasInfo ParseAtlas(string text)
    {
        var pageName = string.Empty;
        var pageWidth = 0;
        var pageHeight = 0;
        var regions = new List<AtlasRegion>();
        var blocks = new List<(string Header, Dictionary<string, string> Properties)>();
        string? blockHeader = null;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void FlushBlock()
        {
            if (blockHeader == null)
                return;
            blocks.Add((blockHeader, properties));
            blockHeader = null;
            properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // A Spine atlas contains an image/page header followed by indented
        // key/value lines, then one such block per region. Treating every
        // non-indented line as a region incorrectly listed format/filter lines.
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace("\0", string.Empty).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;
            var line = rawLine.Trim();
            if (line.StartsWith('#'))
                continue;
            if (!char.IsWhiteSpace(rawLine[0]))
            {
                FlushBlock();
                blockHeader = line;
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0 || blockHeader == null)
                continue;
            properties[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        FlushBlock();

        if (blocks.Count > 0)
        {
            // Cubism atlases may start with comment lines; find the real page
            // header instead of assuming the first block is the page.
            var pageIndex = blocks.FindIndex(block => LooksLikeImage(block.Header));
            if (pageIndex < 0)
                pageIndex = 0;
            var page = blocks[pageIndex];
            pageName = page.Header;
            if (page.Properties.TryGetValue("size", out var pageSize)
                && TryParsePair(pageSize, out pageWidth, out pageHeight))
            {
                // Values assigned by TryParsePair above.
            }
            else
            {
                pageWidth = pageHeight = 0;
            }

            foreach (var block in blocks.Skip(pageIndex + 1))
            {
                // Additional image headers indicate another atlas page, not a region.
                if (LooksLikeImage(block.Header))
                    continue;

                var x = 0;
                var y = 0;
                var width = 0;
                var height = 0;
                var rotated = false;
                if (block.Properties.TryGetValue("xy", out var xy))
                    TryParsePair(xy, out x, out y);
                if (block.Properties.TryGetValue("size", out var size))
                    TryParsePair(size, out width, out height);
                if (block.Properties.TryGetValue("rotate", out var rotate))
                    rotated = !rotate.Equals("false", StringComparison.OrdinalIgnoreCase) && rotate != "0";
                regions.Add(new AtlasRegion(block.Header, x, y, width, height, rotated));
            }
        }

        return new AtlasInfo(pageName, pageWidth, pageHeight, regions);
    }

    public static SkeletonInfo DescribeSkeleton(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return new SkeletonInfo("Empty skeleton data", 0, 0, 0,
                "No skeleton bytes were supplied.", "No skeleton bytes were supplied.", "No skeleton bytes were supplied.");

        var text = Encoding.UTF8.GetString(bytes).Replace("\0", string.Empty);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var root = JToken.Parse(text);
                var bones = (root["bones"] as JArray)?.Select(token => NameOf(token, "bone")).ToArray()
                            ?? Array.Empty<string>();
                var slots = (root["slots"] as JArray)?.Select(token => NameOf(token, "slot")).ToArray()
                            ?? Array.Empty<string>();
                var animations = root["animations"] is JObject animationsObject
                    ? animationsObject.Properties().Select(property => property.Name).ToArray()
                    : Array.Empty<string>();
                return new SkeletonInfo(
                    "JSON skeleton data",
                    bones.Length,
                    slots.Length,
                    animations.Length,
                    JoinNames(bones),
                    JoinNames(slots),
                    JoinNames(animations));
            }
            catch (Exception)
            {
                // A truncated JSON skeleton still has useful header bytes; fall
                // through to the binary summary instead of failing the viewer.
            }
        }

        var headerLength = Math.Min(4, bytes.Length);
        var header = Encoding.ASCII.GetString(bytes, 0, headerLength);
        var version = ReadNullTerminatedString(bytes, 1);
        var description = string.IsNullOrWhiteSpace(version)
            ? $"Binary skeleton data ({bytes.Length:N0} bytes, header \"{header}\")"
            : $"Binary skeleton data ({bytes.Length:N0} bytes, header \"{header}\", version \"{version}\")";
        return new SkeletonInfo(
            description,
            0, 0, 0,
            "Binary skeleton bones are not decoded by the static viewer.",
            "Binary skeleton slots are not decoded by the static viewer.",
            "Binary skeleton animations are not decoded by the static viewer.");
    }

    private static bool LooksLikeImage(string line)
    {
        var extension = Path.GetExtension(line);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".tga", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".psd", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParsePair(string value, out int first, out int second)
    {
        first = second = 0;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out first)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out second);
    }

    private static string ReadNullTerminatedString(byte[] bytes, int index)
    {
        if (bytes == null || index < 0 || index >= bytes.Length)
            return string.Empty;

        var start = index;
        while (index < bytes.Length && bytes[index] != 0)
            index++;
        var value = Encoding.ASCII.GetString(bytes, start, index - start).Trim();
        return value.Length <= 64 ? value : value[..64];
    }

    private static string NameOf(JToken token, string fallbackPrefix)
    {
        if (token is JValue value)
            return value.Value?.ToString() ?? fallbackPrefix;
        var name = token["name"]?.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"{fallbackPrefix}#{token.Path}" : name;
    }

    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count == 0
            ? "None"
            : string.Join(", ", names.Take(20)) + (names.Count > 20 ? $" … (+{names.Count - 20})" : string.Empty);
}
