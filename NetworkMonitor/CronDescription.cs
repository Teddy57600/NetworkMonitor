namespace NetworkMonitor;

/// <summary>
/// Traduit une expression CRON en description lisible en français.
/// </summary>
static class CronDescription
{
    private static readonly string[] _jours =
        ["dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi"];

    private static readonly string[] _mois =
        ["", "janvier", "février", "mars", "avril", "mai", "juin",
         "juillet", "août", "septembre", "octobre", "novembre", "décembre"];

    /// <summary>
    /// Retourne une description en français de l'expression CRON.
    /// Exemples :
    ///   "*/3 * * * *"   → "Lancement toutes les 3 minutes"
    ///   "0 */2 * * *"   → "Lancement toutes les 2 heures"
    ///   "0 8 * * 1"     → "Lancement chaque lundi à 08h00"
    ///   "0 0 1 * *"     → "Lancement le 1er de chaque mois à minuit"
    ///   "*/30 * * * * *" → "Lancement toutes les 30 secondes"
    /// </summary>
    public static string ToFrench(string expression)
    {
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool hasSeconds = parts.Length == 6;

        string sec  = hasSeconds ? parts[0] : "0";
        string min  = parts[hasSeconds ? 1 : 0];
        string hour = parts[hasSeconds ? 2 : 1];
        string dom  = parts[hasSeconds ? 3 : 2];
        string mon  = parts[hasSeconds ? 4 : 3];
        string dow  = parts[hasSeconds ? 5 : 4];

        // --- Secondes (format 6 champs) ---
        if (hasSeconds && hour == "*" && dom == "*" && mon == "*" && dow == "*")
        {
            if (sec == "*" && min == "*")
                return "Lancement chaque seconde";

            if (TryParseStep(sec, out int sStep) && min == "*")
                return sStep == 1 ? "Lancement chaque seconde"
                                  : $"Lancement toutes les {sStep} secondes";
        }

        // --- Minutes ---
        if (hour == "*" && dom == "*" && mon == "*" && dow == "*")
        {
            if (min == "*")
                return "Lancement chaque minute";

            if (TryParseStep(min, out int mStep))
                return mStep == 1 ? "Lancement chaque minute"
                                  : $"Lancement toutes les {mStep} minutes";
        }

        // --- Heures ---
        if (dom == "*" && mon == "*" && dow == "*" && IsZero(min))
        {
            if (hour == "*")
                return "Lancement chaque heure";

            if (TryParseStep(hour, out int hStep))
                return hStep == 1 ? "Lancement chaque heure"
                                  : $"Lancement toutes les {hStep} heures";
        }

        // --- Heure fixe ---
        if (int.TryParse(min, out int minVal) && int.TryParse(hour, out int hourVal))
        {
            string time = FormatTime(hourVal, minVal);
            string day  = BuildDayPart(dom, mon, dow);
            return $"Lancement {day} à {time}";
        }

        return $"Lancement selon planification CRON ({expression})";
    }

    private static string FormatTime(int hour, int min)
    {
        if (hour == 0  && min == 0) return "minuit";
        if (hour == 12 && min == 0) return "midi";
        return $"{hour:D2}h{min:D2}";
    }

    private static string BuildDayPart(string dom, string mon, string dow)
    {
        bool anyDom = dom is "*" or "?";
        bool anyMon = mon == "*";
        bool anyDow = dow is "*" or "?";

        if (anyDom && anyMon && anyDow)
            return "tous les jours";

        if (anyDom && anyMon && int.TryParse(dow, out int dowVal) && dowVal is >= 0 and <= 6)
            return $"chaque {_jours[dowVal]}";

        if (int.TryParse(dom, out int domVal) && anyMon && anyDow)
            return $"le {domVal}{(domVal == 1 ? "er" : "")} de chaque mois";

        if (anyDom && anyDow && int.TryParse(mon, out int monVal) && monVal is >= 1 and <= 12)
            return $"en {_mois[monVal]} chaque année";

        return "selon planification";
    }

    private static bool TryParseStep(string field, out int step)
    {
        step = 0;
        return field.StartsWith("*/") && int.TryParse(field[2..], out step) && step > 0;
    }

    private static bool IsZero(string field) => field is "0" or "00";
}
