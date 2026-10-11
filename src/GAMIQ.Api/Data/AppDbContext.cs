using GAMIQ.Api.Config;
using GAMIQ.Api.Models.Postgres;
using Microsoft.EntityFrameworkCore;

namespace GAMIQ.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options, MigrationState migration) : base(options)
    {
        // read once, so one request uses the same phase for the model and for the controller logic
        Phase = migration.Phase;
    }

    public MigrationPhase Phase { get; }

    public DbSet<User> Users => Set<User>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.UserId);
            entity.Property(u => u.Username).HasMaxLength(50);
            entity.Property(u => u.Email).HasMaxLength(255);
            entity.HasIndex(u => u.Username).IsUnique();
            entity.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(a => a.AccountId);
            entity.HasIndex(a => a.UserId).IsUnique();
            entity.Property(a => a.Balance).HasColumnType("decimal(18,2)");
            entity.Property(a => a.Currency).HasMaxLength(3);
            entity.Property(a => a.Version).IsRowVersion();
            entity.ToTable(t => t.HasCheckConstraint("CK_Accounts_Balance", "\"Balance\" >= 0"));
            entity.HasOne(a => a.User)
                  .WithOne(u => u.Account)
                  .HasForeignKey<Account>(a => a.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(o => o.OrderId);
            entity.HasIndex(o => new { o.UserId, o.CreatedAt });
            entity.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);

            // CP2: which price columns EF may touch depends on the migration phase.
            // An ignored property is left out of every SELECT / INSERT, so the API never asks
            // for a column that doesn't exist yet (Legacy) or was already dropped (Contract).
            if (Phase == MigrationPhase.Contract)
            {
                entity.Ignore(o => o.TotalAmount);
            }
            else
            {
                // Required only while the API still answers with it. From ReadNew on, a row
                // can have NULL here (order made in Contract, then rolled back to ReadNew) and
                // EF must be able to load it.
                entity.Property(o => o.TotalAmount)
                      .HasColumnType("decimal(18,2)")
                      .IsRequired(Phase < MigrationPhase.ReadNew);
                entity.ToTable(t => t.HasCheckConstraint("CK_Orders_TotalAmount", "\"TotalAmount\" >= 0"));
            }

            if (Phase == MigrationPhase.Legacy)
            {
                entity.Ignore(o => o.PriceAmount);
                entity.Ignore(o => o.CurrencyCode);
            }
            else
            {
                entity.Property(o => o.PriceAmount).HasColumnType("decimal(18,2)");
                entity.Property(o => o.CurrencyCode).HasMaxLength(3);
            }

            entity.HasOne(o => o.User)
                  .WithMany(u => u.Orders)
                  .HasForeignKey(o => o.UserId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(oi => oi.OrderItemId);
            entity.HasIndex(oi => oi.OrderId);
            entity.HasIndex(oi => oi.ProductId);
            entity.Property(oi => oi.UnitPrice).HasColumnType("decimal(18,2)");
            entity.Property(oi => oi.ProductId).HasMaxLength(24);
            entity.Property(oi => oi.ProductName).HasMaxLength(200);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_OrderItems_Quantity", "\"Quantity\" > 0");
                t.HasCheckConstraint("CK_OrderItems_UnitPrice", "\"UnitPrice\" >= 0");
            });
            entity.HasOne(oi => oi.Order)
                  .WithMany(o => o.Items)
                  .HasForeignKey(oi => oi.OrderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
