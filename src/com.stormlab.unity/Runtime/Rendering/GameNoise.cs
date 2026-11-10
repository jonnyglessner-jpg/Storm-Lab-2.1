// The game's cloud texture noise (user, 2026-11-07: "the cloud skins and textures need to have much better resolution and
// variety"; a game adaptation, not in the web build, read only by the march under cloudDetail = Enhanced). The web build's
// CloudNoise is 48^3 value noise (its finest lattice 16 cells a tile), which the volume's build and the coarse detail
// read: smoothstepped lattice values read as soft boxes when magnified, and every storm sampled it at the same place.
// This one is 128^3, four independent tiling fields, each with a box mip chain so the march can take the level that
// matches a pixel's footprint:
//   r  gradient (Perlin) fbm, 5 octaves from 4 cells a tile: smooth, organic, no lattice look (the smooth cloud's detail)
//   g  billows: inverted Worley fbm, 3 octaves from 5 cells (cauliflower lobes, not a lattice)
//   b  Perlin-Worley: the gradient fbm eroded by the billows (towers' turrets and the knuckled anvil root)
//   a  fibres: gradient noise stretched 6:1 along x, 4 octaves (ice: streaks drawn out along the wind aloft, sampled in
//      the anvil's own along/across frame; never a periodic pattern)
// Each storm samples it at its own offset (StormRenderer: from the plan's seed), so no two storms share a texture.
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace StormLab
{
    public static class GameNoise
    {
        static Texture3D shared;
        public const int Size = 128;

        /// <summary>The texture, made once and shared by every storm.</summary>
        public static Texture3D Get() => shared ? shared : shared = Make(Size);

        static uint Hash(int x, int y, int z, uint s)
        {
            unchecked
            {
                uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ (uint)z * 0xCB1AB31Fu ^ s * 0x165667B1u;
                h ^= h >> 13; h *= 0x5BD1E995u; h ^= h >> 15;
                return h;
            }
        }
        static float H01(uint h) => (h & 0xFFFFFF) / 16777216f;
        static int Wrap(int i, int n) => ((i % n) + n) % n;

        // 12 gradient directions (Perlin's improved noise), picked by hash
        static readonly float[] gx = { 1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0 };
        static readonly float[] gy = { 1, 1, -1, -1, 0, 0, 0, 0, 1, -1, 1, -1 };
        static readonly float[] gz = { 0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1 };
        static float Grad(int ix, int iy, int iz, float dx, float dy, float dz, int px, int py, int pz, uint seed)
        {
            int g = (int)(Hash(Wrap(ix, px), Wrap(iy, py), Wrap(iz, pz), seed) % 12u);
            return gx[g] * dx + gy[g] * dy + gz[g] * dz;
        }
        static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

        /// <summary>tiling gradient noise at (x, y, z) in [0, 1)^3 with (px, py, pz) cells a tile; about -1..1</summary>
        static float Perlin(float x, float y, float z, int px, int py, int pz, uint seed)
        {
            float fx = x * px, fy = y * py, fz = z * pz;
            int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy), iz = Mathf.FloorToInt(fz);
            float dx = fx - ix, dy = fy - iy, dz = fz - iz;
            float u = Fade(dx), v = Fade(dy), w = Fade(dz);
            float n000 = Grad(ix, iy, iz, dx, dy, dz, px, py, pz, seed), n100 = Grad(ix + 1, iy, iz, dx - 1, dy, dz, px, py, pz, seed);
            float n010 = Grad(ix, iy + 1, iz, dx, dy - 1, dz, px, py, pz, seed), n110 = Grad(ix + 1, iy + 1, iz, dx - 1, dy - 1, dz, px, py, pz, seed);
            float n001 = Grad(ix, iy, iz + 1, dx, dy, dz - 1, px, py, pz, seed), n101 = Grad(ix + 1, iy, iz + 1, dx - 1, dy, dz - 1, px, py, pz, seed);
            float n011 = Grad(ix, iy + 1, iz + 1, dx, dy - 1, dz - 1, px, py, pz, seed), n111 = Grad(ix + 1, iy + 1, iz + 1, dx - 1, dy - 1, dz - 1, px, py, pz, seed);
            float x00 = Mathf.Lerp(n000, n100, u), x10 = Mathf.Lerp(n010, n110, u), x01 = Mathf.Lerp(n001, n101, u), x11 = Mathf.Lerp(n011, n111, u);
            return Mathf.Lerp(Mathf.Lerp(x00, x10, v), Mathf.Lerp(x01, x11, v), w);
        }

        /// <summary>1 at a cell's feature point falling to 0 a cell away: round lobes, tiling every `cells`</summary>
        static float Billow(float x, float y, float z, int cells, uint seed)
        {
            float fx = x * cells, fy = y * cells, fz = z * cells;
            int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy), iz = Mathf.FloorToInt(fz);
            float best = 9f;
            for (int dz = -1; dz <= 1; dz++) for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int cx = ix + dx, cy = iy + dy, cz = iz + dz;
                int wx = Wrap(cx, cells), wy = Wrap(cy, cells), wz = Wrap(cz, cells);
                float qx = cx + H01(Hash(wx, wy, wz, seed)), qy = cy + H01(Hash(wx, wy, wz, seed + 1)), qz = cz + H01(Hash(wx, wy, wz, seed + 2));
                float d = (qx - fx) * (qx - fx) + (qy - fy) * (qy - fy) + (qz - fz) * (qz - fz);
                if (d < best) best = d;
            }
            return 1f - Mathf.Clamp01(Mathf.Sqrt(best));
        }

        public static Texture3D Make(int N)
        {
            var r = new float[N * N * N]; var g = new float[N * N * N]; var b = new float[N * N * N]; var a = new float[N * N * N];
            Parallel.For(0, N, k =>
            {
                for (int j = 0; j < N; j++) for (int i = 0; i < N; i++)
                {
                    float x = (i + 0.5f) / N, y = (j + 0.5f) / N, z = (k + 0.5f) / N;
                    int idx = (k * N + j) * N + i;
                    // r: 5 octaves of gradient noise from 4 cells
                    float p = 0f, amp = 0.5f, sum = 0f; int c = 4;
                    for (int o = 0; o < 5; o++) { p += amp * Perlin(x, y, z, c, c, c, (uint)(101 + 13 * o)); sum += amp; amp *= 0.5f; c *= 2; }
                    p /= sum;
                    // g: billows from 5 cells
                    float bw = 0.62f * Billow(x, y, z, 5, 211) + 0.27f * Billow(x, y, z, 10, 223) + 0.11f * Billow(x, y, z, 20, 239);
                    // a: fibres, 6:1 (x the along-wind axis)
                    float fb = 0f; amp = 0.5f; sum = 0f; int cx = 2, cy = 12;
                    for (int o = 0; o < 4; o++) { fb += amp * Perlin(x, y, z, cx, cy, cy, (uint)(307 + 17 * o)); sum += amp; amp *= 0.55f; cx *= 2; cy *= 2; }
                    fb /= sum;
                    r[idx] = p; g[idx] = bw; a[idx] = fb;
                    // b: Perlin-Worley (the gradient noise remapped by the billows: lobes with soft, eroded edges)
                    float pr = Mathf.Clamp01(0.5f + 0.9f * p);
                    b[idx] = Mathf.Clamp01((pr - (1f - bw)) / Mathf.Max(bw, 1e-3f) * 0.6f + 0.4f * bw);
                }
            });
            // each field centred on 0.5 with a spread of 0.18, so it adds detail without moving the cloud's mean density
            void Normalise(float[] f)
            {
                double mean = 0, sq = 0;
                foreach (var v in f) mean += v;
                mean /= f.Length;
                foreach (var v in f) sq += (v - mean) * (v - mean);
                float sd = (float)Math.Sqrt(sq / f.Length), k = 0.18f / Mathf.Max(sd, 1e-4f);
                for (int i = 0; i < f.Length; i++) f[i] = Mathf.Clamp01(0.5f + (f[i] - (float)mean) * k);
            }
            Normalise(r); Normalise(g); Normalise(b); Normalise(a);
            var px = new Color32[N * N * N];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)(r[i] * 255f + 0.5f), (byte)(g[i] * 255f + 0.5f), (byte)(b[i] * 255f + 0.5f), (byte)(a[i] * 255f + 0.5f));
            var tex = new Texture3D(N, N, N, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, name = "storm game noise", hideFlags = HideFlags.DontSave
            };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }
    }
}
