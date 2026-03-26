using UIKit;
using Foundation;
using System.Diagnostics;
using SwiftBindingsSamples.Models;
using Nuke;

namespace SwiftBindingsSamples.Samples;

public class NukeSample : ILibrarySample
{
    public string LibraryName => "Nuke";
    public string PackageName => "SwiftBindings.Nuke";
    public string Version => "12.8.0";
    public string Description => "Async image loading, caching, and pipeline management";

    public UIView CreateSampleView(nfloat width)
    {
        var container = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 16,
            Alignment = UIStackViewAlignment.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        // Header
        var header = new UILabel
        {
            Text = "Image Loading with Nuke",
            Font = UIFont.BoldSystemFontOfSize(18),
            TextColor = UIColor.Label,
        };
        container.AddArrangedSubview(header);

        // Description
        var desc = new UILabel
        {
            Text = "Nuke's ImagePipeline loads images asynchronously with automatic memory and disk caching. "
                 + "Tap \"Load Images\" to fetch from network, then \"Reload\" to see cache performance.",
            Font = UIFont.SystemFontOfSize(14),
            TextColor = UIColor.SecondaryLabel,
            Lines = 0,
        };
        container.AddArrangedSubview(desc);

        // Image grid (2x2)
        var imageUrls = new[]
        {
            "https://picsum.photos/seed/nuke1/300/200",
            "https://picsum.photos/seed/nuke2/300/200",
            "https://picsum.photos/seed/nuke3/300/200",
            "https://picsum.photos/seed/nuke4/300/200",
        };

        var imageViews = new UIImageView[4];
        var gridContainer = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        var imageSize = (width - 16) / 2;

        for (int i = 0; i < 4; i++)
        {
            var imageView = new UIImageView
            {
                ContentMode = UIViewContentMode.ScaleAspectFill,
                BackgroundColor = UIColor.TertiarySystemFill,
                ClipsToBounds = true,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            imageView.Layer.CornerRadius = 8;
            imageViews[i] = imageView;
            gridContainer.AddSubview(imageView);

            int row = i / 2, col = i % 2;
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                imageView.TopAnchor.ConstraintEqualTo(gridContainer.TopAnchor, row * (imageSize + 16)),
                imageView.LeadingAnchor.ConstraintEqualTo(gridContainer.LeadingAnchor, col * (imageSize + 16)),
                imageView.WidthAnchor.ConstraintEqualTo(imageSize),
                imageView.HeightAnchor.ConstraintEqualTo(imageSize * 2 / 3),
            });
        }

        var imageHeight = imageSize * 2 / 3;
        gridContainer.HeightAnchor.ConstraintEqualTo((imageSize + 16) + imageHeight).Active = true;
        container.AddArrangedSubview(gridContainer);

        // Status label
        var statusLabel = new UILabel
        {
            Text = "Tap \"Load Images\" to fetch from network",
            Font = UIFont.SystemFontOfSize(13),
            TextColor = UIColor.SecondaryLabel,
            TextAlignment = UITextAlignment.Center,
            Lines = 0,
        };
        container.AddArrangedSubview(statusLabel);

        // Button row
        var buttonRow = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Spacing = 12,
            Distribution = UIStackViewDistribution.FillEqually,
        };

        var loadButton = new UIButton(UIButtonType.System);
        loadButton.SetTitle("Load Images", UIControlState.Normal);

        var reloadButton = new UIButton(UIButtonType.System);
        reloadButton.SetTitle("Reload (Cache)", UIControlState.Normal);
        reloadButton.Enabled = false;

        var clearButton = new UIButton(UIButtonType.System);
        clearButton.SetTitle("Clear Cache", UIControlState.Normal);
        clearButton.SetTitleColor(UIColor.SystemRed, UIControlState.Normal);

        buttonRow.AddArrangedSubview(loadButton);
        buttonRow.AddArrangedSubview(reloadButton);
        buttonRow.AddArrangedSubview(clearButton);
        container.AddArrangedSubview(buttonRow);

        // Pipeline info section
        var pipelineHeader = new UILabel
        {
            Text = "Pipeline Configuration",
            Font = UIFont.BoldSystemFontOfSize(15),
            TextColor = UIColor.Label,
        };
        container.AddArrangedSubview(pipelineHeader);

        var pipelineInfo = new UILabel
        {
            Font = UIFont.SystemFontOfSize(13),
            TextColor = UIColor.SecondaryLabel,
            Lines = 0,
        };

        // Show pipeline configuration
        try
        {
            var pipeline = ImagePipeline.Shared;
            pipelineInfo.Text = "ImagePipeline.Shared: available\n"
                + $"  Cache: accessible via Cache\n"
                + $"  Use cache.RemoveAll() to clear all cached data";
        }
        catch (Exception ex)
        {
            pipelineInfo.Text = $"Pipeline info: {ex.Message}";
        }
        container.AddArrangedSubview(pipelineInfo);

        // Load handler — concurrent image loads with timing
        loadButton.PrimaryActionTriggered += async (sender, e) =>
        {
            loadButton.Enabled = false;
            statusLabel.Text = "Loading images concurrently...";
            statusLabel.TextColor = UIColor.SystemBlue;

            // Clear existing images
            foreach (var iv in imageViews)
                iv.Image = null;

            var pipeline = ImagePipeline.Shared;
            var sw = Stopwatch.StartNew();
            int loaded = 0;

            // Launch all loads concurrently
            var tasks = new Task[imageUrls.Length];
            for (int i = 0; i < imageUrls.Length; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        var request = new ImageRequest(imageUrls[idx]);
                        var image = await pipeline.ImageAsync(request);
                        if (image != null)
                        {
                            InvokeOnMainThread(() => imageViews[idx].Image = image);
                            Interlocked.Increment(ref loaded);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Load {idx}: {ex.Message}");
                    }
                });
            }

            await Task.WhenAll(tasks);
            sw.Stop();

            statusLabel.Text = $"Loaded {loaded}/{imageUrls.Length} images in {sw.ElapsedMilliseconds}ms (concurrent)";
            statusLabel.TextColor = loaded == imageUrls.Length ? UIColor.SystemGreen : UIColor.SystemOrange;
            loadButton.Enabled = true;
            reloadButton.Enabled = true;
        };

        // Reload handler — demonstrates cache hit performance
        reloadButton.PrimaryActionTriggered += async (sender, e) =>
        {
            reloadButton.Enabled = false;
            statusLabel.Text = "Reloading from cache...";
            statusLabel.TextColor = UIColor.SystemBlue;

            foreach (var iv in imageViews)
                iv.Image = null;

            var pipeline = ImagePipeline.Shared;
            var sw = Stopwatch.StartNew();
            int loaded = 0;

            for (int i = 0; i < imageUrls.Length; i++)
            {
                try
                {
                    var request = new ImageRequest(imageUrls[i]);
                    var image = await pipeline.ImageAsync(request);
                    if (image != null)
                    {
                        imageViews[i].Image = image;
                        loaded++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Reload {i}: {ex.Message}");
                }
            }
            sw.Stop();

            statusLabel.Text = $"Reloaded {loaded}/{imageUrls.Length} from cache in {sw.ElapsedMilliseconds}ms";
            statusLabel.TextColor = UIColor.SystemGreen;
            reloadButton.Enabled = true;
        };

        // Clear cache handler
        clearButton.PrimaryActionTriggered += (sender, e) =>
        {
            try
            {
                var pipeline = ImagePipeline.Shared;
                var cache = pipeline.Cache;
                cache.RemoveAll();
                foreach (var iv in imageViews)
                    iv.Image = null;
                statusLabel.Text = "Cache cleared. Tap \"Load Images\" to fetch from network again.";
                statusLabel.TextColor = UIColor.SecondaryLabel;
                reloadButton.Enabled = false;
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Clear cache failed: {ex.Message}";
                statusLabel.TextColor = UIColor.SystemRed;
            }
        };

        // Code snippet
        var codeLabel = new UILabel
        {
            Text = "// Concurrent image loading with Nuke\n"
                 + "var pipeline = ImagePipeline.Shared;\n"
                 + "var tasks = urls.Select(url => Task.Run(\n"
                 + "    async () => await pipeline.ImageAsync(\n"
                 + "        new ImageRequest(url))));\n"
                 + "await Task.WhenAll(tasks);\n"
                 + "\n"
                 + "// Clear all caches\n"
                 + "pipeline.Cache.RemoveAll();",
            Font = UIFont.FromName("Menlo", 11),
            TextColor = UIColor.SecondaryLabel,
            Lines = 0,
        };
        var codeContainer = new UIView { BackgroundColor = UIColor.TertiarySystemBackground };
        codeContainer.Layer.CornerRadius = 8;
        codeLabel.TranslatesAutoresizingMaskIntoConstraints = false;
        codeContainer.AddSubview(codeLabel);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            codeLabel.TopAnchor.ConstraintEqualTo(codeContainer.TopAnchor, 12),
            codeLabel.LeadingAnchor.ConstraintEqualTo(codeContainer.LeadingAnchor, 12),
            codeLabel.TrailingAnchor.ConstraintEqualTo(codeContainer.TrailingAnchor, -12),
            codeLabel.BottomAnchor.ConstraintEqualTo(codeContainer.BottomAnchor, -12),
        });
        container.AddArrangedSubview(codeContainer);

        return container;
    }

    private static void InvokeOnMainThread(Action action)
    {
        NSThread.MainThread.InvokeOnMainThread(action);
    }
}
