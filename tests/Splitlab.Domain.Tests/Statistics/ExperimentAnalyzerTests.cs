using Splitlab.Domain.Statistics;

namespace Splitlab.Domain.Tests.Statistics;

public class ExperimentAnalyzerTests
{
    private static VariationData[] Two(long controlExposed, long controlConverted, long treatmentExposed, long treatmentConverted) =>
    [
        new("control", 5_000, controlExposed, controlConverted),
        new("treatment", 5_000, treatmentExposed, treatmentConverted),
    ];

    /// <summary>Composite Simpson's rule on [0, 1].</summary>
    private static double Integrate(Func<double, double> f, int intervals = 40_000)
    {
        double h = 1.0 / intervals;
        double sum = f(0) + f(1);
        for (int i = 1; i < intervals; i++)
        {
            sum += f(i * h) * (i % 2 == 1 ? 4 : 2);
        }

        return sum * h / 3;
    }

    // ---- correctness against closed forms ----

    [Theory]
    [InlineData(100, 1000, 120, 1000)]
    [InlineData(40, 400, 52, 410)]
    [InlineData(10, 50, 9, 60)]
    public void ProbabilityToBeatControlMatchesNumericalIntegration(long cc, long ce, long tc, long te)
    {
        // For two variations, P(B > A) = integral of pdf_B(x) * CDF_A(x) dx: an independent route to the same number.
        var control = new BetaDistribution(1 + cc, 1 + ce - cc);
        var treatment = new BetaDistribution(1 + tc, 1 + te - tc);
        double exact = Integrate(x => treatment.Pdf(x) * control.Cdf(x));

        ExperimentAnalysis analysis = ExperimentAnalyzer.Analyze(Two(ce, cc, te, tc));

        Assert.Equal(exact, analysis.Variations[1].ProbabilityToBeatControl!.Value, 2);
    }

    [Theory]
    [InlineData(100, 1000, 120, 1000)]
    [InlineData(40, 400, 52, 410)]
    public void ExpectedLossMatchesNumericalIntegration(long cc, long ce, long tc, long te)
    {
        // E[max(A - B, 0)] = integral over y of pdf_B(y) * E_A[(X - y)+], where E_A[(X - y)+] has the closed form
        // mean_A * (1 - I_y(a + 1, b)) - y * (1 - I_y(a, b)).
        var a = new BetaDistribution(1 + cc, 1 + ce - cc);
        var b = new BetaDistribution(1 + tc, 1 + te - tc);
        double inner(double y) =>
            (a.Mean * (1 - SpecialFunctions.RegularizedIncompleteBeta(y, a.Alpha + 1, a.Beta))) - (y * (1 - a.Cdf(y)));
        double lossOfChoosingTreatment = Integrate(y => b.Pdf(y) * inner(y));

        ExperimentAnalysis analysis = ExperimentAnalyzer.Analyze(Two(ce, cc, te, tc));

        Assert.Equal(lossOfChoosingTreatment, analysis.Variations[1].ExpectedLoss, 4);
    }

    [Fact]
    public void CredibleIntervalsAreWellCalibrated()
    {
        // Simulate many experiments with a known true rate: a 95% interval should contain it about 95% of the time.
        var random = new SeededRandom(2026);
        const double trueRate = 0.12;
        const int users = 400;
        const int trials = 800;
        int covered = 0;

        for (int t = 0; t < trials; t++)
        {
            long converted = 0;
            for (int i = 0; i < users; i++)
            {
                if (random.NextOpenUnit() < trueRate)
                {
                    converted++;
                }
            }

            var posterior = new BetaDistribution(1 + converted, 1 + users - converted);
            if (posterior.Quantile(0.025) <= trueRate && trueRate <= posterior.Quantile(0.975))
            {
                covered++;
            }
        }

        Assert.InRange((double)covered / trials, 0.93, 0.97);
    }

    [Fact]
    public void ProbabilitiesAreCoherent()
    {
        var variations = new VariationData[]
        {
            new("a", 3_000, 1000, 100),
            new("b", 3_000, 1000, 110),
            new("c", 4_000, 1300, 150),
        };

        ExperimentAnalysis analysis = ExperimentAnalyzer.Analyze(variations);

        Assert.Equal(1.0, analysis.Variations.Sum(v => v.ProbabilityBest), 9);
        Assert.All(analysis.Variations, v => Assert.True(v.ExpectedLoss >= 0));
        Assert.All(analysis.Variations, v => Assert.InRange(v.CredibleLow, 0, v.PosteriorMean));
        Assert.All(analysis.Variations, v => Assert.InRange(v.CredibleHigh, v.PosteriorMean, 1));
        // The variation most likely to be best is the one with the smallest expected loss.
        Assert.Equal(
            analysis.Variations.MaxBy(v => v.ProbabilityBest)!.Key,
            analysis.Variations.MinBy(v => v.ExpectedLoss)!.Key);
    }

    [Fact]
    public void IdenticalDataIsACoinFlip()
    {
        ExperimentAnalysis analysis = ExperimentAnalyzer.Analyze(Two(1000, 100, 1000, 100));

        Assert.Equal(0.5, analysis.Variations[1].ProbabilityToBeatControl!.Value, 1);
        Assert.Equal(analysis.Variations[0].ExpectedLoss, analysis.Variations[1].ExpectedLoss, 3);
        Assert.Equal(RecommendationKind.Inconclusive, analysis.Recommendation.Kind);
    }

    [Fact]
    public void ReportsTheObservedRateAndShrunkPosteriorMean()
    {
        VariationResult control = ExperimentAnalyzer.Analyze(Two(200, 50, 200, 50)).Variations[0];

        Assert.Equal(0.25, control.ObservedRate);
        Assert.Equal(51.0 / 202, control.PosteriorMean, 12); // Beta(1 + 50, 1 + 150)
        Assert.True(control.IsControl);
        Assert.Null(control.ProbabilityToBeatControl);
    }

    [Fact]
    public void ResultsAreReproducibleAndDependOnTheSeed()
    {
        VariationData[] data = Two(1000, 100, 1000, 115);

        ExperimentAnalysis first = ExperimentAnalyzer.Analyze(data);
        ExperimentAnalysis second = ExperimentAnalyzer.Analyze(data);
        ExperimentAnalysis otherSeed = ExperimentAnalyzer.Analyze(data, new AnalysisOptions { Seed = 1 });

        Assert.Equal(first.Variations[1].ProbabilityToBeatControl, second.Variations[1].ProbabilityToBeatControl);
        Assert.Equal(first.Variations[1].ExpectedLoss, second.Variations[1].ExpectedLoss);
        Assert.NotEqual(first.Variations[1].ProbabilityToBeatControl, otherSeed.Variations[1].ProbabilityToBeatControl);
    }

    [Fact]
    public void ZeroExposuresDoNotBreakTheMath()
    {
        ExperimentAnalysis analysis = ExperimentAnalyzer.Analyze(Two(0, 0, 0, 0));

        Assert.All(analysis.Variations, v => Assert.Equal(0.5, v.PosteriorMean));
        Assert.Equal(RecommendationKind.KeepCollecting, analysis.Recommendation.Kind);
    }

    [Fact]
    public void AnInformativePriorPullsTheMean()
    {
        var options = new AnalysisOptions { PriorAlpha = 20, PriorBeta = 80 };

        VariationResult control = ExperimentAnalyzer.Analyze(Two(10, 5, 10, 5), options).Variations[0];

        Assert.Equal(25.0 / 110, control.PosteriorMean, 12);
    }

    // ---- guidance ----

    [Fact]
    public void ACleanWinIsCalledAndNamed()
    {
        ExperimentAnalysis analysis = ExperimentAnalyzer.Analyze(Two(2000, 200, 2000, 300));

        Assert.Equal(RecommendationKind.Leader, analysis.Recommendation.Kind);
        Assert.Equal("treatment", analysis.Recommendation.Variation);
        Assert.True(analysis.Variations[1].ProbabilityToBeatControl > 0.999);
        Assert.False(analysis.SampleRatio!.Mismatch);
    }

    [Fact]
    public void ACleanLossNamesTheControlAsLeader()
    {
        ExperimentAnalysis analysis = ExperimentAnalyzer.Analyze(Two(2000, 300, 2000, 200));

        Assert.Equal("control", analysis.Recommendation.Variation);
    }

    [Fact]
    public void TooLittleDataKeepsCollecting()
    {
        ExperimentAnalysis analysis = ExperimentAnalyzer.Analyze(Two(60, 20, 55, 5));

        Assert.Equal(RecommendationKind.KeepCollecting, analysis.Recommendation.Kind);
        Assert.Null(analysis.Recommendation.Variation);
        Assert.Contains("55", analysis.Recommendation.Message, StringComparison.Ordinal); // shows the sample size
    }

    [Fact]
    public void ASampleRatioMismatchOverridesAnyWinner()
    {
        // Treatment looks wonderful, but the 5000/5000 split came out 7000/3000: the data is not trustworthy.
        ExperimentAnalysis analysis = ExperimentAnalyzer.Analyze(Two(7000, 700, 3000, 600));

        Assert.True(analysis.SampleRatio!.Mismatch);
        Assert.Equal(RecommendationKind.Untrustworthy, analysis.Recommendation.Kind);
        Assert.Null(analysis.Recommendation.Variation);
    }

    [Fact]
    public void TheThresholdIsConfigurable()
    {
        VariationData[] data = Two(2000, 200, 2000, 230);

        Assert.Equal(RecommendationKind.Leader, ExperimentAnalyzer.Analyze(data, new AnalysisOptions { ExpectedLossThreshold = 0.01 }).Recommendation.Kind);
        Assert.Equal(RecommendationKind.Inconclusive, ExperimentAnalyzer.Analyze(data, new AnalysisOptions { ExpectedLossThreshold = 1e-9 }).Recommendation.Kind);
    }

    // ---- input validation ----

    [Fact]
    public void RejectsImpossibleInput()
    {
        Assert.Throws<ArgumentException>(() => ExperimentAnalyzer.Analyze([new("only", 10_000, 10, 1)]));
        Assert.Throws<ArgumentException>(() => ExperimentAnalyzer.Analyze(Two(10, 11, 10, 1))); // more conversions than users
        Assert.Throws<ArgumentException>(() => ExperimentAnalyzer.Analyze(Two(-1, 0, 10, 1)));
        Assert.Throws<ArgumentException>(() => ExperimentAnalyzer.Analyze([new("a", 5_000, 1, 0), new("a", 5_000, 1, 0)]));
        Assert.Throws<ArgumentException>(() => ExperimentAnalyzer.Analyze(Two(10, 1, 10, 1), new AnalysisOptions { PriorAlpha = 0 }));
    }

    [Fact]
    public void HandlesHugeSamples()
    {
        ExperimentAnalysis analysis = ExperimentAnalyzer.Analyze(Two(5_000_000, 500_000, 5_000_000, 505_000));

        Assert.Equal(RecommendationKind.Leader, analysis.Recommendation.Kind);
        Assert.Equal("treatment", analysis.Recommendation.Variation);
    }
}
