using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HistOSets.Core;
using HistOSets.Services;
using Path = System.IO.Path;

namespace HistOSets;

public partial class MainWindow : Window
{
    private readonly string dataDirectory;
    private AtlasCatalog? catalog;
    private LoadedImage? image;
    private AtlasSpecimen? CurrentSpecimen => ListOfSpecimens.SelectedItem as AtlasSpecimen;
    private AtlasElement? CurrentElement => ListOfElements.SelectedItem as AtlasElement;

    public MainWindow() : this(AppContext.BaseDirectory) { }

    public MainWindow(string dataDirectory)
    {
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        InitializeComponent();
        LoadCatalog();
    }

    private void LoadCatalog()
    {
        try
        {
            var next = AtlasLoader.Load(dataDirectory);
            catalog = next;
            ListOfSpecimens.ItemsSource = next.Specimens;
            CatalogCount.Text = $"Записей: {next.Specimens.Count}";
            WarningsButton.Visibility = next.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ListOfSpecimens.SelectedIndex = -1;
            ResetSpecimenView();
        }
        catch (AtlasLoadException ex)
        {
            ErrorLog.Write(ex);
            catalog = null;
            ListOfSpecimens.ItemsSource = null;
            CatalogCount.Text = "Каталог недоступен";
            WarningsButton.Visibility = Visibility.Collapsed;
            WelcomeLogo.Visibility = Visibility.Collapsed;
            ImageMessage.Text = "Не удалось загрузить каталог";
            ImageMessage.Visibility = Visibility.Visible;
            StatusText.Text = ex.Message + "\nИсправьте файл и нажмите «Обновить каталог».";
        }
    }

    private void ResetSpecimenView()
    {
        ListOfElements.ItemsSource = null;
        ClearPolygons();
        Specimen.Source = null;
        image = null;
        AboutS.Text = "";
        AboutE.Text = "";
        SInfo.IsEnabled = EInfo.IsEnabled = PreviewButton.IsEnabled = false;
        WelcomeLogo.Visibility = Visibility.Visible;
        ImageMessage.Visibility = Visibility.Collapsed;
        StatusText.Text = "Выберите препарат, затем его элемент. Для выбора достаточно одного щелчка.";
    }

    private void Specimen_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListOfElements is null) return;
        ResetSpecimenView();
        var current = CurrentSpecimen;
        if (current is null) return;
        WelcomeLogo.Visibility = Visibility.Collapsed;
        ImageMessage.Text = "Загрузка изображения…";
        ImageMessage.Visibility = Visibility.Visible;
        AboutS.Text = Fallback(current.Summary);
        SInfo.IsEnabled = !string.IsNullOrWhiteSpace(current.Description);
        ListOfElements.ItemsSource = current.Elements;
        try
        {
            image = ImageLoader.Load(current.ImagePath);
            Desk.Width = Specimen.Width = image.PixelWidth;
            Desk.Height = Specimen.Height = image.PixelHeight;
            Specimen.Source = image.Bitmap;
            ImageMessage.Visibility = Visibility.Collapsed;
            PreviewButton.IsEnabled = true;
            StatusText.Text = $"{image.PixelWidth} × {image.PixelHeight} пикселей. Элементов: {current.Elements.Count}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        {
            ErrorLog.Write(ex);
            ImageMessage.Text = "Изображение недоступно";
            StatusText.Text = $"Не удалось открыть «{Path.GetFileName(current.ImagePath)}»: {ex.Message}";
        }
    }

    private void Element_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Desk is null) return;
        ClearPolygons();
        var element = CurrentElement;
        AboutE.Text = element is null ? "" : Fallback(element.Summary);
        EInfo.IsEnabled = element is not null && !string.IsNullOrWhiteSpace(element.Description);
        if (element is null || image is null || CurrentSpecimen is null) return;
        foreach (var polygon in element.Polygons)
        {
            var points = polygon.Points.Select(p => ImageCoordinates.ToPixels(p, CurrentSpecimen.CoordinateSpace, image.DpiX, image.DpiY));
            Desk.Children.Add(new Polygon
            {
                Points = new PointCollection(points.Select(p => new Point(p.X, p.Y))),
                Fill = Brushes.Aqua,
                Opacity = 0.5,
                IsHitTestVisible = false
            });
        }
        StatusText.Text = element.Polygons.Count == 0
            ? "Для этого элемента разметка пока не добавлена."
            : $"{element.Name} — областей: {element.Polygons.Count}.";
    }

    private void ClearPolygons()
    {
        if (Desk is null) return;
        for (var i = Desk.Children.Count - 1; i >= 0; i--)
            if (Desk.Children[i] is Polygon) Desk.Children.RemoveAt(i);
    }

    private static string Fallback(string text) => string.IsNullOrWhiteSpace(text) ? "Описание пока не добавлено." : text;
    private void Reload_Click(object sender, RoutedEventArgs e) => LoadCatalog();
    private void SInfo_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSpecimen is { } s && SInfo.IsEnabled)
            new INFO2(s.Name, s.Description) { Owner = this }.Show();
    }
    private void EInfo_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentElement is { } element && EInfo.IsEnabled)
            new INFO2(element.Name, element.Description) { Owner = this }.Show();
    }
    private void Warnings_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is not null)
            new INFO2("Замечания к данным", string.Join("\n\n", catalog.Warnings)) { Owner = this }.Show();
    }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSpecimen is { } s && image is not null)
            new ViewerPreviewWindow(s, image, dataDirectory) { Owner = this }.Show();
    }
}
