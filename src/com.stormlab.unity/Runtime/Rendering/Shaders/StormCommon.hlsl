// Shared by the storm's compute passes and the lightning medium: the parameter block (StormDesign.cs, 23 float4 slots,
// then StormTornadoes.cs, 6 more for the detailed tornado, then StormSpin.cs, 2 of angles turned so far),
// noise, and the near-ground features. Ported from StormLab's src/storm3d/shaders.ts (WGSL, web build f5bd687, MIT):
// the SP struct, NOISE and NEAR blocks, kept line for line. Lengths are km in the storm's frame (origin at the volume
// centre, x east, y north, z up above ground).
#ifndef STORM_COMMON
#define STORM_COMMON

float4 _SP[32];
#define S_f      _SP[0]    // xy forward (storm motion) unit, zw left unit
#define S_base   _SP[1]    // x cloud base, y storm top (EL), z clock s, w storm time min
#define S_tower  _SP[2]    // x top height, y radius, z amount, w striation
#define S_tilt   _SP[3]    // xy updraft tilt km/km, z overshooting top, w mesocyclone spin rad/s (counter-clockwise)
#define S_anvil  _SP[4]    // xy direction, z length, w growth (0..1)
#define S_anvil2 _SP[5]    // x bottom, y top, z mammatus, w backshear km
#define S_wall   _SP[6]    // xy centre, z radius, w amount
#define S_wall2  _SP[7]    // x lowering, y spin rad/s, z tail cloud, w flanking-line maturity (0..1)
#define S_tail   _SP[8]    // xy inflow tail direction, z length, w amount
#define S_ffd    _SP[9]    // xy forward-flank core, z size scale, w amount
#define S_hail   _SP[10]   // xy hail core, z radius, w amount
#define S_rfd    _SP[11]   // x clear slot, y slot radius, z rear-flank rain, w HP wrap
#define S_flank  _SP[12]   // xy flanking-line direction, z growth, w tower count (up to 8)
#define S_tor    _SP[13]   // xy tornado at the ground, z amount, w type (0 slender .. 1 wedge)
#define S_tor2   _SP[14]   // x radius at ground, y radius at cloud base, z condensation reach (0..1), w rope-out
#define S_tor3   _SP[15]   // xy tornado top (under the wall cloud), z debris
#define S_up     _SP[16]   // xy updraft centre at cloud base, z middle-level widening (0..1), w late thin anvil (0..1)
#define S_grid   _SP[17]   // x cells across, y levels, z half-width km, w top km
#define S_rfdb   _SP[18]   // rear-flank downdraft surge: xy centre, z radius km, w clear slot / horseshoe amount
#define S_anti   _SP[19]   // southern curl of the surge: xy position, z anti-mesocyclone, w anticyclonic tornado
#define S_slab   _SP[20]   // build pass: x first z level of this slab (the build is spread over several frames)
#define S_tor4   _SP[21]   // second tornado: x central core radius km, y multi-vortex amount, z breakdown into a collar (0..1), w mode (0 = one funnel, else 1 + wedge amount)
#define S_tor5   _SP[22]   // second tornado: x orbit radius of the sub-vortices as a fraction of the wedge's, y their least radius km
                           // (detailed tornado: z their count, w sinuosity 0..1)
// ---- the detailed tornado (StormLab 2.0's, StormTornadoes.Apply); zero for the web build's tornado
#define S_tor6   _SP[23]   // x spin at the funnel's edge rad/s, y peak wind m/s, z radius of maximum wind km, w dust-cloud radius / ground radius
#define S_tor7   _SP[24]   // x rain wrap 0..1, y shape now (FunnelShape), z EF rating, w fraction of its life gone
#define S_tor8   _SP[25]   // the funnel's look: x brightness (0.55 grey .. 1.2 white), y skin roughness (0.2 laminar .. 1.5 torn), z translucency, w helical striations
#define S_tor9   _SP[26]   // xyz colour of the soil it lofts (linear albedo), w how far that dirt stains the funnel's lower part
#define S_tor10  _SP[27]   // sidewinder: xy direction its lower end is dragged in (unit), z drag km; w angle of the plane the funnel bends in
#define S_tor11  _SP[28]   // satellite tornado: xy position, z amount 0..1, w visible radius km
// ---- angles turned so far, rad, counter-clockwise positive (StormSpin.Advance: each rate added up frame by frame, not
// rate x clock; real-world v / r by default, the web build's rates for the website comparison)
#define S_spin   _SP[29]   // x mesocyclone, y funnel skin, z central core, w suction-vortex wisps
#define S_spin2  _SP[30]   // x suction-vortex orbit, y dust whirl, z wall cloud, w RFD lip
#define S_spin3  _SP[31]   // x anti-mesocyclone (negative: clockwise), y satellite tornado, z anticyclonic tornado (negative),
                           // w how far the inflow has carried the beaver tail's air toward the updraft, km (0: the web build's)

Texture3D<float> _Noise;
SamplerState sampler_LinearRepeat;
SamplerState sampler_LinearClamp;

// WGSL's smoothstep, which the web build also uses with reversed edges (HLSL leaves that undefined)
float ss(float a, float b, float x) { float t = saturate((x - a) / (b - a)); return t * t * (3.0 - 2.0 * t); }

// The light grid stores the sun's optical depth in r, not its transmittance, so the physical lighting can light each
// multiple-scattering octave from it (StormLab 2.0). sunT is the web build's two-lobe transmittance of that depth.
float sunT(float tau) { return 0.72 * exp(-0.38 * tau) + 0.28 * exp(-0.07 * tau); }
// The unified lighting's share of the key light reaching a point under optical depth tau, as a share of open air's:
// StormMarch.compute's stormLight sun for an isotropic medium (its octaves and the light diffused through the cloud,
// 1 / (1 + a tau)). Used where the storm lights or shades something else (the ground it shadows, the bounce off it) so
// that responds as the cloud does. m: x octaves, y each octave's extinction factor, z diffusion (a), w diffused sun.
float sunReach(float tau, float4 m)
{
    float s = 0.0, s0 = 0.0, b = 1.0, k = 0.38;
    [unroll] for (int i = 0; i < 4; i++) { if (i >= (int)m.x) break; s += b * exp(-k * tau); s0 += b; b *= 0.5; k *= m.y; }
    return (s + m.w * (1.0 - exp(-0.1 * tau)) / (1.0 + m.z * max(tau, 0.0))) / max(s0, 1e-4);
}

// ---------------------------------------------------------------------------------------------------- NOISE
float n3(float3 p) { return _Noise.SampleLevel(sampler_LinearRepeat, p, 0); }
// From StormLab 2.0, for the detailed tornado: filtered noise. g_fp is the pixel footprint (km) at the current sample (set
// by the march; 0 elsewhere, which is unfiltered). n3k(q, k, o) samples q * k + o from the mip level whose texels are about
// the footprint, so octaves finer than a pixel average out instead of sparkling
static float g_fp = 0.0;
float nlod(float k) { return max(0.0, log2(max(g_fp * 72.0 * k, 1e-6))); }   // 48 texels per tile, x 1.5
float n3k(float3 q, float k, float3 o) { float l = nlod(k); return l > 3.0 ? 0.5 : _Noise.SampleLevel(sampler_LinearRepeat, q * k + o, l); }
float2 safeNorm(float2 v, float2 fallback) { float l = length(v); return l > 1e-4 ? v / l : fallback; }
// three octaves, about 11, 4.3 and 1.7 km
float fbm(float3 p) { return 0.5 * n3(p * 0.09) + 0.3 * n3(p * 0.23 + float3(0.31, 0.17, 0.53)) + 0.2 * n3(p * 0.6 + float3(0.7, 0.2, 0.4)); }
// rotate a horizontal vector counter-clockwise (x east, y north) by angle a
float2 rot2(float2 v, float a) { float c = cos(a); float s = sin(a); return float2(c * v.x - s * v.y, s * v.x + c * v.y); }
// cauliflower: rounded lobes (inverted ridged noise) at about 2.5 and 1 km
float billow(float3 p) { float a = 1.0 - abs(2.0 * n3(p * 0.55) - 1.0); float b = 1.0 - abs(2.0 * n3(p * 1.4 + float3(0.2, 0.6, 0.3)) - 1.0); return 0.7 * a + 0.3 * b; }

// ---------------------------------------------------------------------------------------------------- NEAR
// The low-level mesocyclone: once the rear-flank downdraft surges, the rotating updraft's lower few km broaden over the
// horseshoe, so its rim rises from the horseshoe's lip into the updraft. Centre and extra radius at height zr above base
float2 mesoCentre(float zr)
{
    float lo = exp(-max(zr, 0.0) / 2.5);
    return S_up.xy + S_tilt.xy * max(zr, 0.0) + (S_rfdb.xy - S_up.xy) * 0.55 * S_rfdb.w * lo;
}
float mesoExtra(float zr) { return S_rfdb.z * 0.3 * S_rfdb.w * exp(-max(zr, 0.0) / 2.5); }
// A restrained lower flare with a tucked underside, shared by geometry and surface detail.
float baseFlare(float zr)
{
    float mature = saturate(S_anvil.w); float h = max(zr, 0.0);
    return (0.22 * S_tower.w + 0.06 * mature) * exp(-h / 1.8)
        - 0.06 * mature * (1.0 - ss(0.0, 0.6, h));
}
// A fuller middle barrel around the whole circulation, with a subtle rear spiral shoulder.
// Share this envelope with the render detail so the wider sides keep their cloud texture.
float mesoShoulder(float2 d, float zr)
{
    float H = max(S_tower.x - S_base.x, 0.5); float middle = H * 0.45;
    float wrap = 0.65 * ss(0.0, 0.45, S_rfd.x) * ss(0.0, middle, max(zr, 0.0));
    float2 rear = cos(2.18 + wrap) * S_f.xy + sin(2.18 + wrap) * S_f.zw;
    float side = ss(-0.2, 0.7, dot(d, rear) / max(length(d), 0.01));
    return 0.5 * S_tower.y * S_up.z * (0.85 + 0.15 * side)
        * exp(-pow((zr - middle) / max(H * 0.24, 0.5), 2.0));
}
// A broad curved underside: level near the core, rolling upward toward the outer wall.
float underbellyRise(float2 d)
{
    float baseRadius = S_tower.y * (1.0 + baseFlare(0.0)) + mesoExtra(0.0) + mesoShoulder(d, 0.0);
    float rim = saturate((length(d) / max(baseRadius, 0.5) - 0.55) / 0.55);
    return 0.75 * rim * rim;
}
// angle round the updraft measured from the inflow vault (the side facing the inflow tail), -pi..pi
float vaultAngle(float2 d) { float2 v = S_tail.xy; return atan2(v.x * d.y - v.y * d.x, dot(v, d)); }
// A modest lift of the main base exposes the lowered rim without detaching its upper connection.
float baseLift() { return 0.62 * ss(0.0, 0.02, S_tilt.w); }
float horseshoeArc(float th)
{
    return ss(lerp(0.92, -0.55, S_rfdb.w), lerp(0.99, -0.33, S_rfdb.w), cos(th + 0.96));
}
// One rear-opening cut for the whole cloud, including the apron, low cloud and rain.
// The channel reaches beyond the updraft rather than ending at a circular clipping bound.
float clearSlot(float3 p)
{
    float halfway = (S_tower.x - S_base.x) * 0.45;
    if (S_rfd.x <= 0.0 || S_rfdb.z <= 0.0 || p.z > S_base.x + halfway) return 0.0;
    float zr = p.z - S_base.x;
    float growth = ss(0.0, 0.45, S_rfd.x);
    float depth = lerp(0.5, halfway, growth);
    float middle = ss(depth * 0.46, depth, zr);
    // A gentle counter-clockwise sweep with height joins the lower horseshoe to the
    // middle-level barrel as a single corkscrew. The opening at cloud base stays rear-facing.
    float wrap = 0.65 * growth * ss(0.0, halfway, max(zr, 0.0));
    float2 rear = cos(2.18 + wrap) * S_f.xy + sin(2.18 + wrap) * S_f.zw;
    // Stay open through the lower levels, then retreat outward as the barrel pinches shut.
    float2 db = p.xy - S_rfdb.xy - S_tilt.xy * max(zr, 0.0) - rear * (S_tower.y * 0.45 * middle);
    float u = dot(db, rear); float v = abs(dot(db, float2(-rear.y, rear.x)));
    float Rs = S_rfdb.z;
    float n = (n3(p * float3(0.35, 0.35, 0.25) + float3(0.7, 0.7, 0.1)) - 0.5) * 0.08 * (1.0 - middle);
    // A long, eased taper avoids the ellipse's pinched top and abrupt roof.
    float arch = 1.0 - middle;
    float feather = lerp(0.25, 0.85, middle);
    float pocket = ss(Rs * 0.82 * arch + feather, max(Rs * 0.6 * arch - feather * 0.25, 0.03), length(db) + n);
    // A restrained rear opening preserves the barrel's mass while still admitting daylight.
    float width = (Rs * (0.3 + 0.28 * growth) + max(u, 0.0) * (0.08 + 0.25 * growth)) * arch;
    float channel = ss(-0.35, 0.45, u) * ss(width + feather + 0.15, max(width - feather, 0.03), v + n);
    float roof = 1.0 - ss(depth * 0.6, depth, zr);
    // Keep only the narrow rising attachment above the wall cloud, not the whole barrel.
    float attachment = ss(S_wall.z * 1.2, S_wall.z * 0.95, length(p.xy - S_wall.xy))
        * S_wall.w * (1.0 - ss(2.0, 4.0, zr));
    float opening = pocket + channel - pocket * channel; // smooth union, no ridge where the two cuts meet
    return growth * opening * roof * (1.0 - attachment);
}
// Near-ground features, drawn at full detail in the render pass (fine = 1) and coarsely into the volume for
// shadows (fine = 0): the wall cloud, its tail cloud, and the inflow tail. Returns density 0..1.
// x 1: the game's wall-cloud underside (StormRenderer.wallBase Smooth): its ragged fringe drawn from noise in the cloud's
// own turning frame rather than round its centre (whose streaks all pointed at the centre, with a seam where the angle
// wraps), a softer underside, and scud: soft fragments hanging under the base that fade into it. 0: the web build's.
// y 1: the game's tail cloud (a narrow band with a rounded underside, joined smoothly to the wall cloud); 0: the web build's.
// w 1: the game's beaver tail and inflow ramp (tailDir, tailRoot, tailSize); 0: the web build's.
float4 _WallP;

// ---- the inflow tail's line: the beaver tail, the volume's inflow ramp and the tail cloud's aim all follow it.
// The web build's runs 35 degrees right of the storm's motion from 2.5 km ahead of the updraft, out into the open inflow,
// diverging from the forward flank's rain until its tip is 8 km from it. A beaver tail is "a smooth, flat, low band along
// the inflow/FFD boundary" (Stull, Practical Meteorology ch. 14; meteorology/Practical_Meteorology_Stull/NOTES.md), so the
// game's (_WallP.w; user, 2026-10-28) runs along the forward flank's inflow-side edge: 10 degrees left of the motion, as
// that edge runs (the forward-flank rain's core and its sweep, StormVolume.compute), from half a kilometre right of the
// motion line through the updraft, so it lies just outside the rain and meets it near the updraft.
// (vaultAngle, the striations' inflow vault, keeps the web build's direction.)
float2 tailDir() { return _WallP.w > 0.5 ? normalize(S_f.xy + 0.176 * S_f.zw) : S_tail.xy; }
float2 tailRoot() { return S_up.xy + S_f.xy * 2.5 + S_f.zw * (_WallP.w > 0.5 ? -0.5 : 0.5); }
// HP storms (their rain wraps round the updraft, S_rfd.w 0.75) keep the web build's bigger beaver tail and a longer ramp
// (user, 2026-10-28: "a bigger inflow tail on the HP storms"; Stull ch. 14: beaver tails are most common in HP storms):
// 0 classic and LP .. 1 HP
float tailHP() { return saturate(S_rfd.w / 0.75); }
// The beaver tail's size. The web build's is 14 km long (S_tail.z), up to 5.6 km wide and 2.5 km deep where it meets the
// storm; the game's (_WallP.w, with the game's tail cloud; user, 2026-10-28: "too big and too long") is three quarters as
// long and a little narrower and shallower. (What read as too big was mostly the volume's inflow ramp along the same line,
// 4 km tall and 10 km long, which the game now keeps only where it rises into the updraft: StormVolume.compute. Half this
// band's size as well left only a sliver at the base.) x length km, y width scale, z depth scale
float3 tailSize() { return _WallP.w > 0.5 ? lerp(float3(0.75 * S_tail.z, 0.8, 0.85), float3(S_tail.z, 1.0, 1.0), tailHP()) : float3(S_tail.z, 1.0, 1.0); }
// How dense the beaver tail and the inflow ramp are. The web build gives an HP storm's 0.85 of a classic storm's; the game's
// HP storms have the full amount, at least a classic storm's (user, 2026-10-28; Stull ch. 14: most common in HP storms)
float tailAmt() { return _WallP.w > 0.5 ? S_tail.w * lerp(1.0, 1.0 / 0.85, tailHP()) : S_tail.w; }
// Where the tail cloud points. The web build's at the inner part of its beaver tail; the game's (_WallP.w; user,
// 2026-10-28) at the forward-flank rain nearest the wall cloud, a quarter of the way from the hail core to the forward
// flank's core: a tail cloud "points towards the forward flank precipitation area" (Stull ch. 14). Along the beaver tail
// it pointed 29 degrees right of that rain.
float2 tailCloudAim()
{
    if (_WallP.w > 0.5) return lerp(S_hail.xy, S_ffd.xy, 0.25);
    float2 root = tailRoot(); float2 tdi = tailDir(); float tli = tailSize().x;
    float bend = 0.035 * (tli - 2.0) * (tli - 2.0) * 0.12 - 0.6 * sin(0.7);
    return root + tdi * 2.0 + float2(-tdi.y, tdi.x) * bend;
}

// where the game's tail cloud (_WallP.y) can be: a corridor from the wall cloud along the tail, aimed as nearField aims it.
// The render pass draws near-ground cloud at full detail only near the wall cloud or where the coarse volume has some;
// the tail runs well past the wall cloud, and its fine edge was cut off where that region ended (user, 2026-10-27)
bool inTailCloud(float3 q)
{
    if (_WallP.y < 0.5 || S_wall.w <= 0.0 || S_wall2.z <= 0.0) return false;
    if (q.z < S_base.x - S_wall2.x - 0.4 || q.z > S_base.x + 0.3) return false;
    float2 wc = S_wall.xy; float Rw = S_wall.z;
    float2 td = normalize(tailCloudAim() - wc + float2(0.0001, 0.0));
    float2 dw = q.xy - wc; float s = dot(dw, td);
    // (with the game's scud round and past its tip: beaverTail's flow)
    return s > 0.0 && s < Rw + (_WallP.w > 0.5 ? 6.5 : 5.0) && abs(dot(dw, float2(-td.y, td.x))) < (_WallP.w > 0.5 ? 1.4 : 1.2);
}

// nearField's inflow tail, on its own so the march can light it as the cloud it is (a game adaptation; the code is the
// web build's, moved out of nearField unchanged)
float beaverTail(float3 p, float fine)
{
    float zb = S_base.x;
    float v = 0.0;
    if (S_tail.w > 0.0)
    {
        // inflow tail (beaver tail): a smooth, laminar band streaming in along the forward flank's edge
        float2 t0 = tailRoot();
        float2 td = tailDir(); float s = dot(p.xy - t0, td);
        float3 sz = tailSize(); float tl = sz.x;
        // its texture streams in along it toward the updraft, as the inflow carries it (S_spin3.w; the shape stays)
        float su = s + S_spin3.w;
        // the tail curves: it bends toward the forward flank as it nears the storm
        float qq = dot(p.xy - t0, float2(-td.y, td.x)) - 0.035 * (tl - s) * (tl - s) * 0.12 + 0.6 * sin(s * 0.35);
        bool game = _WallP.w > 0.5;
        if (s > (game ? -3.5 : -2.0) && s < tl + 2.0 && abs(qq) < 5.0 && p.z > zb - 1.0 && p.z < zb + 3.0)
        {
            float fr = saturate(s / tl);
            float n = 0.55 * n3(float3(su * 0.05, qq * 0.45, p.z * 0.6)) + 0.45 * lerp(0.5, n3(float3(su * 0.25, qq * 1.6, p.z * 2.5) + float3(0.6, 0.2, 0.4)), fine);
            // a deep band near the storm (a kilometre or more), rising as it is drawn up into the updraft's vault,
            // thinning and lowering to a ragged ribbon far out
            float near = (1.0 - fr) * (1.0 - fr);
            float wt = lerp(2.8, 0.45, pow(fr, 0.7)) * sz.y; float zc = zb + 0.2 + 1.1 * near; float th = lerp(0.22, 1.25, near) * sz.z;
            // The game's (user, 2026-10-28: "blend into the main updraft more smoothly"; its smaller band butted into the
            // updraft's side and ended in a blunt cap): over its last 3 km it widens, deepens and rises, and runs on into
            // the updraft, so it meets the updraft's side gradually rather than at an angle; over its outer half it tapers
            // and its tip breaks up in its streaks
            float rootK = game ? 1.0 - ss(0.0, 3.0, s) : 0.0;
            float taper = game ? 1.0 - ss(0.45 * tl, tl, s + (n - 0.5) * 2.0) : 1.0;
            wt *= (1.0 + 1.2 * rootK) * lerp(0.3, 1.0, taper); th *= (1.0 + 1.0 * rootK) * lerp(0.35, 1.0, taper); zc += 0.3 * rootK;
            // laminar streaks drawn out along the band, about 1 km by 250 m and half that, streaming in with it (a game
            // adaptation with the flow, user 2026-10-28: its own noise varies over 4-20 km, so streaming it alone moved
            // nothing that could be seen). Only in the render pass (fine), filtered by the pixel's footprint (n3k)
            float fs = 0.0;
            if (fine > 0.0 && S_spin3.w != 0.0)
                fs = (0.65 * (n3k(float3(su * 0.225, qq, p.z * 1.5), 4.0, float3(0.21, 0.63, 0.37)) - 0.5)
                    + 0.35 * (n3k(float3(su * 0.25, qq, p.z * 1.3), 9.0, float3(0.55, 0.11, 0.83)) - 0.5)) * fine
                    // (smooth where it rises into the updraft: seen down the band, the streaks over its widened root read
                    // as feathers brushed up the updraft's side)
                    * (1.0 - 0.8 * rootK);
            float ends = game ? ss(-3.5, 0.5, s) * ss(0.0, 0.3, taper + fs * 0.4)
                              : ss(-2.0, 0.75, s) * (1.0 - ss(tl * 0.8, tl, s + (n - 0.5) * 4.0 * sz.y));
            float inT = ends
                * ss(wt, wt * 0.45, abs(qq) + (n - 0.5) * 0.9 + fs * 0.5) * ss(th, th * 0.4, abs(p.z - zc) + (n - 0.5) * 0.12 + fs * 0.12)
                * (1.0 + 0.5 * fs);
            v = max(v, tailAmt() * inT * 0.85);
        }
    }
    return v;
}
// Where the beaver tail's band can be, without its noise (a game adaptation): its texture streams along it (S_spin3.w), so
// the render pass draws it wherever the band can be, not only where the volume's coarse copy says it is (that copy is
// repainted every few seconds, and not at all while the game is paused, so the streaming outline outran it)
bool beaverTailBand(float3 p)
{
    if (S_tail.w <= 0.0 || S_spin3.w == 0.0) return false;
    float zb = S_base.x;
    float2 t0 = tailRoot();
    float2 td = tailDir(); float s = dot(p.xy - t0, td);
    float3 sz = tailSize(); float tl = sz.x;
    if (s < -3.5 || s > tl + 2.0) return false;
    float qq = dot(p.xy - t0, float2(-td.y, td.x)) - 0.035 * (tl - s) * (tl - s) * 0.12 + 0.6 * sin(s * 0.35);
    float fr = saturate(s / tl); float near = (1.0 - fr) * (1.0 - fr);
    float wt = lerp(2.8, 0.45, pow(fr, 0.7)) * sz.y; float zc = zb + 0.2 + 1.1 * near; float th = lerp(0.22, 1.25, near) * sz.z;
    // (with the game's widening root, beaverTail)
    return abs(qq) < wt * 2.2 + 0.5 && abs(p.z - zc - 0.15) < th * 2.0 + 0.25;
}

float nearField(float3 p, float fine)
{
    float zb = S_base.x; float clock = S_base.z;
    float v = 0.0;
    if (S_wall.w > 0.0)
    {
        float2 wc = S_wall.xy; float Rw = S_wall.z; float lw = S_wall2.x;
        float2 dw = p.xy - wc; float rw = length(dw);
        if (rw < Rw + 5.5 && p.z < zb + 1.8 && p.z > max(0.06, zb - lw - 0.85))
        {
            // the wall cloud turns counter-clockwise: sample its texture in a frame turned back by the angle turned so far
            float2 dr = rot2(dw, -S_spin2.z);
            float n = 0.5 * n3(float3(dr * 0.5, p.z * 1.0)) + 0.5 * lerp(0.5, n3(float3(dr * 1.9, p.z * 2.2) + float3(0.3, 0.7, 0.2)), fine);
            // ragged fringe hanging from the bottom: fine noise around the circumference, stretched vertically
            float ang = atan2(dr.y, dr.x);
            float fringe = lerp(0.5, n3(float3(ang * 1.3, rw * 0.8, p.z * 0.6) + float3(0.1, 0.9, 0.5)), fine);
            bool smoothBase = _WallP.x > 0.5;
            if (smoothBase)
                fringe = lerp(0.5, 0.6 * n3(float3(dr * 1.1, p.z * 0.8) + float3(0.1, 0.9, 0.5)) + 0.4 * n3(float3(dr * 2.3, p.z * 1.6) + float3(0.6, 0.2, 0.8)), fine);
            // a broad bowl: deepest in the middle, its underside curving up to the base at the rim
            // Broad, subtle lobes rotate with the texture so motion remains readable even
            // when the RFD's stationary outline surrounds the cloud. They shrink with it.
            float lobe = 0.07 * cos(3.0 * ang + 0.8) + 0.035 * sin(5.0 * ang - 0.4);
            float contourR = Rw * (1.0 + lobe);
            float bowl = 1.0 - pow(saturate(rw / contourR), 2.4);
            float bot = zb - lw * (0.15 + 0.85 * bowl + 0.1 * cos(2.0 * ang + 0.5)) + (n - 0.5) * 0.12 - (fringe - 0.5) * (smoothBase ? 0.1 : 0.18);
            float join = ss(zb - 0.1, zb + 1.0, p.z);
            float under = smoothBase ? ss(bot - 0.1, bot + 0.12, p.z) : ss(bot - 0.03, bot + 0.05, p.z);
            float w = ss(contourR + 0.15 + join * 0.8, contourR - 0.2 + join * 0.8, rw + (n - 0.5) * 0.65) * under * ss(zb + 1.8, zb + 0.9, p.z);
            if (smoothBase)
            {
                // scud: thin, soft fragments hugging the base, turning with it, gathered under its middle and none near its
                // rim; they hang from the base itself (tens to under 300 m below it) and thin out downward. A bigger, lower
                // wall cloud has more of it, hanging a little deeper (its radius runs about 1.2-3.6 km, its lowering 0.14-0.67
                // km). Each fragment hangs at its own depth, so they do not line up in one layer (seen side-on, one layer read
                // as a line of cloud under the base).
                float size = saturate((Rw - 1.2) / 2.4);
                float depth = 0.06 + 0.18 * lw;
                float sn = 0.65 * n3(float3(dr * 1.3, p.z * 1.8) + float3(0.45, 0.15, 0.7)) + 0.35 * lerp(0.5, n3(float3(dr * 3.1, p.z * 3.5) + float3(0.2, 0.8, 0.35)), fine);
                float sb = bot - 0.5 * depth * n3(float3(dr * 0.75, 0.37));   // this fragment's own hanging depth
                float hang = ss(sb - depth, sb - 0.3 * depth, p.z) * ss(bot + 0.06, bot - 0.02, p.z) * ss(0.05, 0.25, p.z);
                float inner = ss(contourR * 0.9, contourR * 0.45, rw);
                float cover = lerp(0.66, 0.57, size);
                w = max(w, lerp(0.18, 0.42, size) * ss(cover, cover + 0.16, sn) * hang * inner);
            }
            // the rear-flank downdraft eats the wall cloud's southern and western side away: what's left curls round
            // the clear slot, with the tornado at its tip
            if (S_rfdb.w > 0.0)
            {
                float hb = ss(S_rfdb.z * 0.85, S_rfdb.z * 0.55, length(p.xy - S_rfdb.xy) + (n - 0.5) * 0.7) * ss(Rw * 0.9, Rw * 1.2, rw);
                w *= 1.0 - 0.4 * S_rfdb.w * hb;
            }
            v = max(v, S_wall.w * w);
            // Aim the low wall-cloud tail (the web build's: toward the inner part of the main inflow/beaver tail; the game's:
            // at the forward-flank rain, tailCloudAim)
            float2 td = normalize(tailCloudAim() - wc + float2(0.0001, 0.0)); float s = dot(dw, td); float qq = dot(dw, float2(-td.y, td.x));
            float nt = n3(float3(s * 0.3, qq * 1.0, p.z * 1.5) + float3(0.2, 0.6, 0.1));
            if (_WallP.z > 0.5) { }   // (checks: no tail cloud)
            else if (_WallP.y > 0.5)
            {
                // the game's tail (user, 2026-10-27): the web build's was a flat slab over 2 km wide whose underside hung
                // below the wall cloud's rim, so it met the wall cloud in a step. Here it is a narrow band about a
                // kilometre across, tapering away; its underside is rounded (deepest along its middle, curving up to the
                // base at its sides) and grows out of the wall cloud's own underside at about its level (a tail cloud hangs
                // about as low as the wall cloud it feeds), easing up toward the base with distance
                // (its texture streams in toward the wall cloud, as the scud below does)
                float suT = s + 1.6 * S_spin3.w;
                if (S_spin3.w != 0.0) nt = n3(float3(suT * 0.3, qq * 1.0, p.z * 1.5) + float3(0.2, 0.6, 0.1));
                float far = saturate((s - Rw) / 4.5);
                float hw = lerp(0.6, 0.25, far) * (0.85 + 0.3 * nt);
                float x = abs(qq) / hw;
                float wallD = lw * (0.15 + 0.85 * (1.0 - pow(saturate(s / Rw), 2.4)));
                float tailD = (0.8 * lw + 0.05) * (1.0 - 0.6 * far);
                float D = lerp(wallD, tailD, ss(0.3 * Rw, 1.1 * Rw, s));
                float tb = zb - D * (1.0 - min(x * x * x, 1.0)) + (nt - 0.5) * 0.1;
                float tg = ss(Rw * 0.3, Rw * 0.7, s) * (1.0 - ss(Rw + 2.5, Rw + 4.5, s + (nt - 0.5) * 1.2))
                    * ss(1.0, 0.6, x + (nt - 0.5) * 0.3) * ss(tb - 0.06, tb + 0.1, p.z) * ss(zb + 0.22, zb - 0.04, p.z);
                // joined to the wall cloud softly (a union, not the larger of the two), so no seam where they meet
                float tv = S_wall2.z * tg * 0.9;
                v = v + tv - v * tv;
                // Scud (user, 2026-10-28; Stull ch. 14: ragged fragments "often form near the tip of the tail cloud and are
                // drawn quickly into the tail and the wall cloud by strong winds"): torn fragments beside and below its
                // outer half and out past its tip, from noise streaming in toward the wall cloud at 1.6 x the inflow's
                // 15 m/s (S_spin3.w), so they race in along it and are taken up into it. Render pass only (fine).
                if (fine > 0.0 && S_spin3.w != 0.0)
                {
                    float sc = 0.6 * n3k(float3(suT * 0.7, qq * 1.4, p.z * 2.5) / 2.5, 2.5, float3(0.33, 0.71, 0.19))
                             + 0.4 * n3k(float3(suT * 1.8, qq * 3.2, p.z * 5.0) / 5.0, 5.0, float3(0.81, 0.27, 0.55));
                    float zone = ss(Rw + 0.5, Rw + 2.0, s) * (1.0 - ss(Rw + 4.8, Rw + 6.3, s)) * ss(1.0, 0.45, abs(qq));
                    float hang = ss(zb - D - 0.35, zb - D - 0.2, p.z) * ss(zb + 0.02, zb - 0.1, p.z);
                    float scud = S_wall2.z * zone * hang * ss(0.58, 0.7, sc) * 0.85 * fine;
                    v = v + scud - v * scud;
                }
            }
            else
            {
            float wt = lerp(1.15, 0.45, saturate((s - Rw) / 4.5));
            float zt = zb - lw * 0.6 - 0.24 + 0.15 * saturate((s - Rw) / 4.5);
            float tl = ss(Rw * 0.4, Rw * 0.8, s) * (1.0 - ss(Rw + 3.0, Rw + 4.8, s + (nt - 0.5) * 1.5))
                * ss(wt, wt * 0.4, abs(qq) + (nt - 0.5) * 0.6) * ss(zt - 0.08, zt + 0.08, p.z) * ss(zb + 0.22, zb - 0.04, p.z);
            v = max(v, S_wall2.z * tl * 0.9);
            }
        }
    }
    if (S_rfdb.w > 0.0)
    {
        // the horseshoe: the edge of the base curling round the clear slot, closed on the east and open to the rear, its
        // northern arm running into the wall cloud and its southern arm ending in the anticyclonic curl. Thick and deep:
        // a steep, ragged face toward the slot, rising more gently outward into the base
        float2 db = p.xy - S_rfdb.xy; float rb = length(db); float Rs = S_rfdb.z;
        if (abs(rb - Rs) < 1.4 && p.z < zb + 1.4 && p.z > zb - 0.8)
        {
            float th = atan2(dot(db, S_f.zw), dot(db, S_f.xy));     // the arc runs from -170 deg (south arm) through the east to +60 (north arm)
            float am = horseshoeArc(th);
            float2 dl = rot2(db, -S_spin2.w);
            float nl = 0.6 * n3(float3(dl * 0.6, p.z * 1.2) + float3(0.2, 0.4, 0.9)) + 0.4 * lerp(0.5, n3(float3(dl * 2.2, p.z * 2.6) + float3(0.8, 0.1, 0.3)), fine);
            float north = ss(0.3, 1.0, cos(th - 0.9)); float south = ss(0.5, 1.0, cos(th + 2.97));
            float D = S_rfdb.w * (0.16 + 0.08 * south + 0.15 * north + 0.25 * S_wall2.x * north) * (0.85 + 0.3 * nl);
            // a wedge of rising cloud: its underside slants up on both sides, gently toward the slot and more gently
            // outward into the base, not a vertical cliff
            float x = rb - Rs + (nl - 0.5) * 0.3;
            float join = ss(zb - 0.1, zb + 0.9, p.z);
            float prof = saturate(1.0 - max(-x, 0.0) / (0.5 + 0.2 * join) - max(x, 0.0) / (0.4 + 0.6 * join));
            float bot = zb - D * prof * prof * (3.0 - 2.0 * prof) - (nl - 0.5) * 0.2;
            // soft, ragged underside: density fades over a few hundred metres, with wisps hanging below
            float wisp = lerp(0.5, n3(float3(dl * 3.0, p.z * 6.0) + float3(0.4, 0.9, 0.2)), fine);
            float lip = am * ss(0.0, 0.25, prof) * ss(bot - 0.08 - wisp * 0.07, bot + 0.15, p.z) * ss(zb + 1.4, zb + 0.8, p.z);
            // the lip is the rolled underside of the mesocyclone's rim: none of it sticks out beyond the cloud above
            float mR0 = S_tower.y * (1.0 + baseFlare(0.0)) + mesoExtra(0.0);
            float cover = ss(mR0 + 0.2, mR0 - 1.2, length(p.xy - mesoCentre(0.0)));
            v = max(v, S_rfdb.w * lip * cover);
        }
    }
    if (S_anti.z > 0.0)
    {
        // anti-mesocyclone: a small lowering at the southern curl, turning clockwise
        float2 da = p.xy - S_anti.xy; float ra = length(da);
        float Ra = 0.45 + 0.4 * S_anti.z;
        if (ra < Ra + 1.0 && p.z < zb + 0.3 && p.z > zb - 0.7)
        {
            float2 dra = rot2(da, -S_spin3.x);
            float na = 0.55 * n3(float3(dra * 0.9, p.z * 1.5) + float3(0.6, 0.2, 0.7)) + 0.45 * lerp(0.5, n3(float3(dra * 2.8, p.z * 3.0) + float3(0.1, 0.5, 0.2)), fine);
            float la = (0.1 + 0.25 * S_anti.z) * (1.0 - 0.5 * pow(min(ra / Ra, 1.2), 4.0));
            float bot = zb - la - (na - 0.5) * 0.12;
            float lw2 = ss(Ra + 0.1, Ra - 0.25, ra + (na - 0.5) * 0.6) * ss(bot - 0.03, bot + 0.05, p.z) * ss(zb + 0.2, zb, p.z);
            v = max(v, S_anti.z * lw2 * 0.9);
        }
    }
    v = max(v, beaverTail(p, fine));
    return v * (1.0 - clearSlot(p));
}

#endif
