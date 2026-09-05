namespace ModelConversion.Service.Domain;

public sealed record GeoReference(double Latitude, double Longitude, double Altitude)
{
    public void EnsureValid()
    {
        if (!double.IsFinite(Latitude) || Latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(Latitude), "纬度必须在 -90 到 90 之间");
        if (!double.IsFinite(Longitude) || Longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(Longitude), "经度必须在 -180 到 180 之间");
        if (!double.IsFinite(Altitude))
            throw new ArgumentOutOfRangeException(nameof(Altitude), "高程必须是有限数值");
    }
}
