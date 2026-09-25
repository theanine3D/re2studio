using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace Re2.Studio;

/// <summary>A GPU texture plus its dimensions, so panels can size an ImGui image correctly.</summary>
public sealed class GlTexture : IDisposable
{
    public uint Handle { get; }
    public int Width { get; }
    public int Height { get; }

    private readonly GL _gl;

    public unsafe GlTexture(GL gl, ReadOnlySpan<byte> rgba, int width, int height, bool smooth = false)
    {
        _gl = gl;
        Width = width;
        Height = height;

        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);

        fixed (byte* pixels = rgba)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        // Nearest by default: these are tiny N64 textures and blurring them hides detail.
        int filter = (int)(smooth ? TextureMinFilter.Linear : TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
        // Clamped, not repeated: measured in game, UVs outside 0..1 extend the edge pixels rather than
        // tiling -- the same behaviour as Blender's "Extend" on an Image Texture node.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Replaces the pixels without allocating a new texture, for anything that changes every frame.
    /// </summary>
    public unsafe void Update(ReadOnlySpan<byte> rgba)
    {
        _gl.BindTexture(TextureTarget.Texture2D, Handle);

        fixed (byte* pixels = rgba)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)Width, (uint)Height,
                              PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose() => _gl.DeleteTexture(Handle);
}

/// <summary>
/// Bounded cache of GPU textures keyed by asset id, so scrolling a gallery of 1,200 images does not
/// upload everything at once or leak handles.
/// </summary>
public sealed class TextureCache : IDisposable
{
    private readonly GL _gl;
    private readonly int _limit;
    private readonly Dictionary<int, GlTexture> _entries = new();
    private readonly Dictionary<int, long> _lastUsed = new();
    private readonly Queue<int> _order = new();

    private long _frame;

    /// <summary>A texture the cache does not own, for content that changes rather than repeats.</summary>
    public GlTexture Create(ReadOnlySpan<byte> rgba, int width, int height, bool smooth = false)
        => new(_gl, rgba, width, height, smooth);

    public TextureCache(GL gl, int limit = 2048)
    {
        _gl = gl;
        _limit = limit;
    }

    /// <summary>Marks the start of a frame.</summary>
    public void BeginFrame() => _frame++;

    public GlTexture Get(int key, Func<(byte[] Rgba, int Width, int Height)> load)
    {
        _lastUsed[key] = _frame;

        if (_entries.TryGetValue(key, out var existing)) return existing;

        var (rgba, width, height) = load();
        var texture = new GlTexture(_gl, rgba, width, height);
        _entries[key] = texture;
        _order.Enqueue(key);

        // Never evict a texture that has already been drawn this frame.
        int examined = 0;
        while (_entries.Count > _limit && _order.Count > 0 && examined < _order.Count)
        {
            int candidate = _order.Dequeue();
            examined++;

            if (!_entries.ContainsKey(candidate)) continue;          // already gone

            if (_lastUsed.TryGetValue(candidate, out long used) && used == _frame)
            {
                _order.Enqueue(candidate);                            // in use now: keep, try later
                continue;
            }

            if (_entries.Remove(candidate, out var old)) old.Dispose();
            _lastUsed.Remove(candidate);
            examined = 0;                                             // a real eviction resets the sweep
        }

        return texture;
    }

    /// <summary>Drops one texture, so the next Get builds it afresh from changed content.</summary>
    public void Forget(int key)
    {
        if (_entries.Remove(key, out var old)) old.Dispose();
        _lastUsed.Remove(key);
    }

    public void Dispose()
    {
        foreach (var texture in _entries.Values) texture.Dispose();
        _entries.Clear();
        _lastUsed.Clear();
        _order.Clear();
    }
}
