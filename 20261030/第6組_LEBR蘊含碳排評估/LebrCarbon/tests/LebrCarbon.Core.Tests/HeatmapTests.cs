using LebrCarbon.Core.Visualization;

namespace LebrCarbon.Core.Tests;

public class HeatmapTests
{
    [Fact]
    public void Quantile_breaks_split_values_evenly()
    {
        var values = Enumerable.Range(1, 10).Select(i => (double)i).ToList();
        var b = HeatmapClassifier.Breaks(values);

        Assert.Equal(new double[] { 2, 4, 6, 8 }, b);
        Assert.Equal(0, HeatmapClassifier.Classify(2, b));
        Assert.Equal(1, HeatmapClassifier.Classify(3, b));
        Assert.Equal(4, HeatmapClassifier.Classify(10, b));

        var legend = HeatmapClassifier.Summarize(values, b);
        Assert.Equal(5, legend.Count);
        Assert.All(legend, c => Assert.Equal(2, c.Count));
        Assert.Equal(9, legend[^1].Min);
        Assert.Equal(10, legend[^1].Max);
    }

    [Fact]
    public void One_large_slab_does_not_flatten_the_rest()
    {
        // 9 個小構件 + 1 塊大樓板：分位數分級下，小構件仍分散在不同級距
        var values = new List<double> { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100_000 };
        var b = HeatmapClassifier.Breaks(values);

        Assert.Equal(4, HeatmapClassifier.Classify(100_000, b));
        Assert.True(HeatmapClassifier.Classify(10, b) < HeatmapClassifier.Classify(90, b));
    }

    [Fact]
    public void Zero_and_empty_values_are_ignored()
    {
        Assert.Empty(HeatmapClassifier.Breaks(Array.Empty<double>()));
        Assert.Empty(HeatmapClassifier.Breaks(new double[] { 0, 0 }));

        var b = HeatmapClassifier.Breaks(new double[] { 0, 5 });
        Assert.Equal(0, HeatmapClassifier.Classify(5, b));
        Assert.Single(HeatmapClassifier.Summarize(new double[] { 0, 5 }, b));
    }

    [Fact]
    public void Element_total_adds_both_parts()
    {
        Assert.Equal(150, new ElementCarbon(1, 100, 50).Total);
    }
}
