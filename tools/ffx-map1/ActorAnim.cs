// Actor animation (.erl) decoder — port of noclip_reference bin.ts
// parseAnimation + actor.ts AnimationState evaluation.
// Groups -> animations -> segments -> tracks -> 9-channel curves per
// bone node (eulerXYZ, posXYZ, scaleXYZ). Angles: s16 where 0x800=pi.
// Scales: 0x1000 = 1.0. Curves are delta-encoded opcodes.
namespace FfxMap1;

public sealed class ActorAnim
{
    public required byte[] Data { get; init; }
    public string Path = "";
    int U8(long o) => Data[o];
    int I8(long o) => (sbyte)Data[o];
    int U16(long o) => Data[o] | Data[o + 1] << 8;
    int I16(long o) => (short)(Data[o] | Data[o + 1] << 8);
    uint U32(long o) => (uint)(Data[o] | Data[o + 1] << 8 | Data[o + 2] << 16 | Data[o + 3] << 24);

    public static ActorAnim Load(string path) => new ActorAnim { Data = File.ReadAllBytes(path), Path = path, Groups = new() };

    /// <summary>Curve = constant value, or Int16 array (one per frame).</summary>
    public readonly record struct Curve(bool IsArray, short[]? Values, short Const)
    {
        public short At(int i) => IsArray ? Values![Math.Min(i, Values!.Length - 1)] : Const;
    }

    public sealed class BoneCurve
    {
        public Curve? EulerX, EulerY, EulerZ, PosX, PosY, PosZ, ScaleX, ScaleY, ScaleZ;
    }

    public sealed class Track
    {
        public required BoneCurve[] Curves;
        public required ushort[] Times;
        public required int Duration;
    }

    public sealed class Segment
    {
        public Track? Track;
        public int Start, End, Loops;
    }

    public sealed class Animation
    {
        public required int Id;
        public required List<Segment> Segments;
    }

    public sealed class Group
    {
        public required int Id;
        public required List<Animation> Animations;
    }

    public required List<Group> Groups { get; init; }

    long _typeStart, _streamOffs;

    Curve? ParseCurve(int duration, int index)
    {
        int shift = 2 * (index & 3);
        int type = (U8(_typeStart + (index >> 2)) >> shift) & 3;
        switch (type)
        {
            case 0: // Zero
                if (index % 9 < 6) return null;
                return new Curve(false, null, 0);
            case 1: // One
                if (index % 9 < 6) return new Curve(false, null, 0x1000);
                return null;
            case 2: // Const
                _streamOffs += 2;
                return new Curve(false, null, (short)I16(_streamOffs - 2));
            case 3: // Curve
                long end = U16(_streamOffs) + _streamOffs;
                _streamOffs += 2;
                var curve = new short[duration];
                int idx = 0, value = 0, inc = 0, wait = 0;
                while (_streamOffs < end || wait > 0)
                {
                    if (wait > 0) wait--;
                    else
                    {
                        int op = U8(_streamOffs++);
                        if ((op & 0x80) == 0) inc = (op << 25) >> 25;
                        else if ((op & 0x40) == 0) wait = op & 0x3F;
                        else { int extra = I8(_streamOffs++); inc = (op & 0x3F) | (extra << 6); }
                    }
                    value += inc;
                    if (idx >= duration) break;
                    curve[idx++] = (short)value;
                }
                return new Curve(true, curve, 0);
        }
        return null;
    }

    static int AngleDist(int a, int b, int wrap) => Math.Abs(((a - b + wrap / 2) % wrap + wrap) % wrap - wrap / 2);

    void FixupAngles(Curve? xc, Curve? yc, Curve? zc)
    {
        if (!(xc is { IsArray: true } x && yc is { IsArray: true } y && zc is { IsArray: true } z)) return;
        var xs = x.Values!; var ys = y.Values!; var zs = z.Values!;
        if (xs.Length != ys.Length || xs.Length != zs.Length) return;
        bool flip = false;
        int px = xs[0], py = ys[0], pz = zs[0];
        for (int i = 1; i < xs.Length; i++)
        {
            int cx = xs[i], cy = ys[i], cz = zs[i];
            double main = Math.Sqrt(AngleDist(cx, px, 0x1000) * AngleDist(cx, px, 0x1000)
                + AngleDist(cy, py, 0x1000) * AngleDist(cy, py, 0x1000)
                + AngleDist(cz, pz, 0x1000) * AngleDist(cz, pz, 0x1000));
            double alt = Math.Sqrt(AngleDist(cx + 0x800, px, 0x1000) * AngleDist(cx + 0x800, px, 0x1000)
                + AngleDist(0x800 - cy, py, 0x1000) * AngleDist(0x800 - cy, py, 0x1000)
                + AngleDist(cz + 0x800, pz, 0x1000) * AngleDist(cz + 0x800, pz, 0x1000));
            if (alt < main) flip = !flip;
            px = cx; py = cy; pz = cz;
            if (flip) { xs[i] = (short)(cx + 0x800); ys[i] = (short)(0x800 - cy); zs[i] = (short)(cz + 0x800); }
        }
    }

    public static ActorAnim Parse(byte[] data)
    {
        var a = new ActorAnim { Data = data, Path = "", Groups = new() };
        int groupCount = (int)a.U32(4);
        long groupOffs = a.U32(0xC);
        for (int g = 0; g < groupCount; g++)
        {
            int gid = (int)a.U32(groupOffs + 4);
            int seqCount = a.U16(groupOffs + 8);
            int trackCount = a.U16(groupOffs + 0xA);
            long seqStart = a.U32(groupOffs + 0xC);
            long trackStart = a.U32(groupOffs + 0x10);
            groupOffs += 0x14;

            var tracks = new List<Track>();
            long trackOffs = trackStart;
            for (int i = 0; i < trackCount; i++)
            {
                int timeCount = a.U16(trackOffs + 4);
                long timeStart = a.U32(trackOffs + 8);
                var times = new ushort[timeCount];
                for (int k = 0; k < timeCount; k++) times[k] = (ushort)a.U16(timeStart + 2 * k);
                long inner = a.U32(trackOffs + 0xC);
                trackOffs += 0x10;
                int duration = a.U16(timeStart + 2);
                int entryCount = a.U16(inner + 2);
                a._typeStart = a.U32(inner + 8) + inner;
                a._streamOffs = a.U32(inner + 0xC) + inner;

                var curves = new BoneCurve[entryCount];
                for (int j = 0; j < entryCount; j++)
                {
                    var c = new BoneCurve();
                    c.EulerX = a.ParseCurve(duration, 9 * j + 0);
                    c.EulerY = a.ParseCurve(duration, 9 * j + 1);
                    c.EulerZ = a.ParseCurve(duration, 9 * j + 2);
                    c.PosX = a.ParseCurve(duration, 9 * j + 3);
                    c.PosY = a.ParseCurve(duration, 9 * j + 4);
                    c.PosZ = a.ParseCurve(duration, 9 * j + 5);
                    c.ScaleX = a.ParseCurve(duration, 9 * j + 6);
                    c.ScaleY = a.ParseCurve(duration, 9 * j + 7);
                    c.ScaleZ = a.ParseCurve(duration, 9 * j + 8);
                    curves[j] = c;
                }
                tracks.Add(new Track { Curves = curves, Times = times, Duration = duration });
            }

            var anims = new List<Animation>();
            long seqOffs = seqStart;
            for (int i = 0; i < seqCount; i++)
            {
                int id = a.U16(seqOffs);
                int codeLen = a.U16(seqOffs + 6);
                long codeStart = a.U32(seqOffs + 0xC);
                long codeOffs = codeStart;
                int lastLoopCount = -1;
                var segs = new List<Segment>();
                while (codeStart != 0 && codeOffs < codeStart + codeLen)
                {
                    int op = a.U8(codeOffs++);
                    switch (op)
                    {
                        case 1: // LOAD
                            int tr = a.U16(codeOffs);
                            int loops = a.U16(codeOffs + 2);
                            int start = tracks[tr].Times[a.U16(codeOffs + 4)];
                            int end = tracks[tr].Times[a.U16(codeOffs + 6)];
                            lastLoopCount = loops;
                            codeOffs += 8;
                            var prev = segs.Count > 0 ? segs[^1] : null;
                            if (loops == 1 && prev is { Track: not null, Loops: 1 } && tracks[tr] == prev.Track && prev.End == start - 1)
                                prev.End = end;
                            else
                                segs.Add(new Segment { Track = tracks[tr], Loops = loops, Start = start, End = end });
                            break;
                        case 3: // SLEEP
                            int sleep = a.U16(codeOffs); codeOffs += 2;
                            segs.Add(new Segment { Loops = 1, Start = 0, End = sleep });
                            break;
                        case 0: case 5: break; // END / END_2
                        case 2: lastLoopCount = -1; break; // WAIT_FLAG
                        case 6: // WAIT_COUNT
                            lastLoopCount = -1;
                            int next = a.U8(codeOffs);
                            int nextVal = a.U16(codeOffs + 1);
                            bool bad = next == 1 && nextVal < tracks.Count && segs.Count > 0
                                && tracks[nextVal] == segs[^1].Track;
                            if (!bad) codeOffs += 2;
                            break;
                        default:
                            throw new InvalidDataException($"anim seq op {op} @ {codeOffs - 1:x}");
                    }
                }
                anims.Add(new Animation { Id = id, Segments = segs });
                if (segs.Count == 1 && segs[0].Track is { } t)
                    foreach (var c in t.Curves)
                        a.FixupAngles(c.EulerX, c.EulerY, c.EulerZ);
                seqOffs += 0x10;
            }
            a.Groups.Add(new Group { Id = gid, Animations = anims });
        }
        return a;
    }

    // ---- evaluation (actor.ts animValue + update) ----

    static float LerpAngle(short a, short b, float t)
    {
        int d = ((b - a + 0x800) % 0x1000 + 0x1000) % 0x1000 - 0x800;
        return a + d * t;
    }

    /// <summary>Sample one curve at fractional time t (start..end span).
    /// Returns (euler rad, pos raw, scale) value per channel type.</summary>
    static float Sample(Curve? c, int t, int endT, int channel)
    {
        if (c is not { } cv)
            return channel >= 6 ? 0x1000 : 0;
        if (cv.IsArray)
        {
            short from = cv.At(t), to = cv.At(endT);
            float ft = t - MathF.Floor(t) + (t % 1);
            float frac = t - MathF.Floor(t);
            return channel < 3 ? LerpAngle(from, to, frac) : from + (to - from) * frac;
        }
        return cv.Const;
    }

    /// <summary>Evaluate animation's first segment at integer frame time into
    /// boneState[9*node] {eulerXYZ rad?, posXYZ raw, scaleXYZ raw}.
    /// Angles come out as radians (value * pi/0x800); scale as raw/0x1000
    /// handled by caller via ScaleVal().</summary>
    public void EvalPose(Animation anim, float time, float[] state, ushort[]? boneMap = null)
    {
        var seg = anim.Segments[0];
        if (seg.Track is not { } track) return;
        float t = Math.Clamp(time, seg.Start, Math.Max(seg.Start, seg.End - 1));
        int ti = (int)t;
        int endT = ti + 1;
        if (endT > seg.End - 1) endT = seg.Start;
        for (int i = 0; i < track.Curves.Length; i++)
        {
            int idx = boneMap != null && i < boneMap.Length ? boneMap[i] : i;
            if (idx == 0xFFFF || 9 * idx + 8 >= state.Length) continue;
            var c = track.Curves[i];
            state[9 * idx + 0] = Sample(c.EulerX, ti, endT, 0) * MathF.PI / 0x800;
            state[9 * idx + 1] = Sample(c.EulerY, ti, endT, 1) * MathF.PI / 0x800;
            state[9 * idx + 2] = Sample(c.EulerZ, ti, endT, 2) * MathF.PI / 0x800;
            state[9 * idx + 3] = Sample(c.PosX, ti, endT, 3);
            state[9 * idx + 4] = Sample(c.PosY, ti, endT, 4);
            state[9 * idx + 5] = Sample(c.PosZ, ti, endT, 5);
            state[9 * idx + 6] = Sample(c.ScaleX, ti, endT, 6) / 0x1000f;
            state[9 * idx + 7] = Sample(c.ScaleY, ti, endT, 7) / 0x1000f;
            state[9 * idx + 8] = Sample(c.ScaleZ, ti, endT, 8) / 0x1000f;
        }
    }

    /// <summary>Find animation by 32-bit id: high word = group, low = anim id
    /// (last match wins, per AnimationState.resolve).</summary>
    public Animation? Resolve(int animId)
    {
        var g = Groups.FirstOrDefault(x => x.Id == (animId >> 16 & 0xFFFF) || x.Id == (animId >> 16));
        if (g == null) return null;
        for (int i = g.Animations.Count - 1; i >= 0; i--)
            if (g.Animations[i].Id == (animId & 0xFFFF)) return g.Animations[i];
        return null;
    }

    /// <summary>Bind-pose boneState for a model: euler, offset/scaleOff, scale
    /// — mirrors Actor constructor boneState fill.</summary>
    public static float[] BindState(ActorBin a)
    {
        var sc = a.GetScales();
        float off = sc?.Offset ?? 1;
        var bones = a.Bones();
        var st = new float[9 * bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            st[9 * i + 0] = bones[i].Ex; st[9 * i + 1] = bones[i].Ey; st[9 * i + 2] = bones[i].Ez;
            st[9 * i + 3] = bones[i].Ox / off; st[9 * i + 4] = bones[i].Oy / off; st[9 * i + 5] = bones[i].Oz / off;
            st[9 * i + 6] = bones[i].Sx; st[9 * i + 7] = bones[i].Sy; st[9 * i + 8] = bones[i].Sz;
        }
        return st;
    }
}
