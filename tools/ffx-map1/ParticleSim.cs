// PPP particle runtime — port of noclip_reference/particle.ts simulation
// core: typed instructions (datum parsers + vec remap), emitter/particle
// lifecycle, and draw emission (geo meshes + flipbook quads) as
// camera-facing world-space geometry consumable by MapRenderer.
//
// Scope: covers the ~20 ops that account for ~95% of map particle
// instructions (census evidence/ppp-structural-parser.json). Trails
// (FlipbookTrail/Pyrefly), PointLight, glares, blurs and water are stubbed.
using System;
using System.Collections.Generic;

public static class ParticleSim
{
    public const int VecCount = 64;
    public const float ToRad = (float)(Math.Tau / 0x10000);
    const float EmitterMaxTime = 0x70000;
    const float EmitterDoneTimer = -0x1000;

    // ---------- decoded data ----------

    public sealed class Vec4Data { public float T; public float[] V = new float[4]; }
    public sealed class RandData { public float T; public int Target; public float[] V = new float[4]; public bool Peaked; }
    public sealed class EmitData
    {
        public float T; public int Pattern, Count, Period, Program, ChildPos, ChildDir, ChildAngle, ChildDelta;
        public int Mask; public bool Random, Transform; public float Scale;
    }
    public sealed class GeoData
    {
        public float T; public int GeoIndex, Flags, Blend;
        public bool Fog, Flag2000, Fade, DepthOffset;
        public float[] UInc = new float[4], VInc = new float[4];
        public int BlendMode, BlendAlpha;
        // WibbleUVScrollGeo extras (vertex wobble is shader-side in noclip
        // and almost always zero — parsed but not applied to vertices)
        public float[] WibbleVel = new float[4];
        public int WaterTexSlot, WaterTexDur;
        // Water (0x58): wave phases accumulate CPU-side; displacement itself
        // is shader-side. Radius scales a distance fade we don't apply.
        public float[] XSpeed = new float[4], DiagSpeed = new float[4];
        public float Radius;
    }
    public sealed class FlipData { public float T; public int Index, Speed; public bool FlipX, FlipY; }
    public sealed class ClusterData
    {
        public float T; public int Index, ChildCount, Speed;
        public float[] OffsetRange = new float[4], Vel = new float[4], VelRange = new float[4],
            Accel = new float[4], Scale = new float[4], ScaleRange = new float[4],
            ScaleVel = new float[4], ScaleAccel = new float[4];
        public float RollRange; public int ResetCount, ResetInterval, MirrorFlags;
        public bool OffsedPeaked;
    }
    public sealed class EnabledData { public float T; public bool Enabled; }

    /// <summary>Typed instruction: opcode + datum list + vec slots remapped
    /// to sequential indices (VecMapState semantics — same raw offset shares
    /// the slot; negative offsets reserve fresh slots).</summary>
    public sealed class Instr
    {
        public uint Op;
        public int[] Slots = Array.Empty<int>();
        public List<Vec4Data>? Vecs;
        public List<RandData>? Rands;
        public List<EmitData>? Emits;
        public List<GeoData>? Geos;
        public List<FlipData>? Flips;
        public List<ClusterData>? Clusters;
        public List<EnabledData>? Enables;
        public bool LoopSubtract;   // LoopStep
        public bool AtOrigin;
        /// <summary>0x71 renders the geo at the camera position.</summary>
        public bool AtCamera;       // PeriodicEmit.atOrigin
        public int Range;           // RandomStep/RandomCube: 0 sym,1 pos,2 neg
        public int EulerOrder;      // PosRotScale
        public int ClusterMode;     // FlipbookCluster
        public bool IsReverseVel;
        public bool RenderAll;      // SimpleFlipbook iterates the chain itself
        public int DepthSpace = -1; // DepthOffset/BillboardDepthOffset datum[0]
        public float DepthZ;
        public List<GlareData>? Glares;
        public List<GeoBlurData>? GeoBlurs;
        public List<AttractData>? Attracts;
        public List<TrailData>? Trails;
        public List<ElecTargetData>? ElecTargets;
        public List<ElecColorData>? ElecColors;
        public List<ElecData>? Electrics;
        public List<RainData>? Rains;
        public List<PyreflyData>? Pyreflys;
        public List<ChildSetupData>? ChildSetups;
        /// <summary>PointChain ops: scratch declaration consumed by
        /// Electricity; copied onto the Electricity instrs of the same
        /// program at load time.</summary>
        public int ChainCount, ChainVerts;
        /// <summary>FakeGeo (0x1002): the magic script assigns this at
        /// runtime; -1 renders nothing (matches noclip's unset state).</summary>
        public int FakeGeoIndex = -1;
        public int Id;
    }

    public sealed class Prog
    {
        public int Flags, InstrCount;
        public float Start, Lifetime, LoopEnd, LoopLength;
        public List<Instr> Instructions = new();
        /// <summary>raw vec offset -> slot, for childPos lookups (emit).</summary>
        public Dictionary<long, int> VecMap = new();
    }

    public sealed class Bhv { public float Lifetime; public bool IgnoreLifetime; public List<Prog> Programs = new(); }

    public sealed class GeoPrim { public int[] Indices = Array.Empty<int>(); public float[][] Colors = Array.Empty<float[]>(); public float[][] Uvs = Array.Empty<float[]>(); public Gs.Tex0? Tex0; }
    public sealed class Geo { public uint Flags; public int BlendSettings = -1; public float[][] Points = Array.Empty<float[]>(); public List<GeoPrim> Prims = new(); public float[][] Verts = Array.Empty<float[]>(); public float[][] Normals = Array.Empty<float[]>(); }
    public sealed class Pat { public int GeoIndex; public int[] Indices = Array.Empty<int>(); }

    /// <summary>GlareBase/SimpleGlare/MoreGlare/ScaledGlare datum.</summary>
    public sealed class GlareData
    {
        public float T;
        public int Flipbook = -1;
        public float[] Color = { 1, 1, 1, 1 };
        public float Scale = 1, ScaleDist, MaxScale, MaxRadius;
        public float Factor, MaxDist; public int DistReduction, AlphaDist;
        public int XScaleMode, YScaleMode; public bool ViewDir;
    }
    /// <summary>FlipbookTrail datum (flipbookTrailP/ParamsP): ribbon of
    /// billboarded flipbook quads along the particle's recent positions.</summary>
    public sealed class TrailData
    {
        public float T; public int Flipbook = -1; public int Speed;
        public float MaxScale, MinScale;
        public float[] HeadColor = { 1, 1, 1, 1 }, TailColor = { 1, 1, 1, 1 };
        public float StartGap; public int TrailLength; public bool RenderHead;
    }
    /// <summary>Attract {target particle idx, params vec offset} /
    /// AttractingTarget {minDist, repelDist}.</summary>
    public sealed class AttractData
    {
        public float T; public int Target = -1, Offset;
        public float MinDist, RepelDist;
    }
    /// <summary>GeoBlur datum {geo, inc vec3, flags, mulZ}.</summary>
    public sealed class GeoBlurData
    {
        public float T; public int GeoIndex; public float[] Inc = new float[4];
        public int Flags; public bool MulZ;
    }
    /// <summary>ChildSetup (0x72): positions the child at the pattern point
    /// its parent emitted from — {t, pattern i16 @+0xC}.</summary>
    public sealed class ChildSetupData { public float T; public int Pattern; }
    /// <summary>Pyrefly (0x2E): 28-point position ring + accumulated color
    /// vecs; renders flipbook sprites along the trail with deterministic
    /// per-point LCG size jitter (noclip Pyrefly).</summary>
    public sealed class PyreflyData
    {
        public float T; public int Flipbook, Speed;
        public float MaxScale, MinScale, SizeRange, StartGap;
        public int TrailLength; public bool RenderHead;
        public float[][] Steps = new float[6][];
    }
    /// <summary>ElectricTarget (0x3B): writes {attractCutoff, maxLerpDelta,
    /// threshold, nextTargetProgram|-1} into its state vec each frame.</summary>
    public sealed class ElecTargetData
    {
        public float T, Threshold, AttractCutoff, MaxLerpDelta; public int Next;
    }
    /// <summary>ElectricColor (0x3C): on crossing adds colorAccs to the 4
    /// color-velocity vecs and the param vels; each integer tick accumulates
    /// vels into the 4 color vecs (core, edge, coreStep, edgeStep).</summary>
    public sealed class ElecColorData
    {
        public float T; public float[][] Accs = new float[4][];
        public float WidthVel, PerturbVel, TipAccel, TargetVel;
    }
    /// <summary>Electricity (0x3D) — lightning chains fed by a PointChain
    /// buffer. Child-chain spawning is not ported (rare; chains still grow,
    /// seek targets and perturb).</summary>
    public sealed class ElecData
    {
        public float T; public int TargetProgram;
        public float[] Core = new float[4], Edge = new float[4],
            CoreStep = new float[4], EdgeStep = new float[4];
        public float TotalDisplacement, WidthDelta, EndWidthFrac, CoreWidthFrac,
            ShrinkFrac, ShrinkStep, PerturbDelta, TipAccel, TargetStrengthDelta;
        public int AngleRange, BlendMode;
        public bool Detach, NormalizeDir, Reversed, InheritPos;
    }

    /// <summary>Rain (0x61): camera-centered wraparound box of streak drops.
    /// Scratch = count × 8 floats (pos3+len, vel3+pad).</summary>
    public sealed class RainData
    {
        public float T, Range, BaseLength, LengthRange;
        public int Count; public float[] Vel = new float[4], VelRange = new float[4];
    }

    public sealed class FlipRect
    {
        public float X0, Y0, X1, Y1;           // quad corners /16
        public float U0, V0, U1, V1;           // uv rect
        public float R, G, B, A; public int Blend; public Gs.Tex0? Tex0;
        public float[]? TriX, TriY;            // tris variant: 4 custom points
    }
    public sealed class FlipFrame { public int Duration, Flags; public List<FlipRect> Rects = new(); }
    public sealed class Flipbook { public List<FlipFrame> Frames = new(); }

    public sealed class Layout
    {
        public List<Particles.EmitterSpec> Emitters = new();
        public List<Bhv> Behaviors = new();
        public List<Geo> Geos = new();
        public List<Pat> Patterns = new();
        public List<Flipbook> Flipbooks = new();
        /// <summary>Index where the magic-wide extra flipbooks begin
        /// (bin.ts extraFlipbookIndex); -1 when none were appended.</summary>
        public int ExtraFlipbookIndex = -1;
        /// <summary>File offset of the parsed PPP container (datum patching).</summary>
        public int Offs;
    }

    // ---------- parsing ----------

    static uint U32(byte[] d, long o) => BitConverter.ToUInt32(d, (int)o);
    static int I32(byte[] d, long o) => BitConverter.ToInt32(d, (int)o);
    static ushort U16(byte[] d, long o) => BitConverter.ToUInt16(d, (int)o);
    static short I16(byte[] d, long o) => BitConverter.ToInt16(d, (int)o);
    static float F32(byte[] d, long o) => BitConverter.ToSingle(d, (int)o);
    static float GetT(byte[] d, long o) { int t = I32(d, o); return t < 0 ? t : t / 4096f; }
    static float[] V4F(byte[] d, long o) => new[] { F32(d, o), F32(d, o + 4), F32(d, o + 8), F32(d, o + 12) };
    static float[] V4I(byte[] d, long o) => new[] { (float)I32(d, o), I32(d, o + 4), I32(d, o + 8), I32(d, o + 12) };
    static float[] V4H(byte[] d, long o) => new[] { (float)I16(d, o), I16(d, o + 2), I16(d, o + 4), I16(d, o + 6) };
    static float[] PV4F(byte[] d, long o) => new[] { F32(d, o), F32(d, o + 4), F32(d, o + 8), 0f };
    static float[] V3H(byte[] d, long o) => new[] { (float)I16(d, o), I16(d, o + 2), I16(d, o + 4) };

    /// <summary>Datum record descriptor: size + parse kind.</summary>
    sealed class DatumSpec
    {
        public int Size; public string Kind = "";
        public DatumSpec(int s, string k) { Size = s; Kind = k; }
    }

    /// <summary>opcode -> (parser, slot-count). Port of instructionTable.</summary>
    static readonly Dictionary<uint, (DatumSpec? D, int Slots)> OpSpec = new()
    {
        [0x00] = (new DatumSpec(0x20, "vec"), 2),  // Velocity
        [0x01] = (new DatumSpec(0x20, "ivec"), 2),
        [0x02] = (new DatumSpec(0x20, "vec"), 2),
        [0x03] = (new DatumSpec(0x10, "hvec"), 2), // ColorVelocity
        [0x04] = (new DatumSpec(0x20, "vec"), 2),
        [0x05] = (new DatumSpec(0x20, "ivec"), 2),
        [0x06] = (new DatumSpec(0x20, "vec"), 2),
        [0x07] = (new DatumSpec(0x10, "hvec"), 2),
        [0x08] = (new DatumSpec(0x20, "vec"), 1),  // Step
        [0x09] = (new DatumSpec(0x20, "ivec"), 1),
        [0x0A] = (new DatumSpec(0x20, "vec"), 1),
        [0x0B] = (new DatumSpec(0x10, "hvec"), 1),
        [0x0C] = (new DatumSpec(0x30, "rand"), 0),
        [0x0D] = (new DatumSpec(0x30, "irand"), 0),
        [0x0E] = (new DatumSpec(0x30, "rand"), 1), // RandomCube
        [0x0F] = (null, 0),                      // NOP
        [0x10] = (null, 3),                      // PosRotScale XYZ
        [0x11] = (null, 2),                      // PosScale
        [0x12] = (new DatumSpec(0xC, "enabled"), 0), // ApplyParent
        [0x13] = (null, 0),                      // ComposedMatrix
        [0x14] = (null, 0),                      // StandardMatrix
        [0x15] = (new DatumSpec(0x8, "geo"), 3),   // SimpleGeo
        [0x17] = (new DatumSpec(0x20, "uvscroll"), 4), // UVScrollGeo
        [0x18] = (new DatumSpec(0xC, "flip"), 4),  // SimpleFlipbook
        [0x1B] = (new DatumSpec(0x14, "emit"), 1), // PeriodicEmit
        [0x1C] = (new DatumSpec(0x14, "emitraw"), 1), // ResettingPeriodicEmit
        [0x1D] = (new DatumSpec(0x30, "rand"), 0), // RandomStep+
        [0x1E] = (new DatumSpec(0x30, "rand"), 0), // RandomStep-
        [0x1F] = (new DatumSpec(0x20, "emitdir"), 1), // PeriodicEmit dir
        [0x25] = (new DatumSpec(0x30, "rand"), 1), // RandomCube-
        [0x27] = (new DatumSpec(0x30, "rand"), 1), // RandomCube+
        [0x29] = (new DatumSpec(0x10, "simpleemit"), 1), // SimpleEmit
        [0x2C] = (null, 0),                      // ComposedMatrix copy
        [0x2F] = (new DatumSpec(0x20, "vec"), 2),  // Velocity.reverse (glareVelP ~ vecP)
        [0x30] = (null, 0),                      // AxialBillboardMatrix
        [0x31] = (new DatumSpec(0x100, "cluster"), 3), // FlipbookCluster
        [0x34] = (null, 0),                      // StandardMatrix copy
        [0x37] = (new DatumSpec(0xC, "geoflags"), 3),
        [0x39] = (new DatumSpec(0x20, "vec"), 1),  // SetValue
        [0x3A] = (null, 1),                      // SetPos
        [0x43] = (new DatumSpec(0x1C, "emitangle"), 1), // PeriodicEmit angle
        [0x47] = (new DatumSpec(0x24, "uvscrollf"), 4),
        [0x20] = (new DatumSpec(0x10, "glarebase"), 1), // GlareBase
        [0x21] = (new DatumSpec(0x10, "glare"), 2),    // SimpleGlare
        [0x23] = (new DatumSpec(0x14, "moreglare"), 2),// MoreGlare
        [0x24] = (new DatumSpec(0x14, "scaledglare"), 2), // ScaledGlare
        [0x32] = (new DatumSpec(0x18, "geoblur"), 3),  // GeoBlur (state,color,mtx)
        [0x1A] = (new DatumSpec(0x24, "ftrail"), 2),   // FlipbookTrail (state,alpha)
        [0x63] = (new DatumSpec(0x28, "ftrailp"), 2),  // FlipbookTrail params variant
        [0x5C] = (new DatumSpec(0xC, "attracttgt"), 2),// AttractingTarget (vec,state)
        [0x5D] = (new DatumSpec(0xC, "attract"), 2),   // Attract (src,dst)
        [0x4D] = (new DatumSpec(0x8, "depth"), 1), // DepthOffset
        [0x4E] = (new DatumSpec(0x28, "uvscrollparam"), 4),
        [0x4F] = (new DatumSpec(0x8, "depth"), 1), // BillboardDepthOffset
        [0x50] = (new DatumSpec(0x10, "geoparams"), 3),
        [0x51] = (new DatumSpec(0x10, "flipparam"), 4),
        [0x53] = (null, 3),                      // PosRotScale YZX
        [0x55] = (new DatumSpec(0x18, "hrand"), 0), // RandomStep
        [0x5E] = (new DatumSpec(0x10, "geoblend"), 3),
        [0x64] = (new DatumSpec(0x10, "light"), 3), // PointLight (stub draw)
        [0x66] = (new DatumSpec(0xF0, "cluster"), 3), // cluster CAMERA
        [0x68] = (new DatumSpec(0x20, "ivec"), 2), // Velocity
        [0x69] = (new DatumSpec(0x20, "vec"), 1),  // LoopStep
        [0x6A] = (new DatumSpec(0x20, "ivec"), 1),
        [0x6B] = (new DatumSpec(0x20, "vec"), 1),
        [0x6C] = (null, 3),                      // PosRotScale XYZ
        [0x6D] = (null, 0),                      // ComposedMatrix
        [0x6E] = (new DatumSpec(0x28, "wrapscroll"), 4), // WrapUVScrollGeo
        [0x73] = (null, 3),                      // PosRotScale ZXY
        [0x74] = (new DatumSpec(0x14, "emitraw"), 1), // PeriodicSimpleEmit
        [0x75] = (new DatumSpec(0x20, "vec"), 2),  // ColorVelocity
        [0x76] = (new DatumSpec(0x14, "emit"), 1), // PeriodicEmit.atOrigin
        [0x78] = (new DatumSpec(0x28, "wrapscroll"), 4),
        [0x79] = (new DatumSpec(0x28, "wrapscroll"), 4), // atOrigin variant
        [0x7B] = (new DatumSpec(0x100, "cluster"), 3), // FlipbookCluster mirror
        [0x7F] = (new DatumSpec(0xF0, "cluster"), 3), // cluster CAMERA
        [0x83] = (new DatumSpec(0xF0, "cluster"), 3), // cluster FIXED
        [0x85] = (new DatumSpec(0x100, "cluster"), 3), // cluster MOVING
        [0x87] = (new DatumSpec(0x18, "light"), 3), // PointLightGroup (stub)
        [0x8A] = (new DatumSpec(0x28, "overlay"), 0), // Overlay (stub — noclip overlayP stride)
        [0x19] = (new DatumSpec(0x28, "ftrailvar"), 2), // FlipbookTrail.VarTail
        [0x2D] = (new DatumSpec(0x1C, "circleblur"), 3),// CircleBlur (render no-op)
        [0x3E] = (new DatumSpec(0x1C, "randemitn"), 1), // RandomEmit normalized
        [0x3F] = (new DatumSpec(0x20, "randemit"), 1),  // RandomEmit
        [0x46] = (new DatumSpec(0x1C, "dualemit"), 1),  // DualEmit
        [0x58] = (new DatumSpec(0xA0, "water"), 3),     // Water (slots expanded)
        [0x61] = (new DatumSpec(0x40, "rain"), 1),      // Rain (color slot)
        [0x71] = (new DatumSpec(0x2C, "wrapscrollwater"), 4), // WrapUV atCamera
        [0x77] = (new DatumSpec(0x10, "flipflip"), 4),  // FlippedFlipbook
        [0x2E] = (new DatumSpec(0x50, "pyrefly"), 1),     // expanded post-remap
        [0x33] = (new DatumSpec(0x60, "wibble"), 1),      // expanded post-remap
        [0x70] = (new DatumSpec(0x2C, "wibble0"), 1),     // expanded post-remap
        [0x72] = (new DatumSpec(0x10, "childsetup"), 2),
        [0x3B] = (new DatumSpec(0x18, "electarget"), 1),
        [0x3C] = (new DatumSpec(0x40, "eleccolor"), 2),   // slots expanded post-remap
        [0x3D] = (new DatumSpec(0x60, "electric"), 3),    // slots expanded post-remap
        // PointChain scratch declarations (chains, vertices) — no datum/slots
        [0x38] = (null, 0), [0x41] = (null, 0), [0x44] = (null, 0),
        [0x45] = (null, 0), [0x49] = (null, 0), [0x4A] = (null, 0),
        [0x4B] = (null, 0), [0x4C] = (null, 0), [0x5A] = (null, 0),
        [0x5B] = (null, 0), [0x80] = (null, 0), [0x88] = (null, 0),
        [0x1001] = (null, 0), [0x1008] = (null, 0), [0x1011] = (null, 0),
        [0x1002] = (null, 0), // FakeGeo — geo index is script-assigned
    };

    static (int Chains, int Verts) ChainSpec(uint op) => op switch
    {
        0x38 => (1, 16), 0x41 => (1, 32), 0x44 => (1, 40), 0x45 => (4, 64),
        0x49 => (4, 24), 0x4A => (4, 16), 0x4B => (16, 64), 0x4C => (4, 48),
        0x5A => (1, 24), 0x5B => (1, 8), 0x80 => (1, 48), 0x88 => (4, 32),
        0x1001 => (1, 128), 0x1008 => (1, 64), 0x1011 => (4, 128),
        _ => (0, 0),
    };

    static int RangeOf(uint op) => op switch
    {
        0x1D or 0x27 => 1, 0x1E or 0x25 => 2, _ => 0,
    };

    /// <summary>Full typed parse: emitters + behaviors/programs/instructions
    /// (remapped slots + decoded datums) + geometry + patterns + flipbooks.
    /// funcMap (magic bins): rawOp is a 40-byte-record index -> real opcode.
    /// synthEmitters: magic/actor layout synthesizes one emitter per behavior.</summary>
    public static Layout Load(byte[] d, int offs, int[]? funcMap = null, bool synthEmitters = false)
    {
        var L = new Layout { Offs = offs };
        int nextInstrId = 0;
        var baseL = Particles.Parse(d, offs, synthEmitters);
        foreach (var e in baseL.Emitters) L.Emitters.Add(e);

        // behaviors / programs / instructions
        int behaviorStart = (int)baseL.BehaviorStart;
        for (int i = 0; i < baseL.BehaviorCount; i++)
        {
            if (behaviorStart + 4 * i + 4 > d.Length) break;
            long behaviorOffs = U32(d, behaviorStart + 4 * i) + offs;
            if (behaviorOffs < 0 || behaviorOffs + 0x10 > d.Length) break;
            var b = new Bhv
            {
                Lifetime = U32(d, behaviorOffs) / 4096f,
                IgnoreLifetime = d[behaviorOffs + 4] != 0,
            };
            long programOffs = U32(d, behaviorOffs + 0x0C) + behaviorOffs;
            int programCount = (int)U32(d, programOffs);
            for (int j = 0; j < programCount; j++)
            {
                uint progStart = U32(d, programOffs + 4 + 4 * j);
                long po = behaviorOffs + progStart;
                var prog = new Prog
                {
                    Flags = (int)U32(d, po + 0x0C),
                    Start = U32(d, po + 0x10) / 4096f,
                    Lifetime = U32(d, po + 0x14) / 4096f,
                };
                int ls = I32(d, po + 0x18) >> 12, le = I32(d, po + 0x1C) >> 12;
                prog.LoopEnd = Math.Max(ls, le);
                prog.LoopLength = Math.Abs(le - ls);
                prog.InstrCount = U16(d, po + 0x26);
                long io = po + 0x28;
                var vecMap = new Dictionary<long, int>();
                int nextSlot = 0;
                int Remap(long off)
                {
                    if (off >= 0 && vecMap.TryGetValue(off, out var v)) return v;
                    while (off < 0 && vecMap.ContainsKey(off)) off--;
                    int s = nextSlot++;
                    vecMap[off] = s;
                    return s;
                }
                for (int k = 0; k < prog.InstrCount; k++, io += 0x10)
                {
                    uint rawOp = U32(d, io);
                    if (funcMap != null && rawOp < funcMap.Length) rawOp = (uint)funcMap[rawOp];
                    int datumSize = U16(d, io + 4);
                    long dataOffs = U32(d, io + 8) + behaviorOffs;
                    long indexOffs = U32(d, io + 0xC) + behaviorOffs;
                    var ins = new Instr { Op = rawOp, Id = nextInstrId++ };
                    if (OpSpec.TryGetValue(rawOp, out var spec))
                    {
                        ins.Slots = new int[spec.Slots];
                        for (int s = 0; s < spec.Slots; s++)
                            ins.Slots[s] = Remap(indexOffs >= 0 && indexOffs + 4L * s + 4 <= d.Length ? I32(d, indexOffs + 4 * s) : -1);
                        if (spec.D != null) DecodeData(d, dataOffs, datumSize, spec.D, ins, Remap);
                        ApplyVariant(ins);
                        // electric ops reserve *consecutive* vec4 slots past the
                        // index-table entry (r(off, n)) — expand the remaps so
                        // Slots[i] lines up with the noclip slot arithmetic
                        if (rawOp == 0x3C && indexOffs >= 0 && indexOffs + 8 <= d.Length)
                        {
                            int o0 = I32(d, indexOffs), o1 = I32(d, indexOffs + 4);
                            // [paramVels, colorVels x4, state, params, colors x4]
                            ins.Slots = new[] {
                                Remap(o0), Remap(o0 + 1), Remap(o0 + 2), Remap(o0 + 3), Remap(o0 + 4),
                                Remap(o1), Remap(o1 + 1), Remap(o1 + 2), Remap(o1 + 3), Remap(o1 + 4), Remap(o1 + 5) };
                        }
                        else if (rawOp == 0x3D && indexOffs >= 0 && indexOffs + 0xC <= d.Length)
                        {
                            int o0 = I32(d, indexOffs), o2 = I32(d, indexOffs + 8);
                            // [state, params, colors x4, direction]
                            ins.Slots = new[] {
                                Remap(o0), Remap(o0 + 1), Remap(o0 + 2), Remap(o0 + 3),
                                Remap(o0 + 4), Remap(o0 + 5), Remap(o2) };
                        }
                        else if (rawOp == 0x58 && indexOffs >= 0 && indexOffs + 0xC <= d.Length)
                        {
                            int o0 = I32(d, indexOffs), o2 = I32(d, indexOffs + 8);
                            // [color, u, v, xPhase, diagPhase, texState] — u block is r(o2,5)
                            ins.Slots = new[] { Remap(o0), Remap(o2), Remap(o2 + 1), Remap(o2 + 2), Remap(o2 + 3), Remap(o2 + 4) };
                        }
                        else if ((rawOp == 0x33 || rawOp == 0x70) && indexOffs >= 0 && indexOffs + 0xC <= d.Length)
                        {
                            int o0 = I32(d, indexOffs), o2 = I32(d, indexOffs + 8);
                            // [colorMod, u, v, wibble, texFrame]
                            ins.Slots = new[] { Remap(o0), Remap(o2), Remap(o2 + 1), Remap(o2 + 2), Remap(o2 + 3) };
                        }
                        else if (rawOp == 0x2E && indexOffs >= 0 && indexOffs + 4 <= d.Length)
                        {
                            int o0 = I32(d, indexOffs);
                            // [colors x6, state] — state = r(-1), a slot
                            // private to this instruction instance
                            ins.Slots = new[] {
                                Remap(o0), Remap(o0 + 1), Remap(o0 + 2), Remap(o0 + 3),
                                Remap(o0 + 4), Remap(o0 + 5), Remap(-1) };
                        }
                        var cc = ChainSpec(rawOp);
                        if (cc.Verts > 0) { ins.ChainCount = cc.Chains; ins.ChainVerts = cc.Verts; }
                    }
                    prog.Instructions.Add(ins);
                }
                prog.VecMap = vecMap;
                prog.Instructions.TrimExcess();
                var chain = prog.Instructions.FirstOrDefault(x => x.ChainVerts > 0);
                if (chain != null)
                    foreach (var x in prog.Instructions)
                        if (x.Electrics != null)
                        { x.ChainCount = chain.ChainCount; x.ChainVerts = chain.ChainVerts; }
                b.Programs.Add(prog);
            }
            L.Behaviors.Add(b);
        }

        // geometry: entries {count, ?, ?, ?, ?, pointStart, ?, start}
        long geoP = baseL.GeometryOffs;
        for (int i = 0; i < baseL.GeoCount; i++, geoP += 0x20)
        {
            if (geoP + 0x20 > d.Length) break;
            int pointCount = (int)U32(d, geoP);
            long pointStart = U32(d, geoP + 0x14) + offs;
            long start = U32(d, geoP + 0x1C) + offs;
            if (pointCount > 0x10000 || pointStart + 6L * pointCount > d.Length
                || start >= d.Length) break;
            var g = new Geo { Points = new float[pointCount][] };
            for (int j = 0; j < pointCount; j++)
                g.Points[j] = V3H(d, pointStart + 6 * j);
            ParseGeoDetail(d, start, g);
            L.Geos.Add(g);
        }

        // patterns
        for (int i = 0; i < baseL.PatternCount; i++)
        {
            long po = baseL.PatternOffs + 8 * i;
            var p = new Pat { GeoIndex = U16(d, po) };
            int n = U16(d, po + 2);
            long s = U32(d, po + 4) + offs;
            p.Indices = new int[n];
            for (int j = 0; j < n; j++) p.Indices[j] = U16(d, s + 2 * j);
            L.Patterns.Add(p);
        }

        // flipbooks
        long fp = baseL.FlipbookOffs;
        for (int i = 0; i < baseL.FlipbookCount; i++)
        {
            long fOffs = U32(d, fp + 4 * i) + offs;
            L.Flipbooks.Add(ParseFlipbook(d, fOffs));
        }
        return L;
    }

    static void ApplyVariant(Instr ins)
    {
        switch (ins.Op)
        {
            case 0x69: case 0x6A: case 0x6B: ins.LoopSubtract = true; break;
            case 0x76: ins.AtOrigin = true; break;
            case 0x79: ins.AtOrigin = true; break;
            case 0x71: ins.AtCamera = true; break;
            case 0x2F: ins.IsReverseVel = true; break;
            case 0x1D: case 0x1E: case 0x25: case 0x27: ins.Range = RangeOf(ins.Op); break;
            case 0x53: ins.EulerOrder = 4; break;  // YZX
            case 0x73: ins.EulerOrder = 2; break;  // ZXY
            case 0x66: case 0x7F: ins.ClusterMode = 2; break; // CAMERA
            case 0x83: ins.ClusterMode = 3; break; // FIXED
            case 0x85: ins.ClusterMode = 1; break; // MOVING
            case 0x18: case 0x51: ins.RenderAll = true; break;
        }
    }

    const uint EndTime = 0xFFFFF000;

    static void DecodeData(byte[] d, long dataOffs, int datumSize, DatumSpec spec, Instr ins, Func<long, int> remap)
    {
        if (datumSize <= 0 || dataOffs < 0 || dataOffs >= d.Length) return;
        // parseData: records of `datumSize` bytes starting AT dataOffs until
        // the t field == 0xFFFFF000 sentinel (endTime).
        long eo = dataOffs;
        int guard = 0;
        while (eo + spec.Size <= d.Length && U32(d, eo) != EndTime && guard++ < 4096)
        {
            switch (spec.Kind)
            {
                case "vec": (ins.Vecs ??= new()).Add(new Vec4Data { T = GetT(d, eo), V = V4F(d, eo + 0x10) }); break;
                case "ivec": (ins.Vecs ??= new()).Add(new Vec4Data { T = GetT(d, eo), V = V4I(d, eo + 0x10) }); break;
                case "hvec": (ins.Vecs ??= new()).Add(new Vec4Data { T = GetT(d, eo), V = V4H(d, eo + 8) }); break;
                case "rand": { var rd = new RandData { T = GetT(d, eo), Target = I32(d, eo + 4), V = V4F(d, eo + 0x10), Peaked = d[eo + 0x20] != 0 }; if (rd.Target >= 0) rd.Target = remap(rd.Target); (ins.Rands ??= new()).Add(rd); break; }
                case "irand": { var rd = new RandData { T = GetT(d, eo), Target = I32(d, eo + 4), V = V4I(d, eo + 0x10), Peaked = d[eo + 0x20] != 0 }; if (rd.Target >= 0) rd.Target = remap(rd.Target); (ins.Rands ??= new()).Add(rd); break; }
                case "hrand": { var rd = new RandData { T = GetT(d, eo), Target = I32(d, eo + 4), V = V4H(d, eo + 8), Peaked = d[eo + 0x10] != 0 }; if (rd.Target >= 0) rd.Target = remap(rd.Target); (ins.Rands ??= new()).Add(rd); break; }
                case "emit":
                    (ins.Emits ??= new()).Add(new EmitData
                    {
                        T = GetT(d, eo), Pattern = U16(d, eo + 4), Count = d[eo + 6], Period = d[eo + 7],
                        Random = d[eo + 8] != 0, Program = I32(d, eo + 0xC), ChildPos = I32(d, eo + 0x10),
                        Transform = true, ChildAngle = -1, ChildDir = -1, ChildDelta = -1,
                    });
                    break;
                case "emitraw":
                    (ins.Emits ??= new()).Add(new EmitData
                    {
                        T = GetT(d, eo), Pattern = U16(d, eo + 4), Count = d[eo + 6], Period = d[eo + 7],
                        Random = d[eo + 8] != 0, Program = I32(d, eo + 0xC), ChildPos = I32(d, eo + 0x10),
                        Transform = false, ChildAngle = -1, ChildDir = -1, ChildDelta = -1,
                    });
                    break;
                case "emitdir":
                    (ins.Emits ??= new()).Add(new EmitData
                    {
                        T = GetT(d, eo), Pattern = U16(d, eo + 4), Scale = F32(d, eo + 8),
                        Count = d[eo + 0xC], Period = d[eo + 0xD], Random = d[eo + 0xE] != 0,
                        Program = I32(d, eo + 0x10), ChildPos = I32(d, eo + 0x14), ChildDir = I32(d, eo + 0x1C),
                        Transform = true, ChildAngle = -1, ChildDelta = -1,
                    });
                    break;
                case "emitangle":
                    (ins.Emits ??= new()).Add(new EmitData
                    {
                        T = GetT(d, eo), Pattern = U16(d, eo + 4), Scale = F32(d, eo + 8),
                        Count = d[eo + 0xC], Period = d[eo + 0xD], Random = d[eo + 0xE] != 0,
                        Program = I32(d, eo + 0x10), ChildPos = I32(d, eo + 0x14), ChildDir = I32(d, eo + 0x18),
                        ChildAngle = I32(d, eo + 0x1C), Transform = true, ChildDelta = -1,
                    });
                    break;
                case "simpleemit":
                    (ins.Emits ??= new()).Add(new EmitData
                    {
                        T = GetT(d, eo), Pattern = U16(d, eo + 4), Count = d[eo + 6], Period = d[eo + 7],
                        Random = d[eo + 8] != 0, Program = I32(d, eo + 0xC), ChildPos = -1,
                        Transform = true, ChildAngle = -1, ChildDir = -1, ChildDelta = -1,
                    });
                    break;
                case "geo": (ins.Geos ??= new()).Add(new GeoData { T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4), Blend = -1 }); break;
                case "geoflags": (ins.Geos ??= new()).Add(new GeoData { T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4), Flags = d[eo + 8], Blend = -1 }); break;
                case "geoparams":
                    (ins.Geos ??= new()).Add(new GeoData
                    {
                        T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4), Flags = d[eo + 8],
                        Fog = d[eo + 9] != 0, Flag2000 = d[eo + 0xA] != 0, Fade = d[eo + 0xB] != 0,
                        DepthOffset = d[eo + 0xC] != 0, Blend = -1,
                    });
                    break;
                case "geoblend":
                    (ins.Geos ??= new()).Add(new GeoData
                    {
                        T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4), Blend = d[eo + 8],
                        Flags = d[eo + 0xA], Fog = d[eo + 0xB] != 0, Flag2000 = d[eo + 0xC] != 0,
                        Fade = d[eo + 0xD] != 0, DepthOffset = d[eo + 0xE] != 0,
                    });
                    break;
                case "depth":
                    // DepthData{offset f32x3 @+0x10, scale @+0x20, space u8 @+0x30};
                    // the TS impl only ever reads data[0]
                    ins.DepthSpace = d[eo + 0x30]; ins.DepthZ = F32(d, eo + 0x18);
                    break;
                case "glarebase":
                    (ins.Glares ??= new()).Add(new GlareData
                    {
                        T = GetT(d, eo), Factor = F32(d, eo + 4), MaxDist = F32(d, eo + 8),
                        DistReduction = d[eo + 0xC], ViewDir = d[eo + 0xD] != 0,
                    });
                    break;
                case "glare":
                    (ins.Glares ??= new()).Add(new GlareData
                    {
                        T = GetT(d, eo), Flipbook = (int)U32(d, eo + 4),
                        Color = new[] { d[eo + 8] / 255f, d[eo + 9] / 255f, d[eo + 10] / 255f, d[eo + 11] / 128f },
                        Scale = F32(d, eo + 0xC),
                    });
                    break;
                case "moreglare":
                    (ins.Glares ??= new()).Add(new GlareData
                    {
                        T = GetT(d, eo), Flipbook = (int)U32(d, eo + 4),
                        Color = new[] { d[eo + 8] / 255f, d[eo + 9] / 255f, d[eo + 10] / 255f, d[eo + 11] / 128f },
                        Scale = F32(d, eo + 0xC), ScaleDist = U16(d, eo + 0x10), AlphaDist = d[eo + 0x12],
                    });
                    break;
                case "scaledglare":
                    (ins.Glares ??= new()).Add(new GlareData
                    {
                        T = GetT(d, eo), Flipbook = (int)U32(d, eo + 4),
                        Color = new[] { d[eo + 8] / 255f, d[eo + 9] / 255f, d[eo + 10] / 255f, d[eo + 11] / 128f },
                        MaxScale = F32(d, eo + 0xC), MaxRadius = U16(d, eo + 0x10),
                        XScaleMode = d[eo + 0x12], YScaleMode = d[eo + 0x13],
                    });
                    break;
                case "geoblur":
                    (ins.GeoBlurs ??= new()).Add(new GeoBlurData
                    {
                        T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4),
                        Inc = PV4F(d, eo + 8), Flags = d[eo + 0x14], MulZ = d[eo + 0x15] != 0,
                    });
                    break;
                case "attract":
                    (ins.Attracts ??= new()).Add(new AttractData { T = GetT(d, eo), Target = I32(d, eo + 4), Offset = I32(d, eo + 8) });
                    break;
                case "attracttgt":
                    (ins.Attracts ??= new()).Add(new AttractData { T = GetT(d, eo), MinDist = F32(d, eo + 4), RepelDist = F32(d, eo + 8) });
                    break;
                case "ftrail": case "ftrailp":
                    {
                        var td = new TrailData
                        {
                            T = GetT(d, eo), Flipbook = (int)U32(d, eo + 4), Speed = (int)U32(d, eo + 8),
                            MaxScale = F32(d, eo + 0xC), MinScale = F32(d, eo + 0x10),
                            HeadColor = new[] { d[eo + 0x14] / 255f, d[eo + 0x15] / 255f, d[eo + 0x16] / 255f, d[eo + 0x17] / 128f },
                            TailColor = new[] { d[eo + 0x18] / 255f, d[eo + 0x19] / 255f, d[eo + 0x1A] / 255f, d[eo + 0x1B] / 128f },
                            StartGap = F32(d, eo + 0x1C),
                            TrailLength = eo + 0x21 <= d.Length ? d[eo + 0x20] : 0,
                            RenderHead = eo + 0x23 <= d.Length && d[eo + 0x22] != 0,
                        };
                        (ins.Trails ??= new()).Add(td);
                    }
                    break;
                case "randemit":
                    (ins.Emits ??= new()).Add(new EmitData
                    {
                        T = GetT(d, eo), Pattern = U16(d, eo + 4), Scale = F32(d, eo + 8),
                        Count = d[eo + 0xC], Mask = d[eo + 0xD], Random = d[eo + 0xE] != 0,
                        Program = I32(d, eo + 0x10), ChildPos = I32(d, eo + 0x14), ChildDir = I32(d, eo + 0x1C),
                        Transform = true, ChildAngle = -1, ChildDelta = -1,
                    });
                    break;
                case "randemitn":
                    (ins.Emits ??= new()).Add(new EmitData
                    {
                        T = GetT(d, eo), Pattern = U16(d, eo + 4), Scale = 1,
                        Count = d[eo + 6], Mask = d[eo + 7], Random = d[eo + 8] != 0,
                        Program = I32(d, eo + 0xC), ChildPos = I32(d, eo + 0x10), ChildDir = I32(d, eo + 0x18),
                        Transform = true, ChildAngle = -1, ChildDelta = -1,
                    });
                    break;
                case "dualemit":
                    (ins.Emits ??= new()).Add(new EmitData
                    {
                        T = GetT(d, eo), Pattern = U16(d, eo + 4), // deltaPattern@6 asserted equal upstream
                        Count = d[eo + 8], Mask = d[eo + 9], Random = d[eo + 0xA] != 0,
                        Program = I32(d, eo + 0xC), ChildPos = I32(d, eo + 0x10), ChildDelta = I32(d, eo + 0x18),
                        Transform = true, ChildAngle = -1, ChildDir = -1,
                    });
                    break;
                case "rain":
                    (ins.Rains ??= new()).Add(new RainData
                    {
                        T = GetT(d, eo), Range = F32(d, eo + 4), Count = (int)U32(d, eo + 8),
                        Vel = PV4F(d, eo + 0x10), VelRange = PV4F(d, eo + 0x20),
                        BaseLength = F32(d, eo + 0x30), LengthRange = F32(d, eo + 0x34),
                    });
                    break;
                case "water":
                    (ins.Geos ??= new()).Add(new GeoData
                    {
                        T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4),
                        UInc = PV4F(d, eo + 8), VInc = PV4F(d, eo + 0x14),
                        DiagSpeed = V4F(d, eo + 0x30), XSpeed = V4F(d, eo + 0x60),
                        WaterTexSlot = d[eo + 0x80], WaterTexDur = Math.Max(1, (int)d[eo + 0x81]),
                        BlendMode = d[eo + 0x82], BlendAlpha = d[eo + 0x83],
                        Flag2000 = d[eo + 0x85] != 0, Fog = d[eo + 0x86] != 0,
                        DepthOffset = d[eo + 0x87] != 0, Radius = F32(d, eo + 0x8C), Blend = -1,
                    });
                    break;
                case "wrapscrollwater":
                    (ins.Geos ??= new()).Add(new GeoData
                    {
                        T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4),
                        UInc = PV4F(d, eo + 8), VInc = PV4F(d, eo + 0x14),
                        WaterTexSlot = d[eo + 0x22], WaterTexDur = Math.Max(1, (int)d[eo + 0x23]),
                        BlendMode = d[eo + 0x24], BlendAlpha = d[eo + 0x25], Flags = d[eo + 0x26],
                        Fog = d[eo + 0x27] != 0, Fade = d[eo + 0x28] != 0, Flag2000 = d[eo + 0x29] != 0,
                        Blend = -1,
                    });
                    break;
                case "flipflip":
                    (ins.Flips ??= new()).Add(new FlipData
                    {
                        T = GetT(d, eo), Index = (int)U32(d, eo + 4), Speed = U16(d, eo + 8),
                        FlipX = (d[eo + 0xF] & 2) != 0, FlipY = (d[eo + 0xF] & 1) != 0,
                    });
                    break;
                case "ftrailvar":
                    (ins.Trails ??= new()).Add(new TrailData
                    {
                        T = GetT(d, eo), Flipbook = (int)U32(d, eo + 4), Speed = U16(d, eo + 8),
                        MaxScale = 1, MinScale = F32(d, eo + 0x18),
                        HeadColor = new[] { I16(d, eo + 0x10) / 16384f, I16(d, eo + 0x12) / 16384f, I16(d, eo + 0x14) / 16384f, I16(d, eo + 0x16) / 16384f },
                        TailColor = new[] { 0f, 0, 0, 0 }, StartGap = F32(d, eo + 0x1C),
                        TrailLength = U16(d, eo + 0x20), RenderHead = false,
                    });
                    break;
                case "childsetup":
                    (ins.ChildSetups ??= new()).Add(new ChildSetupData { T = GetT(d, eo), Pattern = I16(d, eo + 0xC) });
                    break;
                case "pyrefly":
                    {
                        var pd = new PyreflyData
                        {
                            T = GetT(d, eo), Flipbook = (int)U32(d, eo + 4),
                            Speed = (int)U32(d, eo + 8),
                            MaxScale = F32(d, eo + 0xC), MinScale = F32(d, eo + 0x10),
                            SizeRange = F32(d, eo + 0x14), StartGap = F32(d, eo + 0x18),
                            TrailLength = d[eo + 0x1C],
                            RenderHead = d[eo + 0x1D] != 0,
                        };
                        for (int si = 0; si < 6; si++) pd.Steps[si] = V4H(d, eo + 0x20 + 8 * si);
                        (ins.Pyreflys ??= new()).Add(pd);
                    }
                    break;
                case "electarget":
                    (ins.ElecTargets ??= new()).Add(new ElecTargetData
                    {
                        T = GetT(d, eo), Threshold = F32(d, eo + 4),
                        AttractCutoff = F32(d, eo + 8), MaxLerpDelta = F32(d, eo + 0xC),
                        Next = I32(d, eo + 0x10),
                    });
                    break;
                case "eleccolor":
                    {
                        var ec = new ElecColorData
                        {
                            T = GetT(d, eo),
                            WidthVel = F32(d, eo + 0x28), PerturbVel = F32(d, eo + 0x2C),
                            TipAccel = F32(d, eo + 0x30), TargetVel = F32(d, eo + 0x34),
                        };
                        ec.Accs[0] = V4H(d, eo + 8); ec.Accs[1] = V4H(d, eo + 0x10);
                        ec.Accs[2] = V4H(d, eo + 0x18); ec.Accs[3] = V4H(d, eo + 0x20);
                        (ins.ElecColors ??= new()).Add(ec);
                    }
                    break;
                case "electric":
                    (ins.Electrics ??= new()).Add(new ElecData
                    {
                        T = GetT(d, eo), TargetProgram = I32(d, eo + 4),
                        Core = V4H(d, eo + 0x10), Edge = V4H(d, eo + 0x18),
                        CoreStep = V4H(d, eo + 0x20), EdgeStep = V4H(d, eo + 0x28),
                        TotalDisplacement = F32(d, eo + 0x30), WidthDelta = F32(d, eo + 0x34),
                        EndWidthFrac = F32(d, eo + 0x38), CoreWidthFrac = F32(d, eo + 0x3C),
                        ShrinkFrac = F32(d, eo + 0x40), ShrinkStep = F32(d, eo + 0x44),
                        PerturbDelta = F32(d, eo + 0x48), TipAccel = F32(d, eo + 0x4C),
                        TargetStrengthDelta = F32(d, eo + 0x50),
                        AngleRange = I16(d, eo + 0x54), BlendMode = d[eo + 0x58],
                        Detach = d[eo + 0x59] != 0, NormalizeDir = d[eo + 0x5A] == 0,
                        Reversed = d[eo + 0x5B] != 0, InheritPos = d[eo + 0x5C] != 0,
                    });
                    break;
                case "flip": (ins.Flips ??= new()).Add(new FlipData { T = GetT(d, eo), Index = (int)U32(d, eo + 4), Speed = U16(d, eo + 8) }); break;
                case "flipparam": (ins.Flips ??= new()).Add(new FlipData { T = GetT(d, eo), Index = (int)U32(d, eo + 4), Speed = U16(d, eo + 8) }); break;
                case "wibble":
                    (ins.Geos ??= new()).Add(new GeoData
                    {
                        T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4),
                        UInc = PV4F(d, eo + 8), VInc = PV4F(d, eo + 0x14),
                        WibbleVel = new[] { F32(d, eo + 0x30), F32(d, eo + 0x34), F32(d, eo + 0x38), F32(d, eo + 0x3C) },
                        WaterTexSlot = d[eo + 0x50], WaterTexDur = Math.Max(1, (int)d[eo + 0x51]),
                        BlendMode = d[eo + 0x52], BlendAlpha = d[eo + 0x53],
                        Flags = d[eo + 0x54], Flag2000 = d[eo + 0x55] != 0,
                        Fog = d[eo + 0x56] != 0, Blend = -1,
                    });
                    break;
                case "wibble0":
                    (ins.Geos ??= new()).Add(new GeoData
                    {
                        T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4),
                        UInc = PV4F(d, eo + 8), VInc = PV4F(d, eo + 0x14),
                        WaterTexSlot = d[eo + 0x20], WaterTexDur = Math.Max(1, (int)d[eo + 0x21]),
                        BlendMode = d[eo + 0x22], BlendAlpha = d[eo + 0x23],
                        Flags = d[eo + 0x24], Flag2000 = d[eo + 0x25] != 0,
                        Fog = d[eo + 0x26] != 0, Blend = -1,
                    });
                    break;
                case "uvscroll":
                case "uvscrollf":
                case "uvscrollparam":
                    {
                        var g = new GeoData
                        {
                            T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4),
                            UInc = PV4F(d, eo + 8), VInc = PV4F(d, eo + 0x14), Blend = -1,
                        };
                        if (spec.Kind == "uvscrollf") g.Flags = d[eo + 0x20];
                        if (spec.Kind == "uvscrollparam")
                        {
                            g.Flags = d[eo + 0x20]; g.Fog = d[eo + 0x21] != 0;
                            g.Flag2000 = d[eo + 0x22] != 0; g.Fade = d[eo + 0x23] != 0; g.DepthOffset = d[eo + 0x24] != 0;
                        }
                        (ins.Geos ??= new()).Add(g);
                    }
                    break;
                case "wrapscroll":
                    (ins.Geos ??= new()).Add(new GeoData
                    {
                        T = GetT(d, eo), GeoIndex = (int)U32(d, eo + 4),
                        UInc = PV4F(d, eo + 8), VInc = PV4F(d, eo + 0x14),
                        BlendMode = d[eo + 0x20], BlendAlpha = d[eo + 0x21], Flags = d[eo + 0x22],
                        Fog = d[eo + 0x23] != 0, Fade = d[eo + 0x24] != 0, Flag2000 = d[eo + 0x25] != 0,
                        DepthOffset = d[eo + 0x26] != 0, Blend = -1,
                    });
                    break;
                case "cluster":
                    (ins.Clusters ??= new()).Add(new ClusterData
                    {
                        T = GetT(d, eo), Index = (int)U32(d, eo + 4), ChildCount = (int)U32(d, eo + 8),
                        Speed = U16(d, eo + 0xC), OffsetRange = V4F(d, eo + 0x10),
                        OffsedPeaked = spec.Size == 0x100 && d[eo + 0x20] != 0,
                        Vel = V4F(d, eo + (spec.Size == 0x100 ? 0x40 : 0x30)),
                        VelRange = V4F(d, eo + (spec.Size == 0x100 ? 0x50 : 0x40)),
                        Accel = V4F(d, eo + (spec.Size == 0x100 ? 0x60 : 0x50)),
                        Scale = V4F(d, eo + (spec.Size == 0x100 ? 0x70 : 0x60)),
                        ScaleRange = V4F(d, eo + (spec.Size == 0x100 ? 0x80 : 0x70)),
                        ScaleVel = V4F(d, eo + 0x90), ScaleAccel = V4F(d, eo + 0xA0),
                        RollRange = F32(d, eo + (spec.Size == 0x100 ? 0xB8 : 0xA8)),
                        ResetCount = d[eo + (spec.Size == 0x100 ? 0xC0 : 0xB0)],
                        ResetInterval = d[eo + (spec.Size == 0x100 ? 0xC1 : 0xB1)],
                        MirrorFlags = spec.Size == 0x100 ? d[eo + 0xF1] : 0,
                    });
                    break;
                case "enabled": (ins.Enables ??= new()).Add(new EnabledData { T = GetT(d, eo), Enabled = I32(d, eo + 4) != -1 }); break;
                case "light": case "overlay": break; // stubs
            }
            eo += spec.Size;
        }
    }

    static void ParseGeoDetail(byte[] d, long start, Geo g)
    {
        if (start < 0 || start + 0x20 > d.Length) return;
        g.Flags = U32(d, start);
        g.BlendSettings = U16(d, start + 0x16);
        long geoOffs = U32(d, start + 4) + start;
        long pointOffs = U32(d, start + 8) + start;
        long normalOffs = U32(d, start + 0xC) + start;
        int vertexCount = U16(d, start + 0x12);
        if (vertexCount == 0 || geoOffs >= d.Length || pointOffs + 6L * vertexCount > d.Length) return;
        g.Verts = new float[vertexCount][];
        for (int i = 0; i < vertexCount; i++) g.Verts[i] = V3H(d, pointOffs + 6 * i);
        g.Normals = new float[vertexCount][];
        for (int i = 0; i < vertexCount; i++)
            g.Normals[i] = normalOffs + 6 * i + 6 <= d.Length
                ? new[] { I16(d, normalOffs + 6 * i) / 4096f, I16(d, normalOffs + 6 * i + 2) / 4096f, I16(d, normalOffs + 6 * i + 4) / 4096f }
                : new float[3];

        long o = geoOffs;
        while (o + 0x10 <= d.Length)
        {
            int rawPrim = d[o + 1];
            if (rawPrim == 0xFF) break;
            int prim = rawPrim >> 1;
            int count = U16(d, o + 2);
            var tex0 = Gs.DecodeTex0(U32(d, o + 8), U32(d, o + 0xC));
            int vpp = prim <= 1 ? 3 : 4;
            int stride = prim switch { 0 => 3 * 4 + 4 * 2, 1 => 3 * 4 + 8 + 3 * 4, 2 => 4 * 4 + 4 * 2, 3 => 4 * 4 + 8 + 4 * 4, _ => -1 };
            if (stride < 0) break;
            if ((rawPrim & 1) != 0) stride += 8;
            bool hasTex = prim == 1 || prim == 3;
            o += 0x10;
            for (int i = 0; i < count && o + stride <= d.Length; i++, o += stride)
            {
                var pr = new GeoPrim { Tex0 = hasTex ? tex0 : null };
                long indexStart = o + 4 * vpp;
                long uvStart = indexStart + 8;
                pr.Indices = new int[vpp];
                pr.Colors = new float[vpp][];
                pr.Uvs = new float[vpp][];
                for (int j = 0; j < vpp; j++)
                {
                    pr.Indices[j] = U16(d, indexStart + 2 * j);
                    pr.Colors[j] = new[] { d[o + 4 * j] / 128f, d[o + 4 * j + 1] / 128f, d[o + 4 * j + 2] / 128f, d[o + 4 * j + 3] / 128f };
                    pr.Uvs[j] = hasTex ? new[] { U16(d, uvStart + 4 * j) / 4096f, U16(d, uvStart + 4 * j + 2) / 4096f } : new float[2];
                }
                g.Prims.Add(pr);
            }
        }
    }

    internal static Flipbook ParseFlipbook(byte[] d, long fOffs)
    {
        var fb = new Flipbook();
        if (fOffs < 0 || fOffs + 0x10 > d.Length) return fb;
        int frameCount = U16(d, fOffs + 4);
        for (int j = 0; j < frameCount; j++)
        {
            long fp = fOffs + 0x10 + 8 * j;
            if (fp + 8 > d.Length) break;
            long frameOffs = U16(d, fp) + fOffs;
            var fr = new FlipFrame { Duration = U16(d, fp + 2), Flags = d[fp + 4] };
            if (frameOffs < 0 || frameOffs + 0x20 > d.Length) { fb.Frames.Add(fr); continue; }
            int drawFlags = U16(d, frameOffs);
            int rectCount = U16(d, frameOffs + 2);
            long colorStart = U32(d, frameOffs + 4) + frameOffs;
            var tex0 = Gs.DecodeTex0(U32(d, frameOffs + 0x10), U32(d, frameOffs + 0x14));
            bool tris = (drawFlags & 8) != 0;
            long ro = frameOffs + 0x20;
            for (int i = 0; i < rectCount; i++, colorStart += 8)
            {
                if (ro + (tris ? 0x20 : 0x18) > d.Length) break;
                var r = new FlipRect { Tex0 = tex0 };
                if (tris)
                {
                    r.TriX = new float[4]; r.TriY = new float[4];
                    for (int k = 0; k < 4; k++, ro += 4) { r.TriX[k] = I16(d, ro) / 16f; r.TriY[k] = I16(d, ro + 2) / 16f; }
                }
                else
                {
                    r.X0 = I16(d, ro) / 16f; r.Y0 = I16(d, ro + 2) / 16f;
                    r.X1 = I16(d, ro + 4) / 16f; r.Y1 = I16(d, ro + 6) / 16f;
                    ro += 8;
                }
                float u0 = I16(d, ro) / 4096f, v0 = I16(d, ro + 2) / 4096f;
                r.U0 = u0; r.V0 = v0; r.U1 = u0 + I16(d, ro + 4) / 4096f; r.V1 = v0 + I16(d, ro + 6) / 4096f;
                ro += 8;
                if (colorStart + 5 <= d.Length)
                {
                    r.R = d[colorStart] / 128f; r.G = d[colorStart + 1] / 128f;
                    r.B = d[colorStart + 2] / 128f; r.A = d[colorStart + 3] / 128f;
                    r.Blend = d[colorStart + 4];
                }
                else { r.R = r.G = r.B = r.A = 1; }
                fr.Rects.Add(r);
            }
            fb.Frames.Add(fr);
        }
        return fb;
    }

    static float Dist3(float[] a, float[] b)
    {
        float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>FlipbookTrail ring store — per (particle, instr) lazily
    /// allocated 31-slot position history. noclip initScratch seeds every
    /// slot with the CURRENT particle position (a fresh trail starts fully
    /// collapsed); zero-seeded slots would streak quads back to the origin.</summary>
    static float[][] TrailStore(Particle p, int instrId, float px, float py, float pz)
    {
        var bufs = p.TrailBufs ??= new();
        if (!bufs.TryGetValue(instrId, out var ring))
        {
            ring = new float[31][];
            for (int i = 0; i < 31; i++)
                ring[i] = new[] { px, py, pz };
            bufs[instrId] = ring;
        }
        return ring;
    }

    // ---------- runtime ----------

    static readonly HashSet<int> ElecDbgSeen = new();

    /// <summary>Runtime state of one lightning chain (noclip ChainState).</summary>
    public sealed class ChainBuf
    {
        public float[] St;   // per chain: currLength, flags, pointsTraveled
        public byte[] Meta;  // per chain: next, child, descCount, target, start, end
        public float[] V;    // chains * vtx * 8: pos3, child, dir3, shrink
        public int Chains, Vtx, Stride;
        public const int FLAG_ACTIVE = 1, FLAG_HIT_MAX = 4, FLAG_DONE_GROWING = 0x20,
            FLAG_DONE_AT_MAX = 0x80, FLAG_FIXED_TARGET = 0x100, FLAG_INHERIT_POS = 0x200;
        public static ChainBuf Create(int chains, int vtx) => new()
        {
            Chains = chains, Vtx = vtx, Stride = vtx * 8,
            St = new float[chains * 3], Meta = new byte[chains * 6],
            V = new float[chains * vtx * 8],
        };
        public int Alloc()   // first inactive chain, else -1
        {
            for (int i = 0; i < Chains; i++) if (St[i * 3 + 1] == 0) return i;
            return -1;
        }
    }

    public sealed class Particle
    {
        public float[][] Vecs = new float[VecCount][];
        public float T, PrevT = -0.01f;
        /// <summary>Last frame's render matrix (GeoBlur ghost).</summary>
        public float[]? PrevRender;
        /// <summary>FlipbookTrail ring buffers: instr Id -> 31 local-space
        /// positions. Written on phase rollover, read as a position history.</summary>
        public Dictionary<int, float[][]>? TrailBufs;
        /// <summary>PointChain scratch per Electricity instr (Id-keyed).</summary>
        public Dictionary<int, ChainBuf>? ElecBufs;
        /// <summary>Rain drop scratch per Rain instr: count×8 (pos3+len, vel4).</summary>
        public Dictionary<int, float[]>? RainBufs;
        public float[] Pose = Identity();
        public float[] Render = Identity();
        public Particle? Next, Parent;
        public bool Visible = true;
        public Emitter? Emit;
        public Particle() { for (int i = 0; i < VecCount; i++) Vecs[i] = new float[4]; }
        public bool Crossed(float t) => PrevT < t && T >= t;
    }

    public enum EmitterState { Running, Ending, Waiting }

    public sealed class Emitter
    {
        public Particles.EmitterSpec Spec;
        public Bhv B;
        public Particle?[] Parts;
        public float T, PrevT = -1, WaitTimer;
        public EmitterState State = EmitterState.Running;
        public float[] Pose = Identity();
        public float[] Color = { 1, 1, 1, 1 };
        public bool Dead;
        /// <summary>VM-driven bins start emitters inert; op 0xDC spawns
        /// active instances on demand.</summary>
        public bool Active = true;

        public Emitter(Particles.EmitterSpec s, Bhv b)
        {
            Spec = s; B = b; WaitTimer = s.Delay;
            Parts = new Particle?[b.Programs.Count];
            // emitter.pose = R(euler,order)·S(scale), T=pos
            Pose = RotEuler(s.Euler[0] * ToRad, s.Euler[1] * ToRad, s.Euler[2] * ToRad, s.EulerOrder);
            Pose[0] *= (float)s.Scale[0]; Pose[1] *= (float)s.Scale[0]; Pose[2] *= (float)s.Scale[0]; Pose[3] *= (float)s.Scale[0];
            Pose[4] *= (float)s.Scale[1]; Pose[5] *= (float)s.Scale[1]; Pose[6] *= (float)s.Scale[1]; Pose[7] *= (float)s.Scale[1];
            Pose[8] *= (float)s.Scale[2]; Pose[9] *= (float)s.Scale[2]; Pose[10] *= (float)s.Scale[2]; Pose[11] *= (float)s.Scale[2];
            Pose[12] = (float)s.Pos[0]; Pose[13] = (float)s.Pos[1]; Pose[14] = (float)s.Pos[2];
        }

        public Particle Emit(int progIdx)
        {
            var p = new Particle { Emit = this };
            p.Next = Parts[progIdx];
            Parts[progIdx] = p;
            foreach (var ins in B.Programs[progIdx].Instructions) ResetInstr(ins, p);
            return p;
        }
    }

    static void ResetInstr(Instr ins, Particle p)
    {
        // Instruction.reset() defaults: zero the state/base vecs
        switch (ins.Op)
        {
            case 0x00: case 0x01: case 0x02: case 0x04: case 0x05: case 0x06: case 0x68: case 0x2F:
            case 0x03: case 0x07: case 0x75:
                if (ins.Slots.Length > 1) Array.Clear(p.Vecs[ins.Slots[1]]); break;
            case 0x08: case 0x09: case 0x0A: case 0x0B: case 0x69: case 0x6A: case 0x6B:
            case 0x1B: case 0x1C: case 0x1F: case 0x43: case 0x74: case 0x76: case 0x29:
            case 0x31: case 0x66: case 0x7B: case 0x7F: case 0x83: case 0x85:
            case 0x3E: case 0x3F: case 0x46: case 0x77:
                if (ins.Slots.Length > 0) Array.Clear(p.Vecs[ins.Slots[0]]); break;
            case 0x58: // Water: zero u,v,xPhase,diagPhase (texState survives)
                for (int i = 1; i < 5 && i < ins.Slots.Length; i++) Array.Clear(p.Vecs[ins.Slots[i]]);
                break;
            case 0x39:
                if (ins.Slots.Length > 0) p.Vecs[ins.Slots[0]][3] = 1; break;
            case 0x18: case 0x51:
                if (ins.Slots.Length > 0) { p.Vecs[ins.Slots[0]][0] = 0; p.Vecs[ins.Slots[0]][1] = 0; } break;
            case 0x33: case 0x70: // WibbleUVScrollGeo: zero u/v/wibble
                for (int i = 1; i < 4 && i < ins.Slots.Length; i++) Array.Clear(p.Vecs[ins.Slots[i]]);
                break;
            case 0x3C: // ElectricColor: zero paramVels + 4 colorVels
                for (int i = 0; i < 5 && i < ins.Slots.Length; i++) Array.Clear(p.Vecs[ins.Slots[i]]);
                break;
            case 0x2E: // Pyrefly: zero colors, state=[flipT=0, phase=0, rng=rand]
                for (int i = 0; i < 6 && i < ins.Slots.Length; i++) Array.Clear(p.Vecs[ins.Slots[i]]);
                if (ins.Slots.Length > 6)
                {
                    p.Vecs[ins.Slots[6]][0] = 0; p.Vecs[ins.Slots[6]][1] = 0;
                    p.Vecs[ins.Slots[6]][2] = (float)(Rng.NextDouble() * 0x1000);
                }
                break;
            case 0x3D: // Electricity: state = [unused, firstChain=-1]
                if (ins.Slots.Length > 0) { p.Vecs[ins.Slots[0]][0] = 0; p.Vecs[ins.Slots[0]][1] = -1; }
                p.ElecBufs?.Remove(ins.Id);
                ZeroElecParams(ins, p);
                break;
        }
    }

    /// <summary>Electricity.loop / ElectricColor reset tail: zero the params
    /// vec and the 4 color vecs (Slots[1..5] on 0x3D).</summary>
    static void ZeroElecParams(Instr ins, Particle p)
    {
        for (int i = 1; i < 6 && i < ins.Slots.Length; i++) Array.Clear(p.Vecs[ins.Slots[i]]);
    }

    static void LoopInstr(Instr ins, Particle p)
    {
        switch (ins.Op)
        {
            case 0x69: case 0x6A: case 0x6B:
                if (ins.Slots.Length > 0 && ins.Vecs is { Count: > 0 })
                    for (int i = 0; i < 4; i++) p.Vecs[ins.Slots[0]][i] -= ins.Vecs[0].V[i];
                break;
            case 0x1C: case 0x1B: case 0x1F: case 0x43: case 0x74: case 0x76:
                if (ins.Op == 0x1C && ins.Slots.Length > 0) Array.Clear(p.Vecs[ins.Slots[0]]);
                break; // PeriodicEmit loop = no-op (except 0x1C resets)
            case 0x39: break; // SetValue loop = no-op
            case 0x3D: ZeroElecParams(ins, p); break;
            case 0x33: case 0x70:
                if (ins.Slots.Length > 3)
                { p.Vecs[ins.Slots[1]][1] = 0; p.Vecs[ins.Slots[1]][2] = 0;
                  p.Vecs[ins.Slots[2]][1] = 0; p.Vecs[ins.Slots[2]][2] = 0; }
                break;
            case 0x58: // Water.loop: same u/v damping
                if (ins.Slots.Length > 2)
                { p.Vecs[ins.Slots[1]][1] = 0; p.Vecs[ins.Slots[1]][2] = 0;
                  p.Vecs[ins.Slots[2]][1] = 0; p.Vecs[ins.Slots[2]][2] = 0; }
                break;
            case 0x2E:
                for (int i = 0; i < 6 && i < ins.Slots.Length; i++) Array.Clear(p.Vecs[ins.Slots[i]]);
                if (ins.Slots.Length > 6) { p.Vecs[ins.Slots[6]][0] = 0; p.Vecs[ins.Slots[6]][1] = 0; }
                break;
            case 0x17: case 0x47: case 0x4E:
                if (ins.Slots.Length > 2) { Array.Clear(p.Vecs[ins.Slots[2]]); Array.Clear(p.Vecs[Math.Min(ins.Slots[2] + 1, VecCount - 1)]); }
                break;
            default: ResetInstr(ins, p); break;
        }
    }

    static readonly Random Rng = new();
    static readonly bool Dbg = Environment.GetEnvironmentVariable("FFX_PSIM_DEBUG") != null;

    static float RandFactor(int dist, int range)
    {
        float f = dist switch
        {
            1 => (float)(Rng.NextDouble() + Rng.NextDouble() - 1),
            2 => (float)(Rng.NextDouble() * (Rng.NextDouble() * 2 - 1)),
            _ => (float)(Rng.NextDouble() * 2 - 1),
        };
        if (range != 0) { f = (f + 1) / 2; if (range == 2) f = -f; }
        return f;
    }

    static T Curr<T>(float t, List<T>? data) where T : class
    {
        if (data == null || data.Count == 0) return null!;
        dynamic curr = data[0];
        foreach (var dd in data) { dynamic x = dd; if (x.T <= t) curr = x; }
        return curr;
    }

    /// <summary>Instruction.update() dispatch — covers the census subset.</summary>
    static void Update(Instr ins, Particle p, Sys sys)
    {
        float dt = p.T - p.PrevT;
        switch (ins.Op)
        {
            case 0x08: case 0x09: case 0x0A: case 0x0B: case 0x69: case 0x6A: case 0x6B:
                {
                    var cd = Curr(p.T, ins.Vecs); if (cd == null) break;
                    if (p.Crossed(cd.T))
                        for (int i = 0; i < 4; i++) p.Vecs[ins.Slots[0]][i] += cd.V[i];
                    break;
                }
            case 0x00: case 0x01: case 0x02: case 0x04: case 0x05: case 0x06: case 0x68: case 0x2F:
                {
                    int bi = ins.Slots[0], vi = ins.Slots[1];
                    if (ins.IsReverseVel) (bi, vi) = (vi, bi);
                    var cd = Curr(p.T, ins.Vecs); if (cd == null) break;
                    var baseV = p.Vecs[bi]; var velV = p.Vecs[vi];
                    for (int i = 0; i < 4; i++) baseV[i] += velV[i] * dt;
                    if (p.Crossed(cd.T))
                    {
                        float newDT = p.T - cd.T;
                        for (int i = 0; i < 4; i++) { velV[i] += cd.V[i]; baseV[i] += cd.V[i] * (1 + newDT); }
                    }
                    break;
                }
            case 0x03: case 0x07: case 0x75:
                {
                    var cd = Curr(p.T, ins.Vecs); if (cd == null) break;
                    var baseV = p.Vecs[ins.Slots[0]]; var velV = p.Vecs[ins.Slots[1]];
                    if (p.Crossed(cd.T))
                        for (int i = 0; i < 4; i++) velV[i] += cd.V[i];
                    if (p.PrevT < (float)Math.Floor(p.T))
                        for (int i = 0; i < 4; i++) baseV[i] += velV[i];
                    for (int i = 0; i < 4; i++) { baseV[i] %= 0x10000; velV[i] %= 0x10000; }
                    break;
                }
            case 0x0C: case 0x0D: case 0x1D: case 0x1E: case 0x55:
                {
                    var cd = Curr(p.T, ins.Rands); if (cd == null) break;
                    if (p.Crossed(cd.T) && cd.Target >= 0)
                    {
                        float f = RandFactor(cd.Peaked ? 1 : 0, ins.Range);
                        var tv = p.Vecs[cd.Target];
                        for (int i = 0; i < 4; i++) tv[i] += cd.V[i] * f;
                    }
                    break;
                }
            case 0x0E: case 0x25: case 0x27:
                {
                    var cd = Curr(p.T, ins.Rands); if (cd == null) break;
                    var rand = p.Vecs[ins.Slots[0]];
                    if (p.Crossed(0))
                        for (int i = 0; i < 3; i++) rand[i] = RandFactor(cd.Peaked ? 1 : 0, ins.Range);
                    if (p.Crossed(cd.T) && cd.Target >= 0)
                    {
                        var tv = p.Vecs[cd.Target];
                        for (int i = 0; i < 4; i++) tv[i] += rand[i] * cd.V[i];
                    }
                    break;
                }
            case 0x39:
                {
                    var cd = Curr(p.T, ins.Vecs); if (cd == null) break;
                    if (p.Crossed(cd.T))
                    {
                        p.Vecs[ins.Slots[0]][0] = cd.V[0];
                        p.Vecs[ins.Slots[0]][1] = cd.V[1];
                        p.Vecs[ins.Slots[0]][2] = cd.V[2];
                    }
                    break;
                }
            case 0x1A: case 0x63: // FlipbookTrail: ring-buffer the pose position
                {                      // whenever the phase counter rolls over
                    var st = p.Vecs[ins.Slots[0]];
                    float newPhase = st[2] - (p.T - p.PrevT);
                    if (newPhase < (int)st[2])
                    {
                        var ring = TrailStore(p, ins.Id, p.Pose[12], p.Pose[13], p.Pose[14]);
                        int idx = newPhase < 0 ? 30 : (int)newPhase % 31;
                        ring[idx][0] = p.Pose[12]; ring[idx][1] = p.Pose[13]; ring[idx][2] = p.Pose[14];
                    }
                    st[2] = (newPhase + 31) % 31;
                    break;
                }
            case 0x5C: // AttractingTarget: state = [minDist, repelDist, vecSlot]
                {
                    var cd = Curr(p.T, ins.Attracts); if (cd == null) break;
                    var st = p.Vecs[ins.Slots[1]];
                    st[0] = cd.MinDist; st[1] = cd.RepelDist; st[2] = ins.Slots[0];
                    break;
                }
            case 0x5D: // Attract: pull dst toward the target particle's vec
                {
                    var cd = Curr(p.T, ins.Attracts);
                    var e2 = p.Emit;
                    if (cd == null || cd.Target < 0 || e2 == null
                        || cd.Target >= e2.B.Programs.Count) break;
                    var target = cd.Target < e2.Parts.Length ? e2.Parts[cd.Target] : null;
                    if (target == null) break;
                    if (!e2.B.Programs[cd.Target].VecMap.TryGetValue(cd.Offset, out int ps)) break;
                    var prm = target.Vecs[ps];
                    int srcSlot = (int)prm[2];
                    if (srcSlot < 0 || srcSlot >= target.Vecs.Length) break;
                    var tv = target.Vecs[srcSlot];
                    var sv = p.Vecs[ins.Slots[0]];
                    float dx = tv[0] - sv[0], dy = tv[1] - sv[1], dz = tv[2] - sv[2];
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dist >= prm[0] || dist < 1e-6f) break;
                    float adt = p.T - p.PrevT;
                    float k = adt * prm[1] * (1 - dist / prm[0]) / dist;
                    var dv = p.Vecs[ins.Slots[1]];
                    dv[0] += dx * k; dv[1] += dy * k; dv[2] += dz * k;
                    break;
                }
            case 0x2E: // Pyrefly: integrate color accumulators + record pos ring
                {
                    var cd = Curr(p.T, ins.Pyreflys); if (cd == null || ins.Slots.Length < 7) break;
                    float pdt = p.T - p.PrevT;
                    var c0 = p.Vecs[ins.Slots[0]]; var c1 = p.Vecs[ins.Slots[1]];
                    var c2 = p.Vecs[ins.Slots[2]]; var c3 = p.Vecs[ins.Slots[3]];
                    var c4 = p.Vecs[ins.Slots[4]]; var c5 = p.Vecs[ins.Slots[5]];
                    for (int i = 0; i < 4; i++)
                    {
                        c0[i] += c2[i] * pdt; c1[i] += c4[i] * pdt;
                        c2[i] += c3[i] * pdt; c4[i] += c5[i] * pdt;
                    }
                    if (p.Crossed(cd.T))
                        for (int i = 0; i < 6; i++)
                            for (int c = 0; c < 4; c++) p.Vecs[ins.Slots[i]][c] += cd.Steps[i][c];
                    var st = p.Vecs[ins.Slots[6]];
                    var ring = TrailStore(p, ins.Id, p.Pose[12], p.Pose[13], p.Pose[14]); // 28 slots used of 31
                    float newPhase = st[1] - pdt;
                    if (newPhase < (float)Math.Floor(st[1]))
                    {
                        int idx = newPhase < 0 ? 27 : (int)newPhase % 28;
                        ring[idx][0] = p.Pose[12]; ring[idx][1] = p.Pose[13]; ring[idx][2] = p.Pose[14];
                    }
                    st[1] = (newPhase + 28) % 28;
                    break;
                }
            case 0x72: // ChildSetup: pos = parent.pose * pattern point[index]
                {
                    var cd = Curr(p.T, ins.ChildSetups);
                    if (cd == null || cd.Pattern < 0 || cd.Pattern >= sys.L.Patterns.Count
                        || p.Parent == null || ins.Slots.Length < 2) break;
                    var pat = sys.L.Patterns[cd.Pattern];
                    if (pat.GeoIndex >= sys.L.Geos.Count || pat.Indices.Length == 0) break;
                    var geo = sys.L.Geos[pat.GeoIndex];
                    int raw = (int)p.Vecs[ins.Slots[0]][0];
                    int pi = Math.Min(pat.Indices[Math.Clamp(raw, 0, pat.Indices.Length - 1)],
                        geo.Points.Length - 1);
                    var gp = geo.Points[pi];
                    var pp = p.Parent.Pose;
                    var dst = p.Vecs[ins.Slots[1]];
                    dst[0] = pp[0] * gp[0] + pp[4] * gp[1] + pp[8] * gp[2] + pp[12];
                    dst[1] = pp[1] * gp[0] + pp[5] * gp[1] + pp[9] * gp[2] + pp[13];
                    dst[2] = pp[2] * gp[0] + pp[6] * gp[1] + pp[10] * gp[2] + pp[14];
                    dst[3] = 1;
                    break;
                }
            case 0x3B: // ElectricTarget: state vec = target params + next
                {
                    var td = Curr(p.T, ins.ElecTargets); if (td == null || ins.Slots.Length < 1) break;
                    var st = p.Vecs[ins.Slots[0]];
                    st[0] = td.AttractCutoff; st[1] = td.MaxLerpDelta; st[2] = td.Threshold;
                    var e2 = p.Emit;
                    st[3] = (td.Next >= 0 && e2 != null && td.Next < e2.Parts.Length
                        && e2.Parts[td.Next] != null) ? td.Next : -1;
                    break;
                }
            case 0x3C: // ElectricColor: accumulate vel bursts on crossing,
                {      // colors integrate vels once per integer frame
                    var cd = Curr(p.T, ins.ElecColors); if (cd == null || ins.Slots.Length < 11) break;
                    if (p.Crossed(cd.T))
                    {
                        for (int i = 0; i < 4; i++)
                            for (int c = 0; c < 4; c++) p.Vecs[ins.Slots[1 + i]][c] += cd.Accs[i][c];
                        var prm = p.Vecs[ins.Slots[6]]; // params vec (state+1)
                        prm[0] += cd.WidthVel; prm[1] += cd.PerturbVel;
                        prm[3] += cd.TipAccel; prm[2] += cd.TargetVel;
                    }
                    if (p.PrevT < (float)Math.Floor(p.T))
                        for (int i = 0; i < 4; i++)
                        {
                            var col = p.Vecs[ins.Slots[7 + i]]; var vel = p.Vecs[ins.Slots[1 + i]];
                            for (int c = 0; c < 4; c++) col[c] += vel[c];
                        }
                    break;
                }
            case 0x3D: UpdateElectricity(ins, p); break;
            case 0x4D: case 0x4F: // DepthOffset: VIEW-space writes offset.z
                if (ins.DepthSpace == 1 && ins.Slots.Length > 0)   // into the
                    p.Vecs[ins.Slots[0]][0] = ins.DepthZ;        // matrix slot
                break;
            case 0x3A:
                p.Pose[12] = p.Vecs[ins.Slots[0]][0];
                p.Pose[13] = p.Vecs[ins.Slots[0]][1];
                p.Pose[14] = p.Vecs[ins.Slots[0]][2];
                break;
            case 0x10: case 0x6C: case 0x53: case 0x73:
                {
                    var pos = p.Vecs[ins.Slots[0]]; var eul = p.Vecs[ins.Slots[1]]; var scl = p.Vecs[ins.Slots[2]];
                    p.Pose = SRT(scl[0], scl[1], scl[2], eul[0] * ToRad, eul[1] * ToRad, eul[2] * ToRad, pos[0], pos[1], pos[2], ins.EulerOrder);
                    break;
                }
            case 0x11:
                {
                    var pos = p.Vecs[ins.Slots[0]]; var scl = p.Vecs[ins.Slots[1]];
                    p.Pose[0] = scl[0]; p.Pose[5] = scl[1]; p.Pose[10] = scl[2];
                    p.Pose[12] = pos[0]; p.Pose[13] = pos[1]; p.Pose[14] = pos[2];
                    break;
                }
            case 0x12:
                {
                    if (p.Parent == null) break;
                    var cd = Curr(p.T, ins.Enables); if (cd == null) break;
                    if (cd.Enabled)
                    {
                        var m = Mul(p.Parent.Pose, p.Pose);
                        p.Pose[12] = m[12]; p.Pose[13] = m[13]; p.Pose[14] = m[14];
                    }
                    break;
                }
            case 0x1B: case 0x1F: case 0x43: case 0x76: case 0x1C:
                {
                    var cd = Curr(p.T, ins.Emits); if (cd == null) break;
                    var sv = p.Vecs[ins.Slots[0]];
                    if (sv[1] <= 0 && sv[2] == 0 && cd.Count > 0)
                    {
                        sv[0] = EmitAtPositions(ins, p, sys, sv[0], cd);
                        sv[1] = cd.Period;
                        if (cd.Period == 0) sv[2] = 1;
                    }
                    sv[1] -= dt;
                    break;
                }
            case 0x74: case 0x29:
                {
                    var cd = Curr(p.T, ins.Emits); if (cd == null) break;
                    var sv = p.Vecs[ins.Slots[0]];
                    if (cd.Count <= 0 || cd.Program < 0) break;
                    if (sv[1] <= 0 && sv[2] == 0)
                    {
                        sv[0] = EmitAtPositions(ins, p, sys, sv[0], cd, simple: true);
                        sv[1] = cd.Period;
                        if (cd.Period == 0) sv[2] = 1;
                    }
                    sv[1] -= dt;
                    break;
                }
            case 0x3E: case 0x3F: case 0x46:
                {
                    // RandomEmit/DualEmit: each frame AND a random byte with the
                    // mask; emit on zero → probability dt/(1<<popcount). mask=0
                    // means every integer tick.
                    var cd = Curr(p.T, ins.Emits); if (cd == null) break;
                    var sv = p.Vecs[ins.Slots[0]];
                    bool emit;
                    if (cd.Mask != 0)
                    {
                        int bits = 0;
                        for (int i = 0; i < 8; i++) if ((cd.Mask & (1 << i)) != 0) bits++;
                        emit = Rng.NextDouble() < dt / (1 << bits);
                    }
                    else emit = p.PrevT < (float)(int)p.T;
                    if (emit) sv[0] = EmitAtPositions(ins, p, sys, sv[0], cd);
                    break;
                }
        }
    }

    static float EmitAtPositions(Instr ins, Particle parent, Sys sys, float lastPos, EmitData cd, bool simple = false)
    {
        var e = parent.Emit!;
        if (cd.Program < 0 || cd.Program >= e.B.Programs.Count) return lastPos;
        if (cd.Pattern < 0 || cd.Pattern >= sys.L.Patterns.Count) return lastPos;
        var pat = sys.L.Patterns[cd.Pattern];
        if (pat.GeoIndex >= sys.L.Geos.Count) return lastPos;
        var geo = sys.L.Geos[pat.GeoIndex];
        int indexCount = pat.Indices.Length;
        float curr = lastPos;
        var prog = e.B.Programs[cd.Program];
        // child vec offsets are remapped through the CHILD program's vecMap
        int posDest = cd.ChildPos >= 0 && prog.VecMap.TryGetValue(cd.ChildPos, out var ps) ? ps : -1;
        int dirDest = cd.ChildDir >= 0 && prog.VecMap.TryGetValue(cd.ChildDir, out var ds) ? ds : -1;
        int deltaDest = cd.ChildDelta >= 0 && prog.VecMap.TryGetValue(cd.ChildDelta, out var dd) ? dd : -1;
        int angleDest = cd.ChildAngle >= 0 && prog.VecMap.TryGetValue(cd.ChildAngle, out var da) ? da : -1;
        int emitCount = Math.Max(0, (int)Math.Round(cd.Count * sys.EmitMul));
        for (int i = 0; i < emitCount; i++)
        {
            var p = e.Emit(cd.Program);
            p.Parent = parent;
            if (cd.Random) curr = (int)(Rng.NextDouble() * indexCount);
            int pi = Math.Min(pat.Indices[(int)curr % indexCount], geo.Points.Length - 1);
            var pos = geo.Points[pi];
            float rx = pos[0], ry = pos[1], rz = pos[2];
            float x = rx, y = ry, z = rz;
            if (cd.Transform && !simple)
            {
                float nx = parent.Pose[0] * x + parent.Pose[4] * y + parent.Pose[8] * z + parent.Pose[12];
                float ny = parent.Pose[1] * x + parent.Pose[5] * y + parent.Pose[9] * z + parent.Pose[13];
                float nz = parent.Pose[2] * x + parent.Pose[6] * y + parent.Pose[10] * z + parent.Pose[14];
                x = nx; y = ny; z = nz;
            }
            if (ins.AtOrigin)
            {
                x -= e.Pose[12]; y -= e.Pose[13]; z -= e.Pose[14];
            }
            if (posDest >= 0)
            {
                p.Vecs[posDest][0] = x; p.Vecs[posDest][1] = y; p.Vecs[posDest][2] = z;
            }
            if (angleDest >= 0)
            {
                // noclip derives angles from the raw pattern point
                p.Vecs[angleDest][0] = (float)(Math.Atan2(rx, -ry) / ToRad);
                p.Vecs[angleDest][2] = (float)(Math.Atan2(-rz, Math.Sqrt(rx * rx + ry * ry)) / ToRad);
            }
            if (dirDest >= 0 && (ins.Op == 0x3E || ins.Op == 0x3F))
            {
                // RandomEmit: w0-rotated direction, normalized to cd.Scale
                float dx = parent.Pose[0] * rx + parent.Pose[4] * ry + parent.Pose[8] * rz;
                float dy = parent.Pose[1] * rx + parent.Pose[5] * ry + parent.Pose[9] * rz;
                float dz = parent.Pose[2] * rx + parent.Pose[6] * ry + parent.Pose[10] * rz;
                float dl = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dl > 1e-6f)
                {
                    float k = cd.Scale / dl;
                    p.Vecs[dirDest][0] = dx * k; p.Vecs[dirDest][1] = dy * k; p.Vecs[dirDest][2] = dz * k;
                }
            }
            if (deltaDest >= 0 && ins.Op == 0x46)
            {
                // DualEmit: pattern pos in parent space minus parent translation
                p.Vecs[deltaDest][0] = x - parent.Pose[12];
                p.Vecs[deltaDest][1] = y - parent.Pose[13];
                p.Vecs[deltaDest][2] = z - parent.Pose[14];
            }
            if (!cd.Random) curr = (curr + 1) % indexCount;
        }
        return curr;
    }

    // ---------- render ----------

    /// <summary>One emitted triangle list; v = stride-13 [pos3,rgba4,uv2,pad4].</summary>
        static void UpdateElectricity(Instr ins, Particle p)
        {
            var cd = Curr(p.T, ins.Electrics);
            if (System.Environment.GetEnvironmentVariable("FFX_DEBUG_ELEC") != null && !ElecDbgSeen.Contains(ins.Id))
            {
                ElecDbgSeen.Add(ins.Id);
                Console.Error.WriteLine($"elec dbg t={p.T} cv={ins.ChainVerts} slots={ins.Slots.Length} datums={(ins.Electrics?.Count ?? -1)} cd={(cd == null ? "null" : $"t={cd.T} disp={cd.TotalDisplacement} tipAcc={cd.TipAccel} wDelta={cd.WidthDelta} angle={cd.AngleRange}")}");
            }
            if (cd == null || ins.ChainVerts < 2 || ins.Slots.Length < 7 || p.Emit == null) return;
            var e = p.Emit;
            p.ElecBufs ??= new();
            if (!p.ElecBufs.TryGetValue(ins.Id, out var buf))
                p.ElecBufs[ins.Id] = buf = ChainBuf.Create(ins.ChainCount, ins.ChainVerts);
            int maxLen = ins.ChainVerts - 1;
            float stepSize = cd.TotalDisplacement / maxLen;
            float dt = p.T - p.PrevT;
            var prm = p.Vecs[ins.Slots[1]];
            float lengthInc = dt * prm[3];

            // pos = particle pose translation; dir = pose-rotated direction vec
            float px = p.Pose[12], py = p.Pose[13], pz = p.Pose[14];
            var dv = p.Vecs[ins.Slots[6]];
            float dx = p.Pose[0] * dv[0] + p.Pose[4] * dv[1] + p.Pose[8] * dv[2];
            float dy = p.Pose[1] * dv[0] + p.Pose[5] * dv[1] + p.Pose[9] * dv[2];
            float dz = p.Pose[2] * dv[0] + p.Pose[6] * dv[1] + p.Pose[10] * dv[2];
            float dl = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dl > 1e-6f) { dx /= dl; dy /= dl; dz /= dl; }

            if (p.Crossed(cd.T))
            {
                if (cd.T == 0)
                {
                    int idx = buf.Alloc();
                    if (idx >= 0)
                    {
                        int so = idx * 3, mo = idx * 6;
                        buf.Meta[mo + 0] = (byte)p.Vecs[ins.Slots[0]][1]; // prepend to list
                        p.Vecs[ins.Slots[0]][1] = idx;
                        buf.Meta[mo + 1] = 255; buf.Meta[mo + 2] = 0;
                        buf.Meta[mo + 3] = (byte)Math.Clamp(cd.TargetProgram, 0, 255);
                        buf.Meta[mo + 4] = 0; buf.Meta[mo + 5] = 0;
                        buf.St[so] = 0; buf.St[so + 2] = 0;
                        buf.St[so + 1] = ChainBuf.FLAG_ACTIVE | ChainBuf.FLAG_FIXED_TARGET
                            | (cd.InheritPos ? ChainBuf.FLAG_INHERIT_POS : 0)
                            | (cd.Detach ? ChainBuf.FLAG_DONE_AT_MAX : 0);
                        int vo = idx * buf.Stride;
                        buf.V[vo] = px; buf.V[vo + 1] = py; buf.V[vo + 2] = pz;
                        buf.V[vo + 3] = 255; buf.V[vo + 4] = dx; buf.V[vo + 5] = dy;
                        buf.V[vo + 6] = dz; buf.V[vo + 7] = 0;
                    }
                }
                var st0 = p.Vecs[ins.Slots[0]];
                for (int i = 0; i < 4; i++)
                {
                    var col = p.Vecs[ins.Slots[2 + i]];
                    var src = i == 0 ? cd.Core : i == 1 ? cd.Edge : i == 2 ? cd.CoreStep : cd.EdgeStep;
                    for (int c = 0; c < 4; c++) col[c] += src[c];
                }
                prm[0] += cd.WidthDelta; prm[1] += cd.PerturbDelta;
                prm[2] += cd.TargetStrengthDelta; prm[3] += cd.TipAccel;
                lengthInc += (1 + p.T - cd.T) * cd.TipAccel;
                _ = st0;
            }

            if (dt <= 0) return;
            bool updateExisting = (float)Math.Floor(p.T) > p.PrevT;
            int chain = (int)p.Vecs[ins.Slots[0]][1];
            int guard = 0;
            while (chain >= 0 && chain < buf.Chains && guard++ < buf.Chains)
            {
                int so = chain * 3, mo = chain * 6;
                int flagsChk = (int)buf.St[so + 1];
                if ((flagsChk & ChainBuf.FLAG_INHERIT_POS) != 0)
                {
                    int vo = chain * buf.Stride + buf.Meta[mo + 4] * 8;
                    buf.V[vo] = px; buf.V[vo + 1] = py; buf.V[vo + 2] = pz;
                    buf.V[vo + 4] = dx; buf.V[vo + 5] = dy; buf.V[vo + 6] = dz;
                }
                UpdateChain(ins, p, e, cd, buf, chain, lengthInc / stepSize, updateExisting, stepSize);
                chain = buf.Meta[mo];
            }
        }

        /// <summary>Port of Electricity.updateSingleChain (minus child-chain
        /// spawning): grows the ring window, lays new points along a
        /// target-seeking noisy direction, perturbs existing points on
        /// integer ticks.</summary>
        static void UpdateChain(Instr ins, Particle p, Emitter e, ElecData cd,
            ChainBuf buf, int chain, float lengthInc, bool updateExisting, float stepSize)
        {
            int so = chain * 3, mo = chain * 6, maxLen = ins.ChainVerts - 1;
            int flags = (int)buf.St[so + 1];
            float startLen = buf.St[so];
            float newLen = startLen + lengthInc;
            int newCount = (int)newLen - (int)startLen;
            int start = buf.Meta[mo + 4], end = buf.Meta[mo + 5];
            int target = buf.Meta[mo + 3];
            if ((flags & ChainBuf.FLAG_HIT_MAX) == 0 && newLen >= maxLen)
            {
                if ((flags & ChainBuf.FLAG_DONE_AT_MAX) != 0)
                    flags |= ChainBuf.FLAG_DONE_GROWING;
                if ((flags & ChainBuf.FLAG_DONE_GROWING) != 0)
                {
                    flags &= ~ChainBuf.FLAG_INHERIT_POS;
                    flags |= ChainBuf.FLAG_HIT_MAX;
                    start = (int)newLen - maxLen;
                    end = (start + ins.ChainVerts - 1) % ins.ChainVerts;
                }
                else
                {
                    newLen = maxLen;
                    end = (start + ins.ChainVerts - 1) % ins.ChainVerts;
                }
            }
            if ((flags & ChainBuf.FLAG_DONE_GROWING) == 0)
            {
                start = buf.Meta[mo + 4];
                if ((flags & ChainBuf.FLAG_FIXED_TARGET) != 0) target = cd.TargetProgram;
            }

            // target params vec from the target program's head particle
            float[] tgt = TargetParams(e, target);
            float[] tgtPos = tgt != null ? TargetPos(e, target) : null;
            float perturb = p.Vecs[ins.Slots[1]][1], tgtStr = p.Vecs[ins.Slots[1]][2];

            int count = start == end ? 0 :
                start < end ? end - start + 1 : maxLen - start + end + 2;
            int vtxI = start;
            float prevShrink = 0;
            bool inNew = false;
            float posX = 0, posY = 0, posZ = 0, dirX = 0, dirY = 0, dirZ = 0;
            for (int i = 0; i < count; i++, vtxI++)
            {
                if (vtxI == ins.ChainVerts) vtxI = 0;
                int vo = chain * buf.Stride + vtxI * 8;
                if (i == 0)
                {
                    posX = buf.V[vo]; posY = buf.V[vo + 1]; posZ = buf.V[vo + 2];
                    dirX = buf.V[vo + 4]; dirY = buf.V[vo + 5]; dirZ = buf.V[vo + 6];
                    if ((flags & ChainBuf.FLAG_FIXED_TARGET) != 0
                        && (flags & ChainBuf.FLAG_DONE_GROWING) == 0 && tgtPos != null)
                    {
                        ComputeDir(ref dirX, ref dirY, ref dirZ, tgtPos, posX, posY, posZ, tgt, tgtStr, cd);
                        dirX = dirX * perturb + buf.V[vo + 4] * (1 - perturb);
                        dirY = dirY * perturb + buf.V[vo + 5] * (1 - perturb);
                        dirZ = dirZ * perturb + buf.V[vo + 6] * (1 - perturb);
                        if (cd.NormalizeDir)
                        {
                            float l = (float)Math.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
                            if (l > 1e-6f) { dirX /= l; dirY /= l; dirZ /= l; }
                        }
                    }
                }
                else
                {
                    if (inNew || updateExisting)
                    {
                        buf.V[vo] = posX + dirX * stepSize;
                        buf.V[vo + 1] = posY + dirY * stepSize;
                        buf.V[vo + 2] = posZ + dirZ * stepSize;
                        ComputeDir(ref dirX, ref dirY, ref dirZ, tgtPos,
                            posX, posY, posZ, tgt, tgtStr, cd);
                        float newShrink = Math.Clamp(
                            prevShrink + cd.ShrinkStep * (2 * (float)Rng.NextDouble() - 1), 0, 1);
                        if (inNew)
                        {
                            buf.V[vo + 7] = newShrink; buf.V[vo + 3] = 255;
                            buf.V[vo + 4] = dirX; buf.V[vo + 5] = dirY; buf.V[vo + 6] = dirZ;
                        }
                        else
                        {
                            buf.V[vo + 7] += (newShrink - buf.V[vo + 7]) * perturb;
                            buf.V[vo + 4] += (dirX - buf.V[vo + 4]) * perturb;
                            buf.V[vo + 5] += (dirY - buf.V[vo + 5]) * perturb;
                            buf.V[vo + 6] += (dirZ - buf.V[vo + 6]) * perturb;
                        }
                    }
                    posX = buf.V[vo]; posY = buf.V[vo + 1]; posZ = buf.V[vo + 2];
                    dirX = buf.V[vo + 4]; dirY = buf.V[vo + 5]; dirZ = buf.V[vo + 6];
                }
                prevShrink = buf.V[vo + 7];

                if (tgtPos != null)
                {
                    float ddx = buf.V[vo] - tgtPos[0], ddy = buf.V[vo + 1] - tgtPos[1],
                        ddz = buf.V[vo + 2] - tgtPos[2];
                    if (ddx * ddx + ddy * ddy + ddz * ddz < tgt[2] * tgt[2])
                    {
                        int nxt = (int)tgt[3];
                        if (nxt < 0)
                        {
                            end = vtxI;
                            newLen -= count - i - 1;
                            break;
                        }
                        target = nxt;
                        tgt = TargetParams(e, target); tgtPos = TargetPos(e, target);
                        if (i == 0) buf.Meta[mo + 3] = (byte)Math.Clamp(target, 0, 255);
                        if (tgtPos == null) break;
                    }
                }
                if (vtxI == buf.Meta[mo + 5]) inNew = true;
            }
            buf.St[so] = newLen; buf.St[so + 1] = flags;
            buf.Meta[mo + 4] = (byte)Math.Clamp(start, 0, 255);
            buf.Meta[mo + 5] = (byte)Math.Clamp(end, 0, 255);
        }

        /// <summary>ElectricTarget state vec {cutoff, lerpDelta, threshold,
        /// next} of the target program's head particle (noclip
        /// updateTarget), or null.</summary>
        static float[]? TargetParams(Emitter e, int prog)
        {
            if (prog < 0 || prog >= e.B.Programs.Count || prog >= e.Parts.Length) return null;
            var tp = e.Parts[prog];
            if (tp == null) return null;
            foreach (var inst in e.B.Programs[prog].Instructions)
                if (inst.ElecTargets != null && inst.Slots.Length > 0)
                    return tp.Vecs[inst.Slots[0]];
            return null;
        }

        static float[]? TargetPos(Emitter e, int prog)
        {
            if (prog < 0 || prog >= e.Parts.Length) return null;
            var tp = e.Parts[prog];
            if (tp == null) return null;
            return new[] { tp.Pose[12], tp.Pose[13], tp.Pose[14] };
        }

        /// <summary>Electricity.computeDirection: bend the segment direction
        /// toward the target (strength scales up inside the attract cutoff),
        /// then add the per-step random rotation.</summary>
        static void ComputeDir(ref float dx, ref float dy, ref float dz, float[]? tgtPos,
            float px, float py, float pz, float[]? tgtPrm, float tgtStr, ElecData cd)
        {
            if (tgtPos != null && tgtPrm != null)
            {
                float vx = tgtPos[0] - px, vy = tgtPos[1] - py, vz = tgtPos[2] - pz;
                float dist = (float)Math.Sqrt(vx * vx + vy * vy + vz * vz);
                if (dist > 1e-6f) { vx /= dist; vy /= dist; vz /= dist; }
                float str = tgtStr;
                if (dist < tgtPrm[0])
                {
                    float t = (tgtPrm[0] - dist) / Math.Max(tgtPrm[0] - tgtPrm[2], 1e-6f);
                    str += tgtPrm[1] * Math.Clamp(t, 0, 1);
                }
                dx += (vx - dx) * str; dy += (vy - dy) * str; dz += (vz - dz) * str;
                if (cd.NormalizeDir)
                {
                    float l = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (l > 1e-6f) { dx /= l; dy /= l; dz /= l; }
                }
            }
            // extra π factor is deliberate — the original game over-scales
            float ang = cd.AngleRange * (float)(Math.PI / 180) * (float)Math.PI
                * (2 * (float)Rng.NextDouble() - 1);
            RotateAxis(ref dx, ref dy, ref dz, (int)(3 * Rng.NextDouble()), ang);
        }

        static void RotateAxis(ref float x, ref float y, ref float z, int axis, float ang)
        {
            float c = (float)Math.Cos(ang), sn = (float)Math.Sin(ang), t;
            switch (axis)
            {
                case 0: t = y * c - z * sn; z = y * sn + z * c; y = t; break;
                case 1: t = x * c + z * sn; z = -x * sn + z * c; x = t; break;
                default: t = x * c - y * sn; y = x * sn + y * c; x = t; break;
            }
        }

    public sealed class DrawItem
    {
        public float[] V = Array.Empty<float>();
        public Gs.Tex0? Tex;
        public bool Trans;
        /// <summary>GS blend byte: 0x00 replace, 0x42 subtract, 0x44/0x04
        /// alpha, 0x46 dst*(1-a), 0x48 additive, 0x88 src*a replace.</summary>
        public int Blend = 0x48;
        /// <summary>Glares draw without depth test (noclip depthCompare=Always).</summary>
        public bool NoDepth;
        /// <summary>Debug: which render path emitted this draw.</summary>
        public string Tag = "";
    }

    public sealed class Sys
    {
        public Layout L = new();
        public List<Emitter> Emitters = new();
        /// <summary>Camera world position for billboard ops
        /// (AxialBillboardMatrix etc.); null = identity fallback.</summary>
        public float[]? CamPos;
        /// <summary>Camera forward (unit, world space) — glare fade needs the
        /// view-space z of the particle position.</summary>
        public float[]? CamFwd;
        /// <summary>Editor overrides: emission count and particle/emitter
        /// lifetime multipliers (1 = faithful). JSON overlay, not binary.</summary>
        public float EmitMul = 1f, LifeMul = 1f;
        /// <summary>FFX_NO_GLARE env: skip glare draw ops (visual debug).</summary>
        static readonly bool DbgNoGlare = Environment.GetEnvironmentVariable("FFX_NO_GLARE") != null;
        /// <summary>FFX_NO_BLUR env: skip GeoBlur draw ops (visual debug).</summary>
        static readonly bool DbgNoBlur = Environment.GetEnvironmentVariable("FFX_NO_BLUR") != null;
        public float Time; // frames

        public static Sys FromBytes(byte[] d, int offs, int[]? funcMap = null, bool synthEmitters = false)
        {
            var s = new Sys { L = Load(d, offs, funcMap, synthEmitters) };
            for (int i = 0; i < s.L.Emitters.Count; i++)
            {
                var e = s.L.Emitters[i];
                if (e.Behavior < 0 || e.Behavior >= s.L.Behaviors.Count) continue;
                s.Emitters.Add(new Emitter(e, s.L.Behaviors[e.Behavior]));
            }
            if (synthEmitters)
            {
                // index specs by behavior id so op 0xDC can spawn by number
                var byB = new List<Particles.EmitterSpec?>(new Particles.EmitterSpec?[s.L.Behaviors.Count]);
                foreach (var e in s.Emitters)
                    if (e.Spec.Behavior >= 0 && e.Spec.Behavior < byB.Count)
                        byB[e.Spec.Behavior] = e.Spec;
                s.SpecByBehavior = byB!;
            }
            return s;
        }

        /// <summary>Emit script-triggered programs (sentinel start) at t=0 —
        /// preview mode renders every phase at once instead of waiting for
        /// the magic script to trigger them.</summary>
        public bool ScriptStartAll = true;

        /// <summary>Magic-program VM (actor.ts MonsterMagicManager port).
        /// When present, base emitters stay inert and the VM spawns them
        /// via op 0xDC with script-driven position/scale/heading.</summary>
        public MagicVm? Vm;
        /// <summary>Emitter spec per behavior index (synth layout) so the
        /// VM can spawn emitters by behavior.</summary>
        public List<Particles.EmitterSpec>? SpecByBehavior;
        bool _vmStarted;
        /// <summary>Set when the VM errored on every state it started —
        /// callers may fall back to ScriptStartAll.</summary>
        public bool VmFailed => Vm != null && Vm.States.Count == 0 && _vmStarted;

        /// <summary>Advance all emitters by dt frames and emit draw items.</summary>
        public void Step(float dt, List<DrawItem> outp)
        {
            Time += dt;
            if (Vm != null)
            {
                if (!_vmStarted) { _vmStarted = true; Vm.StartEffect(0); }
                Vm.Update(dt, outp);
                // scripts that never reach op 0xDC (external battle
                // triggers we can't satisfy) leave the preview empty —
                // fall back to running the base emitters
                if ((!Vm.Alive || Vm.StallFrames > 300) && !Vm.EverSpawned)
                {
                    foreach (var e in Emitters) e.Active = true;
                    Vm = null;
                }
            }
            foreach (var e in Emitters)
            {
                if (e.Dead || !e.Active) continue;
                StepEmitter(e, dt, outp);
            }
        }

        void StepEmitter(Emitter e, float dt, List<DrawItem> outp)
        {
            if (e.WaitTimer >= 0)
            {
                e.WaitTimer -= dt;
                if (e.WaitTimer >= 0) return;
                e.T = 0; e.PrevT = -1;
                for (int i = 0; i < e.Parts.Length; i++) e.Parts[i] = null;
            }
            // start programs whose time came; sentinel starts (>=0x80000,
            // raw high bit) are script-triggered — emit them at t=0 when the
            // preview flag is set so all phases are visible in the editor
            for (int i = 0; i < e.B.Programs.Count; i++)
            {
                var prog = e.B.Programs[i];
                if (e.PrevT < prog.Start && prog.Start <= e.T)
                    e.Emit(i);
                else if (ScriptStartAll && e.PrevT < 0 && prog.Start >= 0x80000)
                    e.Emit(i);
            }
            for (int i = 0; i < e.B.Programs.Count; i++)
            {
                var head = e.Parts[i];
                if (head == null) continue;
                var prog = e.B.Programs[i];
                bool singleFrame = prog.Lifetime == 1 && prog.LoopEnd > 1;
                for (var p = head; p != null; p = p.Next)
                    p.PrevRender = p.Render;
                foreach (var ins in prog.Instructions)
                {
                    if (ins.RenderAll) RenderAllInstr(ins, head, e, outp);
                    for (var p = head; p != null; p = p.Next)
                    {
                        if (!singleFrame || p.PrevT <= 0)
                            Update(ins, p, this);
                        if (p.Visible && !ins.RenderAll)
                            RenderInstr(ins, p, e, outp);
                    }
                }
                // advance + loop/kill
                Particle? prev = null;
                var pp = head;
                while (pp != null)
                {
                    pp.PrevT = pp.T;
                    pp.T += dt;
                    var next = pp.Next;
                    var nextPrev = pp;
                    if (e.State == EmitterState.Running && pp.T >= prog.LoopEnd && prog.LoopLength > 0)
                    {
                        pp.T -= prog.LoopLength;
                        pp.PrevT -= prog.LoopLength;
                        foreach (var ins in prog.Instructions) LoopInstr(ins, pp);
                    }
                    else if (pp.T >= prog.Lifetime * LifeMul)
                    {
                        if (prev == null) e.Parts[i] = pp.Next; else prev.Next = pp.Next;
                        pp.Next = null;
                        nextPrev = prev;
                    }
                    pp = next;
                    prev = nextPrev;
                }
            }
            e.PrevT = e.T;
            e.T += dt;
            bool done;
            if (e.B.IgnoreLifetime)
            {
                if (e.State == EmitterState.Running)
                {
                    if (e.T > EmitterMaxTime) { e.T = EmitterMaxTime; e.PrevT = e.T - dt; }
                    done = false;
                }
                else
                {
                    done = true;
                    for (int i = 0; i < e.Parts.Length; i++)
                        if (e.Parts[i] != null && e.B.Programs[i].Lifetime != EmitterMaxTime) done = false;
                }
            }
            else
            {
                done = e.T >= e.B.Lifetime * LifeMul;
                // magic/actor bins schedule programs past the behavior
                // lifetime — the script layer keeps such emitters alive, so
                // don't reap while a program start is still pending
                if (done)
                    for (int i = 0; i < e.B.Programs.Count; i++)
                        if (e.B.Programs[i].Start > e.T && e.B.Programs[i].Start < 0x80000)
                        { done = false; break; }
            }
            if (done) e.Dead = true;
        }

        /// <summary>Translation-only matrix at the particle's world position —
        /// pair with CamRight/CamUp billboarding (glares, trail points).</summary>
        static float[] WorldPosMat(Particle p, Emitter e)
        {
            var m = Identity();
            m[12] = e.Pose[0] * p.Pose[12] + e.Pose[4] * p.Pose[13] + e.Pose[8] * p.Pose[14] + e.Pose[12];
            m[13] = e.Pose[1] * p.Pose[12] + e.Pose[5] * p.Pose[13] + e.Pose[9] * p.Pose[14] + e.Pose[13];
            m[14] = e.Pose[2] * p.Pose[12] + e.Pose[6] * p.Pose[13] + e.Pose[10] * p.Pose[14] + e.Pose[14];
            return m;
        }

        // p.render composition per op (world-space variant of toView·pose)
        float[] RenderMat(Instr ins, Particle p, Emitter e)
        {
            switch (ins.Op)
            {
                case 0x14: case 0x34: // StandardMatrix: pose·emitterScale
                    {   // mat4.scale = COLUMN scaling: col_i *= scale[i]
                        var m = (float[])p.Pose.Clone();
                        var s = e.Spec.Scale;
                        m[0] *= (float)s[0]; m[1] *= (float)s[0]; m[2] *= (float)s[0];
                        m[4] *= (float)s[1]; m[5] *= (float)s[1]; m[6] *= (float)s[1];
                        m[8] *= (float)s[2]; m[9] *= (float)s[2]; m[10] *= (float)s[2];
                        return Mul(e.Pose, m);
                    }
                case 0x30: // AxialBillboardMatrix: cylindrical billboard —
                    {      // local +Z aims at the camera's XZ projection,
                           // local Y preserved (PriorityY | UseZPlane)
                        var m = (float[])p.Pose.Clone();
                        var s = e.Spec.Scale;
                        m[0] *= (float)s[0]; m[1] *= (float)s[0]; m[2] *= (float)s[0];
                        m[4] *= (float)s[1]; m[5] *= (float)s[1]; m[6] *= (float)s[1];
                        m[8] *= (float)s[2]; m[9] *= (float)s[2]; m[10] *= (float)s[2];
                        if (CamPos != null)
                        {
                            // camera in emitter space: R^T·(cam - ePos)
                            float dx = CamPos[0] - e.Pose[12], dy = CamPos[1] - e.Pose[13], dz = CamPos[2] - e.Pose[14];
                            float cx = e.Pose[0] * dx + e.Pose[1] * dy + e.Pose[2] * dz;
                            float cz = e.Pose[8] * dx + e.Pose[9] * dy + e.Pose[10] * dz;
                            float vx = cx - m[12], vz = cz - m[14];
                            float yaw = (float)Math.Atan2(vx, vz);
                            float c = (float)Math.Cos(yaw), sn = (float)Math.Sin(yaw);
                            float sx = (float)Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
                            float sz = (float)Math.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);
                            m[0] = c * sx; m[1] = 0; m[2] = -sn * sx;   // rotY col0
                            m[8] = sn * sz; m[9] = 0; m[10] = c * sz;   // rotY col2
                        }
                        return Mul(e.Pose, m);
                    }
                default: // ComposedMatrix: emitter.pose · p.pose
                    return Mul(e.Pose, p.Pose);
            }
        }

        void RenderInstr(Instr ins, Particle p, Emitter e, List<DrawItem> outp)
        {
            switch (ins.Op)
            {
                case 0x13: case 0x2C: case 0x6D: case 0x14: case 0x34: case 0x30:
                    p.Render = RenderMat(ins, p, e);
                    break;
                case 0x20: // GlareBase: state = [fade, factor, maxDist, screenDist]
                    {
                        var cd = Curr(p.T, ins.Glares); if (cd == null) break;
                        var st = p.Vecs[ins.Slots[0] + 1]; // state = translate+1
                        if (p.Crossed(cd.T)) { st[1] += cd.Factor; st[2] += cd.MaxDist; }
                        if (CamPos == null || CamFwd == null || st[2] <= 0) { st[0] = 0; break; }
                        float wx = e.Pose[0] * p.Pose[12] + e.Pose[4] * p.Pose[13] + e.Pose[8] * p.Pose[14] + e.Pose[12];
                        float wy = e.Pose[1] * p.Pose[12] + e.Pose[5] * p.Pose[13] + e.Pose[9] * p.Pose[14] + e.Pose[13];
                        float wz = e.Pose[2] * p.Pose[12] + e.Pose[6] * p.Pose[13] + e.Pose[10] * p.Pose[14] + e.Pose[14];
                        float vx = wx - CamPos[0], vy = wy - CamPos[1], vz = wz - CamPos[2];
                        float dist = (float)Math.Sqrt(vx * vx + vy * vy + vz * vz);
                        if (dist < 1e-6f) { st[0] = 0; break; }
                        float vz_ = (vx * CamFwd[0] + vy * CamFwd[1] + vz * CamFwd[2]) / dist;
                        st[3] = dist * (float)Math.Sqrt(Math.Max(0, 1 - vz_ * vz_));
                        float fade = 1 - Math.Clamp((1 + vz_) / .6f, 0, 1);
                        float maxD = st[2] - cd.DistReduction;
                        if (st[1] > 0)
                        {
                            float ax = p.Pose[12] * vx / dist + p.Pose[13] * vy / dist + p.Pose[14] * vz / dist;
                            fade *= 1 - Math.Clamp((1 - ax) / (2 * st[1]), 0, 1);
                        }
                        st[0] = maxD > 0 ? fade * (1 - Math.Clamp(dist / maxD, 0, 1)) : 0;
                        break;
                    }
                case 0x21: case 0x23: case 0x24: // SimpleGlare / MoreGlare / ScaledGlare
                    {
                        if (DbgNoGlare) break;
                        var cd = Curr(p.T, ins.Glares); if (cd == null || cd.Flipbook == 0xFFFF || cd.Flipbook >= L.Flipbooks.Count) break;
                        var fb = L.Flipbooks[cd.Flipbook];
                        var st = p.Vecs[ins.Slots[0]];
                        var bs = ins.Slots.Length > 1 ? p.Vecs[ins.Slots[1] + 1] : null; // glare base state
                        float sx, sy, aMul = bs != null ? bs[0] : 1;
                        if (ins.Op == 0x21) { sx = cd.Scale * (float)e.Spec.Scale[0]; sy = cd.Scale * (float)e.Spec.Scale[1]; }
                        else if (ins.Op == 0x23)
                        {
                            float ratio = bs != null && cd.ScaleDist != 0 ? cd.Scale * bs[3] / cd.ScaleDist : 0;
                            sx = -ratio * (float)e.Spec.Scale[0]; sy = ratio * (float)e.Spec.Scale[1];
                            if (bs != null && cd.AlphaDist != 0) aMul = Math.Clamp(bs[3] / cd.AlphaDist, 0, 1);
                        }
                        else
                        {
                            float ratio = bs != null && cd.MaxRadius != 0 ? 1 - Math.Clamp(bs[3] / cd.MaxRadius, 0, 1) : 1;
                            sx = cd.MaxScale * (float)e.Spec.Scale[0]; sy = cd.MaxScale * (float)e.Spec.Scale[1];
                            if (cd.XScaleMode == 1) sx *= ratio; else if (cd.XScaleMode == 2) sx *= 1 + ratio;
                            if (cd.YScaleMode == 1) sy *= ratio; else if (cd.YScaleMode == 2) sy *= 1 + ratio;
                        }
                        var col = new[] { cd.Color[0] * e.Color[0], cd.Color[1] * e.Color[1], cd.Color[2] * e.Color[2], cd.Color[3] * e.Color[3] * aMul };
                        RenderFlipbookScaled(fb, (int)st[0], col, WorldPosMat(p, e), sx, sy, outp, noDepth: true);
                        if (p.PrevT >= 0) UpdateFlipbook(st, fb, p.T - p.PrevT, 0x200);
                        break;
                    }
                case 0x1A: case 0x63: // FlipbookTrail: billboarded quads along the
                    {                  // position ring, tapered head -> tail
                        var cd = Curr(p.T, ins.Trails);
                        if (cd == null || cd.Flipbook == 0xFFFF || cd.Flipbook >= L.Flipbooks.Count) break;
                        var fb = L.Flipbooks[cd.Flipbook];
                        if (fb.Frames.Count == 0 || cd.TrailLength < 1) break;
                        var st = p.Vecs[ins.Slots[0]];
                        float aMul = ins.Slots.Length > 1 ? p.Vecs[ins.Slots[1]][3] / 16384f : 1f;
                        var head = new[] { cd.HeadColor[0] * e.Color[0], cd.HeadColor[1] * e.Color[1],
                            cd.HeadColor[2] * e.Color[2], cd.HeadColor[3] * e.Color[3] * aMul };
                        var tail = new[] { cd.TailColor[0] * e.Color[0], cd.TailColor[1] * e.Color[1],
                            cd.TailColor[2] * e.Color[2], cd.TailColor[3] * e.Color[3] * aMul };
                        var ring = TrailStore(p, ins.Id, p.Pose[12], p.Pose[13], p.Pose[14]);
                        int head_ = (int)st[2] % 31;
                        // noclip anchors the head at the last recorded ring
                        // sample, then walks the ring newest->oldest.
                        var pts = new List<float[]>();
                        for (int i = 0; i < 31; i++)
                            pts.Add(ring[(head_ + i) % 31]);
                        // walk the trail with the gap/taper logic (render.ts)
                        float seg = Dist3(pts[0], pts[1]);
                        float acc = seg;
                        int ti = 1;
                        for (int i = 0; i < cd.TrailLength; i++)
                        {
                            float frac = cd.TrailLength > 1 ? (float)i / (cd.TrailLength - 1) : 0;
                            float taper = cd.MaxScale + (cd.MinScale - cd.MaxScale) * frac;
                            float[] pos;
                            if (i == 0)
                            {
                                if (!cd.RenderHead) continue;
                                pos = pts[0];
                            }
                            else
                            {
                                float gap = cd.MaxScale != 0 ? cd.StartGap * taper / cd.MaxScale : 0;
                                while (acc < gap && ti < 30)
                                {
                                    ti++;
                                    seg = Dist3(pts[ti - 1], pts[ti]);
                                    acc += seg;
                                }
                                if (acc < gap || ti >= 31) break;
                                acc -= gap;
                                float u = seg > 1e-6f ? 1 - acc / seg : 1;
                                pos = new[] { pts[ti - 1][0] + (pts[ti][0] - pts[ti - 1][0]) * u,
                                    pts[ti - 1][1] + (pts[ti][1] - pts[ti - 1][1]) * u,
                                    pts[ti - 1][2] + (pts[ti][2] - pts[ti - 1][2]) * u };
                            }
                            var col = new[] { head[0] + (tail[0] - head[0]) * frac,
                                head[1] + (tail[1] - head[1]) * frac,
                                head[2] + (tail[2] - head[2]) * frac,
                                head[3] + (tail[3] - head[3]) * frac };
                            // ring stores emitter-local pos; to world via e.Pose.
                            // Quad basis = particle pose axes x emitter scale
                            // (noclip renderScratch) — trails lie in the pose
                            // plane, NOT billboarded to the camera.
                            float wx = e.Pose[0] * pos[0] + e.Pose[4] * pos[1] + e.Pose[8] * pos[2] + e.Pose[12];
                            float wy = e.Pose[1] * pos[0] + e.Pose[5] * pos[1] + e.Pose[9] * pos[2] + e.Pose[13];
                            float wz = e.Pose[2] * pos[0] + e.Pose[6] * pos[1] + e.Pose[10] * pos[2] + e.Pose[14];
                            var axl = new[] { p.Pose[0] * (float)e.Spec.Scale[0], p.Pose[1] * (float)e.Spec.Scale[0], p.Pose[2] * (float)e.Spec.Scale[0] };
                            var ayl = new[] { p.Pose[4] * (float)e.Spec.Scale[1], p.Pose[5] * (float)e.Spec.Scale[1], p.Pose[6] * (float)e.Spec.Scale[1] };
                            var axw = new[] { e.Pose[0] * axl[0] + e.Pose[4] * axl[1] + e.Pose[8] * axl[2],
                                e.Pose[1] * axl[0] + e.Pose[5] * axl[1] + e.Pose[9] * axl[2],
                                e.Pose[2] * axl[0] + e.Pose[6] * axl[1] + e.Pose[10] * axl[2] };
                            var ayw = new[] { e.Pose[0] * ayl[0] + e.Pose[4] * ayl[1] + e.Pose[8] * ayl[2],
                                e.Pose[1] * ayl[0] + e.Pose[5] * ayl[1] + e.Pose[9] * ayl[2],
                                e.Pose[2] * ayl[0] + e.Pose[6] * ayl[1] + e.Pose[10] * ayl[2] };
                            RenderFlipbookAxes(fb, (int)st[0], col, wx, wy, wz, axw, ayw, taper, taper, outp);
                        }
                        if (p.PrevT >= 0) UpdateFlipbook(st, fb, p.T - p.PrevT, cd.Speed);
                        break;
                    }
                case 0x32: // GeoBlur: geo + ghost at previous pose, alpha lerped
                    {      // by the blur accumulator (prev-frame smear approx)
                        if (DbgNoBlur) break;
                        var cd = Curr(p.T, ins.GeoBlurs); if (cd == null || cd.GeoIndex == 0xFFFF || cd.GeoIndex >= L.Geos.Count) break;
                        var st = p.Vecs[ins.Slots[0]];
                        float dt = p.T - p.PrevT;
                        st[1] += dt * st[2]; st[0] += dt * st[1];
                        if (p.Crossed(cd.T)) for (int i = 0; i < 4; i++) st[i] += cd.Inc[i];
                        // real impl samples the PREV-FRAME texture smeared along
                        // screen-space velocity (v_TexCoord = dir, not UV) — a
                        // feedback effect with no software-raster equivalent.
                        // Drawing the geo solid produces a giant opaque disc, so
                        // only the state accumulator is updated here.
                        break;
                    }
                case 0x15: case 0x37: case 0x50: case 0x5E:
                    {
                        var cd = Curr(p.T, ins.Geos); if (cd == null || cd.GeoIndex == 0xFFFF || cd.GeoIndex >= L.Geos.Count) break;
                        var col = GetColor(p, ins.Slots[0], e);
                        if (Dbg && ins.Slots.Length > 0)
                        {
                            var cv = p.Vecs[ins.Slots[0]];
                            Console.Error.WriteLine($"    geo draw op=0x{ins.Op:x2} slot={ins.Slots[0]} colvec=[{cv[0]:0} {cv[1]:0} {cv[2]:0} {cv[3]:0}] eColor=[{e.Color[0]:0.###} {e.Color[1]:0.###} {e.Color[2]:0.###} {e.Color[3]:0.###}] -> col[{col[0]:0.##} {col[1]:0.##} {col[2]:0.##} {col[3]:0.##}]");
                        }
                        RenderGeo(L.Geos[cd.GeoIndex], p.Render, col, 0, 0, outp, cd.Blend, "g"+ins.Op.ToString("x"));
                        break;
                    }
                case 0x17: case 0x47: case 0x4E: case 0x6E: case 0x78: case 0x79: case 0x71:
                    {
                        var cd = Curr(p.T, ins.Geos); if (cd == null || cd.GeoIndex == 0xFFFF || cd.GeoIndex >= L.Geos.Count) break;
                        var rm = p.Render;
                        if (ins.AtCamera && CamPos != null)
                        {
                            rm = (float[])p.Render.Clone();
                            rm[12] = CamPos[0]; rm[13] = CamPos[1]; rm[14] = CamPos[2];
                        }
                        // noclip accumulates scroll in render(): vel += accel*dt; pos += vel*dt,
                        // plus a one-shot uInc/vInc kick when crossing the datum's t
                        float u = 0, v = 0;
                        if (ins.Slots.Length > 2)
                        {
                            var uv = p.Vecs[ins.Slots[2]]; var vv = p.Vecs[ins.Slots[2] + 1];
                            float dt = p.T - p.PrevT;
                            uv[1] += dt * uv[2]; uv[0] += dt * uv[1];
                            vv[1] += dt * vv[2]; vv[0] += dt * vv[1];
                            if (p.Crossed(cd.T))
                            { for (int c = 0; c < 4; c++) { uv[c] += cd.UInc[c]; vv[c] += cd.VInc[c]; } }
                            u = uv[0]; v = vv[0];
                        }
                        var col = GetColor(p, ins.Slots[0], e);
                        RenderGeo(L.Geos[cd.GeoIndex], rm, col, u, v, outp, -1, "g"+ins.Op.ToString("x"));
                        break;
                    }
                case 0x58: // Water: scroll + wave phases + water tex frame (waves are shader-side)
                    {
                        var cd = Curr(p.T, ins.Geos); if (cd == null || cd.GeoIndex == 0xFFFF || cd.GeoIndex >= L.Geos.Count) break;
                        float u = 0, v = 0;
                        if (ins.Slots.Length > 5)
                        {
                            var uv = p.Vecs[ins.Slots[1]]; var vv = p.Vecs[ins.Slots[2]];
                            var xp = p.Vecs[ins.Slots[3]]; var dp = p.Vecs[ins.Slots[4]];
                            var tf = p.Vecs[ins.Slots[5]];
                            float dt = p.T - p.PrevT;
                            if (p.Crossed(cd.T))
                            { for (int c = 0; c < 4; c++) { uv[c] += cd.UInc[c]; vv[c] += cd.VInc[c]; } }
                            uv[1] += dt * uv[2]; uv[0] = (uv[0] + dt * uv[1]) % 0x8000;
                            vv[1] += dt * vv[2]; vv[0] = (vv[0] + dt * vv[1]) % 0x8000;
                            for (int c = 0; c < 4; c++) { xp[c] += cd.XSpeed[c] * dt; dp[c] += cd.DiagSpeed[c] * dt; }
                            tf[0] += dt / cd.WaterTexDur;
                            u = uv[0]; v = vv[0];
                        }
                        var col = GetColor(p, ins.Slots[0], e);
                        RenderGeo(L.Geos[cd.GeoIndex], p.Render, col, u, v, outp, -1, "water");
                        break;
                    }
                case 0x61:
                    RenderRain(ins, p, e, outp);
                    break;
                case 0x33: case 0x70: // WibbleUVScrollGeo: same scroll, wraps %0x8000,
                    {              // wibble vec accumulates (vertex wobble is shader-side, ~always 0)
                        var cd = Curr(p.T, ins.Geos); if (cd == null || cd.GeoIndex == 0xFFFF || cd.GeoIndex >= L.Geos.Count) break;
                        float u = 0, v = 0;
                        if (ins.Slots.Length > 4)
                        {
                            var uv = p.Vecs[ins.Slots[1]]; var vv = p.Vecs[ins.Slots[2]];
                            var wb = p.Vecs[ins.Slots[3]]; var tf = p.Vecs[ins.Slots[4]];
                            float dt = p.T - p.PrevT;
                            if (p.Crossed(cd.T))
                            { for (int c = 0; c < 4; c++) { uv[c] += cd.UInc[c]; vv[c] += cd.VInc[c]; } }
                            uv[1] += dt * uv[2]; uv[0] = (uv[0] + dt * uv[1]) % 0x8000;
                            vv[1] += dt * vv[2]; vv[0] = (vv[0] + dt * vv[1]) % 0x8000;
                            for (int c = 0; c < 4; c++) wb[c] += cd.WibbleVel[c] * dt;
                            tf[0] += dt / cd.WaterTexDur;
                            u = uv[0]; v = vv[0];
                        }
                        var col = GetColor(p, ins.Slots[0], e);
                        RenderGeo(L.Geos[cd.GeoIndex], p.Render, col, u, v, outp, -1, "wibble");
                        break;
                    }
                case 0x77:
                    {
                        // FlippedFlipbook: render matrix = pos/scale vecs ×
                        // emitter scale; random UV flips latched in state[2..3]
                        var cd = Curr(p.T, ins.Flips); if (cd == null || cd.Index == 0xFFFF || cd.Index >= L.Flipbooks.Count || ins.Slots.Length < 4) break;
                        var fb = L.Flipbooks[cd.Index];
                        if (fb.Frames.Count == 0) break;
                        var st = p.Vecs[ins.Slots[0]];
                        if (st[2] == 0)
                        {
                            st[2] = cd.FlipX ? Math.Sign((float)Rng.NextDouble() - .5f) : 1;
                            st[3] = cd.FlipY ? Math.Sign((float)Rng.NextDouble() - .5f) : 1;
                        }
                        var pos = p.Vecs[ins.Slots[1]];
                        var diag = p.Vecs[ins.Slots[2]];
                        // pos is emitter-local — world = e.Pose x pos
                        // (noclip: render = toView x local; same transform)
                        var m = (float[])Identity().Clone();
                        m[12] = e.Pose[0] * pos[0] + e.Pose[4] * pos[1] + e.Pose[8] * pos[2] + e.Pose[12];
                        m[13] = e.Pose[1] * pos[0] + e.Pose[5] * pos[1] + e.Pose[9] * pos[2] + e.Pose[13];
                        m[14] = e.Pose[2] * pos[0] + e.Pose[6] * pos[1] + e.Pose[10] * pos[2] + e.Pose[14];
                        var col = GetColor(p, ins.Slots[3], e);
                        RenderFlipbookScaled(fb, (int)st[0], col, m,
                            diag[0] * (float)e.Spec.Scale[0], diag[1] * (float)e.Spec.Scale[1], outp,
                            st[2] < 0, st[3] < 0);
                        if (p.PrevT >= 0) UpdateFlipbook(st, fb, p.T - p.PrevT, cd.Speed);
                        break;
                    }
                case 0x18: case 0x51:
                    {
                        var cd = Curr(p.T, ins.Flips); if (cd == null || cd.Index == 0xFFFF || cd.Index >= L.Flipbooks.Count) break;
                        var fb = L.Flipbooks[cd.Index];
                        if (fb.Frames.Count == 0) break;
                        var st = p.Vecs[ins.Slots[0]];
                        var col = GetColor(p, ins.Slots[1], e);
                        RenderFlipbook(fb, (int)st[0], col, p.Render, outp);
                        if (p.PrevT >= 0) UpdateFlipbook(st, fb, p.T - p.PrevT, cd.Speed);
                        break;
                    }
                case 0x31: case 0x66: case 0x7B: case 0x7F: case 0x83: case 0x85:
                    RenderCluster(ins, p, e, outp);
                    break;
                case 0x3D:
                    RenderElectricity(ins, p, e, outp);
                    break;
                case 0x1002: // FakeGeo: geo index comes from the magic script
                    if (ins.FakeGeoIndex >= 0 && ins.FakeGeoIndex < L.Geos.Count)
                        RenderGeo(L.Geos[ins.FakeGeoIndex], p.Render,
                            new[] { 1f, 1, 1, 1 }, 0, 0, outp);
                    break;
                case 0x2E:
                    RenderPyrefly(ins, p, e, outp);
                    break;
            }
        }

        /// <summary>Pyrefly render: flipbook sprites along the 28-point
        /// position ring, gap-walked like FlipbookTrail, with deterministic
        /// LCG size jitter and accumulated head/tail colors.</summary>
        void RenderPyrefly(Instr ins, Particle p, Emitter e, List<DrawItem> outp)
        {
            var cd = Curr(p.T, ins.Pyreflys);
            if (cd == null || ins.Slots.Length < 7 || cd.Flipbook == 0xFFFF
                || cd.Flipbook >= L.Flipbooks.Count || p.TrailBufs == null
                || !p.TrailBufs.TryGetValue(ins.Id, out var ring)) return;
            var fb = L.Flipbooks[cd.Flipbook];
            if (fb.Frames.Count == 0) return;
            var st = p.Vecs[ins.Slots[6]];
            var headC = GetColor(p, ins.Slots[0], e);
            var tailC = GetColor(p, ins.Slots[1], e);
            int head = (int)st[1] % 28;
            // current pos + ring (newest->oldest)
            var pts = new List<float[]> { new[] { p.Pose[12], p.Pose[13], p.Pose[14] } };
            for (int i = 1; i < 28; i++) pts.Add(ring[(head + i) % 28]);
            uint rng = (uint)st[2];
            float acc = Dist3(pts[0], pts[1]);
            int ti = 1;
            int frame = fb.Frames[0].Duration > 0
                ? (int)(st[0] / fb.Frames[0].Duration) % fb.Frames.Count : 0;
            for (int i = 0; i < cd.TrailLength; i++)
            {
                rng = (rng * 0x80D + 7) & 0xFFFF;
                float frac = cd.TrailLength > 1 ? (float)i / (cd.TrailLength - 1) : 0;
                float taper = cd.MaxScale + (cd.MinScale - cd.MaxScale) * frac;
                float[] pos;
                if (i == 0)
                {
                    if (!cd.RenderHead) continue;
                    pos = pts[0];
                }
                else
                {
                    float gap = cd.MaxScale != 0 ? cd.StartGap * taper / cd.MaxScale : 0;
                    while (acc < gap && ti < 27)
                    {
                        ti++;
                        acc += Dist3(pts[ti - 1], pts[ti]);
                    }
                    if (acc < gap || ti >= 28) break;
                    acc -= gap;
                    float segLen = Dist3(pts[ti - 1], pts[ti]);
                    float u = segLen > 1e-6f ? 1 - acc / segLen : 1;
                    pos = new[] { pts[ti - 1][0] + (pts[ti][0] - pts[ti - 1][0]) * u,
                        pts[ti - 1][1] + (pts[ti][1] - pts[ti - 1][1]) * u,
                        pts[ti - 1][2] + (pts[ti][2] - pts[ti - 1][2]) * u };
                }
                float rngFrac = rng / 65536f;
                float scale = taper * (1 - cd.SizeRange * rngFrac);
                var col = new[] { headC[0] + (tailC[0] - headC[0]) * frac,
                    headC[1] + (tailC[1] - headC[1]) * frac,
                    headC[2] + (tailC[2] - headC[2]) * frac,
                    headC[3] + (tailC[3] - headC[3]) * frac };
                // same basis as FlipbookTrail: pose axes x emitter scale in
                // world space (noclip renderScratch) — quads lie in the pose
                // plane, not billboarded to the camera.
                float wx = e.Pose[0] * pos[0] + e.Pose[4] * pos[1] + e.Pose[8] * pos[2] + e.Pose[12];
                float wy = e.Pose[1] * pos[0] + e.Pose[5] * pos[1] + e.Pose[9] * pos[2] + e.Pose[13];
                float wz = e.Pose[2] * pos[0] + e.Pose[6] * pos[1] + e.Pose[10] * pos[2] + e.Pose[14];
                var axl = new[] { p.Pose[0] * (float)e.Spec.Scale[0], p.Pose[1] * (float)e.Spec.Scale[0], p.Pose[2] * (float)e.Spec.Scale[0] };
                var ayl = new[] { p.Pose[4] * (float)e.Spec.Scale[1], p.Pose[5] * (float)e.Spec.Scale[1], p.Pose[6] * (float)e.Spec.Scale[1] };
                var axw = new[] { e.Pose[0] * axl[0] + e.Pose[4] * axl[1] + e.Pose[8] * axl[2],
                    e.Pose[1] * axl[0] + e.Pose[5] * axl[1] + e.Pose[9] * axl[2],
                    e.Pose[2] * axl[0] + e.Pose[6] * axl[1] + e.Pose[10] * axl[2] };
                var ayw = new[] { e.Pose[0] * ayl[0] + e.Pose[4] * ayl[1] + e.Pose[8] * ayl[2],
                    e.Pose[1] * ayl[0] + e.Pose[5] * ayl[1] + e.Pose[9] * ayl[2],
                    e.Pose[2] * ayl[0] + e.Pose[6] * ayl[1] + e.Pose[10] * ayl[2] };
                RenderFlipbookAxes(fb, frame, col, wx, wy, wz, axw, ayw, scale, scale, outp);
            }
            st[0] += cd.Speed * (p.T - p.PrevT);
        }

        /// <summary>Lightning render: each live chain becomes a camera-facing
        /// ribbon strip — outer edge-colored quad strip plus a narrower
        /// core-colored strip (coreWidthFrac). Width tapers by endWidthFrac
        /// and per-point shrink; colors are 15-bit accumulators stepped per
        /// point (noclip fillBuffer).</summary>
        void RenderElectricity(Instr ins, Particle p, Emitter e, List<DrawItem> outp)
        {
            if (ins.ChainVerts < 2 || ins.Slots.Length < 7 || p.ElecBufs == null
                || !p.ElecBufs.TryGetValue(ins.Id, out var buf)) return;
            var cd = Curr(p.T, ins.Electrics); if (cd == null) return;
            // width: params[0] accumulates widthDelta per frame (noclip).
            // noclip applies it post-projection in NDC units; world-space
            // approximation below keeps the strip thin at all distances.
            float baseWidth = p.Vecs[ins.Slots[1]][0] * (float)e.Spec.Scale[0] / 0x200f;
            if (baseWidth <= 0) return;
            if (Dbg) Console.Error.WriteLine($"    elec vec0={p.Vecs[ins.Slots[1]][0]:0.#} scale={e.Spec.Scale[0]:0.##} baseWidth={baseWidth:0.###}");
            int blend = cd.BlendMode != 0 ? cd.BlendMode : 0x48;
            // view direction = camRight × camUp (billboard basis is set per-frame)
            float vx = CamRight[1] * CamUp[2] - CamRight[2] * CamUp[1];
            float vy = CamRight[2] * CamUp[0] - CamRight[0] * CamUp[2];
            float vz = CamRight[0] * CamUp[1] - CamRight[1] * CamUp[0];
            int chain = (int)p.Vecs[ins.Slots[0]][1];
            int guard = 0;
            while (chain >= 0 && chain < buf.Chains && guard++ < buf.Chains)
            {
                int mo = chain * 6;
                int start = buf.Meta[mo + 4], end = buf.Meta[mo + 5];
                int count = end >= start ? end - start + 1 : end + ins.ChainVerts - start + 1;
                if (count > 2)
                {
                    var pts = new float[count][];
                    int idx = cd.Reversed ? end : start, step = cd.Reversed ? -1 : 1;
                    for (int i = 0; i < count; i++)
                    {
                        int vo = chain * buf.Stride + idx * 8;
                        float lx = buf.V[vo], ly = buf.V[vo + 1], lz = buf.V[vo + 2];
                        pts[i] = new[]
                        {
                            e.Pose[0] * lx + e.Pose[4] * ly + e.Pose[8] * lz + e.Pose[12],
                            e.Pose[1] * lx + e.Pose[5] * ly + e.Pose[9] * lz + e.Pose[13],
                            e.Pose[2] * lx + e.Pose[6] * ly + e.Pose[10] * lz + e.Pose[14],
                            buf.V[vo + 7],
                        };
                        idx += step;
                        if (idx < 0) idx = ins.ChainVerts - 1;
                        if (idx == ins.ChainVerts) idx = 0;
                    }
                    EmitStrip(pts, p.Vecs[ins.Slots[3]], p.Vecs[ins.Slots[5]],
                        ins.ChainVerts, baseWidth, cd.EndWidthFrac, cd.ShrinkFrac,
                        1f, vx, vy, vz, outp, blend);
                    if (cd.CoreWidthFrac > 0)
                        EmitStrip(pts, p.Vecs[ins.Slots[2]], p.Vecs[ins.Slots[4]],
                            ins.ChainVerts, baseWidth, cd.EndWidthFrac, cd.ShrinkFrac,
                            cd.CoreWidthFrac, vx, vy, vz, outp, blend);
                }
                chain = buf.Meta[mo];
            }
        }

        static void EmitStrip(float[][] pts, float[] col0, float[] colStep,
            int vtxCount, float baseWidth, float endWidthFrac, float shrinkFrac,
            float widthMul, float vx, float vy, float vz, List<DrawItem> outp, int blend)
        {
            int n = pts.Length;
            var tv = new float[13 * 6 * (n - 1)];
            float plx = 0, ply = 0, plz = 0, prx = 0, pry = 0, prz = 0;
            float[] prevCol = null;
            int w = 0;
            for (int i = 0; i < n; i++)
            {
                // perpendicular to (view × segment dir) — screen-aligned ribbon
                float dx, dy, dz;
                if (i + 1 < n)
                {
                    dx = pts[i + 1][0] - pts[i][0]; dy = pts[i + 1][1] - pts[i][1]; dz = pts[i + 1][2] - pts[i][2];
                }
                else
                {
                    dx = pts[i][0] - pts[i - 1][0]; dy = pts[i][1] - pts[i - 1][1]; dz = pts[i][2] - pts[i - 1][2];
                }
                float px = dy * vz - dz * vy, py = dz * vx - dx * vz, pz = dx * vy - dy * vx;
                float pl = (float)Math.Sqrt(px * px + py * py + pz * pz);
                float wid = (1 + (endWidthFrac - 1) * i / (vtxCount - 1)) * baseWidth
                    * (1 - pts[i][3] * shrinkFrac) * widthMul;
                if (pl > 1e-6f) { px = px / pl * wid; py = py / pl * wid; pz = pz / pl * wid; }
                else { px = py = pz = 0; }
                var col = new float[4];
                for (int c = 0; c < 4; c++)
                    col[c] = Math.Max(0, col0[c] + colStep[c] * i) / 0x8000f;
                if (i > 0)
                {
                    // quad: prevL prevR curL | prevR curR curL
                    float[][] q =
                    {
                        new[]{ plx, ply, plz }, new[]{ prx, pry, prz },
                        new[]{ pts[i][0] - px, pts[i][1] - py, pts[i][2] - pz },
                        new[]{ prx, pry, prz },
                        new[]{ pts[i][0] + px, pts[i][1] + py, pts[i][2] + pz },
                        new[]{ pts[i][0] - px, pts[i][1] - py, pts[i][2] - pz },
                    };
                    float[][] qc = { prevCol, prevCol, col, prevCol, col, col };
                    for (int j = 0; j < 6; j++, w += 13)
                    {
                        tv[w] = q[j][0]; tv[w + 1] = q[j][1]; tv[w + 2] = q[j][2];
                        tv[w + 3] = qc[j][0]; tv[w + 4] = qc[j][1];
                        tv[w + 5] = qc[j][2]; tv[w + 6] = qc[j][3];
                    }
                }
                plx = pts[i][0] - px; ply = pts[i][1] - py; plz = pts[i][2] - pz;
                prx = pts[i][0] + px; pry = pts[i][1] + py; prz = pts[i][2] + pz;
                prevCol = col;
            }
            outp.Add(new DrawItem { V = tv, Trans = true, Blend = blend, Tag = "elec" });
        }

        /// <summary>Rain (0x61): wraparound box of streak drops around the
        /// camera. Each drop = a quad from pos to pos+len*velDir with a
        /// ~pixel-constant width (noclip draws screen-space lines).</summary>
        void RenderRain(Instr ins, Particle p, Emitter e, List<DrawItem> outp)
        {
            var cd = Curr(p.T, ins.Rains);
            if (cd == null || cd.Count <= 0 || cd.Range <= 0 || ins.Slots.Length < 1) return;
            var bufs = p.RainBufs ??= new();
            if (!bufs.TryGetValue(ins.Id, out var sc))
                bufs[ins.Id] = sc = new float[cd.Count * 8];
            float cx = CamPos?[0] ?? 0, cy = CamPos?[1] ?? 0, cz = CamPos?[2] ?? 0;
            float dt = p.T - p.PrevT;
            for (int i = 0; i < cd.Count; i++)
            {
                int po = i * 4, vo = (cd.Count + i) * 4;
                int bad = -1;
                for (int c = 0; c < 3; c++)
                {
                    float cam = c == 0 ? cx : c == 1 ? cy : cz;
                    if (sc[po + c] > cam + cd.Range) { bad = c; sc[po + c] = cam - cd.Range + 1; break; }
                    if (sc[po + c] < cam - cd.Range) { bad = c; sc[po + c] = cam + cd.Range - 1; break; }
                }
                if (bad < 0 && sc[po + 3] > 0)
                {
                    sc[po] += sc[vo] * dt; sc[po + 1] += sc[vo + 1] * dt; sc[po + 2] += sc[vo + 2] * dt;
                }
                else
                {
                    for (int c = 0; c < 3; c++)
                    {
                        if (c == bad) continue;
                        float cam = c == 0 ? cx : c == 1 ? cy : cz;
                        sc[po + c] = cam + (float)(Rng.NextDouble() * 2 - 1) * cd.Range;
                    }
                    sc[po + 3] = cd.BaseLength + (float)(Rng.NextDouble() * 2 - 1) * cd.LengthRange;
                    for (int c = 0; c < 4; c++)
                        sc[vo + c] = cd.Vel[c] + (float)(Rng.NextDouble() * 2 - 1) * cd.VelRange[c];
                }
            }
            // streaks run along the datum's base direction
            float dx = cd.Vel[0], dy = cd.Vel[1], dz = cd.Vel[2];
            float dl = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dl < 1e-6f) return;
            dx /= dl; dy /= dl; dz /= dl;
            float fx = CamFwd?[0] ?? 0, fy = CamFwd?[1] ?? 0, fz = CamFwd?[2] ?? 1;
            // quad perpendicular to the streak axis and the view direction
            float px = dy * fz - dz * fy, py = dz * fx - dx * fz, pz = dx * fy - dy * fx;
            float pl = (float)Math.Sqrt(px * px + py * py + pz * pz);
            if (pl < 1e-6f) { px = CamRight[0]; py = CamRight[1]; pz = CamRight[2]; }
            else { px /= pl; py /= pl; pz /= pl; }
            var col = GetColor(p, ins.Slots[0], e);
            for (int i = 0; i < cd.Count; i++)
            {
                int po = i * 4;
                float len = sc[po + 3];
                float bx = sc[po], by = sc[po + 1], bz = sc[po + 2];
                float tx = bx + dx * len, ty = by + dy * len, tz = bz + dz * len;
                float ddx = bx - cx, ddy = by - cy, ddz = bz - cz;
                float w = 0.001f * (float)Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                var tv = new float[13 * 6];
                int[] order = { 0, 1, 2, 2, 1, 3 };
                for (int j = 0; j < 6; j++)
                {
                    int k = order[j]; int o = 13 * j;
                    float ex = (k & 1) != 0 ? tx : bx, ey = (k & 1) != 0 ? ty : by, ez = (k & 1) != 0 ? tz : bz;
                    float sg = (k & 2) != 0 ? -w : w;
                    tv[o] = ex + px * sg; tv[o + 1] = ey + py * sg; tv[o + 2] = ez + pz * sg;
                    tv[o + 3] = col[0] * .5f; tv[o + 4] = col[1] * .5f;
                    tv[o + 5] = col[2] * .5f; tv[o + 6] = col[3];
                }
                outp.Add(new DrawItem { V = tv, Trans = true, Blend = 0x44, Tag = "rain" });
            }
        }

        void RenderAllInstr(Instr ins, Particle head, Emitter e, List<DrawItem> outp)
        {
            // SimpleFlipbook renderAll: iterate whole chain once (render per-particle is skipped)
            switch (ins.Op)
            {
                case 0x18: case 0x51: case 0x77:
                    {
                        var cd = Curr(head.T, ins.Flips); if (cd == null || cd.Index == 0xFFFF || cd.Index >= L.Flipbooks.Count) break;
                        var fb = L.Flipbooks[cd.Index];
                        if (fb.Frames.Count == 0) break;
                        for (var p = head; p != null; p = p.Next)
                        {
                            if (!p.Visible) continue;
                            var st = p.Vecs[ins.Slots[0]];
                            var col = GetColor(p, ins.Slots[1], e);
                            RenderFlipbook(fb, (int)st[0], col, p.Render, outp);
                            if (p.PrevT >= 0) UpdateFlipbook(st, fb, p.T - p.PrevT, cd.Speed);
                        }
                        break;
                    }
            }
        }

        static float[] GetColor(Particle p, int slot, Emitter e)
        {
            if (slot < 0 || slot >= VecCount) return new[] { 1f, 1, 1, 1 };
            var v = p.Vecs[slot];
            return new[] { v[0] * e.Color[0] / 16384f, v[1] * e.Color[1] / 16384f, v[2] * e.Color[2] / 16384f, v[3] * e.Color[3] / 16384f };
        }

        void RenderGeo(Geo g, float[] m, float[] col, float uOff, float vOff, List<DrawItem> outp, int blendOverride = -1, string tag = "pattern")
        {
            if (g.Verts.Length == 0) return;
            // blend: datum override, else the geo's own blendSettings (render.ts)
            int blend = blendOverride >= 0 ? blendOverride : (g.BlendSettings & 0xFF);
            foreach (var pr in g.Prims)
            {
                int vpp = pr.Indices.Length;
                int triN = vpp == 3 ? 1 : 2;
                var v = new float[13 * vpp];
                for (int j = 0; j < vpp; j++)
                {
                    int vi = pr.Indices[j];
                    if (vi >= g.Verts.Length) vi = g.Verts.Length - 1;
                    var sv = g.Verts[vi];
                    int o = 13 * j;
                    v[o] = m[0] * sv[0] + m[4] * sv[1] + m[8] * sv[2] + m[12];
                    v[o + 1] = m[1] * sv[0] + m[5] * sv[1] + m[9] * sv[2] + m[13];
                    v[o + 2] = m[2] * sv[0] + m[6] * sv[1] + m[10] * sv[2] + m[14];
                    for (int c = 0; c < 4; c++) v[o + 3 + c] = pr.Colors[j][c] * col[c];
                    v[o + 7] = pr.Uvs[j][0] + uOff; v[o + 8] = pr.Uvs[j][1] + vOff;
                }
                // emit as triangle list: prim (0,2,1) + (1,2,3) for quads — same winding as noclip
                int[] order = vpp == 3 ? new[] { 0, 2, 1 } : new[] { 0, 2, 1, 1, 2, 3 };
                var tv = new float[13 * order.Length];
                for (int j = 0; j < order.Length; j++)
                    Array.Copy(v, 13 * order[j], tv, 13 * j, 13);
                outp.Add(new DrawItem { V = tv, Tex = pr.Tex0, Trans = true, Blend = blend, Tag = tag });
            }
        }

        internal void RenderFlipbook(Flipbook fb, int frameIdx, float[] col, float[] m, List<DrawItem> outp)
        {
            if (fb.Frames.Count == 0) return;
            var fr = fb.Frames[Math.Clamp(frameIdx, 0, fb.Frames.Count - 1)];
            float tx = m[12], ty = m[13], tz = m[14];
            // quad axes = the render matrix's own X/Y columns — carries pose
            // rotation AND particle/emitter scale (noclip: verts go through
            // the full modelMatrix, not a camera billboard)
            float xx = m[0], xy = m[1], xz = m[2];
            float yx = m[4], yy = m[5], yz = m[6];
            foreach (var r in fr.Rects)
            {
                float[] xs, ys;
                if (r.TriX != null) { xs = r.TriX; ys = r.TriY!; }
                else { xs = new[] { r.X0, r.X1, r.X0, r.X1 }; ys = new[] { r.Y0, r.Y0, r.Y1, r.Y1 }; }
                float[] us = { r.U0, r.U1, r.U0, r.U1 }, vs = { r.V0, r.V0, r.V1, r.V1 };
                int[] order = { 0, 1, 2, 2, 1, 3 };
                var tv = new float[13 * 6];
                for (int j = 0; j < 6; j++)
                {
                    int k = order[j];
                    int o = 13 * j;
                    tv[o] = tx + xx * xs[k] + yx * ys[k];
                    tv[o + 1] = ty + xy * xs[k] + yy * ys[k];
                    tv[o + 2] = tz + xz * xs[k] + yz * ys[k];
                    tv[o + 3] = r.R * col[0]; tv[o + 4] = r.G * col[1];
                    tv[o + 5] = r.B * col[2]; tv[o + 6] = r.A * col[3];
                    tv[o + 7] = us[k]; tv[o + 8] = vs[k];
                }
                outp.Add(new DrawItem { V = tv, Tex = r.Tex0, Trans = true, Blend = r.Blend, Tag = "flipbook" });
            }
        }

        /// <summary>Camera billboard basis (world space) — set before Step().</summary>
        public float[] CamRight = { 1, 0, 0 };
        public float[] CamUp = { 0, 1, 0 };

        sealed class ClusterChild
        {
            public float StartT = -1;
            public float[] Pos = new float[4], Vel = new float[4], Scale = new float[4];
            public float Roll; public bool MirrorX, MirrorY;
        }

        void RenderCluster(Instr ins, Particle p, Emitter e, List<DrawItem> outp)
        {
            var cd = Curr(p.T, ins.Clusters);
            if (cd == null || cd.Index >= L.Flipbooks.Count) return;
            var fb = L.Flipbooks[cd.Index];
            if (fb.Frames.Count == 0) return;
            var sv = p.Vecs[ins.Slots[0]];
            var posV = p.Vecs[ins.Slots[2]];
            // children live in a per-particle scratch: lazily attach to Particle
            var children = ClusterStore(p, cd.ChildCount);
            float dt = p.T - p.PrevT;
            if (sv[1] <= 0 && e.State == EmitterState.Running)
            {
                sv[1] = cd.ResetInterval;
                for (int i = 0; i < cd.ResetCount; i++)
                {
                    int slot = (int)sv[2];
                    var ch = children[slot];
                    ch.StartT = Time;
                    Array.Clear(ch.Pos, 0, 4);
                    for (int a = 0; a < 3; a++) ch.Vel[a] = cd.Vel[a] + cd.VelRange[a] * (float)(Rng.NextDouble() * 2 - 1);
                    for (int a = 0; a < 3; a++)
                        ch.Pos[a] += cd.OffsetRange[a] * (cd.OffsedPeaked ? (float)(Rng.NextDouble() * (Rng.NextDouble() * 2 - 1)) : (float)(Rng.NextDouble() * 2 - 1));
                    for (int a = 0; a < 3; a++) ch.Scale[a] = cd.Scale[a] + cd.ScaleRange[a] * (float)(2 * Rng.NextDouble() - 1);
                    ch.Roll = cd.RollRange * (float)(Rng.NextDouble() * 2 - 1) * (float)(Math.PI / 180);
                    ch.MirrorX = (cd.MirrorFlags & 2) != 0 && Rng.NextDouble() > .5;
                    ch.MirrorY = (cd.MirrorFlags & 1) != 0 && Rng.NextDouble() > .5;
                    sv[2]++;
                    if (sv[2] == cd.ChildCount) sv[2] = 0;
                }
            }
            sv[1] -= dt;
            var colS = p.Vecs[ins.Slots[1]];
            // noclip post-scales the render matrix by the emitter's visual
            // scale in view space: len(cameraBasis.axis * emitter.scale)
            float esx = (float)e.Spec.Scale[0], esy = (float)e.Spec.Scale[1], esz = (float)e.Spec.Scale[2];
            float xScale = (float)Math.Sqrt(CamRight[0] * esx * (CamRight[0] * esx) + CamRight[1] * esy * (CamRight[1] * esy) + CamRight[2] * esz * (CamRight[2] * esz));
            float yScale = (float)Math.Sqrt(CamUp[0] * esx * (CamUp[0] * esx) + CamUp[1] * esy * (CamUp[1] * esy) + CamUp[2] * esz * (CamUp[2] * esz));
            int colorCount = cd.ChildCount * 4;
            bool living = false;
            for (int i = 0; i < cd.ChildCount; i++)
            {
                var ch = children[i];
                float t = Time - ch.StartT;
                if (ch.StartT < 0 || t >= Math.Max(1, colorCount)) continue;
                living = true;
                float px = ch.Pos[0] + ch.Vel[0] * t + cd.Accel[0] * t * t / 2 + posV[0];
                float py = ch.Pos[1] + ch.Vel[1] * t + cd.Accel[1] * t * t / 2 + posV[1];
                float pz = ch.Pos[2] + ch.Vel[2] * t + cd.Accel[2] * t * t / 2 + posV[2];
                float sx = ch.Scale[0] + cd.ScaleVel[0] * t + cd.ScaleAccel[0] * t * t / 2;
                float sy = ch.Scale[1] + cd.ScaleVel[1] * t + cd.ScaleAccel[1] * t * t / 2;
                if (ch.MirrorX) sx = -sx; if (ch.MirrorY) sy = -sy;
                int fIdx = FlipbookFrameAtT(fb, t, cd.Speed);
                // particle color at integer t (approximate: current color vec)
                var col = new[] { colS[0] / 16384f, colS[1] / 16384f, colS[2] / 16384f, colS[3] / 16384f };
                // billboard at world pos (emitter.pose applied)
                float wx = e.Pose[0] * px + e.Pose[4] * py + e.Pose[8] * pz + e.Pose[12];
                float wy = e.Pose[1] * px + e.Pose[5] * py + e.Pose[9] * pz + e.Pose[13];
                float wz = e.Pose[2] * px + e.Pose[6] * py + e.Pose[10] * pz + e.Pose[14];
                var m = Identity();
                m[12] = wx; m[13] = wy; m[14] = wz;
                RenderFlipbookScaled(fb, fIdx, col, m, sx * xScale, sy * yScale, outp);
            }
            // approximation: particle dies on schedule even if children live
            // (noclip holds it at lifetime-1 until children finish)
        }

        void RenderFlipbookScaled(Flipbook fb, int frameIdx, float[] col, float[] m, float sx, float sy, List<DrawItem> outp, bool flipX = false, bool flipY = false, bool noDepth = false)
            => RenderFlipbookAxes(fb, frameIdx, col, m[12], m[13], m[14], CamRight, CamUp, sx, sy, outp, flipX, flipY, noDepth);

        /// <summary>Draws the flipbook quad at (px,py,pz), spanned by the
        /// ax/ay basis vectors (world space). Trails pass the particle pose
        /// axes x emitter scale (noclip renderScratch); billboards pass
        /// CamRight/CamUp.</summary>
        void RenderFlipbookAxes(Flipbook fb, int frameIdx, float[] col, float px, float py, float pz,
            float[] ax, float[] ay, float sx, float sy, List<DrawItem> outp, bool flipX = false, bool flipY = false, bool noDepth = false)
        {
            var fr = fb.Frames[Math.Clamp(frameIdx, 0, fb.Frames.Count - 1)];
            foreach (var r in fr.Rects)
            {
                float[] xs, ys;
                if (r.TriX != null) { xs = r.TriX; ys = r.TriY!; }
                else { xs = new[] { r.X0, r.X1, r.X0, r.X1 }; ys = new[] { r.Y0, r.Y0, r.Y1, r.Y1 }; }
                float[] us = { r.U0, r.U1, r.U0, r.U1 }, vs = { r.V0, r.V0, r.V1, r.V1 };
                if (flipX) { us[0] = r.U1; us[1] = r.U0; us[2] = r.U1; us[3] = r.U0; }
                if (flipY) { vs[0] = r.V1; vs[1] = r.V1; vs[2] = r.V0; vs[3] = r.V0; }
                int[] order = { 0, 1, 2, 2, 1, 3 };
                var tv = new float[13 * 6];
                for (int j = 0; j < 6; j++)
                {
                    int k = order[j];
                    int o = 13 * j;
                    float lx = xs[k] * sx, ly = ys[k] * sy;
                    tv[o] = px + ax[0] * lx + ay[0] * ly;
                    tv[o + 1] = py + ax[1] * lx + ay[1] * ly;
                    tv[o + 2] = pz + ax[2] * lx + ay[2] * ly;
                    tv[o + 3] = r.R * col[0]; tv[o + 4] = r.G * col[1];
                    tv[o + 5] = r.B * col[2]; tv[o + 6] = r.A * col[3];
                    tv[o + 7] = us[k]; tv[o + 8] = vs[k];
                }
                outp.Add(new DrawItem { V = tv, Tex = r.Tex0, Trans = true, Blend = r.Blend, Tag = "flipbook" });
            }
        }

        // per-particle cluster child storage (keyed on Particle identity)
        readonly Dictionary<Particle, ClusterChild[]> _clusters = new();
        ClusterChild[] ClusterStore(Particle p, int n)
        {
            if (!_clusters.TryGetValue(p, out var arr))
            {
                arr = new ClusterChild[n];
                for (int i = 0; i < n; i++) arr[i] = new ClusterChild();
                _clusters[p] = arr;
            }
            if (arr.Length < n)
            {
                var na = new ClusterChild[n];
                Array.Copy(arr, na, arr.Length);
                for (int i = arr.Length; i < n; i++) na[i] = new ClusterChild();
                arr = na; _clusters[p] = arr;
            }
            return arr;
        }

        static int FlipbookFrameAtT(Flipbook fb, float t, int speed)
        {
            int idx = 0;
            t *= speed;
            var frames = fb.Frames;
            while (idx < frames.Count && t >= frames[idx].Duration)
            {
                t -= frames[idx].Duration;
                if (idx == frames.Count - 1)
                {
                    if ((frames[idx].Flags & 0x80) != 0) idx = 0;
                    else return idx;
                }
                else idx++;
            }
            return Math.Min(idx, frames.Count - 1);
        }

        static void UpdateFlipbook(float[] st, Flipbook fb, float dt, int speed)
        {
            if (fb.Frames.Count == 0) return;
            st[1] += dt * speed;
            var frames = fb.Frames;
            while (st[1] >= frames[(int)st[0]].Duration)
            {
                st[1] -= frames[(int)st[0]].Duration;
                if ((int)st[0] == frames.Count - 1)
                {
                    if ((frames[(int)st[0]].Flags & 0x80) != 0) st[0] = 0;
                    st[1] = 0;
                    if ((frames[frames.Count - 1].Flags & 0x80) == 0) break;
                }
                else st[0]++;
            }
            if (st[0] >= frames.Count) st[0] = frames.Count - 1;
        }
    }

    // ---------- mat4 (column-major, matches TS mat4) ----------

    public static float[] Identity() => new float[16] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    public static float[] Mul(float[] a, float[] b)
    {
        var o = new float[16];
        for (int c = 0; c < 4; c++)
        for (int r = 0; r < 4; r++)
            o[4 * c + r] = a[r] * b[4 * c] + a[4 + r] * b[4 * c + 1] + a[8 + r] * b[4 * c + 2] + a[12 + r] * b[4 * c + 3];
        return o;
    }

    /// <summary>Euler rotation matrix for order index (0=XYZ..5=ZYX), radians.</summary>
    public static float[] RotEuler(double x, double y, double z, int order)
    {
        var m = Identity();
        void RotAxis(int axis, double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            var r = Identity();
            switch (axis)
            {
                case 0: r[5] = (float)c; r[6] = (float)s; r[9] = (float)-s; r[10] = (float)c; break;
                case 1: r[0] = (float)c; r[2] = (float)-s; r[8] = (float)s; r[10] = (float)c; break;
                case 2: r[0] = (float)c; r[1] = (float)s; r[4] = (float)-s; r[5] = (float)c; break;
            }
            m = Mul(m, r);
        }
        // order idx: 0 XYZ,1 YXZ,2 ZXY,3 XZY,4 YZX,5 ZYX — applied in listed order
        int[][] orders = { new[] { 0, 1, 2 }, new[] { 1, 0, 2 }, new[] { 2, 0, 1 }, new[] { 0, 2, 1 }, new[] { 1, 2, 0 }, new[] { 2, 1, 0 } };
        var seq = orders[Math.Clamp(order, 0, 5)];
        double[] ang = { x, y, z };
        foreach (var ax in seq) RotAxis(ax, ang[ax]);
        return m;
    }

    /// <summary>S·R(euler,order)·T — computeModelMatrixSRT.</summary>
    public static float[] SRT(double sx, double sy, double sz, double rx, double ry, double rz, double tx, double ty, double tz, int order)
    {
        var m = RotEuler(rx, ry, rz, order);
        m[0] *= (float)sx; m[1] *= (float)sx; m[2] *= (float)sx; m[3] *= (float)sx;
        m[4] *= (float)sy; m[5] *= (float)sy; m[6] *= (float)sy; m[7] *= (float)sy;
        m[8] *= (float)sz; m[9] *= (float)sz; m[10] *= (float)sz; m[11] *= (float)sz;
        m[12] = (float)tx; m[13] = (float)ty; m[14] = (float)tz;
        return m;
    }
}

public static class ActorParticles
{
    static uint U32(byte[] d, long o) => BitConverter.ToUInt32(d, (int)o);
    static ushort U16(byte[] d, long o) => BitConverter.ToUInt16(d, (int)o);

    /// <summary>bin.ts uploadSpriteTextures: sprite images + CLUTs anchored at
    /// a particle block header (actor @0x60, magic header, or standalone
    /// particle bin @0). Returns (spriteCount, clutCount).</summary>
    public static (int sprites, int cluts) UploadSpriteTextures(byte[] buf, int start, Gs gs)
    {
        int spriteSpecsOffs = (int)U32(buf, start + 0x08);
        int clutSpecsOffs = (int)U32(buf, start + 0x0C);
        int dataOffs = (int)U32(buf, start + 0x3C);
        if (dataOffs <= 0 || start + dataOffs + 0x50 > buf.Length) return (0, 0);
        int spriteCount = U16(buf, start + dataOffs + 0x44);
        int imageOffs = (int)U32(buf, start + dataOffs + 0x08);
        int specOffs = start + spriteSpecsOffs;
        int done = 0;
        for (int i = 0; i < spriteCount; i++, specOffs += 0x20, imageOffs += 4)
        {
            if (start + dataOffs + imageOffs + 4 > buf.Length || specOffs + 0x20 > buf.Length) break;
            int imageStart = (int)U32(buf, start + dataOffs + imageOffs);
            if (imageStart == 0) continue;
            int addr = (int)(U32(buf, specOffs) & 0x3FFF);
            int x = (sbyte)buf[specOffs + 0x08] << 4;
            int y = (sbyte)buf[specOffs + 0x09] << 4;
            int bufWidth = buf[specOffs + 0x0A] >> 2;
            int width = U16(buf, specOffs + 0x0C);
            int height = U16(buf, specOffs + 0x0E);
            int format = (sbyte)buf[specOffs + 0x18];
            try { gs.Upload(format, addr, bufWidth, x, y, width, height, buf, start + dataOffs + imageStart); done++; }
            catch { /* unsupported psm — skip this sprite */ }
        }
        int clutCount = U16(buf, start + dataOffs + 0x46);
        imageOffs = (int)U32(buf, start + dataOffs + 0x0C);
        specOffs = start + clutSpecsOffs;
        int cdone = 0;
        for (int i = 0; i < clutCount; i++, specOffs += 0x10, imageOffs += 4)
        {
            if (start + dataOffs + imageOffs + 4 > buf.Length || specOffs + 0x10 > buf.Length) break;
            int imageStart = (int)U32(buf, start + dataOffs + imageOffs);
            if (imageStart == 0) continue;
            int addr = U16(buf, specOffs) & 0x3FFF;
            try { gs.Upload(0 /*PSMCT32*/, addr, 1, 0, 0, 16, 16, buf, start + dataOffs + imageStart); cdone++; }
            catch { }
        }
        return (done, cdone);
    }

    /// <summary>Actor-embedded PPP (bin.ts parseActorParticles): the bin's
    /// @0x60 points to a particle block; dataStart = @+0x3C rel, PPP program
    /// data at dataStart + u32(dataStart+0x20) + 0x10. No funcMap remap.</summary>
    public static ParticleSim.Sys? FromActorBin(byte[] d, Gs? gs = null)
    {
        if (d.Length < 0x64) return null;
        int pOffs = (int)U32(d, 0x60);
        if (pOffs <= 0 || pOffs + 0x44 > d.Length) return null;
        if (gs != null) UploadSpriteTextures(d, pOffs, gs);
        int dataStart = (int)U32(d, pOffs + 0x3C) + pOffs;
        if (dataStart <= pOffs || dataStart + 0x60 > d.Length) return null;
        int particleOffs = (int)U32(d, dataStart + 0x20);
        if (particleOffs <= 0 || dataStart + particleOffs + 4 > d.Length) return null;
        if (U32(d, dataStart + particleOffs) != particleOffs + 0x10) return null;
        int pStart = dataStart + particleOffs + 0x10;
        if (Particles.Validate(d, pStart, synthEmitters: true) != null) return null;
        var sys = ParticleSim.Sys.FromBytes(d, pStart, synthEmitters: true);
        if (sys.Emitters.Count == 0) return null;
        MagicVm.Attach(d, dataStart, sys);
        return sys;
    }
}

public static class MagicParticles
{
    /// <summary>Standalone magic bin (bin.ts magic path): headers located by
    /// the init-function MIPS scan, sprite textures uploaded per header,
    /// particleStart via the dataStart+magicStart index table, opcodes
    /// remapped through the funcMap (40-byte record index -> real opcode).
    /// Emitters are synthesized (one per behavior) like actor particles.</summary>
    public static ParticleSim.Sys? FromMagicBin(byte[] d, int index = -1, Gs? gs = null,
        List<int>? headersOut = null, int forceHeader = -1)
    {
        // the noclip MIPS walkers (HeaderFinder/Annotator) are not ported —
        // the fixParticlePointers arg can point at a runtime-fixed (zeroed)
        // region, so instead scan the file for strict header-shaped structs
        // prefer headers associated with magicHeader callers (Annotator port:
        // a0 at each `jal magicHeader` -> u32(a0+0x2C)); fall back to the
        // strict file scan for bins the tracker cannot resolve
        var headers = MagicBin.FindCallerHeaders(d)
            .Where(h => MagicBin.ValidHeader(d, h)).Distinct().ToList();
        if (headers.Count == 0)
            for (int h = 0; h + 0x60 <= d.Length; h += 4)
                if (MagicBin.ValidHeader(d, h)) headers.Add(h);
        if (forceHeader >= 0) headers = headers.Where(h => h == forceHeader).ToList();
        if (headers.Count == 0) return null;
        headersOut?.AddRange(headers);
        if (gs != null)
            foreach (var h in headers) ActorParticles.UploadSpriteTextures(d, h, gs);
        // pick the header whose index table best matches the MIPS-derived
        // particle-index candidates, then by valid-entry count — strict-scan
        // candidates can be decoys (shape-valid headers over garbage tables)
        var candsAll = MagicBin.FindParticleIndices(d);
        int h0 = -1, dataStart = 0, magicStart = 0, count = 0, bestScore = 0;
        List<int> valid = new();
        foreach (var h in headers)
        {
            int ds = (int)BitConverter.ToUInt32(d, h + 0x3C) + h;
            int ms = (int)BitConverter.ToUInt32(d, ds + 0x20);
            int cnt = BitConverter.ToUInt16(d, ds + 0x50);
            var v = Enumerable.Range(0, cnt).Where(i => ValidEntry(d, ds, ms, cnt, i)).ToList();
            int score = candsAll.Count(v.Contains) * 1000 + v.Count;
            if (score > bestScore) { h0 = h; dataStart = ds; magicStart = ms; count = cnt; valid = v; bestScore = score; }
        }
        if (h0 < 0 || valid.Count == 0) return null;
        if (index < 0)
        {
            // auto: the annotator's magicIndex is the a2 arg of
            // getParticleData callers — take the mode, restricted to
            // entries that validate as real PPP containers
            index = candsAll.Where(valid.Contains).GroupBy(x => x)
                .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? valid[0];
        }
        if (index >= count) { Console.Error.WriteLine($"index {index} >= count {count}"); return null; }
        // the MIPS-derived index can hit an empty stub in multi-effect bins
        // (0189: entry[0] validates but is empty) — fall back to the first
        // valid non-empty entry
        if (!valid.Contains(index)) index = valid[0];
        int pStart = (int)BitConverter.ToUInt32(d, dataStart + magicStart + 4 * index) + dataStart;
        var vErr = Particles.Validate(d, pStart, synthEmitters: true);
        if (vErr != null) { Console.Error.WriteLine($"ppp@0x{pStart:x}: {vErr}"); return null; }
        var funcMap = MagicBin.BuildFuncMap(d, MagicBin.FindFuncOffset(d));
        ParticleSim.Sys sys;
        try
        {
            sys = ParticleSim.Sys.FromBytes(d, pStart,
                funcMap.Count > 0 ? funcMap.ToArray() : null, synthEmitters: true);
        }
        catch (Exception ex) { Console.Error.WriteLine("FromBytes threw: " + ex); return null; }
        if (sys == null || sys.Emitters.Count == 0) { Console.Error.WriteLine("FromBytes: emitters=0"); return null; }
        // extra flipbooks: bin.ts dataStart+0x52 count / +0x24 offset table —
        // magic-wide flipbooks appended after the PPP's own (draw ops index
        // into the combined list, so ordering matters)
        int extraCnt = BitConverter.ToUInt16(d, dataStart + 0x52);
        int extraOffs = (int)BitConverter.ToUInt32(d, dataStart + 0x24);
        if (extraCnt > 0 && extraOffs != 0)
        {
            sys.L.ExtraFlipbookIndex = sys.L.Flipbooks.Count;
            for (int i = 0; i < extraCnt && extraCnt < 256; i++)
            {
                long fo = BitConverter.ToUInt32(d, dataStart + extraOffs + 4 * i) + dataStart;
                if (fo <= 0 || fo >= d.Length) continue;
                // a wrong header's "extra flipbooks" are arbitrary bytes —
                // keep whatever parsed cleanly, drop the rest
                try { sys.L.Flipbooks.Add(ParticleSim.ParseFlipbook(d, fo)); }
                catch { break; }
            }
        }
        if (sys.L.Behaviors.Count > 1) MagicVm.Attach(d, dataStart, sys);
        return sys;
    }

    /// <summary>Structural dump for the editor inspector: headers, the
    /// particle-index table with valid/stub marking, the auto-selected index
    /// and the decoded PPP contents (emitters/programs/geos/flipbooks with
    /// flags, blend settings and TEX0 refs).</summary>
    /// <summary>Index-table entry is valid iff it points past the table
    /// itself (entries into the header/table span are circular decoys) and
    /// the target validates as a PPP container.</summary>
    static bool ValidEntry(byte[] d, int ds, int ms, int cnt, int i)
    {
        if (i >= cnt) return false;
        long pi = BitConverter.ToUInt32(d, ds + ms + 4 * i) + ds;
        if (pi < ds + ms + 4L * cnt || pi >= d.Length) return false;
        // all-zero-count containers validate but hold no particle data —
        // require at least one populated section
        int counts = BitConverter.ToUInt16(d, (int)pi + 0x04)
            + BitConverter.ToUInt16(d, (int)pi + 0x06)
            + BitConverter.ToUInt16(d, (int)pi + 0x08)
            + BitConverter.ToUInt16(d, (int)pi + 0x0A)
            + BitConverter.ToUInt16(d, (int)pi + 0x0C);
        return counts > 0 && Particles.Validate(d, (int)pi, synthEmitters: true) == null;
    }

    public static string Describe(byte[] d)
    {
        var sb = new System.Text.StringBuilder();
        var caller = MagicBin.FindCallerHeaders(d)
            .Where(h => MagicBin.ValidHeader(d, h)).Distinct().ToList();
        var headers = new List<int>(caller);
        if (headers.Count == 0)
            for (int h = 0; h + 0x60 <= d.Length; h += 4)
                if (MagicBin.ValidHeader(d, h)) headers.Add(h);
        if (headers.Count == 0) return "particles: nenhum header válido\n";
        sb.AppendLine("headers" + (caller.Count > 0 ? " (via magicHeader callers)" : " (strict scan)")
            + ": [" + string.Join(", ", headers.Select(h => $"0x{h:x}")) + "]");
        // best header = most valid PPP entries (strict-scan decoys have
        // shape-valid headers pointing at garbage tables)
        int h0 = -1, dataStart = 0, magicStart = 0, count = 0, bestScore = 0;
        var cands = MagicBin.FindParticleIndices(d);
        foreach (var h in headers)
        {
            int ds = (int)BitConverter.ToUInt32(d, h + 0x3C) + h;
            int ms = (int)BitConverter.ToUInt32(d, ds + 0x20);
            int cnt = BitConverter.ToUInt16(d, ds + 0x50);
            int ok = 0, hit = 0;
            for (int i = 0; i < cnt; i++)
                if (ValidEntry(d, ds, ms, cnt, i)) { ok++; if (cands.Contains(i)) hit++; }
            int score = hit * 1000 + ok;
            if (score > bestScore) { h0 = h; dataStart = ds; magicStart = ms; count = cnt; bestScore = score; }
        }
        if (h0 < 0) { sb.AppendLine("  (nenhum header com entries válidas)"); return sb.ToString(); }
        sb.AppendLine($"selected header: 0x{h0:x}");
        sb.AppendLine($"dataStart=0x{dataStart:x} indexTable=+0x{magicStart:x} entries={count} "
            + $"indexCandidates=[{string.Join(", ", cands)}]");
        var valid = new List<int>();
        for (int i = 0; i < count; i++)
        {
            long pi = BitConverter.ToUInt32(d, dataStart + magicStart + 4 * i) + dataStart;
            bool ok = ValidEntry(d, dataStart, magicStart, count, i);
            if (ok) valid.Add(i);
            sb.AppendLine($"  entry[{i}] -> ppp@0x{pi:x} {(ok ? "OK" : "stub/invalid")}");
        }
        int index = cands.Where(valid.Contains).GroupBy(x => x)
            .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key
            ?? (valid.Count > 0 ? valid[0] : -1);
        sb.AppendLine($"selected index: {index}");
        if (index < 0) return sb.ToString();
        var sys = FromMagicBin(d, index);
        if (sys == null) { sb.AppendLine("  (load falhou)"); return sb.ToString(); }
        var l = sys.L;
        sb.AppendLine($"emitters={l.Emitters.Count} behaviors={l.Behaviors.Count} "
            + $"geos={l.Geos.Count} patterns={l.Patterns.Count} flipbooks={l.Flipbooks.Count}");
        for (int i = 0; i < l.Behaviors.Count; i++)
        {
            var b = l.Behaviors[i];
            var ops = new SortedDictionary<string, int>();
            foreach (var p in b.Programs)
                foreach (var ins in p.Instructions)
                {
                    var n = Particles.OpName(ins.Op);
                    ops[n] = ops.TryGetValue(n, out var c) ? c + 1 : 1;
                }
            sb.AppendLine($"behavior[{i}] progs={b.Programs.Count} ops: "
                + string.Join(" ", ops.Select(kv => $"{kv.Key}x{kv.Value}")));
            if (System.Environment.GetEnvironmentVariable("FFX_DEBUG_ELEC") != null)
            {
                sb.AppendLine($"  bhv lifetime={b.Lifetime} ignore={b.IgnoreLifetime}");
                for (int pi = 0; pi < b.Programs.Count; pi++)
                {
                    var pp = b.Programs[pi];
                    sb.AppendLine($"  prog[{pi}] start={pp.Start} life={pp.Lifetime} ops: "
                        + string.Join(" ", pp.Instructions.Select(x =>
                            $"0x{x.Op:x}{(x.Emits != null ? "(" + string.Join(";", x.Emits.Select(em => em.Program)) + ")" : "")}")));
                }
            }
            foreach (var p in b.Programs)
                foreach (var ins in p.Instructions)
                    if (ins.Glares != null)
                        foreach (var gd in ins.Glares)
                            sb.AppendLine($"  glare op=0x{ins.Op:x2} t={gd.T} fb={gd.Flipbook} "
                                + $"scale={gd.Scale:0.###} maxScale={gd.MaxScale:0.###} "
                                + $"col=({gd.Color[0]:0.##},{gd.Color[1]:0.##},{gd.Color[2]:0.##},{gd.Color[3]:0.##}) "
                                + $"slots=[{string.Join(",", ins.Slots)}]");
        }
        for (int i = 0; i < l.Geos.Count; i++)
        {
            var g = l.Geos[i];
            var tex = g.Prims.FirstOrDefault(p => p.Tex0 != null)?.Tex0;
            sb.AppendLine($"geo[{i}] flags=0x{g.Flags:x} blend=0x{g.BlendSettings:x} "
                + $"points={g.Points.Length} prims={g.Prims.Count}"
                + (tex != null ? $" tex0(tbp=0x{tex.Tbp0:x} psm={tex.Psm} cbp=0x{tex.Cbp:x} csa={tex.Csa})" : " untextured"));
        }
        for (int i = 0; i < l.Flipbooks.Count; i++)
        {
            var f = l.Flipbooks[i];
            var blends = f.Frames.SelectMany(fr => fr.Rects.Select(r => r.Blend)).Distinct();
            var tex = f.Frames.SelectMany(fr => fr.Rects.Where(r => r.Tex0 != null).Select(r => r.Tex0!)).FirstOrDefault();
            sb.AppendLine($"flipbook[{i}] frames={f.Frames.Count} "
                + $"rects={f.Frames.Sum(fr => fr.Rects.Count)} blends=[{string.Join(",", blends.Select(b => $"0x{b:x}"))}]"
                + $" tex0={(tex != null && (tex.Tbp0 != 0 || tex.Psm != 0) ? $"tbp=0x{tex.Tbp0:x} psm={tex.Psm}" : "none")}");
        }
        return sb.ToString();
    }
}
