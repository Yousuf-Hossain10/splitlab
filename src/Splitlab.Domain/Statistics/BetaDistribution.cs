namespace Splitlab.Domain.Statistics;

/// <summary>
/// A small deterministic random source (SplitMix64). We do not use <see cref="Random"/> so that a given seed gives the
/// same numbers on every runtime and version, which is what makes Monte Carlo results reproducible and testable (ADR-0006).
/// </summary>
internal sealed class SeededRandom(ulong seed)
{
    private ulong _state = seed;
    private double? _spareNormal;

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A uniform number in the open interval (0, 1).</summary>
    public double NextOpenUnit() => ((NextUInt64() >> 11) + 0.5) * (1.0 / (1UL << 53));

    public double NextNormal()
    {
        if (_spareNormal is { } spare)
        {
            _spareNormal = null;
            return spare;
        }

        double radius = Math.Sqrt(-2 * Math.Log(NextOpenUnit()));
        double angle = 2 * Math.PI * NextOpenUnit();
        _spareNormal = radius * Math.Sin(angle);
        return radius * Math.Cos(angle);
    }

    /// <summary>Gamma(shape, 1) by Marsaglia and Tsang's method.</summary>
    public double NextGamma(double shape)
    {
        if (shape < 1)
        {
            return NextGamma(shape + 1) * Math.Pow(NextOpenUnit(), 1 / shape);
        }

        double d = shape - (1.0 / 3);
        double c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double x = NextNormal();
            double v = 1 + (c * x);
            if (v <= 0)
            {
                continue;
            }

            v = v * v * v;
            double u = NextOpenUnit();
            if (u < 1 - (0.0331 * x * x * x * x) || Math.Log(u) < (0.5 * x * x) + (d * (1 - v + Math.Log(v))))
            {
                return d * v;
            }
        }
    }
}

/// <summary>A Beta(alpha, beta) distribution: the posterior of a conversion rate under a Beta prior.</summary>
internal readonly record struct BetaDistribution(double Alpha, double Beta)
{
    public double Mean => Alpha / (Alpha + Beta);

    public double Cdf(double x) => SpecialFunctions.RegularizedIncompleteBeta(x, Alpha, Beta);

    public double Quantile(double p) => SpecialFunctions.BetaQuantile(p, Alpha, Beta);

    public double Pdf(double x)
    {
        if (x <= 0 || x >= 1)
        {
            return 0;
        }

        double logPdf = ((Alpha - 1) * Math.Log(x)) + ((Beta - 1) * double.LogP1(-x))
            - (SpecialFunctions.LogGamma(Alpha) + SpecialFunctions.LogGamma(Beta) - SpecialFunctions.LogGamma(Alpha + Beta));
        return Math.Exp(logPdf);
    }

    public double Sample(SeededRandom random)
    {
        double x = random.NextGamma(Alpha);
        double y = random.NextGamma(Beta);
        return x / (x + y);
    }
}
