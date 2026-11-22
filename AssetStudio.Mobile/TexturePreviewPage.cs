namespace AssetStudioMobile;

internal sealed class TexturePreviewPage : ContentPage
{
    public TexturePreviewPage(string title, string imagePath)
    {
        Title = title;
        ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));
        Content = new Image
        {
            Source = ImageSource.FromFile(imagePath),
            Aspect = Aspect.AspectFit,
            Margin = 12
        };
    }
}
