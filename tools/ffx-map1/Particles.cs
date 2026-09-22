// FFX particle (PPP) container readout.
// Structural port of parseParticleData in noclip_reference/particle.ts:
// header counts/offsets, emitter records (0x50 stride), behavior + program
// headers, the per-program instruction census (opcode + name + datum size),
// geometry entries, patterns and flipbook count.
//
// NOT ported (documented): instruction semantics (gl-matrix maths, scratch
// buffers, texture decode, rendering) — a browsing/inventory readout, not a
// simulator.
using System;
using System.Collections.Generic;
using System.Text;

public static class Particles
{
    /// <summary>Instruction opcode -> class name (particle.ts instructionTable).</summary>
    static readonly Dictionary<uint, string> OpNames = new()
    {
        { 0x0, "Velocity" },
        { 0x1, "Velocity" },
        { 0x2, "Velocity" },
        { 0x3, "ColorVelocity" },
        { 0x4, "Velocity" },
        { 0x5, "Velocity" },
        { 0x6, "Velocity" },
        { 0x7, "ColorVelocity" },
        { 0x8, "Step" },
        { 0x9, "Step" },
        { 0xA, "Step" },
        { 0xB, "Step" },
        { 0xC, "RandomStep" },
        { 0xD, "RandomStep" },
        { 0xE, "RandomCube" },
        { 0xF, "NOP" },
        { 0x10, "PosRotScale" },
        { 0x11, "PosScale" },
        { 0x12, "ApplyParent" },
        { 0x13, "ComposedMatrix" },
        { 0x14, "StandardMatrix" },
        { 0x15, "SimpleGeo" },
        { 0x17, "UVScrollGeo" },
        { 0x18, "SimpleFlipbook" },
        { 0x19, "FlipbookTrail.VarTail" },
        { 0x1A, "FlipbookTrail" },
        { 0x1B, "PeriodicEmit" },
        { 0x1C, "ResettingPeriodicEmit" },
        { 0x1D, "RandomStep" },
        { 0x1E, "RandomStep" },
        { 0x1F, "PeriodicEmit" },
        { 0x20, "GlareBase" },
        { 0x21, "SimpleGlare" },
        { 0x23, "MoreGlare" },
        { 0x24, "ScaledGlare" },
        { 0x25, "RandomCube" },
        { 0x27, "RandomCube" },
        { 0x29, "SimpleEmit" },
        { 0x2C, "ComposedMatrix" },
        { 0x2D, "CircleBlur" },
        { 0x2E, "Pyrefly" },
        { 0x2F, "Velocity.reverse" },
        { 0x30, "AxialBillboardMatrix" },
        { 0x31, "FlipbookCluster" },
        { 0x32, "GeoBlur" },
        { 0x33, "WibbleUVScrollGeo" },
        { 0x34, "StandardMatrix" },
        { 0x37, "SimpleGeo" },
        { 0x39, "SetValue" },
        { 0x3A, "SetPos" },
        { 0x3B, "ElectricTarget" },
        { 0x3C, "ElectricColor" },
        { 0x38, "PointChain" }, { 0x41, "PointChain" }, { 0x44, "PointChain" },
        { 0x45, "PointChain" }, { 0x49, "PointChain" }, { 0x4A, "PointChain" },
        { 0x4B, "PointChain" }, { 0x4C, "PointChain" }, { 0x5A, "PointChain" },
        { 0x5B, "PointChain" }, { 0x80, "PointChain" }, { 0x88, "PointChain" },
        { 0x1001, "PointChain" }, { 0x1008, "PointChain" }, { 0x1011, "PointChain" },
        { 0x3D, "Electricity" },
        { 0x3E, "RandomEmit" },
        { 0x3F, "RandomEmit" },
        { 0x43, "PeriodicEmit" },
        { 0x46, "DualEmit" },
        { 0x47, "UVScrollGeo" },
        { 0x4D, "DepthOffset" },
        { 0x4E, "UVScrollGeo" },
        { 0x4F, "BillboardDepthOffset" },
        { 0x50, "SimpleGeo" },
        { 0x51, "SimpleFlipbook" },
        { 0x53, "PosRotScale" },
        { 0x55, "RandomStep" },
        { 0x58, "Water" },
        { 0x5C, "AttractingTarget" },
        { 0x5D, "Attract" },
        { 0x5E, "SimpleGeo" },
        { 0x61, "Rain" },
        { 0x63, "FlipbookTrail" },
        { 0x64, "PointLight" },
        { 0x68, "Velocity" },
        { 0x69, "LoopStep" },
        { 0x6A, "LoopStep" },
        { 0x6B, "LoopStep" },
        { 0x6C, "PosRotScale" },
        { 0x6D, "ComposedMatrix" },
        { 0x6E, "WrapUVScrollGeo" },
        { 0x70, "WibbleUVScrollGeo" },
        { 0x71, "WrapUVScrollGeo.atCamera" },
        { 0x72, "ChildSetup" },
        { 0x73, "PosRotScale" },
        { 0x74, "PeriodicSimpleEmit" },
        { 0x75, "ColorVelocity" },
        { 0x76, "PeriodicEmit.atOrigin" },
        { 0x77, "FlippedFlipbook" },
        { 0x78, "WrapUVScrollGeo" },
        { 0x79, "WrapUVScrollGeo.atOrigin" },
        { 0x7B, "FlipbookCluster" },
        { 0x87, "PointLightGroup" },
        { 0x8A, "Overlay" },
        { 0x1002, "FakeGeo" },
    };

    /// <summary>Writer offsets for the 0x50-byte emitter records parsed above
    /// (map/standalone PPP layout — magic/actor bins use 0x10 scale stubs and
    /// have no editable emitter table). pos f3, euler i3 (degrees), scale f3,
    /// delay i32, behavior i32, maxDist/height/width f32, id/g u16.</summary>
    public static class PppEdit
    {
        public const int RecSize = 0x50;
        public const int FPos = 0x00, FEuler = 0x10, FScale = 0x20,
            FDelay = 0x30, FBehavior = 0x34, FMaxDist = 0x38,
            FHeight = 0x3C, FWidth = 0x40;

        /// <summary>File offset of emitter record idx inside a bin whose PPP
        /// container starts at pppOffs (records begin at +0x20).</summary>
        public static long EmitterOff(int pppOffs, int idx) =>
            pppOffs + 0x20L + idx * RecSize;

        /// <summary>Emit-family datum spec per opcode: (kind, entry size,
        /// count offset, period/mask offset or -1, program offset, whether
        /// the second byte is a slot mask rather than a period). All kinds
        /// carry t f32@0 (4096-fixed) and pattern u16@4; program is an i32
        /// child-program selector. Mirrors ParticleSim.DecodeData.</summary>
        public static readonly Dictionary<uint, (string Kind, int Size, int CntOff,
            int PerOff, int ProgOff, bool PerMask)> EmitOps = new()
        {
            [0x1B] = ("emit",       0x14, 6,    7,    0x0C, false), // PeriodicEmit
            [0x1C] = ("emitraw",    0x14, 6,    7,    0x0C, false), // ResettingPeriodicEmit
            [0x1F] = ("emitdir",    0x20, 0x0C, 0x0D, 0x10, false), // PeriodicEmit dir
            [0x29] = ("simpleemit", 0x10, 6,    7,    0x0C, false), // SimpleEmit
            [0x3E] = ("randemitn",  0x1C, 6,    7,    0x0C, true),  // RandomEmit normalized
            [0x3F] = ("randemit",   0x20, 0x0C, 0x0D, 0x10, true),  // RandomEmit
            [0x43] = ("emitangle",  0x1C, 0x0C, 0x0D, 0x10, false), // PeriodicEmit angle
            [0x46] = ("dualemit",   0x1C, 8,    9,    0x0C, true),  // DualEmit
            [0x74] = ("emitraw",    0x14, 6,    7,    0x0C, false), // PeriodicSimpleEmit
            [0x76] = ("emit",       0x14, 6,    7,    0x0C, false), // PeriodicEmit.atOrigin
        };

        /// <summary>Common emit datum fields — pattern (u16@4) and child
        /// program (i32@spec.ProgOff) shared by every emit kind.</summary>
        public const int EmitPatOff = 4;

        /// <summary>Color-carrying datum spec: op → (kind, entry size,
        /// RGBA-byte fields). Alpha is a plain u8 in the record (the sim
        /// divides by 128/255 per field — the raw byte is authoritative).
        /// ftrailvar (0x19) and eleccolor (0x3C) encode colors as i16
        /// vectors, not bytes — excluded here.</summary>
        public static readonly Dictionary<uint, (string Kind, int Size,
            (string Name, int Off)[] Fields)> ColorOps = new()
        {
            [0x21] = ("glare",       0x10, new[] { ("rgb", 8) }),
            [0x23] = ("moreglare",   0x14, new[] { ("rgb", 8) }),
            [0x24] = ("scaledglare", 0x14, new[] { ("rgb", 8) }),
            [0x1A] = ("ftrail",      0x24, new[] { ("head", 0x14), ("tail", 0x18) }),
            [0x63] = ("ftrailp",     0x28, new[] { ("head", 0x14), ("tail", 0x18) }),
        };

        public readonly record struct ColorEntry(int Behavior, int Program,
            int Instr, uint Op, string Kind, int Entry, string Field, long Off);

        /// <summary>Structural add: duplicate emitter record idx at the END
        /// of the emitter table (before the behavior-offset table). All PPP
        /// offsets are container-relative: bump the four header offsets and
        /// every behavior-table entry by 0x50. The container itself lives
        /// inside a map bin — caller must also shift MAP1 header slots
        /// pointing past the insertion offset (FixMapHeaderSlots).</summary>
        public static byte[] DuplicateEmitter(byte[] d, int pppOffs, int idx,
            bool fixMapHeader = true)
        {
            int emitterCount = (int)BitConverter.ToUInt16(d, pppOffs + 0x04);
            int behaviorCount = (int)BitConverter.ToUInt16(d, pppOffs + 0x06);
            if (idx < 0 || idx >= emitterCount)
                throw new InvalidDataException($"emitter {idx} fora de 0..{emitterCount - 1}");
            int ins = pppOffs + 0x20 + emitterCount * RecSize;
            var n = new byte[d.Length + RecSize];
            d.AsSpan(0, ins).CopyTo(n);
            // clone the source record into the appended slot
            d.AsSpan(pppOffs + 0x20 + idx * RecSize, RecSize).CopyTo(n.AsSpan(ins));
            d.AsSpan(ins).CopyTo(n.AsSpan(ins + RecSize));
            // emitter count
            BitConverter.GetBytes((ushort)(emitterCount + 1)).CopyTo(n, pppOffs + 0x04);
            // the four container-relative table offsets
            foreach (int ho in new[] { 0x10, 0x14, 0x18, 0x1C })
            {
                uint v = BitConverter.ToUInt32(n, pppOffs + ho);
                if (v != 0) BitConverter.GetBytes(v + RecSize).CopyTo(n, pppOffs + ho);
            }
            // behavior-offset table entries (also container-relative) —
            // the table itself now sits RecSize later in the file
            uint bto = BitConverter.ToUInt32(n, pppOffs + 0x10) + (uint)pppOffs;
            for (int i = 0; i < behaviorCount; i++)
            {
                int te = (int)bto + 4 * i;
                uint v = BitConverter.ToUInt32(n, te);
                BitConverter.GetBytes(v + RecSize).CopyTo(n, te);
            }
            if (fixMapHeader) FixMapHeaderSlots(n, ins, RecSize, d.Length);
            return n;
        }

        /// <summary>Physical emitter delete — the exact inverse of
        /// DuplicateEmitter: drop the 0x50 record, decrement the count,
        /// shift the four table offsets and every behavior-table entry
        /// back by RecSize. Emitters are never referenced by index
        /// (behaviors address programs inside their own emitter), so no
        /// index fixup is needed.</summary>
        public static byte[] DeleteEmitter(byte[] d, int pppOffs, int idx,
            bool fixMapHeader = true)
        {
            int emitterCount = (int)BitConverter.ToUInt16(d, pppOffs + 0x04);
            int behaviorCount = (int)BitConverter.ToUInt16(d, pppOffs + 0x06);
            if (idx < 0 || idx >= emitterCount)
                throw new InvalidDataException($"emitter {idx} fora de 0..{emitterCount - 1}");
            int del = pppOffs + 0x20 + idx * RecSize;
            var n = new byte[d.Length - RecSize];
            d.AsSpan(0, del).CopyTo(n);
            d.AsSpan(del + RecSize).CopyTo(n.AsSpan(del));
            BitConverter.GetBytes((ushort)(emitterCount - 1)).CopyTo(n, pppOffs + 0x04);
            foreach (int ho in new[] { 0x10, 0x14, 0x18, 0x1C })
            {
                uint v = BitConverter.ToUInt32(n, pppOffs + ho);
                if (v != 0) BitConverter.GetBytes(v - RecSize).CopyTo(n, pppOffs + ho);
            }
            uint bto = BitConverter.ToUInt32(n, pppOffs + 0x10) + (uint)pppOffs;
            for (int i = 0; i < behaviorCount; i++)
            {
                int te = (int)bto + 4 * i;
                uint v = BitConverter.ToUInt32(n, te);
                BitConverter.GetBytes(v - RecSize).CopyTo(n, te);
            }
            if (fixMapHeader) FixMapHeaderSlots(n, del, -RecSize, d.Length);
            return n;
        }

        /// <summary>Shift every non-zero u32 header slot in [0x10,0x80)
        /// that points at/after ins by delta — the MAP1-side fixup for any
        /// insertion inside a fetched geometry bin.</summary>
        public static void FixMapHeaderSlots(byte[] d, int ins, int delta, int oldLen)
        {
            for (int o = 0x10; o < 0x80; o += 4)
            {
                uint v = BitConverter.ToUInt32(d, o);
                if (v != 0 && v < oldLen && v >= ins)
                    BitConverter.GetBytes(v + (uint)delta).CopyTo(d, o);
            }
        }

        /// <summary>Generic datum field type for the universal patch
        /// surface. FT is the 4096-fixed-point t found at record +0.</summary>
        public enum DF : byte { U8, I8, U16, I16, U32, I32, F32, FT, RGBA }
        public readonly record struct DField(string Name, int Off, DF Type);

        /// <summary>Field-level spec for every patchable non-emit datum
        /// kind (emit records are covered by EmitOps + the dedicated UI).
        /// Fields mirror ParticleSim.DecodeData + the noclip parsers.
        /// vec/ivec/hvec datums hold the actual vector constants — editing
        /// them rewrites effect positions/velocities persistently.</summary>
        public static readonly Dictionary<uint, (string Kind, int Size,
            DField[] Fields)> DatumOps = new()
        {
            // geometry binds + flags
            [0x15] = ("geo", 0x08, Flds(("t", 0, DF.FT), ("geo", 4, DF.U32))),
            [0x37] = ("geoflags", 0x0C, Flds(("t", 0, DF.FT), ("geo", 4, DF.U32), ("flags", 8, DF.U8))),
            [0x50] = ("geoparams", 0x10, Flds(("t", 0, DF.FT), ("geo", 4, DF.U32),
                ("flags", 8, DF.U8), ("fog", 9, DF.U8), ("flag2000", 0xA, DF.U8),
                ("fade", 0xB, DF.U8), ("depthoff", 0xC, DF.U8))),
            [0x5E] = ("geoblend", 0x10, Flds(("t", 0, DF.FT), ("geo", 4, DF.U32),
                ("blend", 8, DF.U8), ("flags", 0xA, DF.U8), ("fog", 0xB, DF.U8),
                ("flag2000", 0xC, DF.U8), ("fade", 0xD, DF.U8), ("depthoff", 0xE, DF.U8))),
            // flipbooks
            [0x18] = ("flip", 0x0C, Flds(("t", 0, DF.FT), ("index", 4, DF.U32), ("speed", 8, DF.U16))),
            [0x51] = ("flipparam", 0x10, Flds(("t", 0, DF.FT), ("index", 4, DF.U32), ("speed", 8, DF.U16))),
            [0x77] = ("flipflip", 0x10, Flds(("t", 0, DF.FT), ("index", 4, DF.U32),
                ("speed", 8, DF.U16), ("flipflags", 0xF, DF.U8))),
            // uv scrolls (uInc PV4F@8, vInc PV4F@0x14)
            [0x17] = ("uvscroll", 0x20, UvScroll(null)),
            [0x47] = ("uvscrollf", 0x24, UvScroll(Flds(("flags", 0x20, DF.U8)))),
            [0x4E] = ("uvscrollparam", 0x28, UvScroll(Flds(("flags", 0x20, DF.U8),
                ("fog", 0x21, DF.U8), ("flag2000", 0x22, DF.U8), ("fade", 0x23, DF.U8),
                ("depthoff", 0x24, DF.U8)))),
            [0x6E] = ("wrapscroll", 0x28, UvScroll(Flds(("blendmode", 0x20, DF.U8),
                ("blendalpha", 0x21, DF.U8), ("flags", 0x22, DF.U8), ("fog", 0x23, DF.U8),
                ("fade", 0x24, DF.U8), ("flag2000", 0x25, DF.U8), ("depthoff", 0x26, DF.U8)))),
            [0x78] = ("wrapscroll", 0x28, UvScroll(Flds(("blendmode", 0x20, DF.U8),
                ("blendalpha", 0x21, DF.U8), ("flags", 0x22, DF.U8), ("fog", 0x23, DF.U8),
                ("fade", 0x24, DF.U8), ("flag2000", 0x25, DF.U8), ("depthoff", 0x26, DF.U8)))),
            [0x79] = ("wrapscroll", 0x28, UvScroll(Flds(("blendmode", 0x20, DF.U8),
                ("blendalpha", 0x21, DF.U8), ("flags", 0x22, DF.U8), ("fog", 0x23, DF.U8),
                ("fade", 0x24, DF.U8), ("flag2000", 0x25, DF.U8), ("depthoff", 0x26, DF.U8)))),
            // glare family
            [0x20] = ("glarebase", 0x10, Flds(("t", 0, DF.FT), ("factor", 4, DF.F32),
                ("maxdist", 8, DF.F32), ("distred", 0xC, DF.U8), ("viewdir", 0xD, DF.U8))),
            [0x21] = ("glare", 0x10, Flds(("t", 0, DF.FT), ("flipbook", 4, DF.U32),
                ("rgb", 8, DF.RGBA), ("scale", 0xC, DF.F32))),
            [0x23] = ("moreglare", 0x14, Flds(("t", 0, DF.FT), ("flipbook", 4, DF.U32),
                ("rgb", 8, DF.RGBA), ("scale", 0xC, DF.F32), ("scaledist", 0x10, DF.U16),
                ("alphadist", 0x12, DF.U8))),
            [0x24] = ("scaledglare", 0x14, Flds(("t", 0, DF.FT), ("flipbook", 4, DF.U32),
                ("rgb", 8, DF.RGBA), ("maxscale", 0xC, DF.F32), ("maxradius", 0x10, DF.U16),
                ("xmode", 0x12, DF.U8), ("ymode", 0x13, DF.U8))),
            // trails
            [0x1A] = ("ftrail", 0x24, Trail()),
            [0x63] = ("ftrailp", 0x28, Trail()),
            // rain
            [0x61] = ("rain", 0x40, Flds(("t", 0, DF.FT), ("range", 4, DF.F32),
                ("count", 8, DF.U32), ("velx", 0x10, DF.F32), ("vely", 0x14, DF.F32),
                ("velz", 0x18, DF.F32), ("vrx", 0x20, DF.F32), ("vry", 0x24, DF.F32),
                ("vrz", 0x28, DF.F32), ("baselen", 0x30, DF.F32), ("lenrange", 0x34, DF.F32))),
            // point lights (noclip PointLightP/GroupP)
            [0x64] = ("light", 0x10, Flds(("t", 0, DF.FT), ("radius", 4, DF.F32),
                ("group", 8, DF.U8), ("strength", 0xC, DF.F32))),
            [0x87] = ("lightgroup", 0x14, Flds(("t", 0, DF.FT), ("radius", 4, DF.F32),
                ("group", 8, DF.U8), ("strength", 0xC, DF.F32), ("pattern", 0x10, DF.I16))),
            // overlay sprite (noclip overlayP — 0x28 stride)
            [0x8A] = ("overlay", 0x28, Flds(("t", 0, DF.FT), ("tbp", 4, DF.U32),
                ("cbp", 8, DF.U32), ("width", 0xC, DF.U32), ("height", 0x10, DF.U32),
                ("du", 0x14, DF.U32), ("dv", 0x18, DF.U32), ("uvel", 0x1C, DF.U32),
                ("vvel", 0x20, DF.U32), ("blendalpha", 0x24, DF.U8), ("blendmode", 0x25, DF.U8))),
            // water / wibble scalars+flags (wave vecs stay unlisted)
            [0x58] = ("water", 0xA0, UvScroll(Flds(("texslot", 0x80, DF.U8),
                ("texdur", 0x81, DF.U8), ("blendmode", 0x82, DF.U8), ("blendalpha", 0x83, DF.U8),
                ("cull", 0x84, DF.U8), ("flag2000", 0x85, DF.U8), ("fog", 0x86, DF.U8),
                ("ztest", 0x87, DF.U8), ("radius", 0x8C, DF.F32), ("worldgrid", 0x90, DF.U8)))),
            [0x33] = ("wibble", 0x60, UvScroll(Flds(("texslot", 0x50, DF.U8),
                ("texdur", 0x51, DF.U8), ("blendmode", 0x52, DF.U8), ("blendalpha", 0x53, DF.U8),
                ("flags", 0x54, DF.U8), ("flag2000", 0x55, DF.U8), ("fog", 0x56, DF.U8)))),
            [0x70] = ("wibble0", 0x2C, UvScroll(Flds(("texslot", 0x20, DF.U8),
                ("texdur", 0x21, DF.U8), ("blendmode", 0x22, DF.U8), ("blendalpha", 0x23, DF.U8),
                ("flags", 0x24, DF.U8), ("flag2000", 0x25, DF.U8), ("fog", 0x26, DF.U8)))),
            // electricity
            [0x3D] = ("electric", 0x60, Flds(("t", 0, DF.FT), ("targetprog", 4, DF.I32),
                ("totaldisp", 0x30, DF.F32), ("widthdelta", 0x34, DF.F32),
                ("endwidthfrac", 0x38, DF.F32), ("corewidthfrac", 0x3C, DF.F32),
                ("shrinkfrac", 0x40, DF.F32), ("shrinkstep", 0x44, DF.F32),
                ("perturbdelta", 0x48, DF.F32), ("tipaccel", 0x4C, DF.F32),
                ("targetstrdelta", 0x50, DF.F32), ("anglerange", 0x54, DF.I16),
                ("blendmode", 0x58, DF.U8), ("detach", 0x59, DF.U8),
                ("normalizedir", 0x5A, DF.U8), ("reversed", 0x5B, DF.U8),
                ("inheritpos", 0x5C, DF.U8))),
            [0x3C] = ("eleccolor", 0x40, Flds(("t", 0, DF.FT), ("widthvel", 0x28, DF.F32),
                ("perturbvel", 0x2C, DF.F32), ("tipaccel", 0x30, DF.F32), ("targetvel", 0x34, DF.F32))),
            [0x3B] = ("electarget", 0x18, Flds(("t", 0, DF.FT), ("threshold", 4, DF.F32),
                ("attractcutoff", 8, DF.F32), ("maxlerpdelta", 0xC, DF.F32), ("next", 0x10, DF.I32))),
            // pyrefly / childsetup / attract
            [0x2E] = ("pyrefly", 0x50, Flds(("t", 0, DF.FT), ("flipbook", 4, DF.U32),
                ("speed", 8, DF.U32), ("maxscale", 0xC, DF.F32), ("minscale", 0x10, DF.F32),
                ("sizerange", 0x14, DF.F32), ("startgap", 0x18, DF.F32),
                ("traillen", 0x1C, DF.U8), ("renderhead", 0x1D, DF.U8))),
            [0x72] = ("childsetup", 0x10, Flds(("t", 0, DF.FT), ("pattern", 0xC, DF.I16))),
            [0x5C] = ("attracttgt", 0x0C, Flds(("t", 0, DF.FT), ("mindist", 4, DF.F32),
                ("repeldist", 8, DF.F32))),
            [0x5D] = ("attract", 0x0C, Flds(("t", 0, DF.FT), ("target", 4, DF.I32),
                ("offset", 8, DF.I32))),
            // geoblur (PV4F inc @8 = 3 floats)
            [0x32] = ("geoblur", 0x18, Flds(("t", 0, DF.FT), ("geo", 4, DF.U32),
                ("incx", 8, DF.F32), ("incy", 0xC, DF.F32), ("incz", 0x10, DF.F32),
                ("flags", 0x14, DF.U8), ("mulz", 0x15, DF.U8))),
            // vector constants — the motion data itself
            [0x2F] = ("vec", 0x20, Vec()), [0x39] = ("vec", 0x20, Vec()),
            [0x69] = ("vec", 0x20, Vec()), [0x6B] = ("vec", 0x20, Vec()),
            [0x75] = ("vec", 0x20, Vec()),
            [0x68] = ("ivec", 0x20, IVec()), [0x6A] = ("ivec", 0x20, IVec()),
            [0x03] = ("hvec", 0x10, Flds(("t", 0, DF.FT), ("v0", 8, DF.I16),
                ("v1", 0xA, DF.I16), ("v2", 0xC, DF.I16), ("v3", 0xE, DF.I16))),
            // random steps
            [0x1D] = ("rand", 0x30, Rand()), [0x1E] = ("rand", 0x30, Rand()),
            [0x25] = ("rand", 0x30, Rand()), [0x27] = ("rand", 0x30, Rand()),
            [0x55] = ("hrand", 0x18, Flds(("t", 0, DF.FT), ("target", 4, DF.I32),
                ("v0", 8, DF.I16), ("v1", 0xA, DF.I16), ("v2", 0xC, DF.I16),
                ("v3", 0xE, DF.I16), ("peaked", 0x10, DF.U8))),
            // enable flag
            [0x12] = ("enabled", 0x0C, Flds(("t", 0, DF.FT), ("flag", 4, DF.I32))),
        };

        static DField[] Flds(params (string Name, int Off, DF Type)[] f) =>
            f.Select(x => new DField(x.Name, x.Off, x.Type)).ToArray();
        static DField[] UvScroll(DField[]? extra)
        {
            var f = new List<DField>
            {
                new("t", 0, DF.FT), new("geo", 4, DF.U32),
                new("u0", 8, DF.F32), new("u1", 0xC, DF.F32), new("u2", 0x10, DF.F32),
                new("v0", 0x14, DF.F32), new("v1", 0x18, DF.F32), new("v2", 0x1C, DF.F32),
            };
            if (extra != null) f.AddRange(extra);
            return f.ToArray();
        }
        static DField[] Trail() => Flds(("t", 0, DF.FT), ("flipbook", 4, DF.U32),
            ("speed", 8, DF.U32), ("maxscale", 0xC, DF.F32), ("minscale", 0x10, DF.F32),
            ("head", 0x14, DF.RGBA), ("tail", 0x18, DF.RGBA), ("startgap", 0x1C, DF.F32),
            ("traillen", 0x20, DF.U8), ("renderhead", 0x22, DF.U8));
        static DField[] Vec() => Flds(("t", 0, DF.FT), ("v0", 0x10, DF.F32),
            ("v1", 0x14, DF.F32), ("v2", 0x18, DF.F32), ("v3", 0x1C, DF.F32));
        static DField[] IVec() => Flds(("t", 0, DF.FT), ("v0", 0x10, DF.I32),
            ("v1", 0x14, DF.I32), ("v2", 0x18, DF.I32), ("v3", 0x1C, DF.I32));
        static DField[] Rand() => Flds(("t", 0, DF.FT), ("target", 4, DF.I32),
            ("v0", 0x10, DF.F32), ("v1", 0x14, DF.F32), ("v2", 0x18, DF.F32),
            ("v3", 0x1C, DF.F32), ("peaked", 0x20, DF.U8));

        public readonly record struct DatumEntry(int Behavior, int Program,
            int Instr, uint Op, string Kind, int Entry, long Off);

        /// <summary>Enumerate every patchable non-emit datum record.</summary>
        public static IEnumerable<DatumEntry> DatumEntries(byte[] d,
            int pppOffs, int[]? funcMap = null, bool synthEmitters = false)
        {
            Layout l;
            try { l = Parse(d, pppOffs, synthEmitters); }
            catch { yield break; }
            for (int bi = 0; bi < l.Behaviors.Count; bi++)
            {
                var b = l.Behaviors[bi];
                for (int pi = 0; pi < b.Programs.Count; pi++)
                {
                    var prog = b.Programs[pi];
                    for (int ii = 0; ii < prog.Instructions.Count; ii++)
                    {
                        var ins = prog.Instructions[ii];
                        uint op = ins.Opcode;
                        if (funcMap != null && op < funcMap.Length)
                            op = (uint)funcMap[op];
                        if (!DatumOps.TryGetValue(op, out var spec)) continue;
                        long eo = b.Offs + ins.DataOff;
                        for (int ei = 0; ei < 4096 && eo + spec.Size <= d.Length; ei++, eo += spec.Size)
                        {
                            if (BitConverter.ToUInt32(d, (int)eo) == 0xFFFFF000) break;
                            yield return new DatumEntry(bi, pi, ii, op, spec.Kind, ei, eo);
                        }
                    }
                }
            }
        }

        /// <summary>Read a datum field as a display string (FT → seconds,
        /// RGBA → RRGGBBAA hex).</summary>
        public static string ReadField(byte[] d, long recOff, DField f) => f.Type switch
        {
            DF.U8 => d[recOff + f.Off].ToString(),
            DF.I8 => ((sbyte)d[recOff + f.Off]).ToString(),
            DF.U16 => BitConverter.ToUInt16(d, (int)(recOff + f.Off)).ToString(),
            DF.I16 => BitConverter.ToInt16(d, (int)(recOff + f.Off)).ToString(),
            DF.U32 => BitConverter.ToUInt32(d, (int)(recOff + f.Off)).ToString(),
            DF.I32 => BitConverter.ToInt32(d, (int)(recOff + f.Off)).ToString(),
            DF.F32 => BitConverter.ToSingle(d, (int)(recOff + f.Off)).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            DF.FT => (BitConverter.ToInt32(d, (int)(recOff + f.Off)) / 4096f).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            DF.RGBA => $"{d[recOff + f.Off]:x2}{d[recOff + f.Off + 1]:x2}{d[recOff + f.Off + 2]:x2}{d[recOff + f.Off + 3]:x2}",
            _ => "?",
        };

        /// <summary>Write a typed datum field. Returns false on parse/range
        /// failure (caller reports; nothing is mutated).</summary>
        public static bool WriteField(byte[] d, long recOff, DField f, string val)
        {
            val = val.Trim();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            int o = (int)(recOff + f.Off);
            switch (f.Type)
            {
                case DF.U8:
                    if (!int.TryParse(val, System.Globalization.NumberStyles.Integer, inv, out int u8) || u8 < 0 || u8 > 255) return false;
                    d[o] = (byte)u8; return true;
                case DF.I8:
                    if (!int.TryParse(val, System.Globalization.NumberStyles.Integer, inv, out int i8) || i8 < -128 || i8 > 127) return false;
                    d[o] = (byte)i8; return true;
                case DF.U16:
                    if (!int.TryParse(val, System.Globalization.NumberStyles.Integer, inv, out int u16) || u16 < 0 || u16 > 65535) return false;
                    BitConverter.GetBytes((ushort)u16).CopyTo(d, o); return true;
                case DF.I16:
                    if (!int.TryParse(val, System.Globalization.NumberStyles.Integer, inv, out int i16) || i16 < -32768 || i16 > 32767) return false;
                    BitConverter.GetBytes((short)i16).CopyTo(d, o); return true;
                case DF.U32:
                    if (!uint.TryParse(val, System.Globalization.NumberStyles.Integer, inv, out uint u32)) return false;
                    BitConverter.GetBytes(u32).CopyTo(d, o); return true;
                case DF.I32:
                    if (!int.TryParse(val, System.Globalization.NumberStyles.Integer, inv, out int i32)) return false;
                    BitConverter.GetBytes(i32).CopyTo(d, o); return true;
                case DF.F32:
                    if (!float.TryParse(val, System.Globalization.NumberStyles.Float, inv, out float f32)) return false;
                    BitConverter.GetBytes(f32).CopyTo(d, o); return true;
                case DF.FT:
                    if (!float.TryParse(val, System.Globalization.NumberStyles.Float, inv, out float ft)) return false;
                    BitConverter.GetBytes((int)MathF.Round(ft * 4096f)).CopyTo(d, o); return true;
                case DF.RGBA:
                    var hex = val.TrimStart('#');
                    if (hex.Length != 8 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, inv, out uint rgba)) return false;
                    d[o] = (byte)(rgba >> 24); d[o + 1] = (byte)(rgba >> 16);
                    d[o + 2] = (byte)(rgba >> 8); d[o + 3] = (byte)rgba; return true;
                default: return false;
            }
        }

        /// <summary>Enumerate every color field in color-carrying datums —
        /// one entry per RGBA field per record.</summary>
        public static IEnumerable<ColorEntry> ColorDatumEntries(byte[] d,
            int pppOffs, int[]? funcMap = null, bool synthEmitters = false)
        {
            Layout l;
            try { l = Parse(d, pppOffs, synthEmitters); }
            catch { yield break; }
            for (int bi = 0; bi < l.Behaviors.Count; bi++)
            {
                var b = l.Behaviors[bi];
                for (int pi = 0; pi < b.Programs.Count; pi++)
                {
                    var prog = b.Programs[pi];
                    for (int ii = 0; ii < prog.Instructions.Count; ii++)
                    {
                        var ins = prog.Instructions[ii];
                        uint op = ins.Opcode;
                        if (funcMap != null && op < funcMap.Length)
                            op = (uint)funcMap[op];
                        if (!ColorOps.TryGetValue(op, out var spec)) continue;
                        long eo = b.Offs + ins.DataOff;
                        for (int ei = 0; ei < 4096 && eo + spec.Size <= d.Length; ei++, eo += spec.Size)
                        {
                            if (BitConverter.ToUInt32(d, (int)eo) == 0xFFFFF000) break;
                            foreach (var (name, fo) in spec.Fields)
                                yield return new ColorEntry(bi, pi, ii, op,
                                    spec.Kind, ei, name, eo + fo);
                        }
                    }
                }
            }
        }

        public readonly record struct EmitEntry(int Behavior, int Program,
            int Instr, uint Op, string Kind, int Entry, long Off);

        /// <summary>Enumerate every emit datum entry in a PPP container —
        /// the patch surface for persistent count/period edits. Entries are
        /// spec.Size-strided records terminated by a 0xFFFFF000 t sentinel.</summary>
        public static IEnumerable<EmitEntry> EmitDatumEntries(byte[] d,
            int pppOffs, int[]? funcMap = null, bool synthEmitters = false)
        {
            Layout l;
            try { l = Parse(d, pppOffs, synthEmitters); }
            catch { yield break; }
            for (int bi = 0; bi < l.Behaviors.Count; bi++)
            {
                var b = l.Behaviors[bi];
                for (int pi = 0; pi < b.Programs.Count; pi++)
                {
                    var prog = b.Programs[pi];
                    for (int ii = 0; ii < prog.Instructions.Count; ii++)
                    {
                        var ins = prog.Instructions[ii];
                        uint op = ins.Opcode;
                        // magic bins store a 40-byte-record index, not the
                        // canonical op — remap through the funcMap
                        if (funcMap != null && op < funcMap.Length)
                            op = (uint)funcMap[op];
                        if (!EmitOps.TryGetValue(op, out var spec)) continue;
                        long eo = b.Offs + ins.DataOff;
                        for (int ei = 0; ei < 4096 && eo + spec.Size <= d.Length; ei++, eo += spec.Size)
                        {
                            if (BitConverter.ToUInt32(d, (int)eo) == 0xFFFFF000) break;
                            yield return new EmitEntry(bi, pi, ii, op, spec.Kind, ei, eo);
                        }
                    }
                }
            }
        }
    }

    public static string OpName(uint op) =>
        OpNames.TryGetValue(op, out var n) ? n : "UNK_0x" + op.ToString("X");

    public sealed record EmitterSpec(double[] Pos, double[] Euler, double[] Scale,
        int Delay, int Behavior, float MaxDist, float Height, float Width,
        int Id, int G, int Billboard, int EulerOrder);

    public sealed record Instruction(uint Opcode, string Name, int DatumSize, uint DataOff, uint IndexOff);

    public sealed record Program(int Flags, int Start, int Lifetime, int LoopStart,
        int LoopEnd, int InstrCount, List<Instruction> Instructions);

    public sealed record Behavior(long Offs, int Lifetime, bool IgnoreLifetime,
        int FuncCount, int ProgramCount, List<Program> Programs);

    public sealed record GeoEntry(int PointCount, uint PointStart, uint Start);

    public sealed record Pattern(int GeoIndex, int IndexCount, uint IndexStart);

    public sealed record Layout(int Offs, int EmitterCount, int BehaviorCount,
        int GeoCount, int FlipbookCount, int PatternCount, uint BehaviorStart,
        uint GeometryOffs, uint FlipbookOffs, uint PatternOffs, List<EmitterSpec> Emitters,
        List<Behavior> Behaviors, List<GeoEntry> Geometry, List<Pattern> Patterns);

    static uint U32(byte[] d, long o) => BitConverter.ToUInt32(d, (int)o);
    static ushort U16(byte[] d, long o) => BitConverter.ToUInt16(d, (int)o);
    static int I32(byte[] d, long o) => BitConverter.ToInt32(d, (int)o);
    static float F32(byte[] d, long o) => BitConverter.ToSingle(d, (int)o);

    static double[] Vec3F(byte[] d, long o) => new[] { (double)F32(d, o), F32(d, o + 4), F32(d, o + 8) };
    static double[] Vec3I(byte[] d, long o) => new[] { (double)I32(d, o), I32(d, o + 4), I32(d, o + 8) };

    /// <summary>Validates that a PPP container plausibly begins at offs:
    /// counts bounded, emitter table in bounds, section offsets ordered
    /// and inside the file. synthEmitters: magic/actor layout replaces the
    /// 0x50-byte emitter records with 0x10-byte scale records, one per
    /// behavior (particle.ts magic path). Returns an error string or null.</summary>
    public static string? Validate(byte[] d, int offs, bool synthEmitters = false)
    {
        if (offs < 0 || offs + 0x20 > d.Length)
            return $"offset 0x{offs:X} fora do arquivo ({d.Length} bytes)";
        int emitterCount = U16(d, offs + 0x04);
        int behaviorCount = U16(d, offs + 0x06);
        int geoCount = U16(d, offs + 0x08);
        int flipbookCount = U16(d, offs + 0x0A);
        int patternCount = U16(d, offs + 0x0C);
        if (emitterCount > 2048 || behaviorCount > 2048 || geoCount > 2048
            || flipbookCount > 2048 || patternCount > 2048)
            return $"counts implausíveis e={emitterCount} b={behaviorCount} g={geoCount} f={flipbookCount} p={patternCount} — offset provavelmente errado";
        long emittersEnd = synthEmitters
            ? offs + 0x20L + behaviorCount * 0x10
            : offs + 0x20L + emitterCount * 0x50;
        if (emittersEnd > d.Length)
            return $"tabela de emitters ultrapassa EOF (+0x{emittersEnd:X} > 0x{d.Length:X})";
        uint bs = U32(d, offs + 0x10) + (uint)offs;
        uint go = U32(d, offs + 0x14) + (uint)offs;
        uint fo = U32(d, offs + 0x18) + (uint)offs;
        if (behaviorCount > 0 && (bs < emittersEnd || bs >= d.Length))
            return $"behaviorStart 0x{bs:X} fora de [emittersEnd 0x{emittersEnd:X}, EOF 0x{d.Length:X})";
        if (geoCount > 0 && (go < bs || go >= d.Length))
            return $"geometry 0x{go:X} fora de [behaviorStart, EOF)";
        if (fo < go || fo > d.Length)
            return $"flipbook 0x{fo:X} fora de [geometry, EOF]";
        return null;
    }

    /// <summary>Parses the PPP container at the given offset (0 for standalone
    /// particle bins; the MAP1 +0x38 slot points at an in-file offset).
    /// Caller should run Validate() first — offsets into the middle of a bin
    /// decode garbage otherwise. synthEmitters: magic/actor bins carry no
    /// emitter records; one implicit emitter per behavior is synthesized
    /// from the 0x10-byte scale table at +0x20.</summary>
    public static Layout Parse(byte[] d, int offs = 0, bool synthEmitters = false)
    {
        int emitterCount = U16(d, offs + 0x04);
        int behaviorCount = U16(d, offs + 0x06);
        int geoCount = U16(d, offs + 0x08);
        int flipbookCount = U16(d, offs + 0x0A);
        int patternCount = U16(d, offs + 0x0C);
        uint behaviorStartOffs = U32(d, offs + 0x10) + (uint)offs;
        uint geometryOffs = U32(d, offs + 0x14) + (uint)offs;
        uint flipbookOffs = U32(d, offs + 0x18) + (uint)offs;
        uint patternOffs = U32(d, offs + 0x1C) + (uint)offs;

        var emitters = new List<EmitterSpec>();
        long p = offs + 0x20;
        if (synthEmitters)
        {
            for (int i = 0; i < behaviorCount && p + 0x10 <= d.Length; i++, p += 0x10)
                emitters.Add(new EmitterSpec(
                    new double[3], new double[3], Vec3F(d, p),
                    0, i, 0, 0, 0, 0, 0, 0, 0));
        }
        else for (int i = 0; i < emitterCount && p + 0x50 <= d.Length; i++, p += 0x50)
        {
            emitters.Add(new EmitterSpec(
                Vec3F(d, p + 0x00), Vec3I(d, p + 0x10), Vec3F(d, p + 0x20),
                I32(d, p + 0x30), I32(d, p + 0x34), F32(d, p + 0x38), F32(d, p + 0x3C),
                F32(d, p + 0x40), U16(d, p + 0x44), U16(d, p + 0x46),
                d[p + 0x4A], d[p + 0x4B]));
        }

        var behaviors = new List<Behavior>();
        for (int i = 0; i < behaviorCount; i++)
        {
            long bo = behaviorStartOffs + 4L * i;
            if (bo + 4 > d.Length) break;
            uint behaviorOffs = U32(d, bo) + (uint)offs;
            if (behaviorOffs + 0x10 > d.Length) break;
            int lifetime = (int)(U32(d, behaviorOffs) / 0x1000);
            bool ignoreLifetime = d[behaviorOffs + 0x04] != 0;
            uint funcOffs = U32(d, behaviorOffs + 0x08) + behaviorOffs;
            int funcCount = funcOffs + 4 <= d.Length ? (int)U32(d, funcOffs) : 0;
            uint programOffs = U32(d, behaviorOffs + 0x0C) + behaviorOffs;
            int programCount = programOffs + 4 <= d.Length ? (int)U32(d, programOffs) : 0;
            var programs = new List<Program>();
            for (int j = 0; j < programCount; j++)
            {
                long ps = programOffs + 4 + 4L * j;
                if (ps + 4 > d.Length) break;
                uint startOff = U32(d, ps);
                long po = behaviorOffs + startOff;
                if (po + 0x28 > d.Length) break;
                int flags = (int)U32(d, po + 0x0C);
                int start = (int)(U32(d, po + 0x10) >>> 0xC);
                int life = (int)(U32(d, po + 0x14) >>> 0xC);
                int loopStart = I32(d, po + 0x18) >> 0xC;
                int loopEnd = I32(d, po + 0x1C) >> 0xC;
                int instrCount = U16(d, po + 0x26);
                long io = po + 0x28;
                var instrs = new List<Instruction>();
                for (int k = 0; k < instrCount; k++, io += 0x10)
                {
                    if (io + 0x10 > d.Length) break;
                    uint raw = U32(d, io);
                    int datumSize = U16(d, io + 0x04);
                    uint dataOff = U32(d, io + 0x08);
                    uint indexOff = U32(d, io + 0x0C);
                    instrs.Add(new Instruction(raw, OpName(raw), datumSize, dataOff, indexOff));
                }
                programs.Add(new Program(flags, start, life, loopStart, loopEnd, instrCount, instrs));
            }
            behaviors.Add(new Behavior(behaviorOffs, lifetime, ignoreLifetime, funcCount, programCount, programs));
        }

        var geometry = new List<GeoEntry>();
        long gs = geometryOffs;
        for (int i = 0; i < geoCount && gs + 0x20 <= d.Length; i++, gs += 0x20)
        {
            int pointCount = (int)U32(d, gs + 0x00);
            uint pointStart = U32(d, gs + 0x14) + (uint)offs;
            uint start = U32(d, gs + 0x1C) + (uint)offs;
            geometry.Add(new GeoEntry(pointCount, pointStart, start));
        }

        var patterns = new List<Pattern>();
        for (int i = 0; i < patternCount; i++)
        {
            long o = patternOffs + 8L * i;
            if (o + 8 > d.Length) break;
            patterns.Add(new Pattern(U16(d, o), U16(d, o + 2), U32(d, o + 4) + (uint)offs));
        }

        return new Layout(offs, emitterCount, behaviorCount, geoCount, flipbookCount,
            patternCount, behaviorStartOffs, geometryOffs, flipbookOffs, patternOffs,
            emitters, behaviors, geometry, patterns);
    }

    public static string Describe(string path, byte[] d, int offs = 0)
    {
        var err = Validate(d, offs);
        if (err != null)
            return path + "  " + d.Length + " bytes\nERRO: " + err + "\n";
        var l = Parse(d, offs);
        var sb = new StringBuilder();
        sb.AppendLine(path + "  " + d.Length + " bytes  (PPP em +0x" + offs.ToString("x") + ")");
        sb.AppendLine("counts: emitters=" + l.EmitterCount + " behaviors=" + l.BehaviorCount
            + " geo=" + l.GeoCount + " flipbooks=" + l.FlipbookCount + " patterns=" + l.PatternCount);
        sb.AppendLine("offsets: behavior=0x" + l.BehaviorStart.ToString("x")
            + " geometry=0x" + l.GeometryOffs.ToString("x")
            + " flipbook=0x" + l.FlipbookOffs.ToString("x")
            + " pattern=0x" + l.PatternOffs.ToString("x"));

        for (int i = 0; i < l.Emitters.Count; i++)
        {
            var e = l.Emitters[i];
            sb.AppendLine("emitter[" + i + "] pos=(" + e.Pos[0].ToString("0.##") + "," + e.Pos[1].ToString("0.##")
                + "," + e.Pos[2].ToString("0.##") + ") euler=(" + e.Euler[0] + "," + e.Euler[1] + "," + e.Euler[2]
                + ") scale=(" + e.Scale[0].ToString("0.###") + "," + e.Scale[1].ToString("0.###") + "," + e.Scale[2].ToString("0.###")
                + ") delay=" + e.Delay + " behavior=" + e.Behavior + " id=" + e.Id
                + " billboard=" + e.Billboard + " eulerOrder=" + e.EulerOrder);
        }

        for (int i = 0; i < l.Behaviors.Count; i++)
        {
            var b = l.Behaviors[i];
            sb.AppendLine("behavior[" + i + "] lifetime=" + b.Lifetime + " ignoreLifetime=" + b.IgnoreLifetime
                + " funcs=" + b.FuncCount + " programs=" + b.ProgramCount);
            for (int j = 0; j < b.Programs.Count; j++)
            {
                var pr = b.Programs[j];
                sb.AppendLine("  program[" + j + "] flags=0x" + pr.Flags.ToString("x") + " start=" + pr.Start
                    + " lifetime=" + pr.Lifetime + " loop=" + pr.LoopStart + ".." + pr.LoopEnd
                    + " instrs=" + pr.InstrCount);
                var counts = new SortedDictionary<string, int>();
                foreach (var ins in pr.Instructions)
                    counts[ins.Name] = counts.TryGetValue(ins.Name, out var c) ? c + 1 : 1;
                var parts2 = new List<string>();
                foreach (var kv in counts) parts2.Add(kv.Key + " x" + kv.Value);
                sb.AppendLine("    ops: " + string.Join(" ", parts2));
            }
        }

        for (int i = 0; i < l.Geometry.Count; i++)
        {
            var g = l.Geometry[i];
            sb.AppendLine("geo[" + i + "] points=" + g.PointCount + " pointStart=0x" + g.PointStart.ToString("x")
                + " start=0x" + g.Start.ToString("x"));
        }
        for (int i = 0; i < l.Patterns.Count; i++)
        {
            var pt = l.Patterns[i];
            sb.AppendLine("pattern[" + i + "] geo=" + pt.GeoIndex + " indices=" + pt.IndexCount);
        }
        return sb.ToString();
    }
}

/// <summary>Sprite-texture containers — the "common_textures.bin" block
/// and the particle bin's +0x18 section share this layout (noclip
/// uploadSpriteTextures): u32@+0x08 sprite spec table, u32@+0x0C CLUT
/// spec table, u32@+0x3C dataStart (relative to the container start);
/// dataStart+0x44 u16 spriteCount, +0x46 clutCount, +0x08/+0x0C u32
/// image-offset arrays. Sprite specs are 0x20 bytes; CLUT specs 0x10.
/// These feed a DEDICATED GS memory map (particleMap) — map PPP sprites
/// never live in the level-texture GS state.</summary>
public static class Sprites
{
    /// <summary>Upload every sprite + CLUT of a container into gs.
    /// start = file offset of the container (0 for a standalone file).</summary>
    public static void Upload(byte[] d, int start, Gs gs)
    {
        if (start + 0x40 > d.Length) return;
        int dataOff = start + (int)BitConverter.ToUInt32(d, start + 0x3C);
        if (dataOff + 0x48 > d.Length) return;
        int spriteCount = BitConverter.ToUInt16(d, dataOff + 0x44);
        int clutCount = BitConverter.ToUInt16(d, dataOff + 0x46);
        int imgOffs = (int)BitConverter.ToUInt32(d, dataOff + 0x08);
        int specOffs = start + (int)BitConverter.ToUInt32(d, start + 0x08);
        for (int i = 0; i < spriteCount; i++, specOffs += 0x20, imgOffs += 4)
        {
            if (imgOffs + 4 > d.Length || specOffs + 0x20 > d.Length) break;
            int imageStart = (int)BitConverter.ToUInt32(d, dataOff + imgOffs);
            if (imageStart == 0) continue;
            int addr = (int)BitConverter.ToUInt32(d, specOffs) & 0x3FFF;
            int x = ((sbyte)d[specOffs + 0x08]) << 4;
            int y = ((sbyte)d[specOffs + 0x09]) << 4;
            int bufW = d[specOffs + 0x0A] >> 2;
            int w = BitConverter.ToUInt16(d, specOffs + 0x0C);
            int h = BitConverter.ToUInt16(d, specOffs + 0x0E);
            int psm = (sbyte)d[specOffs + 0x18];
            int src = dataOff + imageStart;
            if (src >= d.Length || w <= 0 || h <= 0) continue;
            try { gs.Upload(psm, addr, bufW, x, y, w, h, d, src); }
            catch { }
        }
        imgOffs = (int)BitConverter.ToUInt32(d, dataOff + 0x0C);
        specOffs = start + (int)BitConverter.ToUInt32(d, start + 0x0C);
        for (int i = 0; i < clutCount; i++, specOffs += 0x10, imgOffs += 4)
        {
            if (imgOffs + 4 > d.Length || specOffs + 0x10 > d.Length) break;
            int imageStart = (int)BitConverter.ToUInt32(d, dataOff + imgOffs);
            if (imageStart == 0) continue;
            int addr = BitConverter.ToUInt16(d, specOffs) & 0x3FFF;
            int src = dataOff + imageStart;
            if (src >= d.Length) continue;
            try { gs.Upload(0, addr, 1, 0, 0, 0x10, 0x10, d, src); }
            catch { }
        }
    }
}
