using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Re2.Core.Formats;
using Silk.NET.OpenGL;

namespace Re2.Studio;

/// <summary>Renders a posed character into an offscreen framebuffer that ImGui can display.</summary>
public sealed class ModelViewport : IDisposable
{
    /// <summary>Debug switch: draw mesh parts the rig has no joint for, at identity.</summary>
    public static bool DrawPartsWithoutJoints;

    /// <summary>
    /// Debug switch: ignore the rotation of joints whose mesh part is a degenerate stub, on the
    /// theory that such a part marks a bone the model does not actually have.
    /// </summary>
    public static bool SkipStubJointRotation;

    private const string VertexShader = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec3 aNormal;
        layout(location=2) in vec2 aUv;
        uniform mat4 uMvp;
        out vec3 vNormal;
        out vec2 vUv;
        void main() {
            gl_Position = uMvp * vec4(aPos, 1.0);
            vNormal = aNormal;
            vUv = aUv;
        }
        """;

    private const string FragmentShader = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vUv;
        uniform sampler2D uTexture;
        uniform int uUseTexture;
        uniform vec3 uTint;
        uniform float uHighlight;
        out vec4 FragColor;
        void main() {
            vec3 n = normalize(vNormal);
            float light = 0.45 + 0.55 * max(dot(n, normalize(vec3(0.35, 0.75, 0.55))), 0.0);
            vec4 base = uUseTexture == 1 ? texture(uTexture, vUv) : vec4(uTint, 1.0);
            if (base.a < 0.5) discard;
            vec3 lit = base.rgb * light;
            // Mixed rather than replaced, so the shape of what is highlighted stays readable.
            FragColor = vec4(mix(lit, vec3(1.0, 0.92, 0.15), uHighlight * 0.65), 1.0);
        }
        """;

    private readonly GL _gl;
    private uint _program, _vao, _vbo, _fbo, _colour, _depth;
    private int _width, _height;

    public float Yaw = 0.6f;
    public float Pitch = 0.15f;

    /// <summary>Camera distance as a multiple of the model's radius, so any model frames sensibly.</summary>
    public float Distance = 2.6f;

    /// <summary>Model-space centre and radius, used to frame whatever is loaded.</summary>
    private Vector3 _centre;
    private float _radius = 1f;
    public bool ShowTextures = true;
    public bool ColourByPart;

    /// <summary>Draws a marker at every joint, through the model, to show where the rig actually is.</summary>
    public bool ShowJoints;

    public uint ColourTexture => _colour;

    /// <summary>A run of vertices sharing one texture.</summary>
    private readonly List<(int Start, int Count, int TextureIndex, Vector3 Tint)> _batches = new();

    /// <summary>Texture slots to pick out in the render.</summary>
    public HashSet<int> HighlightTextures { get; } = new();
    private readonly List<float> _vertices = new();

    /// <summary>
    /// The joint markers, held apart from the model's own batches because they are drawn afterwards
    /// with depth testing off -- a marker buried inside a limb is exactly the one worth seeing.
    /// </summary>
    private readonly List<(int Start, int Count, Vector3 Tint)> _jointBatches = new();

    public ModelViewport(GL gl)
    {
        _gl = gl;
        _program = BuildProgram();
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        Resize(720, 720);
    }

    private uint BuildProgram()
    {
        uint Compile(ShaderType type, string source)
        {
            uint shader = _gl.CreateShader(type);
            _gl.ShaderSource(shader, source);
            _gl.CompileShader(shader);
            string log = _gl.GetShaderInfoLog(shader);
            if (!string.IsNullOrWhiteSpace(log)) throw new InvalidOperationException($"{type}: {log}");
            return shader;
        }

        uint vs = Compile(ShaderType.VertexShader, VertexShader);
        uint fs = Compile(ShaderType.FragmentShader, FragmentShader);
        uint program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return program;
    }

    public unsafe void Resize(int width, int height)
    {
        if (width == _width && height == _height) return;
        _width = Math.Max(64, width);
        _height = Math.Max(64, height);

        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _gl.DeleteTexture(_colour); _gl.DeleteRenderbuffer(_depth); }

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colour = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _colour);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)_width, (uint)_height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colour, 0);

        _depth = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)_width, (uint)_height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depth);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static readonly Vector3[] PartColours =
    {
        new(0.86f,0.31f,0.31f), new(0.31f,0.78f,0.47f), new(0.35f,0.55f,0.90f), new(0.90f,0.74f,0.27f),
        new(0.78f,0.43f,0.86f), new(0.35f,0.82f,0.82f), new(0.94f,0.55f,0.24f), new(0.67f,0.67f,0.67f),
        new(1.00f,0.47f,0.63f), new(0.47f,1.00f,0.47f), new(0.47f,0.47f,1.00f), new(0.90f,0.90f,0.43f),
        new(1.00f,0.63f,0.24f), new(0.63f,1.00f,0.86f), new(0.78f,0.55f,1.00f), new(0.55f,0.55f,0.55f),
        new(0.40f,0.40f,0.40f)
    };

    /// <summary>Rebuilds the vertex buffer for one pose.</summary>
    public unsafe void SetMesh(MeshFile mesh, PoseBank? bank, Pose? pose)
    {
        _vertices.Clear();
        _batches.Clear();
        _jointBatches.Clear();

        var stub = new bool[mesh.Parts.Count];
        if (SkipStubJointRotation)
            for (int i = 0; i < mesh.Parts.Count; i++)
                stub[i] = mesh.Parts[i].SubMeshes.Sum(m => m.Vertices.Count) is > 0 and <= 4;

        var transforms = BuildTransforms(bank, pose, mesh.Parts.Count, stub);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int p = 0; p < mesh.Parts.Count; p++)
        {
            var part = mesh.Parts[p];

            // A mesh can carry more parts than the rig drives (asset 5599 has 17 against 15 joints).
            if (bank is not null && p >= bank.PartCount && !DrawPartsWithoutJoints) continue;

            var world = p < transforms.Length ? transforms[p] : Matrix4x4.Identity;

            foreach (var sub in part.SubMeshes)
            {
                int start = _vertices.Count / 8;

                foreach (var t in sub.Triangles)
                    foreach (int index in new[] { t.A, t.B, t.C })
                    {
                        var v = sub.Vertices[index];
                        var position = Vector3.Transform(new Vector3(v.X, v.Y, v.Z), world) * 0.001f;
                        var (nx, ny, nz) = v.AsNormal();
                        var normal = Vector3.TransformNormal(new Vector3(nx, ny, nz), world);
                        if (normal.LengthSquared() < 1e-6f) normal = Vector3.UnitY;
                        normal = Vector3.Normalize(normal);

                        min = Vector3.Min(min, position);
                        max = Vector3.Max(max, position);

                        _vertices.AddRange(new[]
                        {
                            position.X, position.Y, position.Z,
                            normal.X, normal.Y, normal.Z,
                            v.U, v.V
                        });
                    }

                int count = _vertices.Count / 8 - start;
                if (count > 0) _batches.Add((start, count, sub.TextureIndex, PartColours[p % PartColours.Length]));
            }
        }

        if (min.X <= max.X)
        {
            _centre = (min + max) * 0.5f;
            _radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
        }

        // After the bounds are settled, so turning the markers on does not move the camera, and sized
        // against the model so they stay readable whether it is a rat or a door.
        if (ShowJoints && bank is not null) AddJointMarkers(bank, transforms);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        var data = _vertices.ToArray();
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);

        const uint stride = 8 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    /// <summary>A small cube at each joint's posed position.</summary>
    private void AddJointMarkers(PoseBank bank, Matrix4x4[] transforms)
    {
        var root = new Vector3(0.95f, 0.85f, 0.25f);
        var limb = new Vector3(0.20f, 0.85f, 1.00f);

        float size = _radius * 0.022f;

        // The root goes last.
        var order = Enumerable.Range(0, Math.Min(bank.PartCount, transforms.Length))
                              .OrderBy(j => j == bank.RootIndex ? 1 : 0);

        foreach (int joint in order)
        {
            // The joint itself sits at its transform's origin; the geometry hanging off it is drawn
            // separately, so this is the pivot rather than the limb.
            var centre = Vector3.Transform(Vector3.Zero, transforms[joint]) * 0.001f;

            int start = _vertices.Count / 8;
            AddCube(centre, size);
            int count = _vertices.Count / 8 - start;

            if (count > 0)
                _jointBatches.Add((start, count, joint == bank.RootIndex ? root : limb));
        }
    }

    /// <summary>Appends an axis-aligned cube as twelve triangles, with a flat normal per face.</summary>
    private void AddCube(Vector3 centre, float size)
    {
        // Face centres and the two edge directions that span each, in the order right, left, up,
        // down, forward, back.
        var faces = new (Vector3 Normal, Vector3 U, Vector3 V)[]
        {
            (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
            (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
            (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
            (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
        };

        foreach (var (normal, u, v) in faces)
        {
            var origin = centre + normal * size;

            var a = origin - u * size - v * size;
            var b = origin + u * size - v * size;
            var c = origin + u * size + v * size;
            var d = origin - u * size + v * size;

            foreach (var corner in new[] { a, b, c, a, c, d })
                _vertices.AddRange(new[]
                {
                    corner.X, corner.Y, corner.Z,
                    normal.X, normal.Y, normal.Z,
                    0f, 0f
                });
        }
    }

    /// <summary>World matrix per part, walking the rig so each limb inherits its parents.</summary>
    private static Matrix4x4[] BuildTransforms(PoseBank? bank, Pose? pose, int partCount, bool[]? stub = null)
    {
        var result = new Matrix4x4[Math.Max(partCount, bank?.PartCount ?? 0)];
        for (int i = 0; i < result.Length; i++) result[i] = Matrix4x4.Identity;
        if (bank is null) return result;

        void Walk(int index, Matrix4x4 parent)
        {
            if (index < 0 || index >= bank.PartCount) return;

            var joint = bank.Joints[index];
            var local = Matrix4x4.CreateTranslation(joint.X, joint.Y, joint.Z);

            if (pose is not null && !(stub is not null && index < stub.Length && stub[index]))
            {
                var (ax, ay, az) = pose.Angles[index];
                var rotation = Matrix4x4.CreateRotationX(PoseBank.AngleToRadians(ax))
                             * Matrix4x4.CreateRotationY(PoseBank.AngleToRadians(ay))
                             * Matrix4x4.CreateRotationZ(PoseBank.AngleToRadians(az));
                local = rotation * local;
            }

            var world = local * parent;
            if (index < result.Length) result[index] = world;

            foreach (int child in joint.Children) Walk(child, world);
        }

        Walk(bank.RootIndex, Matrix4x4.Identity);
        return result;
    }

    /// <summary>The game's axes to the viewer's, matching <c>GltfExporter.ToGltfAxes</c>.</summary>
    private static readonly Matrix4x4 GameToView = new(
        0, 0, 1, 0,
        0, -1, 0, 0,
        1, 0, 0, 0,
        0, 0, 0, 1);

    public unsafe void Render(Func<int, GlTexture?>? textureFor)
    {
        Span<int> previousViewport = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, previousViewport);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.ClearColor(0.07f, 0.07f, 0.09f, 1f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);

        if (_batches.Count > 0)
        {
            _gl.UseProgram(_program);
            _gl.BindVertexArray(_vao);

            // Recentre on the model: a character's root sits far from the origin, so drawing it
            // straight would put most of it off-camera.
            var model = Matrix4x4.CreateTranslation(-_centre)
                      * GameToView
                      * Matrix4x4.CreateRotationY(Yaw)
                      * Matrix4x4.CreateRotationX(Pitch);
            var eye = new Vector3(0, 0, Distance * _radius);
            var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, (float)_width / _height,
                MathF.Max(0.01f, _radius * 0.01f), _radius * 50f);
            var mvp = model * view * projection;

            int mvpLocation = _gl.GetUniformLocation(_program, "uMvp");
            _gl.UniformMatrix4(mvpLocation, 1, false, (float*)&mvp);

            foreach (var batch in _batches)
            {
                var texture = ShowTextures && !ColourByPart ? textureFor?.Invoke(batch.TextureIndex) : null;

                _gl.Uniform1(_gl.GetUniformLocation(_program, "uHighlight"),
                             HighlightTextures.Contains(batch.TextureIndex) ? 1f : 0f);
                _gl.Uniform1(_gl.GetUniformLocation(_program, "uUseTexture"), texture is null ? 0 : 1);
                var tint = ColourByPart ? batch.Tint : new Vector3(0.82f, 0.79f, 0.75f);
                _gl.Uniform3(_gl.GetUniformLocation(_program, "uTint"), tint.X, tint.Y, tint.Z);

                if (texture is not null)
                {
                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, texture.Handle);
                    _gl.Uniform1(_gl.GetUniformLocation(_program, "uTexture"), 0);
                }

                _gl.DrawArrays(PrimitiveType.Triangles, batch.Start, (uint)batch.Count);
            }

            // Joint markers last, with depth testing off so they show through the limbs they sit
            // inside -- which is where most of them are.
            if (_jointBatches.Count > 0)
            {
                _gl.Disable(EnableCap.DepthTest);
                _gl.Uniform1(_gl.GetUniformLocation(_program, "uHighlight"), 0f);
                _gl.Uniform1(_gl.GetUniformLocation(_program, "uUseTexture"), 0);

                foreach (var marker in _jointBatches)
                {
                    _gl.Uniform3(_gl.GetUniformLocation(_program, "uTint"),
                                 marker.Tint.X, marker.Tint.Y, marker.Tint.Z);
                    _gl.DrawArrays(PrimitiveType.Triangles, marker.Start, (uint)marker.Count);
                }

                _gl.Enable(EnableCap.DepthTest);
            }

            _gl.BindVertexArray(0);
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Viewport(previousViewport[0], previousViewport[1],
                     (uint)previousViewport[2], (uint)previousViewport[3]);
    }

    public void Dispose()
    {
        _gl.DeleteProgram(_program);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _gl.DeleteTexture(_colour); _gl.DeleteRenderbuffer(_depth); }
    }
}
