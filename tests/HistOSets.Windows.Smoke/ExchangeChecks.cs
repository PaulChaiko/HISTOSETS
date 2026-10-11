using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using HistOSets;
using HistOSets.Core;
using HistOSets.Services;
using HistOSets.Storage;

internal static partial class Program
{
    private static void CheckExchangeWorkflow(string temp, string output)
    {
        // Reuse the ready object drawn by the native brush/eraser test, so the
        // exchange exercises real compound geometry, holes and WIC calibration.
        var sourceRoot = Path.Combine(temp, "advanced-editor-catalog");
        var targetRoot = Path.Combine(temp, "exchange-target");
        var packagePath = Path.Combine(temp, "selected-material.histosets-exchange");
        var conflictPath = Path.Combine(temp, "changed-material.histosets-exchange");
        var damagedPath = Path.Combine(temp, "damaged-material.histosets-exchange");
        File.WriteAllText(damagedPath, "Not an exchange archive");
        Guid materialId;
        Guid tagId;
        Guid analogId;
        MaterialDraft expected;
        using (var source = new CatalogStore(sourceRoot))
        {
            source.RenameCatalog("Коллекция для обмена");
            materialId = source.LoadMaterialIndex().Single().Id;
            var topic = source.LoadTaxonomy().Groups.Single(g => g.IsSystem && g.Code == "topic").Id;
            tagId = source.SaveTag(null, topic, "Обмен материалами", "Material exchange");
            analogId = source.SaveAnalogGroup(null, "Группа для обмена", "Exchange group");
            expected = source.LoadMaterialDraft(materialId);
            expected.TagIds.Add(tagId);
            expected.AnalogGroupId = analogId;
            expected = source.SaveMaterialDraft(expected, ImageLoader.ReadMetadata);
            var unselected = new MaterialDraft();
            unselected.Texts["ru"].Name = "Не выбран для экспорта";
            unselected.Texts["en"].Name = "Not selected for export";
            source.SaveMaterialDraft(unselected, ImageLoader.ReadMetadata);
        }
        using (var target = new CatalogStore(targetRoot)) target.RenameCatalog("Получатель материалов");

        var home = new HomeWindow(sourceDirectory: null, catalogDirectory: sourceRoot, rememberLocation: false);
        home.Show();
        try
        {
            WaitHome(home);
            if (Equals(((Button)home.FindName("ViewButton")).Content, "View")) Click(home, "LanguageButton");
            Check(((Button)home.FindName("ExchangeButton")).IsEnabled,
                "The home screen enables material exchange for the selected catalog");
            InModal<ExchangeWindow>(home, "ExchangeButton", exchange =>
            {
                WaitExchange(exchange);
                CompleteExchangeTask(exchange, exchange.ReloadExportIndexAsync());
                Check(((ItemsControl)exchange.FindName("ExportList")).Items.Count == 2,
                    "The native export chooser lists the ready source and the unselected draft");
                exchange.SelectForExport([materialId]);
                Check(((Button)exchange.FindName("ExportButton")).IsEnabled,
                    "Selecting a material enables exchange export");
                SaveScreenshot(exchange, Path.Combine(output, "native-exchange-export.png"));
                CompleteExchangeTask(exchange, exchange.ExportSelectedToPathAsync(packagePath));
                Check(File.Exists(packagePath), "The native export action writes the selected exchange package");
            });
        }
        finally { home.Close(); }
        using (var package = ExchangePackage.Open(packagePath))
            Check(package.Materials.Count == 1 && package.Materials.Single().Id == materialId,
                "Selective export includes only the chosen material UUID");

        var beforePreview = ExchangeCatalogSnapshot(targetRoot);
        home = new HomeWindow(sourceDirectory: null, catalogDirectory: targetRoot, rememberLocation: false);
        home.Show();
        try
        {
            WaitHome(home);
            InModal<ExchangeWindow>(home, "ExchangeButton", exchange =>
            {
                LoadExchangePackage(exchange, damagedPath);
                Check(exchange.Preview is null && !((Button)exchange.FindName("ApplyButton")).IsEnabled
                    && !string.IsNullOrWhiteSpace(((TextBlock)exchange.FindName("OperationStatus")).Text)
                    && ExchangeCatalogSnapshot(targetRoot) == beforePreview,
                    "An invalid exchange package shows a native error and cannot change the target catalog");
                LoadExchangePackage(exchange, packagePath);
                var preview = exchange.Preview ?? throw ExchangeFailure(exchange, "No import preview");
                Check(preview.Items.Single(i => i.Kind == ExchangeEntityKind.Material).Status == ExchangeChangeKind.Add
                    && ((Button)exchange.FindName("ApplyButton")).IsEnabled,
                    "Import preview identifies a new material and enables applying the complete graph");
                Check(ExchangeCatalogSnapshot(targetRoot) == beforePreview,
                    "Opening an import preview does not change the target SQLite database or catalog files");
                SelectExchangeItem(exchange, preview.Items.Single(i => i.Kind == ExchangeEntityKind.Material).Key);
                SaveScreenshot(exchange, Path.Combine(output, "native-exchange-import.png"));
            });
            Check(ExchangeCatalogSnapshot(targetRoot) == beforePreview
                && ((TextBlock)home.FindName("CatalogStatistics")).Text == "Объектов: 0",
                "Closing the exchange preview cancels import without changing the empty catalog");

            InModal<ExchangeWindow>(home, "ExchangeButton", exchange =>
            {
                LoadExchangePackage(exchange, packagePath);
                Click(exchange, "ApplyButton");
                WaitExchange(exchange);
                Check(exchange.Preview is null && !((Button)exchange.FindName("ApplyButton")).IsEnabled,
                    "Applying import clears the consumed preview and prevents applying it twice");
            });
            Check(((TextBlock)home.FindName("CatalogStatistics")).Text == "Объектов: 1",
                "Returning from import refreshes the home catalog count");

            var beforeRepeat = ExchangeCatalogSnapshot(targetRoot);
            InModal<ExchangeWindow>(home, "ExchangeButton", exchange =>
            {
                LoadExchangePackage(exchange, packagePath);
                Check(exchange.Preview is not null && exchange.Preview.Items.All(i => i.Status == ExchangeChangeKind.Unchanged),
                    "Reimport of the same package identifies all material and dictionary entries as unchanged");
                CompleteExchangeTask(exchange, exchange.ApplyImportAsync());
                Check(exchange.Preview is null && ExchangeCatalogSnapshot(targetRoot) == beforeRepeat,
                    "Applying an unchanged package creates no duplicate material, revision or extra backup");
            });
        }
        finally { home.Close(); }

        using (var target = new CatalogStore(targetRoot))
        {
            var imported = target.LoadMaterialDraft(materialId);
            Check(imported.Status == "ready" && imported.Texts["ru"].Name == expected.Texts["ru"].Name
                && imported.Texts["en"].Description == expected.Texts["en"].Description
                && imported.Images.Single().Id == expected.Images.Single().Id
                && imported.Elements.Single().Id == expected.Elements.Single().Id,
                "Native import preserves ready status, bilingual text and the material graph's UUIDs");
            Check(SameRegions(imported.Elements.Single().Regions.Select(r => new AtlasRegion(r.Id, r.Geometry)),
                    expected.Elements.Single().Regions.ToDictionary(r => r.Id, r => RegionGeometryCodec.Serialize(r.Geometry)))
                && imported.Images.Single().Calibration == expected.Images.Single().Calibration,
                "Native exchange preserves exact compound holes, independent regions and image calibration");
            Check(imported.TagIds.SetEquals([tagId]) && imported.AnalogGroupId == analogId
                && target.LoadTaxonomy().Tags.Single(t => t.Id == tagId).Names["en"] == "Material exchange"
                && target.LoadAnalogGroups().Single(g => g.Id == analogId).Names["ru"] == "Группа для обмена",
                "Native import retains tags, translated dictionary values and the analogue group");
            Check(File.Exists(imported.Images.Single().ManagedPath)
                && HashExchangeFile(imported.Images.Single().ManagedPath!) == HashExchangeFile(expected.Images.Single().ManagedPath!)
                && Directory.GetFiles(Path.Combine(targetRoot, "BACKUPS"), "before-exchange-*.histosets").Length == 1,
                "Import installs the verified original image and makes one full backup before the first change");
            imported.Texts["ru"].Name = "Локальная редакция";
            imported.Texts["en"].Name = "Local revision";
            target.SaveMaterialDraft(imported, ImageLoader.ReadMetadata);
        }
        using (var source = new CatalogStore(sourceRoot))
        {
            var changed = source.LoadMaterialDraft(materialId);
            changed.Texts["ru"].Name = "Входящая редакция";
            changed.Texts["en"].Name = "Incoming revision";
            source.SaveMaterialDraft(changed, ImageLoader.ReadMetadata);
            source.ExportMaterials(conflictPath, [materialId]);
        }

        home = new HomeWindow(sourceDirectory: null, catalogDirectory: targetRoot, rememberLocation: false);
        home.Show();
        try
        {
            WaitHome(home);
            var beforeConflict = ExchangeCatalogSnapshot(targetRoot);
            InModal<ExchangeWindow>(home, "ExchangeButton", exchange =>
            {
                WaitExchange(exchange);
                Click(exchange, "LanguageButton");
                WaitExchange(exchange);
                LoadExchangePackage(exchange, conflictPath);
                var item = exchange.Preview?.Items.Single(i => i.Kind == ExchangeEntityKind.Material)
                    ?? throw ExchangeFailure(exchange, "No material conflict preview");
                Check(item.Id == materialId && item.Status == ExchangeChangeKind.Conflict && item.RequiresDecision
                    && !((Button)exchange.FindName("ApplyButton")).IsEnabled,
                    "Independent edits of the same material UUID require an explicit conflict decision");
                Check(((Button)exchange.FindName("CloseButton")).Content is string closeText && closeText.Contains("Close", StringComparison.Ordinal)
                    && item.LocalSummary != item.IncomingSummary && ExchangeCatalogSnapshot(targetRoot) == beforeConflict,
                    "English conflict preview displays both different versions without changing catalog data");
                var conflictRow = SelectExchangeItem(exchange, item.Key);
                SaveScreenshot(exchange, Path.Combine(output, "native-exchange-conflict-en.png"));
                VisualChildren<ComboBox>(conflictRow).Single().SelectedValue = ExchangeResolution.UseIncoming;
                Pump();
                Check(((Button)exchange.FindName("ApplyButton")).IsEnabled,
                    "Choosing the incoming version resolves the conflict and enables apply");
                Click(exchange, "ApplyButton");
                WaitExchange(exchange);
                Check(exchange.Preview is null, "The resolved native conflict is applied successfully");
            });
            Check(Equals(((Button)home.FindName("ViewButton")).Content, "View"),
                "Changing language in exchange also updates the owner home screen");
            Click(home, "LanguageButton");
        }
        finally { home.Close(); }
        using (var target = new CatalogStore(targetRoot))
        {
            var merged = target.LoadMaterialDraft(materialId);
            Check(merged.Texts["ru"].Name == "Входящая редакция" && merged.Texts["en"].Name == "Incoming revision"
                && target.LoadMaterialIndex().Count == 1 && merged.Images.Single().Calibration == expected.Images.Single().Calibration,
                "The explicit incoming conflict resolution persists without duplicating or losing the object's calibration");
        }
    }

    private static void CompleteExchangeTask(ExchangeWindow exchange, Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump();
        if (!task.IsCompleted) throw ExchangeFailure(exchange, "Exchange operation timed out");
        task.GetAwaiter().GetResult();
        WaitExchange(exchange);
    }

    private static DataGridRow SelectExchangeItem(ExchangeWindow exchange, string key)
    {
        var grid = (DataGrid)exchange.FindName("PreviewList");
        grid.SelectedValuePath = "Item.Key";
        grid.SelectedValue = key;
        if (grid.SelectedItem is null) throw ExchangeFailure(exchange, "Exchange preview row is missing");
        grid.ScrollIntoView(grid.SelectedItem);
        grid.UpdateLayout();
        Pump();
        return grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem) as DataGridRow
            ?? throw ExchangeFailure(exchange, "Exchange preview row was not rendered");
    }

    private static void LoadExchangePackage(ExchangeWindow exchange, string path)
    {
        WaitExchange(exchange);
        CompleteExchangeTask(exchange, exchange.LoadPackagePathAsync(path));
    }

    private static void WaitExchange(ExchangeWindow exchange)
    {
        Pump();
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (exchange.IsBusy && DateTime.UtcNow < deadline) Pump();
        if (exchange.IsBusy) throw ExchangeFailure(exchange, "Exchange UI stayed busy");
        Pump();
    }

    private static Exception ExchangeFailure(ExchangeWindow exchange, string message) => new(
        message + ": " + ((TextBlock)exchange.FindName("OperationStatus")).Text);

    private static string HashExchangeFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ExchangeCatalogSnapshot(string root) => HashExchangeFile(Path.Combine(root, "catalog.sqlite"))
        + "\n" + string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file)).Order(StringComparer.Ordinal));
}
