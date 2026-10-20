using System.Globalization;

namespace MoneySite.Services;

/// Consistent US-dollar formatting regardless of the server's regional settings.
public static class Money
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Cents(decimal v) => v.ToString("C2", Us);
    public static string Whole(decimal v) => v.ToString("C0", Us);

    /// Compact axis label: $1.2k, -$3M.
    public static string Short(decimal v)
    {
        var a = Math.Abs(v);
        var s = a >= 1_000_000 ? (a / 1_000_000).ToString("0.#", Us) + "M"
              : a >= 1_000 ? (a / 1_000).ToString("0.#", Us) + "k"
              : a.ToString("0", Us);
        return (v < 0 ? "-$" : "$") + s;
    }
}
