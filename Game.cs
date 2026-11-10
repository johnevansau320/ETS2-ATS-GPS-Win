namespace Ets2Gps;

/// <summary>
/// The two games the app works with. Euro Truck Simulator 2 and American Truck Simulator share the engine, the file formats and the
/// telemetry; they differ in where they are installed, their map and the units shown (ATS: miles, mph, gallons, dollars).
/// </summary>
public sealed record GameInfo(string Id, string Name, string Short, string SteamApp, string Process, string FilePrefix, bool Imperial, string Currency)
{
    /// <summary>The folder name of the install (steamapps\common) and of the profiles and mods (Documents).</summary>
    public string Folder => Name;
}

public static class Games
{
    public static readonly GameInfo Ets2 = new("ets2", "Euro Truck Simulator 2", "ETS2", "227300", "eurotrucks2", "ets", false, "€");
    public static readonly GameInfo Ats = new("ats", "American Truck Simulator", "ATS", "270880", "amtrucks", "ats", true, "$");
    public static readonly GameInfo[] All = { Ets2, Ats };

    /// <summary>The game the app is working with now (switched when the telemetry reports the other game).</summary>
    public static GameInfo Current { get; set; } = Ets2;

    public static GameInfo ById(string? id) => All.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Ets2;

    /// <summary>The game from the telemetry's game name ("ETS2", "ATS"), or null when it says nothing.</summary>
    public static GameInfo? FromTelemetryName(string? name) =>
        string.IsNullOrEmpty(name) ? null : name.Contains("ATS", StringComparison.OrdinalIgnoreCase) || name.Contains("American", StringComparison.OrdinalIgnoreCase) ? Ats
        : name.Contains("ETS", StringComparison.OrdinalIgnoreCase) || name.Contains("Euro", StringComparison.OrdinalIgnoreCase) ? Ets2 : null;
}

/// <summary>
/// Numbers as the current game shows them: metric for ETS2; miles, mph, feet, US gallons and dollars for ATS. Inputs are always metric
/// (game metres, km, km/h, litres), as the telemetry and the map give them.
/// </summary>
public static class Units
{
    const float MPerMile = 1609.344f, MPerFoot = 0.3048f, LPerGallon = 3.785411784f;
    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    /// <summary>"game" (km for ETS2, miles for ATS), "metric" or "imperial": the user's choice in Settings.</summary>
    public static string Choice = "game";
    public static bool Imperial => Choice == "imperial" || (Choice != "metric" && Games.Current.Imperial);

    public static string SpeedUnit => Imperial ? "mph" : "km/h";
    public static string DistUnit => Imperial ? "mi" : "km";
    public static string FuelUnit => Imperial ? "gal" : "L";
    public static string AltUnit => Imperial ? "ft" : "m";
    public static string Money => Games.Current.Currency;

    /// <summary>km/h -> the speed shown (km/h or mph).</summary>
    public static float Speed(float kmh) => Imperial ? kmh * 1000f / MPerMile : kmh;
    /// <summary>A speed shown (km/h or mph) -> km/h.</summary>
    public static float SpeedToKmh(float shown) => Imperial ? shown * MPerMile / 1000f : shown;
    /// <summary>km -> the long distance shown (km or miles).</summary>
    public static float Long(float km) => Imperial ? km * 1000f / MPerMile : km;
    public static float Fuel(float litres) => Imperial ? litres / LPerGallon : litres;
    public static float Alt(float metres) => Imperial ? metres / MPerFoot : metres;

    /// <summary>"12 km" / "7.5 mi" style, for a distance in km.</summary>
    public static string LongText(float km, string fmt = "N0") => Long(km).ToString(fmt, Inv) + " " + DistUnit;
    public static string SpeedText(float kmh) => Math.Round(Math.Max(0, Speed(kmh))).ToString(Inv) + " " + SpeedUnit;
    public static string AltText(float metres) => Math.Round(Alt(metres)).ToString(Inv) + " " + AltUnit;
    public static string FuelText(float litres, string fmt = "0") => Fuel(litres).ToString(fmt, Inv) + " " + FuelUnit;
    /// <summary>Cargo mass: "12.3 t" or "27,100 lb".</summary>
    public static string Mass(float kg) => Imperial ? (MathF.Round(kg * 2.20462262f / 100f) * 100f).ToString("N0", Inv) + " lb" : (kg / 1000f).ToString("0.0", Inv) + " t";

    /// <summary>Fuel consumption from litres per km: "32.5 L/100 km" or "7.2 mpg".</summary>
    public static string Consumption(float lPerKm) =>
        Imperial ? (lPerKm > 0 ? (MPerMile / 1000f / (lPerKm / LPerGallon)).ToString("0.0", Inv) : "-") + " mpg" : (lPerKm * 100f).ToString("0.0", Inv) + " L/100 km";

    /// <summary>Short distance for the screen, from metres: "300 m" / "1.2 km" / "12 km", or "500 ft" / "0.3 mi" / "12 mi".</summary>
    public static string Short(float m)
    {
        if (!Imperial)
        {
            if (m < 1000f) return Math.Max(10, (int)MathF.Round(m / 10f) * 10) + " m";
            if (m < 10000f) return (m / 1000f).ToString("0.0", Inv) + " km";
            return (int)MathF.Round(m / 1000f) + " km";
        }
        float mi = m / MPerMile;
        if (mi < 0.1f) return Math.Max(50, (int)MathF.Round(m / MPerFoot / 50f) * 50) + " ft";
        if (mi < 10f) return mi.ToString("0.0", Inv) + " mi";
        return (int)MathF.Round(mi) + " mi";
    }

    /// <summary>Distance as spoken, from metres: "300 meters" / "1.5 kilometers", or "500 feet" / "half a mile" / "3 miles".</summary>
    public static string Spoken(float m)
    {
        if (!Imperial)
        {
            if (m < 950f) return Math.Max(50, (int)MathF.Round(m / 50f) * 50) + " meters";
            if (m < 10000f)
            {
                float half = MathF.Round(m / 500f) / 2f;
                return half.ToString("0.#", Inv) + " kilometers";
            }
            return (int)MathF.Round(m / 1000f) + " kilometers";
        }
        float mi = m / MPerMile;
        if (mi < 0.19f) return Math.Max(100, (int)MathF.Round(m / MPerFoot / 100f) * 100) + " feet";
        if (mi < 0.38f) return "a quarter mile";
        if (mi < 0.63f) return "half a mile";
        if (mi < 0.88f) return "three quarters of a mile";
        if (mi < 1.25f) return "1 mile";
        if (mi < 10f) return (MathF.Round(mi * 2f) / 2f).ToString("0.#", Inv) + " miles";
        return (int)MathF.Round(mi) + " miles";
    }
}
