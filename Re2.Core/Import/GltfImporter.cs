using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Re2.Core.Export;
using Re2.Core.Formats;
using SharpGLTF.Schema2;

namespace Re2.Core.Import;

/// <summary>What the importer had to say about a conversion, for reporting back to the user.</summary>
public sealed record MeshImportReport(
    int Parts, int SubMeshes, int Vertices, int Triangles,
    int PartsMatchedByName, int ClampedPositions, int UnmatchedMaterials)
{
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Reads a glTF/GLB back into the game's mesh format -- the inverse of <see cref="GltfExporter"/>.
/// </summary>
public static class GltfImporter
{
    /// <summary>Matches the exporter's <c>name_partNN</c> convention on a node or mesh name.</summary>
    private static readonly Regex PartSuffix = new(@"part[_\-]?(\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches the exporter's <c>texNN</c> material naming.</summary>
    private static readonly Regex TextureName = new(@"tex[_\-]?(\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed class Options
    {
        /// <summary>Rest-pose position per part, exactly as passed to the exporter.</summary>
        public IReadOnlyList<(int X, int Y, int Z)>? PartOffsets { get; set; }

        /// <summary>Parts the output must have.</summary>
        public int PartCount { get; set; }

        /// <summary>Textures the model references. Kept from the mesh being replaced.</summary>
        public int TextureCount { get; set; }

        /// <summary>Model units per glTF unit. The default undoes the exporter's scaling.</summary>
        public float Scale { get; set; } = GltfExporter.Scale;
    }

    public static (MeshBuildModel Model, MeshImportReport Report) Load(string path, Options options)
    {
        var root = ModelRoot.Load(path);
        var scene = root.DefaultScene ?? root.LogicalScenes.FirstOrDefault()
            ?? throw new InvalidDataException("The glTF has no scene.");

        var warnings = new List<string>();
        int clamped = 0, unmatchedMaterials = 0, matchedByName = 0, outOfRangeMaterials = 0;
        int outOfRangeUvs = 0;

        // Gather drawable nodes first so part numbering can be resolved before anything is built.
        var drawn = new List<Node>();
        foreach (var node in scene.VisualChildren) Collect(node, drawn);
        if (drawn.Count == 0) throw new InvalidDataException("The glTF contains no meshes.");

        // A skinned file says which joint each vertex belongs to, which is both more robust than node
        // names and the only thing that survives a modelling tool merging the parts into one object.
        var skinned = drawn.Where(n => n.Skin is not null).ToList();
        if (skinned.Count > 0) return LoadSkinned(skinned, options, warnings);

        var byPart = new Dictionary<int, List<Node>>();
        int nextUnnamed = 0;

        foreach (var node in drawn)
        {
            int index;
            var match = PartSuffix.Match(node.Name ?? "");
            if (!match.Success) match = PartSuffix.Match(node.Mesh?.Name ?? "");

            if (match.Success)
            {
                index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                matchedByName++;
            }
            else
            {
                index = nextUnnamed;
                warnings.Add($"'{node.Name ?? node.Mesh?.Name ?? "(unnamed)"}' has no partNN suffix; " +
                             $"it was taken as part {index} by position.");
            }

            nextUnnamed = Math.Max(nextUnnamed, index + 1);
            if (!byPart.TryGetValue(index, out var list)) byPart[index] = list = new List<Node>();
            list.Add(node);
        }

        int partCount = Math.Max(options.PartCount, byPart.Keys.Count == 0 ? 0 : byPart.Keys.Max() + 1);
        var model = new MeshBuildModel { TextureCount = options.TextureCount };
        for (int i = 0; i < partCount; i++) model.Parts.Add(new MeshBuildPart());

        foreach (var (partIndex, nodes) in byPart.OrderBy(kv => kv.Key))
        {
            var offset = Vector3.Zero;
            if (options.PartOffsets is not null && partIndex < options.PartOffsets.Count)
            {
                var (ox, oy, oz) = options.PartOffsets[partIndex];
                offset = new Vector3(ox, oy, oz);
            }

            foreach (var node in nodes)
            foreach (var primitive in node.Mesh.Primitives)
            {
                if (primitive.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                {
                    warnings.Add($"part {partIndex}: skipped a {primitive.DrawPrimitiveType} primitive; " +
                                 "only triangles can be encoded.");
                    continue;
                }

                int textureIndex = ResolveTexture(primitive.Material?.Name, options.TextureCount,
                                                  out bool matched, out var problem);

                // Geometry the model marks as untextured is a normal state, not a lost binding, so
                // the exporter's own name for it must not be reported as a problem.
                bool deliberatelyUntextured = string.Equals(primitive.Material?.Name,
                    GltfExporter.UntexturedMaterialName, StringComparison.OrdinalIgnoreCase);

                if (!matched && !deliberatelyUntextured && primitive.Material is not null)
                {
                    if (problem == SlotProblem.OutOfRange) outOfRangeMaterials++;
                    else unmatchedMaterials++;
                }

                var pool = ReadVertices(primitive, node.WorldMatrix, offset, options.Scale,
                                        ref clamped, ref outOfRangeUvs);
                // Degenerate triangles draw nothing but still consume a vertex slot each, and DCC
                // exports produce them routinely; drop them here rather than in the encoder.
                var triangles = primitive.GetTriangleIndices()
                    .Where(t => t.A != t.B && t.B != t.C && t.A != t.C)
                    .Select(t => new MeshTriangle(t.A, t.B, t.C))
                    .ToList();

                if (pool.Count == 0 || triangles.Count == 0) continue;

                // One sub-mesh per primitive, whatever its size: material state is per sub-mesh, and
                // MeshWriter chunks the geometry across G_VTX loads on its own.
                var built = new MeshBuildSubMesh { TextureIndex = textureIndex };
                built.Vertices.AddRange(pool);
                built.Triangles.AddRange(triangles);
                model.Parts[partIndex].SubMeshes.Add(built);
            }
        }

        if (clamped > 0)
            warnings.Add($"{clamped:N0} vertex coordinates were outside the signed 16-bit range and " +
                         "were clamped. The model is probably too large -- scale it down and re-export.");

        if (unmatchedMaterials > 0)
            warnings.Add($"{unmatchedMaterials} material(s) had no texNN name, so their geometry is " +
                         "untextured. Name a material tex07 to bind texture 7.");

        if (outOfRangeMaterials > 0)
            warnings.Add(OutOfRangeMessage(outOfRangeMaterials, options.TextureCount));

        if (outOfRangeUvs > 0) warnings.Add(UvRangeMessage(outOfRangeUvs));

        var report = new MeshImportReport(
            Parts: model.Parts.Count(p => p.SubMeshes.Count > 0),
            SubMeshes: model.Parts.Sum(p => p.SubMeshes.Count),
            Vertices: model.TotalVertices,
            Triangles: model.TotalTriangles,
            PartsMatchedByName: matchedByName,
            ClampedPositions: clamped,
            UnmatchedMaterials: unmatchedMaterials)
        { Warnings = warnings };

        return (model, report);
    }

    /// <summary>
    /// Reads a skinned model, taking each vertex's part from the joint it is weighted to.
    /// </summary>
    private static (MeshBuildModel Model, MeshImportReport Report) LoadSkinned(
        List<Node> meshNodes, Options options, List<string> warnings)
    {
        int clamped = 0, unmatchedMaterials = 0, blended = 0, mixedTriangles = 0, outOfRangeMaterials = 0;
        int outOfRangeUvs = 0, beyondRig = 0;

        // The model's part count is the ROM's, exactly as its texture slot count is: the rig driving
        // it has a fixed number of joints, so a file carrying an extra bone -- an armature object
        // exported alongside the real ones, say -- must not add a part to the model.
        int partCount = options.PartCount;
        if (partCount <= 0)
            foreach (var node in meshNodes)
                for (int j = 0; j < node.Skin!.JointsCount; j++)
                    partCount = Math.Max(partCount, JointPart(node.Skin.GetJoint(j).Joint, j) + 1);

        var model = new MeshBuildModel { TextureCount = options.TextureCount };
        for (int i = 0; i < partCount; i++) model.Parts.Add(new MeshBuildPart());

        foreach (var node in meshNodes)
        {
            var skin = node.Skin!;

            // Slot order is whatever the exporting tool chose, so the part each slot stands for is
            // read from the joint's name rather than assumed to be its position.
            var slotToPart = new int[skin.JointsCount];
            var slotToLocal = new Matrix4x4[skin.JointsCount];

            for (int j = 0; j < skin.JointsCount; j++)
            {
                var joint = skin.GetJoint(j);
                slotToPart[j] = JointPart(joint.Joint, j);

                // The inverse bind matrix is what turns a skinned vertex back into the space of the
                // joint it belongs to -- exactly the per-part space the game stores.
                slotToLocal[j] = joint.InverseBindMatrix;
            }

            foreach (var primitive in node.Mesh.Primitives)
            {
                if (primitive.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                {
                    warnings.Add($"skipped a {primitive.DrawPrimitiveType} primitive; only triangles " +
                                 "can be encoded.");
                    continue;
                }

                int textureIndex = ResolveTexture(primitive.Material?.Name, options.TextureCount,
                                                  out bool matched, out var problem);
                bool deliberatelyUntextured = string.Equals(primitive.Material?.Name,
                    GltfExporter.UntexturedMaterialName, StringComparison.OrdinalIgnoreCase);

                if (!matched && !deliberatelyUntextured && primitive.Material is not null)
                {
                    if (problem == SlotProblem.OutOfRange) outOfRangeMaterials++;
                    else unmatchedMaterials++;
                }

                var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (positions is null) continue;

                var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var joints = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

                int uvsOutOfRange = 0;

                // Which part each vertex ends up in, and through which joint slot.
                var vertexPart = new int[positions.Count];
                var slotOf = new int[positions.Count];
                for (int i = 0; i < positions.Count; i++)
                {
                    int slot = 0;
                    if (joints is not null && weights is not null && i < joints.Count && i < weights.Count)
                    {
                        var j = joints[i];
                        var w = weights[i];

                        float best = w.X;
                        slot = (int)j.X;
                        if (w.Y > best) { best = w.Y; slot = (int)j.Y; }
                        if (w.Z > best) { best = w.Z; slot = (int)j.Z; }
                        if (w.W > best) { best = w.W; slot = (int)j.W; }

                        // Anything genuinely shared between joints cannot be represented.
                        float total = w.X + w.Y + w.Z + w.W;
                        if (total > 0 && best < total * 0.999f) blended++;
                    }

                    slotOf[i] = slot >= 0 && slot < slotToPart.Length ? slot : 0;
                    vertexPart[i] = slotToPart[slotOf[i]];
                }

                // Split the primitive into one sub-mesh per part, remapping indices as we go.
                var pools = new Dictionary<int, List<MeshVertex>>();
                var lists = new Dictionary<int, List<MeshTriangle>>();
                var remap = new Dictionary<(int Part, int Vertex), int>();

                int Emit(int part, int vertex)
                {
                    if (remap.TryGetValue((part, vertex), out int existing)) return existing;

                    if (!pools.TryGetValue(part, out var pool)) pools[part] = pool = new List<MeshVertex>();

                    // Into the joint's own space first, while still in glTF axes, then across to the
                    // game's axes and units.
                    var bind = slotToLocal[slotOf[vertex]];
                    var local = Vector3.Transform(positions[vertex], bind);
                    var position = GltfExporter.ToGltfAxes(local) / options.Scale;

                    var rotation = bind;
                    rotation.Translation = Vector3.Zero;

                    var normal = normals is not null && vertex < normals.Count
                        ? GltfExporter.ToGltfAxes(Vector3.TransformNormal(normals[vertex], rotation))
                        : Vector3.UnitY;
                    normal = normal.LengthSquared() > 1e-9f ? Vector3.Normalize(normal) : Vector3.UnitY;

                    var uv = uvs is not null && vertex < uvs.Count ? uvs[vertex] : Vector2.Zero;

                    pool.Add(new MeshVertex(
                        ClampToShort(position.X, ref clamped),
                        ClampToShort(position.Y, ref clamped),
                        ClampToShort(position.Z, ref clamped),
                        EncodeUv(uv.X, ref uvsOutOfRange), EncodeUv(uv.Y, ref uvsOutOfRange),
                        EncodeNormal(normal.X), EncodeNormal(normal.Y), EncodeNormal(normal.Z),
                        0xFF));

                    int index = pool.Count - 1;
                    remap[(part, vertex)] = index;
                    return index;
                }

                foreach (var t in primitive.GetTriangleIndices())
                {
                    if (t.A == t.B || t.B == t.C || t.A == t.C) continue;       // degenerate

                    int part = vertexPart[t.A];
                    if (vertexPart[t.B] != part || vertexPart[t.C] != part) mixedTriangles++;

                    if (!lists.TryGetValue(part, out var list)) lists[part] = list = new List<MeshTriangle>();
                    list.Add(new MeshTriangle(Emit(part, t.A), Emit(part, t.B), Emit(part, t.C)));
                }

                outOfRangeUvs += uvsOutOfRange;

                foreach (var (part, triangles) in lists)
                {
                    if (triangles.Count == 0) continue;

                    // Geometry on a bone the model does not have cannot be kept, but it must be
                    // reported rather than quietly dropped.
                    if (part < 0 || part >= model.Parts.Count)
                    {
                        beyondRig += triangles.Count;
                        continue;
                    }


                    var built = new MeshBuildSubMesh { TextureIndex = textureIndex };
                    built.Vertices.AddRange(pools[part]);
                    built.Triangles.AddRange(triangles);
                    model.Parts[part].SubMeshes.Add(built);
                }
            }
        }

        if (clamped > 0)
            warnings.Add($"{clamped:N0} vertex coordinates were outside the signed 16-bit range and " +
                         "were clamped. The model is probably too large -- scale it down and re-export.");

        if (unmatchedMaterials > 0)
            warnings.Add($"{unmatchedMaterials} material(s) had no texNN name, so their geometry is " +
                         "untextured. Name a material tex07 to bind texture 7.");

        if (outOfRangeMaterials > 0)
            warnings.Add(OutOfRangeMessage(outOfRangeMaterials, options.TextureCount));

        if (outOfRangeUvs > 0) warnings.Add(UvRangeMessage(outOfRangeUvs));

        if (beyondRig > 0)
            warnings.Add($"{beyondRig:N0} triangles were weighted to a bone beyond the {partCount} this " +
                         "model has, and could not be kept. Weight them to one of its own bones.");

        if (blended > 0)
            warnings.Add($"{blended:N0} vertices were weighted to more than one bone. The game moves " +
                         "each part rigidly, so the heaviest bone was used for each.");

        if (mixedTriangles > 0)
            warnings.Add($"{mixedTriangles:N0} triangles spanned two bones and were assigned to the " +
                         "first corner's bone. Weight a whole part to one bone to avoid this.");

        // Said here as well as in the writer, because at this point the part numbers can still be
        // matched up against the bones the user can see.
        var starved = model.Parts
            .Select((part, index) => (Index: index, part.SubMeshes.Count))
            .Where(x => x.Count == 0)
            .Select(x => x.Index)
            .ToList();

        if (starved.Count > 0)
            warnings.Add($"part(s) {string.Join(", ", starved)} ended up with no geometry. Every part " +
                         "needs some, or the game crashes as the model appears -- check that your bones " +
                         "are numbered the way the exported model's were.");

        var report = new MeshImportReport(
            Parts: model.Parts.Count(p => p.SubMeshes.Count > 0),
            SubMeshes: model.Parts.Sum(p => p.SubMeshes.Count),
            Vertices: model.TotalVertices,
            Triangles: model.TotalTriangles,
            PartsMatchedByName: model.Parts.Count(p => p.SubMeshes.Count > 0),
            ClampedPositions: clamped,
            UnmatchedMaterials: unmatchedMaterials)
        { Warnings = warnings };

        return (model, report);
    }

    /// <summary>Two separate limits, which is why this says both.</summary>
    private static string UvRangeMessage(int count)
        => $"{count:N0} UV coordinate(s) were outside the -2.0 to +2.0 the format can store and were " +
           "clamped, which distorts those faces. Keep UVs within 0 to 1: the game does not tile " +
           "textures, it extends the edge pixels, so anything outside that range smears in game.";

    /// <summary>
    /// Says plainly that a model cannot gain texture slots, because that is the one thing a user
    /// cannot fix by renaming and the tool cannot fix for them.
    /// </summary>
    private static string OutOfRangeMessage(int count, int textureCount)
        => $"{count} material(s) name a texture slot this model does not have; it has {textureCount} " +
           $"(tex00 to tex{Math.Max(0, textureCount - 1):D2}). Their geometry was imported untextured. " +
           "A model cannot be given more texture slots than the game already allots it -- repaint one " +
           "of the existing textures instead.";

    /// <summary>The part a joint node stands for, from its name, falling back to its slot.</summary>
    private static int JointPart(Node joint, int slot)
    {
        var match = JointNodeName.Match(joint.Name ?? "");
        return match.Success && int.TryParse(match.Groups[1].Value, out int index) ? index : slot;
    }

    private static void Collect(Node node, List<Node> into)
    {
        if (node.Mesh is not null && node.Mesh.Primitives.Count > 0) into.Add(node);
        foreach (var child in node.VisualChildren) Collect(child, into);
    }

    /// <summary>Reads one primitive's vertices back into model space.</summary>
    private static List<MeshVertex> ReadVertices(MeshPrimitive primitive, Matrix4x4 world,
                                                 Vector3 partOffset, float scale, ref int clamped,
                                                 ref int outOfRangeUvs)
    {
        var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
        if (positions is null) return new List<MeshVertex>();

        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();

        var normalRotation = world;
        normalRotation.Translation = Vector3.Zero;

        var vertices = new List<MeshVertex>(positions.Count);

        for (int i = 0; i < positions.Count; i++)
        {
            // Back to the game's axes (+Y down) before anything measured in them is applied.
            var world_ = GltfExporter.ToGltfAxes(Vector3.Transform(positions[i], world));
            var model = world_ / scale - partOffset;

            short x = ClampToShort(model.X, ref clamped);
            short y = ClampToShort(model.Y, ref clamped);
            short z = ClampToShort(model.Z, ref clamped);

            var normal = normals is not null && i < normals.Count
                ? GltfExporter.ToGltfAxes(Vector3.TransformNormal(normals[i], normalRotation))
                : Vector3.UnitY;
            if (normal.LengthSquared() > 1e-9f) normal = Vector3.Normalize(normal);
            else normal = Vector3.UnitY;

            var uv = uvs is not null && i < uvs.Count ? uvs[i] : Vector2.Zero;

            vertices.Add(new MeshVertex(
                x, y, z,
                EncodeUv(uv.X, ref outOfRangeUvs), EncodeUv(uv.Y, ref outOfRangeUvs),
                EncodeNormal(normal.X), EncodeNormal(normal.Y), EncodeNormal(normal.Z),
                0xFF));
        }

        return vertices;
    }

    private static short ClampToShort(float value, ref int clamped)
    {
        float rounded = MathF.Round(value);
        if (rounded > short.MaxValue) { clamped++; return short.MaxValue; }
        if (rounded < short.MinValue) { clamped++; return short.MinValue; }
        return (short)rounded;
    }

    /// <summary>
    /// The largest UV the format can hold, just under 2.0: the stored value is a signed 16-bit
    /// number and <see cref="MeshVertex.UvScale"/> is 16,384 per unit.
    /// </summary>
    public const float MaxUv = 32767f / MeshVertex.UvScale;

    /// <summary>
    /// UVs are S10.5 over a fixed 0..512 range, so 1.0 is 16,384 rather than the texture width.
    /// </summary>
    private static short EncodeUv(float value, ref int outOfRange)
    {
        if (float.IsNaN(value)) return 0;

        float scaled = value * MeshVertex.UvScale;

        if (scaled >= short.MaxValue) { outOfRange++; return short.MaxValue; }
        if (scaled <= short.MinValue) { outOfRange++; return short.MinValue; }

        return (short)MathF.Round(scaled);
    }

    private static byte EncodeNormal(float component)
        => (byte)(sbyte)Math.Clamp((int)MathF.Round(component * 127f), -127, 127);

    /// <summary>Matches the exporter's <c>jointNN</c> node naming.</summary>
    private static readonly Regex JointNodeName = new(@"joint[_\-]?(\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Each joint's offset from its parent, in the game's units, read from the file's skeleton.
    /// </summary>
    public static Dictionary<int, (int X, int Y, int Z)> LoadRestPose(string path, int partCount)
    {
        var offsets = new Dictionary<int, (int X, int Y, int Z)>();
        var model = ModelRoot.Load(path);

        foreach (var node in model.LogicalNodes)
        {
            var match = JointNodeName.Match(node.Name ?? "");
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, out int index)) continue;
            if (index < 0 || index >= partCount) continue;

            // Back out of glTF's axes and scale, the exact inverse of what the exporter applied.
            var local = GltfExporter.ToGltfAxes(node.LocalTransform.Translation) / GltfExporter.Scale;

            offsets[index] = ((int)MathF.Round(local.X),
                              (int)MathF.Round(local.Y),
                              (int)MathF.Round(local.Z));
        }

        return offsets;
    }

    /// <summary>
    /// The base-colour images in a glTF, keyed by the texture slot their material name gives.
    /// </summary>
    public static Dictionary<int, byte[]> LoadTextureImages(string path, int textureCount)
    {
        var images = new Dictionary<int, byte[]>();
        var model = ModelRoot.Load(path);

        foreach (var material in model.LogicalMaterials)
        {
            int slot = ResolveTexture(material.Name, textureCount, out bool matched);
            if (!matched || slot < 0) continue;

            var content = material.FindChannel("BaseColor")?.Texture?.PrimaryImage?.Content;
            if (content is null) continue;

            var bytes = content.Value.Content.ToArray();
            if (bytes.Length == 0) continue;

            images[slot] = bytes;
        }

        return images;
    }

    /// <summary>Why a material did not bind to a texture slot.</summary>
    private enum SlotProblem { None, Unnamed, OutOfRange }

    /// <summary>The texture slot a material name binds to, or -1 with a reason.</summary>
    private static int ResolveTexture(string? materialName, int textureCount, out bool matched,
                                      out SlotProblem problem)
    {
        matched = false;
        problem = SlotProblem.None;

        if (string.IsNullOrEmpty(materialName)) { problem = SlotProblem.Unnamed; return -1; }

        var match = TextureName.Match(materialName);
        if (!match.Success) { problem = SlotProblem.Unnamed; return -1; }

        int index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (textureCount > 0 && index >= textureCount) { problem = SlotProblem.OutOfRange; return -1; }

        matched = true;
        return index;
    }

    private static int ResolveTexture(string? materialName, int textureCount, out bool matched)
        => ResolveTexture(materialName, textureCount, out matched, out _);
}
