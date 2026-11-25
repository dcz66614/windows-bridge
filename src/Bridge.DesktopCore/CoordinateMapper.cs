namespace Bridge.DesktopCore;

/// <summary>
/// Pure coordinate math (docs/04 §8). Physical pixels only; the
/// caller is responsible for DPI awareness (the process must be
/// PerMonitorV2 so these are physical, not logical, pixels).
/// </summary>
public static class CoordinateMapper
{
    public const int SendInputAbsoluteMax = 65535;

    /// <summary>
    /// Image pixel → native virtual-desktop pixel:
    /// native = crop_left + floor(image * crop_size / image_size).
    /// </summary>
    public static (int NativeX, int NativeY) ImageToNative(
        int imageX, int imageY,
        int imageWidth, int imageHeight,
        int cropLeft, int cropTop, int cropWidth, int cropHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image size must be positive.");
        if (cropWidth <= 0 || cropHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(cropWidth), "Crop size must be positive.");
        if (imageX < 0 || imageX >= imageWidth)
            throw new ArgumentOutOfRangeException(nameof(imageX), "Image x must be inside the image.");
        if (imageY < 0 || imageY >= imageHeight)
            throw new ArgumentOutOfRangeException(nameof(imageY), "Image y must be inside the image.");
        var nativeX = cropLeft + (int)Math.Floor((double)imageX * cropWidth / imageWidth);
        var nativeY = cropTop + (int)Math.Floor((double)imageY * cropHeight / imageHeight);
        return (nativeX, nativeY);
    }

    /// <summary>
    /// Native virtual-desktop pixel → SendInput absolute coordinates
    /// (0..65535 across the whole virtual desktop; MOUSEEVENTF_VIRTUALDESK).
    /// The virtual desktop origin may be negative (monitors left/above
    /// the primary), which this handles.
    /// </summary>
    public static (int AbsoluteX, int AbsoluteY) NativeToSendInput(
        int nativeX, int nativeY,
        int virtualLeft, int virtualTop, int virtualWidth, int virtualHeight)
    {
        if (virtualWidth <= 0 || virtualHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(virtualWidth), "Virtual desktop size must be positive.");
        if (nativeX < virtualLeft || nativeX >= virtualLeft + virtualWidth ||
            nativeY < virtualTop || nativeY >= virtualTop + virtualHeight)
            throw new ArgumentOutOfRangeException(nameof(nativeX), "Native point is outside the virtual desktop.");
        var absoluteX = (int)Math.Round((double)(nativeX - virtualLeft) * SendInputAbsoluteMax / (virtualWidth - 1));
        var absoluteY = (int)Math.Round((double)(nativeY - virtualTop) * SendInputAbsoluteMax / (virtualHeight - 1));
        return (absoluteX, absoluteY);
    }

    public static bool IsInside(int x, int y, int left, int top, int width, int height) =>
        x >= left && x < left + width && y >= top && y < top + height;
}
