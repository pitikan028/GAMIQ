using GAMIQ.Api.Config;
using GAMIQ.Api.Data;
using GAMIQ.Api.Data.Mongo;
using GAMIQ.Api.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

// connection strings come from .env (see .env.example), not appsettings.json
DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var postgresConnection = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnection))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres is not set. Copy .env.example to .env in the repo root first.");
}

// CP2: the current expand-contract phase, and one EF model per phase (see PhaseModelCacheKeyFactory)
builder.Services.AddSingleton<MigrationState>();
builder.Services.AddSingleton<BackfillJob>();
builder.Services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(postgresConnection)
    .ReplaceService<IModelCacheKeyFactory, PhaseModelCacheKeyFactory>());

builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("MongoDb"));
builder.Services.AddSingleton<MongoContext>();

var app = builder.Build();

// migrate + seed both databases (skipped if data already exists)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var mongo = scope.ServiceProvider.GetRequiredService<MongoContext>();

    await mongo.EnsureIndexesAsync();
    var products = await MongoSeeder.SeedAsync(mongo);
    await DbInitializer.SeedAsync(db, products);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Run();
