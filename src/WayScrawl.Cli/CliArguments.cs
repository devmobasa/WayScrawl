using System.Globalization;

namespace WayScrawl.Cli;

internal sealed class CliArguments
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public CliArguments(string[] arguments, string[] options, string[] flags)
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            string key = arguments[i];
            if (values.ContainsKey(key)) throw new ArgumentException("Repeated option: " + key);
            if (flags.Contains(key)) values[key] = "true";
            else if (options.Contains(key) && i + 1 < arguments.Length && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
                values[key] = arguments[++i];
            else throw new ArgumentException("Unknown option or missing value: " + key);
        }
    }
    public bool Has(string name) => values.ContainsKey(name);
    public string Get(string name, string fallback) => values.GetValueOrDefault(name, fallback);
    public string Require(string name) => values.TryGetValue(name, out var value) ? value : throw new ArgumentException("Required: " + name);
    public double Number(string name, double fallback)
    {
        if (!double.TryParse(Get(name, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new ArgumentException(name + " must be a finite number using '.' as the decimal separator.");
        return value;
    }
    public int Integer(string name, int fallback) => int.Parse(Get(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
}
