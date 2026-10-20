using PurchaseOrders.Api.Services;
using PurchaseOrders.Contracts;

namespace PurchaseOrders.Api.Endpoints;

public static class PurchaseOrderEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/suppliers", (PurchaseOrderService service, CancellationToken ct) => service.GetActiveSuppliersAsync(ct))
            .WithName("ListSuppliers");

        var orders = api.MapGroup("/purchase-orders").WithTags("PurchaseOrders");

        orders.MapGet("/", (PurchaseOrderService service, string? supplierCode, int? take, CancellationToken ct) =>
                service.ListAsync(supplierCode, take ?? 50, ct))
            .WithName("ListPurchaseOrders");

        orders.MapGet("/{id:guid}", async (Guid id, PurchaseOrderService service, CancellationToken ct) =>
                await service.GetAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound())
            .WithName("GetPurchaseOrder")
            .Produces<PurchaseOrderDto>()
            .Produces(StatusCodes.Status404NotFound);

        orders.MapPost("/", async (CreatePurchaseOrderRequest request, PurchaseOrderService service, CancellationToken ct) =>
                await service.CreateDraftAsync(request, ct) switch
                {
                    CreateDraftResult.Created created => Results.Created($"/api/purchase-orders/{created.Order.Id}", created.Order),
                    CreateDraftResult.Invalid invalid => Results.ValidationProblem(invalid.Errors),
                    _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
                })
            .WithName("CreateDraftPurchaseOrder")
            .Produces<PurchaseOrderDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        return app;
    }
}
