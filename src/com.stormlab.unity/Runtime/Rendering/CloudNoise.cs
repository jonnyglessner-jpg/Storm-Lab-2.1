// The web build's tiling 3-D value noise (src/render/noise.ts): three octaves on 4, 8 and 16 cell lattices, N^3 bytes, seeded
// with mulberry32(99) so the billows match the web build's exactly.
using UnityEngine;

namespace StormLab
{
    public static class CloudNoise
    {
        public static Texture3D Make(int N = 48)
        {
            var acc = new float[N * N * N];
            uint s = 99;
            float Rand()
            {
                unchecked
                {
                    s += 0x6D2B79F5u;
                    uint t = (s ^ (s >> 15)) * (1u | s);
                    t = (t + ((t ^ (t >> 7)) * (61u | t))) ^ t;
                    return (float)((t ^ (t >> 14)) / 4294967296.0);
                }
            }
            int[] lcs = { 4, 8, 16 }; float[] amps = { 0.5f, 0.3f, 0.2f };
            for (int o = 0; o < 3; o++)
            {
                int Lc = lcs[o]; float amp = amps[o];
                var lat = new float[Lc * Lc * Lc];
                for (int q = 0; q < lat.Length; q++) lat[q] = Rand();
                float Sm(float t) => t * t * (3 - 2 * t);
                float Lt(int a, int b, int c) => lat[((c % Lc) * Lc + (b % Lc)) * Lc + (a % Lc)];
                for (int k = 0; k < N; k++) for (int j = 0; j < N; j++) for (int i = 0; i < N; i++)
                {
                    float fx = i * Lc / (float)N, fy = j * Lc / (float)N, fz = k * Lc / (float)N;
                    int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy), z0 = Mathf.FloorToInt(fz);
                    float tx = Sm(fx - x0), ty = Sm(fy - y0), tz = Sm(fz - z0);
                    float c00 = Lt(x0, y0, z0) * (1 - tx) + Lt(x0 + 1, y0, z0) * tx, c10 = Lt(x0, y0 + 1, z0) * (1 - tx) + Lt(x0 + 1, y0 + 1, z0) * tx;
                    float c01 = Lt(x0, y0, z0 + 1) * (1 - tx) + Lt(x0 + 1, y0, z0 + 1) * tx, c11 = Lt(x0, y0 + 1, z0 + 1) * (1 - tx) + Lt(x0 + 1, y0 + 1, z0 + 1) * tx;
                    acc[(k * N + j) * N + i] += amp * ((c00 * (1 - ty) + c10 * ty) * (1 - tz) + (c01 * (1 - ty) + c11 * ty) * tz);
                }
            }
            var lev = new float[N * N * N];
            for (int q = 0; q < acc.Length; q++) lev[q] = Mathf.Clamp01((acc[q] - 0.5f) * 1.8f + 0.5f);
            // a mip chain (2x2x2 box, wrapping) from StormLab 2.0; the march samples level 0, as the web build does
            int mips = 1; for (int n = N; n > 1 && n % 2 == 0; n /= 2) mips++;
            var tex = new Texture3D(N, N, N, TextureFormat.R8, mips)
            {
                name = "StormLab noise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, hideFlags = HideFlags.DontSave
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
