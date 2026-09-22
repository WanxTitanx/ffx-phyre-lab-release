// MagicVm.cs — port of actor.ts MonsterMagicManager + MonsterMagicState +
// MonsterEmitter/processMonsterParticle from the FFX editor reference.
//
// Magic/actor bins carry a u16 "magic program" bytecode at
//   scriptStart = dataStart + u32(dataStart+0x04) + 0x10   (scriptCount = u16(dataStart+0x42))
// that the game interprets per frame to sequence emitter activation
// (op 0xDC), emitter placement (0xDD), basic sprite particles (0x8F),
// screen-space flipbooks (0x24/0x25), waits, forks, loops and vec math.
//
// Without this VM the preview hack (Sys.ScriptStartAll) fires every
// sentinel-start program at t=0, collapsing all phases into frame zero.
// With it, emitters spawn when the script says so, positioned/scaled by
// the script state.
//
// Deliberate omissions vs the reference (kept explicit, not silent):
//   - bones/actor attachment: no model in the particle pipeline, so
//     bone-sourced positions fall back to origin like noclip's model==null
//     path. ActorPos/ActorHeading/EffectLevel are settable.
//   - DripState (0x4A/0x53): state is tracked so scripts don't error, but
//     drip ribbons are not rendered yet.
//   - texture/stream ops (0x27/0x28/0x2B/0x84/0xAB): decoded for length
//     only — the GS upload path already runs at load time.

using System;
using System.Collections.Generic;

public sealed class MagicVm
{
    readonly short[] Data;
    readonly ParticleSim.Sys S;
    public readonly List<State> States = new();
    /// <summary>Tracker flags — the battle system sets these as the magic
    /// progresses; a preview can't, so they start all-set (waits pass).
    /// Ops that wait-for-clear still work since scripts also set/unset.</summary>
    public int Flags = -1;
    /// <summary>Battle effect level (magic tier). Preview defaults to max
    /// so level-gated branches take the full-effect path.</summary>
    public int EffectLevel = 3;
    public float[] ActorPos = new float[3];
    public float ActorHeading; // radians
    public float[] ClearColor = { 1, 1, 1 };
    float _toNext = 1;
    static readonly Random Rng = new();

    public bool Alive => States.Count > 0;
    public int ProgLen => Data.Length;
    /// <summary>Set when op 0xDC spawned at least one emitter — proves the
    /// script reached its particle phase.</summary>
    public bool EverSpawned;
    /// <summary>Frames without any emitter spawn. Scripts stuck on external
    /// battle triggers stall forever; after ~10s the preview gives up and
    /// falls back to the static emitters.</summary>
    public int StallFrames;

    public sealed class Thread
    {
        public int Ptr = -1, Timer, Saved;
        public bool Render;
    }

    public sealed class State
    {
        public float[] Pos = new float[3];
        public float[][] Vecs = new float[6][];
        public State? Parent;
        public int Origin;
        public bool Errored, Alive = true;
        public int Flags;
        public int[] LoopCounts = new int[4];
        public float Scale = 1;
        public int VecOp;
        public bool SetPos = true;
        public State? VecBaseState;
        public State?[] SavedChildren = new State?[8];
        public int MatrixSource;
        public bool AtCamera;
        public Thread[] Threads = new Thread[4];
        public MonsterEmitter? Basic;
        public ParticleSim.Emitter? Full;
        public FlipSt? Flip;
        public float DepthShift;
        public float[] Color = { 1, 1, 1, 1 };

        public State(int addr)
        {
            for (int i = 0; i < 6; i++) Vecs[i] = new float[4];
            for (int i = 0; i < 4; i++) Threads[i] = new Thread();
            Threads[0].Ptr = addr;
        }

        public void DoVecOp()
        {
            if (VecOp == 3) // ANGLE: cylindrical offset from base state
            {
                var p = VecBaseState?.Pos;
                if (p != null) Array.Copy(p, Pos, 3); else Array.Clear(Pos);
                float ang = Vecs[1][2] * (float)(Math.PI * 2 / 0x1000);
                float r = Vecs[1][0] / 16f;
                Pos[0] += r * MathF.Sin(ang);
                Pos[1] += Vecs[1][1] / 16f;
                Pos[2] += r * MathF.Cos(ang);
            }
            else
                for (int i = 0; i < 3; i++) Pos[i] = Vecs[1][i] / 16f;
        }
    }

    /// <summary>FlipbookState port: looping flipbook anchored at the
    /// state's pos; scale/alpha optionally driven by vec w components.</summary>
    public sealed class FlipSt
    {
        public int Flipbook, AlphaSrc = -1, ScaleSrc = -1, Rotation;
        public float Frame;
        public FlipSt(int fb, int v0, int v1, int rot)
        {
            Flipbook = fb; Rotation = rot;
            if (v0 == 1) ScaleSrc = 0; else if (v0 == 2) AlphaSrc = 0;
            if (v1 == 1) ScaleSrc = 1; else if (v1 == 2) AlphaSrc = 1;
        }
        public void Render(State st, List<ParticleSim.DrawItem> outp,
            ParticleSim.Sys sys, float dt)
        {
            int fi = Flipbook + Math.Max(0, sys.L.ExtraFlipbookIndex);
            if (fi < 0 || fi >= sys.L.Flipbooks.Count) return;
            var fb = sys.L.Flipbooks[fi];
            if (fb.Frames.Count == 0) return;
            float scale = ScaleSrc >= 0 ? st.Vecs[ScaleSrc][3] / 0x1000f : 1;
            var col = (float[])st.Color.Clone();
            if (AlphaSrc >= 0) col[3] = st.Vecs[AlphaSrc][3] / 0x80f;
            var pose = ParticleSim.Identity();
            pose[0] = scale; pose[5] = -scale;
            pose[12] = st.Pos[0]; pose[13] = st.Pos[1]; pose[14] = st.Pos[2];
            sys.RenderFlipbook(fb, (int)Frame % fb.Frames.Count,
                col, pose, outp);
            Frame += Math.Min(dt, 1);
            if (Frame >= fb.Frames.Count) Frame -= fb.Frames.Count;
        }
    }

    /// <summary>Simple script-driven sprite particles (op 0x8F). Each
    /// particle runs a second mini-bytecode (processMonsterParticle) at
    /// emit time to set pos/vel/color/flipbook/lifetime.</summary>
    public sealed class MonsterEmitter
    {
        public readonly MonsterParticle[] Parts;
        int _next;
        public MonsterEmitter(State owner, int count)
        {
            Owner = owner;
            Parts = new MonsterParticle[Math.Max(1, count)];
            for (int i = 0; i < Parts.Length; i++) Parts[i] = new MonsterParticle(this);
        }
        public State Owner;
        public void Emit(int ptr, float[] pos)
        {
            var p = Parts[_next]; _next = (_next + 1) % Parts.Length;
            p.Ptr = ptr; Array.Copy(pos, p.Pos, 3);
            Array.Clear(p.Vel); Array.Clear(p.Accel);
            p.Lifetime = 0; p.T = 0;
        }
    }

    public enum PosMode { Default = 0, Radial = 1, Bone = 2 }

    public sealed class MonsterParticle
    {
        public MonsterEmitter Emitter;
        public int Ptr = -1;
        public float[] Pos = new float[3], Vel = new float[3], Accel = new float[3], Center = new float[3];
        public float Lifetime, T;
        public float[] Color = { .5f, .5f, .5f, 1 }, ColorVel = new float[4];
        public float[] ScaleAngle = { 1, 0, 0, 0 };
        public int Flipbook;
        public bool ShouldLoop;
        public PosMode Mode;
        public MonsterParticle(MonsterEmitter e) { Emitter = e; }
    }

    public MagicVm(short[] data, ParticleSim.Sys sys, int effectLevel = 0)
    {
        Data = data; S = sys; EffectLevel = effectLevel;
    }

    /// <summary>Parse the magic program out of a bin's dataStart and attach
    /// a VM that drives the (now-inert) base emitters via op 0xDC.
    /// scriptCount = u16(dataStart+0x42); program = shorts at
    /// dataStart + u32(dataStart+0x04) + 0x10.</summary>
    public static bool Attach(byte[] d, int dataStart, ParticleSim.Sys sys)
    {
        bool dbg = Environment.GetEnvironmentVariable("FFX_VM") != null;
        if (dataStart + 0x54 > d.Length) return false;
        int sc = BitConverter.ToUInt16(d, dataStart + 0x42);
        int so = BitConverter.ToInt32(d, dataStart + 0x04);
        if (dbg) Console.Error.WriteLine($"  vm.attach ds=0x{dataStart:x} sc={sc} so=0x{so:x} -> prog@0x{dataStart + so + 0x10:x}");
        if (sc <= 0 || so <= 0) return false;
        long po = (long)dataStart + so + 0x10;
        if (po < 0 || po >= d.Length - 2) return false;
        int n = (int)Math.Min(d.Length - po, 0x40000) / 2;
        var prog = new short[n];
        Buffer.BlockCopy(d, (int)po, prog, 0, n * 2);
        sys.Vm = new MagicVm(prog, sys);
        foreach (var e in sys.Emitters) e.Active = false;
        return true;
    }

    /// <summary>Entry 0 (the only one the reference uses for magic bins).</summary>
    public void StartEffect(int slot = 0)
    {
        if (Data.Length == 0) return;
        int first = Data[0];
        if (first == 0 || first == 0x7F) return;
        States.Add(new State(0));
    }

    /// <summary>Per-frame update: ticks script threads once per frame,
    /// then runs basic emitters and screen flipbooks.</summary>
    public void Update(float dt, List<ParticleSim.DrawItem> outp)
    {
        _toNext -= dt;
        if (_toNext <= 0) { _toNext += 1; Tick(); }
        if (!EverSpawned) StallFrames++;
        foreach (var s in States)
        {
            if (s.Threads[0].Ptr < 0) continue;
            if (s.Basic != null) RunBasicEmitter(s.Basic, dt, outp);
            if (s.Full != null)
            {
                // matrixSource<0 walks up the parent chain; atCamera pins
                // the emitter to the camera position
                if (s.MatrixSource < 0)
                {
                    var src = s; int count = s.MatrixSource;
                    while (count < 0 && src.Parent != null) { src = src.Parent; count++; }
                    if (count == 0 && src.AtCamera)
                    {
                        s.Full.Pose[12] = S.CamPos[0];
                        s.Full.Pose[13] = S.CamPos[1];
                        s.Full.Pose[14] = S.CamPos[2];
                    }
                }
            }
            s.Flip?.Render(s, outp, S, dt);
        }
    }

    sealed class Ctx
    {
        public State State = null!;
        public Thread Thread = null!;
        public bool GotEndAll;
        public int Flags;
    }
    readonly Ctx _ctx = new();

    void Tick()
    {
        _ctx.State = States.Count > 0 ? States[0] : null!;
        _ctx.Thread = null!;
        _ctx.GotEndAll = false;
        _ctx.Flags = Flags;
        bool anyDead = false;
        var add = new List<State>();
        foreach (var state in States)
        {
            if (state.Errored) continue;
            for (int phase = 0; phase < 2; phase++)
            {
                bool isRender = phase == 1;
                foreach (var th in state.Threads)
                {
                    if (th.Render != isRender || th.Ptr < 0) continue;
                    if (th.Timer > 0) th.Timer--;
                    if (th.Timer == 0)
                    {
                        _ctx.State = state; _ctx.Thread = th;
                        int guard = 0;
                        while (th.Timer == 0 && th.Ptr >= 0 && guard++ < 10000)
                        {
                            int prev = th.Ptr;
                            int step = ProcessOp(th.Ptr, state, th, add);
                            if (step < 0) { state.Errored = true; break; }
                            if (th.Ptr == prev && th.Timer == 0) th.Ptr += step;
                        }
                        if (guard >= 10000) state.Errored = true;
                    }
                }
                if (!isRender)
                {
                    for (int j = 3; j >= 0; j--)
                        for (int c = 0; c < 4; c++) state.Vecs[j][c] += state.Vecs[j + 2][c];
                    if (state.SetPos) state.DoVecOp();
                }
            }
            if (!state.Alive) anyDead = true;
        }
        if (add.Count > 0) States.AddRange(add);
        if (_ctx.GotEndAll) States.Clear();
        else if (anyDead) States.RemoveAll(s => !s.Alive);
        Flags = _ctx.Flags;
    }

    float Comp(State s, int idx) => s.Vecs[idx >> 2][idx & 3];

    /// <summary>Spawn a full emitter for behavior index (0xDC). The magic
    /// layout synthesizes one emitter spec per behavior, so this maps 1:1
    /// onto S.SpecByBehavior.</summary>
    ParticleSim.Emitter? SpawnEmitter(int behavior)
    {
        if (S.SpecByBehavior == null || behavior < 0 || behavior >= S.SpecByBehavior.Count
            || behavior >= S.L.Behaviors.Count || S.SpecByBehavior[behavior] == null) return null;
        var e = new ParticleSim.Emitter(S.SpecByBehavior[behavior], S.L.Behaviors[behavior])
        { Active = true, WaitTimer = 0 };
        S.Emitters.Add(e);
        EverSpawned = true;
        return e;
    }

    public static bool Trace = Environment.GetEnvironmentVariable("FFX_VM_TRACE") != null;

    int ProcessOp(int start, State st, Thread th, List<State> add)
    {
        var d = Data;
        int offs = start;
        int b = d[offs++] & 0xFFFF;
        int op = b & 0xFF, mode = (int)((uint)b >> 12);
        if (Trace) Console.Error.WriteLine($"    @{start:x4} op={op:x2} mode={mode} base=0x{b:x4}");
        void Jump(int byteOffs) => th.Ptr += byteOffs / 2;
        switch (op)
        {
            case 0x00: case 0x7F: // end
                th.Ptr = -1;
                if (st.Threads[0].Ptr == -1) st.Alive = false;
                return 1;
            case 0x01: _ctx.GotEndAll = true; return 1;
            case 0x02: Jump(d[offs]); return 2;
            case 0x03: th.Saved = start + 2; Jump(d[offs]); return 2;
            case 0x04: th.Ptr = th.Saved; return 1;
            case 0x05:
                if (mode == 0) { _ctx.Flags |= d[offs]; return 2; }
                if (mode == 1) { _ctx.Flags &= ~d[offs]; return 2; }
                if (mode == 5) { if ((_ctx.Flags & d[offs]) == 0) th.Timer = 1; return 2; }
                if (mode == 6) { _ctx.Flags |= d[offs]; return 2; }
                return -1;
            case 0x09: // sleep
                th.Timer = (int)((uint)b >> 9); Jump(2); return 1;
            case 0x0A: case 0x0B: case 0x0C: case 0x0D: case 0x0E: case 0x0F:
            case 0x12: case 0x13: case 0x14: case 0x15: case 0x16: case 0x17:
            case 0xD6: case 0xD7: case 0xD8: case 0xD9: case 0xDA: case 0xDB:
            {
                // vec[index].comp(mask) = / += / += random — mode selects comps
                int vbase = op >= 0xD6 ? 0xD6 : op >= 0x12 ? 0x12 : 0x0A;
                int index = op - vbase;
                float scale = index >= 4 ? 0x1000 : index >= 2 ? 0x100 : 1;
                for (int i = 0; i < 4; i++)
                    if ((mode & (1 << i)) != 0)
                    {
                        float val = d[offs++] / scale;
                        if (vbase == 0x12) st.Vecs[index][i] += (float)(Rng.NextDouble() * 2 - 1) * val;
                        else if (vbase == 0xD6) st.Vecs[index][i] += val;
                        else st.Vecs[index][i] = val;
                    }
                return offs - start;
            }
            case 0x1D: // wait for parent death
                if (st.Parent != null && !st.Parent.Alive) { st.Alive = false; th.Ptr = -1; }
                else th.Timer = 1;
                return 2;
            case 0x1E: // start loop
            {
                int otherMode = (b & 0xE00) >> 9;
                if (otherMode == 0) { st.LoopCounts[mode] = d[offs]; return 2; }
                if (otherMode == 2) { st.LoopCounts[mode] = (int)(d[offs] + d[offs + 1] * Rng.NextDouble()); return 3; }
                return -1;
            }
            case 0x1F: // end loop
                if (--st.LoopCounts[mode] > 0) Jump(d[offs]);
                return 2;
            case 0x21: return mode == 1 ? 4 : -1; // tracker alloc
            case 0x22: return 1; // damage set
            case 0x23: return 2; // blend =
            case 0x24: case 0x25: // screen flipbook
                st.Flip = new FlipSt(d[offs], (int)((uint)b >> 13), (b >> 9) & 7, op - 0x24);
                return 2;
            case 0x26: // queue thread
            {
                bool renderPhase = (b & 0x8000) != 0;
                int idx = mode & 3, refOffs = d[offs++];
                if (idx == 0)
                    for (int i = 1; i < 4 && idx <= 0; i++)
                        if (st.Threads[i].Ptr < 0) idx = i;
                if (idx > 0)
                {
                    st.Threads[idx].Ptr = start + refOffs / 2;
                    st.Threads[idx].Render = renderPhase;
                    st.Threads[idx].Timer = 0;
                }
                return 2;
            }
            case 0x27: return mode == 4 ? 3 : mode == 3 ? 1 : 2; // tex ops
            case 0x28: return mode == 0 ? 2 : -1; // upload clut
            case 0x2A: return 1; // clear streams
            case 0x2B: return mode switch { 0 => 2, 1 or 2 or 3 => 1, 4 or 5 => 3, _ => -1 };
            case 0x2E: case 0x2F: // flag ops (0x2E targets parent)
            {
                if (op == 0x2E) offs++; // ref (asserted -10..-1 in noclip)
                int flag = d[offs++] & 0xFFFF;
                var tgt = op == 0x2E ? (st.Parent ?? st) : st;
                int masked = tgt.Flags & flag;
                int branchOffs = (mode == 2 || mode == 3) ? d[offs++] : 0;
                switch (mode)
                {
                    case 0: tgt.Flags |= flag; break;
                    case 1: tgt.Flags &= ~flag; break;
                    case 2: if (masked != 0) th.Ptr += branchOffs / 2; break;
                    case 3: if (masked == 0) th.Ptr += branchOffs / 2; break;
                    case 4: if (masked != 0) th.Timer = 1; break;
                    case 5: if (masked == 0) th.Timer = 1; break;
                    default: return -1;
                }
                return offs - start;
            }
            case 0x32: // fork child state
            {
                var child = new State(start + d[offs] / 2)
                { Parent = st, Origin = start + d[offs] / 2 };
                Array.Copy(st.Vecs[0], child.Vecs[0], 4);
                Array.Copy(st.Vecs[1], child.Vecs[1], 4);
                Array.Copy(st.Pos, child.Pos, 3);
                add.Add(child);
                return 2;
            }
            case 0x33: return 2; // buffer id
            case 0x36: // pos lerp parent->grandparent
            {
                float t;
                if (mode == 0) { t = Comp(st, d[offs]); offs += 4; }
                else if (mode == 1) { t = d[offs + 2]; offs += 4; }
                else return -1;
                var gp = st.Parent?.Parent;
                if (st.Parent != null && gp != null)
                    for (int i = 0; i < 3; i++)
                        st.Pos[i] = st.Parent.Pos[i] + (gp.Pos[i] - st.Parent.Pos[i]) * (t / 0x1000f);
                return 4;
            }
            case 0x37: // reset vecs
                if (mode == 0) { Array.Clear(st.Vecs[0]); Array.Clear(st.Vecs[1]); }
                else if (mode == 1) { var w = st.Vecs[1][3]; Array.Clear(st.Vecs[1]); st.Vecs[1][3] = w; }
                else if (mode == 2) { var w = st.Vecs[0][3]; Array.Clear(st.Vecs[0]); st.Vecs[0][3] = w; }
                else if (mode == 4) { Array.Clear(st.Vecs[2]); Array.Clear(st.Vecs[3]); Array.Clear(st.Vecs[4]); Array.Clear(st.Vecs[5]); }
                else if (mode == 5)
                { var w3 = st.Vecs[3][3]; Array.Clear(st.Vecs[3]); st.Vecs[3][3] = w3;
                  var w5 = st.Vecs[5][3]; Array.Clear(st.Vecs[5]); st.Vecs[5][3] = w5; }
                else if (mode == 6)
                { var w2 = st.Vecs[2][3]; Array.Clear(st.Vecs[2]); st.Vecs[2][3] = w2;
                  var w4 = st.Vecs[4][3]; Array.Clear(st.Vecs[4]); st.Vecs[4][3] = w4; }
                return 1;
            case 0x38: return mode == 0 ? 1 : -1; // actor[var].color = vec[1]
            case 0x40: // state color
            {
                bool justAlpha = false; int vecSource = -1, other = -0x100;
                switch (mode)
                {
                    case 0: other = d[offs++]; vecSource = 1; break;
                    case 1: other = d[offs++]; vecSource = 1; break;
                    case 2: break; // literal
                    case 3: vecSource = 0; break;
                    case 4: other = d[offs++]; vecSource = 0; break;
                    case 5: justAlpha = true; break;
                    default: return -1;
                }
                if (justAlpha) st.Color[3] = d[offs++] / 0x80f;
                else if (vecSource >= 0)
                {
                    var refState = other == -1 ? st.Parent : st;
                    if (refState != null)
                        for (int c = 0; c < 4; c++) st.Color[c] = refState.Vecs[vecSource][c] / 0x80f;
                }
                else
                {
                    for (int c = 0; c < 4; c++) st.Color[c] = d[offs + c] / 0x80f;
                    offs += 4;
                }
                return offs - start;
            }
            case 0x4A: return mode == 0 ? 3 : mode == 1 ? 5 : -1; // drip (state not rendered yet)
            case 0x4D: return mode <= 2 ? 2 : -1; // colored rect
            case 0x53: return 2; // update drip
            case 0x5A: st.VecOp = d[offs]; st.VecBaseState = st.Parent; return 4;
            case 0x5D: // fork for each target — one child per target in game; preview uses one
                if ((mode & 0xC) == 0)
                {
                    var child = new State(start + d[offs] / 2) { Parent = st };
                    Array.Copy(st.Vecs[0], child.Vecs[0], 4);
                    Array.Copy(st.Vecs[1], child.Vecs[1], 4);
                    Array.Copy(st.Pos, child.Pos, 3);
                    add.Add(child);
                    return 2;
                }
                return -1;
            case 0x5F: // actor pos
                if (mode == 0 || mode == 1)
                {
                    for (int i = 0; i < 3; i++) st.Vecs[1][i] = 16 * ActorPos[i];
                    Array.Copy(ActorPos, st.Pos, 3);
                    return 1;
                }
                return -1;
            case 0x61: // copy vec comps from parent
            {
                int prm = d[offs++]; int index = prm & 7;
                offs++; // -1 (parent marker)
                bool copyPos = (prm & 0x10) != 0;
                for (int i = 0; i < 4; i++)
                    if (((prm >> (12 + i)) & 1) != 0 && st.Parent != null)
                    {
                        st.Vecs[index][i] = st.Parent.Vecs[index][i];
                        if (copyPos && i < 3) st.Pos[i] = st.Parent.Pos[i];
                    }
                return 3;
            }
            case 0x64: return mode == 0 || mode == 8 ? 1 : mode == 1 ? 2 : -1; // var =
            case 0x68: if (mode <= 1) { st.MatrixSource = d[offs]; return 2; } return -1;
            case 0x6B:
                if (mode == 0) return 1; // force flipbook loop
                if (mode == 1) return 3; // flipbook scale/roll
                if (mode == 2) { st.DepthShift = d[offs] / 16f; return 2; }
                return -1;
            case 0x6F: return mode == 6 ? 4 : -1; // fill tex for geos
            case 0x70: // effect level branch/wait
            {
                int flag = d[offs++] & 0xFFFF;
                if (mode == 0 || mode == 1)
                {
                    int dest = d[offs++];
                    if ((EffectLevel == flag) == (mode == 0)) Jump(dest);
                    return 3;
                }
                if (mode == 2 || mode == 3)
                {
                    int dest = d[offs++];
                    if (((EffectLevel & flag) == 0) == (mode == 3)) Jump(dest);
                    return 3;
                }
                if (mode == 4 || mode == 5)
                {
                    if ((EffectLevel == flag) != (mode == 5)) th.Timer = 1;
                    return 2;
                }
                return -1;
            }
            case 0x72: // vec heading
                if (mode <= 2)
                {
                    float h = ActorHeading * 0x800f / MathF.PI;
                    if (mode == 1) st.Vecs[1][2] = h;
                    else
                    {
                        st.Vecs[0][1] = h;
                        if (mode == 0) { st.Vecs[0][0] = 0; st.Vecs[0][2] = 0; }
                    }
                    return 1;
                }
                return -1;
            case 0x80:
                if (mode == 2) { // save to parent.savedChildren[(arg-0xA8)/2]
                    int idx = (d[offs + 1] - 0xA8) / 2;
                    if (st.Parent != null && idx >= 0 && idx < st.Parent.SavedChildren.Length)
                        st.Parent.SavedChildren[idx] = st;
                    return 3;
                }
                if (mode == 0) return 3; // sib maybe-jump (log only in noclip)
                if (mode == 1) return 1;
                if (mode == 3)
                {
                    int idx = (d[offs] - 0xA8) / 2, tidx = d[offs + 1], jmp = d[offs + 2];
                    var child = idx >= 0 && idx < st.SavedChildren.Length ? st.SavedChildren[idx] : null;
                    if (child != null && tidx >= 0 && tidx < 4)
                        child.Threads[tidx].Ptr = start + jmp / 2;
                    return 4;
                }
                if (mode == 4) return 2; // custom render hook
                if (mode == 5) return 1;
                return -1;
            case 0x84: // battle/shatter ops — sizes only
            {
                int sub = d[offs];
                return sub switch { 4 => 2, 8 => 5, 9 => 3, 10 or 11 => 5, _ => -1 };
            }
            case 0x8C:
                if (mode == 1) return 3; // comp = height * arg
                if (mode == 2) { st.DoVecOp(); return 1; }
                if (mode == 6) { Array.Clear(st.Pos); Array.Clear(st.Vecs[1]); return 1; }
                if (mode == 5) return 2; // vec[3] = pos delta (drip)
                return -1;
            case 0x8F:
                if (mode == 0) { st.Basic = new MonsterEmitter(st, d[offs]); return 2; }
                if ((mode & 7) == 1)
                {
                    if (st.Basic != null)
                    {
                        float[] pos = new float[3];
                        if ((mode & 8) != 0)
                            for (int i = 0; i < 3; i++) pos[i] = st.Pos[i] * 16;
                        else
                            for (int i = 0; i < 3; i++) pos[i] = st.Vecs[1][i];
                        st.Basic.Emit(start + d[offs] / 2, pos);
                    }
                    return 2;
                }
                if (mode == 2) return 3; // emit other particle (unhandled)
                return -1;
            case 0x90:
                if (mode == 2) { ClearColor[0] = ClearColor[1] = ClearColor[2] = 1; }
                else if (mode == 3) Array.Clear(ClearColor);
                return 1;
            case 0x96: return mode == 0 ? 1 : -1;
            case 0x97: return (mode <= 1 || mode == 4 || mode == 5) ? 1 : -1;
            case 0x98: case 0x99: // bone/ref position → pos + vec[1] (no model: origin)
            {
                int boneIndex = d[offs++];
                int outLen;
                float px = 0, py = 0, pz = 0;
                switch (mode)
                {
                    case 0: outLen = 2; break;
                    case 1: px = st.Vecs[0][0]; py = st.Vecs[0][1]; pz = st.Vecs[0][2]; outLen = 2; break;
                    case 3: outLen = 1; break;
                    case 4: px = st.Vecs[0][0]; py = st.Vecs[0][1]; pz = st.Vecs[0][2]; outLen = 2; break;
                    case 5: px = d[offs]; py = d[offs + 1]; pz = d[offs + 2]; outLen = 5; break;
                    case 8:
                    {
                        float ang = st.Vecs[1][2] * (float)(Math.PI * 2 / 0x1000);
                        py = st.Vecs[1][1] / 16000f;
                        float r = st.Vecs[1][0] / 16000f;
                        px = r * MathF.Sin(ang); pz = r * MathF.Cos(ang);
                        if (d[offs] == 1) (px, py) = (py, px);
                        outLen = 3; break;
                    }
                    default: return -1;
                }
                st.Pos[0] = px; st.Pos[1] = py; st.Pos[2] = pz;
                st.Vecs[1][0] = px * 16; st.Vecs[1][1] = py * 16; st.Vecs[1][2] = pz * 16;
                return outLen;
            }
            case 0xA1:
                if (mode == 0)
                {
                    if (Rng.NextDouble() < d[offs] / 256.0) Jump(d[offs + 1]);
                    return 3;
                }
                if (mode == 7) return 3; // if model == id jump (no model in preview)
                if (mode == 0xA) return 1;
                return -1;
            case 0xA4: // branch/wait on vec component compare
            {
                bool isParent = (b & 0x400) != 0, twoVar = (b & 0x800) != 0, wait = (b & 0x200) != 0;
                var cmpState = isParent ? st.Parent ?? st : st;
                if (isParent) offs++;
                int dest = d[offs++];
                float left, right;
                if (twoVar)
                {
                    int pair = d[offs++];
                    left = Comp(cmpState, ((pair & 0xFF) - 0x30) / 4);
                    right = Comp(cmpState, (((pair >> 8) & 0xFF) - 0x30) / 4);
                }
                else
                {
                    left = Comp(cmpState, (d[offs++] - 0x30) / 4);
                    right = d[offs++];
                }
                bool res = mode switch
                {
                    0 => left != right, 1 => left == right, 2 => left >= right,
                    3 => left > right, 4 => left <= right, 5 => left < right,
                    _ => false,
                };
                if (wait) { if (!res) th.Timer = 1; }
                else if (res) Jump(dest);
                return offs - start;
            }
            case 0xA9: // vec component arithmetic
            {
                bool isRoot = (b & 0x400) != 0, twoVar = (b & 0x800) != 0, setVec = (b & 0x200) != 0;
                if (isRoot) offs++;
                int destOffs, leftOffs, rightOffs;
                if (twoVar)
                {
                    destOffs = d[offs] & 0xFF; leftOffs = (d[offs] >> 8) & 0xFF; rightOffs = d[offs + 1] & 0xFF;
                }
                else
                {
                    rightOffs = d[offs]; destOffs = d[offs + 1] & 0xFF; leftOffs = (d[offs + 1] >> 8) & 0xFF;
                }
                var vecs = isRoot && st.Parent != null ? st.Parent.Vecs : st.Vecs;
                int li = (leftOffs - 0x30) / 4, di = (destOffs - 0x30) / 4,
                    ri = (rightOffs - 0x30) / 4;
                if (li < 0 || li >= 24 || di < 0 || di >= 24) return -1;
                if (twoVar && (ri < 0 || ri >= 24)) return -1;
                float l = vecs[li >> 2][li & 3];
                float r = twoVar ? vecs[ri >> 2][ri & 3] : rightOffs;
                float res = mode switch { 0 => l + r, 1 => l - r, 2 => l * r, 3 => r != 0 ? l / r : 0, _ => 0 };
                vecs[di >> 2][di & 3] = res;
                if (setVec) st.DoVecOp();
                return 3 + (isRoot ? 1 : 0);
            }
            case 0xAB: return mode switch { 1 => 2, 3 => 1, 4 or 5 => 9, 6 => 1, _ => -1 };
            case 0xAC:
                if (mode == 0) // pos = vec[1] + random vec(len comp)
                {
                    float len = Comp(st, d[offs] & 0xF);
                    for (int i = 0; i < 3; i++)
                        st.Pos[i] = st.Vecs[1][i] / 16f + (float)(Rng.NextDouble() * 2 - 1) * len / 16f;
                    return 2;
                }
                if (mode == 1) // pos = vec[1] + randomCube(vec[0])
                {
                    for (int i = 0; i < 3; i++)
                        st.Pos[i] = st.Vecs[1][i] / 16f + (float)(Rng.NextDouble() * 2 - 1) * st.Vecs[0][i] / 16f;
                    return 1;
                }
                return -1;
            case 0xB0:
                if (mode == 0) { th.Timer = (int)(Rng.NextDouble() * d[offs]); Jump(4); return 2; }
                if (mode == 8)
                {
                    if (st.Parent != null && st.Parent.Parent != null) th.Timer = 1;
                    return 2;
                }
                return -1;
            case 0xB8: return 1; // nop
            case 0xC0: return 2; // magic sync
            case 0xD3: return mode <= 1 ? 2 : -1; // actor layer
            case 0xDC: // emitter bhv <behavior> scale <s>
                if (mode == 0)
                {
                    int behavior = d[offs + 1];
                    st.Scale = d[offs + 2] / 256f;
                    st.Full = SpawnEmitter(behavior);
                    return 4;
                }
                return -1;
            case 0xDD: // update spawned emitter (pos/scale/heading) or cleanup-jump
            {
                int toCleanup = d[offs++];
                var e = st.Full;
                if (e == null) return 2;
                if (e.Dead) { Jump(toCleanup); return 2; }
                var spec = e.Spec;
                float sc = st.Scale * st.Vecs[0][3] / 256f;
                float eulY = (0x400 - st.Vecs[0][1]) * MathF.PI / 0x800f;
                var m = ParticleSim.RotEuler(spec.Euler[0] * ParticleSim.ToRad, eulY,
                    spec.Euler[2] * ParticleSim.ToRad, spec.EulerOrder);
                for (int c = 0; c < 3; c++)
                    for (int r = 0; r < 3; r++)
                        m[4 * c + r] *= (float)(spec.Scale[c] * sc);
                m[12] = st.Pos[0]; m[13] = st.Pos[1]; m[14] = st.Pos[2];
                e.Pose = m;
                return 2;
            }
            case 0xDE: return 1; // cleanup marker
            case 0xE0: // vec[index].comp(mask) = args * scale
            {
                int index = (b >> 9) & 7;
                float scale = index < 2 ? 0x100f : index < 4 ? 1f : 1 / 16f;
                for (int i = 0; i < 4; i++)
                    if ((mode & (1 << i)) != 0)
                        st.Vecs[index][i] = d[offs++] * scale;
                return offs - start;
            }
            case 0xF2:
                if (mode == 1)
                {
                    st.VecOp = 0;
                    for (int i = 0; i < 3; i++) st.Vecs[1][i] = 16 * st.Pos[i];
                    st.SetPos = false;
                    return 2;
                }
                return -1;
            case 0xFF:
                st.AtCamera = (b & 0x800) != 0;
                return (b & 0x400) != 0 ? 4 : 1;
            default:
                return -1;
        }
    }

    /// <summary>processMonsterParticle port: runs a particle's mini-program
    /// until a sleep/lifetime op, setting pos/vel/accel/color/flipbook.</summary>
    void RunMonsterParticle(MonsterParticle p)
    {
        var d = Data;
        int curr = p.Ptr, boneListStart = -1, guard = 0;
        bool running = true;
        while (running && p.Lifetime <= 0 && guard++ < 10000)
        {
            if (curr < 0 || curr >= d.Length) { p.Ptr = -1; p.Lifetime = -1; return; }
            int value = d[curr] & 0xFFFF;
            int op = value & 0xFF;
            switch (op)
            {
                case 0: running = false; curr++; break;
                case 1: case 3:
                {
                    int timer = value >> 8;
                    p.Lifetime = op == 3 ? timer * (float)Rng.NextDouble() : timer;
                    curr++; break;
                }
                case 2:
                {
                    int jmp = curr + d[curr + 1] / 2;
                    if (jmp < p.Ptr) { running = false; break; } // jump to other program = end
                    curr = jmp; break;
                }
                case 4: case 5: case 6:
                {
                    int vecIndex = (value >> 8) & 7;
                    var v = vecIndex == 0 ? p.Pos : vecIndex == 1 ? p.Vel : vecIndex == 2 ? p.Accel : null;
                    float scale = new[] { 1f, 0x100f, 0x1000f }[Math.Min(vecIndex, 2)];
                    curr++;
                    for (int i = 0; i < 3; i++)
                        if (((value >> (12 + i)) & 1) != 0)
                        {
                            float comp = d[curr++] / scale;
                            if (v != null)
                            {
                                if (op == 4) v[i] = comp;
                                else if (op == 5) v[i] += (float)(Rng.NextDouble() * 2 - 1) * comp;
                                else v[i] += comp;
                            }
                        }
                    break;
                }
                case 7:
                    p.Flipbook = d[curr + 1];
                    p.Color = new[] { .5f, .5f, .5f, 1f };
                    Array.Clear(p.ColorVel);
                    p.ScaleAngle = new[] { 1f, 0, 0, 0 };
                    curr += 2; break;
                case 8: p.ShouldLoop = (value & 0xF000) == 0; curr++; break;
                case 9: case 10:
                {
                    int idx = (value >> 12) & 7;
                    float factor = idx >= 2 ? (float)(Math.PI * 2 / 0x1000) : 1 / 0x1000f;
                    float val = op == 9 ? d[curr + 1] : (float)(Rng.NextDouble() * 2 - 1) * d[curr + 1];
                    if (idx < 4)
                    {
                        if (op == 9) p.ScaleAngle[idx] = val * factor;
                        else p.ScaleAngle[idx] += val * factor;
                    }
                    curr += 2; break;
                }
                case 11:
                {
                    bool isVel = (value & 0x1000) != 0;
                    var t = isVel ? p.ColorVel : p.Color;
                    // noclip: color = (b0/0xFF, b1/0xFF, b2/0xFF, b3/0x80),
                    // vel bytes are signed
                    float B(int i)
                    {
                        int v = (d[curr + 1 + i / 2] >> (8 * (i & 1))) & 0xFF;
                        if (isVel && v >= 0x80) v -= 0x100;
                        return v;
                    }
                    t[0] = B(0) / 0xFFf; t[1] = B(1) / 0xFFf;
                    t[2] = B(2) / 0xFFf; t[3] = B(3) / 0x80f;
                    curr += 3; break;
                }
                case 12:
                    p.Mode = (PosMode)Math.Min(value >> 12, 2);
                    if (p.Mode == PosMode.Radial) Array.Copy(p.Pos, p.Center, 3);
                    curr++; break;
                case 13:
                {
                    curr++;
                    for (int i = 0; i < 4; i++)
                        if (((value >> (12 + i)) & 1) != 0)
                            p.Color[i] = d[curr++];
                    break;
                }
                case 16:
                    if (Rng.NextDouble() < d[curr + 1] / 256.0) curr += d[curr + 2] / 2;
                    else curr += 3;
                    break;
                case 18:
                    p.Pos[2] = p.Emitter.Owner.Vecs[0][1];
                    curr += 2; break;
                case 19:
                {
                    float bas = d[curr + 1], range = Math.Max(0, (int)d[curr + 2]);
                    p.T = bas + range * (float)Rng.NextDouble();
                    curr += 3; break;
                }
                case 20: // random bone — no model: pick list entry, keep index only
                {
                    boneListStart = curr + d[curr + 1] / 2;
                    int count = boneListStart < d.Length ? d[boneListStart] : 0;
                    p.Mode = PosMode.Bone;
                    if (count > 0 && boneListStart + count < d.Length)
                    {
                        int idx = (int)(Rng.NextDouble() * count);
                        p.Center[0] = d[boneListStart + 1 + idx];
                        p.Center[2] = (float)Rng.NextDouble();
                    }
                    curr += 2; break;
                }
                default: running = false; break;
            }
        }
        p.Ptr = curr;
        if (!running) { p.Ptr = -1; p.Lifetime = -1; }
    }

    /// <summary>Per-frame basic-particle physics + flipbook draws.</summary>
    void RunBasicEmitter(MonsterEmitter em, float dt, List<ParticleSim.DrawItem> outp)
    {
        dt = Math.Min(dt, 1);
        foreach (var p in em.Parts)
        {
            if (p.Lifetime <= 0 && p.Ptr >= 0) RunMonsterParticle(p);
            if (p.Lifetime <= 0) continue;
            for (int i = 0; i < 3; i++)
            {
                p.Vel[i] += dt * p.Accel[i];
                p.Pos[i] += dt * p.Vel[i];
            }
            p.ScaleAngle[0] += dt * p.ScaleAngle[1];
            p.ScaleAngle[2] += dt * p.ScaleAngle[3];
            for (int i = 0; i < 4; i++) p.Color[i] += dt * p.ColorVel[i];
            int fi = p.Flipbook + Math.Max(0, S.L.ExtraFlipbookIndex);
            if (fi < 0 || fi >= S.L.Flipbooks.Count) { p.Lifetime -= dt; continue; }
            var fb = S.L.Flipbooks[fi];
            if (fb.Frames.Count == 0) { p.Lifetime -= dt; continue; }
            float[] wpos = new float[3];
            if (p.Mode == PosMode.Radial)
            {
                float ang = p.Pos[2] * MathF.PI / 0x800f;
                wpos[0] = MathF.Cos(ang) * p.Pos[0] + p.Center[0];
                wpos[1] = p.Pos[1] + p.Center[1];
                wpos[2] = MathF.Sin(ang) * p.Pos[0] + p.Center[2];
            }
            else Array.Copy(p.Pos, wpos, 3);
            var pose = ParticleSim.Identity();
            pose[0] = pose[5] = pose[10] = p.ScaleAngle[0];
            // roll about view axis
            float ca = MathF.Cos(p.ScaleAngle[2]), sa = MathF.Sin(p.ScaleAngle[2]);
            float m0 = pose[0], m1 = pose[1], m4 = pose[4], m5 = pose[5];
            pose[0] = m0 * ca; pose[1] = m0 * sa;
            pose[4] = m4 * ca - m5 * sa; pose[5] = m4 * sa + m5 * ca;
            pose[12] = wpos[0] / 16f; pose[13] = wpos[1] / 16f; pose[14] = wpos[2] / 16f;
            var col = new[] { p.Color[0] * 2, p.Color[1] * 2, p.Color[2] * 2, p.Color[3] };
            S.RenderFlipbook(fb, ((int)p.T) % fb.Frames.Count, col, pose, outp);
            p.T += dt;
            if (!p.ShouldLoop && p.T >= fb.Frames.Count) p.Lifetime = 0;
            else p.Lifetime -= dt;
        }
    }
}
