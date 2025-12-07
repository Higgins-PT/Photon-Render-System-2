using System.Collections.Generic;
using UnityEngine;

namespace PhotonGISystem2
{
    [ExecuteInEditMode]
    public class BakeProbeSHVisualizer : MonoBehaviour
    {
        [Header("Visualization Settings")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private Color sampleColor = Color.magenta;
        [SerializeField, Min(0f)] private float baseProbability = 0.01f;
        [SerializeField, Range(1, 256)] private int sampleDirectionCount = 64;
        [SerializeField, Min(0.01f)] private float lineLengthMultiplier = 10f;
        [SerializeField, Range(64, 4096)] private int pdfIntegralSampleCount = 1024;

        [Header("Temporal Filtering")]
        [SerializeField, Range(1, 256)] private int temporalSampleWindow = 50;

        [Header("PDF Debug")]
        [SerializeField] private Color pdfSampleColor = Color.cyan;
        [SerializeField, Min(0.01f)] private float pdfSampleLineLength = 10f;

        private readonly float[] _shCoefficients = new float[9];
        private readonly Queue<CascadedProbeManager.ProbeSHL2> _temporalSamples = new();
        private CascadedProbeManager.ProbeSHL2 _temporalSum;
        private readonly List<Vector3> _pdfSampleDirections = new();
        [SerializeField, HideInInspector] private int _pdfSampleAttemptCount;

        private void OnDrawGizmos()
        {
            if (!drawGizmos)
                return;

            if (!TryBuildPdf(true, out var pdf, out Vector3 origin))
                return;

            float baseline = Mathf.Max(0f, baseProbability);
            float multiplier = Mathf.Max(0.01f, lineLengthMultiplier);

            Gizmos.color = sampleColor;
            int directions = Mathf.Max(1, sampleDirectionCount);

            for (int i = 0; i < directions; i++)
            {
                Vector3 dir = FibonacciDirection(i, directions);
                float intensity = pdf.EvaluatePdf(dir) + baseline;
                float length = intensity * multiplier;
                Gizmos.DrawLine(origin, origin + dir * length);
            }

            if (_pdfSampleDirections.Count > 0)
            {
                Color previous = Gizmos.color;
                Gizmos.color = pdfSampleColor;
                float pdfLength = Mathf.Max(0.01f, pdfSampleLineLength);
                foreach (var dir in _pdfSampleDirections)
                {
                    Gizmos.DrawLine(origin, origin + dir * pdfLength);
                }
                Gizmos.color = previous;
            }
        }

        public void RunPdfSamplingTest()
        {
            if (!TryBuildPdf(false, out var pdf, out _))
            {
                Debug.LogWarning("[BakeProbeSHVisualizer] Cannot build PDF for sampling test.");
                return;
            }

            GeneratePdfSamples(pdf);
#if UNITY_EDITOR
            UnityEditor.SceneView.RepaintAll();
#endif
        }

        public int LastPdfSampleAttemptCount => _pdfSampleAttemptCount;
        public int LastPdfSampleCount => _pdfSampleDirections.Count;

        private bool TryBuildPdf(bool updateTemporalSample, out SHL2Pdf pdf, out Vector3 origin)
        {
            pdf = null;
            origin = transform.position;

            var probeManager = CascadedProbeManager.Instance;
            if (probeManager == null)
                return false;

            Camera camera = Camera.main;
            if (camera == null)
                return false;

            if (!probeManager.TryFindProbeIndices(camera, transform.position, out int cascadeIndex, out Vector3 cellCoords))
                return false;

            if (!TryGetInterpolatedProbeSH(probeManager, camera, cascadeIndex, cellCoords, out var interpolatedSh))
                return false;

            if (updateTemporalSample)
            {
                AddTemporalSample(interpolatedSh);
                interpolatedSh = GetTemporalAverage();
            }
            else if (_temporalSamples.Count > 0)
            {
                interpolatedSh = GetTemporalAverage();
            }

            CopyCoefficients(interpolatedSh);
            pdf = new SHL2Pdf(_shCoefficients, pdfIntegralSampleCount);
            return true;
        }

        private void AddTemporalSample(CascadedProbeManager.ProbeSHL2 sample)
        {
            _temporalSamples.Enqueue(sample);
            AccumulateWeightedSh(ref _temporalSum, sample, 1f);

            while (_temporalSamples.Count > Mathf.Max(1, temporalSampleWindow))
            {
                var removed = _temporalSamples.Dequeue();
                AccumulateWeightedSh(ref _temporalSum, removed, -1f);
            }
        }

        private CascadedProbeManager.ProbeSHL2 GetTemporalAverage()
        {
            int count = _temporalSamples.Count;
            if (count <= 0)
                return default;

            var avg = _temporalSum;
            DivideSh(ref avg, count);
            return avg;
        }

        private bool TryGetInterpolatedProbeSH(
            CascadedProbeManager manager,
            Camera camera,
            int cascadeIndex,
            Vector3 cellCoords,
            out CascadedProbeManager.ProbeSHL2 interpolatedSh)
        {
            interpolatedSh = default;
            int axisCount = Mathf.Max(1, manager.ProbesPerAxis);

            int minX = Mathf.Clamp(Mathf.FloorToInt(cellCoords.x), 0, axisCount - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(cellCoords.y), 0, axisCount - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt(cellCoords.z), 0, axisCount - 1);

            int maxX = Mathf.Min(minX + 1, axisCount - 1);
            int maxY = Mathf.Min(minY + 1, axisCount - 1);
            int maxZ = Mathf.Min(minZ + 1, axisCount - 1);

            float tx = Mathf.Clamp01(cellCoords.x - minX);
            float ty = Mathf.Clamp01(cellCoords.y - minY);
            float tz = Mathf.Clamp01(cellCoords.z - minZ);

            if (maxX == minX) tx = 0f;
            if (maxY == minY) ty = 0f;
            if (maxZ == minZ) tz = 0f;

            float oneMinusTx = 1f - tx;
            float oneMinusTy = 1f - ty;
            float oneMinusTz = 1f - tz;

            float totalWeight = 0f;
            for (int ix = 0; ix < 2; ix++)
            {
                int px = ix == 0 ? minX : maxX;
                float wx = ix == 0 ? oneMinusTx : tx;
                if (wx <= 0f)
                    continue;

                for (int iy = 0; iy < 2; iy++)
                {
                    int py = iy == 0 ? minY : maxY;
                    float wy = iy == 0 ? oneMinusTy : ty;
                    if (wy <= 0f)
                        continue;

                    for (int iz = 0; iz < 2; iz++)
                    {
                        int pz = iz == 0 ? minZ : maxZ;
                        float wz = iz == 0 ? oneMinusTz : tz;
                        float weight = wx * wy * wz;
                        if (weight <= 0f)
                            continue;

                        if (manager.TryGetProbeData(camera, cascadeIndex, px, py, pz, out _, out var sampleSh))
                        {
                            AccumulateWeightedSh(ref interpolatedSh, sampleSh, weight);
                            totalWeight += weight;
                        }
                    }
                }
            }

            if (totalWeight <= 0f)
                return false;

            NormalizeSh(ref interpolatedSh, totalWeight);
            return true;
        }

        private void GeneratePdfSamples(SHL2Pdf pdf)
        {
            _pdfSampleDirections.Clear();
            _pdfSampleAttemptCount = 0;

            int targetSamples = Mathf.Max(1, sampleDirectionCount);

            int maxAttempts = Mathf.Max(targetSamples * 1000, targetSamples);
            while (_pdfSampleDirections.Count < targetSamples && _pdfSampleAttemptCount < maxAttempts)
            {
                _pdfSampleAttemptCount++;
                Vector3 candidate = Random.onUnitSphere;
                float pdfValue = Mathf.Max(0f, pdf.EvaluatePdf(candidate));
                if (pdfValue <= 0f)
                    continue;

                float acceptanceProbability = Mathf.Clamp01(pdfValue);
                if (Random.value <= acceptanceProbability)
                {
                    _pdfSampleDirections.Add(candidate);
                }
            }
        }

        private void CopyCoefficients(CascadedProbeManager.ProbeSHL2 sh)
        {
            _shCoefficients[0] = sh.shL00;
            _shCoefficients[1] = sh.shL1_1;
            _shCoefficients[2] = sh.shL10;
            _shCoefficients[3] = sh.shL11;
            _shCoefficients[4] = sh.shL2_2;
            _shCoefficients[5] = sh.shL2_1;
            _shCoefficients[6] = sh.shL20;
            _shCoefficients[7] = sh.shL21;
            _shCoefficients[8] = sh.shL22;
        }

        private static void AccumulateWeightedSh(
            ref CascadedProbeManager.ProbeSHL2 accumulator,
            CascadedProbeManager.ProbeSHL2 sample,
            float weight)
        {
            accumulator.shL00 += sample.shL00 * weight;
            accumulator.shL1_1 += sample.shL1_1 * weight;
            accumulator.shL10 += sample.shL10 * weight;
            accumulator.shL11 += sample.shL11 * weight;
            accumulator.shL2_2 += sample.shL2_2 * weight;
            accumulator.shL2_1 += sample.shL2_1 * weight;
            accumulator.shL20 += sample.shL20 * weight;
            accumulator.shL21 += sample.shL21 * weight;
            accumulator.shL22 += sample.shL22 * weight;
        }

        private static void NormalizeSh(ref CascadedProbeManager.ProbeSHL2 sh, float weightSum)
        {
            if (weightSum <= 0f)
                return;

            float inv = 1f / weightSum;
            sh.shL00 *= inv;
            sh.shL1_1 *= inv;
            sh.shL10 *= inv;
            sh.shL11 *= inv;
            sh.shL2_2 *= inv;
            sh.shL2_1 *= inv;
            sh.shL20 *= inv;
            sh.shL21 *= inv;
            sh.shL22 *= inv;
        }

        private static void DivideSh(ref CascadedProbeManager.ProbeSHL2 sh, float divisor)
        {
            if (Mathf.Approximately(divisor, 0f))
                return;

            float inv = 1f / divisor;
            sh.shL00 *= inv;
            sh.shL1_1 *= inv;
            sh.shL10 *= inv;
            sh.shL11 *= inv;
            sh.shL2_2 *= inv;
            sh.shL2_1 *= inv;
            sh.shL20 *= inv;
            sh.shL21 *= inv;
            sh.shL22 *= inv;
        }

        private static Vector3 FibonacciDirection(int index, int total)
        {
            if (total <= 0)
                total = 1;

            float fi = (index + 0.5f) / total;
            float phi = 2.0f * Mathf.PI * index / 1.6180339887498948482f;
            float z = 1.0f - 2.0f * fi;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1.0f - z * z));
            float x = Mathf.Cos(phi) * r;
            float y = Mathf.Sin(phi) * r;
            return new Vector3(x, y, z);
        }
    }
}
