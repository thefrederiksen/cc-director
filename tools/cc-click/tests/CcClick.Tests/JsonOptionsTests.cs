using System.Text.Json;
using Xunit;

namespace CcClick.Tests;

public class JsonOptionsTests
{
    [Fact]
    public void Default_UsesCamelCaseNamingPolicy()
    {
        Assert.Equal(JsonNamingPolicy.CamelCase, JsonOptions.Default.PropertyNamingPolicy);
    }

    [Fact]
    public void Default_UsesIndentedFormatting()
    {
        Assert.True(JsonOptions.Default.WriteIndented);
    }

    [Fact]
    public void Serialize_SimpleObject_UsesCamelCasePropertyNames()
    {
        var obj = new { FirstName = "Alice", LastName = "Smith", Age = 30 };
        string json = JsonSerializer.Serialize(obj, JsonOptions.Default);

        Assert.Contains("\"firstName\"", json);
        Assert.Contains("\"lastName\"", json);
        Assert.Contains("\"age\"", json);
        Assert.DoesNotContain("\"FirstName\"", json);
        Assert.DoesNotContain("\"LastName\"", json);
        Assert.DoesNotContain("\"Age\"", json);
    }

    [Fact]
    public void Serialize_SimpleObject_ProducesIndentedOutput()
    {
        var obj = new { Name = "Test" };
        string json = JsonSerializer.Serialize(obj, JsonOptions.Default);

        // Indented output contains newlines
        Assert.Contains("\n", json);
    }

    [Fact]
    public void Deserialize_CamelCaseJson_WorksWithDefaultOptions()
    {
        string json = "{\"name\":\"Bob\",\"value\":42}";
        var result = JsonSerializer.Deserialize<TestDto>(json, JsonOptions.Default);

        Assert.NotNull(result);
        Assert.Equal("Bob", result!.Name);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Deserialize_PascalCaseJson_WorksWithDefaultOptions()
    {
        // CamelCase policy applies to serialization naming;
        // deserialization should still handle PascalCase property matching
        string json = "{\"Name\":\"Carol\",\"Value\":99}";
        var result = JsonSerializer.Deserialize<TestDto>(json, JsonOptions.Default);

        Assert.NotNull(result);
        Assert.Equal("Carol", result!.Name);
        Assert.Equal(99, result.Value);
    }

    [Fact]
    public void Roundtrip_SerializeAndDeserialize_PreservesValues()
    {
        var original = new TestDto { Name = "RoundTrip", Value = 123 };
        string json = JsonSerializer.Serialize(original, JsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<TestDto>(json, JsonOptions.Default);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Name, deserialized!.Name);
        Assert.Equal(original.Value, deserialized.Value);
    }

    private class TestDto
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }
}
