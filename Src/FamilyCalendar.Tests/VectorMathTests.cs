using FamilyCalendar.Services.Assistant;

namespace FamilyCalendar.Tests;

public class VectorMathTests
{
    [Fact]
    public void ToBytes_ToFloats_RoundTrips_AndUsesFourBytesPerValue()
    {
        var v = new[] { 0.5f, -1.25f, 3.14159f, 0f };

        var bytes = TaskVectorStore.ToBytes(v);

        Assert.Equal(16, bytes.Length);
        Assert.Equal(v, TaskVectorStore.ToFloats(bytes));
        // little-endian: 1.0f = 0x3F800000
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, TaskVectorStore.ToBytes(new[] { 1f }));
    }

    [Fact]
    public void ToFloats_Throws_WhenLengthIsNotMultipleOfFour()
        => Assert.Throws<ArgumentException>(() => TaskVectorStore.ToFloats(new byte[5]));

    [Fact]
    public void CosineSimilarity_KnownValues()
    {
        Assert.Equal(1.0, TaskVectorStore.CosineSimilarity(new[] { 1f, 2f }, new[] { 2f, 4f }), 6);
        Assert.Equal(0.0, TaskVectorStore.CosineSimilarity(new[] { 1f, 0f }, new[] { 0f, 1f }), 6);
        Assert.Equal(-1.0, TaskVectorStore.CosineSimilarity(new[] { 1f, 1f }, new[] { -1f, -1f }), 6);
        Assert.Equal(0.0, TaskVectorStore.CosineSimilarity(new[] { 1f, 1f }, new[] { 1f }));      // lunghezze diverse
        Assert.Equal(0.0, TaskVectorStore.CosineSimilarity(new[] { 0f, 0f }, new[] { 1f, 1f }));   // vettore nullo
    }

    [Fact]
    public void Normalize_ProducesUnitVector_AndRejectsZero()
    {
        var n = TaskVectorStore.Normalize(new[] { 3f, 4f });

        Assert.NotNull(n);
        Assert.Equal(0.6f, n![0], 5);
        Assert.Equal(0.8f, n[1], 5);
        Assert.Null(TaskVectorStore.Normalize(new[] { 0f, 0f }));
    }

    [Fact]
    public void Dot_OnBlob_MatchesCosineOfNormalizedVectors()
    {
        var a = TaskVectorStore.Normalize(new[] { 1f, 2f, 3f })!;
        var b = TaskVectorStore.Normalize(new[] { 3f, 2f, 1f })!;

        var dot = TaskVectorStore.Dot(a, TaskVectorStore.ToBytes(b));

        Assert.Equal(TaskVectorStore.CosineSimilarity(a, b), dot, 5);
    }
}
