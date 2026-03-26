using UIKit;
using Foundation;
using SwiftBindingsSamples.Models;

namespace SwiftBindingsSamples.UI;

public class SampleViewController : UIViewController
{
    private readonly ILibrarySample _sample;

    public SampleViewController(ILibrarySample sample)
    {
        _sample = sample;
        Title = sample.LibraryName;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        View!.BackgroundColor = UIColor.SystemBackground;

        var scrollView = new UIScrollView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        View.AddSubview(scrollView);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            scrollView.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor),
            scrollView.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
            scrollView.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
            scrollView.BottomAnchor.ConstraintEqualTo(View.BottomAnchor),
        });

        var contentWidth = View.Bounds.Width - 32;
        var sampleView = _sample.CreateSampleView(contentWidth);
        sampleView.TranslatesAutoresizingMaskIntoConstraints = false;
        scrollView.AddSubview(sampleView);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            sampleView.TopAnchor.ConstraintEqualTo(scrollView.TopAnchor, 16),
            sampleView.LeadingAnchor.ConstraintEqualTo(scrollView.LeadingAnchor, 16),
            sampleView.TrailingAnchor.ConstraintEqualTo(scrollView.TrailingAnchor, -16),
            sampleView.BottomAnchor.ConstraintEqualTo(scrollView.BottomAnchor, -16),
            sampleView.WidthAnchor.ConstraintEqualTo(contentWidth),
        });
    }
}
