// phyre-meshedit — conservative in-place vertex edits on .phyre models (P16).
// Reuses PhyreLib block/stream derivation (same DataInfo logic as the
// exporter) to locate named vertex streams, then rewrites floats inside the
// vertices region only. Layout-preserving: same byte size, same regions,
// only edited semantic floats change.
//
// Semantics covered: position (Vertex|SkinnableVertex), UV (ST),
// normals (Normal|SkinnableNormal), tangents (Tangent|SkinnableTangent),
// skin weights (SkinWeights), vertex color/alpha (Color f4).
// After position edits, PMeshInstanceBounds (min+size f3 records) are
// recomputed from the edited segments so culling stays correct.
// Material/texture/transparency state lives in PMaterial/PParameterBuffer/
// texture regions which are never touched — diffs stay inside vertex streams.
//
//   phyre-meshedit info <in.phyre>
//   phyre-meshedit streams <in.phyre>
//   phyre-meshedit blocks <in.phyre>
//   phyre-meshedit links <in.phyre>
//   phyre-meshedit edit <in> <out> [--seg N] [--range A:B] --translate x,y,z | --scale x,y,z
//   phyre-meshedit uv <in> <out> [--seg N] [--range A:B] --offset du,dv | --set u,v
//   phyre-meshedit normals <in> <out> [--seg N] [--range A:B] --set x,y,z
//   phyre-meshedit tangents <in> <out> [--seg N] [--range A:B] --set x,y,z
//   phyre-meshedit weights <in> <out> [--seg N] [--range A:B] --slot i --set w  (renormalizes)
//   phyre-meshedit alpha <in> <out> [--seg N] [--range A:B] --set a
//   phyre-meshedit jointmat <in> <out> --joint N [--scale s] [--translate x,y,z]  (PMatrix4 local bind)
//   phyre-meshedit blob <in> <out> --radius R [--center x,y,z] [--seg N]

using PhyreLib;
using System.Globalization;

if (args.Length < 2) { Usage(); return 2; }

var p = Phyre.Load(args[1]);
var seg = p.Block("PMeshSegment");
var dataBlk = p.Block("PDataBlock");
var vstream = p.Block("PVertexStream");
if (seg == null || dataBlk == null || vstream == null)
{ Console.Error.WriteLine("missing PMeshSegment/PDataBlock/PVertexStream"); return 1; }

var sdName = p.SharedData.Select(x => x.AsString()).ToArray();
var vsByData = new Dictionary<int, int>();
foreach (var l in dataBlk.ObjectLinks.Where(l => l.ObjBlockId == vstream.Id))
    vsByData[(int)l.ParentObjId] = (int)l.ObjId;
string CompName(int vsId)
{
    var links = vstream.ObjectLinks.Where(l => l.ParentObjId == (uint)vsId).ToArray();
    return (links.Length >= 1 && links[0].SharedDataId != uint.MaxValue &&
            links[0].SharedDataId < (uint)sdName.Length) ? sdName[links[0].SharedDataId] : $"vs{vsId}";
}
(long off, int ec, int es) DataInfo(int dbId)
{
    long o = dataBlk.DataOffset + (long)dataBlk.ElemSize * dbId;
    int es = (int)p.U(o); int ec = (int)p.U(o + 4);
    long voff = p.VertexBase + p.U(o + 48);
    return (voff, ec, es);
}

// per segment: semantic name -> (off, count, elemSize). Multiple ST/Tangent
// sets share one name — keep the first (primary channel).
var segStreams = new List<Dictionary<string, (long off, int ec, int es)>>();
for (int s = 0; s < seg.ElemCount; s++)
{
    var map = new Dictionary<string, (long, int, int)>();
    var dbLink = seg.ObjectLinks.FirstOrDefault(l => l.ParentObjId == (uint)s && l.ObjBlockId == dataBlk.Id);
    if (dbLink != null)
        for (int db = (int)dbLink.ObjId; db < (int)dbLink.ObjId + Math.Max(1, dbLink.ObjArrayCount); db++)
        {
            int vsId = vsByData.TryGetValue(db, out var v) ? v : -1;
            string name = vsId >= 0 ? CompName(vsId) : "?";
            if (!map.ContainsKey(name)) map[name] = DataInfo(db);
        }
    segStreams.Add(map);
}
(long off, int ec, int es)? Find(int s, params string[] names)
{
    foreach (var n in names)
        if (segStreams[s].TryGetValue(n, out var d)) return d;
    return null;
}

// PMeshSegment record fields (108B): +52 vertCount-1, +56 indexCount,
// +92 index byte offset (into index region), +100 index byte size.
const int SEG_VERTS1 = 52, SEG_IDXCNT = 56, SEG_IDXOFF = 92, SEG_IDXSIZE = 100;
// Header u32 field 18 = IndicesSize, 19 = VerticesSize.
const int HDR_INDICES = 72, HDR_VERTICES = 76;

void RecomputeBounds(byte[] outp, IEnumerable<int> editedSegs, Func<long, long>? adjust = null, int growVerts = 0)
{
    adjust ??= o => o;
    var boundsBlk = p.Block("PMeshInstanceBounds");
    var instBlk = p.Block("PMeshInstance");
    var meshBlk = p.Block("PMesh");
    if (boundsBlk == null || instBlk == null || meshBlk == null) return;
    var instToBounds = new Dictionary<int, int>();
    foreach (var l in boundsBlk.ObjectLinks.Where(l => l.ObjBlockId == instBlk.Id))
        instToBounds[(int)l.ObjId] = (int)l.ParentObjId;
    var instToMesh = new Dictionary<int, List<int>>();
    foreach (var l in instBlk.ObjectLinks.Where(l => l.ObjBlockId == meshBlk.Id))
    {
        if (!instToMesh.TryGetValue((int)l.ParentObjId, out var li))
            instToMesh[(int)l.ParentObjId] = li = new List<int>();
        li.Add((int)l.ObjId);
    }
    var meshToSegs = new Dictionary<int, List<int>>();
    foreach (var l in meshBlk.ObjectLinks.Where(l => l.ObjBlockId == seg.Id))
    {
        if (!meshToSegs.TryGetValue((int)l.ParentObjId, out var li))
            meshToSegs[(int)l.ParentObjId] = li = new List<int>();
        for (int k = 0; k < Math.Max(1, l.ObjArrayCount); k++) li.Add((int)l.ObjId + k);
    }
    var edited = editedSegs.ToHashSet();
    foreach (var (inst, meshIds) in instToMesh)
    {
        var segIds = meshIds.SelectMany(m => meshToSegs.TryGetValue(m, out var l2) ? l2 : new List<int>());
        if (!segIds.Any(edited.Contains) || !instToBounds.TryGetValue(inst, out int belem)) continue;
        float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue };
        float[] mx = { float.MinValue, float.MinValue, float.MinValue };
        bool any = false;
        foreach (var s2 in segIds)
        {
            var pos = Find(s2, "SkinnableVertex", "Vertex");
            if (pos == null) continue;
            var (off, ec, es) = pos.Value;
            int n = ec + (edited.Contains(s2) ? growVerts : 0);
            for (int i = 0; i < n; i++)
            {
                long o = adjust(off) + i * (long)es;
                for (int k = 0; k < 3; k++)
                {
                    float vv = BitConverter.ToSingle(outp, (int)o + k * 4);
                    if (vv < mn[k]) mn[k] = vv;
                    if (vv > mx[k]) mx[k] = vv;
                }
                any = true;
            }
        }
        if (!any) continue;
        long bo = boundsBlk.DataOffset + (long)belem * boundsBlk.ElemSize;
        for (int k = 0; k < 3; k++)
        {
            BitConverter.GetBytes(mn[k]).CopyTo(outp, bo + k * 4);
            BitConverter.GetBytes(mx[k] - mn[k]).CopyTo(outp, bo + 16 + k * 4);
        }
    }
}

switch (args[0])
{
    case "grow":
        {
            // grow <in> <out> --seg N --verts K | --tri "x,y,z;x,y,z;x,y,z" [--pos x,y,z] [--indices M]
            if (args.Length < 3) { Usage(); return 2; }
            string outPath = args[2];
            int segIdx = -1, addV = 0, addI = 0;
            float[]? posOff = null;
            var tris = new List<float[]>();
            // --clone startTri,stride,count: duplicate real triangles (all
            // streams cloned per source vertex) offset by --dxyz. Produces
            // provably-renderable geometry on visible surfaces.
            int cloneStart = -1, cloneStride = 1, cloneCount = 0;
            for (int i = 3; i + 1 < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--seg": segIdx = int.Parse(args[i + 1]); break;
                    case "--verts": addV = int.Parse(args[i + 1]); break;
                    case "--indices": addI = int.Parse(args[i + 1]); break;
                    case "--pos": posOff = args[i + 1].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                    case "--dxyz": posOff = args[i + 1].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                    case "--clone":
                        var cp = args[i + 1].Split(',').Select(int.Parse).ToArray();
                        cloneStart = cp[0]; cloneStride = cp.Length > 1 ? cp[1] : 1; cloneCount = cp.Length > 2 ? cp[2] : 1;
                        break;
                    case "--tri":
                        foreach (var v in args[i + 1].Split(';'))
                            tris.Add(v.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray());
                        break;
                    default: Console.Error.WriteLine($"unknown arg {args[i]}"); return 2;
                }
            }
            if (segIdx < 0 || segIdx >= segStreams.Count)
            { Console.Error.WriteLine($"--seg {segIdx} out of range (0..{segStreams.Count - 1})"); return 1; }
            if (tris.Count > 0) { addV = tris.Count; addI = tris.Count; }
            int[]? srcVerts = null;
            if (cloneStart >= 0)
            {
                int triTotal = (int)p.U(seg.DataOffset + (long)segIdx * seg.ElemSize + SEG_IDXCNT) / 3;
                if (cloneCount <= 0 || cloneStride <= 0 ||
                    cloneStart + (long)(cloneCount - 1) * cloneStride >= triTotal)
                { Console.Error.WriteLine($"--clone out of range (tris={triTotal})"); return 1; }
                long ib = p.VertexBase - p.Header.IndicesSize + p.U(seg.DataOffset + (long)segIdx * seg.ElemSize + SEG_IDXOFF);
                var sv = new List<int>();
                for (int j = 0; j < cloneCount; j++)
                {
                    long t = ib + (long)(cloneStart + j * cloneStride) * 6;
                    for (int e = 0; e < 3; e++)
                        sv.Add(BitConverter.ToUInt16(p.Bytes, (int)t + e * 2));
                }
                srcVerts = sv.ToArray();
                addV = srcVerts.Length; addI = srcVerts.Length;
            }
            if (addV <= 0 && addI <= 0)
            { Console.Error.WriteLine("grow requires --verts K, --indices M, --tri or --clone"); return 2; }

            var segRec = seg.DataOffset + (long)segIdx * seg.ElemSize;
            int oldVerts = (int)p.U(segRec + SEG_VERTS1) + 1;
            int oldIdxCnt = (int)p.U(segRec + SEG_IDXCNT);
            long segIdxOff = p.U(segRec + SEG_IDXOFF);
            long segIdxSize = p.U(segRec + SEG_IDXSIZE);
            long idxBase = p.VertexBase - p.Header.IndicesSize;

            // all streams of this segment must share the vertex count
            var sStreams = segStreams[segIdx];
            if (addV > 0)
            {
                foreach (var (name, d) in sStreams)
                    if (d.ec != oldVerts)
                    { Console.Error.WriteLine($"seg {segIdx} stream {name} count {d.ec} != verts {oldVerts} — unsupported layout"); return 1; }
            }

            // build insertion list (fileOffset, payload bytes)
            var inserts = new List<(long at, byte[] data)>();
            var segDbs = new List<(int db, long off, int ec, int es, string name)>();
            if (addV > 0)
            {
                var dbLink = seg.ObjectLinks.First(l => l.ParentObjId == (uint)segIdx && l.ObjBlockId == dataBlk.Id);
                for (int db = (int)dbLink.ObjId; db < (int)dbLink.ObjId + Math.Max(1, dbLink.ObjArrayCount); db++)
                {
                    var d = DataInfo(db);
                    int vsId = vsByData.TryGetValue(db, out var v) ? v : -1;
                    segDbs.Add((db, d.off, d.ec, d.es, vsId >= 0 ? CompName(vsId) : "?"));
                }
                foreach (var (db, off, ec, es, name) in segDbs)
                {
                    var payload = new byte[addV * es];
                    // clone per-source-vertex (or last element) so normals/uv/
                    // weights/joint refs stay valid on every stream
                    for (int k = 0; k < addV; k++)
                    {
                        int src = srcVerts != null ? srcVerts[k] : ec - 1;
                        Array.Copy(p.Bytes, off + (long)src * es, payload, (long)k * es, es);
                    }
                    inserts.Add((off + (long)ec * es, payload));
                }
            }
            if (addI > 0)
            {
                var ipay = new byte[addI * 2];
                for (int k = 0; k < addI; k++)
                    BitConverter.GetBytes((ushort)(oldVerts + (k % Math.Max(1, addV)))).CopyTo(ipay, k * 2);
                inserts.Add((idxBase + segIdxOff + segIdxSize, ipay));
            }

            // apply inserts (descending order keeps earlier offsets valid)
            var outpL = new List<byte>(p.Bytes);
            foreach (var (at, data2) in inserts.OrderByDescending(x => x.at))
                outpL.InsertRange((int)at, data2);
            var outp = outpL.ToArray();

            // fixups ----------------------------------------------------------
            // 1) PDataBlock: ec for grown streams; voff shift for streams whose
            //    data began at/after an insertion point (index inserts sit below
            //    VertexBase, so vertex streams never need index-size shifts —
            //    VertexBase moves but stored voff is relative to it).
            int vertInsertsBefore(long origOff)
            {
                int s2 = 0;
                foreach (var (at, data2) in inserts)
                    if (at >= p.VertexBase && at <= origOff) s2 += data2.Length;
                return s2;
            }
            var grownDb = segDbs.Select(x => x.db).ToHashSet();
            for (int db = 0; db < dataBlk.ElemCount; db++)
            {
                long ro = dataBlk.DataOffset + (long)dataBlk.ElemSize * db;
                var d = DataInfo(db);
                if (grownDb.Contains(db))
                {
                    BitConverter.GetBytes((uint)(d.ec + addV)).CopyTo(outp, ro + 4);
                    BitConverter.GetBytes((uint)(d.ec + addV) * (uint)d.es).CopyTo(outp, ro + 56);
                }
                int sh = vertInsertsBefore(d.off);
                if (sh > 0)
                    BitConverter.GetBytes((uint)(d.off - p.VertexBase + sh)).CopyTo(outp, ro + 48);
            }
            // 2) PMeshSegment: vertCount-1, indexCount, index byte size; later
            //    segments' index offsets shift by the index growth.
            BitConverter.GetBytes((uint)(oldVerts - 1 + addV)).CopyTo(outp, segRec + SEG_VERTS1);
            BitConverter.GetBytes((uint)(oldIdxCnt + addI)).CopyTo(outp, segRec + SEG_IDXCNT);
            BitConverter.GetBytes((uint)(segIdxSize + addI * 2)).CopyTo(outp, segRec + SEG_IDXSIZE);
            for (int s2 = 0; s2 < seg.ElemCount; s2++)
            {
                long r2 = seg.DataOffset + (long)s2 * seg.ElemSize;
                uint o2 = p.U(r2 + SEG_IDXOFF);
                if (o2 > segIdxOff)
                    BitConverter.GetBytes(o2 + (uint)(addI * 2)).CopyTo(outp, r2 + SEG_IDXOFF);
            }
            // 3) header sizes
            BitConverter.GetBytes(p.Header.IndicesSize + (uint)(addI * 2)).CopyTo(outp, HDR_INDICES);
            BitConverter.GetBytes(p.Header.VerticesSize + (uint)inserts.Where(x => x.at >= p.VertexBase).Sum(x => x.data.Length)).CopyTo(outp, HDR_VERTICES);
            // 4) explicit positions for the new verts (--tri / --pos).
            //    In `outp` the stream moved right by the index growth (index
            //    region sits below VertexBase) plus any vertex inserts before it.
            var posS = Find(segIdx, "SkinnableVertex", "Vertex");
            if (posS != null && addV > 0 && (tris.Count > 0 || posOff != null))
            {
                var (poff, _, pes) = posS.Value;
                long baseOff = poff + addI * 2 + vertInsertsBefore(poff);
                for (int k = 0; k < addV; k++)
                {
                    long o = baseOff + (long)(oldVerts + k) * pes;
                    var tgt = tris.Count > 0 ? tris[k % tris.Count] : null;
                    for (int c = 0; c < 3; c++)
                    {
                        float v2 = tgt != null ? tgt[c] : BitConverter.ToSingle(outp, (int)o + c * 4) + (posOff?[c] ?? 0);
                        BitConverter.GetBytes(v2).CopyTo(outp, o + c * 4);
                    }
                }
            }
            RecomputeBounds(outp, new[] { segIdx },
                adjust: o => o >= p.VertexBase ? o + addI * 2 + vertInsertsBefore(o) : o,
                growVerts: addV);
            File.WriteAllBytes(outPath, outp);
            Console.WriteLine($"grew seg {segIdx}: +{addV} verts, +{addI} idx -> {outPath} ({outp.Length} bytes, was {p.Bytes.Length})");
            return 0;
        }
    case "links":
        foreach (var b in p.Blocks)
        {
            var byBlk = b.ObjectLinks.GroupBy(l => l.ObjBlockId);
            Console.WriteLine($"{b.Name}: {b.ObjectLinks.Count} links -> " +
                string.Join(", ", byBlk.Select(g =>
                    $"blk{g.Key}({p.Blocks.FirstOrDefault(x => x.Id == g.Key)?.Name}) x{g.Count()}")));
        }
        return 0;
    case "alinks":
        foreach (var b in p.Blocks)
            foreach (var l in b.ArrayLinks)
                Console.WriteLine($"{b.Name}[{l.ParentObjId}] fldOff={l.ParentFieldOffset} objOff={l.ParentObjOffset} flag={l.ParentOffsetFlag} -> arrOff={l.Offset} cnt={l.Count}");
        return 0;
    case "blocks":
        foreach (var b in p.Blocks)
            Console.WriteLine($"{b.Name ?? "?"} id={b.Id} count={b.ElemCount} off={b.DataOffset} size={b.DataSize} elem={b.ElemSize}");
        return 0;
    case "streams":
        for (int s = 0; s < seg.ElemCount; s++)
        {
            var dbLink = seg.ObjectLinks.FirstOrDefault(l => l.ParentObjId == (uint)s && l.ObjBlockId == dataBlk.Id);
            if (dbLink == null) { Console.WriteLine($"seg {s}: (no data blocks)"); continue; }
            for (int db = (int)dbLink.ObjId; db < (int)dbLink.ObjId + Math.Max(1, dbLink.ObjArrayCount); db++)
            {
                int vsId = vsByData.TryGetValue(db, out var v) ? v : -1;
                var di = DataInfo(db);
                Console.WriteLine($"seg {s} db {db}: name={CompName(vsId)} count={di.ec} elemSize={di.es} off={di.off}");
            }
        }
        return 0;
    case "info":
        for (int s = 0; s < segStreams.Count; s++)
        {
            var pos = Find(s, "SkinnableVertex", "Vertex");
            var st = Find(s, "ST");
            var n = Find(s, "Normal", "SkinnableNormal");
            Console.WriteLine($"seg {s}: verts={(pos?.ec ?? 0)} posOff={(pos?.off ?? 0)} stride={(pos?.es ?? 12)} " +
                              $"st={(st != null ? $"off={st.Value.off} stride={st.Value.es}" : "none")} " +
                              $"normal={(n != null ? $"off={n.Value.off} stride={n.Value.es}" : "none")} " +
                              $"streams=[{string.Join(',', segStreams[s].Keys)}]");
        }
        Console.WriteLine($"total segs={segStreams.Count} vertexBase={p.VertexBase} bytes={p.Bytes.Length} " +
                          $"dataBlkOff={dataBlk.DataOffset} dataBlkElem={dataBlk.ElemSize}");
        return 0;
    case "jointmat":
        {
            // jointmat <in> <out> --joint N [--scale s] [--translate x,y,z]
            // Patches PMatrix4[localmatBase + joint] — the skeleton local
            // bind matrix the D3D11 path consumes (.chr SKL is NOT the PC
            // render skeleton; P23 runtime probe). 3x3 part = m[0..10],
            // translation = m[12..14], row-major float[16] (64B elems).
            if (args.Length < 3) { Usage(); return 2; }
            string outPath = args[2];
            int joint = -1, absElem = -1;
            float jscale = 1.0f;
            float[]? jtrans = null;
            for (int i = 3; i + 1 < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--joint": joint = int.Parse(args[i + 1]); break;
                    case "--abs": absElem = int.Parse(args[i + 1]); break;
                    case "--scale": jscale = float.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                    case "--translate": jtrans = args[i + 1].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                    default: Console.Error.WriteLine($"unknown arg {args[i]}"); return 2;
                }
            }
            var pmat = p.Block("PMatrix4");
            var pmesh = p.Block("PMesh");
            if (pmat == null || pmesh == null || pmat.ElemSize != 64)
            { Console.Error.WriteLine("missing PMatrix4/PMesh or unexpected elem size"); return 1; }
            int lbase = -1, nj = 0;
            foreach (var l in pmesh.ObjectLinks)
                if (l.ObjBlockId == pmat.Id && l.ParentOffsetFlag == 1 && l.ParentFieldOffset == 28)
                { lbase = (int)l.ObjId; nj = (int)l.ObjArrayCount; }
            if (lbase < 0 || nj <= 0)
            { Console.Error.WriteLine("no skeleton link (PMesh->PMatrix4 field 28)"); return 1; }
            int elem;
            if (absElem >= 0)
            {
                if (absElem >= pmat.ElemCount)
                { Console.Error.WriteLine($"--abs {absElem} out of range (0..{pmat.ElemCount - 1})"); return 1; }
                elem = absElem;
            }
            else
            {
                if (joint < 0 || joint >= nj)
                { Console.Error.WriteLine($"--joint {joint} out of range (0..{nj - 1}, or --abs 0..{pmat.ElemCount - 1})"); return 1; }
                elem = lbase + joint;
            }
            long mo = pmat.DataOffset + (long)pmat.ElemSize * elem;
            var outp = p.Bytes.ToArray();
            if (jscale != 1.0f)
                for (int i = 0; i < 11; i++)
                    BitConverter.GetBytes(BitConverter.ToSingle(outp, (int)mo + i * 4) * jscale).CopyTo(outp, mo + i * 4);
            if (jtrans != null)
                for (int k = 0; k < 3; k++)
                    BitConverter.GetBytes(BitConverter.ToSingle(outp, (int)mo + (12 + k) * 4) + jtrans[k]).CopyTo(outp, mo + (12 + k) * 4);
            File.WriteAllBytes(outPath, outp);
            Console.WriteLine($"joint {joint} (PMatrix4[{elem}]) scale={jscale} trans=[{(jtrans == null ? "-" : string.Join(',', jtrans))}] -> {outPath}");
            return 0;
        }
    case "scene":
        {
            // scene <in> — dump the rigid scene graph: PNode local matrices,
            // PNode->PWorldMatrix binding, PMeshInstance->PWorldMatrix users,
            // cameras, materials. PMatrix4x3 (48B) packing per SDK
            // PhyreMatrix4x3.h: col1.w=col0.x, col2.w=col0.y, col3.w=col0.z,
            // col3.xyz = translation.
            var pnode = p.Block("PNode");
            var pwm = p.Block("PWorldMatrix");
            var pinst = p.Block("PMeshInstance");
            var pmeshB = p.Block("PMesh");
            var pmat = p.Block("PMaterial");
            var camO = p.Block("PCameraOrthographic");
            var camP = p.Block("PCameraPerspective");
            if (pwm == null) { Console.Error.WriteLine("no PWorldMatrix block (skinned model?)"); return 1; }

            // Decode PMatrix4x3 element -> (T, basis col lengths)
            (double tx, double ty, double tz, double s0, double s1, double s2) DecWM(int m)
            {
                long o = pwm.DataOffset + (long)pwm.ElemSize * m;
                double s0 = Math.Sqrt(p.F(o + 12) * p.F(o + 12) + p.F(o + 28) * p.F(o + 28) + p.F(o + 44) * p.F(o + 44));
                double s1 = Math.Sqrt(p.F(o) * p.F(o) + p.F(o + 4) * p.F(o + 4) + p.F(o + 8) * p.F(o + 8));
                double s2 = Math.Sqrt(p.F(o + 16) * p.F(o + 16) + p.F(o + 20) * p.F(o + 20) + p.F(o + 24) * p.F(o + 24));
                return (p.F(o + 32), p.F(o + 36), p.F(o + 40), s0, s1, s2);
            }

            // instance/camera -> world matrix users
            var users = new Dictionary<int, List<string>>();
            void AddUsers(PhyreObjectBlock? blk, string tag)
            {
                if (blk == null) return;
                foreach (var l in blk.ObjectLinks.Where(l => l.ObjBlockId == pwm.Id))
                {
                    if (!users.TryGetValue((int)l.ObjId, out var li)) users[(int)l.ObjId] = li = new();
                    li.Add($"{tag}{(int)l.ParentObjId}");
                }
            }
            AddUsers(pinst, "inst");
            AddUsers(camO, "camO");
            AddUsers(camP, "camP");

            // node -> worldmatrix and node parent map
            var nodeWm = new Dictionary<int, int>();
            var nodeParent = new Dictionary<int, int>();
            if (pnode != null)
            {
                foreach (var l in pnode.ObjectLinks.Where(l => l.ObjBlockId == pwm.Id))
                    nodeWm[(int)l.ParentObjId] = (int)l.ObjId;
                var pgroup = pnode.ObjectLinks.Where(l => l.ObjBlockId == pnode.Id && l.ParentOffsetFlag == 0)
                    .GroupBy(l => l.ParentObjOffset).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (pgroup != null)
                    foreach (var l in pgroup) nodeParent[(int)l.ParentObjId] = (int)l.ObjId;
            }

            // materials per mesh (PMesh -> PMaterial links)
            var meshMats = new Dictionary<int, List<int>>();
            if (pmeshB != null && pmat != null)
                foreach (var l in pmeshB.ObjectLinks.Where(l => l.ObjBlockId == pmat.Id))
                {
                    if (!meshMats.TryGetValue((int)l.ParentObjId, out var li)) meshMats[(int)l.ParentObjId] = li = new();
                    li.Add((int)l.ObjId);
                }

            // material -> parameter buffer + samplers (via PParameterBuffer links)
            var matParam = new Dictionary<int, int>();
            if (pmat != null)
                foreach (var l in pmat.ObjectLinks)
                    if (p.Blocks.FirstOrDefault(b => b.Id == l.ObjBlockId)?.Name == "PParameterBuffer")
                        matParam[(int)l.ParentObjId] = (int)l.ObjBlockId;

            // import string table: trailing bytes of PAssetReferenceImport block
            var pimp = p.Block("PAssetReferenceImport");
            var importStrings = new List<string>();
            if (pimp != null)
            {
                long s0 = pimp.DataOffset + (long)pimp.ElemSize * pimp.ElemCount;
                long s1 = pimp.DataOffset + pimp.DataSize;
                if (s1 > s0)
                {
                    var raw = System.Text.Encoding.ASCII.GetString(p.Bytes, (int)s0, (int)(s1 - s0));
                    importStrings.AddRange(raw.Split('\0', StringSplitOptions.RemoveEmptyEntries));
                }
            }

            Console.WriteLine($"scene: nodes={pnode?.ElemCount ?? 0} worldMats={pwm.ElemCount} instances={pinst?.ElemCount ?? 0} " +
                              $"meshes={pmeshB?.ElemCount ?? 0} materials={pmat?.ElemCount ?? 0} " +
                              $"cameras={(camO?.ElemCount ?? 0) + (camP?.ElemCount ?? 0)} importStrings={importStrings.Count}");
            if (pmeshB != null)
            {
                Console.WriteLine("== Materials (mesh -> materials, material -> paramBuffer) ==");
                for (int m = 0; m < pmeshB.ElemCount; m++)
                {
                    if (!meshMats.TryGetValue(m, out var mats) || mats.Count == 0) continue;
                    var pb = mats.Select(mi => matParam.TryGetValue(mi, out var pbi) ? pbi : -1);
                    Console.WriteLine($"mesh[{m,2}] mats=[{string.Join(',', mats)}] paramBufs=[{string.Join(',', pb)}]");
                }
                var shaders = importStrings.Where(s => s.Contains("Shader", StringComparison.OrdinalIgnoreCase)).ToList();
                var texs = importStrings.Where(s => s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToList();
                var coll = importStrings.Where(s => s.Contains('#')).ToList();
                Console.WriteLine($"imports: shaders={shaders.Count} textures={texs.Count} colladaNodes={coll.Count}");
                foreach (var s in shaders.Take(3)) Console.WriteLine($"  shader: {s}");
                foreach (var s in texs.Take(4)) Console.WriteLine($"  tex: {s}");
            }
            Console.WriteLine("== PWorldMatrix ==");
            for (int m = 0; m < pwm.ElemCount; m++)
            {
                var (tx, ty, tz, s0, s1, s2) = DecWM(m);
                var use = users.TryGetValue(m, out var u) ? string.Join(',', u) : "-";
                Console.WriteLine($"wm[{m,2}] T=({tx,10:F3},{ty,9:F3},{tz,10:F3}) scale=({s0:F3},{s1:F3},{s2:F3}) usedBy=[{use}]");
            }
            if (pnode != null)
            {
                Console.WriteLine("== PNode ==");
                for (int i = 0; i < pnode.ElemCount; i++)
                {
                    long o = pnode.DataOffset + (long)pnode.ElemSize * i + 16; // local PMatrix4
                    double tx = p.F(o + 48), ty = p.F(o + 52), tz = p.F(o + 56);
                    double sx = Math.Sqrt(p.F(o) * p.F(o) + p.F(o + 4) * p.F(o + 4) + p.F(o + 8) * p.F(o + 8));
                    nodeWm.TryGetValue(i, out var wm);
                    nodeParent.TryGetValue(i, out var par);
                    Console.WriteLine($"node[{i,2}] parent={(par >= 0 ? par : -1),3} wm={(wm >= 0 ? wm : -1),3} " +
                                      $"localT=({tx,10:F3},{ty,9:F3},{tz,10:F3}) sx={sx:F3}");
                }
            }
            return 0;
        }
    case "worldmat":
        {
            // worldmat <in> <out> --matrix M | --node N [--translate x,y,z] [--scale s]
            // --matrix: patch PWorldMatrix[M] (PMatrix4x3 packed).
            // --node:   patch PNode[N].local PMatrix4 (row-major, +16 in record).
            if (args.Length < 3) { Usage(); return 2; }
            string outPath = args[2];
            int wmat = -1, node = -1, wmatEnd = -1, nodeEnd = -1;
            float wscale = 1.0f; float[]? wtrans = null;
            for (int i = 3; i + 1 < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--matrix":
                        {
                            var v = args[i + 1];
                            if (v == "all") { wmat = 0; wmatEnd = int.MaxValue; }
                            else if (v.Contains(':'))
                            {
                                var ab = v.Split(':');
                                wmat = int.Parse(ab[0]); wmatEnd = int.Parse(ab[1]);
                            }
                            else wmat = int.Parse(v);
                            break;
                        }
                    case "--node":
                        {
                            var v = args[i + 1];
                            if (v == "all") { node = 0; nodeEnd = int.MaxValue; }
                            else if (v.Contains(':'))
                            {
                                var ab = v.Split(':');
                                node = int.Parse(ab[0]); nodeEnd = int.Parse(ab[1]);
                            }
                            else node = int.Parse(v);
                            break;
                        }
                    case "--scale": wscale = float.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                    case "--translate": wtrans = args[i + 1].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                    default: Console.Error.WriteLine($"unknown arg {args[i]}"); return 2;
                }
            }
            if (wtrans != null && wtrans.Length != 3) { Console.Error.WriteLine("--translate needs x,y,z"); return 2; }
            var pwm = p.Block("PWorldMatrix");
            var pnode = p.Block("PNode");
            var outp = p.Bytes.ToArray();
            int patched = 0;
            if (wmat >= 0)
            {
                if (pwm == null || wmat >= pwm.ElemCount) { Console.Error.WriteLine($"--matrix {wmat} out of range"); return 1; }
                if (wmatEnd < 0) wmatEnd = wmat;
                if (wmatEnd >= pwm.ElemCount) wmatEnd = pwm.ElemCount - 1;
                for (int m = wmat; m <= wmatEnd; m++)
                {
                    long o = pwm.DataOffset + (long)pwm.ElemSize * m;
                    if (wscale != 1.0f)
                        foreach (int fi in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 11 }) // col1.xyz+w, col2.xyz+w, col3.w (basis only)
                            BitConverter.GetBytes(BitConverter.ToSingle(outp, (int)o + fi * 4) * wscale).CopyTo(outp, o + fi * 4);
                    if (wtrans != null)
                        for (int k = 0; k < 3; k++)
                            BitConverter.GetBytes(BitConverter.ToSingle(outp, (int)o + (8 + k) * 4) + wtrans[k]).CopyTo(outp, o + (8 + k) * 4);
                    patched++;
                }
            }
            if (node >= 0)
            {
                if (pnode == null || node >= pnode.ElemCount) { Console.Error.WriteLine($"--node {node} out of range"); return 1; }
                if (nodeEnd < 0) nodeEnd = node;
                if (nodeEnd >= pnode.ElemCount) nodeEnd = pnode.ElemCount - 1;
                for (int nd = node; nd <= nodeEnd; nd++)
                {
                    long o = pnode.DataOffset + (long)pnode.ElemSize * nd + 16; // local PMatrix4 start
                    if (wscale != 1.0f)
                        foreach (int fi in new[] { 0, 1, 2, 4, 5, 6, 8, 9, 10 }) // 3x3 basis rows
                            BitConverter.GetBytes(BitConverter.ToSingle(outp, (int)o + fi * 4) * wscale).CopyTo(outp, o + fi * 4);
                    if (wtrans != null)
                        for (int k = 0; k < 3; k++)
                            BitConverter.GetBytes(BitConverter.ToSingle(outp, (int)o + (12 + k) * 4) + wtrans[k]).CopyTo(outp, o + (12 + k) * 4);
                    patched++;
                }
            }
            if (patched == 0) { Console.Error.WriteLine("nothing to patch: pass --matrix M and/or --node N"); return 2; }
            File.WriteAllBytes(outPath, outp);
            Console.WriteLine($"worldmat: patched {patched} record(s) scale={wscale} trans=[{(wtrans == null ? "-" : string.Join(',', wtrans))}] -> {outPath}");
            return 0;
        }
    case "blob":
        {
            // blob <in> <out> --radius R [--center x,y,z] [--seg N]
            // P24: replaces every position with a jagged-sphere point
            // (deterministic per vertex) and writes radial normals —
            // a synthetic body that still articulates through the
            // original SkinWeights/SkinIndices (rig untouched).
            if (args.Length < 3) { Usage(); return 2; }
            string outPath = args[2];
            int segIdx = -1;
            float radius = 2.0f;
            float[] center = { 0, 0, 0 };
            for (int i = 3; i + 1 < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--seg": segIdx = int.Parse(args[i + 1]); break;
                    case "--radius": radius = float.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                    case "--center": center = args[i + 1].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                    default: Console.Error.WriteLine($"unknown arg {args[i]}"); return 2;
                }
            }
            if (radius <= 0) { Console.Error.WriteLine("--radius must be > 0"); return 2; }
            if (segIdx >= segStreams.Count)
            { Console.Error.WriteLine($"--seg {segIdx} out of range (0..{segStreams.Count - 1})"); return 1; }
            var outp = p.Bytes.ToArray();
            var segs = segIdx >= 0 ? new[] { segIdx } : Enumerable.Range(0, segStreams.Count).ToArray();
            int edited = 0;
            foreach (var s in segs)
            {
                var pos = Find(s, "SkinnableVertex", "Vertex");
                if (pos == null) continue;
                var (poff, pec, pes) = pos.Value;
                var nrm = Find(s, "Normal", "SkinnableNormal");
                for (int i = 0; i < pec; i++)
                {
                    // deterministic pseudo-random direction+radius per vert
                    uint h = (uint)(i * 2654435761u) ^ (uint)(s * 2246822519u);
                    double u = ((h >> 8) & 0xFFFFFF) / 16777216.0;
                    double v = ((h >> 1) & 0x7FFFFF) / 8388608.0;
                    double w = (h & 0xFF) / 255.0;
                    double th = 2 * Math.PI * u, ph = Math.Acos(2 * v - 1);
                    double rr = radius * (0.75 + 0.5 * w); // 0.75..1.25 R
                    float x = (float)(rr * Math.Sin(ph) * Math.Cos(th));
                    float y = (float)(rr * Math.Sin(ph) * Math.Sin(th));
                    float z = (float)(rr * Math.Cos(ph));
                    long o = poff + i * (long)pes;
                    BitConverter.GetBytes(center[0] + x).CopyTo(outp, o);
                    BitConverter.GetBytes(center[1] + y).CopyTo(outp, o + 4);
                    BitConverter.GetBytes(center[2] + z).CopyTo(outp, o + 8);
                    if (nrm != null)
                    {
                        double il = 1.0 / Math.Sqrt(x * x + y * y + z * z);
                        long no2 = nrm.Value.off + i * (long)nrm.Value.es;
                        BitConverter.GetBytes((float)(x * il)).CopyTo(outp, no2);
                        BitConverter.GetBytes((float)(y * il)).CopyTo(outp, no2 + 4);
                        BitConverter.GetBytes((float)(z * il)).CopyTo(outp, no2 + 8);
                    }
                    edited++;
                }
            }
            RecomputeBounds(outp, segs);
            File.WriteAllBytes(outPath, outp);
            Console.WriteLine($"blobbed {edited} verts (r={radius}) -> {outPath} ({outp.Length} bytes, layout preserved)");
            return 0;
        }
    case "edit":
    case "uv":
    case "normals":
    case "tangents":
    case "weights":
    case "joints":
    case "alpha":
        {
            if (args.Length < 3) { Usage(); return 2; }
            string outPath = args[2];
            int segIdx = -1, ra = 0, rb = int.MaxValue, wslot = -1;
            float[]? translate = null, scale = null, set = null, uvOff = null;
            for (int i = 3; i + 1 < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--seg": segIdx = int.Parse(args[i + 1]); break;
                    case "--range": var ab = args[i + 1].Split(':'); ra = int.Parse(ab[0]); rb = int.Parse(ab[1]); break;
                    case "--translate": translate = args[i + 1].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                    case "--scale": scale = args[i + 1].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                    case "--set": set = args[i + 1].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                    case "--offset": uvOff = args[i + 1].Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                    case "--slot": wslot = int.Parse(args[i + 1]); break;
                    default: Console.Error.WriteLine($"unknown arg {args[i]}"); return 2;
                }
            }
            if (segIdx >= segStreams.Count)
            { Console.Error.WriteLine($"--seg {segIdx} out of range (0..{segStreams.Count - 1})"); return 1; }
            if ((args[0] == "weights" || args[0] == "joints") && (wslot < 0 || wslot > 3))
            { Console.Error.WriteLine("weights requires --slot 0..3"); return 2; }
            if (ra < 0 || ra >= rb)
            { Console.Error.WriteLine($"--range {ra}:{rb} empty"); return 1; }

            var outp = p.Bytes.ToArray();
            int edited = 0;
            var editedSegs = new List<int>();
            var segs = segIdx >= 0 ? new[] { segIdx } : Enumerable.Range(0, segStreams.Count).ToArray();
            foreach (var s in segs)
            {
                // resolve target stream for this command
                (long off, int ec, int es)? t = args[0] switch
                {
                    "edit" => Find(s, "SkinnableVertex", "Vertex"),
                    "uv" => Find(s, "ST"),
                    "normals" => Find(s, "Normal", "SkinnableNormal"),
                    "tangents" => Find(s, "Tangent", "SkinnableTangent"),
                    "weights" => Find(s, "SkinWeights"),
                    "joints" => Find(s, "SkinIndices"),
                    "alpha" => Find(s, "Color"),
                    _ => null,
                };
                if (t == null) continue;
                var (off, ec, es) = t.Value;
                int end = Math.Min(rb, ec);
                if (ra >= end) continue;
                for (int i = ra; i < end; i++)
                {
                    long o = off + i * (long)es;
                    switch (args[0])
                    {
                        case "edit" when translate != null:
                            for (int k = 0; k < 3; k++)
                                BitConverter.GetBytes(p.F(o + k * 4) + translate[k]).CopyTo(outp, o + k * 4);
                            break;
                        case "edit" when scale != null:
                            for (int k = 0; k < 3; k++)
                                BitConverter.GetBytes(p.F(o + k * 4) * scale[k]).CopyTo(outp, o + k * 4);
                            break;
                        case "uv" when uvOff != null:
                            for (int k = 0; k < 2; k++)
                                BitConverter.GetBytes(p.F(o + k * 4) + uvOff[k]).CopyTo(outp, o + k * 4);
                            break;
                        case "uv" when set != null:
                            for (int k = 0; k < 2; k++)
                                BitConverter.GetBytes(set[k]).CopyTo(outp, o + k * 4);
                            break;
                        case "normals" or "tangents" when set != null:
                            for (int k = 0; k < 3; k++)
                                BitConverter.GetBytes(set[k]).CopyTo(outp, o + k * 4);
                            break;
                        case "weights" when set != null:
                        {
                            BitConverter.GetBytes(set[0]).CopyTo(outp, o + wslot * 4);
                            float sum = 0;
                            for (int k = 0; k < 4; k++) sum += BitConverter.ToSingle(outp, (int)o + k * 4);
                            if (sum > 1e-6f)
                                for (int k = 0; k < 4; k++)
                                    BitConverter.GetBytes(BitConverter.ToSingle(outp, (int)o + k * 4) / sum)
                                        .CopyTo(outp, o + k * 4);
                            break;
                        }
                        case "joints" when set != null:
                            // SkinIndices: 4×u8 joint refs — no renormalize,
                            // out-of-palette values are a caller bug
                            outp[o + wslot] = (byte)set[0];
                            break;
                        case "alpha" when set != null:
                            BitConverter.GetBytes(set[0]).CopyTo(outp, o + 12);
                            break;
                    }
                    edited++;
                }
                if (end > ra) editedSegs.Add(s);
            }
            if (edited == 0)
            { Console.Error.WriteLine("no vertices edited (check --seg/--range/stream availability)"); return 1; }

            // recompute PMeshInstanceBounds for instances containing edited segments
            int boundsFixed = 0;
            if (args[0] == "edit")
            {
                var boundsBlk = p.Block("PMeshInstanceBounds");
                var instBlk = p.Block("PMeshInstance");
                var meshBlk = p.Block("PMesh");
                if (boundsBlk != null && instBlk != null && meshBlk != null)
                {
                    // instance -> bounds elem
                    var instToBounds = new Dictionary<int, int>();
                    foreach (var l in boundsBlk.ObjectLinks.Where(l => l.ObjBlockId == instBlk.Id))
                        instToBounds[(int)l.ObjId] = (int)l.ParentObjId;
                    // instance -> mesh elems
                    var instToMesh = new Dictionary<int, List<int>>();
                    foreach (var l in instBlk.ObjectLinks.Where(l => l.ObjBlockId == meshBlk.Id))
                    {
                        if (!instToMesh.TryGetValue((int)l.ParentObjId, out var li))
                            instToMesh[(int)l.ParentObjId] = li = new List<int>();
                        li.Add((int)l.ObjId);
                    }
                    // mesh -> segment elems
                    var meshToSegs = new Dictionary<int, List<int>>();
                    foreach (var l in meshBlk.ObjectLinks.Where(l => l.ObjBlockId == seg.Id))
                    {
                        if (!meshToSegs.TryGetValue((int)l.ParentObjId, out var li))
                            meshToSegs[(int)l.ParentObjId] = li = new List<int>();
                        for (int k = 0; k < Math.Max(1, l.ObjArrayCount); k++) li.Add((int)l.ObjId + k);
                    }
                    foreach (var (inst, meshIds) in instToMesh)
                    {
                        var segIds = meshIds.SelectMany(m => meshToSegs.TryGetValue(m, out var l2) ? l2 : new List<int>());
                        if (!segIds.Intersect(editedSegs).Any() || !instToBounds.TryGetValue(inst, out int belem))
                            continue;
                        float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue };
                        float[] mx = { float.MinValue, float.MinValue, float.MinValue };
                        bool any = false;
                        foreach (var s2 in segIds)
                        {
                            var pos = Find(s2, "SkinnableVertex", "Vertex");
                            if (pos == null) continue;
                            var (off, ec, es) = pos.Value;
                            for (int i = 0; i < ec; i++)
                            {
                                long o = off + i * (long)es;
                                for (int k = 0; k < 3; k++)
                                {
                                    float vv = BitConverter.ToSingle(outp, (int)o + k * 4);
                                    if (vv < mn[k]) mn[k] = vv;
                                    if (vv > mx[k]) mx[k] = vv;
                                }
                                any = true;
                            }
                        }
                        if (!any) continue;
                        long bo = boundsBlk.DataOffset + (long)belem * boundsBlk.ElemSize;
                        for (int k = 0; k < 3; k++)
                        {
                            BitConverter.GetBytes(mn[k]).CopyTo(outp, bo + k * 4);
                            BitConverter.GetBytes(mx[k] - mn[k]).CopyTo(outp, bo + 16 + k * 4);
                        }
                        boundsFixed++;
                    }
                }
            }
            File.WriteAllBytes(outPath, outp);
            Console.WriteLine($"edited {edited} verts -> {outPath} ({outp.Length} bytes, layout preserved)" +
                              (boundsFixed > 0 ? $", bounds recomputed for {boundsFixed} instances" : ""));
            return 0;
        }
    default:
        Usage(); return 2;
}

void Usage()
{
    Console.Error.WriteLine("usage: phyre-meshedit info|streams|blocks|links <in.phyre>");
    Console.Error.WriteLine("       phyre-meshedit edit <in> <out> [--seg N] [--range A:B] --translate x,y,z|--scale x,y,z");
    Console.Error.WriteLine("       phyre-meshedit uv <in> <out> [--seg N] [--range A:B] --offset du,dv|--set u,v");
    Console.Error.WriteLine("       phyre-meshedit normals <in> <out> [--seg N] [--range A:B] --set x,y,z");
    Console.Error.WriteLine("       phyre-meshedit tangents <in> <out> [--seg N] [--range A:B] --set x,y,z");
    Console.Error.WriteLine("       phyre-meshedit weights <in> <out> [--seg N] [--range A:B] --slot 0..3 --set w");
    Console.Error.WriteLine("       phyre-meshedit joints <in> <out> [--seg N] [--range A:B] --slot 0..3 --set j   (SkinIndices u8)");
    Console.Error.WriteLine("       phyre-meshedit alpha <in> <out> [--seg N] [--range A:B] --set a");
    Console.Error.WriteLine("       phyre-meshedit jointmat <in> <out> --joint N [--scale s] [--translate x,y,z]  (PMatrix4 local bind)");
    Console.Error.WriteLine("       phyre-meshedit blob <in> <out> --radius R [--center x,y,z] [--seg N]");
}
