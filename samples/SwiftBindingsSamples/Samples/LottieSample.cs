using UIKit;
using Foundation;
using CoreGraphics;
using SwiftBindingsSamples.Models;
using Lottie;

namespace SwiftBindingsSamples.Samples;

public class LottieSample : ILibrarySample
{
    public string LibraryName => "Lottie";
    public string PackageName => "SwiftBindings.Lottie";
    public string Version => "4.6.0";
    public string Description => "Animation loading, playback controls, and caching";

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
            Text = "Lottie Animations",
            Font = UIFont.BoldSystemFontOfSize(18),
            TextColor = UIColor.Label,
        };
        container.AddArrangedSubview(header);

        // Description
        var desc = new UILabel
        {
            Text = "Load Lottie animations from bundled JSON, control playback with play/pause/stop, "
                 + "adjust speed, and toggle loop modes — all from C# via SwiftBindings.",
            Font = UIFont.SystemFontOfSize(14),
            TextColor = UIColor.SecondaryLabel,
            Lines = 0,
        };
        container.AddArrangedSubview(desc);

        // Load animation
        var animPath = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json");
        LottieAnimation? animation = null;
        LottieAnimationView? animView = null;

        if (animPath != null)
        {
            try
            {
                animation = LottieAnimation.Filepath(animPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load animation: {ex.Message}");
            }
        }

        // Animation metadata section
        var metadataHeader = new UILabel
        {
            Text = "Animation Metadata",
            Font = UIFont.BoldSystemFontOfSize(15),
            TextColor = UIColor.Label,
        };
        container.AddArrangedSubview(metadataHeader);

        var metadataLabel = new UILabel
        {
            Font = UIFont.SystemFontOfSize(14),
            TextColor = UIColor.Label,
            Lines = 0,
        };

        if (animation != null)
        {
            var markers = animation.MarkerNames;
            metadataLabel.Text = $"Duration: {animation.Duration:F1}s\n"
                + $"Framerate: {animation.Framerate:F0} fps\n"
                + $"Frames: {animation.StartFrame:F0} - {animation.EndFrame:F0}\n"
                + $"Markers: {markers.Count}";
        }
        else
        {
            metadataLabel.Text = animPath != null
                ? "Failed to load PlaneAnimation.json"
                : "PlaneAnimation.json not found in bundle";
            metadataLabel.TextColor = UIColor.SystemRed;
        }
        container.AddArrangedSubview(metadataLabel);

        // Animation view
        var playbackStatus = new UILabel
        {
            Font = UIFont.SystemFontOfSize(13),
            TextColor = UIColor.SecondaryLabel,
            TextAlignment = UITextAlignment.Center,
            Lines = 0,
        };

        try
        {
            var animHeight = width * 500 / 868;
            var animFrame = new CGRect(0, 0, width, animHeight);
            animView = new LottieAnimationView((Swift.CGRect)animFrame);

            animView.TranslatesAutoresizingMaskIntoConstraints = false;
            animView.BackgroundColor = UIColor.SecondarySystemBackground;
            animView.Layer.CornerRadius = 8;
            animView.ClipsToBounds = true;

            if (animation != null)
            {
                animView.Animation = animation;
                animView.LoopMode = LottieLoopMode.Loop;
                animView.Play();
                playbackStatus.Text = "Playing (loop mode, 1.0x speed)";
                playbackStatus.TextColor = UIColor.SystemGreen;
            }
            else
            {
                playbackStatus.Text = "No animation loaded";
                playbackStatus.TextColor = UIColor.SystemOrange;
            }

            var animContainer = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
            animContainer.AddSubview(animView);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                animView.TopAnchor.ConstraintEqualTo(animContainer.TopAnchor),
                animView.LeadingAnchor.ConstraintEqualTo(animContainer.LeadingAnchor),
                animView.TrailingAnchor.ConstraintEqualTo(animContainer.TrailingAnchor),
                animView.HeightAnchor.ConstraintEqualTo(animHeight),
                animContainer.HeightAnchor.ConstraintEqualTo(animHeight),
            });
            container.AddArrangedSubview(animContainer);
        }
        catch (Exception ex)
        {
            playbackStatus.Text = $"LottieAnimationView failed: {ex.Message}";
            playbackStatus.TextColor = UIColor.SystemRed;
        }

        container.AddArrangedSubview(playbackStatus);

        // Playback controls
        var controlsHeader = new UILabel
        {
            Text = "Playback Controls",
            Font = UIFont.BoldSystemFontOfSize(15),
            TextColor = UIColor.Label,
        };
        container.AddArrangedSubview(controlsHeader);

        var controlRow = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Spacing = 12,
            Distribution = UIStackViewDistribution.FillEqually,
        };

        var playButton = new UIButton(UIButtonType.System);
        playButton.SetTitle("Play", UIControlState.Normal);

        var pauseButton = new UIButton(UIButtonType.System);
        pauseButton.SetTitle("Pause", UIControlState.Normal);

        var stopButton = new UIButton(UIButtonType.System);
        stopButton.SetTitle("Stop", UIControlState.Normal);

        controlRow.AddArrangedSubview(playButton);
        controlRow.AddArrangedSubview(pauseButton);
        controlRow.AddArrangedSubview(stopButton);
        container.AddArrangedSubview(controlRow);

        // Speed control
        var speedRow = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Spacing = 8,
            Alignment = UIStackViewAlignment.Center,
        };

        var speedLabel = new UILabel
        {
            Text = "Speed: 1.0x",
            Font = UIFont.SystemFontOfSize(14),
            TextColor = UIColor.Label,
        };
        speedLabel.SetContentHuggingPriority(251, UILayoutConstraintAxis.Horizontal);

        var speedSlider = new UISlider
        {
            MinValue = 0.25f,
            MaxValue = 3.0f,
            Value = 1.0f,
        };

        speedRow.AddArrangedSubview(speedLabel);
        speedRow.AddArrangedSubview(speedSlider);
        container.AddArrangedSubview(speedRow);

        // Loop mode toggle
        var loopRow = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Spacing = 8,
            Distribution = UIStackViewDistribution.FillEqually,
        };

        var loopButton = new UIButton(UIButtonType.System);
        loopButton.SetTitle("Loop", UIControlState.Normal);

        var onceButton = new UIButton(UIButtonType.System);
        onceButton.SetTitle("Play Once", UIControlState.Normal);

        var reverseButton = new UIButton(UIButtonType.System);
        reverseButton.SetTitle("Auto Reverse", UIControlState.Normal);

        loopRow.AddArrangedSubview(loopButton);
        loopRow.AddArrangedSubview(onceButton);
        loopRow.AddArrangedSubview(reverseButton);
        container.AddArrangedSubview(loopRow);

        // Frame scrubber
        var frameRow = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Spacing = 8,
            Alignment = UIStackViewAlignment.Center,
        };

        var frameLabel = new UILabel
        {
            Text = "Frame: 0",
            Font = UIFont.SystemFontOfSize(14),
            TextColor = UIColor.Label,
        };
        frameLabel.SetContentHuggingPriority(251, UILayoutConstraintAxis.Horizontal);

        var frameSlider = new UISlider
        {
            MinValue = 0,
            MaxValue = animation != null ? (float)animation.EndFrame : 238,
            Value = 0,
        };

        frameRow.AddArrangedSubview(frameLabel);
        frameRow.AddArrangedSubview(frameSlider);
        container.AddArrangedSubview(frameRow);

        // Cache section
        var cacheHeader = new UILabel
        {
            Text = "Animation Cache",
            Font = UIFont.BoldSystemFontOfSize(15),
            TextColor = UIColor.Label,
        };
        container.AddArrangedSubview(cacheHeader);

        var cacheInfo = new UILabel
        {
            Font = UIFont.SystemFontOfSize(13),
            TextColor = UIColor.SecondaryLabel,
            Lines = 0,
        };

        try
        {
            var cache = DefaultAnimationCache.SharedCache;
            if (cache != null)
            {
                cacheInfo.Text = $"DefaultAnimationCache.SharedCache: available\n"
                    + $"CacheSize limit: {cache.CacheSize}";
            }
            else
            {
                cacheInfo.Text = "DefaultAnimationCache.SharedCache: null";
            }
        }
        catch (Exception ex)
        {
            cacheInfo.Text = $"Cache info: {ex.Message}";
        }
        container.AddArrangedSubview(cacheInfo);

        // Wire up button handlers
        var capturedAnimView = animView;

        playButton.PrimaryActionTriggered += (s, e) =>
        {
            if (capturedAnimView == null) return;
            capturedAnimView.Play();
            playbackStatus.Text = $"Playing ({GetLoopModeText(capturedAnimView)}, {capturedAnimView.AnimationSpeed:F1}x)";
            playbackStatus.TextColor = UIColor.SystemGreen;
        };

        pauseButton.PrimaryActionTriggered += (s, e) =>
        {
            if (capturedAnimView == null) return;
            capturedAnimView.Pause();
            playbackStatus.Text = $"Paused at frame {capturedAnimView.CurrentFrame:F0}";
            playbackStatus.TextColor = UIColor.SystemOrange;
        };

        stopButton.PrimaryActionTriggered += (s, e) =>
        {
            if (capturedAnimView == null) return;
            capturedAnimView.Stop();
            playbackStatus.Text = "Stopped";
            playbackStatus.TextColor = UIColor.SecondaryLabel;
        };

        speedSlider.ValueChanged += (s, e) =>
        {
            if (capturedAnimView == null) return;
            var speed = Math.Round(speedSlider.Value, 2);
            capturedAnimView.AnimationSpeed = speed;
            speedLabel.Text = $"Speed: {speed:F2}x";
            if (capturedAnimView.IsAnimationPlaying)
            {
                playbackStatus.Text = $"Playing ({GetLoopModeText(capturedAnimView)}, {speed:F1}x)";
            }
        };

        loopButton.PrimaryActionTriggered += (s, e) =>
        {
            if (capturedAnimView == null) return;
            capturedAnimView.LoopMode = LottieLoopMode.Loop;
            playbackStatus.Text = $"Loop mode set — {(capturedAnimView.IsAnimationPlaying ? "playing" : "stopped")}";
            playbackStatus.TextColor = UIColor.SystemGreen;
        };

        onceButton.PrimaryActionTriggered += (s, e) =>
        {
            if (capturedAnimView == null) return;
            capturedAnimView.LoopMode = LottieLoopMode.PlayOnce;
            playbackStatus.Text = $"Play Once mode — {(capturedAnimView.IsAnimationPlaying ? "playing" : "stopped")}";
            playbackStatus.TextColor = UIColor.SystemBlue;
        };

        reverseButton.PrimaryActionTriggered += (s, e) =>
        {
            if (capturedAnimView == null) return;
            capturedAnimView.LoopMode = LottieLoopMode.AutoReverse;
            playbackStatus.Text = $"Auto Reverse mode — {(capturedAnimView.IsAnimationPlaying ? "playing" : "stopped")}";
            playbackStatus.TextColor = UIColor.SystemPurple;
        };

        frameSlider.ValueChanged += (s, e) =>
        {
            if (capturedAnimView == null) return;
            var frame = Math.Round((double)frameSlider.Value);
            capturedAnimView.CurrentFrame = frame;
            frameLabel.Text = $"Frame: {frame:F0}";
        };

        // Code snippet
        var codeLabel = new UILabel
        {
            Text = "// Load and play a Lottie animation\n"
                 + "var path = NSBundle.MainBundle\n"
                 + "    .PathForResource(\"MyAnim\", \"json\");\n"
                 + "var anim = LottieAnimation.Filepath(path);\n"
                 + "var view = new LottieAnimationView(frame);\n"
                 + "view.Animation = anim;\n"
                 + "view.LoopMode = LottieLoopMode.Loop;\n"
                 + "view.AnimationSpeed = 1.5;\n"
                 + "view.Play();",
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

    private static string GetLoopModeText(LottieAnimationView view)
    {
        // Can't read LoopMode back (enum getter crashes on device), so use a fixed label
        return "loop";
    }
}
