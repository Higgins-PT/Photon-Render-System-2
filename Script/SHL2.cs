using System.Collections.Generic;
using System.Text;
using UnityEngine;
namespace PhotonGISystem2
{
    /// <summary>
    /// Real Spherical Harmonics up to 2nd order (L2).
    /// 9 basis functions Y_lm, using the common graphics convention:
    /// index:
    /// 0:  l=0, m=0
    /// 1:  l=1, m=-1
    /// 2:  l=1, m=0
    /// 3:  l=1, m=1
    /// 4:  l=2, m=-2
    /// 5:  l=2, m=-1
    /// 6:  l=2, m=0
    /// 7:  l=2, m=1
    /// 8:  l=2, m=2
    /// </summary>
    public static class SHL2
    {
        // Precomputed constants for real SH (Ramamoorthi & Hanrahan style)
        private const float c0 = 0.282095f;
        private const float c1 = 0.488603f;
        private const float c2 = 1.092548f;
        private const float c3 = 0.315392f;
        private const float c4 = 0.546274f;

        /// <summary>
        /// Evaluate 9 real SH basis functions (L2) for a given direction.
        /// dir will be normalized inside. outBasis length must be >= 9.
        /// </summary>
        public static void EvaluateBasis(Vector3 dir, float[] outBasis)
        {
            if (outBasis == null || outBasis.Length < 9)
            {
                Debug.LogError("SHL2.EvaluateBasis: outBasis must be length >= 9.");
                return;
            }

            if (dir.sqrMagnitude > 0.0f)
                dir.Normalize();
            else
                dir = Vector3.up;

            float x = dir.x;
            float y = dir.y;
            float z = dir.z;

            // L0
            outBasis[0] = c0;

            // L1
            outBasis[1] = c1 * y;                     // Y_1^-1
            outBasis[2] = c1 * z;                     // Y_1^0
            outBasis[3] = c1 * x;                     // Y_1^1

            // L2
            outBasis[4] = c2 * x * y;                 // Y_2^-2
            outBasis[5] = c2 * y * z;                 // Y_2^-1
            outBasis[6] = c3 * (3.0f * z * z - 1f);   // Y_2^0
            outBasis[7] = c2 * x * z;                 // Y_2^1
            outBasis[8] = c4 * (x * x - y * y);       // Y_2^2
        }

        /// <summary>
        /// Evaluate scalar value f(ω) = sum_i coeffs[i] * Y_i(ω).
        /// coeffs.Length must be >= 9.
        /// </summary>
        public static float EvaluateScalar(Vector3 dir, float[] coeffs)
        {
            if (coeffs == null || coeffs.Length < 9)
            {
                Debug.LogError("SHL2.EvaluateScalar: coeffs must be length >= 9.");
                return 0f;
            }

            float[] basis = new float[9];
            EvaluateBasis(dir, basis);

            float v = 0f;
            for (int i = 0; i < 9; i++)
                v += coeffs[i] * basis[i];

            return v;
        }
    }

    /// <summary>
    /// 用 L2 实球谐拟合一组 (direction, value) 样本，得到 9 个系数。
    /// </summary>
    public static class SHL2Fitter
    {
        /// <summary>
        /// 假设 directions 在球面上大致均匀分布。
        /// c_i ≈ 4π / N * Σ_k f(ω_k) Y_i(ω_k)
        /// </summary>
        public static float[] FitUniform(
            IReadOnlyList<Vector3> directions,
            IReadOnlyList<float> values)
        {
            if (directions == null || values == null)
            {
                Debug.LogError("SHL2Fitter.FitUniform: directions / values is null.");
                return null;
            }

            int n = directions.Count;
            if (n == 0 || n != values.Count)
            {
                Debug.LogError("SHL2Fitter.FitUniform: directions.Count must == values.Count and > 0.");
                return null;
            }

            float[] coeffs = new float[9];
            float[] basis = new float[9];

            for (int k = 0; k < n; k++)
            {
                Vector3 dir = directions[k];
                float value = values[k];

                SHL2.EvaluateBasis(dir, basis);

                for (int i = 0; i < 9; i++)
                {
                    coeffs[i] += value * basis[i];
                }
            }

            float factor = 4.0f * Mathf.PI / n;
            for (int i = 0; i < 9; i++)
            {
                coeffs[i] *= factor;
            }

            return coeffs;
        }
    }

    /// <summary>
    /// 标量 SH L2 pdf：
    /// 用 9 个 SH 系数构造 f(ω)，
    /// 然后 pdf(ω) = max(0, f(ω)) / ∫ max(0,f(ω)) dΩ
    /// 使用 Fibonacci sphere 粗略估计积分。
    /// </summary>
    public class SHL2Pdf
    {
        private const float GoldenRatio = 1.6180339887498948482f;

        public readonly float[] Coeffs = new float[9];

        /// <summary> pdf(ω) = max(0, raw(ω)) * Normalization </summary>
        public float Normalization { get; private set; } = 0f;

        public SHL2Pdf(float[] rawCoeffs, int sampleCount = 2048)
        {
            if (rawCoeffs == null || rawCoeffs.Length < 9)
            {
                Debug.LogError("SHL2Pdf: rawCoeffs must be length >= 9.");
                return;
            }

            for (int i = 0; i < 9; i++)
                Coeffs[i] = rawCoeffs[i];

            float integral = EstimateIntegral(sampleCount);

            if (integral > 0f)
            {
                Normalization = 1.0f / integral;
            }
            else
            {
                Normalization = 0f;
                StringBuilder coeffDump = new StringBuilder(256);
                for (int i = 0; i < Coeffs.Length; i++)
                {
                    if (i > 0)
                        coeffDump.Append(", ");

                    coeffDump.Append("c");
                    coeffDump.Append(i);
                    coeffDump.Append('=');
                    coeffDump.Append(Coeffs[i].ToString("G6"));
                }

                Debug.LogWarning($"SHL2Pdf: estimated integral <= 0, value is {integral}, pdf will be identically 0. Coeffs: {coeffDump}");
            }
        }

        /// <summary>
        /// 原始 SH 函数值 f(ω)。
        /// </summary>
        public float EvaluateRaw(Vector3 dir)
        {
            return SHL2.EvaluateScalar(dir, Coeffs);
        }

        /// <summary>
        /// pdf(ω) = max(0, f(ω)) / ∫ max(0,f(ω)) dΩ
        /// </summary>
        public float EvaluatePdf(Vector3 dir)
        {
            if (Normalization == 0f)
                return 0f;

            float raw = EvaluateRaw(dir);
            float g = Mathf.Max(0f, raw);
            return g * Normalization;
        }

        private float EstimateIntegral(int sampleCount)
        {
            if (sampleCount <= 0)
                sampleCount = 1;

            float sum = 0f;

            for (int i = 0; i < sampleCount; i++)
            {
                Vector3 dir = FibonacciDirection(i, sampleCount);
                float value = EvaluateRaw(dir);
                if (value > 0f)
                    sum += value;
            }

            float avg = sum / sampleCount;
            float integral = avg * (4.0f * Mathf.PI);
            return integral;
        }

        private static Vector3 FibonacciDirection(int i, int n)
        {
            float fi = (i + 0.5f) / n;
            float phi = 2.0f * Mathf.PI * i / GoldenRatio;
            float z = 1.0f - 2.0f * fi;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1.0f - z * z));

            float x = Mathf.Cos(phi) * r;
            float y = Mathf.Sin(phi) * r;

            return new Vector3(x, y, z);
        }
    }
}