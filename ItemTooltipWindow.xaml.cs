using Lootlens.Data;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace Lootlens;

// Small, item-scoped tooltip shown only over the hovered item (Blitz-style), separate from the search window.
public partial class ItemTooltipWindow : Window {
    private bool _visible;
    private DateTime _hiddenAt = DateTime.MinValue;

    public ItemTooltipWindow() {
        InitializeComponent();
    }

    public bool IsTooltipVisible => _visible;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT {
        public int X;
        public int Y;
    }

    public void ShowItem(ItemCache.Item item, Settings settings) {
        ItemPresenter.RenderInlines(TooltipText, item, settings);
        SetVisible(true);
    }

    public void ShowTooltip() => SetVisible(true);

    public void HideTooltip() => SetVisible(false);

    public void PositionNearCursor() {
        if (!GetCursorPos(out var point))
            return;

        var workArea = SystemParameters.WorkArea;
        var boxWidth = (int)Math.Max(ActualWidth, 60);
        var boxHeight = (int)Math.Max(ActualHeight, 24);

        var left = point.X + 18;
        var top = point.Y + 18;

        if (left + boxWidth > workArea.Right)
            left = point.X - boxWidth - 18;

        if (top + boxHeight > workArea.Bottom)
            top = point.Y - boxHeight - 18;

        var maxLeft = (int)Math.Max(workArea.Left, workArea.Right - boxWidth);
        var maxTop = (int)Math.Max(workArea.Top, workArea.Bottom - boxHeight);

        Left = Math.Clamp(left, (int)workArea.Left, maxLeft);
        Top = Math.Clamp(top, (int)workArea.Top, maxTop);
    }

    private void SetVisible(bool visible) {
        if (visible) {
            _hiddenAt = DateTime.MinValue;
            if (_visible && Visibility == Visibility.Visible && Opacity >= 0.9)
                return;

            BeginAnimation(OpacityProperty, null);
            Visibility = Visibility.Visible;
            Opacity = 0;
            _visible = true;

            var showAnimation = new DoubleAnimation {
                From = 0,
                To = 0.98,
                Duration = TimeSpan.FromMilliseconds(150),
                FillBehavior = FillBehavior.Stop
            };

            showAnimation.Completed += (_, __) => {
                if (_visible)
                    Opacity = 0.98;
            };

            BeginAnimation(OpacityProperty, showAnimation);
            return;
        }

        if (!_visible || Visibility != Visibility.Visible)
            return;

        if (_hiddenAt == DateTime.MinValue)
            _hiddenAt = DateTime.UtcNow;

        if (DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(220))
            return;

        BeginAnimation(OpacityProperty, null);
        _visible = false;

        var hideAnimation = new DoubleAnimation {
            From = Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            FillBehavior = FillBehavior.Stop
        };

        hideAnimation.Completed += (_, __) => {
            if (!_visible) {
                Visibility = Visibility.Hidden;
                Opacity = 0;
            }
        };

        BeginAnimation(OpacityProperty, hideAnimation);
    }
}
