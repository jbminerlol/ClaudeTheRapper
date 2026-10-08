using System.Buffers.Binary;
using System.Numerics;

namespace PaRappaPC;

internal readonly record struct VdfVertex(short X, short Y, short Z)
{
    public Vector3 ToVector3(float scale) => new(X * scale, Y * scale, Z * scale);
}

internal sealed record VdfFrame(uint ObjectId, uint VertexOffsetBytes, VdfVertex[] Vertices)
{
    public int FirstVertex => checked((int)(VertexOffsetBytes / 8));
}

internal sealed class VdfAnimation
{
    private readonly Dictionary<uint, List<VdfFrame>> _objects;

    private VdfAnimation(Dictionary<uint, List<VdfFrame>> objects) => _objects = objects;

    public IEnumerable<uint> ObjectIds => _objects.Keys.Order();
    public IReadOnlyList<VdfFrame> Frames(uint objectId) => _objects[objectId];

    public static VdfAnimation Read(string path) => Parse(File.ReadAllBytes(path));

    public static VdfAnimation Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) throw new InvalidDataException("VDF is shorter than its frame count header.");
        uint recordCount = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (recordCount is < 1 or > 100_000) throw new InvalidDataException($"Implausible VDF record count: {recordCount}.");

        var objects = new Dictionary<uint, List<VdfFrame>>();
        int offset = 4;
        for (uint i = 0; i < recordCount; i++)
        {
            if (data.Length - offset < 12) throw new InvalidDataException($"Truncated VDF frame record {i}.");
            uint objectId = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            uint vertexOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            uint vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]);
            offset += 12;

            if ((vertexOffset & 7) != 0) throw new InvalidDataException($"VDF record {i} has a vertex offset not aligned to its 8-byte vertex stride.");
            if (vertexCount is < 1 or > 100_000) throw new InvalidDataException($"Implausible vertex count in VDF record {i}: {vertexCount}.");
            int byteCount = checked((int)vertexCount * 8);
            if (byteCount > data.Length - offset) throw new InvalidDataException($"Truncated vertex data in VDF record {i}.");

            var vertices = new VdfVertex[checked((int)vertexCount)];
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                int at = offset + vertex * 8;
                vertices[vertex] = new VdfVertex(
                    BinaryPrimitives.ReadInt16LittleEndian(data[at..]),
                    BinaryPrimitives.ReadInt16LittleEndian(data[(at + 2)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(data[(at + 4)..]));
            }
            offset += byteCount;

            if (!objects.TryGetValue(objectId, out var frames)) objects[objectId] = frames = [];
            frames.Add(new VdfFrame(objectId, vertexOffset, vertices));
        }

        return new VdfAnimation(objects);
    }

    public ObjMesh ApplyFrame(ObjMesh baseMesh, uint objectId, int frameIndex)
    {
        if (!_objects.TryGetValue(objectId, out var frames)) throw new ArgumentOutOfRangeException(nameof(objectId));
        if ((uint)frameIndex >= frames.Count) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        var frame = frames[frameIndex];
        int firstVertex = frame.FirstVertex;
        if (firstVertex > baseMesh.Vertices.Count || frame.Vertices.Length > baseMesh.Vertices.Count - firstVertex)
            throw new InvalidDataException($"VDF object {objectId} frame {frameIndex} affects vertices {firstVertex}–{firstVertex + frame.Vertices.Length - 1}, but the OBJ has {baseMesh.Vertices.Count} vertices. Load the matching base mesh.");

        var vertices = baseMesh.Vertices.ToArray();
        for (int i = 0; i < frame.Vertices.Length; i++)
        {
            var delta = frame.Vertices[i];
            vertices[firstVertex + i] += new Vector3(
                delta.X * baseMesh.NormalizationScale,
                delta.Y * baseMesh.NormalizationScale * baseMesh.AnimationYSign,
                delta.Z * baseMesh.NormalizationScale);
        }
        return baseMesh.WithVertices(vertices);
    }
}
