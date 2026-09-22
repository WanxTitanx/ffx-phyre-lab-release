// GPU raster backend — GL entry points that Avalonia's GlInterface does not
// expose (it only surfaces the subset its own compositor needs). Resolved via
// gl.GetProcAddress so we stay inside the shared compositor context.
using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace FfxLab;

/// <summary>GL constants not present in the public GlConsts surface — fixed
/// OpenGL enum values (stable across GL versions).</summary>
static class GL
{
    public const int TEXTURE_2D = 0x0DE1, TEXTURE0 = 0x84C0;
    public const int TRIANGLES = 0x0004;
    public const int FLOAT = 0x1406, UNSIGNED_BYTE = 0x1401, UNSIGNED_INT = 0x1405;
    public const int ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893;
    public const int STATIC_DRAW = 0x88E4, DYNAMIC_DRAW = 0x88E8;
    public const int VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30;
    public const int COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    public const int COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x200;
    public const int RGBA = 0x1908, BGRA = 0x80E1, RGBA8 = 0x8058;
    public const int TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800;
    public const int TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
    public const int NEAREST = 0x2600, LINEAR = 0x2601, REPEAT = 0x2901, CLAMP_TO_EDGE = 0x812F;
    public const int DEPTH_TEST = 0x0B71, BLEND = 0x0BE2, CULL_FACE = 0x0B44, SCISSOR_TEST = 0x0C11;
    public const int LESS = 0x0201, LEQUAL = 0x0203, ALWAYS = 0x0207;
    public const int BACK = 0x0405, FRONT = 0x0404, CCW = 0x0901, CW = 0x0900;
    public const int SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303;
    public const int ONE = 1, ZERO = 0, DST_COLOR = 0x0306;
    public const int FUNC_ADD = 0x8006, FUNC_REVERSE_SUBTRACT = 0x800B, FUNC_SUBTRACT = 0x800A;
    public const int UNPACK_ALIGNMENT = 0x0CF5, PACK_ALIGNMENT = 0x0D05;
    public const int VIEWPORT = 0x0BA2, FRAMEBUFFER = 0x8D40, COLOR_ATTACHMENT0 = 0x8CE0;
}

/// <summary>Delegates bound to the live GL context via gl.GetProcAddress.
/// Created once per GlViewport init; every field maps 1:1 to a GL symbol.</summary>
sealed class GlFuncs
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void BlendFuncD(int sfactor, int dfactor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void BlendEquationD(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void BlendFuncSeparateD(int srgb, int drgb, int sa, int da);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CullFaceD(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FrontFaceD(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ScissorD(int x, int y, int w, int h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Uniform1iD(int loc, int v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Uniform1fD(int loc, float x);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Uniform4fD(int loc, float x, float y, float z, float w);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Uniform2fD(int loc, float x, float y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LineWidthD(float w);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PixelStoreiD(int name, int v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TexSubImage2DD(int target, int level, int x, int y, int w, int h, int fmt, int type, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ReadPixelsD(int x, int y, int w, int h, int fmt, int type, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DrawElementsD(int mode, int count, int type, IntPtr indices);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void BufferSubDataD(int target, IntPtr offs, IntPtr size, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GenerateMipmapD(int target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DepthRangeD(float near, float far);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PolygonOffsetD(float factor, float units);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Uniform1fvD(int loc, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetErrorD();

    public BlendFuncD BlendFunc = null!;
    public BlendEquationD BlendEquation = null!;
    public BlendFuncSeparateD BlendFuncSeparate = null!;
    public CullFaceD CullFace = null!;
    public FrontFaceD FrontFace = null!;
    public ScissorD Scissor = null!;
    public Uniform1iD Uniform1i = null!;
    public Uniform1fD Uniform1f = null!;
    public Uniform4fD Uniform4f = null!;
    public Uniform2fD Uniform2f = null!;
    public LineWidthD LineWidth = null!;
    public PixelStoreiD PixelStorei = null!;
    public TexSubImage2DD TexSubImage2D = null!;
    public ReadPixelsD ReadPixels = null!;
    public BufferSubDataD BufferSubData = null!;
    public GenerateMipmapD GenerateMipmap = null!;
    public PolygonOffsetD PolygonOffset = null!;

    static T Bind<T>(GlInterface gl, string name) where T : Delegate
    {
        var p = gl.GetProcAddress(name);
        if (p == IntPtr.Zero) throw new MissingMethodException($"GL symbol {name} unavailable");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }
    static T? BindOpt<T>(GlInterface gl, string name) where T : Delegate
    {
        var p = gl.GetProcAddress(name);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    public static GlFuncs Load(GlInterface gl) => new()
    {
        BlendFunc = Bind<BlendFuncD>(gl, "glBlendFunc"),
        BlendEquation = BindOpt<BlendEquationD>(gl, "glBlendEquation") ?? ((m) => { }),
        BlendFuncSeparate = BindOpt<BlendFuncSeparateD>(gl, "glBlendFuncSeparate")
            ?? ((a, b, c, d) => { }),
        CullFace = Bind<CullFaceD>(gl, "glCullFace"),
        FrontFace = Bind<FrontFaceD>(gl, "glFrontFace"),
        Scissor = Bind<ScissorD>(gl, "glScissor"),
        Uniform1i = Bind<Uniform1iD>(gl, "glUniform1i"),
        Uniform1f = Bind<Uniform1fD>(gl, "glUniform1f"),
        Uniform4f = Bind<Uniform4fD>(gl, "glUniform4f"),
        Uniform2f = BindOpt<Uniform2fD>(gl, "glUniform2f") ?? ((l, x, y) => { }),
        LineWidth = BindOpt<LineWidthD>(gl, "glLineWidth") ?? ((w) => { }),
        PixelStorei = Bind<PixelStoreiD>(gl, "glPixelStorei"),
        TexSubImage2D = Bind<TexSubImage2DD>(gl, "glTexSubImage2D"),
        ReadPixels = BindOpt<ReadPixelsD>(gl, "glReadPixels") ?? ((x, y, w, h, f, t, d) => { }),
        BufferSubData = BindOpt<BufferSubDataD>(gl, "glBufferSubData") ?? ((t, o, s, d) => { }),
        GenerateMipmap = BindOpt<GenerateMipmapD>(gl, "glGenerateMipmap") ?? ((t) => { }),
        PolygonOffset = BindOpt<PolygonOffsetD>(gl, "glPolygonOffset") ?? ((f, u) => { }),
    };
}
