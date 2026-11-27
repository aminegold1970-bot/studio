namespace AssetStudioMobile;

/// <summary>
/// Runtime color helper for controls created in code. XAML uses AppThemeBinding
/// directly; these colors cannot use markup extensions because they are assigned
/// from C#, so they follow Application.Current.RequestedTheme instead.
/// </summary>
internal static class AssetStudioTheme
{
    private static readonly Dictionary<string, string> DarkColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#202020"] = "#F2F2F2",
        ["#FFFFFF"] = "#1E1E1E",
        ["#555555"] = "#B8B8B8",
        ["#E2E2E2"] = "#3A3A3A",
        ["#444444"] = "#D0D0D0",
        ["#F0F0F0"] = "#2C2C2C",
        ["#C8C8C8"] = "#3F3F3F",
        ["#B7B7B7"] = "#3A3A3A",
        ["#666666"] = "#A8A8A8",
        ["#777777"] = "#9A9A9A",
        ["#888888"] = "#9A9A9A",
        ["#2878C8"] = "#4FA3F7",
        ["#A7A5A6"] = "#2A2A2A",
        ["#E8E8E8"] = "#333333",
        ["#666466"] = "#B8B8B8",
        ["#D0D0D0"] = "#3A3A3A",
        ["#D6D6D6"] = "#3A3A3A",
        ["#333333"] = "#E8E8E8",
        ["#111111"] = "#F2F2F2",
        ["#F4F4F4"] = "#2C2C2C",
        ["#F7F7F7"] = "#2C2C2C"
    };

    public static Color FromHex(string light)
    {
        var dark = DarkColors.TryGetValue(light, out var mapped) ? mapped : light;
        return FromHex(light, dark);
    }

    public static Color FromHex(string light, string dark) =>
        Application.Current?.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb(dark)
            : Color.FromArgb(light);
}
