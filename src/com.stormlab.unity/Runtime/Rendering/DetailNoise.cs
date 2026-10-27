// The enhanced cloud detail's noise (StormRenderer.cloudDetail = Enhanced; a game adaptation, not in the web build):
// tiling 3-D billows, inverted Worley cells on 4, 8 and 16 cell lattices, N^3 bytes with a box mip chain. The web
// build's CloudNoise stays as it is (the volume's build uses it); this one is read only by the march's finest detail.
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace StormLab
{
    public static class DetailNoise
    {
        static Texture3D shared;

        /// <summary>The texture, made once and shared by every storm.</summary>
        public static Texture3D Get() => shared ? shared : shared = Make();

        // a fixed integer hash, so the billows are the same on every machine
        static uint Hash(uint x, uint y, uint z, uint s)
        {
            unchecked
            {
                uint h = x * 0x8DA6B343u ^ y * 0xD8163841u ^ z * 0xCB1AB31Fu ^ s * 0x165667B1u;
                h ^= h >> 13; h *= 0x5BD1E995u; h ^= h >> 15;
                return h;
            }
        }
        static float H01(uint h) => (h & 0xFFFFFF) / 16777216f;

        /// <summary>1 at a cell's feature point falling to 0 a cell away: round lobes, tiling every `cells`.</summary>
        static float Billow(float x, float y, float z, int cells, uint seed)
        {
            float fx = x * cells, fy = y * cells, fz = z * cells;
            int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy), iz = Mathf.FloorToInt(fz);
            float best = 9f;
            for (int dz = -1; dz <= 1; dz++) for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int cx = ix + dx, cy = iy + dy, cz = iz + dz;
                uint wx = (uint)((cx % cells + cells) % cells), wy = (uint)((cy % cells + cells) % cells), wz = (uint)((cz % cells + cells) % cells);
                float px = cx + H01(Hash(wx, wy, wz, seed)), py = cy + H01(Hash(wx, wy, wz, seed + 1)), pz = cz + H01(Hash(wx, wy, wz, seed + 2));
                float d = (px - fx) * (px - fx) + (py - fy) * (py - fy) + (pz - fz) * (pz - fz);
                if (d < best) best = d;
            }
            return 1f - Mathf.Clamp01(Mathf.Sqrt(best));
        }

        public static Texture3D Make(int N = 64)
        {
            var acc = new float[N * N * N];
            int[] lcs = { 4, 8, 16 }; float[] amps = { 0.72f, 0.21f, 0.07f };   // mostly lobes: finer cells read as grain
            Parallel.For(0, N, k =>
            {
                for (int j = 0; j < N; j++) for (int i = 0; i < N; i++)
                {
                    float x = (i + 0.5f) / N, y = (j + 0.5f) / N, z = (k + 0.5f) / N, v = 0f;
                    for (int o = 0; o < 3; o++) v += amps[o] * Billow(x, y, z, lcs[o], (uint)(17 + 7 * o));
                    acc[(k * N + j) * N + i] = v;
                }
            });
            // centred on 0.5 with a spread of 0.18, so it adds detail without moving the cloud's mean density
            double mean = 0, sq = 0;
            foreach (var a in acc) mean += a;
            mean /= acc.Length;
            foreach (var a in acc) sq += (a - mean) * (a - mean);
            float sd = (float)Math.Sqrt(sq / acc.Length);
            var lev = new float[acc.Length];
            for (int q = 0; q < acc.Length; q++) lev[q] = Mathf.Clamp01(0.5f + (acc[q] - (float)mean) / sd * 0.18f);
            // a mip chain (2x2x2 box, wrapping), as CloudNoise's: the march picks the level by the pixel's footprint
            int mips = 1; for (int n = N; n > 1 && n % 2 == 0; n /= 2) mips++;
            var tex = new Texture3D(N, N, N, TextureFormat.R8, mips)
            {
                name = "StormLab detail noise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, hideFlags = HideFlags.DontSave
            };
            int Nl = N;
            for (int m = 0; m < mips; m++)
            {
                var bytes = new byte[Nl * Nl * Nl];
                for (int q = 0; q < bytes.Length; q++) bytes[q] = (byte)Mathf.RoundToInt(255 * lev[q]);
                tex.SetPixelData(bytes, m);
                if (m == mips - 1) break;
                int Nh = Nl / 2; var nxt = new float[Nh * Nh * Nh];
                for (int k = 0; k < Nh; k++) for (int j = 0; j < Nh; j++) for (int i = 0; i < Nh; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < 8; c++) sum += lev[(((2 * k + (c >> 2)) % Nl) * Nl + (2 * j + ((c >> 1) & 1)) % Nl) * Nl + (2 * i + (c & 1)) % Nl];
                    nxt[(k * Nh + j) * Nh + i] = sum / 8f;
                }
                lev = nxt; Nl = Nh;
            }
            tex.Apply(false, true);
            return tex;
        }
    }
}
