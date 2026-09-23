using System;
using System.Collections.Generic;
using System.Numerics;
using Re2.Core.Formats;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;

namespace Re2.Core.Export;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>A decoded texture ready to embed in a glTF material.</summary>
public sealed record ExportTexture(byte[] Png, int Width, int Height);

/// <summary>Writes decoded meshes out as glTF, with textures when they are supplied.</summary>
public static class GltfExporter
{
    /// <summary>
    /// The game works in roughly one unit per millimetre, so scale down to keep exported models a
    /// sensible size in DCC tools.
    /// </summary>
    public const float Scale = 0.001f;

    /// <summary>Material name given to geometry the model marks as untextured.</summary>
    public const string UntexturedMaterialName = "untextured";

    /// <summary>Converts a position or normal from the game's axes to glTF's.</summary>
    public static Vector3 ToGltfAxes(Vector3 v) => new(v.Z, -v.Y, v.X);

    /// <summary>The same axis change applied to a rotation.</summary>
    public static Quaternion ToGltfAxes(Quaternion q) => new(q.Z, -q.Y, q.X, q.W);


    public static void Save(MeshFile mesh, string path, string name = "mesh")
        => Save(mesh, null, path, name, null);

    /// <summary>Exports a mesh.</summary>
    public static void Save(MeshFile mesh, IReadOnlyList<ExportTexture>? textures, string path,
        string name = "mesh", IReadOnlyList<(int X, int Y, int Z)>? partOffsets = null)
    {
        var scene = new SceneBuilder();
        var materials = BuildMaterials(textures);
        var fallback = new MaterialBuilder(UntexturedMaterialName).WithDoubleSide(true);

        foreach (var part in mesh.Parts)
        {
            if (part.SubMeshes.Count == 0) continue;

            var builder = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1>($"{name}_part{part.Index:D2}");

            var offset = Vector3.Zero;
            if (partOffsets is not null && part.Index < partOffsets.Count)
            {
                var (ox, oy, oz) = partOffsets[part.Index];
                offset = new Vector3(ox, oy, oz);
            }

            foreach (var sub in part.SubMeshes)
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
                            ToGltfAxes((new Vector3(v.X, v.Y, v.Z) + offset) * Scale),
                            ToGltfAxes(Vector3.Normalize(normal))),
                        new VertexColor1Texture1(
                            Vector4.One,
                            new Vector2(v.U, v.V))));
                }

                // Corner order is untouched: the axis change is a rotation, so the faces already
                // point the way they did in the game.
                foreach (var t in sub.Triangles)
                    primitive.AddTriangle(verts[t.A], verts[t.B], verts[t.C]);
            }

            scene.AddRigidMesh(builder, Matrix4x4.Identity);
        }

        scene.ToGltf2().SaveGLB(path);
    }

    internal static List<MaterialBuilder> BuildMaterials(IReadOnlyList<ExportTexture>? textures)
    {
        var materials = new List<MaterialBuilder>();
        if (textures is null) return materials;

        for (int i = 0; i < textures.Count; i++)
        {
            var material = new MaterialBuilder($"tex{i:D2}")
                .WithDoubleSide(true)
                .WithMetallicRoughnessShader()
                .WithMetallicRoughness(0f, 1f)
                // The textures use 1-bit alpha, so masking matches the game rather than blending.
                .WithAlpha(AlphaMode.MASK, 0.5f);

            material.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(textures[i].Png));
            materials.Add(material);
        }

        return materials;
    }
}
