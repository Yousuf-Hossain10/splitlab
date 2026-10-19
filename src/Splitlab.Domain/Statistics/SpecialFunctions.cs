namespace Splitlab.Domain.Statistics;

/// <summary>
/// The few special functions the statistics need, implemented here so the domain stays dependency-free.
/// Algorithms follow the standard continued-fraction and series expansions (Lanczos, Lentz).
/// </summary>
internal static class SpecialFunctions
{
    private const double Epsilon = 1e-15;
    private const double Tiny = 1e-300;
    private const int MaxIterations = 200_000;

    private static readonly double[] Lanczos =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    ];

    /// <summary>Natural log of the gamma function, for x &gt; 0.</summary>
    public static double LogGamma(double x)
    {
        if (x < 0.5)
        {
            // Reflection formula.
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        }

        x -= 1;
        double a = Lanczos[0];
        double t = x + 7.5;
        for (int i = 1; i < Lanczos.Length; i++)
        {
            a += Lanczos[i] / (x + i);
        }

        return (0.5 * Math.Log(2 * Math.PI)) + ((x + 0.5) * Math.Log(t)) - t + Math.Log(a);
    }

    /// <summary>Regularized incomplete beta function I_x(a, b): the CDF of a Beta(a, b) variable at x.</summary>
    public static double RegularizedIncompleteBeta(double x, double a, double b)
    {
        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        double logFront = LogGamma(a + b) - LogGamma(a) - LogGamma(b) + (a * Math.Log(x)) + (b * double.LogP1(-x));
        double front = Math.Exp(logFront);

        // The continued fraction converges fastest on the side of the mean; use symmetry for the other side.
        return x < (a + 1) / (a + b + 2)
            ? front * BetaContinuedFraction(x, a, b) / a
            : 1 - (front * BetaContinuedFraction(1 - x, b, a) / b);
    }

    /// <summary>The x with I_x(a, b) = p, found by bisection (robust, and fast enough for the handful of calls we make).</summary>
    public static double BetaQuantile(double p, double a, double b)
    {
        double low = 0;
        double high = 1;
        for (int i = 0; i < 100; i++)
        {
            double mid = 0.5 * (low + high);
            if (RegularizedIncompleteBeta(mid, a, b) < p)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return 0.5 * (low + high);
    }

    /// <summary>Upper regularized incomplete gamma Q(a, x) = 1 - P(a, x).</summary>
    public static double RegularizedGammaQ(double a, double x)
    {
        if (x <= 0)
        {
            return 1;
        }

        return x < a + 1 ? 1 - GammaPSeries(a, x) : GammaQContinuedFraction(a, x);
    }

    /// <summary>P(X &gt; x) for a chi-square variable with <paramref name="degreesOfFreedom"/> degrees of freedom.</summary>
    public static double ChiSquareSurvival(double x, int degreesOfFreedom) =>
        double.IsPositiveInfinity(x) ? 0 : RegularizedGammaQ(degreesOfFreedom / 2.0, x / 2.0);

    private static double BetaContinuedFraction(double x, double a, double b)
    {
        double qab = a + b;
        double qap = a + 1;
        double qam = a - 1;
        double c = 1;
        double d = 1 - (qab * x / qap);
        if (Math.Abs(d) < Tiny)
        {
            d = Tiny;
        }

        d = 1 / d;
        double h = d;

        for (int m = 1; m <= MaxIterations; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + (aa * d);
            if (Math.Abs(d) < Tiny)
            {
                d = Tiny;
            }

            c = 1 + (aa / c);
            if (Math.Abs(c) < Tiny)
            {
                c = Tiny;
            }

            d = 1 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + (aa * d);
            if (Math.Abs(d) < Tiny)
            {
                d = Tiny;
            }

            c = 1 + (aa / c);
            if (Math.Abs(c) < Tiny)
            {
                c = Tiny;
            }

            d = 1 / d;
            double delta = d * c;
            h *= delta;

            if (Math.Abs(delta - 1) < Epsilon)
            {
                return h;
            }
        }

        throw new InvalidOperationException("Incomplete beta did not converge.");
    }

    private static double GammaPSeries(double a, double x)
    {
        double ap = a;
        double sum = 1 / a;
        double term = sum;
        for (int n = 0; n < MaxIterations; n++)
        {
            ap += 1;
            term *= x / ap;
            sum += term;
            if (Math.Abs(term) < Math.Abs(sum) * Epsilon)
            {
                return sum * Math.Exp(-x + (a * Math.Log(x)) - LogGamma(a));
            }
        }

        throw new InvalidOperationException("Incomplete gamma series did not converge.");
    }

    private static double GammaQContinuedFraction(double a, double x)
    {
        double b = x + 1 - a;
        double c = 1 / Tiny;
        double d = 1 / b;
        double h = d;
        for (int i = 1; i <= MaxIterations; i++)
        {
            double an = -i * (i - a);
            b += 2;
            d = (an * d) + b;
            if (Math.Abs(d) < Tiny)
            {
                d = Tiny;
            }

            c = b + (an / c);
            if (Math.Abs(c) < Tiny)
            {
                c = Tiny;
            }

            d = 1 / d;
            double delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < Epsilon)
            {
                return Math.Exp(-x + (a * Math.Log(x)) - LogGamma(a)) * h;
            }
        }

        throw new InvalidOperationException("Incomplete gamma continued fraction did not converge.");
    }
}
