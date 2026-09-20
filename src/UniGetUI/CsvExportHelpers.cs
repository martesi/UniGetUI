namespace UniGetUI.Interface;

internal static class CsvExportHelpers
{
    internal static string EscapeField(string? field)
    {
        field ??= "";

        if (field.Length > 0 && "=+-@\t\r".IndexOf(field[0]) >= 0)
            field = "'" + field;

        if (field.IndexOfAny(['"', ',', '\n', '\r']) >= 0)
            return "\"" + field.Replace("\"", "\"\"") + "\"";

        return field;
    }
}
