using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace GAMIQ.Api.Models.Mongo;

public class Product
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    public string Name { get; set; } = null!;
    public string Description { get; set; } = string.Empty;

    // store as a real number in Mongo (the driver defaults decimal to string)
    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Price { get; set; }

    public int Stock { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<string> Platforms { get; set; } = new();
    public List<string> Tags { get; set; } = new();

    // fields that differ from game to game (developer, ageRating, maxPlayers, minSpecs, ...)
    public Dictionary<string, object> Attributes { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
