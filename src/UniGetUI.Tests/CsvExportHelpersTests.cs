using UniGetUI.Interface;

namespace UniGetUI.Tests;

public sealed class CsvExportHelpersTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+SUM(A1:A2)", "'+SUM(A1:A2)")]
    public void EscapeField_ProducesSafeCsv(string input, string expected)
    {
        Assert.Equal(expected, CsvExportHelpers.EscapeField(input));
    }
}
