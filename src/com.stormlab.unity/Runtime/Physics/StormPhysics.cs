// C# port of StormLab's environment physics (src/physics/constants.ts, thermo.ts, winds.ts, environment.ts, storm.ts).
// StormLab: https://github.com/jonnyglessner-jpg/StormLab (MIT). Kept line-for-line with the TypeScript so the two
// can be diffed; SI units throughout.
using System;
using System.Collections.Generic;

namespace StormLab
{
    public static class C
    {
        public const double RD = 287.04, CP = 1005.7, G = 9.81, EPS = 0.622, LV = 2.501e6, P00 = 1e5;
        /// <summary>Vertical grid of the environment profiles: 0-16 km every 100 m.</summary>
        public const double DZ = 100; public const int NZ = 161;
        public const double MPH = 0.44704, KT = 0.514444, FT = 0.3048;
        public static double Clamp(double v, double a, double b) => Math.Max(a, Math.Min(b, v));
        public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }

    public struct Vec2
    {
        public double x, y;
        public Vec2(double x, double y) { this.x = x; this.y = y; }
    }

    // ------------------------------------------------------------------ thermo.ts
    public class Sounding
    {
        public double[] z = new double[C.NZ], T = new double[C.NZ], q = new double[C.NZ], p = new double[C.NZ];
        public int kt; public double ztrop;
    }

    public class Parcel
    {
        public double[] Tp, Tvp, B;
        public double cape, cin, cape3, lcl; public double? lfc, el;
        public double thm, qm;
    }

    public class EnvInput
    {
        public double T0F, TdF, lapse, cap, moist;   // thermo (moist in m)
        public double llj, shear, upper;             // winds, m/s
    }

    public static class Thermo
    {
        public static double Esat(double T) => 611.2 * Math.Exp(17.67 * (T - 273.15) / (T - 29.65));
        public static double Qsat(double p, double T) { double e = Esat(T); return C.EPS * e / Math.Max(p - e, 1); }
        public static double TdFromQ(double p, double q)
        {
            double e = q * p / (C.EPS + q), l = Math.Log(e / 611.2);
            return 273.15 + 243.5 * l / (17.67 - l);
        }
        public static double F2K(double f) => (f - 32) / 1.8 + 273.15;

        public static Sounding MakeSounding(EnvInput o)
        {
            var s = new Sounding(); var z = s.z; var T = s.T; var q = s.q; var p = s.p;
            double T0 = F2K(o.T0F), Td0 = Math.Min(F2K(o.TdF), T0), P0 = 98000;
            double zml = C.Clamp(125 * (T0 - Td0), 700, 3000), md = C.Clamp(o.moist, 0, 2500), zm = zml + md;
            double capD = 300, zcap = zm + capD, ztrop0 = 12500;
            double z3 = Math.Max(3000, zcap + 200);
            double Tml = T0 - 0.0095 * zml, Tm = Tml - 0.0065 * md, Tcap = Tm + o.cap, T3 = Tcap - 0.0065 * (z3 - zcap), T6 = T3 - o.lapse / 1000 * (6000 - z3);
            for (int k = 0; k < C.NZ; k++)
            {
                double zz = k * C.DZ; z[k] = zz;
                double t;
                if (zz <= zml) t = T0 - 0.0095 * zz;
                else if (zz <= zm) t = Tml - 0.0065 * (zz - zml);
                else if (zz <= zcap) t = Tm + o.cap * (zz - zm) / capD;
                else if (zz <= z3) t = Tcap - 0.0065 * (zz - zcap);
                else if (zz <= 6000) t = T3 - o.lapse / 1000 * (zz - z3);
                else t = T6 - 0.0072 * (zz - 6000);
                T[k] = t;
            }
            int kt = C.NZ - 1;
            for (int k = 0; k < C.NZ; k++) if (T[k] <= 213 || z[k] >= ztrop0) { kt = k; break; }
            for (int k = kt + 1; k < C.NZ; k++) T[k] = T[kt] + 0.0015 * (z[k] - z[kt]);
            double q0 = Qsat(P0, Td0);
            for (int it = 0; it < 3; it++)
            {
                p[0] = P0;
                for (int k = 1; k < C.NZ; k++)
                {
                    double tv = 0.5 * (T[k] * (1 + 0.61 * q[k]) + T[k - 1] * (1 + 0.61 * q[k - 1]));
                    p[k] = p[k - 1] * Math.Exp(-C.G * C.DZ / (C.RD * tv));
                }
                for (int k = 0; k < C.NZ; k++)
                {
                    double zz = z[k];
                    if (zz <= zml) { q[k] = Math.Min(q0, Qsat(p[k], T[k]) * 0.98); continue; }
                    if (zz <= zm) { q[k] = Math.Min(q0, C.Lerp(0.9, 0.85, (zz - zml) / md) * Qsat(p[k], T[k])); continue; }
                    double rh = zz <= zcap ? C.Lerp(0.85, 0.65, (zz - zm) / capD)
                        : zz <= z3 ? C.Lerp(0.65 - 0.04 * o.cap, 0.5, (zz - zcap) / (z3 - zcap))
                        : zz <= 6000 ? C.Lerp(0.5, 0.35 - 0.02 * (o.lapse - 6.5), (zz - z3) / (6000 - z3))
                        : Math.Max(0.15, 0.3 - 0.02 * (zz - 6000) / 1000);
                    q[k] = Math.Min(q0, rh * Qsat(p[k], T[k]));
                }
            }
            s.kt = kt; s.ztrop = z[kt];
            return s;
        }

        class Layer { public int k0, k1; public double area; }

        public static Parcel MakeParcel(Sounding s)
        {
            var z = s.z; var T = s.T; var q = s.q; var p = s.p;
            int n = (int)Math.Round(1000 / C.DZ);
            double thm = 0, qm = 0;
            for (int k = 0; k <= n; k++) { thm += T[k] * Math.Pow(C.P00 / p[k], C.RD / C.CP); qm += q[k]; }
            thm /= n + 1; qm /= n + 1;
            var Tp = new double[C.NZ]; var Tvp = new double[C.NZ]; var Tve = new double[C.NZ];
            double Gam(double t, double pp)
            {
                double qs = Qsat(pp, t);
                return C.G * (1 + C.LV * qs / (C.RD * t)) / (C.CP + C.LV * C.LV * qs * C.EPS / (C.RD * t * t));
            }
            double tp = thm * Math.Pow(p[0] / C.P00, C.RD / C.CP), qp = qm; int klcl = -1;
            for (int k = 0; k < C.NZ; k++)
            {
                if (k > 0)
                {
                    if (klcl < 0)
                    {
                        tp -= C.G / C.CP * C.DZ;
                        if (qm >= Qsat(p[k], tp)) klcl = k;
                    }
                    else
                    {
                        double pm = Math.Sqrt(p[k] * p[k - 1]), g1 = Gam(tp, p[k - 1]), g2 = Gam(tp - g1 * C.DZ, pm);
                        tp -= 0.5 * (g1 + g2) * C.DZ;
                    }
                    qp = klcl < 0 ? qm : Math.Min(qm, Qsat(p[k], tp));
                }
                Tp[k] = tp; Tvp[k] = tp * (1 + 0.61 * qp); Tve[k] = T[k] * (1 + 0.61 * q[k]);
            }
            double tdm = TdFromQ(p[0], qm), t0 = thm * Math.Pow(p[0] / C.P00, C.RD / C.CP);
            double lcl = C.Clamp(125 * (t0 - tdm), 0, 6000);
            var B = new double[C.NZ];
            for (int k = 0; k < C.NZ; k++) B[k] = C.G * (Tvp[k] - Tve[k]) / Tve[k];
            var layers = new List<Layer>();
            for (int k = 1; k < C.NZ; k++)
            {
                double b = 0.5 * (B[k] + B[k - 1]) * C.DZ;
                if (b > 0 && z[k] >= lcl - C.DZ)
                {
                    var last = layers.Count > 0 ? layers[layers.Count - 1] : null;
                    if (last != null && last.k1 == k - 1) { last.k1 = k; last.area += b; }
                    else layers.Add(new Layer { k0 = k - 1, k1 = k, area = b });
                }
            }
            Layer main = null;
            foreach (var l in layers) if (main == null || l.area > main.area) main = l;
            double cape = 0, cin = 0, cape3 = 0; int klfc = -1, kel = -1;
            if (main != null && main.area >= 1)
            {
                klfc = main.k0; kel = main.k1; cape = main.area;
                for (int k = klfc + 1; k <= kel; k++) if (z[k] <= 3000) cape3 += 0.5 * (B[k] + B[k - 1]) * C.DZ;
            }
            int kTop = klfc >= 0 ? klfc : C.NZ - 1;
            for (int k = 1; k <= kTop; k++) { double b = 0.5 * (B[k] + B[k - 1]) * C.DZ; if (b < 0) cin += b; }
            if (klfc < 0)
            {
                cin = 0;
                for (int k = 1; k < C.NZ && z[k] <= 3000; k++) { double b = 0.5 * (B[k] + B[k - 1]) * C.DZ; if (b < 0) cin += b; }
            }
            return new Parcel
            {
                Tp = Tp, Tvp = Tvp, B = B, cape = cape, cin = cin, cape3 = cape3, lcl = lcl,
                lfc = klfc >= 0 ? z[klfc] : (double?)null, el = kel >= 0 ? z[kel] : (double?)null, thm = thm, qm = qm
            };
        }

        public static double FreezingLevel(Sounding s)
        {
            for (int k = 0; k < C.NZ; k++) if (s.T[k] <= 273.15) return s.z[k];
            return 0;
        }
    }

    // ------------------------------------------------------------------ winds.ts
    public class Winds { public double[] u = new double[C.NZ], v = new double[C.NZ]; }
    public struct Bunkers { public Vec2 rm, lm, mw; }

    public static class WindProfile
    {
        public static Vec2 FromDir(double deg, double spd)
        {
            double a = deg * Math.PI / 180;
            return new Vec2(-spd * Math.Sin(a), -spd * Math.Cos(a));
        }

        public static Winds Make(EnvInput o)
        {
            double s0 = 2.5 + 0.35 * o.llj;
            Vec2 V0 = FromDir(150, s0), V05 = FromDir(170, 0.75 * o.llj + 1.5), V1 = FromDir(190, o.llj);
            Vec2 e = FromDir(240, 1);
            double bq = -(e.x * V0.x + e.y * V0.y), cq = V0.x * V0.x + V0.y * V0.y - o.shear * o.shear;
            double disc = bq * bq - cq, s6 = Math.Max(0, -bq + Math.Sqrt(Math.Max(disc, 0)));
            var V6 = new Vec2(s6 * e.x, s6 * e.y);
            double s1 = Math.Sqrt(V1.x * V1.x + V1.y * V1.y);
            Vec2 V3 = FromDir(215, 0.5 * (s1 + s6));
            Vec2 V10 = FromDir(245, o.upper), V12 = FromDir(247, o.upper * 0.95), V16 = FromDir(250, o.upper * 0.5);
            var cz = new double[] { 0, 500, 1000, 3000, 6000, 10000, 12000, 16000 };
            var cv = new Vec2[] { V0, V05, V1, V3, V6, V10, V12, V16 };
            int nc = cz.Length;
            var w = new Winds();
            for (int k = 0; k < C.NZ; k++)
            {
                double zz = k * C.DZ;
                int i = 0; while (i < nc - 2 && zz > cz[i + 1]) i++;
                double za = cz[i], zb = cz[i + 1], t = (zz - za) / (zb - za);
                Vec2 A = cv[i], Bv = cv[i + 1], Pm = cv[Math.Max(0, i - 1)], Pn = cv[Math.Min(nc - 1, i + 2)];
                for (int c = 0; c < 2; c++)
                {
                    double a_ = c == 0 ? A.x : A.y, b_ = c == 0 ? Bv.x : Bv.y, pm = c == 0 ? Pm.x : Pm.y, pn = c == 0 ? Pn.x : Pn.y;
                    double m0 = 0.5 * (b_ - pm) * (i > 0 ? 1 : 2) * 0.5, m1 = 0.5 * (pn - a_) * (i < nc - 2 ? 1 : 2) * 0.5;
                    double t2 = t * t, t3 = t2 * t;
                    double val = (2 * t3 - 3 * t2 + 1) * a_ + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * b_ + (t3 - t2) * m1;
                    if (c == 0) w.u[k] = val; else w.v[k] = val;
                }
            }
            return w;
        }

        public static double At(double[] arr, double zz)
        {
            double f = C.Clamp(zz / C.DZ, 0, C.NZ - 1); int k = Math.Min(C.NZ - 2, (int)Math.Floor(f));
            return arr[k] + (arr[k + 1] - arr[k]) * (f - k);
        }

        public static Vec2 LayerMean(Winds w, double z0, double z1)
        {
            double su = 0, sv = 0; int n = 0;
            for (double zz = z0; zz <= z1 + 1e-6; zz += 50) { su += At(w.u, zz); sv += At(w.v, zz); n++; }
            return new Vec2(su / n, sv / n);
        }

        public static Bunkers MakeBunkers(Winds w)
        {
            Vec2 mw = LayerMean(w, 0, 6000), lo = LayerMean(w, 0, 500), hi = LayerMean(w, 5500, 6000);
            double su = hi.x - lo.x, sv = hi.y - lo.y, m = Math.Sqrt(su * su + sv * sv); if (m == 0) m = 1;
            return new Bunkers
            {
                rm = new Vec2(mw.x + 7.5 * sv / m, mw.y - 7.5 * su / m),
                lm = new Vec2(mw.x - 7.5 * sv / m, mw.y + 7.5 * su / m),
                mw = mw
            };
        }

        public static double Srh(Winds w, Vec2 c, double top)
        {
            double h = 0;
            for (double zz = 0; zz < top; zz += 50)
            {
                double u0 = At(w.u, zz) - c.x, v0 = At(w.v, zz) - c.y, u1 = At(w.u, zz + 50) - c.x, v1 = At(w.v, zz + 50) - c.y;
                h += u1 * v0 - u0 * v1;
            }
            return h;
        }
    }

    // ------------------------------------------------------------------ environment.ts
    public class Environment
    {
        public EnvInput o; public Sounding s; public Parcel pc; public Winds w; public Bunkers bk;
        public double shear6, shear1, srh1, srh3, srAnvil, srMid, srLow, lapse36, scp, stp, brn;

        static double SrSpeed(Vec2 m, Vec2 c) => Math.Sqrt((m.x - c.x) * (m.x - c.x) + (m.y - c.y) * (m.y - c.y));

        public static Environment Make(EnvInput o)
        {
            var E = new Environment { o = o };
            E.s = Thermo.MakeSounding(o); E.pc = Thermo.MakeParcel(E.s); E.w = WindProfile.Make(o); E.bk = WindProfile.MakeBunkers(E.w);
            var w = E.w; var pc = E.pc;
            E.shear6 = Hyp(WindProfile.At(w.u, 6000) - w.u[0], WindProfile.At(w.v, 6000) - w.v[0]);
            E.shear1 = Hyp(WindProfile.At(w.u, 1000) - w.u[0], WindProfile.At(w.v, 1000) - w.v[0]);
            E.srh1 = WindProfile.Srh(w, E.bk.rm, 1000); E.srh3 = WindProfile.Srh(w, E.bk.rm, 3000);
            E.srAnvil = SrSpeed(WindProfile.LayerMean(w, 9000, 11000), E.bk.rm);
            E.srMid = SrSpeed(WindProfile.LayerMean(w, 4000, 6000), E.bk.rm);
            E.srLow = SrSpeed(WindProfile.LayerMean(w, 0, 1000), E.bk.rm);
            E.lapse36 = (E.s.T[30] - E.s.T[60]) / 3;
            double shTermS = E.shear6 < 10 ? 0 : Math.Min(E.shear6, 30) / 20;
            E.scp = Math.Max(0, (pc.cape / 1000) * (E.srh3 / 50) * shTermS);
            double lclT = pc.lcl < 1000 ? 1 : pc.lcl > 2000 ? 0 : (2000 - pc.lcl) / 1000;
            double shT = E.shear6 < 12.5 ? 0 : Math.Min(E.shear6, 30) / 20;
            double cinT = pc.cin > -50 ? 1 : pc.cin < -200 ? 0 : (200 + pc.cin) / 150;
            E.stp = Math.Max(0, (pc.cape / 1500) * lclT * (E.srh1 / 150) * shT * cinT);
            Vec2 mw = WindProfile.LayerMean(w, 0, 6000), lo = WindProfile.LayerMean(w, 0, 500);
            E.brn = pc.cape / Math.Max(0.5 * Math.Pow(Hyp(mw.x - lo.x, mw.y - lo.y), 2), 1);
            return E;
        }

        public static double Hyp(double a, double b) => Math.Sqrt(a * a + b * b);
    }

    // ------------------------------------------------------------------ storm.ts
    public enum StormMode { None, Capped, Pulse, Multicell, Marginal, Split, Supercell, WeakSuper }
    public enum Precip { Ordinary, LP, Classic, HP }

    public class StormDiag
    {
        public Environment E;
        public bool forms, capped;
        public StormMode mode = StormMode.None;
        public double w, wLow, vMid, vLow, hpness, fzl, hailIn, ptor, vMed, vSig = 0.3;
        public Precip precip = Precip.Ordinary;

        public static bool IsRotating(StormMode m) => m == StormMode.Supercell || m == StormMode.Split || m == StormMode.WeakSuper || m == StormMode.Marginal;

        public static StormDiag Make(Environment E)
        {
            var pc = E.pc;
            var o = new StormDiag { E = E, fzl = Thermo.FreezingLevel(E.s) };
            o.forms = pc.cape >= 150 && pc.cin > -200 && pc.el.HasValue && pc.el.Value - pc.lcl > 3000;
            o.capped = pc.cape >= 150 && pc.cin <= -200;
            if (!o.forms) { o.mode = o.capped ? StormMode.Capped : StormMode.None; return o; }

            double wpar = Math.Sqrt(2 * pc.cape);
            double dyn = C.Clamp(E.srh3 / 300, 0, 1) * C.Clamp((E.shear6 - 12) / 15, 0, 1);
            o.w = C.Clamp(0.5 * wpar * (1 + 0.35 * dyn), 5, 0.8 * wpar);
            o.wLow = 0.3 * o.w * (0.8 + 0.4 * dyn);

            double sh = E.shear6;
            StormMode mode;
            if (sh < 10) mode = StormMode.Pulse;
            else if (sh < 17.5) mode = StormMode.Multicell;
            else if (sh < 20) mode = StormMode.Marginal;
            else mode = E.srh3 < 90 ? StormMode.Split : StormMode.Supercell;
            if (o.w < 12 && mode != StormMode.Pulse) mode = sh >= 20 ? StormMode.WeakSuper : StormMode.Multicell;
            o.mode = mode;
            bool rotating = IsRotating(mode);
            double rf = mode == StormMode.Marginal ? 0.6 : mode == StormMode.WeakSuper ? 0.8 : rotating ? 1 : 0.25;

            o.vMid = rf * 1.25 * Math.Sqrt(Math.Max(E.srh3, 0)) * Math.Sqrt(C.Clamp(o.w / 30, 0.25, 1.5));
            double lclF = C.Clamp((2300 - pc.lcl) / 1300, 0.25, 1);
            o.vLow = rf * 1.05 * Math.Sqrt(Math.Max(E.srh1, 0)) * Math.Sqrt(C.Clamp(o.wLow / 10, 0.3, 1.6)) * (0.55 + 0.45 * lclF);

            double sra = E.srAnvil, moist = C.Clamp((E.o.TdF - 68) / 8, 0, 1);
            o.hpness = C.Clamp((24 - sra) / 10 + 0.35 * moist, 0, 1) - C.Clamp((sra - 28) / 8, 0, 1);
            o.precip = !rotating ? Precip.Ordinary : o.hpness > 0.5 ? Precip.HP : o.hpness < -0.35 ? Precip.LP : Precip.Classic;

            o.hailIn = Math.Max(0, Math.Pow(0.75 * o.w * (rotating ? 1 : 0.8) / 14, 2) / 2.54 - Math.Max(0, (o.fzl - 3800) / 1000) * 0.4);

            double stp = E.stp;
            o.ptor = rotating && mode != StormMode.Marginal
                ? 0.65 * (1 - Math.Exp(-0.45 * stp)) * (mode == StormMode.WeakSuper ? 0.7 : 1)
                : mode == StormMode.Marginal ? 0.15 * (1 - Math.Exp(-0.45 * stp)) : 0;
            o.vMed = (72 + 16 * Math.Log(1 + stp)) * C.MPH;
            o.vSig = 0.3;
            return o;
        }

        /// <summary>Tornado core radius in yards for a target peak wind (tornadoParams in storm.ts, rc only).</summary>
        public double TornadoRadiusYd(double V)
        {
            double mph = V / C.MPH;
            double wet = C.Clamp((1300 - E.pc.lcl) / 800, 0, 1), meso = C.Clamp((vLow - 12) / 15, 0, 1);
            double rcYd = C.Clamp((35 + 1.5 * (mph - 65)) * (0.75 + 0.55 * wet + 0.4 * meso), 20, 450);
            return Math.Round(rcYd / 5) * 5;
        }

        /// <summary>Peak tornado wind (mph) for the median tornado, rounded like tornadoParams.</summary>
        public double MedianTornadoMph => Math.Round(vMed / C.MPH);
    }
}
