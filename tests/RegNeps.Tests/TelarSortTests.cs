using RegNeps.Application.Analytics;

namespace RegNeps.Tests;

public class TelarSortTests
{
    [Fact]
    public void TelarKeyComparer_Orders_Numeric_Codes_Naturally()
    {
        var keys = new[] { "104", "003", "801", "004", "103", "912" };
        var ordered = keys.OrderBy(k => k, TelarKeyComparer.Instance).ToArray();
        Assert.Equal(new[] { "003", "004", "103", "104", "801", "912" }, ordered);
    }

    [Fact]
    public void ByTelar_In_Summary_Uses_Natural_Order()
    {
        // El comparador es el usado por AnalyticsService para ByTelar.
        var groups = new[]
        {
            new GroupSummary("104", 50, 555, 1, 50, 0, 1),
            new GroupSummary("003", 38, 422, 1, 38, 0, 0),
            new GroupSummary("801", 40, 444, 1, 40, 0, 0),
        };

        var byTelar = groups.OrderBy(x => x.Key, TelarKeyComparer.Instance).Select(x => x.Key).ToArray();
        Assert.Equal(new[] { "003", "104", "801" }, byTelar);
    }
}
