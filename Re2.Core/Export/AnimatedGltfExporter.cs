using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Re2.Core.Formats;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Re2.Core.Export;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>;

/// <summary>
/// Exports a character as an animated glTF: a node per part arranged in the game's hierarchy, each
/// carrying its rigid limb mesh, with one glTF animation per clip driving the node rotations.
/// </summary>
public static class AnimatedGltfExporter
{
    /// <summary>Frames per second the clips are authored at.</summary>
    public const float FrameRate = 30f;

    public static void Save(
        MeshFile mesh,
        PoseBank bank,
        AnimationSet? animations,
        IReadOnlyList<ExportTexture>? textures,
        string path,
        string name = "character")
    {
        var scene = new SceneBuilder();
        var materials = GltfExporter.BuildMaterials(textures);
        var fallback = new MaterialBuilder(GltfExporter.UntexturedMaterialName).WithDoubleSide(true);

        // One node per mesh part, so every part can be a joint the skin binds to.
        var nodes = new NodeBuilder[Math.Max(mesh.Parts.Count, bank.PartCount)];
        BuildNodes(bank, nodes, bank.RootIndex, null);

        // Skinned vertices live in the pose the skin was bound in, not in each part's own space, so the
        // joint's rest position has to be added back on.
        var rest = bank.RestWorldPositions();

        // Every joint a skin binds to has to sit in one tree, so the extras hang off the root rather
        // than standing alone.
        var root = nodes[bank.RootIndex];
        var rootWorld = new Vector3(rest[bank.RootIndex].X, rest[bank.RootIndex].Y, rest[bank.RootIndex].Z);

        for (int part = 0; part < nodes.Length; part++)
            nodes[part] ??= root.CreateNode($"joint{part:D2}")
                                .WithLocalTranslation(GltfExporter.ToGltfAxes(-rootWorld * GltfExporter.Scale));

        var builder = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>(name);

        for (int part = 0; part < mesh.Parts.Count; part++)
        {
            var source = mesh.Parts[part];
            if (source.SubMeshes.Count == 0) continue;

            var origin = part < rest.Length
                ? new Vector3(rest[part].X, rest[part].Y, rest[part].Z)
                : Vector3.Zero;

            // Every vertex of a part belongs entirely to that part's joint.
            var binding = new VertexJoints4((part, 1f));

            foreach (var sub in source.SubMeshes)
            {
                int index = sub.TextureIndex;
                var material = index >= 0 && index < materials.Count ? materials[index] : fallback;
                var primitive = builder.UsePrimitive(material);

                var verts = new List<VERTEX>(sub.Vertices.Count);
                foreach (var v in sub.Vertices)
                {
                    var (nx, ny, nz) = v.AsNormal();
                    var normal = new Vector3(nx, ny, nz);
                    if (normal.LengthSquared() < 1e-6f) normal = Vector3.UnitY;

                    verts.Add(new VERTEX(
                        new VertexPositionNormal(
                            GltfExporter.ToGltfAxes((new Vector3(v.X, v.Y, v.Z) + origin) * GltfExporter.Scale),
                            GltfExporter.ToGltfAxes(Vector3.Normalize(normal))),
                        new VertexColor1Texture1(Vector4.One, new Vector2(v.U, v.V)),
                        binding));
                }

                foreach (var t in sub.Triangles)
                    primitive.AddTriangle(verts[t.A], verts[t.B], verts[t.C]);
            }
        }

        // Binding to every joint gives the file a real armature rather than a tree of parented
        // objects, which is what a modelling tool needs to pose it.
        scene.AddSkinnedMesh(builder, Matrix4x4.Identity, nodes);

        if (animations is not null) AddAnimations(bank, animations, nodes);

        scene.ToGltf2().SaveGLB(path);
    }

    private static void BuildNodes(PoseBank bank, NodeBuilder[] nodes, int index, NodeBuilder? parent)
    {
        if (index < 0 || index >= bank.PartCount || index >= nodes.Length || nodes[index] is not null) return;

        var joint = bank.Joints[index];
        var node = parent is null ? new NodeBuilder($"joint{index:D2}") : parent.CreateNode($"joint{index:D2}");
        node.WithLocalTranslation(
            GltfExporter.ToGltfAxes(new Vector3(joint.X, joint.Y, joint.Z) * GltfExporter.Scale));
        nodes[index] = node;

        foreach (int child in joint.Children) BuildNodes(bank, nodes, child, node);
    }

    /// <summary>There is deliberately no "rest" clip.</summary>
    private static void AddAnimations(PoseBank bank, AnimationSet animations, NodeBuilder[] nodes)
    {
        foreach (var clip in animations.Clips)
        {
            if (clip.FrameCount == 0) continue;
            string track = $"clip{clip.Index:D2}";

            for (int part = 0; part < bank.PartCount && part < nodes.Length; part++)
            {
                var node = nodes[part];
                if (node is null) continue;

                var curve = node.UseRotation(track);
                for (int frame = 0; frame < clip.FrameCount; frame++)
                {
                    int poseIndex = clip.PoseIndices[frame];
                    if (poseIndex < 0 || poseIndex >= bank.PoseCount) continue;

                    var pose = bank.GetPose(poseIndex);
                    var (ax, ay, az) = pose.Angles[part];

                    curve.WithPoint(frame / FrameRate,
                                    GltfExporter.ToGltfAxes(EulerToQuaternion(ax, ay, az)));
                }
            }
        }
    }

    /// <summary>Raw 12-bit Euler angles to a quaternion, applied X then Y then Z.</summary>
    public static Quaternion EulerToQuaternion(int rawX, int rawY, int rawZ)
    {
        float x = PoseBank.AngleToRadians(rawX);
        float y = PoseBank.AngleToRadians(rawY);
        float z = PoseBank.AngleToRadians(rawZ);

        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z)
             * Quaternion.CreateFromAxisAngle(Vector3.UnitY, y)
             * Quaternion.CreateFromAxisAngle(Vector3.UnitX, x);
    }

    /// <summary>
    /// The exact inverse of <see cref="EulerToQuaternion"/>, giving raw 12-bit angles again.
    /// </summary>
    public static (int X, int Y, int Z) QuaternionToEuler(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var m = Matrix4x4.CreateFromQuaternion(q);

        // System.Numerics matrices are row-major with M{row}{column}, and transform row vectors, so
        // the entry the textbook calls R[2][0] is M13 here.
        float sy = -m.M13;
        sy = Math.Clamp(sy, -1f, 1f);

        float x, y, z;
        if (MathF.Abs(sy) > 0.99999f)
        {
            // Gimbal lock: fold the whole rotation into Z.
            y = MathF.Asin(sy);
            x = 0f;
            z = MathF.Atan2(-m.M21, m.M22);
        }
        else
        {
            y = MathF.Asin(sy);
            x = MathF.Atan2(m.M23, m.M33);
            z = MathF.Atan2(m.M12, m.M11);
        }

        return (PoseBankWriter.FromRadians(x), PoseBankWriter.FromRadians(y), PoseBankWriter.FromRadians(z));
    }
}
