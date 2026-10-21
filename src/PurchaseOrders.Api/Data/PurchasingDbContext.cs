using Microsoft.EntityFrameworkCore;
using PurchaseOrders.Api.Domain;

namespace PurchaseOrders.Api.Data;

public sealed class PurchasingDbContext(DbContextOptions<PurchasingDbContext> options) : DbContext(options)
{
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Supplier>(supplier =>
        {
            supplier.ToTable("suppliers");
            supplier.HasKey(s => s.Code);
            supplier.Property(s => s.Code).HasMaxLength(16);
            supplier.Property(s => s.Name).HasMaxLength(200).IsRequired();
            supplier.Property(s => s.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            supplier.HasData(SeedData.Suppliers);
        });

        modelBuilder.Entity<PurchaseOrder>(order =>
        {
            order.ToTable("purchase_orders");
            order.HasKey(o => o.Id);
            order.Property(o => o.OrderNumber).HasMaxLength(32).IsRequired();
            order.HasIndex(o => o.OrderNumber).IsUnique();
            order.Property(o => o.SupplierCode).HasMaxLength(16).IsRequired();
            order.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
            order.Property(o => o.Notes).HasMaxLength(PurchaseOrderValidator.MaxNotesLength);
            order.Ignore(o => o.Total);
            order.HasOne(o => o.Supplier).WithMany().HasForeignKey(o => o.SupplierCode).OnDelete(DeleteBehavior.Restrict);
            order.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
            order.Navigation(o => o.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<PurchaseOrderLine>(line =>
        {
            line.ToTable("purchase_order_lines");
            line.HasKey(l => l.Id);
            line.Property(l => l.Sku).HasMaxLength(32).IsRequired();
            line.Property(l => l.Description).HasMaxLength(200).IsRequired();
            line.Property(l => l.UnitPrice).HasPrecision(18, 2);
            line.Ignore(l => l.LineTotal);
            line.HasIndex(l => new { l.PurchaseOrderId, l.LineNumber }).IsUnique();
        });
    }
}

public static class SeedData
{
    public static readonly Supplier[] Suppliers =
    [
        new() { Code = "ACME", Name = "Acme Industrial Supply", Currency = "USD" },
        new() { Code = "NWIND", Name = "Northwind Traders", Currency = "USD" },
        new() { Code = "CONTOSO", Name = "Contoso Components", Currency = "EUR" },
        new() { Code = "FABRIKAM", Name = "Fabrikam Fasteners", Currency = "USD", IsActive = false },
    ];
}
