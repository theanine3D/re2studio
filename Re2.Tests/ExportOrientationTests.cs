using System.IO;
using System.Linq;
using System.Numerics;
using Re2.Core.Assets;
using Re2.Core.Export;
using Re2.Core.Formats;
using SharpGLTF.Schema2;
using Xunit;

namespace Re2.Tests;

/// <summary>Exported models have to be the right way up <b>and the right way round</b>.</summary>
public class ExportOrientationTests
{
    [Fact]
    public void TheAxisMapIsItsOwnInverse()
    {
        var position = new Vector3(11, -22, 33);
        Assert.Equal(position, GltfExporter.ToGltfAxes(GltfExporter.ToGltfAxes(position)));

        var rotation = Quaternion.Normalize(new Quaternion(0.3f, -0.5f, 0.1f, 0.8f));
        var round = GltfExporter.ToGltfAxes(GltfExporter.ToGltfAxes(rotation));
        Assert.Equal(rotation.X, round.X, 1e-5f);
        Assert.Equal(rotation.Y, round.Y, 1e-5f);
        Assert.Equal(rotation.Z, round.Z, 1e-5f);
        Assert.Equal(rotation.W, round.W, 1e-5f);
    }

    [Fact]
    public void TheAxisMapSwapsXAndZAndInvertsY()
    {
        Assert.Equal(new Vector3(3, -2, 1), GltfExporter.ToGltfAxes(new Vector3(1, 2, 3)));
    }

    /// <summary>The heart of it: the conversion must be a <b>rotation</b>, not a reflection.</summary>
    [Fact]
    public void TheAxisMapIsARotationNotAMirror()
    {
        var x = GltfExporter.ToGltfAxes(Vector3.UnitX);
        var y = GltfExporter.ToGltfAxes(Vector3.UnitY);
        var z = GltfExporter.ToGltfAxes(Vector3.UnitZ);

        // Determinant of the three mapped basis vectors: +1 is a rotation, -1 is a mirror.
        float determinant = Vector3.Dot(x, Vector3.Cross(y, z));
        Assert.Equal(1f, determinant, 1e-5f);
    }

    /// <summary>
    /// Handedness has to survive the round trip too, or an imported model would come back mirrored
    /// even though the export looked right.
    /// </summary>
    [Fact]
    public void TheRoundTripPreservesHandedness()
    {
        // A triangle with a known facing, carried out to glTF axes and back.
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(10, 0, 0);
        var c = new Vector3(0, 0, 10);
        var before = Vector3.Cross(b - a, c - a);

        Vector3 Map(Vector3 v) => GltfExporter.ToGltfAxes(GltfExporter.ToGltfAxes(v));
        var after = Vector3.Cross(Map(b) - Map(a), Map(c) - Map(a));

        Assert.True(Vector3.Dot(Vector3.Normalize(before), Vector3.Normalize(after)) > 0.999f,
                    "the face normal flipped, so the round trip mirrored the geometry");
    }

    /// <summary>
    /// End to end on real ROM data: the exported file's vertical extent must come from the game's Y,
    /// inverted, and its X and Z must have traded places -- with the model still the same size.
    /// </summary>
    [RomFact]
    public void AnExportedCharacterIsUprightAndUnmirrored()
    {
        var rom = TestRom.Rom;
        var directory = AssetDirectory.Read(rom);

        var entry = directory.Entries.First(e =>
            directory.TryGetData(rom, e, out var data) && MeshFile.TryParse(data, out _));
        Assert.True(directory.TryGetData(rom, entry, out var meshData));
        var mesh = MeshFile.Parse(meshData);

        var raw = mesh.Parts.SelectMany(p => p.SubMeshes).SelectMany(s => s.Vertices).ToList();
        Assert.NotEmpty(raw);

        float Scaled(float v) => v * GltfExporter.Scale;

        string path = Path.Combine(Path.GetTempPath(), "re2-orient-" + Path.GetRandomFileName()[..6] + ".glb");
        try
        {
            GltfExporter.Save(mesh, path, "orientation");

            var model = ModelRoot.Load(path);
            var points = model.LogicalMeshes
                .SelectMany(m => m.Primitives)
                .SelectMany(p => p.GetVertexAccessor("POSITION").AsVector3Array())
                .ToList();

            Assert.NotEmpty(points);

            // Up in the file is down in the game.
            Assert.Equal(Scaled(-raw.Max(v => (float)v.Y)), points.Min(p => p.Y), 1e-3f);
            Assert.Equal(Scaled(-raw.Min(v => (float)v.Y)), points.Max(p => p.Y), 1e-3f);

            // The game's Z becomes the file's X, and the game's X becomes the file's Z.
            Assert.Equal(Scaled(raw.Min(v => (float)v.Z)), points.Min(p => p.X), 1e-3f);
            Assert.Equal(Scaled(raw.Max(v => (float)v.Z)), points.Max(p => p.X), 1e-3f);
            Assert.Equal(Scaled(raw.Min(v => (float)v.X)), points.Min(p => p.Z), 1e-3f);
            Assert.Equal(Scaled(raw.Max(v => (float)v.X)), points.Max(p => p.Z), 1e-3f);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
