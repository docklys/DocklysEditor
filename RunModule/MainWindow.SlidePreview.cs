using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace RunModule;

public partial class MainWindow
{
    private DispatcherTimer? _slideTimer;
    private TranslateTransform? _slideTransform;
    private double _slideX;
    private double _slideTargetX;
    private bool _slideRunning;
    private SlideAnimPhase _slidePhase;
    private int _slideTick;
    private int _slideHoldDuration;

    private enum SlideAnimPhase
    {
        SlideOutLeft,
        HoldLeft,
        SlideInFromLeft,
        HoldCenter1,
        SlideOutRight,
        HoldRight,
        SlideInFromRight,
        HoldCenter2,
    }

    private void SlidePreviewButton_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if ((sender as ToggleButton)?.IsChecked == true)
            StartSlidePreview();
        else
            StopSlidePreview();
    }

    private void StartSlidePreview()
    {
        if (_slideRunning) return;
        var panel = this.FindControl<StackPanel>("ModulePreviewPanel");
        if (panel == null) return;

        _slideTransform = new TranslateTransform(0, 0);
        panel.RenderTransform = _slideTransform;
        _slideX = 0;
        _slideRunning = true;

        SetSlidePhase(SlideAnimPhase.SlideOutLeft);

        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slideTimer.Tick += OnSlideTick;
        _slideTimer.Start();
    }

    private void StopSlidePreview()
    {
        _slideRunning = false;
        _slideTimer?.Stop();
        _slideTimer = null;

        var panel = this.FindControl<StackPanel>("ModulePreviewPanel");
        if (panel?.RenderTransform is not TranslateTransform t) return;

        // Ease back to center using the same lerp, then remove the transform.
        var returnTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        returnTimer.Tick += (_, _) =>
        {
            double dx = (0 - t.X) * 0.2;
            if (Math.Abs(dx) < 0.5)
            {
                t.X = 0;
                returnTimer.Stop();
                panel.RenderTransform = null;
                _slideTransform = null;
            }
            else
            {
                t.X += dx;
            }
        };
        returnTimer.Start();
    }

    private void OnSlideTick(object? sender, EventArgs e)
    {
        if (!_slideRunning || _slideTransform == null) return;

        if (_slidePhase is SlideAnimPhase.HoldLeft or SlideAnimPhase.HoldRight or
                           SlideAnimPhase.HoldCenter1 or SlideAnimPhase.HoldCenter2)
        {
            if (++_slideTick >= _slideHoldDuration)
                AdvanceSlidePhase();
            return;
        }

        // Exact same lerp formula as Docklys MainWindow.Animation.cs
        double dx = (_slideTargetX - _slideX) * 0.2;
        if (Math.Abs(dx) < 0.5)
        {
            _slideX = _slideTargetX;
            _slideTransform.X = _slideX;
            AdvanceSlidePhase();
        }
        else
        {
            _slideX += dx;
            _slideTransform.X = _slideX;
        }
    }

    private void AdvanceSlidePhase()
    {
        SetSlidePhase(_slidePhase switch
        {
            SlideAnimPhase.SlideOutLeft     => SlideAnimPhase.HoldLeft,
            SlideAnimPhase.HoldLeft         => SlideAnimPhase.SlideInFromLeft,
            SlideAnimPhase.SlideInFromLeft  => SlideAnimPhase.HoldCenter1,
            SlideAnimPhase.HoldCenter1      => SlideAnimPhase.SlideOutRight,
            SlideAnimPhase.SlideOutRight    => SlideAnimPhase.HoldRight,
            SlideAnimPhase.HoldRight        => SlideAnimPhase.SlideInFromRight,
            SlideAnimPhase.SlideInFromRight => SlideAnimPhase.HoldCenter2,
            SlideAnimPhase.HoldCenter2      => SlideAnimPhase.SlideOutLeft,
            _                               => SlideAnimPhase.SlideOutLeft,
        });
    }

    private void SetSlidePhase(SlideAnimPhase phase)
    {
        _slidePhase = phase;
        _slideTick = 0;

        var grid = this.FindControl<Grid>("PreviewGrid");
        double bw = grid?.Bounds.Width ?? 0;
        double w = bw > 50 ? bw : 600;

        switch (phase)
        {
            case SlideAnimPhase.SlideOutLeft:
                _slideTargetX = -w;
                break;

            case SlideAnimPhase.HoldLeft:
                _slideHoldDuration = 19;  // ~300 ms
                break;

            case SlideAnimPhase.SlideInFromLeft:
                // _slideX is already at -w from the preceding SlideOutLeft phase.
                _slideTargetX = 0;
                break;

            case SlideAnimPhase.HoldCenter1:
            case SlideAnimPhase.HoldCenter2:
                _slideHoldDuration = 50;  // ~800 ms
                break;

            case SlideAnimPhase.SlideOutRight:
                _slideTargetX = w;
                break;

            case SlideAnimPhase.HoldRight:
                _slideHoldDuration = 19;
                break;

            case SlideAnimPhase.SlideInFromRight:
                // _slideX is already at +w from the preceding SlideOutRight phase.
                _slideTargetX = 0;
                break;
        }
    }
}
