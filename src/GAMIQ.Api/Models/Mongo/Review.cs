using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace GAMIQ.Api.Models.Mongo;

// Player review + whatever telemetry the client sends along (platform, patch, fps, ...)
public class Review
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    [BsonRepresentation(BsonType.ObjectId)]
    public string ProductId { get; set; } = null!;

    // Users.UserId in PostgreSQL
    public int UserId { get; set; }

    public int Rating { get; set; }
    public string Comment { get; set; } = string.Empty;
    public double PlaytimeHours { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
