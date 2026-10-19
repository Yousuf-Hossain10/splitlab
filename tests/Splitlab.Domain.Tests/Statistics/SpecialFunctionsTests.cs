using Splitlab.Domain.Statistics;

namespace Splitlab.Domain.Tests.Statistics;

public class SpecialFunctionsTests
{
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(2.0, 0.0)]
    [InlineData(5.0, 3.1780538303479458)] // ln(4!)
    [InlineData(0.5, 0.5723649429247001)] // ln(sqrt(pi))
    [InlineData(10.0, 12.801827480081469)] // ln(9!)
    public void LogGammaMatchesKnownValues(double x, double expected)
    {
        Assert.Equal(expected, SpecialFunctions.LogGamma(x), 10);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.77)]
    public void BetaCdfIsExactForSimpleShapes(double x)
    {
        Assert.Equal(x, SpecialFunctions.RegularizedIncompleteBeta(x, 1, 1), 12); // uniform
        Assert.Equal(Math.Pow(x, 3), SpecialFunctions.RegularizedIncompleteBeta(x, 3, 1), 12);
        Assert.Equal(1 - Math.Pow(1 - x, 4), SpecialFunctions.RegularizedIncompleteBeta(x, 1, 4), 12);
        Assert.Equal(0.5, SpecialFunctions.RegularizedIncompleteBeta(0.5, 7.5, 7.5), 12); // symmetric
    }

    [Theory]
    [InlineData(2, 5, 0.3)]
    [InlineData(10, 3, 0.8)]
    [InlineData(50, 60, 0.45)]
    [InlineData(101, 901, 0.1)]
    public void BetaCdfMatchesTheBinomialIdentity(int a, int b, double x)
    {
        // I_x(a, b) = P(Binomial(a + b - 1, x) >= a), computed exactly from the pmf in log space.
        int n = a + b - 1;
        double tail = 0;
        for (int j = a; j <= n; j++)
        {
            double logPmf = SpecialFunctions.LogGamma(n + 1) - SpecialFunctions.LogGamma(j + 1) - SpecialFunctions.LogGamma(n - j + 1)
                + (j * Math.Log(x)) + ((n - j) * Math.Log(1 - x));
            tail += Math.Exp(logPmf);
        }

        Assert.Equal(tail, SpecialFunctions.RegularizedIncompleteBeta(x, a, b), 9);
    }

    [Fact]
    public void BetaCdfCopesWithVeryLargeCounts()
    {
        // 2 million users: the posterior is extremely narrow around 0.1 (standard deviation about 0.0002).
        double a = 200_001;
        double b = 1_800_001;

        Assert.Equal(0.5, SpecialFunctions.RegularizedIncompleteBeta(a / (a + b), a, b), 2);
        Assert.True(SpecialFunctions.RegularizedIncompleteBeta(0.0985, a, b) < 1e-9);
        Assert.True(SpecialFunctions.RegularizedIncompleteBeta(0.1015, a, b) > 1 - 1e-9);
    }

    [Theory]
    [InlineData(0.025, 3, 9)]
    [InlineData(0.5, 40, 60)]
    [InlineData(0.975, 101, 901)]
    public void BetaQuantileInvertsTheCdf(double p, double a, double b)
    {
        double x = SpecialFunctions.BetaQuantile(p, a, b);

        Assert.Equal(p, SpecialFunctions.RegularizedIncompleteBeta(x, a, b), 10);
    }

    [Fact]
    public void UniformCredibleIntervalIsTheObviousOne()
    {
        var uniform = new BetaDistribution(1, 1);

        Assert.Equal(0.025, uniform.Quantile(0.025), 10);
        Assert.Equal(0.975, uniform.Quantile(0.975), 10);
    }

    [Theory]
    [InlineData(3.841458820694124, 1, 0.05)]
    [InlineData(5.991464547107979, 2, 0.05)]
    [InlineData(7.814727903251179, 3, 0.05)]
    [InlineData(18.307038053275146, 10, 0.05)]
    [InlineData(6.6348966010212145, 1, 0.01)]
    public void ChiSquareSurvivalMatchesCriticalValues(double x, int df, double expected)
    {
        Assert.Equal(expected, SpecialFunctions.ChiSquareSurvival(x, df), 9);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(4.0)]
    [InlineData(25.0)]
    public void ChiSquareSurvivalHasClosedFormsForTwoAndFourDegrees(double x)
    {
        Assert.Equal(Math.Exp(-x / 2), SpecialFunctions.ChiSquareSurvival(x, 2), 12);
        Assert.Equal(Math.Exp(-x / 2) * (1 + (x / 2)), SpecialFunctions.ChiSquareSurvival(x, 4), 12);
    }

    [Fact]
    public void ChiSquareSurvivalEdges()
    {
        Assert.Equal(1, SpecialFunctions.ChiSquareSurvival(0, 3));
        Assert.Equal(0, SpecialFunctions.ChiSquareSurvival(double.PositiveInfinity, 3));
    }
}

public class SeededRandomTests
{
    [Fact]
    public void SameSeedGivesTheSameSequenceAndDifferentSeedsDiffer()
    {
        var a = new SeededRandom(7);
        var b = new SeededRandom(7);
        var c = new SeededRandom(8);

        double[] first = Enumerable.Range(0, 5).Select(_ => a.NextOpenUnit()).ToArray();
        double[] second = Enumerable.Range(0, 5).Select(_ => b.NextOpenUnit()).ToArray();
        double[] other = Enumerable.Range(0, 5).Select(_ => c.NextOpenUnit()).ToArray();

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void GeneratorIsPinnedToKnownOutputs()
    {
        // Guards against anyone "improving" the generator: published results depend on these exact numbers.
        var random = new SeededRandom(0);

        Assert.Equal(0xE220A8397B1DCDAFUL, random.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, random.NextUInt64());
    }

    [Fact]
    public void UniformNumbersStayInsideTheOpenInterval()
    {
        var random = new SeededRandom(1);

        for (int i = 0; i < 100_000; i++)
        {
            double u = random.NextOpenUnit();
            Assert.InRange(u, double.Epsilon, 1 - 1e-16);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.5)]
    [InlineData(40.0)]
    [InlineData(5_000.0)]
    public void GammaSamplesHaveTheRightMeanAndVariance(double shape)
    {
        var random = new SeededRandom(99);
        const int n = 200_000;
        double sum = 0;
        double sumSquares = 0;
        for (int i = 0; i < n; i++)
        {
            double x = random.NextGamma(shape);
            sum += x;
            sumSquares += x * x;
        }

        double mean = sum / n;
        double variance = (sumSquares / n) - (mean * mean);

        Assert.Equal(shape, mean, shape * 0.01);
        Assert.Equal(shape, variance, shape * 0.03); // Gamma(k, 1) has mean = variance = k
    }

    [Fact]
    public void BetaSamplesMatchTheirDistribution()
    {
        var beta = new BetaDistribution(31, 171);
        var random = new SeededRandom(5);
        const int n = 200_000;
        int below = 0;
        double sum = 0;
        double median = beta.Quantile(0.5);
        for (int i = 0; i < n; i++)
        {
            double x = beta.Sample(random);
            sum += x;
            if (x < median)
            {
                below++;
            }
        }

        Assert.Equal(beta.Mean, sum / n, 3);
        Assert.Equal(0.5, (double)below / n, 2);
    }
}
