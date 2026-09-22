// SoftRenderer — CPU rasterizer for the inspector viewport (P14).
// Port of tools/render_gltf.py semantics: LBS skinning in bind pose,
// perspective camera (orbit yaw/pitch/dist/target), z-buffer, nearest
// texture sampling, wireframe mode, per-joint weight heatmap.
// Produces a raw ARGB uint[] framebuffer — UI wraps it in a bitmap;
// headless mode encodes it via Png.Encode. No Avalonia dependency.

using System.Numerics;

namespace PhyreInspector;

public sealed class SoftRenderer
{
    public int W = 960, H = 640;
    public float Yaw = 0.6f, Pitch = -0.25f, Dist;
    public readonly HashSet<int> HiddenPrims = new(); // prim indices skipped in both passes
    public Vector3 Target;
    public bool Wireframe;
    public int HighlightJoint = -1;   // >=0: weight heatmap for this skin joint

    private byte[]? _tex; private int _texW, _texH;
    private uint[] _fb = Array.Empty<uint>();
    private float[] _zb = Array.Empty<float>();

    public void SetTexture(string pngPath)
    {
        try
        {
            var (bgra, w, h) = Png.Decode(pngPath);
            _tex = bgra; _texW = w; _texH = h;
        }
        catch { _tex = null; }
    }

    // Render into the internal framebuffer; returns it (W*H ARGB).
    public uint[] Render(GltfScene s)
    {
        if (_fb.Length != W * H) { _fb = new uint[W * H]; _zb = new float[W * H]; }
        Array.Fill(_fb, 0xFF1A1A22u); Array.Fill(_zb, float.MaxValue);

        var world = s.JointWorld();
        var skinned = new Vector3[s.Prims.Count][];
        for (int pi = 0; pi < s.Prims.Count; pi++)
        {
            if (HiddenPrims.Contains(pi)) { skinned[pi] = Array.Empty<Vector3>(); continue; }
            var p = s.Prims[pi];
            var sp = new Vector3[p.Pos.Length];
            bool hasSkin = s.SkinJoints.Length > 0 && p.Joints.GetLength(0) == p.Pos.Length;
            for (int i = 0; i < p.Pos.Length; i++)
            {
                if (!hasSkin) { sp[i] = p.Pos[i]; continue; }
                var acc = Vector3.Zero;
                for (int k = 0; k < 4; k++)
                {
                    float w = p.Weights[i, k];
                    if (w <= 0) continue;
                    int j = p.Joints[i, k];
                    if (j >= s.SkinJoints.Length) continue;
                    var m = s.InvBind[j] * world[s.SkinJoints[j]];
                    acc += Vector3.Transform(p.Pos[i], m) * w;
                }
                sp[i] = acc;
            }
            skinned[pi] = sp;
        }

        // frame scene if no camera set
        var center = (s.BoundsMin + s.BoundsMax) * 0.5f;
        var radius = MathF.Max(0.5f, (s.BoundsMax - s.BoundsMin).Length() * 0.5f);
        if (Dist <= 0) { Dist = radius * 2.4f; Target = center; }

        // camera basis
        var eye = Target + Dist * new Vector3(MathF.Cos(Yaw) * MathF.Cos(Pitch),
                                              MathF.Sin(Pitch),
                                              MathF.Sin(Yaw) * MathF.Cos(Pitch));
        var fwd = Vector3.Normalize(Target - eye);
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        var up = Vector3.Cross(right, fwd);
        float f = H * 1.1f;

        for (int pi = 0; pi < s.Prims.Count; pi++)
        {
            if (HiddenPrims.Contains(pi)) continue;
            var p = s.Prims[pi]; var sp = skinned[pi];
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                int i0 = p.Indices[t], i1 = p.Indices[t + 1], i2 = p.Indices[t + 2];
                if (i0 >= sp.Length || i1 >= sp.Length || i2 >= sp.Length) continue;
                var s0 = Proj(sp[i0], eye, fwd, right, up, f);
                var s1 = Proj(sp[i1], eye, fwd, right, up, f);
                var s2 = Proj(sp[i2], eye, fwd, right, up, f);
                if (s0.Z <= 0 || s1.Z <= 0 || s2.Z <= 0) continue;
                if (Wireframe)
                {
                    Line(s0, s1, 0xFFCCCCCC); Line(s1, s2, 0xFFCCCCCC); Line(s2, s0, 0xFFCCCCCC);
                }
                else
                {
                    FillTri(s0, s1, s2,
                        p.Uv.Length > i0 ? p.Uv[i0] : Vector2.Zero,
                        p.Uv.Length > i1 ? p.Uv[i1] : Vector2.Zero,
                        p.Uv.Length > i2 ? p.Uv[i2] : Vector2.Zero,
                        p, new[] { i0, i1, i2 });
                }
            }
        }
        return _fb;
    }

    Vector3 Proj(Vector3 v, Vector3 eye, Vector3 fwd, Vector3 right, Vector3 up, float f)
    {
        var d = v - eye;
        float z = Vector3.Dot(d, fwd);
        if (z <= 0.01f) return new Vector3(0, 0, -1);
        float x = Vector3.Dot(d, right) / z, y = Vector3.Dot(d, up) / z;
        return new Vector3(W * 0.5f + f * x, H * 0.5f - f * y, z);
    }

    void Line(Vector3 a, Vector3 b, uint col)
    {
        int x0 = (int)a.X, y0 = (int)a.Y, x1 = (int)b.X, y1 = (int)b.Y;
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, e = dx + dy;
        while (true)
        {
            if (x0 >= 0 && y0 >= 0 && x0 < W && y0 < H) _fb[y0 * W + x0] = col;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * e;
            if (e2 >= dy) { e += dy; x0 += sx; }
            if (e2 <= dx) { e += dx; y0 += sy; }
        }
    }

    void FillTri(Vector3 s0, Vector3 s1, Vector3 s2, Vector2 t0, Vector2 t1, Vector2 t2,
                 GltfScene.Prim p, int[] vi)
    {
        float minX = MathF.Min(s0.X, MathF.Min(s1.X, s2.X)), maxX = MathF.Max(s0.X, MathF.Max(s1.X, s2.X));
        float minY = MathF.Min(s0.Y, MathF.Min(s1.Y, s2.Y)), maxY = MathF.Max(s0.Y, MathF.Max(s1.Y, s2.Y));
        int x0 = Math.Max(0, (int)minX), x1 = Math.Min(W - 1, (int)MathF.Ceiling(maxX));
        int y0 = Math.Max(0, (int)minY), y1 = Math.Min(H - 1, (int)MathF.Ceiling(maxY));
        float den = (s1.Y - s2.Y) * (s0.X - s2.X) + (s2.X - s1.X) * (s0.Y - s2.Y);
        if (MathF.Abs(den) < 1e-7f) return;

        // face normal for lambert-ish shading (screen-space normal is fine here)
        var n = Vector3.Normalize(Vector3.Cross(s1 - s0, s2 - s0));
        float shade = 0.45f + 0.55f * MathF.Abs(n.Z);

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float w0 = ((s1.Y - s2.Y) * (x - s2.X) + (s2.X - s1.X) * (y - s2.Y)) / den;
                float w1 = ((s2.Y - s0.Y) * (x - s2.X) + (s0.X - s2.X) * (y - s2.Y)) / den;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = w0 * s0.Z + w1 * s1.Z + w2 * s2.Z;
                int o = y * W + x;
                if (z >= _zb[o]) continue;
                _zb[o] = z;

                if (HighlightJoint >= 0 && p.Joints.GetLength(0) > 0)
                {
                    // heatmap: barycentric blend of selected joint's weight on verts
                    float hw = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        float bary = k == 0 ? w0 : k == 1 ? w1 : w2;
                        for (int j = 0; j < 4; j++)
                            if (p.Joints[vi[k], j] == HighlightJoint)
                                hw += p.Weights[vi[k], j] * bary;
                    }
                    byte rr = (byte)Math.Clamp((int)(hw * 380), 0, 255);
                    _fb[o] = 0xFF000000 | (uint)(rr << 16) | (uint)((255 - rr) / 3);
                    continue;
                }

                uint col;
                if (_tex != null && p.Uv.Length > 0)
                {
                    var uv = t0 * w0 + t1 * w1 + t2 * w2;
                    int tx = ((int)(uv.X * _texW) % _texW + _texW) % _texW;
                    int ty = ((int)(uv.Y * _texH) % _texH + _texH) % _texH;
                    int ti = (ty * _texW + tx) * 4;
                    uint b = _tex[ti], g = _tex[ti + 1], rr = _tex[ti + 2];
                    col = 0xFF000000 | ((uint)(rr * shade) & 0xFF) << 16
                                     | ((uint)(g * shade) & 0xFF) << 8
                                     | ((uint)(b * shade) & 0xFF);
                }
                else
                    col = 0xFF000000 | (uint)(200 * shade) << 16 | (uint)(160 * shade) << 8 | (uint)(255 * shade);
                _fb[o] = col;
            }
    }
}
