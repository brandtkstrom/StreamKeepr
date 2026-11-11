namespace StreamKeepr.Ui;

/// <summary>
/// Scoped viewer time zone. The <c>sk-tz</c> cookie is read at prerender; an
/// invalid or missing value falls back to UTC and is corrected once by
/// <see cref="ViewerClockSync"/> after the circuit starts.
/// </summary>
public sealed class ViewerClock
{
    public const string CookieName = "sk-tz";
    public const string PersistenceKey = "sk-tz";

    private readonly TimeProvider _timeProvider;
    private TimeZoneInfo _timeZone;

    public ViewerClock(IClientStateStore store, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;

        var cookie = store.ReadCookie(CookieName);
        if (TryResolve(cookie, out var zone))
        {
            _timeZone = zone;
        }
        else if (store.TryRestore<string>(PersistenceKey, out var persisted) &&
                 TryResolve(persisted, out zone))
        {
            _timeZone = zone;
        }
        else
        {
            _timeZone = TimeZoneInfo.Utc;
            IsFallbackToUtc = true;
        }

        store.Persist(PersistenceKey, _timeZone.Id);
    }

    public TimeZoneInfo TimeZone => _timeZone;

    public bool IsFallbackToUtc { get; private set; }

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _timeZone);

    public event Action? Changed;

    public DateTimeOffset ToLocal(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _timeZone);

    /// <summary>
    /// Applies a browser-reported IANA zone. Returns true only when the zone is
    /// valid and different from the current one.
    /// </summary>
    public bool TrySetTimeZone(string? timeZoneId)
    {
        if (!TryResolve(timeZoneId, out var zone) ||
            string.Equals(zone.Id, _timeZone.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _timeZone = zone;
        IsFallbackToUtc = false;
        Changed?.Invoke();
        return true;
    }

    private static bool TryResolve(string? timeZoneId, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return false;

        var value = timeZoneId.Trim();
        try
        {
            // ASP.NET Core normally decodes request cookies; accept a raw value
            // written by prefs.js as well.
            value = Uri.UnescapeDataString(value);

            zone = TimeZoneInfo.FindSystemTimeZoneById(value);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }
}
