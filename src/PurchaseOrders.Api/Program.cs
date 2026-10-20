using Microsoft.EntityFrameworkCore;
using PurchaseOrders.Api.Data;
using PurchaseOrders.Api.Endpoints;
using PurchaseOrders.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<PurchasingDbContext>("purchasingdb");

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<SupplierSimulationOptions>(builder.Configuration.GetSection(SupplierSimulationOptions.SectionName));
builder.Services.AddSingleton<ISupplierOrderSimulator, SupplierOrderSimulator>();
builder.Services.AddScoped<PurchaseOrderService>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapGet("/openapi.json", () => Results.Ok(new { message = "OpenAPI disabled in this sample." }));
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapPurchaseOrderEndpoints();
app.MapDefaultEndpoints();

app.Run();


