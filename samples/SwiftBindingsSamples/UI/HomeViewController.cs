using UIKit;
using Foundation;
using SwiftBindingsSamples.Models;
using SwiftBindingsSamples.Samples;

namespace SwiftBindingsSamples.UI;

public class HomeViewController : UITableViewController
{
    private const string CellId = "SampleCell";

    private readonly ILibrarySample[] _samples =
    [
        new NukeSample(),
        new LottieSample(),
    ];

    public HomeViewController() : base(UITableViewStyle.InsetGrouped)
    {
        Title = "SwiftBindings Samples";
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RegisterClassForCellReuse(typeof(UITableViewCell), CellId);
        NavigationController!.NavigationBar.PrefersLargeTitles = true;
    }

    public override nint NumberOfSections(UITableView tableView) => 1;

    public override nint RowsInSection(UITableView tableView, nint section) => _samples.Length;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var cell = tableView.DequeueReusableCell(CellId, indexPath);
        var sample = _samples[indexPath.Row];

        var config = UIListContentConfiguration.SubtitleCellConfiguration;
        config.Text = sample.LibraryName;
        config.SecondaryText = $"{sample.PackageName} {sample.Version} — {sample.Description}";

        cell.ContentConfiguration = config;
        cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;

        return cell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        var sample = _samples[indexPath.Row];
        var vc = new SampleViewController(sample);
        NavigationController!.PushViewController(vc, true);
    }
}
