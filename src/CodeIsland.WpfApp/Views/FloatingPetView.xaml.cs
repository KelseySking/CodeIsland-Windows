using CodeIsland.WpfApp.Services;

namespace CodeIsland.WpfApp.Views;

public partial class FloatingPetView
{
    private readonly WpfPetCatalogService _catalog;

    public FloatingPetView(WpfPetCatalogService catalog)
    {
        _catalog = catalog;
        InitializeComponent();
        ReloadDefaultPet();
    }

    public void ReloadDefaultPet()
    {
        var path = _catalog.DefaultPet?.SpritesheetPath;
        if (string.Equals(Sprite.AtlasPath, path, StringComparison.OrdinalIgnoreCase))
            Sprite.ReloadAtlas();
        else
            Sprite.AtlasPath = path;
    }

    public void PlayWave(Action completed) => Sprite.PlayWave(completed);

    public void PlayJump(Action completed) => Sprite.PlayJump(completed);

    public void SetDragDirection(double horizontalDelta) => Sprite.SetDragDirection(horizontalDelta);

    public void EndDrag() => Sprite.EndDrag();

    public bool IsVisiblePixelAt(System.Windows.Point localPoint) => Sprite.IsVisiblePixelAt(localPoint);
}
