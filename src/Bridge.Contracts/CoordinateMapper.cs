namespace Bridge.Contracts;

/// <summary>
/// Image-pixel to native-desktop coordinate mapping (docs/04 §8).
/// native = crop_left + floor(image * crop_size / image_size).
/// No rounding bias (+0.5), floor everywhere, negative origins supported.
/// The mapped point is re-checked against the crop and virtual desktop
/// by the caller before any input is injected.
/// </summary>
public static class CoordinateMapper
{
    public static (long NativeX, long NativeY) Map(
        long imageX, long imageY,
        long imageWidth, long imageHeight,
        long cropLeft, long cropTop, long cropWidth, long cropHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image size must be positive.");
        if (cropWidth <= 0 || cropHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(cropWidth), "Crop size must be positive.");
        if (imageX < 0 || imageX >= imageWidth || imageY < 0 || imageY >= imageHeight)
            throw new ArgumentOutOfRangeException(nameof(imageX), "Image point is outside the image.");
        long nativeX = cropLeft + imageX * cropWidth / imageWidth;
        long nativeY = cropTop + imageY * cropHeight / imageHeight;
        return (nativeX, nativeY);
    }
}
