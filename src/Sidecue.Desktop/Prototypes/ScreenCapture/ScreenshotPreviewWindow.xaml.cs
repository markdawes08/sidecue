using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sidecue.Windows.ScreenCapture;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop.Prototypes.ScreenCapture;

/// <summary>An owned modal preview that returns only an explicitly included screenshot.</summary>
public sealed partial class ScreenshotPreviewWindow : Window
{
    private readonly IWindowCaptureExclusion _exclusion;
    private bool _included;

    private ScreenshotPreviewWindow(Window owner, BitmapSource image, IWindowCaptureExclusion exclusion)
    {
        _exclusion = exclusion;
        InitializeComponent();
        Owner = owner;
        PreviewImage.Source = image;
    }

    /// <summary>Previews the frame on the owner's dispatcher and returns the original frame on Include.</summary>
    /// <remarks>
    /// The call owns the frame until it returns an approved frame to the caller.
    /// Cancel, Close, token cancellation, and failure release the frame.
    /// </remarks>
    public static async Task<OwnedScreenshotFrame?> ShowAsync(Window owner, OwnedScreenshotFrame frame,
        IWindowCaptureExclusion? exclusion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool approved = false;
        try
        {
            ArgumentNullException.ThrowIfNull(owner);
            Dispatcher dispatcher = owner.Dispatcher;
            dispatcher.VerifyAccess();
            if (!owner.IsVisible || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            BitmapSource image = await Task.Run(() => CreatePreview(frame), cancellationToken).ConfigureAwait(false);
            approved = await dispatcher.InvokeAsync(() => ShowPreview(owner, image,
                exclusion ?? new WindowCaptureExclusion(), cancellationToken), DispatcherPriority.Normal, cancellationToken)
                .Task.ConfigureAwait(false);
            return approved ? frame : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || owner.Dispatcher.HasShutdownStarted)
        {
            return null;
        }
        finally
        {
            if (!approved)
            {
                frame.Dispose();
            }
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureStatusText.Text = _exclusion.Apply(new WindowInteropHelper(this).Handle).Status switch
        {
            CaptureExclusionStatus.ApiAccepted => "Windows accepted capture exclusion. Screen-sharing verification is pending.",
            CaptureExclusionStatus.Unavailable => "Capture exclusion is unavailable for this window.",
            _ => "Windows could not apply capture exclusion to this window."
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        PreviewImage.Source = null;
        base.OnClosed(e);
    }

    private static bool ShowPreview(Window owner, BitmapSource image, IWindowCaptureExclusion exclusion,
        CancellationToken cancellationToken)
    {
        if (!owner.IsVisible || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var dialog = new ScreenshotPreviewWindow(owner, image, exclusion);
        try
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(dialog.ScheduleCancel);
            return !cancellationToken.IsCancellationRequested && dialog.ShowDialog() == true && dialog._included &&
                !cancellationToken.IsCancellationRequested && owner.IsVisible;
        }
        finally
        {
            dialog.PreviewImage.Source = null;
            if (dialog.IsVisible)
            {
                dialog.Close();
            }
        }
    }

    private void ScheduleCancel() => _ = Dispatcher.InvokeAsync(() =>
    {
        if (IsVisible)
        {
            Close();
        }
    }, DispatcherPriority.Send);

    private void OnIncludeClick(object sender, RoutedEventArgs e)
    {
        _included = true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static WriteableBitmap CreatePreview(OwnedScreenshotFrame frame)
    {
        byte[]? copy = null;
        try
        {
            ReadOnlyMemory<byte> pixels = frame.Pixels;
            if (!MemoryMarshal.TryGetArray(pixels, out ArraySegment<byte> buffer))
            {
                copy = pixels.ToArray();
                buffer = new ArraySegment<byte>(copy);
            }

            var bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), buffer.Array!, frame.Stride, buffer.Offset);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (copy is not null)
            {
                CryptographicOperations.ZeroMemory(copy);
            }
        }
    }
}
