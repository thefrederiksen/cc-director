using System.Text.Json.Serialization;

namespace CcDirector.ContentWriter.Models;

public class ContentDocument
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "in_progress";

    [JsonPropertyName("created")]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("modified")]
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("selected")]
    public List<int> Selected { get; set; } = new();

    [JsonPropertyName("sections")]
    public List<ContentSection> Sections { get; set; } = new();
}

public class ContentSection
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("heading")]
    public string Heading { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";
}
