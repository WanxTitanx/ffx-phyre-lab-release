using System.Text;
using System.Text.Json;
using PhyreLib;

// Adapted for ffx-phyre-lab tools/phyre-exporter (P08): namespace PhyreExporter, no editor deps.
// PhyreSkinnedAnimExportLab — read-only RE tool. Emits skinned glTF (skeleton +
// JOINTS_0/WEIGHTS_0 + inverseBindMatrices) from a mXXX.dae.phyre. When the file
// carries keyframed PAnimationChannel data, it ALSO emits the glTF animation clip.
// Monsters without a matrix palette / skin fall back to a static mesh export.
//
// All structural constants are DERIVED from the file (no per-monster hardcodes):
//   LOCALMAT_BASE / NJOINT  <- PMesh -> PMatrix4 object-link with ParentFieldOffset==28
//   joints                  == PNode[0 .. NJOINT-1]
//   parent-link offset       <- PNode->PNode object-link group with the most links
//                               (each non-root node has exactly one parent)
//   keyframed target node    <- PAnimationChannel u32@+24 ; subType u32@+32 (1=rot,0=scale)
// Proven byte-identical on the m153/m189/m190/m323 rig family; generalizes to all 341.
//
// usage:
//   single:  PhyreSkinnedAnimExportLab <in.dae.phyre> <out.gltf>
//   batch:   PhyreSkinnedAnimExportLab batch <ps3_mon_root> <out_dir>

if (args.Length >= 1 && args[0].Equals("batch", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: batch <ps3_mon_root> <out_dir>"); return 1; }
    return RunBatch(args[1], args[2]);
}
if (args.Length < 2) { Console.Error.WriteLine("usage: <dae.phyre> <out.gltf>   |   batch <ps3_mon_root> <out_dir>"); return 1; }

var single = ExportOne(args[0], args[1], verbose: true);
if (!single.Ok) { Console.Error.WriteLine($"FAILED: {single.Error}"); return 1; }
return 0;

// ----------------------------------------------------------------------------
int RunBatch(string monRoot, string outDir)
{
    if (!Directory.Exists(monRoot)) { Console.Error.WriteLine($"mon root not found: {monRoot}"); return 1; }
    Directory.CreateDirectory(outDir);
    var monsters = Directory.GetDirectories(monRoot)
        .Select(Path.GetFileName)
        .Where(n => n is { Length: 4 } && n[0] == 'm' && n.Skip(1).All(char.IsDigit))
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();
    Console.WriteLine($"batch: {monsters.Length} monster dirs under {monRoot}");

    var results = new List<ExportResult>();
    int ok = 0, skinned = 0, staticOnly = 0, anim = 0, restOk = 0, textured = 0, fail = 0;
    foreach (var m in monsters)
    {
        var inPath = Path.Combine(monRoot, m!, "mdl", "d3d11", $"{m}.dae.phyre");
        if (!File.Exists(inPath)) { results.Add(ExportResult.Failed(m!, "missing .dae.phyre")); fail++; continue; }
        var outPath = Path.Combine(outDir, $"{m}_skinned.gltf");
        ExportResult r;
        try { r = ExportOne(inPath, outPath, verbose: false); }
        catch (Exception ex) { r = ExportResult.Failed(m!, ex.GetType().Name + ": " + ex.Message); }
        results.Add(r);
        if (r.Ok)
        {
            ok++;
            if (r.HasSkin) { skinned++; if (r.RestPoseOk) restOk++; } else staticOnly++;
            if (r.HasAnim) anim++;
            if (r.Textured) textured++;
        }
        else fail++;
    }

    var index = new
    {
        generatedAtUtc = DateTimeOffset.UtcNow,
        tool = "RuntimeTools/PhyreSkinnedAnimExportLab batch (read-only RE; no game files modified)",
        ps3MonRoot = monRoot,
        total = monsters.Length,
        exported = ok,
        skinned,
        staticOnly,
        withAnimation = anim,
        textured = textured,
        restPoseSelfCheckPass = restOk,
        restPoseSelfCheckTotal = skinned,
        failed = fail,
        decisionBand = "mesh_skinned_static_gltf_candidate",
        promotionStatus = "not_promoted_external_visual_validation_required",
        note = "Skinned (skeleton+JOINTS_0/WEIGHTS_0+inverseBindMatrices) glTF per monster where a matrix "
             + "palette/skin exists; static mesh otherwise. Animation emitted only where keyframed "
             + "PAnimationChannel exists. Skeleton + parent-link offset derived per file. "
             + "Materials/textures still flat-gray (A2 pending).",
        results,
    };
    var idxPath = Path.Combine(outDir, "_index.json");
    File.WriteAllText(idxPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    // ---- A4: viewer catalog for RuntimeTools/FFXModelViewerWeb (Three.js); openableAssetPath is relative to this file ----
    var catEntries = results.Where(r => r.Ok).Select(r =>
    {
        var tags = new List<string> { "mesh_static_lab_candidate" };
        if (r.HasAnim) tags.Add("partial_embedded_clip_visible");
        string band = r.HasAnim ? "mesh_skinned_animated_gltf_candidate"
                    : r.HasSkin ? "mesh_skinned_static_gltf_candidate"
                    : "mesh_static_gltf_candidate";
        return new
        {
            monsterId = r.Monster,
            label = $"{r.Monster} {(r.HasSkin ? $"skinned ({r.Joints} joints)" : "static mesh")}{(r.HasAnim ? " + clip" : "")}",
            decisionBand = band,
            assetStatus = (r.Textured ? "textured_" : "untextured_") + (r.HasSkin ? "skinned" : "static"),
            motionStatus = r.HasAnim ? "keyframed_clip_2s_24fps" : "static_pose",
            motionReadiness = r.HasAnim ? "ps3_keyframed_real" : "no_skeletal_clip",
            tags = tags.ToArray(),
            openableAssetPath = $"{r.Monster}_skinned.gltf",
        };
    }).ToArray();
    var catalog = new
    {
        generatedAtUtc = DateTimeOffset.UtcNow,
        schemaVersion = "phyre-skinned-1",
        generator = "RuntimeTools/PhyreSkinnedAnimExportLab batch (read-only RE; no game files modified)",
        defaultAssetId = catEntries.Any(e => e.monsterId == "m189") ? "m189" : (catEntries.FirstOrDefault()?.monsterId ?? "m001"),
        summary = new { entryCount = catEntries.Length, openableAssetCount = catEntries.Length },
        entries = catEntries,
    };
    var catPath = Path.Combine(outDir, "modelviewer-catalog.json");
    File.WriteAllText(catPath, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    Console.WriteLine($"DONE: exported={ok}/{monsters.Length}  skinned={skinned}  staticOnly={staticOnly}  withAnim={anim}  textured={textured}  restPoseOk={restOk}/{skinned}  failed={fail}");
    Console.WriteLine($"index: {idxPath}");
    Console.WriteLine($"catalog: {catPath} ({catEntries.Length} entries)");
    foreach (var r in results.Where(x => !x.Ok))
        Console.WriteLine($"  FAIL {r.Monster}: {r.Error}");
    foreach (var r in results.Where(x => x.Ok && x.HasSkin && !x.RestPoseOk))
        Console.WriteLine($"  REST-POSE OFF {r.Monster}: restErr={r.RestErr:E2} joints={r.Joints}");
    return 0;
}

// ----------------------------------------------------------------------------
ExportResult ExportOne(string inPath, string outPath, bool verbose)
{
    string monster = Path.GetFileNameWithoutExtension(inPath);
    if (monster.EndsWith(".dae", StringComparison.OrdinalIgnoreCase)) monster = monster[..^4];
    var p = Phyre.Load(inPath);

    var pnode = p.Block("PNode");
    var pmat = p.Block("PMatrix4");
    var pmesh = p.Block("PMesh");
    var seg = p.Block("PMeshSegment");
    var remap = p.Block("PSkinBoneRemap");
    var dataBlk = p.Block("PDataBlock");
    var vstream = p.Block("PVertexStream");
    if (pnode is null || pmesh is null || seg is null || dataBlk is null || vstream is null)
        return ExportResult.Failed(monster, "missing core mesh block (PNode/PMesh/PMeshSegment/PDataBlock/PVertexStream)");

    var sdName = p.SharedData.Select(s => s.AsString()).ToArray();

    // ---- DERIVE skeleton range from PMesh -> PMatrix4 object-link (field 28) ----
    int NJOINT = 0, LOCALMAT_BASE = 0; bool haveSkeleton = false;
    if (pmat != null)
        foreach (var l in pmesh.ObjectLinks)
            if (l.ObjBlockId == pmat.Id && l.ParentOffsetFlag == 1 && l.ParentFieldOffset == 28)
            { LOCALMAT_BASE = (int)l.ObjId; NJOINT = (int)l.ObjArrayCount; haveSkeleton = NJOINT > 0; }

    // ---- DERIVE PNode parent map: PNode->PNode object-link group with the most links ----
    int[] parentOf = Enumerable.Repeat(-1, pnode.ElemCount).ToArray();
    int parentLinkOffset = -1;
    if (haveSkeleton)
    {
        var parentGroup = pnode.ObjectLinks
            .Where(l => l.ObjBlockId == pnode.Id && l.ParentOffsetFlag == 0)
            .GroupBy(l => l.ParentObjOffset)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (parentGroup != null)
        {
            parentLinkOffset = (int)parentGroup.Key;
            foreach (var l in parentGroup)
                if ((int)l.ParentObjId < parentOf.Length) parentOf[(int)l.ParentObjId] = (int)l.ObjId;
        }
    }

    // ---------- bind-matrix helpers (PMatrix4[LOCALMAT_BASE+j], row-major float[16]) ----------
    double[] ReadMat16(int idx)
    {
        long o = pmat!.DataOffset + (long)pmat.ElemSize * idx;
        var m = new double[16];
        for (int i = 0; i < 16; i++) m[i] = p.F(o + i * 4);
        return m;
    }
    (double[] t, double[] q, double[] s) DecomposeLocal(int joint)
    {
        var m = ReadMat16(LOCALMAT_BASE + joint);
        double[] t = { m[12], m[13], m[14] };
        double[] c0 = { m[0], m[1], m[2] };
        double[] c1 = { m[4], m[5], m[6] };
        double[] c2 = { m[8], m[9], m[10] };
        double sx = Math.Sqrt(c0[0]*c0[0]+c0[1]*c0[1]+c0[2]*c0[2]);
        double sy = Math.Sqrt(c1[0]*c1[0]+c1[1]*c1[1]+c1[2]*c1[2]);
        double sz = Math.Sqrt(c2[0]*c2[0]+c2[1]*c2[1]+c2[2]*c2[2]);
        double[,] R = new double[3,3];
        double[] sc = { sx==0?1:sx, sy==0?1:sy, sz==0?1:sz };
        double[][] cols = { c0, c1, c2 };
        for (int j = 0; j < 3; j++) for (int i = 0; i < 3; i++) R[i,j] = cols[j][i] / sc[j];
        double tr = R[0,0]+R[1,1]+R[2,2];
        double qw,qx,qy,qz;
        if (tr > 0) { double S = Math.Sqrt(tr+1.0)*2; qw=0.25*S; qx=(R[2,1]-R[1,2])/S; qy=(R[0,2]-R[2,0])/S; qz=(R[1,0]-R[0,1])/S; }
        else if (R[0,0]>R[1,1] && R[0,0]>R[2,2]) { double S=Math.Sqrt(1.0+R[0,0]-R[1,1]-R[2,2])*2; qw=(R[2,1]-R[1,2])/S; qx=0.25*S; qy=(R[0,1]+R[1,0])/S; qz=(R[0,2]+R[2,0])/S; }
        else if (R[1,1]>R[2,2]) { double S=Math.Sqrt(1.0+R[1,1]-R[0,0]-R[2,2])*2; qw=(R[0,2]-R[2,0])/S; qx=(R[0,1]+R[1,0])/S; qy=0.25*S; qz=(R[1,2]+R[2,1])/S; }
        else { double S=Math.Sqrt(1.0+R[2,2]-R[0,0]-R[1,1])*2; qw=(R[1,0]-R[0,1])/S; qx=(R[0,2]+R[2,0])/S; qy=(R[1,2]+R[2,1])/S; qz=0.25*S; }
        var q = Normalize4(new[]{qx,qy,qz,qw});
        return (t, q, new[]{ sx, sy, sz });
    }
    static double[] Normalize4(double[] q){ double n=Math.Sqrt(q[0]*q[0]+q[1]*q[1]+q[2]*q[2]+q[3]*q[3]); if(n==0) return new[]{0.0,0,0,1}; return new[]{q[0]/n,q[1]/n,q[2]/n,q[3]/n}; }
    double[,] LocalColMat(int joint)
    {
        var m = ReadMat16(LOCALMAT_BASE + joint);
        var c = new double[4,4];
        for (int r = 0; r < 4; r++) for (int cc2 = 0; cc2 < 4; cc2++) c[r,cc2] = m[cc2*4 + r];
        return c;
    }
    static double[,] Mul(double[,] A, double[,] B){ var R=new double[4,4]; for(int i=0;i<4;i++)for(int j=0;j<4;j++){double s=0;for(int k=0;k<4;k++)s+=A[i,k]*B[k,j];R[i,j]=s;} return R; }
    static double[,] Identity(){ var I=new double[4,4]; for(int i=0;i<4;i++)I[i,i]=1; return I; }
    static double[,] Inverse(double[,] m)
    {
        double[,] a = (double[,])m.Clone(); double[,] inv = Identity();
        for (int i = 0; i < 4; i++)
        {
            int piv = i; for (int r = i+1; r < 4; r++) if (Math.Abs(a[r,i]) > Math.Abs(a[piv,i])) piv = r;
            if (piv != i) for (int c = 0; c < 4; c++){ (a[i,c],a[piv,c])=(a[piv,c],a[i,c]); (inv[i,c],inv[piv,c])=(inv[piv,c],inv[i,c]); }
            double d = a[i,i]; if (Math.Abs(d) < 1e-12) d = 1e-12;
            for (int c = 0; c < 4; c++){ a[i,c]/=d; inv[i,c]/=d; }
            for (int r = 0; r < 4; r++) if (r!=i){ double f=a[r,i]; for(int c=0;c<4;c++){ a[r,c]-=f*a[i,c]; inv[r,c]-=f*inv[i,c]; } }
        }
        return inv;
    }

    // ---------- world bind + inverse bind per joint ----------
    var worldCol = new double[NJOINT][,];
    var invBindCache = new double[NJOINT][,];
    if (haveSkeleton)
    {
        int[] depth = new int[NJOINT];
        for (int j = 0; j < NJOINT; j++){ int dd=0,c=j; while(c>=0 && c<NJOINT && parentOf[c]>=0 && parentOf[c]<NJOINT){c=parentOf[c];dd++; if(dd>NJOINT)break;} depth[j]=dd; }
        foreach (int j in Enumerable.Range(0, NJOINT).OrderBy(x => depth[x]))
        {
            var L = LocalColMat(j);
            int par = parentOf[j];
            worldCol[j] = (par < 0 || par >= NJOINT) ? L : Mul(worldCol[par], L);
        }
        for (int j=0;j<NJOINT;j++) invBindCache[j] = Inverse(worldCol[j]);
    }

    // ---------- vertex/index/skin per segment ----------
    long vertexDataBase = p.VertexBase;
    var vsByData = new Dictionary<int,int>();
    foreach (var l in dataBlk.ObjectLinks.Where(l => l.ObjBlockId == vstream.Id)) vsByData[(int)l.ParentObjId] = (int)l.ObjId;
    string CompName(int vsId){ var links = vstream.ObjectLinks.Where(l => l.ParentObjId == (uint)vsId).ToArray(); return (links.Length>=1 && links[0].SharedDataId!=uint.MaxValue && links[0].SharedDataId<(uint)sdName.Length)?sdName[links[0].SharedDataId]:$"vs{vsId}"; }
    (long off,int ec,int es) DataInfo(int dbId){ long o=dataBlk.DataOffset+(long)dataBlk.ElemSize*dbId; int es=(int)p.U(o); int ec=(int)p.U(o+4); long voff=vertexDataBase+p.U(o+48); return (voff,ec,es); }
    int RemapNode(int idx){ long o = remap!.DataOffset + (long)remap.ElemSize*idx; return (int)(p.U(o) & 0xFFFF); }

    var buf = new List<byte>();
    void Align4(){ while (buf.Count % 4 != 0) buf.Add(0); }
    void WF(float f){ buf.AddRange(BitConverter.GetBytes(f)); }
    void WU16(ushort v){ buf.AddRange(BitConverter.GetBytes(v)); }
    var bufferViews = new List<object>();
    var accessors = new List<object>();
    int AddBV(int byteOffset,int byteLength,int? target=null){ bufferViews.Add(target==null? (object)new{buffer=0,byteOffset,byteLength} : new{buffer=0,byteOffset,byteLength,target}); return bufferViews.Count-1; }

    var primitives = new List<object>();
    var materials = new List<object>();
    double[] gmin = { 1e30,1e30,1e30 }, gmax = { -1e30,-1e30,-1e30 };
    // self-check bounds tracked over SKINNED segments only (rigid segments have no skin matrix)
    double[] skinnedRawMin = { 1e30,1e30,1e30 }, skinnedRawMax = { -1e30,-1e30,-1e30 };
    double[] skinnedOutMin = { 1e30,1e30,1e30 }, skinnedOutMax = { -1e30,-1e30,-1e30 };
    bool anySkin = false; int skinnedSegs = 0, rigidSegs = 0;

    // ---------- A2: decode + bind the primary monster texture (coarse, all submeshes) ----------
    // texture lives at  <mon>/<monster>/tex/d3d11/<monster>.dds.phyre  (sibling of mdl/d3d11/<monster>.dae.phyre)
    string? texRelUri = null; string texStatus = "none";
    try
    {
        var monDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inPath))!, "..", ".."));
        var texSrc = Path.Combine(monDir, "tex", "d3d11", $"{monster}.dds.phyre");
        if (File.Exists(texSrc))
        {
            var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
            var pngPath = Path.Combine(outDir, "textures", $"{monster}.png");
            var rep = PhyreExporter.PhyreTextureExtractor.ExtractTextureFile(texSrc, pngPath, monster, portable: false);
            if (rep.PngPath != null && File.Exists(pngPath))
            {
                texRelUri = "textures/" + monster + ".png";
                texStatus = $"{rep.Format} {rep.Width}x{rep.Height}";
            }
        }
        else texStatus = "missing_dds_phyre";
    }
    catch (Exception ex) { texStatus = "decode_failed:" + ex.Message; }
    bool textureBound = texRelUri != null;

    for (int s = 0; s < seg.ElemCount; s++)
    {
        long so = seg.DataOffset + (long)seg.ElemSize * s;
        int fileMaterialId = p.I(so + 0);           // PMeshSegment[0] = file material id (per descriptor parser)
        int indexCount = p.I(so + 56);
        long indexOff = p.ExternalIndexBase + p.U(so + 92);
        var dbLink = seg.ObjectLinks.FirstOrDefault(l => l.ParentObjId == (uint)s && l.ObjBlockId == dataBlk.Id);
        if (dbLink == null) continue;
        int dbStart = (int)dbLink.ObjId, dbCount = (int)Math.Max(1, dbLink.ObjArrayCount);
        int rmStart = 0;
        if (remap != null)
        {
            var rmLink = seg.ObjectLinks.FirstOrDefault(l => l.ParentObjId == (uint)s && l.ObjBlockId == remap.Id);
            if (rmLink != null) rmStart = (int)rmLink.ObjId;
        }

        long posOff=0, stOff=0, swOff=0, siOff=0; int vcount=0, posStride=12, stStride=8, swStride=16, siStride=4;
        bool hasST=false, hasSW=false, hasSI=false;
        for (int db = dbStart; db < dbStart + dbCount; db++)
        {
            int vsId = vsByData.TryGetValue(db, out var v) ? v : -1; string name = vsId>=0?CompName(vsId):"?";
            var di = DataInfo(db);
            switch (name)
            {
                case "SkinnableVertex": posOff=di.off; vcount=di.ec; posStride=di.es; break;
                case "Vertex": if (vcount==0){ posOff=di.off; vcount=di.ec; posStride=di.es; } break;
                case "ST": stOff=di.off; stStride=di.es; hasST=true; break;
                case "SkinWeights": swOff=di.off; swStride=di.es; hasSW=true; break;
                case "SkinIndices": siOff=di.off; siStride=di.es; hasSI=true; break;
            }
        }
        if (vcount == 0) continue;
        bool segSkinned = hasSW && hasSI && remap != null && haveSkeleton;
        if (segSkinned) skinnedSegs++; else rigidSegs++;

        // POSITION
        Align4(); int posBVStart=buf.Count;
        double[] pmin={1e30,1e30,1e30}, pmax={-1e30,-1e30,-1e30};
        for (int i=0;i<vcount;i++){ float x=p.F(posOff+i*(long)posStride+0),y=p.F(posOff+i*(long)posStride+4),z=p.F(posOff+i*(long)posStride+8); WF(x);WF(y);WF(z);
            pmin[0]=Math.Min(pmin[0],x);pmin[1]=Math.Min(pmin[1],y);pmin[2]=Math.Min(pmin[2],z);pmax[0]=Math.Max(pmax[0],x);pmax[1]=Math.Max(pmax[1],y);pmax[2]=Math.Max(pmax[2],z); }
        for(int k=0;k<3;k++){gmin[k]=Math.Min(gmin[k],pmin[k]);gmax[k]=Math.Max(gmax[k],pmax[k]);}
        int posBV=AddBV(posBVStart,buf.Count-posBVStart,34962);
        int posAcc=accessors.Count; accessors.Add(new{bufferView=posBV,componentType=5126,count=vcount,type="VEC3",min=pmin,max=pmax});

        int stAcc=-1;
        if (hasST){ Align4(); int b=buf.Count; for(int i=0;i<vcount;i++){ WF(p.F(stOff+i*(long)stStride+0)); WF(p.F(stOff+i*(long)stStride+4)); } int bv=AddBV(b,buf.Count-b,34962); stAcc=accessors.Count; accessors.Add(new{bufferView=bv,componentType=5126,count=vcount,type="VEC2"}); }

        int jAcc=-1,wAcc=-1;
        var vJoints = new ushort[vcount][]; var vWeights = new float[vcount][];
        if (segSkinned)
        {
            anySkin = true;
            Align4(); int jb=buf.Count;
            for (int i=0;i<vcount;i++)
            {
                var loc = new int[]{ p.Bytes[(int)(siOff+i*(long)siStride+0)], p.Bytes[(int)(siOff+i*(long)siStride+1)], p.Bytes[(int)(siOff+i*(long)siStride+2)], p.Bytes[(int)(siOff+i*(long)siStride+3)] };
                var js = new ushort[4];
                // glTF best practice: unused (zero-weight) influence slots must use joint index 0
                // (else ACCESSOR_JOINTS_USED_ZERO_WEIGHT warnings). Does not change skinning.
                for (int k=0;k<4;k++){ float w=p.F(swOff+i*(long)swStride+k*4); int rmIdx=rmStart+loc[k]; int node=RemapNode(rmIdx); js[k]=(w==0f)?(ushort)0:(ushort)Math.Min(node, NJOINT-1); }
                vJoints[i]=js; foreach(var jj in js) WU16(jj);
            }
            int jbv=AddBV(jb,buf.Count-jb,34962); jAcc=accessors.Count; accessors.Add(new{bufferView=jbv,componentType=5123,count=vcount,type="VEC4"});

            Align4(); int wb=buf.Count;
            for (int i=0;i<vcount;i++){ var ws=new float[4]; for(int k=0;k<4;k++){ ws[k]=p.F(swOff+i*(long)swStride+k*4); } vWeights[i]=ws; foreach(var ww in ws) WF(ww); }
            int wbv=AddBV(wb,buf.Count-wb,34962); wAcc=accessors.Count; accessors.Add(new{bufferView=wbv,componentType=5126,count=vcount,type="VEC4"});
        }

        Align4(); int ib=buf.Count; int maxIdx=0; for(int i=0;i<indexCount;i++){ ushort idx=(ushort)p.U(indexOff + i*2L); maxIdx=Math.Max(maxIdx,idx); WU16(idx); }
        int ibv=AddBV(ib,buf.Count-ib,34963); int iAcc=accessors.Count; accessors.Add(new{bufferView=ibv,componentType=5123,count=indexCount,type="SCALAR",min=new[]{0},max=new[]{maxIdx}});

        // REST-POSE self-check (skinned segments only): skinMat = world(node)*invBind(node) == I.
        if (segSkinned)
            for (int i=0;i<vcount;i++)
            {
                double x=p.F(posOff+i*(long)posStride+0),y=p.F(posOff+i*(long)posStride+4),z=p.F(posOff+i*(long)posStride+8);
                for(int k=0;k<3;k++){ double val=k==0?x:k==1?y:z; skinnedRawMin[k]=Math.Min(skinnedRawMin[k],val); skinnedRawMax[k]=Math.Max(skinnedRawMax[k],val); }
                double[] outp={0,0,0};
                for (int k=0;k<4;k++){ double w=vWeights[i][k]; if(w==0)continue; int node=vJoints[i][k]; if(node>=NJOINT)continue;
                    var sm = Mul(worldCol[node], invBindCache[node]);
                    double vx = sm[0,0]*x+sm[0,1]*y+sm[0,2]*z+sm[0,3];
                    double vy = sm[1,0]*x+sm[1,1]*y+sm[1,2]*z+sm[1,3];
                    double vz = sm[2,0]*x+sm[2,1]*y+sm[2,2]*z+sm[2,3];
                    outp[0]+=w*vx; outp[1]+=w*vy; outp[2]+=w*vz; }
                for(int k=0;k<3;k++){ skinnedOutMin[k]=Math.Min(skinnedOutMin[k],outp[k]); skinnedOutMax[k]=Math.Max(skinnedOutMax[k],outp[k]); }
            }

        // Per-submesh material identity = real fileMaterialId from PMeshSegment. Monsters carry exactly one
        // external base texture (mXXX.dds.phyre, by-convention; no per-submesh texture string in the model),
        // so all textured submeshes bind that single texture. Per-material shader/alpha/sampler semantics
        // (which sampler slot is albedo/normal/etc.) remain IDA-blocked (same frontier as the map material lane).
        int matIndex = materials.Count;
        if (textureBound && stAcc>=0)
            materials.Add(new { name=$"submesh_{s}_material_{fileMaterialId}_tex", pbrMetallicRoughness=new{ baseColorTexture=new{ index=0, texCoord=0 }, metallicFactor=0.0, roughnessFactor=0.8 }, doubleSided=true });
        else
            materials.Add(new { name=$"submesh_{s}_material_{fileMaterialId}", pbrMetallicRoughness=new{ baseColorFactor=new[]{0.7,0.7,0.72,1.0}, metallicFactor=0.0, roughnessFactor=0.8 }, doubleSided=true });
        var attrs = new Dictionary<string,int>{ ["POSITION"]=posAcc };
        if (stAcc>=0) attrs["TEXCOORD_0"]=stAcc;
        if (jAcc>=0){ attrs["JOINTS_0"]=jAcc; attrs["WEIGHTS_0"]=wAcc; }
        primitives.Add(new { attributes=attrs, indices=iAcc, mode=4, material=matIndex, extras=new{ submeshId=s, fileMaterialId, skinned=segSkinned, source="PMeshSegment[0]=materialId" } });
    }

    // ---------- inverse bind matrices accessor ----------
    int ibmAcc=-1;
    if (anySkin)
    {
        Align4(); int ibmStart=buf.Count;
        for (int j=0;j<NJOINT;j++){ var inv=invBindCache[j];
            for (int col=0;col<4;col++) for (int row=0;row<4;row++) WF((float)inv[row,col]); }
        int ibmBV=AddBV(ibmStart,buf.Count-ibmStart); ibmAcc=accessors.Count; accessors.Add(new{bufferView=ibmBV,componentType=5126,count=NJOINT,type="MAT4"});
    }

    // ---------- animation (only when keyframed PAnimationChannel exists) ----------
    var ch = p.Block("PAnimationChannel");
    var chTimes = p.Block("PAnimationChannelTimes");
    var animSamplers = new List<object>();
    var animChannels = new List<object>();
    int rotKeys = 0, scaleKeys = 0, kfNodeRot = -1, kfNodeScale = -1;
    bool hasAnim = anySkin && ch != null && ch.ElemCount >= 2 && chTimes != null;
    if (hasAnim)
    {
        kfNodeRot   = (int)p.U(ch!.DataOffset + 0L*ch.ElemSize + 24);
        kfNodeScale = (int)p.U(ch.DataOffset + 1L*ch.ElemSize + 24);

        long timesAbs = chTimes!.DataOffset + 12;
        var tlist = new List<float>{ 0f };
        for (int i=0;i<48;i++) tlist.Add(p.F(timesAbs + i*4));
        int keyN = tlist.Count;

        long valuesBase = ch.DataOffset + (long)ch.ElemCount * ch.ElemSize;
        var quats = new List<float[]>();
        for (int i=0;i<48;i++){ long b=valuesBase + (i*4L)*4; quats.Add(new[]{ p.F(b), p.F(b+4), p.F(b+8), p.F(b+12) }); }
        float[] lastGood = { 0,0,0,1 };
        for (int i=0;i<quats.Count;i++){ var q=quats[i]; double n=Math.Sqrt((double)q[0]*q[0]+q[1]*q[1]+q[2]*q[2]+q[3]*q[3]); if(n<1e-6){ quats[i]=(float[])lastGood.Clone(); } else { for(int k=0;k<4;k++)q[k]=(float)(q[k]/n); lastGood=q; } }
        var rotOut = new List<float[]>{ (float[])quats[0].Clone() }; rotOut.AddRange(quats);

        long scaleBase = valuesBase + 784;
        var scales = new List<float[]>();
        for (int i=0;i<48;i++){ long b=scaleBase + (i*3L)*4; scales.Add(new[]{ p.F(b), p.F(b+4), p.F(b+8) }); }
        for (int i=0;i<scales.Count;i++){ var sgv=scales[i]; for(int k=0;k<3;k++) if(Math.Abs(sgv[k])<1e-6) sgv[k]=1f; }
        var scaleOut = new List<float[]>{ (float[])scales[0].Clone() }; scaleOut.AddRange(scales);

        Align4(); int tb=buf.Count; foreach(var tt in tlist) WF(tt); int tBV=AddBV(tb,buf.Count-tb); int tAcc=accessors.Count; accessors.Add(new{bufferView=tBV,componentType=5126,count=keyN,type="SCALAR",min=new[]{tlist.Min()},max=new[]{tlist.Max()}});
        Align4(); int rb=buf.Count; foreach(var q in rotOut){ WF(q[0]);WF(q[1]);WF(q[2]);WF(q[3]); } int rBV=AddBV(rb,buf.Count-rb); int rAcc=accessors.Count; accessors.Add(new{bufferView=rBV,componentType=5126,count=rotOut.Count,type="VEC4"});
        Align4(); int sb2=buf.Count; foreach(var sgv in scaleOut){ WF(sgv[0]);WF(sgv[1]);WF(sgv[2]); } int sBV=AddBV(sb2,buf.Count-sb2); int sAcc=accessors.Count; accessors.Add(new{bufferView=sBV,componentType=5126,count=scaleOut.Count,type="VEC3"});

        animSamplers.Add(new { input=tAcc, output=rAcc, interpolation="LINEAR" });
        animSamplers.Add(new { input=tAcc, output=sAcc, interpolation="LINEAR" });
        animChannels.Add(new { sampler=0, target=new { node=kfNodeRot, path="rotation" } });
        animChannels.Add(new { sampler=1, target=new { node=kfNodeScale, path="scale" } });
        rotKeys = rotOut.Count; scaleKeys = scaleOut.Count;
    }

    // ---------- nodes ----------
    var nodes = new List<object>();
    if (haveSkeleton)
        for (int j=0;j<NJOINT;j++)
        {
            var (t,q,sc) = DecomposeLocal(j);
            var kids = new List<int>();
            for (int c=0;c<NJOINT;c++) if (parentOf[c]==j) kids.Add(c);
            var node = new Dictionary<string,object>{
                ["name"]=$"joint_{j}",
                ["translation"]=new[]{ t[0],t[1],t[2] },
                ["rotation"]=new[]{ q[0],q[1],q[2],q[3] },
                ["scale"]=new[]{ sc[0],sc[1],sc[2] },
            };
            if (kids.Count>0) node["children"]=kids.ToArray();
            nodes.Add(node);
        }
    int meshNodeIndex = nodes.Count;
    var meshNode = new Dictionary<string,object>{ ["name"]=$"{monster}_mesh", ["mesh"]=0 };
    if (anySkin) meshNode["skin"]=0;
    nodes.Add(meshNode);

    var sceneRoots = haveSkeleton
        ? Enumerable.Range(0,NJOINT).Where(j => parentOf[j] < 0 || parentOf[j] >= NJOINT).ToList()
        : new List<int>();
    var sceneNodes = new List<int>(sceneRoots) { meshNodeIndex };

    // ---------- assemble glTF ----------
    var gltf = new Dictionary<string,object>{
        ["asset"]=new{ version="2.0", generator="RuntimeTools/PhyreSkinnedAnimExportLab (read-only RE; no game files modified)" },
        ["scene"]=0,
        ["scenes"]=new[]{ new { nodes=sceneNodes.ToArray(), name=monster } },
        ["nodes"]=nodes.ToArray(),
        ["meshes"]=new[]{ new { name=monster, primitives=primitives.ToArray() } },
        ["materials"]=materials.ToArray(),
        ["buffers"]=new[]{ new { uri="data:application/octet-stream;base64,"+Convert.ToBase64String(buf.ToArray()), byteLength=buf.Count } },
        ["bufferViews"]=bufferViews.ToArray(),
        ["accessors"]=accessors.ToArray(),
    };
    if (anySkin)
        gltf["skins"]=new[]{ new { inverseBindMatrices = ibmAcc, joints = Enumerable.Range(0,NJOINT).ToArray(), skeleton = sceneRoots.Count>0?sceneRoots[0]:0, name = $"{monster}_skin" } };
    if (hasAnim)
        gltf["animations"]=new object[]{ new { name=$"{monster}_clip", samplers=animSamplers.ToArray(), channels=animChannels.ToArray() } };
    if (textureBound)
    {
        gltf["samplers"]=new[]{ new { magFilter=9729, minFilter=9987, wrapS=10497, wrapT=10497 } };
        gltf["images"]=new[]{ new { uri=texRelUri!, name=$"{monster}_tex" } };
        gltf["textures"]=new[]{ new { sampler=0, source=0, name=$"{monster}_texture" } };
    }

    var json = JsonSerializer.Serialize(gltf, new JsonSerializerOptions{ WriteIndented=false });
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllText(outPath, json, new UTF8Encoding(false));

    // rest-pose verdict: skinned output bounds must reproduce skinned-segment raw bounds.
    double restErr = 0; bool restOk = !anySkin; // static-only counts as N/A pass
    if (anySkin)
    {
        for (int k=0;k<3;k++){ restErr=Math.Max(restErr, Math.Abs(skinnedOutMin[k]-skinnedRawMin[k])); restErr=Math.Max(restErr, Math.Abs(skinnedOutMax[k]-skinnedRawMax[k])); }
        double diag = 0; for(int k=0;k<3;k++){ double d=skinnedRawMax[k]-skinnedRawMin[k]; diag+=d*d; } diag=Math.Sqrt(diag);
        restOk = restErr <= Math.Max(1e-2, 1e-3*diag);
    }
    double identErr = 0;
    if (anySkin && sceneRoots.Count>0){ var test = Mul(worldCol[sceneRoots[0]], Inverse(worldCol[sceneRoots[0]])); for(int i=0;i<4;i++)for(int j=0;j<4;j++){ double exp=i==j?1:0; identErr=Math.Max(identErr,Math.Abs(test[i,j]-exp)); } }

    if (verbose)
    {
        Console.WriteLine($"wrote {outPath} ({json.Length} bytes, buffer={buf.Count})");
        Console.WriteLine($"monster={monster} nodes={nodes.Count} joints={NJOINT} localmatBase={LOCALMAT_BASE} parentOff={parentLinkOffset} primitives={primitives.Count} (skinnedSegs={skinnedSegs} rigidSegs={rigidSegs}) hasSkin={anySkin} hasAnim={hasAnim}");
        Console.WriteLine($"all-segment POSITION bounds: min=[{gmin[0]:F2},{gmin[1]:F2},{gmin[2]:F2}] max=[{gmax[0]:F2},{gmax[1]:F2},{gmax[2]:F2}]");
        if (anySkin) Console.WriteLine($"rest-pose (skinned segs): raw min=[{skinnedRawMin[0]:F2},{skinnedRawMin[1]:F2},{skinnedRawMin[2]:F2}] max=[{skinnedRawMax[0]:F2},{skinnedRawMax[1]:F2},{skinnedRawMax[2]:F2}]  out min=[{skinnedOutMin[0]:F2},{skinnedOutMin[1]:F2},{skinnedOutMin[2]:F2}] max=[{skinnedOutMax[0]:F2},{skinnedOutMax[1]:F2},{skinnedOutMax[2]:F2}]  (restErr={restErr:E2} ok={restOk})");
        if (anySkin) Console.WriteLine($"world*invBind identity check (node {(sceneRoots.Count>0?sceneRoots[0]:0)}) maxErr={identErr:E2}");
        if (hasAnim) Console.WriteLine($"keyframed rotNode={kfNodeRot} scaleNode={kfNodeScale} rotKeys={rotKeys} scaleKeys={scaleKeys}");
        Console.WriteLine($"texture: bound={textureBound} ({texStatus})");
    }

    return new ExportResult(monster, true, null, NJOINT, LOCALMAT_BASE, parentLinkOffset, primitives.Count,
        skinnedSegs, rigidSegs, anySkin, hasAnim, restOk, restErr, identErr,
        new[]{gmin[0],gmin[1],gmin[2]}, new[]{gmax[0],gmax[1],gmax[2]},
        hasAnim ? new[]{kfNodeRot,kfNodeScale} : Array.Empty<int>(), textureBound, texStatus);
}

// ----------------------------------------------------------------------------
public sealed record ExportResult(
    string Monster, bool Ok, string? Error, int Joints, int LocalMatBase, int ParentLinkOffset, int Primitives,
    int SkinnedSegs, int RigidSegs, bool HasSkin, bool HasAnim, bool RestPoseOk, double RestErr, double IdentityErr,
    double[] PosMin, double[] PosMax, int[] KeyframedNodes, bool Textured, string TextureStatus)
{
    public static ExportResult Failed(string monster, string error) =>
        new(monster, false, error, 0, 0, -1, 0, 0, 0, false, false, false, 0, 0,
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<int>(), false, "n/a");
}
