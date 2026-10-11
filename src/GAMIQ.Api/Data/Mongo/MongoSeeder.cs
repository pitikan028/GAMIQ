using System.Dynamic;
using Bogus;
using GAMIQ.Api.Models.Mongo;
using MongoDB.Driver;

namespace GAMIQ.Api.Data.Mongo;

// Seeds the "products" and "reviews" collections. Fixed seed (42) so everyone gets the same data.
public static class MongoSeeder
{
    private const int ProductCount = 1200;
    private const int ReviewCount = 2000;

    private static readonly string[] Genres =
        { "Action", "RPG", "Strategy", "Simulation", "Sports", "Adventure", "Indie", "Puzzle", "Horror", "Racing" };

    private static readonly string[] Platforms =
        { "Windows", "MacOS", "Linux", "PlayStation5", "XboxSeriesX", "NintendoSwitch" };

    private static readonly string[] Tags =
        { "Multiplayer", "Singleplayer", "Co-op", "Open World", "Early Access", "VR Supported", "Controller Support" };

    private static readonly string[] TitleStart =
    {
        "Shadow", "Crimson", "Iron", "Silent", "Neon", "Hollow", "Eternal", "Frozen", "Broken", "Golden",
        "Last", "Wild", "Starfall", "Rogue", "Ashen", "Lunar", "Savage", "Mystic", "Steel", "Phantom"
    };

    private static readonly string[] TitleEnd =
    {
        "Frontier", "Legacy", "Protocol", "Kingdom", "Horizon", "Dominion", "Requiem", "Odyssey", "Outpost", "Harbor",
        "Circuit", "Covenant", "Wasteland", "Citadel", "Drift", "Echoes", "Rebellion", "Tactics", "Colony", "Arena"
    };

    private static readonly string[] Subtitles =
        { "Remastered", "Rebirth", "Origins", "Definitive Edition", "Part II", "Reckoning", "Nightfall", "Awakening", "Chronicles" };

    private static readonly string[] Hooks =
    {
        "Explore a huge open world full of secrets",
        "Build and manage your own colony on a hostile planet",
        "Fight through procedurally generated dungeons",
        "Team up with friends in online co-op missions",
        "Race across neon-lit city streets at night",
        "Command your army in turn-based battles",
        "Survive the night in an abandoned research station",
        "Solve hand-crafted physics puzzles",
        "Lead your club from the lower league to the championship",
        "Uncover the history of a forgotten kingdom"
    };

    private static readonly string[] Extras =
    {
        "Around 40 hours of story content.",
        "Comes with a level editor and mod support.",
        "Cross-platform multiplayer is supported.",
        "Fully voiced in 8 languages, including Thai subtitles.",
        "New maps and modes are added every season.",
        "Works well for short sessions and long campaigns."
    };

    private static readonly string[] GoodComments =
    {
        "Loved it, one of the best games I played this year.",
        "Great gameplay loop, I lost track of time.",
        "Runs smoothly and the soundtrack is amazing.",
        "Worth the price, co-op with friends is a blast.",
        "Story hooked me from the first hour."
    };

    private static readonly string[] MixedComments =
    {
        "Fun at first but gets repetitive after a few hours.",
        "Good ideas, performance still needs work on my PC.",
        "Decent game, wait for a sale though.",
        "Combat is great, the menus are a mess."
    };

    private static readonly string[] BadComments =
    {
        "Crashes a lot after the latest patch.",
        "Too many bugs to recommend right now.",
        "Not worth the price, the campaign is very short.",
        "Servers are down half the time."
    };

    private static readonly decimal[] Prices = { 99, 149, 199, 259, 329, 399, 499, 590, 799, 999, 1290, 1590, 1890 };

    public static async Task<List<Product>> SeedAsync(MongoContext mongo)
    {
        var f = new Faker { Random = new Randomizer(42) };

        if (await mongo.Products.CountDocumentsAsync(FilterDefinition<Product>.Empty) == 0)
        {
            await mongo.Products.InsertManyAsync(BuildProducts(f));
        }

        var products = await mongo.Products.Find(FilterDefinition<Product>.Empty).ToListAsync();

        if (products.Count > 0 && await mongo.Reviews.CountDocumentsAsync(FilterDefinition<Review>.Empty) == 0)
        {
            await mongo.Reviews.InsertManyAsync(BuildReviews(f, products));
        }

        return products;
    }

    private static List<Product> BuildProducts(Faker f)
    {
        var usedNames = new HashSet<string>();
        var products = new List<Product>();

        while (products.Count < ProductCount)
        {
            var name = $"{f.PickRandom(TitleStart)} {f.PickRandom(TitleEnd)}";
            if (f.Random.Bool(0.6f))
            {
                name += $": {f.PickRandom(Subtitles)}";
            }

            if (!usedNames.Add(name))
            {
                continue;
            }

            var genres = f.PickRandom(Genres, f.Random.Number(1, 3)).ToList();
            var platforms = f.PickRandom(Platforms, f.Random.Number(1, 4)).ToList();
            var tags = f.PickRandom(Tags, f.Random.Number(1, 3)).ToList();

            products.Add(new Product
            {
                Name = name,
                Description = $"{f.PickRandom(Hooks)}. {f.PickRandom(Extras)}",
                Price = f.PickRandom(Prices),
                Stock = f.Random.Number(0, 500),
                Genres = genres,
                Platforms = platforms,
                Tags = tags,
                Attributes = BuildAttributes(f, genres, platforms, tags),
                CreatedAt = f.Date.Past(3).ToUniversalTime()
            });
        }

        return products;
    }

    // Every game gets the basic fields, the rest depends on genre/platform/tags.
    // This is the part that doesn't fit nicely into a fixed SQL table.
    private static Dictionary<string, object> BuildAttributes(Faker f, List<string> genres, List<string> platforms, List<string> tags)
    {
        var attrs = new Dictionary<string, object>
        {
            ["developer"] = f.Company.CompanyName(),
            ["publisher"] = f.Company.CompanyName(),
            ["releaseYear"] = f.Random.Number(2015, 2026),
            ["ageRating"] = f.PickRandom("E", "E10+", "T", "M"),
            ["languages"] = f.PickRandom(new[] { "English", "Thai", "Japanese", "Korean", "German", "French" }, f.Random.Number(1, 4)).ToList()
        };

        if (f.Random.Bool(0.3f))
        {
            attrs["discountPercent"] = f.PickRandom(10, 20, 25, 33, 50, 75);
        }

        if (tags.Contains("Multiplayer") || tags.Contains("Co-op"))
        {
            attrs["maxPlayers"] = f.PickRandom(2, 4, 8, 16, 64);
        }

        if (tags.Contains("VR Supported"))
        {
            attrs["vrHeadsets"] = f.PickRandom(new[] { "Meta Quest 3", "Valve Index", "PS VR2", "HTC Vive" }, f.Random.Number(1, 3)).ToList();
        }

        if (genres.Contains("RPG") || genres.Contains("Adventure"))
        {
            attrs["estimatedHours"] = f.Random.Number(10, 120);
        }

        if (genres.Contains("Sports"))
        {
            attrs["licensedLeague"] = f.Random.Bool();
        }

        if (platforms.Contains("Windows"))
        {
            dynamic specs = new ExpandoObject();
            specs.os = "Windows 10 64-bit";
            specs.ramGb = f.PickRandom(4, 8, 16);
            specs.storageGb = f.Random.Number(5, 150);
            specs.gpu = f.PickRandom("GTX 1050", "GTX 1660", "RTX 2060", "RTX 3060", "RX 580");
            attrs["minSpecs"] = specs;
        }

        return attrs;
    }

    private static List<Review> BuildReviews(Faker f, List<Product> products)
    {
        var reviews = new List<Review>();

        for (var i = 0; i < ReviewCount; i++)
        {
            var product = f.PickRandom(products);
            var rating = f.Random.WeightedRandom(new[] { 1, 2, 3, 4, 5 }, new[] { 0.08f, 0.1f, 0.2f, 0.32f, 0.3f });
            var comment = rating >= 4 ? f.PickRandom(GoodComments)
                : rating == 3 ? f.PickRandom(MixedComments)
                : f.PickRandom(BadComments);

            var platform = f.PickRandom(product.Platforms);
            var metadata = new Dictionary<string, object>
            {
                ["platform"] = platform,
                ["patchVersion"] = $"{f.Random.Number(1, 3)}.{f.Random.Number(0, 12)}.{f.Random.Number(0, 9)}",
                ["verifiedPurchase"] = f.Random.Bool(0.8f)
            };

            if (platform is "Windows" or "Linux" or "MacOS")
            {
                metadata["avgFps"] = f.Random.Number(30, 144);
            }

            if (f.Random.Bool(0.4f))
            {
                metadata["helpfulVotes"] = f.Random.Number(0, 250);
            }

            reviews.Add(new Review
            {
                ProductId = product.Id,
                UserId = f.Random.Number(1, 1000), // seeded users in PostgreSQL are 1..1000
                Rating = rating,
                Comment = comment,
                PlaytimeHours = Math.Round(f.Random.Double(0.5, 300), 1),
                Metadata = metadata,
                CreatedAt = f.Date.Past(2).ToUniversalTime()
            });
        }

        return reviews;
    }
}
